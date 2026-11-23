#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Tags a release of main (PLAN.md "Packaging"), from the repository root, after the pull request from develop
# into main has passed and been merged:
#   scripts/make-release.sh 0.1.0 [--dry-run | --wait]   (or 0.1.0-rc.1 for a pre-release)
#   scripts/make-release.sh --changelog 0.1.0            only step 5, for a tag that exists already
#  1. fetches, and takes main's latest commit on GitHub (whatever branch is checked out here); checks the tag is new
#     and newer than the last release;
#  2. ci.yml must have passed for that commit, or for the pull request's head it merged with the same files (the
#     pull request's own run: no second run on main); waits if that is still running; if nothing equivalent was
#     tested (a docs-only pull request, a commit made on main), starts ci.yml on main and waits (about 5 minutes);
#     stops if it failed;
#  3. opens an editor for the tag's message (the release notes' top part), prefilled with the "## Unreleased" section
#     of CHANGELOG.md on main (kept up to date with each change to osctl itself): usually just save;
#     with no such section, the commits since the last release; an empty message stops;
#  4. pushes the tag, which starts release.yml (a draft release with every package, about 10 minutes), and prints
#     the run's address and the command that publishes the draft once it is ready (publish it by hand after a look);
#     with --wait, waits for the run and prints the draft's address;
#  5. right after the tag is pushed: in CHANGELOG.md on the local develop, the tag's message becomes the section
#     "## 0.1.0 (date)" and "## Unreleased" starts again, holding only entries develop gained after the merge; a
#     commit made without touching the working tree, whatever is checked out: push develop to keep it.
# Needs git and gh (logged in). Only the tag is pushed (step 4); --dry-run stops before it and starts nothing.
set -euo pipefail

usage() { sed -n '7,8s/^# //p' "$0" >&2; exit 2; }
mode=release
if [ "${1:-}" = --changelog ]; then mode=changelog; shift; fi
[ $# -ge 1 ] || usage
version="${1#v}"
dry_run=false wait=false
case "${2:-}" in
  --dry-run) dry_run=true ;;
  --wait) wait=true ;;
  '') ;;
  *) usage ;;
esac
tag="v$version"
remote=origin

die() { echo "make-release: $*" >&2; exit 1; }
step() { echo; echo "== $*"; }

trim_blank() { sed -e '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'; }

# After the tag, on the local develop (not pushed): main is merged back in, so the tag (on main) is in develop's
# history and MinVer versions drafts after it; then CHANGELOG.md's released entries become the section "## VERSION
# (date)" (the tag's message), and "## Unreleased" keeps only what develop gained after the merge
# (scripts/changelog.sh --rollover). Made without a checkout (merge-tree, a scratch index), so the working tree and
# whatever is checked out are left alone. release.yml does the same after a merge.
add_changelog() {
  local tag="$1" version="${1#v}" develop base tagged merged work commit status=0
  develop="$(git rev-parse refs/heads/develop)"
  if ! git merge-base --is-ancestor "$remote/develop" "$develop"; then
    echo "CHANGELOG.md: local develop is behind $remote/develop; pull it, then: scripts/make-release.sh --changelog $version"
    return 0
  fi
  base="$develop"
  tagged="$(git rev-parse "$tag^{commit}")"
  if ! git merge-base --is-ancestor "$tagged" "$base"; then
    # main holds only what came from develop, so the merge changes no files; a conflict means commits made on main.
    merged="$(git merge-tree --write-tree "$base" "$tagged")" \
      || { echo "CHANGELOG.md: merging $tag into develop conflicts; merge main into develop by hand."; return 0; }
    base="$(git commit-tree "$merged" -p "$base" -p "$tagged" -m "Merge $version from main into develop")"
  fi
  work="$(mktemp -d)"
  git show "$base:CHANGELOG.md" 2>/dev/null | scripts/changelog.sh --rollover "$tag" > "$work/new" || status=$?
  if [ "$status" = 0 ]; then
    GIT_INDEX_FILE="$work/index" git read-tree "$base"
    GIT_INDEX_FILE="$work/index" git update-index --add --cacheinfo "100644,$(git hash-object -w "$work/new"),CHANGELOG.md"
    commit="$(git commit-tree "$(GIT_INDEX_FILE="$work/index" git write-tree)" -p "$base" -m "CHANGELOG: $version")"
  elif [ "$status" = 3 ]; then
    echo "CHANGELOG.md on develop already has $version"
    commit="$base"
  else
    rm -rf "$work"
    die "CHANGELOG.md: scripts/changelog.sh --rollover failed"
  fi
  rm -rf "$work"
  [ "$commit" != "$develop" ] || return 0
  if [ "$(git symbolic-ref -q --short HEAD)" = develop ]; then
    git merge --ff-only --quiet "$commit" \
      || { echo "develop could not move to ${commit::7} (files changed here?): git merge --ff-only $commit"; return 0; }
  else
    git update-ref -m "make-release: $version" refs/heads/develop "$commit" "$develop"
  fi
  echo "develop: $version merged in from main and added to CHANGELOG.md (${commit::7}); push develop to keep it"
}

echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' \
  || die "'$1' is not a version (0.1.0 or 0.1.0-rc.1)"
[ -f src/Osctl.slnx ] || die "run it from the repository root"
command -v gh >/dev/null || die "gh is needed"
gh auth status >/dev/null 2>&1 || die "gh is not logged in (gh auth login)"
repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

if [ "$mode" = changelog ]; then
  git fetch --quiet --no-tags "$remote" develop 'refs/tags/v*:refs/tags/v*' || die "git fetch failed"
  git rev-parse -q --verify "refs/tags/$tag" >/dev/null || die "no tag $tag"
  add_changelog "$tag"
  exit 0
fi

step "Checks"
# Only the version tags: develop-draft moves with every drafts run and would be refused ("would clobber").
git fetch --quiet --no-tags "$remote" main develop 'refs/tags/v*:refs/tags/v*' || die "git fetch failed"
sha="$(git rev-parse "$remote/main")"
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null || git ls-remote --exit-code --tags "$remote" "$tag" >/dev/null; then
  die "$tag already exists"
fi
previous="$(git -c versionsort.suffix=- tag -l 'v[0-9]*' --sort=-v:refname | head -n 1)"
if [ -n "$previous" ]; then
  newest="$(printf '%s\n%s\n' "${previous#v}" "$version" | sort -V | tail -n 1)"
  [ "$newest" = "$version" ] && [ "${previous#v}" != "$version" ] || die "$tag is not newer than $previous"
  [ "$(git rev-parse "$previous^{commit}")" != "$sha" ] || die "main has nothing new since $previous"
  echo "Release $tag: main at ${sha::7}, $(git rev-list --count "$previous..$sha") commits after $previous"
else
  echo "Release $tag: main at ${sha::7}, the first release"
fi
unmerged="$(git rev-list --count "$sha..$remote/develop")"
[ "$unmerged" = 0 ] || echo "Note: develop has $unmerged commit(s) not in main; they are not in this release."

step "CI for ${sha::7} on main"
# What ci.yml may have tested: this commit, or the pull request's head a merge brought in, if its files are the same.
tested=("$sha")
if merged="$(git rev-parse -q --verify "$sha^2")" && git diff --quiet "$merged" "$sha"; then
  tested+=("$merged")
  echo "Merged from ${merged::7} with the same files: its pull request's ci.yml run counts"
fi
shas_json="$(printf '"%s",' "${tested[@]}")"
shas_json="[${shas_json%,}]"
ci_state() { # the best result of ci.yml runs for those commits: success, running, failure or none
  gh run list -R "$repo" -w ci.yml -L 100 --json headSha,status,conclusion \
    -q "[.[] | select(.headSha as \$h | $shas_json | index(\$h))] |
        if any(.conclusion == \"success\") then \"success\"
        elif any(.status != \"completed\") then \"running\"
        elif length > 0 then \"failure\" else \"none\" end"
}
state="$(ci_state)"
if [ "$state" = none ]; then
  if $dry_run; then
    echo "(dry run) ci.yml has not run for it: would start it on main and wait"
  else
    gh workflow run ci.yml -R "$repo" --ref main
    echo "ci.yml has not run for it (a docs-only pull request?): started it on main; waiting for it to appear…"
    for _ in $(seq 30); do sleep 5; state="$(ci_state)"; [ "$state" = none ] || break; done
    [ "$state" != none ] || die "the ci.yml run did not appear"
  fi
fi
if [ "$state" = running ]; then
  if $dry_run; then
    echo "(dry run) ci.yml is running for it: would wait for it"
  else
    run="$(gh run list -R "$repo" -w ci.yml -L 100 --json databaseId,headSha,status \
      -q "[.[] | select((.headSha as \$h | $shas_json | index(\$h)) and .status != \"completed\")][0].databaseId")"
    echo "Waiting for ci.yml (run $run, about 5 minutes)…"
    gh run watch -R "$repo" "$run" --exit-status --interval 30 >/dev/null || true
    state="$(ci_state)"
  fi
fi
case "$state" in
  success) echo "ci.yml passed" ;;
  failure) die "ci.yml failed for ${sha::7}: fix it on develop and merge again, or re-run it if it was a flake" ;;
esac

step "Tag message"
message_file="$(mktemp)"
trap 'rm -f "$message_file" "$message_file.clean"' EXIT
# The notes: this version's section on develop (a release tagged again), or main's "## Unreleased" section.
written="$(git show refs/heads/develop:CHANGELOG.md 2>/dev/null \
  | awk -v head="## $version " 'index($0, head) == 1 { f = 1; next } f && /^## / { exit } f { print }' | trim_blank)"
from="$version's section of CHANGELOG.md on develop"
if [ -z "$written" ]; then
  written="$(git show "$sha:CHANGELOG.md" 2>/dev/null | scripts/changelog.sh --unreleased)" || true
  from="the Unreleased section of CHANGELOG.md on main"
fi
{
  if [ -n "$written" ]; then
    echo "$written"
    echo
    echo "# From $from: save as it is, or edit."
  else
    echo "What changed in $version (replace this with the release's notes: the first line is the summary)."
    echo
    git log --no-merges -n 60 --format='- %s' ${previous:+"$previous.."}"$sha"
    echo
  fi
  echo "# Lines starting with # are dropped. An empty message stops the release; nothing has been pushed yet."
} > "$message_file"
if $dry_run; then
  echo "(dry run) would open an editor with:"
  sed 's/^/    /' "$message_file"
  step "Dry run: would tag ${sha::7} as $tag and push the tag, starting release.yml, then add it to CHANGELOG.md on develop"
  exit 0
fi
"${GIT_EDITOR:-$(git var GIT_EDITOR)}" "$message_file"
grep -v '^#' "$message_file" > "$message_file.clean" || true
[ -n "$(tr -d '[:space:]' < "$message_file.clean")" ] || die "empty message: stopped (nothing pushed)"
git tag -a "$tag" -F "$message_file.clean" "$sha"

step "Push $tag"
if ! git push "$remote" "refs/tags/$tag"; then
  git tag -d "$tag" >/dev/null
  die "the push failed; the local tag was removed"
fi

step "CHANGELOG.md"
add_changelog "$tag"

step "release.yml"
run=""
for _ in $(seq 30); do
  sleep 5
  run="$(gh run list -R "$repo" -w release.yml -L 20 --json databaseId,headBranch \
    -q "[.[] | select(.headBranch == \"$tag\")][0].databaseId // empty")"
  [ -n "$run" ] && break
done
[ -n "$run" ] || die "release.yml did not start for $tag: see the Actions page"
run_url="https://github.com/$repo/actions/runs/$run"
if ! $wait; then
  echo "release.yml is running (about 10 minutes): $run_url"
  echo "    (gh run watch $run -R $repo  to follow it)"
  echo "Then check the draft release (Releases, \"osctl $version\") and publish it there or with:"
  echo "    gh release edit $tag -R $repo --draft=false"
  exit 0
fi
echo "Waiting for release.yml ($run_url, about 10 minutes)…"
if gh run watch -R "$repo" "$run" --exit-status --interval 30 >/dev/null; then
  echo
  echo "Draft release ready: $(gh release view "$tag" -R "$repo" --json url -q .url)"
  echo "Check it (notes, files), then publish it on that page or with:"
  echo "    gh release edit $tag -R $repo --draft=false"
else
  die "release.yml failed: gh run view $run -R $repo --log-failed (fix, then re-run it from the Actions page)"
fi
