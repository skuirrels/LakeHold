#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Human-facing prose uses LakeHold. The historical Lakehold spelling remains part of technical
# contracts and cannot be renamed casually: .NET projects/namespaces, configuration paths and
# environment variables, JSON configuration roots, HTTP header names, and repository paths.
invalid_pattern='\bLakehold\b(?!\.[A-Za-z]|:[A-Za-z]|__[A-Za-z]|-[A-Z]|/|"\s*:)'

paths=(
  "$repo_root/README.md"
  "$repo_root/docs"
  "$repo_root/web/lakehold-ui/README.md"
  "$repo_root/web/lakehold-ui/public"
  "$repo_root/web/lakehold-ui/src"
  "$repo_root/web/lakehold-ui/e2e"
)

if matches="$(rg -n --pcre2 "$invalid_pattern" "${paths[@]}")"; then
  echo "error: website and documentation copy must spell the product LakeHold" >&2
  echo "$matches" >&2
  exit 1
fi

echo "Brand casing check passed."
