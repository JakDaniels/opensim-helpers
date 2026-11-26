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
| CI and releases | Same flow as SpatialAudioServer: work on `develop`, CI on pull requests, merging develop → main releases (next patch version, `minor`/`major` labels). The repository is `JakDaniels/opensimtools` (renamed from opensim-helpers); its own GitHub Pages site, https://jakdaniels.github.io/opensimtools/, is the signed apt repository (its own key), not SpatialAudioServer's. |
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
osctl region list|remove|rename|move|set|get <region> …
osctl list                           instances and regions table (as mios --list), with state
osctl start|stop|restart [<name>…] [--force]     none named = all enabled instances
osctl status [<name>…]               systemd state + readiness + regions
osctl enable|disable <name>…         start at boot or not
osctl console <name>                 attach to the tmux console (Ctrl-a d to leave, as MIOS)
osctl cmd <name> "<console command>" type a command into the tmux console and print its output
osctl logs <name> [-f]
osctl config show <name> [--section S…] | explain <name> <Section> <Key> | diff <name>
osctl visitors [<name>…] [--since DATE|7d] [--csv|--json]
osctl backup create [<name>…] [--oar] [--db] [--iar USER]… [--scheduled] ; osctl backup list|prune|iar-password
osctl schedule restart <name> "OnCalendar spec" [--warn 10m] ; schedule backup <name> "spec" [--iar U] ; remove|list
osctl restart|stop [<name>…] --warn 10m   in-world countdown first; restart --scheduled is what the timer runs
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

`[Supervisor] Mode` in osctl.ini chooses how: `systemd` (system units, from the package), `systemd-user` (your
own units: `osctl units install --user` writes them with `--root`, so a development root can be tried under real
systemd), or `direct` (no systemd: `osctl start` runs `osctl run` in the background with `setsid`; no restart after
a crash, nothing at boot; for development and the tests). `osctl units install|show` writes or prints the unit;
it carries the [Restart] and [Timeouts] values, so it is installed again after they change.

`osctl run <name>`:
1. Regenerates the instance's configuration (below) and checks it (regions exist, ports free).
2. Starts a tmux server just for this instance (`tmux -S /run/osctl/<name>.sock`, `remain-on-exit on`; under a
   long development root the socket goes to `/tmp/osctl-<hash>/`, as socket paths must fit in 107 bytes) running
   `dotnet OpenSim.dll -inimaster=none -inifile=generated/OpenSim.ini -inidirectory=generated/config -logconfig=generated/log4net.config`
   (`none` skips OpenSim's own master file; the empty `config/` stops OpenSim merging any `bin/config/*.ini`;
   tried by hand in phase 2: a SQLite standalone with two regions started unattended and shut down with exit code 0)
   in the build's `current/bin`. The tmux server stays in the unit's cgroup, so systemd sees and kills everything.
   The pane first runs `sh -c 'read -r _ && exec "$@"'`: osctl attaches `pipe-pane` (the console transcript,
   `/var/log/osctl/<name>/console.log`, the previous one kept as `.1`) and then presses Enter, so the transcript
   has OpenSim from its first line and the pane's PID is OpenSim's. OpenSim runs from the release directory itself,
   not `current/`, and the release is recorded in `/run/osctl/<name>.state` so pruning keeps it.
3. Waits for readiness: every region's `INITIALIZATION COMPLETE FOR <region> - LOGINS ENABLED` in the console
   transcript (RegionReady prints it only to the console, once scripts have compiled; `/simstatus/` answers
   earlier). If [Timeouts] StartSeconds pass first, it logs a warning and carries on as ready. Then `READY=1`.
4. Waits for the OpenSim process to exit and exits with its exit code. A `shutdown` typed on the console exits 0
   and the unit stays stopped; a crash exits non-zero and systemd restarts it.

`osctl run-stop` sends `shutdown` (RemoteAdmin `admin_shutdown`, falling back to tmux `send-keys`) and waits up
to [Timeouts] StopSeconds, then kills OpenSim (SIGTERM, then SIGKILL); systemd kills after `TimeoutStopSec`. The
runner does the same on SIGTERM. `--force` on `osctl stop` kills straight away.

"Broken": with `Restart=on-failure`, a unit that stays failed after restarts is one systemd gave up on ("Start
request repeated too quickly"; systemd 259 leaves `Result` at the last exit code, not `start-limit-hit`).
`osctl status` shows it as broken; `osctl start` resets it (`reset-failed`) and starts it again.

Tried on this machine under systemd user units (phase 3): start (ready in ~5 s), crash (SIGKILL) restarted after
10 s, four quick crashes gave broken, `osctl start` recovered it, `osctl stop` through RemoteAdmin left it
inactive with success, `shutdown` typed at the console left it stopped (exit 0, no restart), enable/disable.

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

Done in phase 5 (`src/Osctl/Operations/`, `Cli/OperationsCommands.cs`). Decided (2026-11-24): a scheduled backup
holds OARs + databases (+ IARs of users named in `instance.ini`); retention 7 daily + 4 weekly; the restart warning
counts down (warning time, 5 min, 1 min); in direct mode `osctl schedule` prints crontab lines.

- **Layout:** one directory per backup, `<backups root>/<yyyyMMdd-HHmmss>` (local time), `.partial` while being
  made, with `backup.ini` listing each file and its error, if any. The root is the instance's `backups/`, or
  `[Backups] Dir/<instance>`.
- **OAR**, one per region: RemoteAdmin `admin_save_oar` (`region_name`, `filename`). It answers after at most 5 s
  whether or not the save is done (RemoteAdminPlugin waits on a monitor for 5 s), so osctl waits for
  `[ARCHIVER]: Finished writing out OAR for <region>` in OpenSim.log (logged on success and failure alike), watches
  for RADMIN's "Saving of OAR file … failed", then reads the file through gzip + tar to its end to check it.
- **IAR:** `save iar First Last / "password" "file"` sent with RemoteAdmin `admin_console_command`. Unlike a command
  typed into the tmux console, that is not echoed, logged or kept in the console history, so the password stays
  out of every log. Done when `[INVENTORY ARCHIVER]: Saved archive with N items for First Last`; failures: "Failed
  to find user info", "Password for user … incorrect", "Archive save for … failed". Passwords: the instance's
  `secrets.ini` `[IAR] First Last = …` (`osctl backup iar-password`), else the estate owner's on a standalone.
- **Databases:** MariaDB: `mariadb-dump` of the instance's and the grid's estate database (credentials in a 0600
  temporary file); SQLite: `sqlite3 <file> ".backup …"` of each SQLite file in `data/` (online, consistent while
  OpenSim writes). Both gzipped. The `sqlite3` program, not Microsoft.Data.Sqlite: its native library does not fit
  the single-file package. The package recommends `sqlite3`; `osctl doctor` checks it.
- **A grid (Robust):** its databases, then its FSAssets files as `fsassets/` (decided 2026-11-26: hard-linked
  snapshots). FSAssets names a file after its asset's SHA-256 (`data/ab/cd/ef/0123/<hash>.gz`) and never changes or
  removes one, so a file the previous backup has (same path and size) is hard-linked (`link(2)`; copied if that
  fails), and new files are copied and checked against their hash: each backup is whole, only new assets take space,
  pruning stays "delete the directory". Order: the database (the index) first, then the spool's `<hash>.asset`
  files (stored, not yet written out), then `data/`. A file leaves the spool only once it is whole in `data/`, so
  every indexed asset is in one of the two. A new file that fails its hash and was written since a minute before the
  start is still being written and is left out; an older one is copied as it is, with a warning. Part of `--db`
  (the files and their index belong together); `backup.ini [Assets]` keeps the counts. Tried on the dev grid: 423
  files, 11.5 MiB, in about a second; the next backup linked them all (48 KB on disk); every hash in the dumped
  index was in the snapshot.
- **Definition:** each backup holds `definition/` (instance.ini, regions/, overrides/, start-up and shutdown commands,
  the config sets it uses; a grid also its simulators' set), decided 2026-11-26, so a lost instance can be made
  again. Not secrets.ini, as backups are often copied off the machine.
- **Restore** (decided 2026-11-26): `osctl backup restore <instance> [<backup name or dir>]`, only into the instance
  the backup came from (no `--to`: a simulator's database is keyed by region UUID). Parts: `--db`, `--assets`,
  `--definition` (instance stopped), `--oar <region>`, `--iar <user>` (running: RemoteAdmin `admin_load_oar`, which
  answers once loaded, then `[ARCHIVER]: Successfully loaded archive`; `load iar` through `admin_console_command`,
  then `[INVENTORY ARCHIVER]: Loaded N items … for First Last`). None given: databases and assets, and the
  definition when the instance is gone. Always a backup of the instance as it is first (`--no-backup`), and a plan
  shown until `--yes`. MariaDB: drop, create, feed the dump to `mariadb`. SQLite: the file replaced and its
  `-wal`/`-shm`/`-journal` removed. Assets: `data/` replaced, the spool's files put back in `tmp/`. A simulator's
  estate database is left alone when other instances share it (a grid's simulators) unless `--with-estates`.
  A definition restore checks the build exists and the ports are free, writes the config sets only where missing, and
  makes a new secrets.ini (new RemoteAdmin password). Tried on the dev root: an OAR into the running Sim1 (6 s, with
  the backup first), the whole TestGrid (two MariaDB loads and 423 asset files in 2 s; Robust and Sim1 started and
  the owner check passed), and Test1 deleted and made again from a copied backup (same definition and region UUIDs,
  started).
- **Not running:** OARs and IARs are skipped (OpenSim writes them); the databases hold everything.
- **Pruning:** of the complete backups, keep the newest, and the newest of each of the last `KeepDaily` days and
  `KeepWeekly` ISO weeks that have one; an incomplete backup goes once a newer complete one exists.
- **Warnings:** `osctl restart|stop --warn 10m` broadcasts (`admin_broadcast`) at the warning time and at 5 and
  1 minutes before, to running instances only; instances with different warning times count down side by side.
- **Timers:** `instance.ini [Schedule] Restart/Backup` (systemd calendar expressions, checked with
  `systemd-analyze calendar`) become `osctl-restart@<name>.timer` / `osctl-backup@<name>.timer`, which start the
  template services `osctl-restart@.service` (`osctl restart --scheduled %i`: running instances only, warned by
  `[Schedule] WarnMinutes`) and `osctl-backup@.service` (`osctl backup create --scheduled %i`: full backup, then
  prune). Both Type=oneshot, no start time limit, as `opensim`. The services come with the package (and
  `units install`); the timers are per instance. Writing a timer to /etc/systemd/system and `daemon-reload` need
  root, so in system mode `osctl schedule` saves instance.ini and, if it cannot write there, says to run
  `sudo osctl units install`, which writes every instance's timers and removes those of schedules that are gone.
  `systemd-user` needs no root. `direct`: crontab lines (`schedule list --cron`) for the simple calendar forms.
- **Visitors:** MIOS's lines ("Found presence", "authenticated and authorized incoming root agent") are gone from
  OpenSim. Today: `[SCENE PRESENCE]: Complete movement of <name> into <region> <pos>` (INFO) for every arrival
  (login, teleport, crossing), `[SCENE]: Incoming client <name> in region <region> via regular|HG login` (DEBUG)
  just before it for logins, `[LLOGIN SERVICE]: Login request for <name> at … IP <ip>,` for the address. Read
  from OpenSim.log and its rotated copies (OpenSim.log.*, .gz too).

Tried against OpenSim (the sas-dev SQLite standalone, direct mode): two OARs, the owner's IAR and eight SQLite files
in 12 s; a wrong IAR password reported; no password in any log; `restart --warn 70s` broadcast in-world at 70 s and
1 min, then restarted; `visitors` on the SpatialAudioServer test standalone's log (118 arrivals). Not yet tried:
the timers under system systemd (the opensim user restarting through polkit) and MariaDB dumps.

## Robust and local grids (phase 6)

Decided (2026-11-25): a grid of your own on this machine, Hypergrid, plain HTTP; groups and offline IM on; voice
stays in each simulator (os-webrtc-sas as today); simulators on other machines and moving a standalone into a grid
are later.

**A grid is a Robust instance.** `osctl grid add <grid> --build B --host grid.example.org [--name "My Grid"]
[--nick mygrid] [--public-port 8002] [--private-port 8003]` creates:

- the instance `<grid>` of kind `robust` (`[Instance] Kind = robust`), on the shipped config set `robust-hg`
  (`Robust.HG.ini.example`, with `[Set Robust.HG.ini]` edits that switch on Groups V2, HG groups, offline IM and
  FSAssets (`FSAssetConnector`, `HGFSAssetService`; decided 2026-11-26: assets as files, the database holds only
  their index), and set `[Hypergrid] HomeURI`/`GatekeeperURI`), MariaDB only (Robust's SQLite support is incomplete), database
  `opensim_<grid>`. Its `[Grid]` section holds `Host`, `PublicPort`, `PrivatePort`, `Name`, `Nick`,
  `DefaultRegion` and `AssetsDir` (FSAssets' files; default the instance's `data/fsassets`); `osctl grid set`
  changes them, `osctl grid list|show` shows them.
- the config set `<grid>` (`/etc/osctl/configsets/<grid>.ini`, a plain file you can read and change) for the
  simulators: `Mode = grid`, `Grid = <grid>`, `Robust = <grid>`, OpenSim's own `OpenSim.ini.example` and
  `GridCommon.ini.example` with `Include-Architecture = config-include/GridHypergrid.ini`, and Groups V2 / offline
  IM switched on (`[Set OpenSim.ini]`). Simulators join with `osctl instance add Sim1 --configset <grid>`.

Ports: `[Ports] RobustPortMin`/`RobustPortMax` (8002–8099); `grid add` takes the first free pair. 8002 (public:
login, grid info, map, Hypergrid gatekeeper and user agent) must be reachable from outside; 8003 (private: the
simulators' services) must not be: Robust listens on every address, so a firewall or the container's port
forwarding keeps it in. The simulators' own ports (region UDP and HTTP) must be public too; Caddy cannot help with
UDP, so the README describes port forwarding (incus proxy devices) rather than a reverse proxy. HTTPS for 8002
through a proxy is possible later; most grids use plain HTTP.

What osctl sets itself for a Robust instance ("osctl" layer, as for simulators): `[Const] BaseHostname`,
`PublicPort`, `PrivatePort`; `[Startup] PIDFile`, `ConsoleHistoryFile`, `RegistryLocation`; `[DatabaseService]`; `[GridInfoService] gridname`, `gridnick`; `[MapImageService]
TilesStoragePath`; `[GridService] MapTileDirectory`; `[AssetService] BaseDirectory`/`SpoolDirectory`
(`<AssetsDir>/data`, `<AssetsDir>/tmp`: the spool must be on the same file system); `[GridService] Region_<uuid> = "DefaultRegion, DefaultHGRegion, FallbackRegion"` for the
grid's `DefaultRegion` (without one nobody can log in for the first time; the first region added to the grid
becomes it). Its log4net file comes from `Robust.exe.config`. For a simulator on a local grid: `[Const]
BaseHostname` (the grid's host), `PublicPort` and `PrivatePort` (the grid's), `PrivURL = http://127.0.0.1`.

Running: the same unit, tmux console, `osctl run`, logs, status and backups (database dumps and the FSAssets files; no OAR). Robust runs
`[Run] RobustCommand` (`dotnet Robust.dll`) with `-inifile` and `-logconfig`. It is ready when both its ports
answer; it is stopped by typing `shutdown` into its console (it has no RemoteAdmin). A simulator on a local grid:

- `osctl start Sim1` starts the grid first if it is not running; `osctl start` with no names starts grids first
  and `stop` stops them last; `osctl enable Sim1` enables its grid too. At boot both units start together and the
  simulator waits (below), so no systemd drop-in is needed.
- `osctl run` waits for the grid to be ready (up to [Timeouts] StartSeconds; it gives up after 20 s if the
  grid's runner never appears), then checks (through the private port's `/accounts`) that the estate
  owner has an account, and stops with the command that makes one if not (OpenSim would otherwise ask on its
  console and never start unattended).
- `osctl stop <grid>` warns while simulators on it are running; `instance delete <grid>` refuses while instances
  use it, and removes its config set.

Accounts: `osctl user create <instance> "First Last" [--password P] [--email E]` (password generated and shown
once when not given) and `osctl user password <instance> "First Last" [--password P]`, typed into the console of a
running grid or standalone (`create user …`, `reset user password …`).

## Stats and web UI (phase 7)

`osctl-web.service` (ASP.NET Core, localhost by default): polls each running instance's ManagedStats JSON every
minute into SQLite (MIOS's `Stats.ini` definitions are the starting set of series, with retention like MIOS's
RRAs: 1 min for a day, 5 min for a week, 1 h for 8 weeks, 1 day for 3 years), and serves a page with status and
graphs (static HTML/JS, no build step). Read-only to begin with.

## Packaging

`scripts/build-packages.sh [x64|arm64]` (nfpm, as in SpatialAudioServer) makes `dist/osctl_<version>_<arch>.deb`:

- `/usr/bin/osctl`: a self-contained single-file .NET publish (runtime inside, invariant globalisation), so no
  .NET 10 is needed; OpenSim's .NET 8 SDK is only recommended, because not every Ubuntu release's archive has it
  (26.04's comes from a PPA here). `osctl doctor` says what is missing.
- `/usr/lib/systemd/system/osctl@.service`, written at build time by `osctl units show --defaults --program
  /usr/bin/osctl`; `sudo osctl units install` writes `/etc/systemd/system/osctl@.service` (which wins) after
  [Restart] or [Timeouts] change.
- `/usr/lib/tmpfiles.d/osctl.conf`: `/run/osctl` 2770 opensim:opensim.
- `/usr/share/polkit-1/rules.d/50-osctl.rules`: members of the opensim group may start, stop, restart, kill and
  reset-failed `osctl@<name>.service` without sudo. Enabling at boot is not covered (polkit does not say which unit
  `manage-unit-files` is for), so `sudo osctl enable <name>`.
- Depends: tmux (>= 3.3), git. Recommends: systemd, polkitd, dotnet-sdk-8.0, libgdiplus, mariadb-client.

The install script creates the system account `opensim` (group `opensim`, home `/var/lib/osctl`, no login), runs
`osctl init`, and makes `/etc/osctl`, `/var/lib/osctl`, `/var/cache/osctl` and `/var/log/osctl` group-shared:
owner opensim:opensim, directories 2770 (setgid, so new files stay in the group), files group-writable;
`secrets.ini` root:opensim 0640. osctl itself runs with umask 007, so what one member creates the others and the
instances (running as opensim) can change, and nobody else can read. People who manage instances join the group
(`sudo usermod -aG opensim <user>`). An upgrade fixes only the top directories (builds are large) and leaves running
instances alone; removal stops them; purge removes /etc/osctl, /var/lib/osctl, /var/cache/osctl and /var/log/osctl
(never the MariaDB databases).

CI and releases as in SpatialAudioServer: `ci.yml` (tests, on pull requests), `drafts.yml` (by hand on develop:
the rolling pre-release "develop (draft)"), `release.yml` (a merged pull request into main tags the next version,
builds and tests the packages into a draft release, published with the label `publish`; afterwards main is merged
back into develop and CHANGELOG.md rolls over), all building with `packages.yml`, which also installs and tries the
x64 package in Ubuntu 24.04, Ubuntu 26.04 and Debian 13 containers (`scripts/test-packages.sh`). The apt repository
is this repository's GitHub Pages site, https://jakdaniels.github.io/opensimtools/ (`pages.yml`,
`scripts/build-repos.sh`, as in SpatialAudioServer but .deb only): rebuilt from the latest two published releases
whenever one is published, signed with the key in `packaging/signing-key.asc` (its own, made 2026-11-23, fingerprint
866F6A96EA961E4B5CB8C70C243C8C3F505D1D3B, expires 2031-11-22; the private half is the secret
`OPENSIMTOOLS_SIGNING_KEY`). `release.yml` starts it when it publishes a release.

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
  `release.yml` on merge to main, `pages.yml` building the signed apt repository on this repository's Pages.

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
5. **Operations** (done): visitors, backups, scheduled restarts with warnings; `region get`.
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

Settled: the apt repository is this repository's own GitHub Pages site (see "Packaging"), not SpatialAudioServer's.
