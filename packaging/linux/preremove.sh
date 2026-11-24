#!/bin/sh
# Copyright 2026 Simon Annetts/Jak Daniels
# SPDX-License-Identifier: Apache-2.0

# Before removing osctl (deb: "remove"). Upgrades leave the instances running.
set -e
if [ "${1:-}" = remove ] && [ -d /run/systemd/system ]; then
  # The schedules' timers first, so that none starts a restart or backup on the way out.
  for unit in $(systemctl list-unit-files --plain --no-legend 'osctl-restart@*.timer' 'osctl-backup@*.timer' | awk '{print $1}'); do
    systemctl disable --now "$unit" || true
  done
  for unit in $(systemctl list-units --plain --no-legend 'osctl@*.service' | awk '{print $1}'); do
    systemctl stop "$unit" || true
  done
fi
