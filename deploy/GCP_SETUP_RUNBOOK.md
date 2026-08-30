# Neytrix — GCP Setup Runbook

Everything below runs from your own terminal, authenticated as yourself
(`gcloud auth login`) with Owner or the equivalent granular roles on
`neytrix-prod`. I don't have GCP access, so these are the exact commands to
run, in order — paste the output back to me at any point and I'll tell you
what's next or help debug.

Target config (already baked into the scripts below):

| | |
|---|---|
| Project | `neytrix-prod` |
| Region | `us-west1` |
| Service | `neytrix-api` |
| Runtime SA | `neytrix-run@neytrix-prod.iam.gserviceaccount.com` |
| Build SA | `neytrix-build@neytrix-prod.iam.gserviceaccount.com` |

The good news: `deploy/bootstrap-gcp.sh` and `deploy/deploy.sh` already exist
in the repo (written in an earlier session) and are idempotent — safe to
re-run if a step fails partway through. This runbook just walks you through
using them in the right order.

---

## 0. Pull the latest code

```bash
git fetch origin
git checkout chore/cloud-run-deployment
git pull origin chore/cloud-run-deployment
```

This picks up today's Stripe webhook tenant-resolution fix, which was just
pushed.

## 1. Point gcloud at the project and confirm billing

```bash
gcloud auth login                 # if you haven't already
gcloud config set project neytrix-prod
gcloud beta billing projects describe neytrix-prod --format='value(billingEnabled)'
```

Should print `True`. If not, enable billing in the console before continuing
— Cloud Build and Cloud Run both fail without it.

## 2. Run the bootstrap script

```bash
./deploy/bootstrap-gcp.sh neytrix-prod us-west1
```

This one script does everything in the "final APIs / IAM / secrets" part of
the checklist:

- **Enables the 2 remaining APIs**: `aiplatform.googleapis.com` (Vertex AI)
  and `secretmanager.googleapis.com` (Secret Manager), plus re-affirms the
  ones already on (`run`, `cloudbuild`, `artifactregistry`, `iamcredentials`,
  `logging`).
- **Creates the Artifact Registry repo** `neytrix` in `us-west1` (no-op if it
  already exists).
- **Creates both service accounts** — `neytrix-run` (runtime) and
  `neytrix-build` (deploy) — if they don't exist yet.
- **Grants all 6 required IAM bindings**:
  - `neytrix-build` → `roles/run.admin` (deploy revisions)
  - `neytrix-build` → `roles/artifactregistry.writer` (push images)
  - `neytrix-build` → `roles/logging.logWriter` (write build logs)
  - `neytrix-build` → `roles/iam.serviceAccountUser` **on** `neytrix-run`
    (actAs the runtime SA at deploy time)
  - `neytrix-run` → `roles/secretmanager.secretAccessor` (resolve secrets at
    startup)
  - `neytrix-run` → `roles/aiplatform.user` (call Vertex AI)
- **Creates 5 empty secret shells** in Secret Manager: `neytrix-db-connection`,
  `neytrix-stripe-secret-key`, `neytrix-stripe-webhook-secret`,
  `neytrix-gcal-client-id`, `neytrix-gcal-client-secret`. They're created
  empty on purpose — the script never touches secret material.

Every step is idempotent (checks `describe` before `create`), so if it dies
partway through — e.g. a permissions error — fix that and just re-run the
whole thing.

## 3. Add real values to the 5 secrets

The bootstrap script created empty shells. Add one version to each — none of
these values should ever be pasted into chat with me, only run locally:

```bash
# Supabase connection string, authenticating as neytrix_app (never postgres —
# that role bypasses RLS). Get this from Supabase → Project Settings →
# Database → Connection string (session pooler, port 6543).
printf '%s' 'Host=...;Port=6543;Database=postgres;Username=neytrix_app.PROJECT_REF;Password=...;SSL Mode=Require;Trust Server Certificate=true;Pooling=true;MinPoolSize=0;MaxPoolSize=10' \
  | gcloud secrets versions add neytrix-db-connection --data-file=-

# Stripe test secret key (sk_test_...) for the pilot.
printf '%s' 'sk_test_...' \
  | gcloud secrets versions add neytrix-stripe-secret-key --data-file=-

# Stripe webhook signing secret (whsec_...) — you'll get/update the real
# value in step 6, once the webhook endpoint exists to register in Stripe.
# A placeholder is fine for the first deploy.
printf '%s' 'whsec_placeholder' \
  | gcloud secrets versions add neytrix-stripe-webhook-secret --data-file=-

# Google Calendar OAuth client (from Google Cloud Console → APIs & Services
# → Credentials).
printf '%s' '...apps.googleusercontent.com' \
  | gcloud secrets versions add neytrix-gcal-client-id --data-file=-
printf '%s' '...' \
  | gcloud secrets versions add neytrix-gcal-client-secret --data-file=-
```

Verify none are empty:

```bash
for s in neytrix-db-connection neytrix-stripe-secret-key neytrix-stripe-webhook-secret neytrix-gcal-client-id neytrix-gcal-client-secret; do
  echo -n "$s: "; gcloud secrets versions list "$s" --format='value(name)' | wc -l
done
```

## 4. Make sure the production database has the schema

If `neytrix-db-connection` points at a Supabase database that hasn't had the
migration applied yet:

```bash
psql "$CONN" -f db/migrations/001_initial_schema.sql
psql "$CONN" -c "select tablename, rowsecurity from pg_tables where schemaname='public';"
```

Every tenant-scoped table should show `rowsecurity = t`. If this Supabase
instance already has the schema from earlier work, skip this.

## 5. First deploy

```bash
./deploy/deploy.sh neytrix-prod us-west1
```

This runs a preflight (APIs enabled, Dockerfile present, runtime SA exists,
every secret has a version) and then `gcloud run deploy --source .` — Cloud
Build packages the local source, builds the container, and deploys it as
`neytrix-api` in `us-west1`, running as `neytrix-run`. It then polls
`/healthz` and `/readyz` and reports the live URL.

## 6. Fix the two chicken-and-egg env vars, then redeploy

`GoogleCalendar__RedirectUri` and `ALLOWED_ORIGINS` need the real Cloud Run
URL, which only exists after step 5:

```bash
REDIRECT_URI="https://<the-url-from-step-5>/oauth/callback" \
ALLOWED_ORIGINS="https://your-widget-host" \
./deploy/deploy.sh neytrix-prod us-west1
```

Also now register the webhook in the Stripe dashboard pointed at
`https://<the-url>/api/v1/chat/webhooks/stripe`, copy the real signing
secret it gives you, and replace the placeholder from step 3:

```bash
printf '%s' 'whsec_<the real one>' \
  | gcloud secrets versions add neytrix-stripe-webhook-secret --data-file=-
```

A new secret version takes effect on the next deploy/revision — redeploy
once more, or it'll pick up automatically next time Cloud Build runs.

## 7. Wire up continuous deploy (Cloud Build trigger)

This part needs the GitHub connection made through the console (Cloud
Build's GitHub App install is OAuth-based and isn't a plain `gcloud` one-
liner):

1. Console → Cloud Build → **Repositories** → **Connect repository** →
   GitHub → authorize the Cloud Build GitHub App → select
   `demissiejoseph-netizen/neytrix-ai-enrollment-agent`.
2. Then create the trigger itself, which *is* scriptable once the repo
   connection exists:

   ```bash
   gcloud builds triggers create github \
     --name=neytrix-main-deploy \
     --repo-name=neytrix-ai-enrollment-agent \
     --repo-owner=demissiejoseph-netizen \
     --branch-pattern='^main$' \
     --build-config=cloudbuild.yaml \
     --service-account=projects/neytrix-prod/serviceAccounts/neytrix-build@neytrix-prod.iam.gserviceaccount.com
   ```

3. Run it once manually to confirm it goes green before relying on it:

   ```bash
   gcloud builds triggers run neytrix-main-deploy --branch=main
   ```

Note this deploys from `main`, not `chore/cloud-run-deployment` — merge the
branch to `main` first (with your own review/PR — I won't open or merge a PR
without you asking), or point `--branch-pattern` at the working branch
temporarily.

## 8. Smoke test

```bash
URL=$(gcloud run services describe neytrix-api --region=us-west1 --format='value(status.url)')
curl -sS -X POST "$URL/api/v1/chat/sessions" \
  -H 'Content-Type: application/json' \
  -H 'X-Tenant-Slug: <a real tenant slug>' \
  -d '{"channel":"widget"}'
```

Should return a session with a token. That confirms RLS, the Postgres
connection, and the app boot path all work in the real environment — the
last checklist item on the deploy dashboard.

---

Paste command output back to me at any step (bootstrap, deploy, or smoke
test) and I'll read the state, flip the matching items on the [deploy
dashboard](https://www.perplexity.ai/computer/a/neytrix-deploy-dashboard-UZTS8lbGQsSW.M0x29N2MQ),
and tell you exactly what's next.
