#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# The changelog (CHANGELOG.md: an "## Unreleased" section kept up to date with each change to osctl itself
# (src/Osctl), then one section per release, newest first; each release's section is its tag's message).
#
#   scripts/changelog.sh [VERSION]
#     Prints the changelog for the packages, made from the release tags' messages, so a release's packages hold its
#     own notes (build-packages.sh). With the build's VERSION, a build
#     that is not a release (a draft) gets CHANGELOG.md's Unreleased entries on top, headed with that version.
#   scripts/changelog.sh --unreleased < CHANGELOG.md
#     Prints the Unreleased entries: the next release's notes (release.yml, make-release.sh).
#   scripts/changelog.sh --rollover TAG < CHANGELOG.md > NEW
#     After TAG is released: its section "## VERSION (date)" (the tag's message) goes under "## Unreleased", which keeps
#     only the entries the release did not have (those develop gained after the merge). Exit status 3 when the file
#     already has that version.
set -euo pipefail

trim_blank() { sed -e '/./,$!d' | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'; }
# A part of CHANGELOG.md: its own heading and introduction (head), the Unreleased section's lines (unreleased), or
# the released sections (released).
part() {
  awk -v part="$1" '
    /^## / { seen = 1; inside = ($0 ~ /^## Unreleased/); if (inside) next }
    part == "head" && !seen { print }
    part == "unreleased" && inside { print }
    part == "released" && seen && !inside { print }'
}
heading() { echo "# Changelog"; echo; echo "What changed in each release: the same text as its release notes."; }
tag_message() { git tag -l --format='%(contents)' "$1" | sed '/-----BEGIN PGP SIGNATURE-----/,$d' | trim_blank; }
tag_date() {
  local date
  date="$(git tag -l --format='%(taggerdate:short)' "$1")"
  [ -n "$date" ] || date="$(git log -1 --format=%cs "$1")"
  echo "$date"
}

case "${1:-}" in
  --unreleased)
    part unreleased | trim_blank
    ;;

  --rollover)
    tag="${2:?usage: $0 --rollover TAG < CHANGELOG.md}"
    version="${tag#v}"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    cat > "$work/old"
    if grep -q "^## $version " "$work/old"; then exit 3; fi
    # The entries the release had: the Unreleased section of CHANGELOG.md at the tag.
    git show "$tag:CHANGELOG.md" 2>/dev/null | part unreleased | grep -v '^$' > "$work/released" || true
    part unreleased < "$work/old" | grep -vxF -f "$work/released" | trim_blank > "$work/left" || true
    if [ -n "$(part head < "$work/old" | tr -d '[:space:]')" ]; then part head < "$work/old" | trim_blank; else heading; fi
    echo
    echo "## Unreleased"
    echo
    if [ -s "$work/left" ]; then cat "$work/left"; echo; fi
    echo "## $version ($(tag_date "$tag"))"
    echo
    tag_message "$tag"
    if [ -n "$(part released < "$work/old" | tr -d '[:space:]')" ]; then
      echo
      part released < "$work/old" | trim_blank
    fi
    ;;

  -*)
    sed -n '8,17s/^# //p' "$0" >&2
    exit 2
    ;;

  *)
    heading
    echo
    if [ -n "${1:-}" ] && ! git describe --exact-match --tags --match 'v[0-9]*' HEAD >/dev/null 2>&1; then
      echo "## Unreleased ($1)"
      echo
      unreleased="$(part unreleased < CHANGELOG.md 2>/dev/null | trim_blank)" || true
      echo "${unreleased:-Nothing noted since the last release.}"
      echo
    fi
    git -c versionsort.suffix=- for-each-ref --sort=-v:refname --format='%(refname:strip=2)' 'refs/tags/v[0-9]*' | while read -r tag; do
      echo "## ${tag#v} ($(tag_date "$tag"))"
      echo
      tag_message "$tag"
      echo
    done
    ;;
esac
