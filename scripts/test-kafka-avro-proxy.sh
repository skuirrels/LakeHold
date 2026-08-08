#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture="$repo_root/tests/fixtures/kafka-avro-proxy/compose.yaml"
compose_project="${LAKEHOLD_KAFKA_TEST_PROJECT:-lakehold-kafka-avro-test}"

cleanup() {
  docker compose -p "$compose_project" -f "$fixture" down -v --remove-orphans
}

trap 'cleanup || true' EXIT

docker compose -p "$compose_project" -f "$fixture" \
  up -d --wait kafka schema-registry schema-registry-tls socks-gateway registry-proxy
docker compose -p "$compose_project" -f "$fixture" run --rm lakehold-connector-test
