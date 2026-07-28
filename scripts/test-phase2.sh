#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_project="lakehold-phase2"
compose_files=(
  -f "$repo_root/compose.production.yaml"
  -f "$repo_root/compose.build.yaml"
  -f "$repo_root/compose.test.yaml"
  -f "$repo_root/compose.phase2.yaml"
)

cleanup() {
  docker compose -p "$compose_project" "${compose_files[@]}" down --volumes --remove-orphans
}
trap cleanup EXIT

export LAKEHOLD_PORT="${LAKEHOLD_PHASE2_UI_PORT:-6399}"

cleanup
docker compose -p "$compose_project" "${compose_files[@]}" \
  up --detach --build --wait api workbench webhook

LAKEHOLD_PHASE2=1 \
LAKEHOLD_E2E_BASE_URL="${LAKEHOLD_E2E_BASE_URL:-http://127.0.0.1:6399}" \
LAKEHOLD_PHASE2_API_URL="${LAKEHOLD_PHASE2_API_URL:-http://127.0.0.1:6200}" \
LAKEHOLD_PHASE2_WEBHOOK_URL="${LAKEHOLD_PHASE2_WEBHOOK_URL:-http://127.0.0.1:6190}" \
npm --prefix "$repo_root/web/lakehold-ui" run test:e2e -- --grep "@phase2"
