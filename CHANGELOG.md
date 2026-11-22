# Changelog

Changes that matter to people who run osctl. `scripts/make-release.sh` (to come, as in SpatialAudioServer) turns
the Unreleased section into the release notes.

## Unreleased

- osctl: first skeleton: `osctl doctor` checks git, tmux (3.3 or later), the .NET SDK and the .NET 8 runtime
  OpenSim needs, libgdiplus, the MariaDB client, systemd and the `opensim` account; `--root` (or `OSCTL_ROOT`)
  points osctl at another directory than `/` for development.
