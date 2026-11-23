#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Prints the next release's version after the newest release tag (vX.Y.Z), for release.yml after a merge:
#   scripts/next-version.sh ["LABELS"]      (the pull request's labels, separated by spaces)
# The patch version goes up (0.1.0 → 0.1.1); with the label "minor" the minor version (0.2.0), with "major" the
# major version (1.0.0). After a pre-release (v0.2.0-rc.1), plain: its release (0.2.0). With no release yet: 0.1.0.
set -euo pipefail

labels=" ${1:-} "
previous="$(git -c versionsort.suffix=- tag -l 'v[0-9]*' --sort=-v:refname | head -n 1)"
if [ -z "$previous" ]; then
  echo "0.1.0"
  exit 0
fi
release="${previous#v}"
release="${release%%-*}"
IFS=. read -r major minor patch <<< "$release"
case "$labels" in
  *" major "*) echo "$((major + 1)).0.0" ;;
  *" minor "*) echo "$major.$((minor + 1)).0" ;;
  *)
    if [ "$release" != "${previous#v}" ]; then
      echo "$release"
    else
      echo "$major.$minor.$((patch + 1))"
    fi
    ;;
esac
