#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ui_root="$repo_root/web/lakehold-ui"
node_options="${NODE_OPTIONS:-}"

# Node 24 exposes an experimental global localStorage without a backing file. That prevents
# Vitest's jsdom environment from installing its real in-memory browser storage. Disable only that
# Node global when the runtime supports the flag; older supported Node releases continue unchanged.
if node --help | grep -q -- '--no-experimental-webstorage'; then
  node_options="${node_options:+$node_options }--no-experimental-webstorage"
fi

cd "$ui_root"
NODE_OPTIONS="$node_options" exec ng test --watch=false
