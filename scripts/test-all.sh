#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ui_root="$repo_root/web/lakehold-ui"
compose_project="${LAKEHOLD_TEST_PROJECT:-lakehold-test}"
postgres_port="${LAKEHOLD_TEST_POSTGRES_PORT:-56439}"
minio_port="${LAKEHOLD_TEST_MINIO_PORT:-60000}"
ui_port="${LAKEHOLD_TEST_UI_PORT:-6499}"
npm_cache="${LAKEHOLD_TEST_NPM_CACHE:-$repo_root/.npm-cache}"
test_results="$(mktemp -d "${TMPDIR:-/tmp}/lakehold-test-results.XXXXXX")"
compose_files=(
  -f "$repo_root/compose.production.yaml"
  -f "$repo_root/compose.build.yaml"
  -f "$repo_root/compose.test.yaml"
)

cleanup() {
  docker compose -p "$compose_project" "${compose_files[@]}" \
    down --volumes --remove-orphans
}
trap cleanup EXIT

export LAKEHOLD_PORT="$ui_port"
export LAKEHOLD_TEST_POSTGRES_PORT="$postgres_port"
export LAKEHOLD_TEST_MINIO_PORT="$minio_port"
export LAKEHOLD_TEST_POSTGRES="dbname=lakeholdmeta host=127.0.0.1 port=$postgres_port user=lakehold password=lakehold"
export LAKEHOLD_TEST_S3_ENDPOINT="http://127.0.0.1:$minio_port"
export LAKEHOLD_TEST_S3_KEY="lakehold"
export LAKEHOLD_TEST_S3_SECRET="lakehold123"
export LAKEHOLD_TEST_S3_BUCKET="lakehold-test"
export npm_config_cache="$npm_cache"

echo "==> restoring and building the backend"
dotnet restore "$repo_root/Lakehold.slnx"
dotnet build "$repo_root/Lakehold.slnx" --no-restore

echo "==> installing, testing, and building the frontend"
npm --prefix "$ui_root" ci
npm --prefix "$ui_root" run test:e2e:install
npm --prefix "$ui_root" run test:unit
npm --prefix "$ui_root" run build

echo "==> starting disposable PostgreSQL and S3 integrations"
cleanup
docker compose -p "$compose_project" "${compose_files[@]}" \
  up --detach --wait postgres minio
docker compose -p "$compose_project" "${compose_files[@]}" \
  run --rm minio-bucket

echo "==> running every backend test with integrations required"
dotnet test "$repo_root/Lakehold.slnx" \
  --no-build \
  --logger trx \
  --results-directory "$test_results"

if grep -R -q 'outcome="NotExecuted"' "$test_results"; then
  echo "error: at least one backend test was skipped"
  exit 1
fi

echo "==> starting a fresh seeded application for browser journeys"
docker compose -p "$compose_project" "${compose_files[@]}" \
  up --detach --build --wait api web

echo "==> running normal browser journeys"
LAKEHOLD_E2E_BASE_URL="http://127.0.0.1:$ui_port" \
  npm --prefix "$ui_root" run test:e2e

echo "==> removing the normal test stack"
cleanup

echo "==> running the read-only demo journey"
npm --prefix "$ui_root" run test:e2e:demo

echo "==> running the disposable production-operator journey"
npm --prefix "$ui_root" run test:e2e:phase2

echo "==> complete Lakehold test suite passed"
