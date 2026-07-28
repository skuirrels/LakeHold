#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_project="lakehold-demo"
compose_files=(
  -f "$repo_root/compose.production.yaml"
  -f "$repo_root/compose.build.yaml"
  -f "$repo_root/compose.test.yaml"
  -f "$repo_root/compose.demo.yaml"
)

cleanup() {
  docker compose -p "$compose_project" "${compose_files[@]}" down --volumes --remove-orphans
}
trap cleanup EXIT

export LAKEHOLD_PORT="${LAKEHOLD_DEMO_UI_PORT:-6599}"

cleanup
docker compose -p "$compose_project" "${compose_files[@]}" \
  up --detach --build --wait api workbench

LAKEHOLD_DEMO=1 \
LAKEHOLD_E2E_BASE_URL="${LAKEHOLD_E2E_BASE_URL:-http://127.0.0.1:6599}" \
npm --prefix "$repo_root/web/lakehold-ui" run test:e2e -- --grep "@demo|@website"
