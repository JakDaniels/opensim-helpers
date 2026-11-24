#!/bin/sh
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Installs the osctl .deb in this (clean, root) system and checks it, as far as a container allows (no systemd):
# the package installs with its dependencies, sets up the opensim account and the group-shared directories, a member
# of the opensim group can work without sudo, and a purge removes it all. Used by .github/workflows/packages.yml.
#   scripts/test-packages.sh DIR        (DIR holds the packages; the amd64 one is tested)
set -eu
dir="$(cd "$1" && pwd)" # absolute: apt takes "packages/x.deb" for a package name, "/…/x.deb" for a file
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
deb=$(ls "$dir"/osctl_*_amd64.deb)

check() { # check DESCRIPTION EXPECTED ACTUAL
  if [ "$2" = "$3" ]; then echo "ok   $1: $3"; else echo "FAIL $1: expected '$2', got '$3'" >&2; exit 1; fi
}

echo "== install"
apt-get install -y -q "$deb"
osctl --version

echo "== account and directories"
getent passwd opensim
check "secrets.ini owner and mode" "root:opensim 640" "$(stat -c '%U:%G %a' /etc/osctl/secrets.ini)"
for d in /etc/osctl /var/lib/osctl /var/cache/osctl /var/log/osctl /run/osctl; do
  check "$d" "opensim:opensim 2770" "$(stat -c '%U:%G %a' "$d")"
done
test -f /etc/osctl/osctl.ini
test -f /etc/osctl/configsets/osgrid.ini
test -f /usr/lib/systemd/system/osctl@.service
grep -q '^ExecStart=/usr/bin/osctl run %i$' /usr/lib/systemd/system/osctl@.service
grep -q '^ExecStart=/usr/bin/osctl restart --scheduled %i$' /usr/lib/systemd/system/osctl-restart@.service
grep -q '^ExecStart=/usr/bin/osctl backup create --scheduled %i$' /usr/lib/systemd/system/osctl-backup@.service
test -f /usr/share/polkit-1/rules.d/50-osctl.rules
osctl configset list

echo "== a member of the opensim group, without sudo"
useradd --create-home --groups opensim tester
runuser -u tester -- osctl doctor || true # no systemd or .NET 8 here: warnings and failures are expected
runuser -u tester -- osctl build add master --repo git://opensimulator.org/git/opensim --ref master
check "a member's build definition" "opensim 660" "$(stat -c '%G %a' /etc/osctl/builds/master.ini)"
runuser -u tester -- osctl build list
# Someone outside the group cannot even read the settings.
useradd --create-home outsider
if runuser -u outsider -- cat /etc/osctl/osctl.ini >/dev/null 2>&1; then echo "FAIL an outsider can read /etc/osctl" >&2; exit 1; fi
echo "ok   an outsider cannot read /etc/osctl"

echo "== upgrade (reinstall) keeps the site's files"
echo "; kept" >> /etc/osctl/osctl.ini
apt-get install -y -q --reinstall "$deb"
grep -q '^; kept$' /etc/osctl/osctl.ini
test -f /etc/osctl/builds/master.ini

echo "== purge"
apt-get purge -y -q osctl
for d in /etc/osctl /var/lib/osctl /usr/bin/osctl; do
  if [ -e "$d" ]; then echo "FAIL $d is still there after a purge" >&2; exit 1; fi
done
echo "PASS"
