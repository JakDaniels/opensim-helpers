#!/usr/bin/env bash
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Builds the apt repository for GitHub Pages (PLAN.md "Packaging"), signed with the project key, from release
# packages already downloaded, as SpatialAudioServer's scripts/build-repos.sh does (.deb only):
#   scripts/build-repos.sh PACKAGES_DIR SITE_DIR SITE_URL
# PACKAGES_DIR holds the .deb files to serve (pages.yml puts the latest two releases there). The signing key must be
# in gpg's keyring (pages.yml imports it from a secret), and its public half must equal packaging/signing-key.asc,
# which the site serves as key.asc. Needs apt-ftparchive (apt-utils) and gpg.
# Layout:  deb/  dists/stable/{Release,InRelease,Release.gpg,main/binary-{amd64,arm64}/Packages[.gz]}, pool/main/
#          key.asc, index.html
set -euo pipefail

packages="$(cd "${1:?usage: $0 PACKAGES_DIR SITE_DIR SITE_URL}" && pwd)"
site="${2:?}"
url="${3:?}"
url="${url%/}"
root="$(cd "$(dirname "$0")/.." && pwd)"

# The signing key: the one secret key in the keyring, and it must be the published one.
fingerprint="$(gpg --batch --with-colons --list-secret-keys | awk -F: '$1 == "fpr" { print $10; exit }')"
[ -n "$fingerprint" ] || { echo "No secret key in gpg's keyring." >&2; exit 1; }
published="$(gpg --batch --with-colons --show-keys "$root/packaging/signing-key.asc" | awk -F: '$1 == "fpr" { print $10; exit }')"
[ "$fingerprint" = "$published" ] || { echo "The signing key $fingerprint is not packaging/signing-key.asc ($published)." >&2; exit 1; }
# The key has no passphrase of its own (the Actions secret holding it is encrypted), so nothing asks for one.
sign() { gpg --batch --yes --local-user "$fingerprint" "$@"; }

rm -rf "$site"
mkdir -p "$site/deb/pool/main"
site="$(cd "$site" && pwd)"
cp "$root/packaging/signing-key.asc" "$site/key.asc"
touch "$site/.nojekyll"

# One suite ("stable", component "main") for every supported Debian and Ubuntu release: the package carries what
# it needs.
cp "$packages"/*.deb "$site/deb/pool/main/"
(
  cd "$site/deb"
  for arch in amd64 arm64; do
    dir="dists/stable/main/binary-$arch"
    mkdir -p "$dir"
    apt-ftparchive --arch "$arch" packages pool/main > "$dir/Packages"
    gzip -9kn "$dir/Packages"
  done
  apt-ftparchive \
    -o APT::FTPArchive::Release::Origin=opensimtools \
    -o APT::FTPArchive::Release::Label=opensimtools \
    -o APT::FTPArchive::Release::Suite=stable \
    -o APT::FTPArchive::Release::Codename=stable \
    -o "APT::FTPArchive::Release::Architectures=amd64 arm64" \
    -o APT::FTPArchive::Release::Components=main \
    -o "APT::FTPArchive::Release::Description=opensimtools packages" \
    release dists/stable > Release.tmp
  mv Release.tmp dists/stable/Release
  sign --clearsign -o dists/stable/InRelease dists/stable/Release
  sign --armor --detach-sign -o dists/stable/Release.gpg dists/stable/Release
)

# A page for people who open the address in a browser.
versions="$(ls "$site/deb/pool/main" | sed -nE 's/^osctl_([^_]+)_amd64\.deb$/\1/p' | sort -V -r | paste -sd, - | sed 's/,/, /g')"
cat > "$site/index.html" <<HTML
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>opensimtools packages</title>
<style>body{font:16px/1.5 system-ui,sans-serif;max-width:52rem;margin:2rem auto;padding:0 1rem}
pre{background:#f4f4f4;padding:.8rem;overflow-x:auto}@media (prefers-color-scheme:dark){body{background:#111;color:#ddd}pre{background:#222}a{color:#8cf}}</style>
</head><body>
<h1>opensimtools packages</h1>
<p>The apt repository for <a href="https://github.com/JakDaniels/opensimtools">opensimtools</a>: osctl, which manages
OpenSimulator instances on one machine. Versions here: ${versions:-none yet}. Every release is also on the
<a href="https://github.com/JakDaniels/opensimtools/releases">Releases page</a>.</p>
<h2>Ubuntu 24.04 or later, Debian 13 or later</h2>
<pre>curl -fsSL $url/key.asc | sudo gpg --dearmor -o /usr/share/keyrings/opensimtools.gpg
echo "deb [signed-by=/usr/share/keyrings/opensimtools.gpg] $url/deb stable main" |
  sudo tee /etc/apt/sources.list.d/opensimtools.list
sudo apt update
sudo apt install osctl</pre>
<p>OpenSim itself needs the .NET 8 SDK; <code>osctl doctor</code> checks what is missing.</p>
<p>The signing key's fingerprint: <code>$fingerprint</code>.</p>
</body></html>
HTML

du -sh "$site"
find "$site" -type f | sed "s|^$site/||" | sort
