#!/usr/bin/env bash
set -euo pipefail

# Removes linked worktrees whose work is finished, and the local branches they held.
#
# Agent sessions create a worktree per task and do not always remove it afterwards. They accumulate:
# each one is a full checkout, each pins a branch so `git branch -d` refuses it, and one of them
# eventually holds `main`, at which point the primary checkout can never simply be on the default
# branch. That last consequence is the expensive one — "am I current?" stops having an obvious
# answer, and a stale checkout is how confident, wrong claims about the codebase get made.
#
# Dry run by default, in keeping with every other destructive operation in this repository. Pass
# --apply to actually remove. A worktree is only ever a candidate when all of these hold:
#
#   * it is a linked worktree, never this primary checkout;
#   * it has no uncommitted tracked changes and no untracked files worth keeping;
#   * it is on a branch (a detached HEAD may be mid-rebase or mid-bisect); and
#   * that branch is already an ancestor of origin/main, so nothing unmerged can be lost.
#
# Note that "clean and merged" does not prove "finished": an agent session that has just started,
# or one parked between steps, looks exactly like a completed one. That is precisely why this
# refuses to act without --apply — read the list before confirming it.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

apply=false
if [[ "${1:-}" == "--apply" ]]; then
  apply=true
elif [[ $# -gt 0 ]]; then
  echo "usage: $(basename "$0") [--apply]" >&2
  exit 2
fi

# Judging "merged" against a stale remote-tracking ref would report a branch as unmerged long after
# its pull request landed, which is the failure mode that leaves these lying around in the first place.
echo "==> git fetch origin"
git fetch --quiet origin

upstream="origin/main"
git rev-parse --verify --quiet "$upstream" >/dev/null || {
  echo "error: $upstream not found; cannot decide what is merged." >&2
  exit 1
}

candidates=()
skipped=()

# `git worktree list --porcelain` emits blank-line-separated records: a `worktree <path>` line, a
# `HEAD <sha>` line, and then either `branch <ref>` or `detached`.
current_path=""
current_branch=""
flush() {
  [[ -z "$current_path" ]] && return 0

  if [[ "$current_path" == "$repo_root" ]]; then
    skipped+=("$current_path — primary checkout")
  elif [[ -z "$current_branch" ]]; then
    skipped+=("$current_path — detached HEAD, may be mid-operation")
  elif [[ -n "$(git -C "$current_path" status --porcelain 2>/dev/null)" ]]; then
    skipped+=("$current_path — has uncommitted or untracked files")
  elif ! git merge-base --is-ancestor "$current_branch" "$upstream" 2>/dev/null; then
    skipped+=("$current_path [$current_branch] — branch not merged into $upstream")
  else
    candidates+=("$current_path"$'\t'"$current_branch")
  fi

  current_path=""
  current_branch=""
}

while IFS= read -r line; do
  case "$line" in
    worktree\ *) flush; current_path="${line#worktree }" ;;
    branch\ refs/heads/*) current_branch="${line#branch refs/heads/}" ;;
  esac
done < <(git worktree list --porcelain)
flush

if [[ ${#skipped[@]} -gt 0 ]]; then
  echo
  echo "Keeping:"
  printf '  %s\n' "${skipped[@]}"
fi

if [[ ${#candidates[@]} -eq 0 ]]; then
  echo
  echo "Nothing to prune."
  exit 0
fi

echo
echo "Finished worktrees (clean, and merged into $upstream):"
while IFS=$'\t' read -r path branch; do
  printf '  %s [%s]\n' "$path" "$branch"
done < <(printf '%s\n' "${candidates[@]}")

if [[ "$apply" != true ]]; then
  echo
  echo "Dry run. Re-run with --apply to remove them and delete their branches."
  exit 0
fi

echo
while IFS=$'\t' read -r path branch; do
  git worktree remove --force "$path"
  echo "removed worktree $path"
  # -d rather than -D: git re-checks the merge itself, so a mistake in the loop above cannot
  # silently discard commits.
  if git branch -d "$branch" >/dev/null 2>&1; then
    echo "deleted branch  $branch"
  else
    echo "kept branch     $branch (git declined to delete it)"
  fi
done < <(printf '%s\n' "${candidates[@]}")

git worktree prune
echo
echo "Done."
git worktree list
