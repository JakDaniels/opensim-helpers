#!/bin/sh
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# After installing or upgrading osctl (deb: "configure [old-version]").
set -e

first_install=false
case "${1:-}" in
  configure) [ -z "${2:-}" ] && first_install=true ;;
esac

# The account instances run as: a system user, its own group (which people who manage instances join), no login.
if ! getent passwd opensim >/dev/null; then
  useradd --system --user-group --no-create-home --home-dir /var/lib/osctl \
    --shell /usr/sbin/nologin --comment "OpenSimulator (osctl)" opensim
fi

# The directories and default files ('osctl init' never changes a file that exists, so upgrades keep the site's).
/usr/bin/osctl init >/dev/null

# Group-shared (PLAN.md "Packaging"): members of the opensim group manage everything without sudo; the setgid bit
# keeps new files in the group, and osctl's umask (007) keeps them group-writable.
# All of each tree on a first install; on an upgrade only the top directories (builds can be large).
for dir in /etc/osctl /var/lib/osctl /var/cache/osctl /var/log/osctl; do
  if $first_install; then
    chown -R opensim:opensim "$dir"
    find "$dir" -type d -exec chmod g+rwxs,o-rwx {} +
    find "$dir" -type f -exec chmod g+rw,o-rwx {} +
  else
    chown opensim:opensim "$dir"
    chmod 2770 "$dir"
  fi
done
# Passwords: root writes them ('sudo osctl db init'), the instances read them.
chown root:opensim /etc/osctl/secrets.ini
chmod 0640 /etc/osctl/secrets.ini

if [ -d /run/systemd/system ]; then
  systemd-tmpfiles --create /usr/lib/tmpfiles.d/osctl.conf || true
  systemctl daemon-reload
  # Instances running before an upgrade carry on; they use the new osctl at their next start.
else
  install -d -o opensim -g opensim -m 2770 /run/osctl
fi

if $first_install; then
  cat <<'MSG'

osctl is installed. Next:
  sudo usermod -aG opensim $USER            manage instances without sudo (then log in again)
  osctl doctor                              check .NET 8, tmux, MariaDB and the rest
  sudo osctl db init                        MariaDB users for osctl and OpenSim (or use --db sqlite)
  osctl build add master --repo git://opensimulator.org/git/opensim --ref master
  osctl build update master
  osctl instance add Test1 --estate-owner "Test User"
  osctl region add Welcome --instance Test1 --location 1000,1000
  osctl start Test1 && osctl console Test1
Settings: /etc/osctl/osctl.ini. Documentation: /usr/share/doc/osctl/

MSG
fi
