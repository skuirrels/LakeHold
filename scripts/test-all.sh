#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ui_root="$repo_root/web/lakehold-ui"
compose_project="${LAKEHOLD_TEST_PROJECT:-lakehold-test}"
postgres_port="${LAKEHOLD_TEST_POSTGRES_PORT:-56439}"
minio_port="${LAKEHOLD_TEST_MINIO_PORT:-60000}"
ui_port="${LAKEHOLD_TEST_UI_PORT:-6499}"
npm_cache="${LAKEHOLD_TEST_NPM_CACHE:-$repo_root/.npm-cache}"
lock_dir="$repo_root/.lakehold-test.lock"
test_results="$(mktemp -d "${TMPDIR:-/tmp}/lakehold-test-results.XXXXXX")"
compose_files=(
  -f "$repo_root/compose.production.yaml"
  -f "$repo_root/compose.build.yaml"
  -f "$repo_root/compose.test.yaml"
)

if ! mkdir "$lock_dir" 2>/dev/null; then
  echo "error: another make test run is already using this checkout"
  echo "       if no test is running, remove $lock_dir and try again"
  exit 1
fi

cleanup_stack() {
  docker compose -p "$compose_project" "${compose_files[@]}" \
    --profile linq down --volumes --remove-orphans
}

cleanup_on_exit() {
  cleanup_stack || true
  rmdir "$lock_dir" 2>/dev/null || true
}
trap cleanup_on_exit EXIT

export LAKEHOLD_PORT="$ui_port"
export LAKEHOLD_TEST_POSTGRES_PORT="$postgres_port"
export LAKEHOLD_TEST_MINIO_PORT="$minio_port"
export LAKEHOLD_TEST_POSTGRES="Host=127.0.0.1;Port=$postgres_port;Database=lakeholdmeta;Username=lakehold;Password=lakehold"
export LAKEHOLD_TEST_S3_ENDPOINT="http://127.0.0.1:$minio_port"
export LAKEHOLD_TEST_S3_KEY="lakehold"
export LAKEHOLD_TEST_S3_SECRET="lakehold123"
export LAKEHOLD_TEST_S3_BUCKET="lakehold-test"
export npm_config_cache="$npm_cache"
export NG_CLI_ANALYTICS=false

echo "==> restoring and building the backend"
dotnet restore "$repo_root/Lakehold.slnx"
dotnet build "$repo_root/Lakehold.slnx" --no-restore

echo "==> installing, testing, and building the frontend"
npm --prefix "$ui_root" ci --include=dev --no-fund
if [[ ! -x "$ui_root/node_modules/.bin/playwright" ]]; then
  echo "error: npm ci completed without installing the Playwright executable"
  exit 1
fi
npm --prefix "$ui_root" run test:e2e:install
# Angular's local persistent cache is a developer optimisation, not test evidence. CI mode disables
# it, avoiding native LMDB cache corruption making two identical `make test` runs disagree.
CI=1 npm --prefix "$ui_root" run test:unit
CI=1 npm --prefix "$ui_root" run build

echo "==> starting disposable PostgreSQL and S3 integrations"
cleanup_stack
docker compose -p "$compose_project" "${compose_files[@]}" \
  up --detach --wait postgres minio
docker compose -p "$compose_project" "${compose_files[@]}" \
  run --rm minio-bucket

echo "==> running every backend test with integrations required"
dotnet test "$repo_root/Lakehold.slnx" \
  --no-build \
  --maxcpucount:1 \
  --filter 'FullyQualifiedName!~KafkaAvroProxyFixtureTests' \
  --logger trx \
  --results-directory "$test_results"

if grep -R -q 'outcome="NotExecuted"' "$test_results"; then
  echo "error: at least one backend test was skipped"
  exit 1
fi

echo "==> proving Kafka Avro through trusted proxy gateways"
"$repo_root/scripts/test-kafka-avro-proxy.sh"

echo "==> starting a fresh seeded application for browser journeys"
docker compose -p "$compose_project" "${compose_files[@]}" \
  --profile linq up --detach --build --wait api workbench linq-compiler

echo "==> running private-workbench browser journeys"
LAKEHOLD_WORKBENCH_ONLY=1 \
LAKEHOLD_E2E_BASE_URL="http://127.0.0.1:$ui_port" \
  npm --prefix "$ui_root" run test:e2e -- --grep-invert "@website"

echo "==> removing the normal test stack"
cleanup_stack

echo "==> running the public website and read-only demo journeys"
npm --prefix "$ui_root" run test:e2e:demo

echo "==> running the disposable production-operator journey"
npm --prefix "$ui_root" run test:e2e:phase2

echo "==> complete Lakehold test suite passed"
