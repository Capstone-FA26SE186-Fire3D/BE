#!/usr/bin/env bash
set -euo pipefail
# Run in Google Cloud Shell or a shell with authenticated gcloud. No credentials in arguments.
: "${GCP_PROJECT_ID:?Set the existing project ID with billing enabled}"
: "${GITHUB_REPOSITORY_ID:?Set the numeric GitHub repository ID}"
: "${GITHUB_OWNER_ID:?Set the numeric GitHub organization ID}"
region="${GCP_REGION:-asia-northeast1}"
[[ "$GITHUB_REPOSITORY_ID" =~ ^[0-9]+$ && "$GITHUB_OWNER_ID" =~ ^[0-9]+$ ]]
gcloud projects describe "$GCP_PROJECT_ID" --format='value(projectId)'
test "$(gcloud billing projects describe "$GCP_PROJECT_ID" --format='value(billingEnabled)')" = True || { echo 'Enable billing first'; exit 1; }
gcloud services enable run.googleapis.com artifactregistry.googleapis.com iam.googleapis.com iamcredentials.googleapis.com sts.googleapis.com secretmanager.googleapis.com --project "$GCP_PROJECT_ID"
number=$(gcloud projects describe "$GCP_PROJECT_ID" --format='value(projectNumber)')
runtime="fire3d-runtime@$GCP_PROJECT_ID.iam.gserviceaccount.com"
deployer="fire3d-deploy@$GCP_PROJECT_ID.iam.gserviceaccount.com"
for name in fire3d-runtime fire3d-deploy; do
  gcloud iam service-accounts describe "$name@$GCP_PROJECT_ID.iam.gserviceaccount.com" --project "$GCP_PROJECT_ID" >/dev/null 2>&1 || \
    gcloud iam service-accounts create "$name" --project "$GCP_PROJECT_ID"
done
gcloud artifacts repositories describe fire3d --location "$region" --project "$GCP_PROJECT_ID" >/dev/null 2>&1 || \
  gcloud artifacts repositories create fire3d --repository-format docker --location "$region" --project "$GCP_PROJECT_ID"
gcloud artifacts repositories add-iam-policy-binding fire3d --location "$region" --project "$GCP_PROJECT_ID" --member "serviceAccount:$deployer" --role roles/artifactregistry.writer >/dev/null
gcloud projects add-iam-policy-binding "$GCP_PROJECT_ID" --member "serviceAccount:$deployer" --role roles/run.developer --condition=None >/dev/null
gcloud iam service-accounts add-iam-policy-binding "$runtime" --project "$GCP_PROJECT_ID" --member "serviceAccount:$deployer" --role roles/iam.serviceAccountUser >/dev/null
for secret in fire3d-supabase-connection fire3d-jwt-key; do
  gcloud secrets describe "$secret" --project "$GCP_PROJECT_ID" >/dev/null 2>&1 || \
    gcloud secrets create "$secret" --replication-policy automatic --project "$GCP_PROJECT_ID"
  gcloud secrets add-iam-policy-binding "$secret" --project "$GCP_PROJECT_ID" --member "serviceAccount:$runtime" --role roles/secretmanager.secretAccessor >/dev/null
done
gcloud iam workload-identity-pools describe fire3d-github --location global --project "$GCP_PROJECT_ID" >/dev/null 2>&1 || \
  gcloud iam workload-identity-pools create fire3d-github --location global --project "$GCP_PROJECT_ID" --display-name 'Fire3D GitHub'
condition="assertion.repository_id == '$GITHUB_REPOSITORY_ID' && assertion.repository_owner_id == '$GITHUB_OWNER_ID' && assertion.ref == 'refs/heads/develop' && assertion.workflow_ref == 'Capstone-FA26SE186-Fire3D/BE/.github/workflows/deploy-staging.yml@refs/heads/develop'"
provider_args=(--location global --workload-identity-pool fire3d-github --project "$GCP_PROJECT_ID" --issuer-uri https://token.actions.githubusercontent.com --attribute-mapping 'google.subject=assertion.sub,attribute.repository_id=assertion.repository_id' --attribute-condition "$condition")
if gcloud iam workload-identity-pools providers describe github --location global --workload-identity-pool fire3d-github --project "$GCP_PROJECT_ID" >/dev/null 2>&1; then
  gcloud iam workload-identity-pools providers update-oidc github "${provider_args[@]}"
else
  gcloud iam workload-identity-pools providers create-oidc github "${provider_args[@]}"
fi
gcloud iam service-accounts add-iam-policy-binding "$deployer" --project "$GCP_PROJECT_ID" \
  --member "principalSet://iam.googleapis.com/projects/$number/locations/global/workloadIdentityPools/fire3d-github/attribute.repository_id/$GITHUB_REPOSITORY_ID" \
  --role roles/iam.workloadIdentityUser >/dev/null
printf 'GCP_PROJECT_ID=%s\nGCP_REGION=%s\nGCP_RUNTIME_SERVICE_ACCOUNT=%s\nGCP_DEPLOY_SERVICE_ACCOUNT=%s\nGCP_WIF_PROVIDER=projects/%s/locations/global/workloadIdentityPools/fire3d-github/providers/github\n' "$GCP_PROJECT_ID" "$region" "$runtime" "$deployer" "$number"
echo 'Next: add secret versions privately, set GitHub staging variables, then merge reviewed PR to develop.'
