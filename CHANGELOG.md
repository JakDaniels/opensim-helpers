# Changelog

Changes that matter to people who run osctl. `scripts/make-release.sh` (to come, as in SpatialAudioServer) turns
the Unreleased section into the release notes.

## Unreleased

- osctl: NPCs work again. The generated OpenSim.ini dropped sections with nothing set in them, and OpenSim
  turns NPCs off when `[NPC]` is missing; empty sections are now kept, as OpenSim keeps them.

## 0.4.1 (2026-11-27)

- osctl: `osctl grid start|stop|restart <grid>` works on a grid and its simulators together: the grid starts first
  and stops last, and a restart brings back the simulators that were running.
- osctl: `osctl config tree` lists only the files osctl reads (others are counted; `--all` lists them), and for an
  instance not yet moved to /etc only its definition, not its data and asset files.

## 0.4.0 (2026-11-27)

- osctl: `osctl config tree` shows the files under /etc/osctl as a tree with what each one is, and
  `osctl config tree <instance>` the files its configuration is made from, in the order they are read.
- osctl: `osctl help --tree` shows every command as a tree (`--options` adds their options), and
  `osctl help <command>` is the same as `--help`.
- osctl: each instance's definition (instance.ini, secrets, regions, overrides, start-up and shutdown commands)
  is now in `/etc/osctl/instances/<name>/`, so all configuration is under `/etc/osctl`; existing instances are
  moved there by the first osctl command once they are stopped. `/var/lib/osctl` keeps their data and backups.
- osctl: `osctl region defaults set|get` (`--global` or `--instance`) for region defaults, now also per instance,
  and `osctl config set <section> Key=Value --global|--instance` for OpenSim.ini overrides, so neither needs
  files edited by hand.
- osctl: `osctl backup restore` puts a backup back: the databases, a grid's asset files, osctl's own definition
  (to make a lost instance again), or one region's OAR or a user's IAR. It shows a plan until `--yes`, and
  backs the instance up as it is now first. Backups now also hold the instance's definition (not its secrets).
- osctl: a grid's backups now hold its FSAssets asset files too. Each backup hard-links the files the last one has
  and copies only new ones, checked against their hashes, so a week of backups takes little more than one copy.
- osctl: no warning about a missing config-include/osslEnable.ini at start; OpenSim ships it only as an example,
  for local OSSL changes.

## 0.3.0 (2026-11-26)

- osctl: grids of your own. `osctl grid add` sets up Robust (Hypergrid, groups, offline messages, and assets as
  files with FSAssets) with a config set for its simulators; `osctl user create|password` manage accounts;
  simulators wait for their grid and check their estate owner has an account; the first region becomes the default.

## 0.2.1 (2026-11-25)

- osctl: warns (in `config` commands, `start`, `restart` and the runner log) when an instance has SpatialAudioServer
  voice (os-webrtc-sas) but no `StunServers`, without which WebRTC viewers' voice never starts; `start` and `restart`
  now show configuration warnings.

## 0.2.0 (2026-11-24)

- osctl: `osctl visitors` lists who arrived in which region and when (login, hypergrid login or arrival, with the
  login IP), from the instances' logs; `--since`, `--csv`, `--json`.
- osctl: `osctl backup create|list|prune|iar-password`: OARs of every region, the databases (mariadb-dump or
  sqlite3, gzipped) and users' inventories (IARs), one directory per backup; pruning keeps 7 daily and 4 weekly.
- osctl: `osctl schedule restart|backup|remove|list`: scheduled restarts and backups as systemd timers (crontab lines
  in direct mode); `osctl restart|stop --warn 10m` warns people in-world first, again at 5 and 1 minutes.
- osctl: `osctl doctor` checks for sqlite3, which backups of SQLite instances need.

- osctl: `osctl region get <region> [settings…]` shows a region's effective settings (defaults, module files, its own),
  filtered by name with wildcards; `--own`, `--origin`, `--json`.

## 0.1.0 (2026-11-23)

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
- osctl: `osctl start|stop|restart|status|enable|disable|console|cmd|logs`: instances run under systemd (or
  directly, `[Supervisor] Mode`), each with its OpenSim console in tmux; ready when every region says LOGINS
  ENABLED; stopped through RemoteAdmin; restarted after a crash and shown as broken after repeated quick crashes.
- osctl: `osctl units install [--user]` writes the systemd unit; `osctl run|run-stop` are what it runs.
- osctl: `osctl build update --restart` restarts the instances on a build that got a new release; releases that
  running instances use are never pruned.
- osctl: a .deb package (x64, arm64) with the systemd unit, the opensim account and group-shared directories, so
  members of the opensim group manage instances without sudo; osctl runs with umask 007 for that.
- osctl: `osctl units show --defaults --program PATH` prints the unit for another installation (the package build).
