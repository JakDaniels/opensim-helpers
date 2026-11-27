# opensimtools: osctl

osctl ("MIOS 2.0") manages several [OpenSimulator](http://opensimulator.org) instances on one Debian or Ubuntu
machine:

- **Builds:** it builds OpenSim from git as named builds (a branch or tag, plus addon modules and patches). Each
  update goes into a new release directory, so running instances are never rebuilt under them, and you can roll back.
- **Instances and regions:** it creates instances and regions, and allocates ports, UUIDs and grid locations,
  checking for clashes.
- **Configuration:** it generates each instance's OpenSim configuration from layers of `.ini` files, so you never edit
  OpenSim's own files.
- **Running:** each instance runs under systemd, with its OpenSim console in tmux. It is restarted after a crash,
  and left stopped as "broken" after repeated quick crashes.
- **Databases:** MariaDB/MySQL, or SQLite for small standalones.

It replaces [MIOS](https://github.com/JakDaniels/MIOS), the old PHP scripts. [PLAN.md](PLAN.md) is the full design,
with the reasons for each decision.

**Status:** version 0.1. Builds, instances, regions, configuration, databases and running under systemd work.
A grid of your own (Robust, with Hypergrid) is new; a stats page is still to come.

## Contents

- [Requirements](#requirements)
- [Installing](#installing)
- [After installing](#after-installing)
- [Your first instance](#your-first-instance)
- [How configuration works](#how-configuration-works)
- [osctl.ini settings](#osctlini-settings)
- [Typical setups](#typical-setups)
- [A grid of your own](#a-grid-of-your-own)
- [Running instances](#running-instances)
- [Visitors, backups and scheduled restarts](#visitors-backups-and-scheduled-restarts)
- [Where things are](#where-things-are)
- [Upgrading and removing](#upgrading-and-removing)
- [Command reference](#command-reference)

## Requirements

- **Operating system:** Ubuntu 24.04 or later, or Debian 13 or later, on x64 (amd64) or arm64.
- **Installed with the package:** tmux 3.3 or later and git.
- **For OpenSim itself:** the .NET 8 SDK, which OpenSim needs to build and to run, and libgdiplus (map tiles and
  textures). The package recommends both, but not every distribution release has .NET 8 in its own archive; see
  [The .NET 8 SDK](#the-net-8-sdk).
- **A database:** MariaDB (or MySQL) for real instances. SQLite needs nothing extra, except `sqlite3` for
  backups (the package recommends it).
- **osctl itself:** nothing more. osctl carries its own .NET runtime.

## Installing

### From the apt repository (recommended)

The repository is https://jakdaniels.github.io/opensimtools/. Adding it means `apt upgrade` brings new osctl
releases with your other updates.

1. Add the repository's signing key:

   ```
   curl -fsSL https://jakdaniels.github.io/opensimtools/key.asc | sudo gpg --dearmor -o /usr/share/keyrings/opensimtools.gpg
   ```

   The key's fingerprint is `866F6A96EA961E4B5CB8C70C243C8C3F505D1D3B`. To check it:
   `gpg --show-keys /usr/share/keyrings/opensimtools.gpg`.

2. Add the repository:

   ```
   echo "deb [signed-by=/usr/share/keyrings/opensimtools.gpg] https://jakdaniels.github.io/opensimtools/deb stable main" |
     sudo tee /etc/apt/sources.list.d/opensimtools.list
   ```

3. Install osctl:

   ```
   sudo apt update
   sudo apt install osctl
   ```

### From a downloaded package

Each release on the [Releases page](https://github.com/JakDaniels/opensimtools/releases) has the .deb for x64
(`amd64`) and arm64, and a `SHA256SUMS` file. Download the one you need, check it, and install it with apt (not
`dpkg -i`, so that its dependencies come too):

```
sha256sum -c SHA256SUMS --ignore-missing
sudo apt install ./osctl_0.1.0_amd64.deb
```

A package installed this way is not upgraded by `apt upgrade`; use the apt repository for that.

### What the package sets up

- **The program:** `/usr/bin/osctl`.
- **The account:** a system user and group `opensim`. Instances run as this user, and members of the group manage
  them.
- **Settings:** `/etc/osctl/`, with the default settings, config sets and region defaults.
- **Data:** `/var/lib/osctl/` (builds, instances), `/var/cache/osctl/` (asset caches) and `/var/log/osctl/`
  (logs).
- **Group-shared directories:** all of these belong to the `opensim` group, and only its members (and root) can
  read them.
- **systemd:** the unit `osctl@.service` (one copy per instance, as `osctl@<instance>.service`), the services
  `osctl-restart@.service` and `osctl-backup@.service` that scheduled restarts and backups start, and a polkit rule
  that lets members of the `opensim` group start, stop and restart instances without sudo.

## After installing

### Join the opensim group

Everyone who manages instances joins the group, then logs out and in again (or runs `newgrp opensim` in the
current shell):

```
sudo usermod -aG opensim $USER
```

Members of the group can run every osctl command without sudo, except:

- `sudo osctl db init`, which sets up MariaDB as root;
- `sudo osctl enable|disable <instance>`, which decides what starts at boot;
- `sudo osctl units install`, which rewrites the systemd unit.

### Check the machine

```
osctl doctor
```

This checks git, tmux, the .NET SDK, the .NET 8 runtime, libgdiplus, the MariaDB client, systemd, your group
membership, osctl's settings and, once it is set up, the database connection. Each problem comes with what to do
about it. Failures are things osctl cannot work without; warnings are things only some setups need.

### The .NET 8 SDK

OpenSim targets .NET 8. Both the SDK (to build OpenSim) and its runtime (to run it) are needed; a newer .NET alone
does not do.

- **Ubuntu 24.04:** `sudo apt install dotnet-sdk-8.0`.
- **Ubuntu releases without it in their archive** (such as 26.04): add the .NET backports PPA, then install the SDK:

  ```
  sudo add-apt-repository ppa:dotnet/backports
  sudo apt install dotnet-sdk-8.0
  ```

- **Debian:** use Microsoft's package repository ([instructions](https://learn.microsoft.com/dotnet/core/install/linux-debian)),
  then `sudo apt install dotnet-sdk-8.0`.

Also install libgdiplus if `osctl doctor` asks for it: `sudo apt install libgdiplus`.

### MariaDB (skip this for SQLite only)

1. Install the server:

   ```
   sudo apt install mariadb-server
   ```

2. OpenSim stores assets in the database and needs large packets. Add this to
   `/etc/mysql/mariadb.conf.d/50-server.cnf`, under `[mysqld]`, then restart MariaDB
   (`sudo systemctl restart mariadb`):

   ```
   max_allowed_packet = 128M
   ```

3. Create osctl's database users:

   ```
   sudo osctl db init
   ```

   This runs as root and connects as MariaDB's root account over its socket, which Ubuntu and Debian allow. It
   creates two users:

   - **`osctl_admin`:** creates and drops the instances' databases;
   - **`opensim`:** what OpenSim connects as.

   Both get rights only on databases whose names start with `opensim_` (`[Database] DatabasePrefix`). It makes
   their passwords and puts them in `/etc/osctl/secrets.ini`.

   **Check the prefix first:** if you already have databases starting with `opensim_` that osctl should not touch,
   change `DatabasePrefix` in `/etc/osctl/osctl.ini` before running it.

4. Run `osctl doctor` again: its "database" line should say OK.

For a database server on another machine, set `[Database] Host` and `Port` in `/etc/osctl/osctl.ini`, create the
two users there by hand (all rights on `` `opensim\_%`.* ``), and put their passwords in `/etc/osctl/secrets.ini`.

## Your first instance

This makes a standalone (its own grid, no other services needed) with one region.

1. **Define a build:** which OpenSim source to build.

   ```
   osctl build add master --repo git://opensimulator.org/git/opensim --ref master
   ```

2. **Build it.** The first time clones the source and takes a few minutes; later updates only build when
   something changed.

   ```
   osctl build update master
   ```

3. **Create the instance.** This one uses the shipped `standalone` config set and MariaDB; add `--db sqlite` to skip
   MariaDB.

   ```
   osctl instance add Test1 --estate-owner "Test User"
   ```

   On a standalone, OpenSim creates the estate owner's avatar the first time it starts. osctl makes a password for
   it and shows it once. Note it down: you log in as "Test User" with it. (`--estate-owner-password` sets your own
   instead.)

4. **Add a region.** The location is in grid cells (256 m each); the port is allocated for you.

   ```
   osctl region add Welcome --instance Test1 --location 1000,1000
   ```

5. **Start the instance.** This waits until every region says it is ready for logins.

   ```
   osctl start Test1
   osctl status
   ```

6. **Use it:**

   - `osctl console Test1` attaches to the OpenSim console. Ctrl-a then d leaves it running.
   - `osctl cmd Test1 show regions` runs one console command and prints its output.
   - `osctl logs Test1 -f` follows the log.

7. **Log in** with a viewer, using the login URI `http://<this machine>:9000/`. 9000 is the instance's port: see
   `osctl instance show Test1`.

8. **Start it at boot** (optional): `sudo osctl enable Test1`.

## How configuration works

You never edit OpenSim's own `.ini` files. Every time an instance starts, osctl builds its `OpenSim.ini` from these
layers, in order; a later layer wins for any key it sets:

1. **The config set:** the base files from the build's `bin/`, or downloaded (such as OSGrid's), loaded as OpenSim
   itself loads them, includes and all.
2. **Global overrides:** `/etc/osctl/overrides/*.ini`, in alphabetical order, for every instance.
3. **Instance overrides:** `/etc/osctl/instances/<instance>/overrides/*.ini`, in alphabetical order.
4. **What osctl sets itself:** ports, file paths (logs, caches, script engine, databases), the database
   connection, the estate defaults and RemoteAdmin. These can't be overridden, because osctl relies on them.

An override file holds only the settings you want to change, under their sections:

```
; /etc/osctl/overrides/10-physics.ini
[Startup]
    physics = BulletSim

[Permissions]
    allow_grid_gods = true
```

Or let osctl write it: `osctl config set <Section> Key=Value … --instance Test1` (or `--global` for every instance)
keeps the settings in `50-osctl.ini` in that overrides directory; `Key=` with nothing after it removes one. Files
named after `50-osctl.ini` (such as `90-mine.ini`) are read later and win; osctl warns when one sets the same key.

```
osctl config set Startup physics=ubODE MaxPoolThreads=200 --instance Test1
osctl config set Permissions allow_grid_gods=true --global
```

To see the result, and where each value comes from:

```
osctl config show Test1 --section Startup Network   # the configuration it would start with now
osctl config explain Test1 Startup physics          # every file that set it, in order, and the final value
osctl config diff Test1                             # what changed since its last start
```

Passwords are shown as `********` unless you add `--show-secrets`. Changes take effect at the instance's next
start (`osctl restart Test1`).

**The ini rules are OpenSim's (Nini's), not every ini reader's:**

- `;` starts a comment; `#` does not, and a line starting with `#` is an error.
- Within one file, a repeated key keeps its first value, and a repeated `[Section]` replaces the earlier one.
- Quote a value that has `;` in it or begins or ends with spaces: `ConnectionString = "a;b"`.

### Region settings

Each region's settings are built in this order, a later one winning:

1. **Global region defaults:** `[Region Defaults]` in `/etc/osctl/region-defaults.ini`, for every region. The
   shipped values are MIOS's: `MaxPrims = 15000`, `MaxAgents = 100`, `ExternalHostName = SYSTEMIP` and a few more.
   `osctl region defaults set MaxPrims=20000 --global` changes one.
2. **Instance region defaults:** `[Region Defaults]` in `/etc/osctl/instances/<instance>/region-defaults.ini`, for
   that instance's regions: `osctl region defaults set MaxAgents=40 --instance Test1`.
3. Other `.ini` files in the instance's `regions/` directory, under a `[Region Name]` section (for example, an
   addon module's per-region settings).
4. The region's own settings: `osctl region set Welcome MaxAgents=40 RegionType=Estate`.

In each of these commands `Key=` with nothing after it removes a setting. `osctl region defaults get --global` or
`--instance Test1` shows the defaults (for an instance: the global ones with its own over them; `--own` for its
own only), with the same `--origin` and `--json` as `region get` below.

`ExternalHostName = SYSTEMIP` makes OpenSim use this machine's IP address. For regions people reach over the
internet, set it to your public DNS name, either for all regions in the region defaults or for one region with
`osctl region set Welcome ExternalHostName=sim.example.org`.

`osctl region get Welcome` shows a region's settings after these steps are applied, as `Key = Value`
lines. Name settings to see only those, e.g. `osctl region get Welcome MaxAgents 'Max*'`; names ignore case, and
`*` and `?` are wildcards. A name that matches nothing gives exit code 1. `--own` shows only the region's own settings
(step 4), `--origin` adds the file and line each value comes from, and `--json` prints JSON. Passwords are shown as
`********` unless `--show-secrets`.

### Config sets

A config set is the base configuration an instance starts from. Three are shipped, in `/etc/osctl/configsets/`:

| Config set | What it is |
|---|---|
| `standalone` | OpenSim's own standalone setup, no Hypergrid. All services run in the instance. |
| `standalone-hg` | The same with Hypergrid, so avatars can visit other grids. |
| `osgrid` | Regions on [OSGrid](https://www.osgrid.org), with the ini files OSGrid publishes (downloaded). |

Choose one with `osctl instance add … --configset osgrid`. The default is `[Defaults] ConfigSet` in `osctl.ini`.
`osctl configset list|show <set>` shows them, and `osctl configset fetch [set]` downloads a grid's files again,
for when the grid changes them.

You can write your own: copy a shipped one under a new name and change it. A config set has three parts:

```
[ConfigSet]
    Description = My grid
    Mode = grid                    ; standalone (own services) or grid (a grid's services)
    Grid = mygrid                  ; grid mode: instances in the same grid share an estate database and asset cache
    Load = OpenSimDefaults.ini, OpenSim.ini

[Files]                            ; where a file OpenSim reads from bin/ really comes from
    OpenSim.ini = https://example.org/OpenSim.ini.txt
    config-include/GridCommon.ini = mygrid/GridCommon.ini    ; a file under /etc/osctl/configsets/
    config-include/FlotsamCache.ini = bin:config-include/FlotsamCache.ini.example

[Set OpenSim.ini]                  ; values changed in a file before its includes are followed
    Architecture|Include-Architecture = config-include/GridHypergrid.ini
```

## osctl.ini settings

`/etc/osctl/osctl.ini` holds osctl's own settings. The installed file lists every setting with its default; change
what you need. An unknown setting is reported by `osctl doctor`, so typos show up.

| Section | Setting | Default | What it does |
|---|---|---|---|
| `[Ports]` | `RegionPortMin`, `RegionPortMax` | 9000, 9099 | The range region ports are allocated from. An instance's HTTP port is its lowest region port. |
| | `SslPortOffset` | 500 | Added to the HTTP port for HTTPS, when an instance turns it on. |
| `[Timeouts]` | `StartSeconds` | 180 | How long regions may take to say they are ready. After that, osctl logs a warning and treats the instance as ready anyway. |
| | `StopSeconds` | 90 | How long a shutdown may take before OpenSim is killed. |
| `[Restart]` | `MaxRestarts`, `IntervalSeconds` | 3, 300 | More crashes than this within this many seconds, and the instance is left stopped as "broken". |
| | `DelaySeconds` | 10 | How long systemd waits before restarting a crashed instance. |
| `[Tmux]` | `Prefix` | `C-a` | The console's tmux prefix key (Ctrl-a, as in MIOS). |
| | `HistoryLimit` | 10000 | How many lines of console scrollback to keep. |
| `[Database]` | `Host`, `Port` | localhost, 3306 | The MariaDB/MySQL server. |
| | `AdminUser`, `User` | osctl_admin, opensim | The users that create databases, and that OpenSim connects as. |
| | `DatabasePrefix` | `opensim_` | Instance databases are `<prefix><instance>`; estate databases are `<prefix>estate_<grid>`. |
| `[Defaults]` | `ConfigSet` | standalone | The config set for `instance add` without `--configset`. |
| | `Build` | (none) | The build for `instance add` without `--build`. With only one build, that one is used. |
| `[Account]` | `User`, `Group` | opensim, opensim | The account instances run as, and the group whose members manage them. |
| `[Supervisor]` | `Mode` | systemd | How instances run: see [Running instances](#running-instances). |
| `[Run]` | `Command` | `dotnet OpenSim.dll` | What an instance runs in its build's `bin/`; osctl adds its own switches. |
| `[Backups]` | `Dir` | (none) | Where backups go: each instance's own `backups/` directory, or `<Dir>/<instance>/` if set. |
| | `KeepDaily`, `KeepWeekly` | 7, 4 | Pruning keeps the newest backup of each of the last this many days and weeks. |
| | `SaveTimeoutMinutes` | 60 | How long saving one OAR or IAR may take. |
| `[Schedule]` | `WarnMinutes` | 10 | How long before a scheduled restart people in the regions are warned. |

**Reinstall the unit after some changes:** the systemd unit holds the `[Restart]` and `[Timeouts]` values, so after
changing them run `sudo osctl units install`.

**Passwords** are not in `osctl.ini`:

- the database passwords are in `/etc/osctl/secrets.ini` (root-owned, readable by the group);
- each instance's estate owner and RemoteAdmin passwords are in that instance's own `secrets.ini`.

## Typical setups

### A quick test standalone (SQLite)

No database server, no other services. Good for trying things out; MariaDB is better for anything you keep.

```
osctl instance add Play --db sqlite --estate-owner "Test User"
osctl region add Sandbox --instance Play --location 1000,1000
osctl start Play
```

### A standalone with Hypergrid (MariaDB)

```
osctl instance add Home --configset standalone-hg --estate "Home Estate" --estate-owner "Jo Bloggs"
osctl region add "Home Island" --instance Home --location 1000,1000 --size 512
osctl region add Harbour --instance Home --location 1002,1000
osctl start Home
```

Each standalone is a grid of its own, so several standalones can use the same locations and region names.

### Regions on OSGrid

```
osctl instance add OSG1 --configset osgrid --estate "My OSGrid Estate" --estate-owner "Jo Bloggs"
osctl region add "My Place" --instance OSG1 --location 12000,12000
osctl start OSG1
```

- **The estate owner:** an existing OSGrid account. No password is made, because the account already exists.
- **The location:** osctl checks locations only against your own regions. Find a free one on OSGrid's map first.
- **Ports:** OSGrid's servers and visitors must reach the instance's ports from the internet: the HTTP port (TCP)
  and every region port (UDP). Open them in your firewall and router.
- **Your address:** set `ExternalHostName` to your public DNS name or IP address (see
  [Region settings](#region-settings)).

### A custom build: OpenSim with an addon module and patches

A build can add addon modules (copied into OpenSim's `addon-modules/`), and patches from an addon or from a
directory of your own:

```
osctl build add sas-dev --repo git://opensimulator.org/git/opensim --ref master
osctl build addon add sas-dev os-webrtc-sas --repo https://github.com/JakDaniels/os-webrtc-sas.git --patches core-patches
osctl build update sas-dev
osctl instance add Voice --build sas-dev --estate-owner "Test User"
```

The patches are applied with `git apply`, the addon's first, then the build's own (`--patches DIR` on `build add`).
A patch that no longer applies stops the update, and the current release stays as it was.

Builds and releases:

- `osctl build update` builds a new release only when a commit, a patch or a build command changed; `--force`
  builds anyway.
- Instances use a new release from their next start. `osctl build update sas-dev --restart` restarts the running
  ones straight away.
- The last 3 releases are kept (`--keep N` on `build add`). `osctl build rollback sas-dev` goes back one.
- `osctl build show sas-dev` lists the releases.

## A grid of your own

A grid is one Robust instance (the grid's services: accounts, inventory, assets, login, the map, groups, offline
messages and the Hypergrid gatekeeper) and any number of simulator instances that use it. osctl sets up both on this
machine, with Hypergrid, so your avatars can visit other grids and visitors can come in. It needs MariaDB.

```
osctl grid add MyGrid --host grid.example.org --name "My Grid"
osctl start MyGrid
osctl user create MyGrid "Jo Bloggs"
osctl instance add Sim1 --configset MyGrid --estate "Mainland" --estate-owner "Jo Bloggs"
osctl region add Welcome --instance Sim1 --location 1000,1000
osctl start Sim1
```

- **`grid add`** creates the instance `MyGrid` (Robust, from OpenSim's `Robust.HG.ini.example`, with groups,
  offline messages and FSAssets switched on), its databases, and the config set `MyGrid`, which its simulators use. It takes two
  ports from `[Ports] RobustPortMin`–`RobustPortMax`: 8002 and 8003 for the first grid.
- **`--host`** is the name or address viewers and other grids reach the grid at. Viewers log in with
  `http://grid.example.org:8002/` (`osctl grid show MyGrid` prints it).
- **Accounts:** `osctl user create` makes one (the password is generated and shown once, or give `--password`);
  `osctl user password` changes one. The grid must be running. The estate owner of each simulator needs an
  account first: a simulator whose owner has none does not start, and says which command makes it.
- **Assets** are kept as files by FSAssets, not in the database: in the grid instance's `data/fsassets/`, or
  wherever `--assets-dir` (on `grid add` or `grid set`) says. The database holds only their index.
  `osctl backup create MyGrid` saves the files with the database (see [Backups](#backups)).
- **The default region:** the grid's first region is where new avatars and Hypergrid visitors arrive.
  `osctl grid set MyGrid --default-region <region>` chooses another; restart the grid afterwards.
- **Starting and stopping:** starting a simulator starts its grid first, `osctl start` starts grids before
  simulators, `osctl stop` stops them after, and `osctl enable Sim1` also makes the grid start at boot. A
  simulator waits for its grid to be ready.
- **Changes:** `osctl grid set` changes the host, names and ports; restart the grid and its simulators. Robust's
  other settings are overridden like any instance's (`osctl config set GridInfoService welcome=… --instance MyGrid`,
  or files in `/etc/osctl/instances/MyGrid/overrides/`).
- **Removing:** `osctl instance delete MyGrid --yes` once its simulators are deleted; its config set goes too.

**Ports.** Most grids use plain HTTP, and Hypergrid addresses are usually `http://host:8002`. Open to the internet
(forward to this machine or container):

| Port | Protocol | What |
|---|---|---|
| 8002 (the grid's public port) | TCP | Login, grid info, the map, Hypergrid |
| each simulator's HTTP port (its lowest region port) | TCP | The simulator's own services |
| each region's port | UDP | The viewers' connection to the region |

`osctl grid show MyGrid` lists them all. **Keep 8003 (the private port) closed:** it is the grid's services, with
no password, for the simulators on this machine. Robust listens on every address, so close it with a firewall
or by not forwarding it. A reverse proxy such as Caddy cannot carry the regions' UDP, so forward the ports
instead. With incus, for example, a proxy device per port on the host:

```
incus config device add opensim robust-public proxy listen=tcp:0.0.0.0:8002 connect=tcp:127.0.0.1:8002
incus config device add opensim sim1-http proxy listen=tcp:0.0.0.0:9000 connect=tcp:127.0.0.1:9000
incus config device add opensim welcome-udp proxy listen=udp:0.0.0.0:9000 connect=udp:127.0.0.1:9000
```

Set `ExternalHostName` in the regions to the same public name (see [Region settings](#region-settings)), so
viewers are sent to an address they can reach.

## Running instances

`[Supervisor] Mode` in `osctl.ini` decides how instances run:

| Mode | How | When to use it |
|---|---|---|
| `systemd` (default) | System units `osctl@<instance>.service`, running as `opensim`. Restarted after a crash; started at boot once enabled. | Normal use with the package. |
| `systemd-user` | Your own systemd user units, running as you. `osctl units install --user` writes them and sets this mode. | Trying osctl from a source checkout under real systemd. For start at boot without logging in: `loginctl enable-linger $USER`. |
| `direct` | No systemd: `osctl start` runs the instance in the background itself. No restart after a crash, nothing at boot. | Development, tests, containers. |

**Stopping, crashes and "broken":**

- **Stopping:** an instance is shut down through OpenSim's RemoteAdmin, falling back to typing `shutdown` into its
  console. It is killed only if it does not stop within `StopSeconds`.
- **A console shutdown:** typing `shutdown` in the console stops the instance for good (no restart).
- **A crash:** systemd restarts the instance after `DelaySeconds`.
- **"Broken":** after more than `MaxRestarts` crashes within `IntervalSeconds`, the instance is left stopped, and
  `osctl status` shows "broken". `osctl start` tries again.

**Warning people first:** `osctl restart --warn 10m <instance>` (and `stop --warn …`) broadcasts a warning
in-world, again at 5 minutes and 1 minute, then restarts.

**The console** is OpenSim's own, in tmux. Any member of the `opensim` group can attach (`osctl console`) and type
into it. Everything it shows is also kept in `/var/log/osctl/<instance>/console.log`.

## Visitors, backups and scheduled restarts

### Visitors

`osctl visitors` lists who arrived in which region, and when, from the instances' `OpenSim.log` files (rotated and
gzipped copies too). Each arrival is a `login` (with the IP address when the login went through this simulator), an
`hg-login` from another grid, or an `arrival` by teleport or region crossing.

```
osctl visitors                              every instance
osctl visitors Test1 --since 7d             the last week (or --since 2026-11-01)
osctl visitors --csv > visitors.csv         also --json
```

### Backups

`osctl backup create <instance>` makes one backup directory, named after its start time, holding:

- **an OAR of each region** (objects, terrain, parcels), saved by OpenSim through RemoteAdmin;
- **the databases:** the instance's and its grid's estate database with `mariadb-dump`, or each SQLite file in its
  `data/` with `sqlite3` (both gzipped);
- **IARs** (inventories) of the users listed in `instance.ini` `[Backups] Iar`, or given with `--iar "First Last"`;
- **for a grid:** its databases, then its asset files in `fsassets/` (FSAssets' `data/`, and in `spool/` any assets
  not yet written out), running or not;
- `definition/`: osctl's own definition of the instance (what is in `/etc/osctl/instances/<instance>/`: `instance.ini`,
  its regions, region defaults, overrides, start-up and shutdown commands; and the config sets it uses), so a lost instance can be made again. Not `secrets.ini`;
- `backup.ini`, which lists every file and whether it was saved.

**A grid's asset files** never change once written, and each is named after its content's SHA-256. So each backup
hard-links the files the previous backup already has and copies only new ones, checking each against its hash.
Every backup directory is whole on its own, yet a week of backups takes little more space than one copy.
`backup list` shows each backup's full size, with how much of it was new. Use `du` for the space on disk, since it
counts a linked file once. If no hard link can be made, the file is copied. A new file that does not match its hash
is most likely still being written: it is left for the next backup. An older one is copied as it is, with a warning.
A grid's inventories (IARs) are saved by one of its simulators, not by the grid.

OARs and IARs need the instance running, since OpenSim writes them. For a stopped instance only the databases are
saved, and those hold everything anyway. `--oar`, `--db` and `--iar` choose parts. A backup with a failed part is
marked incomplete, and the command exits with 1.

**IAR passwords:** OpenSim asks for the user's password to save an inventory. On a standalone, the estate owner's
password is already known. For anyone else, store it first with
`osctl backup iar-password <instance> "First Last"`, which asks for it and keeps it in the instance's `secrets.ini`.
osctl sends it through RemoteAdmin, so it never shows in the console or its logs.

```
osctl backup create Test1                   OARs, databases, IARs from instance.ini
osctl backup create Test1 --db              only the databases (for a grid, its asset files too)
osctl backup list                           every backup, its size, and whether it is complete
osctl backup prune --dry-run                what pruning would remove
```

### Restoring

`osctl backup restore <instance> [<backup>]` puts a backup back into the instance it came from (never into another
one). `<backup>` is a name from `backup list`, or the path to a backup directory copied from elsewhere. Without it,
the newest complete backup is used. The command first shows what it would do; add `--yes` to do it. Before changing
anything it backs the instance up as it is now, so a restore can itself be undone (`--no-backup` skips that).

| Part | Option | Instance | What it does |
|---|---|---|---|
| Databases | `--db` | stopped | Replaces each database (MariaDB: dropped, then loaded from the dump; SQLite: the file replaced) |
| Asset files | `--assets` | stopped | A grid: replaces its FSAssets files with the backup's |
| Definition | `--definition` | stopped | Puts back `instance.ini`, the regions, overrides and commands; makes a lost instance again |
| OAR | `--oar "Region"` | running | Replaces everything in that region with its OAR (repeat for more regions) |
| IAR | `--iar "First Last"` | running | Loads the user's IAR into their inventory: added to what is there, not replacing it |

With none of these options, osctl restores the databases and, for a grid, its asset files. If the instance no longer
exists, it also restores the definition. The databases hold the regions, assets and inventories, so OARs and IARs
are not needed for a whole restore. Parts that need the instance stopped and parts that need it running are
restored in two goes.

**Shared estates:** a grid's simulators keep their estates in the grid's estate database, and each simulator's
backup holds a copy of it. Restoring one simulator leaves that database alone, so it cannot wind back the other
simulators' estates. `--with-estates` restores it anyway. Restoring the grid itself always includes it.

**A lost instance:** the build it uses must exist (`osctl build add`), and its ports must be free. It gets a new
RemoteAdmin password. Passwords are not in backups, so store a standalone's estate owner password and any IAR
passwords again with `osctl backup iar-password`. Then `osctl start` it, and `osctl enable` it to start at boot.

```
osctl backup restore Test1                           show what restoring the newest backup would do
osctl backup restore Test1 --yes                     restore its databases (Test1 stopped)
osctl backup restore Test1 20261126-030000 --oar "Home" --yes    one region from that backup's OAR (Test1 running)
osctl backup restore Test1 /mnt/old/Test1/20261126-030000 --yes  make a lost Test1 again from a copied backup
```

**Pruning** keeps the newest backup, plus the newest of each of the last 7 days and the last 4 weeks that have one.
Change the numbers with `[Backups] KeepDaily`/`KeepWeekly` in `osctl.ini`, per instance in `instance.ini`, or with
`--keep-daily`/`--keep-weekly`. An incomplete backup is removed once a newer complete one exists. Scheduled
backups prune after each run.

### Scheduled restarts and backups

```
osctl schedule restart Test1 "Sun 04:00" --warn 15m
osctl schedule backup Test1 "*-*-* 03:00" --iar "Test User" --keep-daily 14
osctl schedule list                         when, the next run, and the timer's state
osctl schedule remove Test1 restart
```

**When:** a systemd calendar expression, for example `Sun 04:00`, `Mon,Thu 05:30`, `*-*-* 03:00` (daily at
3:00) or `daily`. `systemd-analyze calendar "<expression>"` shows when one fires.

**What runs:**

- A scheduled restart warns people in the regions (at the warning time, then at 5 and 1 minutes), then restarts the
  instance. An instance that is not running is left alone.
- A scheduled backup makes a full backup, then prunes old ones.

**How the schedule is stored:** in `instance.ini` `[Schedule]`, plus a systemd timer per job
(`osctl-restart@<instance>.timer`, `osctl-backup@<instance>.timer`). The timers run as `opensim`.

**Under system systemd, timers need root:** writing them to `/etc/systemd/system` takes root. When you aren't
root, `osctl schedule` saves the schedule and says to run `sudo osctl units install`, which writes every instance's
timers.

**Other modes:**

- **`systemd-user`:** the timers are your own and need no root.
- **`direct`:** there are no timers. `osctl schedule` prints crontab lines instead (`osctl schedule list --cron`
  prints them again).

## Where things are

| Path | What |
|---|---|
| `/etc/osctl/osctl.ini` | osctl's settings (see above). |
| `/etc/osctl/secrets.ini` | Database passwords (written by `sudo osctl db init`). |
| `/etc/osctl/configsets/` | Config sets. |
| `/etc/osctl/overrides/` | Global overrides for every instance. |
| `/etc/osctl/region-defaults.ini` | Settings every region starts with. |
| `/etc/osctl/builds/<build>.ini` | Build definitions (also written by `osctl build add`). |
| `/var/lib/osctl/builds/<build>/` | The source clone, addons, releases, `current` (the release in use), build logs. |
| `/etc/osctl/instances/<instance>/` | The instance's definition: `instance.ini`, `secrets.ini`, `overrides/`, `region-defaults.ini`, `regions/`, start-up and shutdown commands. |
| `/var/lib/osctl/instances/<instance>/` | What it makes: `generated/` (what OpenSim reads, rewritten at every start), `data/` (SQLite), `backups/`, script state. |
| `/etc/systemd/system/osctl-*@<instance>.timer` | The instances' scheduled restarts and backups (written by `osctl schedule` or `sudo osctl units install`). |
| `/var/log/osctl/<instance>/` | `OpenSim.log`, `console.log`, console history, crash reports. |
| `/var/cache/osctl/assets/<grid>/` | The asset cache, one per grid. |
| `/run/osctl/` | Console sockets and run state while instances run. |

## Upgrading and removing

- **Upgrading:** `sudo apt upgrade`, or `sudo apt install ./osctl_<new version>_amd64.deb`. Your settings, builds
  and instances are kept. Running instances carry on and use the new osctl from their next start.
  Instances made by osctl 0.3 or earlier kept their definition in `/var/lib/osctl/instances/<instance>/`; the first
  osctl command after the upgrade moves it to `/etc/osctl/instances/<instance>/` (a running instance once it has
  stopped) and says so.
- **Removing:** `sudo apt remove osctl` stops the instances and removes the program. Settings, builds, instances and
  logs stay, so a reinstall carries on where it left off.
- **Purging:** `sudo apt purge osctl` removes `/etc/osctl`, `/var/lib/osctl`, `/var/cache/osctl` and
  `/var/log/osctl` as well. MariaDB databases are never dropped this way. Delete instances with
  `osctl instance delete <name> --yes` first if you want their databases gone; that dumps each database to
  `/var/lib/osctl/deleted/` before dropping it.

## Command reference

`osctl --help` and `osctl <command> --help` give the options of each command.

| Command | What it does |
|---|---|
| `osctl doctor` | Checks the machine. |
| `osctl init` | Creates the directories and default files (the package does this). |
| `osctl build add\|list\|show\|update\|rollback\|remove` | Named OpenSim builds. |
| `osctl build addon add\|remove` | A build's addon modules. |
| `osctl configset list\|show\|fetch` | Config sets. |
| `osctl instance add\|list\|show\|delete` | Instances. |
| `osctl region add\|list\|remove\|rename\|move\|set\|get` | Regions. `rename` and `move` keep the region's UUID, so its contents stay. |
| `osctl list` | Every instance with its regions. |
| `osctl grid add\|list\|show\|set` | Grids of your own: a Robust instance and the config set its simulators use. |
| `osctl user create\|password <grid> "First Last"` | Accounts on a running grid or standalone. |
| `osctl config show\|explain\|diff\|generate` | An instance's generated configuration. |
| `osctl db init` | Sets up MariaDB (as root). |
| `osctl start\|stop\|restart [instance…]` | All instances when none is named. `stop --force` kills straight away; `--warn 10m` warns people in-world first. |
| `osctl status [instance…]` | Running, starting, stopped, broken; PID, uptime, release. |
| `osctl enable\|disable <instance…>` | Start at boot or not (as root). |
| `osctl console <instance>` | Attaches to the OpenSim console (Ctrl-a d to leave). |
| `osctl cmd <instance> <command…>` | Runs one console command and prints its output. |
| `osctl logs <instance> [-f] [-n N] [--console\|--runner]` | The OpenSim log, the console transcript, or osctl's own messages. |
| `osctl visitors [instance…] [--since …] [--csv\|--json]` | Who arrived in the regions, and when. |
| `osctl backup create\|list\|prune\|iar-password` | Backups: OARs, databases, IARs. |
| `osctl schedule restart\|backup\|remove\|list` | Scheduled restarts (with warnings) and backups. |
| `osctl units install [--user]\|show` | The systemd units: instances, and the scheduled jobs' services and timers. |

**Options for every command:**

- **`--json`:** list and status commands can print JSON, for scripts.
- **`--dry-run`:** shows what a command would change, and changes nothing.
- **`--root DIR`** (or `OSCTL_ROOT=DIR`): uses another directory instead of `/` for everything, for trying osctl
  out without touching the system.

**Exit codes:** 0 means success; 1 means the command ran but something failed; 2 means an error osctl can
explain, printed as one line.

## Building from source

You need the .NET 10 SDK.

```
dotnet test src                       # the tests (some need tmux)
dotnet run --project src/Osctl -- doctor
scripts/build-packages.sh x64         # the .deb (needs nfpm)
```

Day-to-day work happens on the `develop` branch; merging into `main` makes a release. See [PLAN.md](PLAN.md) for
how osctl works inside.

## Licence

Apache-2.0, see [LICENSE](LICENSE).
