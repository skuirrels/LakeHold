#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_project="lakehold-demo"
compose_files=(
  -f "$repo_root/compose.production.yaml"
  -f "$repo_root/compose.build.yaml"
  -f "$repo_root/compose.demo.yaml"
)

cleanup() {
  docker compose -p "$compose_project" "${compose_files[@]}" --profile linq \
    down --volumes --remove-orphans
}

finish() {
  status=$?
  if ((status != 0)); then
    docker compose -p "$compose_project" "${compose_files[@]}" --profile linq \
      logs --no-color --tail 100 demo-postgres linq-compiler || true
  fi
  cleanup
  exit "$status"
}
trap finish EXIT

export LAKEHOLD_PORT="${LAKEHOLD_DEMO_UI_PORT:-6599}"
export LAKEHOLD_LINQ_PLANNER_KEY="${LAKEHOLD_LINQ_PLANNER_KEY:-$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')}"

cleanup
docker compose -p "$compose_project" "${compose_files[@]}" --profile linq \
  up --detach --build --wait api workbench linq-compiler

LAKEHOLD_DEMO=1 \
LAKEHOLD_E2E_BASE_URL="${LAKEHOLD_E2E_BASE_URL:-http://127.0.0.1:6599}" \
npm --prefix "$repo_root/web/lakehold-ui" run test:e2e -- --grep "@demo|@website"
