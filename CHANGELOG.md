# Changelog

Changes that matter to people who run osctl. `scripts/make-release.sh` (to come, as in SpatialAudioServer) turns
the Unreleased section into the release notes.

## Unreleased

- osctl: first skeleton: `osctl doctor` checks git, tmux (3.3 or later), the .NET SDK and the .NET 8 runtime
  OpenSim needs, libgdiplus, the MariaDB client, systemd and the `opensim` account; `--root` (or `OSCTL_ROOT`)
  points osctl at another directory than `/` for development.
- osctl: `osctl init` creates osctl's directories and default settings files (`osctl.ini`, `secrets.ini` readable
  by its group only, `region-defaults.ini` with MIOS's region values) and never changes existing files.
- osctl: `--dry-run` shows what a command would change; expected errors are one line on stderr with exit code 2.
- osctl: `osctl build add|addon add|addon remove|list|show|update|rollback|remove`: named OpenSim builds from git,
  with addon modules and patches; each update builds a new release, makes it current only once it is complete,
  skips the build when nothing changed, and keeps the last few releases for rollback.
- osctl: `osctl configset list|show|fetch`, with the config sets `standalone`, `standalone-hg` and `osgrid`
  (OSGrid's published ini files, downloaded and cached).
- osctl: `osctl instance add|list|show|delete`, `osctl region add|list|remove|rename|move|set` and `osctl list`:
  instances and regions with ports, UUIDs and grid locations allocated and checked for clashes; a standalone's
  estate owner gets a generated password, shown once.
- osctl: `osctl config show|explain|diff|generate`: each instance's OpenSim configuration built from its config set,
  the global and instance overrides, and osctl's own settings, with `explain` showing where a value comes from;
  nothing an instance writes goes into the shared build.
- osctl: `sudo osctl db init` sets up MariaDB for osctl; instances get their own databases, dumped before they are
  dropped; `osctl doctor` checks the database connection.
