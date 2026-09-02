#!/usr/bin/env bash
# =============================================================================
# Manual source-based deploy to Cloud Run. Use this for the FIRST deploy, before
# the Cloud Build trigger exists. After the trigger is wired, prefer pushing to
# main and letting cloudbuild.yaml do it.
#
#   ./deploy/deploy.sh neytrix-502106 us-west1
#
# Runs a preflight check first and refuses to deploy if a prerequisite is
# missing, so you get a clear error instead of a half-created revision.
# =============================================================================
set -euo pipefail

PROJECT_ID="${1:-${PROJECT_ID:-}}"
REGION="${2:-${REGION:-us-west1}}"
SERVICE="${SERVICE:-neytrix-api}"
RUNTIME_SA="${RUNTIME_SA:-neytrix-run@${PROJECT_ID}.iam.gserviceaccount.com}"

if [[ -z "$PROJECT_ID" ]]; then
  echo "usage: $0 <project-id> [region]" >&2
  exit 64
fi

log()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '    \033[0;32m✓\033[0m %s\n' "$*"; }
fail() { printf '    \033[0;31m✗\033[0m %s\n' "$*"; FAILED=1; }
FAILED=0

gcloud config set project "$PROJECT_ID" >/dev/null

# -----------------------------------------------------------------------------
log "Preflight"
# -----------------------------------------------------------------------------
for api in run.googleapis.com cloudbuild.googleapis.com \
           artifactregistry.googleapis.com aiplatform.googleapis.com \
           secretmanager.googleapis.com; do
  if gcloud services list --enabled --format='value(config.name)' \
       | grep -qx "$api"; then
    ok "$api enabled"
  else
    fail "$api NOT enabled — run ./deploy/bootstrap-gcp.sh first"
  fi
done

if [[ -f Dockerfile ]]; then
  ok "Dockerfile present"
else
  fail "Dockerfile missing at repo root"
fi

if gcloud iam service-accounts describe "$RUNTIME_SA" >/dev/null 2>&1; then
  ok "runtime service account exists"
else
  fail "$RUNTIME_SA does not exist — run ./deploy/bootstrap-gcp.sh first"
fi

# Every secret must have at least one version, or the revision will fail to
# start with a confusing mount error rather than a clear message.
for s in neytrix-db-connection neytrix-stripe-secret-key \
         neytrix-stripe-webhook-secret neytrix-gcal-client-id \
         neytrix-gcal-client-secret; do
  COUNT=$(gcloud secrets versions list "$s" \
            --filter='state=enabled' --format='value(name)' 2>/dev/null | wc -l | tr -d ' ')
  if [[ "$COUNT" -gt 0 ]]; then
    ok "$s has $COUNT enabled version(s)"
  else
    fail "$s has no enabled versions — add one before deploying"
  fi
done

if [[ "$FAILED" -ne 0 ]]; then
  echo
  echo "Preflight failed. Nothing was deployed." >&2
  exit 1
fi

# -----------------------------------------------------------------------------
log "Deploying $SERVICE from source"
# -----------------------------------------------------------------------------
# ALLOWED_ORIGINS contains commas, so --set-env-vars uses gcloud's alternate
# delimiter syntax: a leading ^@^ redefines the separator to @.
gcloud run deploy "$SERVICE" \
  --source . \
  --project "$PROJECT_ID" \
  --region "$REGION" \
  --platform managed \
  --service-account "$RUNTIME_SA" \
  --allow-unauthenticated \
  --port 8080 \
  --cpu 1 \
  --memory 1Gi \
  --min-instances 0 \
  --max-instances 4 \
  --timeout 300 \
  --concurrency 80 \
  --set-env-vars "^@^ASPNETCORE_ENVIRONMENT=Production@VertexAI__ProjectId=${PROJECT_ID}@VertexAI__Location=${REGION}@VertexAI__Model=gemini-1.5-pro@GoogleCalendar__RedirectUri=${REDIRECT_URI:-https://REPLACE_WITH_SERVICE_URL/oauth/callback}@ALLOWED_ORIGINS=${ALLOWED_ORIGINS:-https://REPLACE_WITH_WIDGET_HOST}" \
  --set-secrets "ConnectionStrings__DefaultConnection=neytrix-db-connection:latest,Stripe__SecretKey=neytrix-stripe-secret-key:latest,Stripe__WebhookSecret=neytrix-stripe-webhook-secret:latest,GoogleCalendar__ClientId=neytrix-gcal-client-id:latest,GoogleCalendar__ClientSecret=neytrix-gcal-client-secret:latest"

# -----------------------------------------------------------------------------
log "Verifying"
# -----------------------------------------------------------------------------
URL=$(gcloud run services describe "$SERVICE" --region "$REGION" \
        --format 'value(status.url)')
echo "    Service URL: $URL"

for i in $(seq 1 20); do
  CODE=$(curl -s -o /dev/null -w '%{http_code}' "$URL/healthz" || echo 000)
  if [[ "$CODE" == "200" ]]; then
    ok "/healthz returned 200"
    break
  fi
  sleep 3
done

if [[ "${CODE:-000}" != "200" ]]; then
  echo
  echo "Service deployed but /healthz never returned 200. Recent logs:" >&2
  gcloud run services logs read "$SERVICE" --region "$REGION" --limit 50 >&2
  exit 1
fi

CODE=$(curl -s -o /dev/null -w '%{http_code}' "$URL/readyz" || echo 000)
if [[ "$CODE" == "200" ]]; then
  ok "/readyz returned 200 — database reachable"
else
  echo "    ! /readyz returned $CODE. The app is up but cannot reach Postgres."
  echo "      Check the neytrix-db-connection secret and Supabase network rules."
fi

cat <<EOF

Next:
  1. If GoogleCalendar__RedirectUri or ALLOWED_ORIGINS still say REPLACE_*,
     rerun with the real values now that you know the host:
       REDIRECT_URI="$URL/oauth/callback" \\
       ALLOWED_ORIGINS="https://your-widget-host" \\
       ./deploy/deploy.sh $PROJECT_ID $REGION
  2. Point the Stripe webhook endpoint at:
       $URL/api/v1/chat/webhooks/stripe
  3. Smoke test with a real tenant slug:
       curl -sS -X POST "$URL/api/v1/chat/sessions" \\
         -H 'Content-Type: application/json' \\
         -H 'X-Tenant-Slug: your-tenant-slug' \\
         -d '{"channel":"widget"}'

EOF
