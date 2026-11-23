#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Builds the osctl .deb (PLAN.md "Packaging") into dist/:
#   scripts/build-packages.sh [x64|arm64]      (default x64)
# Needs the .NET SDK and nfpm (https://nfpm.goreleaser.com; CI pins its version). The version comes from git tags
# (MinVer), so run it in a clone with its history. Used by .github/workflows/packages.yml and by hand.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

arch="${1:-x64}"
case "$arch" in
  x64) goarch=amd64 ;;
  arm64) goarch=arm64 ;;
  *) echo "usage: $0 [x64|arm64]" >&2; exit 2 ;;
esac

stage="dist/stage"
rm -rf "$stage"
mkdir -p "$stage" dist

# Self-contained single file: the .NET runtime is inside, so the target needs no .NET 10 (OpenSim's .NET 8 is its
# own matter). Invariant globalisation: no libicu dependency (its package name changes with every release).
dotnet publish src/Osctl -c Release -r "linux-$arch" --self-contained -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:InvariantGlobalization=true -p:DebugType=none -o "$stage/publish"
install -m 0755 "$stage/publish/osctl" "$stage/osctl"

# The unit as osctl writes it with the default settings, running /usr/bin/osctl ('osctl units install' writes a new
# one to /etc/systemd/system after [Restart] or [Timeouts] change). Built with the host's own publish, which runs here.
dotnet publish src/Osctl -c Release -p:DebugType=none -o "$stage/host" >/dev/null
dotnet "$stage/host/osctl.dll" units show --defaults --program /usr/bin/osctl > "$stage/osctl@.service"
grep -q '^ExecStart=/usr/bin/osctl run %i$' "$stage/osctl@.service"

version="$(dotnet msbuild src/Osctl -restore -t:MinVer -getProperty:MinVerVersion -nologo | tail -n 1)"
if [ -z "$version" ]; then
  echo "Could not determine the version." >&2
  exit 1
fi
echo "Version $version ($goarch)"
scripts/changelog.sh "$version" > "$stage/CHANGELOG.md"

VERSION="$version" ARCH="$goarch" nfpm package -f packaging/linux/nfpm.yaml -p deb -t dist/
ls -l dist/*.deb
