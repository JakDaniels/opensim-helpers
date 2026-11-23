#!/bin/sh
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# After removing osctl. The instances, builds, logs and the opensim account stay, so a reinstall carries on;
# "apt purge" removes the settings, builds, instances (with their SQLite data) and logs. MariaDB databases are
# never dropped by a purge: 'osctl instance delete' does that, after a dump.
set -e
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload || true
fi
if [ "${1:-}" = purge ]; then
  rm -rf /etc/osctl /var/lib/osctl /var/cache/osctl /var/log/osctl /run/osctl
fi
