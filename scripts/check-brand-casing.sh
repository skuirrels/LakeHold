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

# This check used to shell out to ripgrep from inside an `if`, where a non-zero exit means "no
# matches" and `set -e` does not apply. On a machine without ripgrep the search never ran, the
# missing command looked exactly like a clean result, and the script reported success — so the rule
# was enforced only where someone happened to have the tool installed. Everything below exists to
# make "the search did not run" distinguishable from "the search found nothing".
#
# Perl is the engine because it is the one PCRE-capable search available everywhere this runs, and
# because a single engine cannot disagree with itself: a second, faster path selected by
# availability would decide the same file differently on a developer's machine than in CI.
if ! command -v perl >/dev/null 2>&1; then
  echo "error: perl is required to run the brand casing check, and it is not on PATH" >&2
  echo "       install it, or run the check where it is available — it must not be skipped" >&2
  exit 1
fi

# A path that has been moved or renamed would otherwise narrow the check in silence.
for path in "${paths[@]}"; do
  if [[ ! -e "$path" ]]; then
    echo "error: ${path#"$repo_root/"} is missing; update the path list in ${BASH_SOURCE[0]##*/}" >&2
    exit 1
  fi
done

# `-T` skips the icons and images under public/, which cannot hold prose but can hold byte
# sequences that match. It is tested only after readability, because it answers false for a file it
# cannot open — which would put an unreadable file straight back into the silently-unscanned
# category everything here exists to rule out. Closing each file keeps `$.` a per-file line number.
scan='
  my $pattern = shift @ARGV;
  for my $file (@ARGV) {
    next unless -f $file;
    die "cannot read $file\n" unless -r $file;
    next unless -T $file;
    open my $fh, "<", $file or die "cannot read $file: $!\n";
    while (my $line = <$fh>) {
      print "$file:$.:$line" if $line =~ /$pattern/;
    }
    close $fh;
  }
'

set +e
matches="$(find "${paths[@]}" -type f -print0 | xargs -0 perl -e "$scan" "$invalid_pattern")"
status=$?
set -e

if ((status != 0)); then
  echo "error: the brand casing check could not run (search exited $status)" >&2
  exit 1
fi

if [[ -n "$matches" ]]; then
  echo "error: website and documentation copy must spell the product LakeHold" >&2
  echo "$matches" >&2
  exit 1
fi

echo "Brand casing check passed."
