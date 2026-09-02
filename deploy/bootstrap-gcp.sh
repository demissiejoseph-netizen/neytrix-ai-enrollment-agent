#!/usr/bin/env bash
# =============================================================================
# One-time GCP bootstrap for the Neytrix AI Enrollment Agent.
#
# Idempotent: every step tolerates already-existing resources, so it is safe to
# re-run after a partial failure.
#
#   ./deploy/bootstrap-gcp.sh neytrix-502106 us-west1
#
# Prerequisites: gcloud authenticated as a principal with Project Owner (or
# Service Usage Admin + IAM Admin + Artifact Registry Admin + Secret Manager
# Admin), and billing enabled on the project.
# =============================================================================
set -euo pipefail

PROJECT_ID="${1:-${PROJECT_ID:-}}"
REGION="${2:-${REGION:-us-west1}}"
SERVICE="${SERVICE:-neytrix-api}"
REPOSITORY="${REPOSITORY:-neytrix}"
RUNTIME_SA_NAME="${RUNTIME_SA_NAME:-neytrix-run}"
BUILD_SA_NAME="${BUILD_SA_NAME:-neytrix-build}"

if [[ -z "$PROJECT_ID" ]]; then
  echo "usage: $0 <project-id> [region]" >&2
  exit 64
fi

RUNTIME_SA="${RUNTIME_SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"
BUILD_SA="${BUILD_SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

log()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '    \033[0;32m✓\033[0m %s\n' "$*"; }
warn() { printf '    \033[0;33m!\033[0m %s\n' "$*"; }

log "Targeting project $PROJECT_ID in $REGION"
gcloud config set project "$PROJECT_ID" >/dev/null
if ! gcloud beta billing projects describe "$PROJECT_ID" \
     --format='value(billingEnabled)' 2>/dev/null | grep -qi true; then
  warn "Could not confirm billing is enabled. Cloud Build and Cloud Run will"
  warn "fail without it. Verify in the console before continuing."
fi

# -----------------------------------------------------------------------------
log "Enabling required APIs"
# -----------------------------------------------------------------------------
# Enabling an already-enabled service is a no-op, so just enable them all.
gcloud services enable \
  run.googleapis.com \
  cloudbuild.googleapis.com \
  artifactregistry.googleapis.com \
  aiplatform.googleapis.com \
  secretmanager.googleapis.com \
  iamcredentials.googleapis.com \
  logging.googleapis.com
ok "APIs enabled"

# -----------------------------------------------------------------------------
log "Creating Artifact Registry repository '$REPOSITORY'"
# -----------------------------------------------------------------------------
if gcloud artifacts repositories describe "$REPOSITORY" \
     --location "$REGION" >/dev/null 2>&1; then
  ok "Repository already exists"
else
  gcloud artifacts repositories create "$REPOSITORY" \
    --repository-format=docker \
    --location="$REGION" \
    --description="Neytrix API container images"
  ok "Repository created"
fi

# -----------------------------------------------------------------------------
log "Creating service accounts"
# -----------------------------------------------------------------------------
create_sa() {
  local name="$1" display="$2"
  if gcloud iam service-accounts describe \
       "${name}@${PROJECT_ID}.iam.gserviceaccount.com" >/dev/null 2>&1; then
    ok "$name already exists"
  else
    gcloud iam service-accounts create "$name" --display-name="$display"
    ok "$name created"
  fi
}
create_sa "$RUNTIME_SA_NAME" "Neytrix Cloud Run runtime"
create_sa "$BUILD_SA_NAME"   "Neytrix Cloud Build deployer"

# -----------------------------------------------------------------------------
log "Granting IAM roles"
# -----------------------------------------------------------------------------
grant_project() {
  gcloud projects add-iam-policy-binding "$PROJECT_ID" \
    --member="serviceAccount:$1" --role="$2" \
    --condition=None --quiet >/dev/null
  ok "$2 → ${1%%@*}"
}

# Build SA: needs to push images, deploy revisions, act as the runtime SA, and
# write its own logs (mandatory once a build uses a dedicated service account).
grant_project "$BUILD_SA" roles/run.admin
grant_project "$BUILD_SA" roles/artifactregistry.writer
grant_project "$BUILD_SA" roles/logging.logWriter
gcloud iam service-accounts add-iam-policy-binding "$RUNTIME_SA" \
  --member="serviceAccount:$BUILD_SA" \
  --role=roles/iam.serviceAccountUser --quiet >/dev/null
ok "roles/iam.serviceAccountUser → ${BUILD_SA%%@*} on ${RUNTIME_SA%%@*}"

# Runtime SA: reads its own secrets and calls Vertex AI. Nothing more.
grant_project "$RUNTIME_SA" roles/secretmanager.secretAccessor
grant_project "$RUNTIME_SA" roles/aiplatform.user
grant_project "$RUNTIME_SA" roles/logging.logWriter

# -----------------------------------------------------------------------------
log "Creating secret shells"
# -----------------------------------------------------------------------------
# Created empty on purpose — this script must never contain secret material.
# Add versions with:
#   printf '%s' "$VALUE" | gcloud secrets versions add <name> --data-file=-
SECRETS=(
  neytrix-db-connection
  neytrix-stripe-secret-key
  neytrix-stripe-webhook-secret
  neytrix-gcal-client-id
  neytrix-gcal-client-secret
)
for s in "${SECRETS[@]}"; do
  if gcloud secrets describe "$s" >/dev/null 2>&1; then
    ok "$s already exists"
  else
    gcloud secrets create "$s" --replication-policy=automatic
    ok "$s created (no versions yet)"
  fi
done

# -----------------------------------------------------------------------------
log "Bootstrap complete"
# -----------------------------------------------------------------------------
cat <<EOF

Remaining manual steps, in order:

  1. Add a version to every secret (they are empty shells right now):
       printf '%s' 'Host=...' | gcloud secrets versions add neytrix-db-connection --data-file=-
       printf '%s' 'sk_live_...' | gcloud secrets versions add neytrix-stripe-secret-key --data-file=-
       printf '%s' 'whsec_...'   | gcloud secrets versions add neytrix-stripe-webhook-secret --data-file=-
       printf '%s' '...apps.googleusercontent.com' | gcloud secrets versions add neytrix-gcal-client-id --data-file=-
       printf '%s' '...' | gcloud secrets versions add neytrix-gcal-client-secret --data-file=-

     Verify none are empty:
       for s in ${SECRETS[*]}; do
         echo -n "\$s: "; gcloud secrets versions list "\$s" --format='value(name)' | wc -l
       done

  2. Apply the database schema as a role that owns the tables, then confirm the
     RLS policies exist and that neytrix_app is NOT a superuser:
       psql "\$CONN" -f db/migrations/001_initial_schema.sql
       psql "\$CONN" -c "select tablename, rowsecurity from pg_tables where schemaname='public';"

  3. First deploy:
       ./deploy/deploy.sh $PROJECT_ID $REGION

  4. Read the assigned URL, then set the two placeholder values for real and
     redeploy — GoogleCalendar__RedirectUri and ALLOWED_ORIGINS both need the
     live host, which does not exist until after step 3.

  5. Connect the GitHub repo in Cloud Build, create a push trigger on ^main\$
     with config /cloudbuild.yaml and build service account:
       $BUILD_SA

EOF
