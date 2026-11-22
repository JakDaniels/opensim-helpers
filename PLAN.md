# osctl – plan

osctl ("MIOS 2.0") manages several OpenSimulator instances on one Ubuntu machine. It builds OpenSim from git,
creates instances and regions, generates each instance's configuration from layered `.ini` files, runs the
instances under systemd with a tmux console each, and handles databases, backups and scheduled restarts. Later
phases add Robust (grid services) and a stats web page.

It replaces MIOS (PHP, `docs/reference/MIOS` in the SpatialAudioServer tree), keeping what worked and removing
what aged badly. It is written in C# on .NET 10.

## Resolved decisions

| Topic | Decision |
|---|---|
| Name and CLI | `osctl`, with subcommands (`osctl instance add`, `osctl start`, …), not MIOS's `--flags`. |
| Supervision | One systemd template unit per instance (`osctl@<name>.service`). OpenSim runs in a tmux pane of its own, so `osctl console <name>` attaches like `mios --view`. Restart limits and "broken" detection come from systemd. osctl has no daemon of its own. |
| Databases | MariaDB/MySQL for normal instances (the default for `instance add`); SQLite (`--db sqlite`) for throwaway standalones. |
| Builds | Named builds (repo + branch/tag, optional addon modules and patches). Each instance chooses a build. |
| Install layout | A .deb package: `/usr/bin/osctl`, config in `/etc/osctl`, data in `/var/lib/osctl`, run as the system user `opensim`. systemd system units. |
| First start | Unattended. osctl writes `[Estates] DefaultEstateName/DefaultEstateOwner*` so OpenSim asks nothing. On a standalone OpenSim creates the owner's account: `instance add` generates its password, shows it once and keeps it in the instance's `secrets.ini` (`--estate-owner-password` sets your own). A manual foreground start stays available. |
| Scope | Simulators, then Robust grid services, backups (OAR/IAR, DB dumps), scheduled restarts. Stats and a web UI come last. Importing old MIOS installs is **not** in scope. |
| Console | OpenSim's local console in tmux; **no REST console** (OpenSim's `console = rest` replaces the local console, so the tmux pane could no longer take input). The tmux socket is open to the `opensim` group. `osctl cmd` types into the pane and reads the output back from it. |
| osctl's own config | ini files, in the same dialect as OpenSim's, so one parser serves both. |
| Region defaults | MIOS's `Regions.ini.example` values become the shipped `region-defaults.ini`. |
| Grids | OSGrid gets a ready-made config set now; other grids later (the mechanism allows for them); see "Config sets and grids". |
| CI and releases | Same flow as SpatialAudioServer: work on `develop`, CI on pull requests, merging develop → main releases (next patch version, `minor`/`major` labels), .deb packages served from the same signed apt repository. |
| Platform | Linux only, Ubuntu first (26.04 here; 24.04 should work). |
| Licence | Apache-2.0 (as the repo). |

## What MIOS did, and what changes

| MIOS | osctl |
|---|---|
| `--os-update` / `--rebuild` / `--recompile`: one `~/opensim` tree from master, built in place with Mono `xbuild` | `osctl build …`: several named builds, built with `./runprebuild.sh` + `dotnet build -c Release` into a **new release directory**, then switched over. Running instances never have their `bin/` rebuilt under them; rollback is possible. |
| `--os-config`: copy/download base ini files of a "config set" (`standalone`, `osgrid`) | Config sets stay; defined in `/etc/osctl/configsets/*.ini`. Files come from the build's `bin/` or a URL (URLs are cached). osctl follows `Include-*` directives itself, so a config set names only the top-level files. |
| Overrides: `.config/Overrides/*.ini` (global), `Instances/X/Configs/Overrides/*.ini`, merged in alphabetical order, later wins | Same layering and rules. New: `osctl config explain` shows which file set a key. |
| `[Const]` keys injected (`InstanceName`, `InstancePort`, `InstanceDBName`, …) for `${Const\|…}` use | Kept, with a few more (see "Configuration generation"). |
| Forced settings (PID file, console history, regions dir, script engine path, ports, DB connection strings) | Kept, updated: `[Startup] LogFile` is no longer read by OpenSim, so osctl writes a log4net config per instance and passes `-logconfig`. |
| `os_runner.sh.php` daemon polling a `.runlist` file, tmux windows in one session `OS`, restart up to 3 times in 30 s then "broken" | systemd unit per instance: `Restart=on-failure`, `StartLimitBurst`/`StartLimitIntervalSec`; a unit in `failed` state is "broken". One tmux server per instance. |
| `--start X --manual` for the first run (estate questions) | Not needed. `osctl run <name> --foreground` runs an instance in the terminal for debugging. |
| `--disable`/`--enable` (never start) | `osctl disable/enable` = start at boot or not (`systemctl enable/disable`). `osctl delete` needs the instance stopped and a confirmation (or `--yes`). |
| `--add-region`, `--rename-region`; ports from 9000–9025; UUIDs from md5(time); location clash check per config set | Same, plus `region remove`, `region move`, `region set`. Ports also checked against what is listening on the machine. UUIDs from `Guid.NewGuid()`. Location clashes are checked per grid. |
| `--visitors [--csv]` from `OpenSim.log` | `osctl visitors` with `--csv` / `--json`, regexes updated for today's log format. |
| `--init-mysql`: grants for `opensim_admin` and `opensim` | `osctl db init` (uses the root unix socket via sudo, as Ubuntu's MariaDB allows), checks `max_allowed_packet`. |
| `--get-stats` every minute via cron into RRD files; PHP + Apache graphs | Later phase: SQLite storage and a small built-in web page. |
| `AUTO_RESTART_INSTANCES` cron line | Per-instance systemd timers, with an in-world warning before the restart. |
| Everything under `~/MIOS`, PHP config file with passwords | `/etc/osctl` (ini files; secrets in a separate file, mode 0640) and `/var/lib/osctl`. |

## Layout on disk

```
/etc/osctl/
  osctl.ini                 global settings (port ranges, timeouts, tmux options, DB hosts, defaults)
  secrets.ini               DB passwords, RemoteAdmin passwords (0640 root:opensim)
  configsets/<set>.ini      config set definitions (files, order, source: build bin/ or URL)
  overrides/*.ini           global overrides, alphabetical
  region-defaults.ini       [Region Defaults] applied to every region
  builds/<build>.ini        build definitions (repo, ref, addons, patches)
/var/lib/osctl/
  builds/<build>/src/       git clone
  builds/<build>/releases/<yyyymmdd-hhmm>-<commit>/   built output (bin/)
  builds/<build>/current -> releases/…
  configsets/<set>/         cached downloaded files
  instances/<name>/
    instance.ini            kind (sim/robust), build, config set, database, estate, schedule
    overrides/*.ini         instance overrides
    regions/Regions.ini     the regions; other *.ini here = per-region module settings (as MIOS)
    startup_commands.txt, shutdown_commands.txt
    generated/              OpenSim.ini (combined, 0640), Regions/, config/ (empty), log4net.config — rebuilt on each start
    scriptengines/, data/ (SQLite files), backups/
/var/cache/osctl/assets/<grid>/   Flotsam asset cache, one per grid (sharing between grids is wrong, as MIOS noted)
/var/log/osctl/<name>/      OpenSim.log, console history
/run/osctl/                 tmux sockets, PID files
```

Instance names: letters, digits, `_` and `-` only (so they work unchanged as unit names, tmux sockets and DB
names). Region names follow OpenSim's rules (MIOS's regex, plus anything OpenSim itself accepts).

## Commands (first cut)

```
osctl init                           create the directories and default settings files (never overwrites)
osctl doctor                         check dotnet SDK/runtime 8, tmux, git, libgdiplus, MariaDB, permissions
osctl build add|list|update|rollback|remove <build> [--repo URL] [--ref REF] [--addon REPO[@REF]] [--patches DIR]
osctl configset list|fetch [<set>]
osctl db init                        create MariaDB users; per-instance DBs are made by 'instance add'
osctl instance add <name> --build B --configset S [--db mariadb|sqlite] [--estate NAME --estate-owner "First Last"]
osctl instance list|show|delete <name>
osctl region add <region> --instance I --location X,Y [--size 256..4096] [--port P] [--uuid U]
osctl region list|remove|rename|move|set <region> …
osctl list                           instances and regions table (as mios --list), with state
osctl start|stop|restart [<name>…] [--force]     none named = all enabled instances
osctl status [<name>…]               systemd state + readiness + regions
osctl enable|disable <name>…         start at boot or not
osctl console <name>                 attach to the tmux console (Ctrl-a d to leave, as MIOS)
osctl cmd <name> "<console command>" type a command into the tmux console and print its output
osctl logs <name> [-f]
osctl config show <name> [--section S…] | explain <name> <Section> <Key> | diff <name>
osctl visitors [<name>…] [--since DATE] [--csv|--json]
osctl backup <name> [--oar] [--iar USER] [--db] ; osctl backup list|prune
osctl schedule restart <name> "OnCalendar spec" [--warn 10m] ; osctl schedule backup …
osctl run <name> [--foreground]      what the systemd unit runs; not usually called by hand
```

All list/status commands take `--json` so scripts (and the later web UI) can use them.

Every command takes `--root DIR` (or `OSCTL_ROOT`), so development and tests use a directory instead of `/`, and
`--dry-run`, which prints the commands that would change something (`would run: …`) while queries still run.
Exit codes: 0 success; 1 the command ran but something failed (a doctor check, an instance that would not start);
2 an error osctl can explain (bad setting, missing file), printed as one line on stderr.

## Running instances: systemd + tmux

`osctl@.service` (installed by the package), roughly:

```
[Service]
User=opensim
Type=notify
ExecStart=/usr/bin/osctl run %i
ExecStop=/usr/bin/osctl run-stop %i
TimeoutStopSec=90
Restart=on-failure
RestartSec=10
LimitSTACK=1G            # what bin/opensim.sh does with ulimit -s
LimitNOFILE=65536
[Unit]
StartLimitIntervalSec=300
StartLimitBurst=3        # MIOS's "broken" rule, values from osctl.ini
```

`osctl run <name>`:
1. Regenerates the instance's configuration (below) and checks it (ports free, DB reachable).
2. Starts a tmux server just for this instance (`tmux -S /run/osctl/<name>.sock`, `remain-on-exit on`) running
   `dotnet OpenSim.dll -inimaster=none -inifile=generated/OpenSim.ini -inidirectory=generated/config -logconfig=generated/log4net.config`
   (`none` skips OpenSim's own master file; the empty `config/` stops OpenSim merging any `bin/config/*.ini`;
   tried by hand in phase 2: a SQLite standalone with two regions started unattended and shut down with exit code 0)
   in the build's `current/bin`. The tmux server stays in the unit's cgroup, so systemd sees and kills everything.
3. Waits for readiness, then sends `READY=1`. "LOGINS ENABLED" goes to the console only (the tmux pane), not the
   log; the log has `[RegionReady]: Enabled for region <name>` per region, and `/simstatus/` answers on the
   instance port. Which to use is settled in phase 3.
4. Waits for the OpenSim process to exit and exits with its exit code. A `shutdown` typed on the console exits 0
   and the unit stays stopped; a crash exits non-zero and systemd restarts it.

`osctl run-stop` sends `shutdown` (RemoteAdmin `admin_shutdown`, falling back to tmux `send-keys`) and waits;
systemd kills after `TimeoutStopSec`. `--force` on `osctl stop` kills straight away.

Permissions: members of group `opensim` run osctl without sudo. The package ships a polkit rule letting that group
manage `osctl@*.service` and the osctl timers. Each instance's tmux socket (`/run/osctl/<name>.sock`) is mode 0660
`opensim:opensim` and the tmux server allows the group (`server-access`), so any group member can attach and type.
That is deliberate: being in the group means you are trusted to run the simulators.

`osctl cmd <name> "…"`: notes the pane's history position, types the command (`send-keys`), waits until the
console prompt (`Region (…) #`) comes back or a timeout passes, then prints the new lines (`capture-pane`).
Commands that keep printing after the prompt returns (such as `save oar`) are handled by waiting for their
completion line in the log instead. Where RemoteAdmin has a matching call (shutdown, broadcast, save OAR), osctl
uses RemoteAdmin, because it reports success or failure properly.

## Configuration generation

For each start (and for `osctl config show`), in order, later wins:

1. The config set's files, loaded as OpenSim loads them from the build's `current/bin` (with the config set's
   overlay and edits, below).
2. `/etc/osctl/overrides/*.ini`, alphabetical.
3. The instance's `overrides/*.ini`, alphabetical.
4. What osctl sets itself (shown as "set by osctl" by `config explain`), `src/Osctl/Instances/ConfigGenerator.cs`:
   - `[Const]` `InstanceName`, `InstancePort` (lowest region port, as MIOS), `InstanceDBName`, `EstateDBName`,
     `InstanceDir`, `GridName`, for `${Const|…}` in overrides;
   - `[Startup]` `PIDFile`, `ConsoleHistoryFile`, `crash_dir`, `regionload_regionsdir`, startup/shutdown command
     files, `RegistryLocation`, `ManagedStatsRemoteFetchURI`; `[YEngine] ScriptEnginesPath` (XEngine is gone from
     current OpenSim); `[Network]` HTTP and HTTPS ports; `[AssetCache] CacheDirectory` (per grid);
   - everything else OpenSim writes in its working directory, the shared release `bin/` (found by running it):
     `[Mesh] MeshFileCachePath`, `[MapImageService] TilesStoragePath` and `[GridService] MapTileDirectory`;
   - `[Estates]` defaults (on a standalone also the owner's UUID, e-mail and password);
   - `[RemoteAdmin]` on, `access_ip_addresses = 127.0.0.1`, the instance's generated password;
   - MariaDB: `[DatabaseService]` provider and connection strings (the config set's `Include-Storage` is not
     followed); SQLite: every `URI=file:` connection string moved into the instance's `data/`.

`Include-*` keys are removed from the result (they have been followed), and so are sections left empty.
Values stay raw so OpenSim still expands `${Const|…}`. Regions: `[Region Defaults]` from `region-defaults.ini`,
then the instance's other `regions/*.ini` (per-region module settings, by region name), then `regions/Regions.ini`;
written to `generated/Regions/Regions.ini`. `osctl config show|explain|diff|generate` work on this; passwords are
shown as `********` unless `--show-secrets`.

Step 1 is loaded the way OpenSim loads it (with its includes, below), and the override files of steps 2 and 3 are
merged on top of that result. Overrides are plain files: an `Include-*` key in one is reported, not followed.

### The ini dialect

osctl reads and writes ini files the way Nini does, because Nini is what OpenSim uses (checked in opensim-libs
`Nini/Source/Ini/IniReader.cs`, `IniDocument.cs`, `Config/ConfigCollection.cs`, and OpenSim's
`ConfigurationLoader.cs`; `src/Osctl/Ini/` follows them, with tests):

- `;` starts a comment, at the start of a line or after an unquoted value. `#` is **not** a comment: a line
  starting with `#` is an error ("expected assignment operator").
- A value whose first character is `"` runs to the next `"`, exactly (spaces and `;` kept); the rest of the line
  is ignored. An unquoted value ends at `;`, at a `"`, or at the end of the line, and loses trailing spaces. So a
  value can never contain `"` or a line break; the writer refuses such values rather than write something OpenSim
  would read differently.
- Section and key names are case-sensitive.
- Within one file, a repeated key keeps its **first** value, and a repeated `[Section]` **replaces** the earlier
  one completely (it does not add to it).
- Between files (merging), later files win key by key; new sections and keys are added at the end.
- Includes: after each file is merged, every `Include-*` key in the merged result so far is looked at, and the
  files it names that are not on the list yet go to the **end** of the list. So included files load after all the
  files given on the command line. Paths are relative to `bin/`; `*` and `?` work in any path part (osctl sorts the
  matches, where OpenSim takes file system order); a non-wildcard include that matches nothing is only a warning.
  http includes (which OpenSim reads as XML) are not supported.
- OpenSim expands `${Section|Key}` itself after loading, so osctl leaves those untouched.

## Config sets and grids

A config set is `/etc/osctl/configsets/<name>.ini` (`src/Osctl/ConfigSets/ConfigSet.cs`; `osctl init` installs the
shipped ones). It works as an overlay on the build's `bin/`, because grid files include each other by their
`bin/` paths (OSGrid's `OpenSim.ini` includes `bin/config-include/GridHypergrid.ini`, which includes OSGrid's own
`GridCommon.ini`):

```
[ConfigSet]
    Description = …
    Mode = standalone | grid      ; standalone: own services, OpenSim creates the estate owner
    Grid = osgrid                 ; grid mode: shared estate database and asset cache, clash checks
    Load = OpenSimDefaults.ini, OpenSim.ini
[Files]                           ; a path as OpenSim reads it from bin/ = where it really comes from:
    OpenSim.ini = bin:OpenSim.ini.example                       ; another file in bin/
    config-include/GridCommon.ini = https://download.osgrid.org/GridCommon.ini.txt   ; downloaded
    config-include/Extra.ini = my-grid/Extra.ini                ; a file under /etc/osctl/configsets/
[Set OpenSim.ini]                 ; edits to a file before its includes are followed: Section|Key = value
    Architecture|Include-Architecture = config-include/StandaloneHypergrid.ini
```

`[Set …]` exists because an override cannot undo an include that has already been followed. Each standalone
instance is a grid of its own (`standalone-<instance>`). Downloads are cached in `/var/lib/osctl/configsets/<set>/`;
`osctl configset fetch` refreshes them (`instance add` fetches them the first time), and a download that does not
read as an ini file never replaces the cached copy. `osctl configset list|show` show them.

Shipped config sets:
- `standalone` and `standalone-hg`: OpenSim's own files.
- `osgrid`: OSGrid's downloadable `OpenSim.ini`, `GridCommon.ini` and `FlotsamCache.ini`, as MIOS had.
- Later, one for each further grid with a public setup (none chosen yet). Grids that publish their ini files are
  downloaded like OSGrid. For the others, the config set is `GridHypergrid.ini` plus a small shipped
  `GridCommon` override with that grid's service URIs.
- `grid-generic`: a template for any Hypergrid grid. `osctl configset add <name> --grid-uri http://host:port`
  reads the grid's `/get_grid_info` (grid name, login URI, gatekeeper, …), fills in the usual Robust service
  URIs on that host, and writes `/etc/osctl/configsets/<name>.ini` plus its override file for you to check.

Grid details change, so shipped grid config sets are data files that can be updated without a new osctl release
(`osctl configset fetch` also refreshes the shipped grid definitions from the osctl repository).

## Builds

A build is defined in `/etc/osctl/builds/<name>.ini` (`osctl build add`, `osctl build addon add`, or by hand):
`[Build]` has `Repository`, `Ref` (branch, tag or commit; `HEAD` = the repository's default branch),
`Patches` (optional directory of `*.patch`, relative to `/etc/osctl/builds`), `KeepReleases` (3), `Prebuild`
(`sh runprebuild.sh`) and `Compile` (`dotnet build -c Release OpenSim.sln`); each `[Addon <name>]` has
`Repository`, `Ref` and `Patches` (a directory in the addon, such as os-webrtc-sas's `core-patches`). The commands
are settable for forks that build differently (and so the tests can use a fake build).

`osctl build update [<b>…] [--force] [--verbose]`, holding a lock on the build (`.lock`, flock):

1. Clone or fetch the source into `builds/<b>/src` and each addon into `builds/<b>/addons/<a>` (`--prune --tags
   --force`, and `remote set-head --auto` so `HEAD` follows a changed default branch).
2. Resolve the refs (`origin/<ref>`, then a tag, then a commit) and work out the fingerprint: the commits, the
   SHA-256 of every patch, and the two commands. If it matches the current release's, the build is up to date and
   nothing is built (`--force` builds anyway).
3. Check out with `checkout --force --detach` and `clean -fdx` (osctl owns these clones), copy each addon into
   `addon-modules/<a>` without its `.git`, apply the patches (the addons' first, in addon order, then the build's;
   each directory sorted by name; `git apply --check` first, so a patch that no longer applies stops the update
   with a clear message).
4. Run the prebuild and compile commands (`sh -c`, telemetry off), all output to `logs/<release>.log`; on failure
   the last 30 lines are shown.
5. Check `bin/` has `OpenSim.dll`, `OpenSim.runtimeconfig.json` and `Robust.dll`, copy it to
   `releases/<release>.partial/bin` with `release.ini` (repository, ref, commit and subject, addon commits, patch
   hashes, commands, osctl version), rename to `releases/<release>`, and switch `current` with one `rename(2)`.
6. Prune: keep the newest `KeepReleases` and always the current one.

Release names are `yyyyMMdd-HHmmss-<commit>` (UTC), so they sort by age. Nothing instances use changes until the
switch, and a failure leaves the current release as it was. Instances use the new release at their next
(re)start; `osctl build update --restart` (phase 3) restarts the instances on that build.
`osctl build rollback <b> [--to <release>]` switches `current` back; `osctl build list|show` show the releases;
`osctl build remove <b> --yes` removes everything.

Tried for real: build `sas-dev` = OpenSim master + `os-webrtc-sas` (its `core-patches/` applied), 1 min 42 s;
an update with nothing new takes 0.3 s. A release's `bin/` is about 90 MB and the source tree about 490 MB.

Still to do in later phases: pruning must also keep releases that running instances use (phase 3); builds run
as the `opensim` user with group-writable directories when osctl is installed (phase 4); releases could
hard-link files unchanged from the previous release to save space.

## Databases

- MariaDB: `sudo osctl db init`, once, connects as root over the unix socket (Ubuntu's MariaDB lets the system root
  in that way), generates the passwords missing from `secrets.ini` (edited in place, comments kept), and creates
  `osctl_admin` and `opensim`, both with all rights on `opensim\_%` databases only; it warns when
  `max_allowed_packet` is under 128 MB. A server on another machine: create the two users by hand. `osctl doctor`
  checks that `osctl_admin` can connect.
- Each instance gets `opensim_<name>`; the estate database is one per grid (`opensim_estate_<grid>`), not one
  shared by everything as in MIOS, because standalones and grids must not share estates. `instance add` creates
  them (utf8mb4) and refuses to start without `db init`. `instance delete` dumps the instance's database (and a
  standalone's own estate database) with `mariadb-dump` to `/var/lib/osctl/deleted/<name>-<time>/` and drops them
  (`--keep-database` leaves them). The dump's password goes in a temporary 0600 file, not on the command line.
- SQLite: OpenSim's `SQLiteStandalone.ini` storage, with file paths redirected to the instance's `data/`.
  Standalone only.
- Library: MySqlConnector.

## Backups and scheduled restarts

- OAR per region through RemoteAdmin (`admin_save_oar`), waiting for completion; IAR via a console command
  (needs care: it wants the user's password, to be checked); DB dumps with `mariadb-dump` or SQLite's online
  backup. Stored under `instances/<name>/backups/` (or a configured path) with retention (`keep daily/weekly`).
- Timers: `osctl-restart@<name>.timer` and `osctl-backup@<name>.timer`, `OnCalendar` from `instance.ini`. A
  scheduled restart broadcasts a warning in-world (RemoteAdmin `admin_broadcast`) N minutes before.

## Robust and local grids (phase 6)

Instances of kind `robust`: config sets from `Robust.ini.example` / `Robust.HG.ini.example`, same layering, run
`dotnet Robust.dll`, own port range, own database. A Robust instance defines a grid; simulator instances can
use a generated config set `grid:<robust instance>` whose GridCommon URIs point at it. `osctl user create` (via
the Robust console) for the first accounts.

## Stats and web UI (phase 7)

`osctl-web.service` (ASP.NET Core, localhost by default): polls each running instance's ManagedStats JSON every
minute into SQLite (MIOS's `Stats.ini` definitions are the starting set of series, with retention like MIOS's
RRAs: 1 min for a day, 5 min for a week, 1 h for 8 weeks, 1 day for 3 years), and serves a page with status and
graphs (static HTML/JS, no build step). Read-only to begin with.

## Implementation notes

- .NET 10, one console project `src/Osctl` + `src/Osctl.Tests` (xUnit). Libraries: System.CommandLine,
  MySqlConnector, Microsoft.Data.Sqlite (later), Spectre.Console for tables (optional).
- External tools are called through one small process-runner abstraction (git, dotnet, tmux, systemctl, mariadb),
  so commands can be tested with fakes and `--dry-run` can print what would happen.
- osctl itself never needs the .NET 8 runtime; OpenSim does. `osctl doctor` checks both.
- Every new source file starts with the Apache-2.0 copyright/SPDX header, as in SpatialAudioServer.
- CHANGELOG.md with an `## Unreleased` section, as in SpatialAudioServer; `scripts/make-release.sh` turns it
  into the release notes.
- CI and releases copied from SpatialAudioServer: `develop` for day-to-day work, a ruleset on `main` (pull request,
  merge commits only, required test check), `ci.yml` on pull requests, `drafts.yml` (by hand) for test packages,
  `release.yml` on merge to main, `pages.yml` adding the .debs to the existing signed apt repository
  (jakdaniels.github.io), so `apt install osctl` works next to `spatialaudioserver`.

## Phases

0. **Skeleton**: solution, CLI framework, `/etc/osctl` + `/var/lib/osctl` loading (overridable with
   `--root` for tests and dev), ini library with tests, `osctl doctor`.
1. **Builds**: `build add/update/list/rollback`, release directories, addon modules and patches.
2. **Instances and regions**: instance/region commands, port/UUID/location allocation, config generation,
   `config show/explain/diff`, MariaDB and SQLite setup.
3. **Running**: `osctl run`, systemd unit, tmux console, start/stop/restart/status/enable/disable/console/cmd/logs,
   readiness, RemoteAdmin. First real goal: the SpatialAudioServer test standalone run by osctl.
4. **Packaging**: .deb (x64, arm64), `opensim` user, polkit rule, tmpfiles, units; CI and release workflows as
   in SpatialAudioServer. (CI for tests starts in phase 0; this phase adds packages and releases.)
5. **Operations**: visitors, backups, scheduled restarts with warnings.
6. **Robust and local grids.**
7. **Stats and web UI.**

## Testing

- Unit tests: ini parsing/merging against real OpenSim files, allocation rules, unit/config rendering (golden files),
  visitor regexes against sample log lines.
- Integration (CI and locally): build OpenSim master, create a SQLite standalone with one region, `osctl run
  --foreground` in tmux without systemd, wait for readiness, shut down cleanly; check the exit-code handling.
- Hands-on: systemd units, crash restart and "broken", reboot start, console attach, on this machine.

## Open questions

None at the moment. Which further grids get shipped config sets is decided later.

Settled: the apt repository stays in SpatialAudioServer. Its `pages.yml` already pulls acousticmultimixer's latest
release from that repository; osctl's latest release is added the same way, and osctl's `release.yml` triggers
SpatialAudioServer's `pages.yml` (`gh workflow run -R JakDaniels/SpatialAudioServer`, which needs a token with
access to that repository).
