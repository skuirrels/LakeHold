#!/usr/bin/env bash
set -euo pipefail

# Export from an API started the way CI starts it: on the host, with no OIDC authority configured.
#
# The document is not independent of that. Configuring an authority registers the browser-sign-in and
# protected-resource endpoints, which changes the order tags are first encountered and so the order
# they appear in here — a diff of pure reordering that fails the contract check for no real change.
# Exporting from the development compose stack, where OIDC is fully configured, produces exactly that.

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_url="${1:-http://127.0.0.1:5200/api/v1/openapi.json}"
destination="${repository_root}/openapi/lakehold-v1.json"
temporary="$(mktemp "${TMPDIR:-/tmp}/lakehold-openapi.XXXXXX")"
trap 'rm -f "${temporary}"' EXIT

curl --fail --silent --show-error --location \
  --retry 5 --retry-connrefused --retry-all-errors --retry-delay 1 \
  "${source_url}" --output "${temporary}"
"${repository_root}/scripts/verify-openapi.sh" "${temporary}"
mkdir -p "$(dirname "${destination}")"
mv "${temporary}" "${destination}"
trap - EXIT
printf 'Exported %s\n' "${destination}"
