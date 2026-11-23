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
Visitor reports, backups, scheduled restarts, Robust (grid services) and a stats page are still to come.

## Contents

- [Requirements](#requirements)
- [Installing](#installing)
- [After installing](#after-installing)
- [Your first instance](#your-first-instance)
- [How configuration works](#how-configuration-works)
- [osctl.ini settings](#osctlini-settings)
- [Typical setups](#typical-setups)
- [Running instances](#running-instances)
- [Where things are](#where-things-are)
- [Upgrading and removing](#upgrading-and-removing)
- [Command reference](#command-reference)

## Requirements

- **Operating system:** Ubuntu 24.04 or later, or Debian 13 or later, on x64 (amd64) or arm64.
- **Installed with the package:** tmux 3.3 or later and git.
- **For OpenSim itself:** the .NET 8 SDK, which OpenSim needs to build and to run, and libgdiplus (map tiles and
  textures). The package recommends both, but not every distribution release has .NET 8 in its own archive; see
  [The .NET 8 SDK](#the-net-8-sdk).
- **A database:** MariaDB (or MySQL) for real instances. SQLite needs nothing extra.
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
- **systemd:** the unit `osctl@.service` (one copy per instance, as `osctl@<instance>.service`), and a polkit rule
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
3. **Instance overrides:** `/var/lib/osctl/instances/<instance>/overrides/*.ini`, in alphabetical order.
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

1. `[Region Defaults]` in `/etc/osctl/region-defaults.ini`, for every region. The shipped values are MIOS's:
   `MaxPrims = 15000`, `MaxAgents = 100`, `ExternalHostName = SYSTEMIP` and a few more.
2. Other `.ini` files in the instance's `regions/` directory, under a `[Region Name]` section (for example, an
   addon module's per-region settings).
3. The region's own settings: `osctl region set Welcome MaxAgents=40 RegionType=Estate`. `Key=` with nothing
   after it removes one.

`ExternalHostName = SYSTEMIP` makes OpenSim use this machine's IP address. For regions people reach over the
internet, set it to your public DNS name, either for all regions in `region-defaults.ini` or for one region with
`osctl region set Welcome ExternalHostName=sim.example.org`.

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

**The console** is OpenSim's own, in tmux. Any member of the `opensim` group can attach (`osctl console`) and type
into it. Everything it shows is also kept in `/var/log/osctl/<instance>/console.log`.

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
| `/var/lib/osctl/instances/<instance>/` | `instance.ini`, `secrets.ini`, `overrides/`, `regions/`, `generated/` (what OpenSim reads, rewritten at every start), `data/` (SQLite), script state. |
| `/var/log/osctl/<instance>/` | `OpenSim.log`, `console.log`, console history, crash reports. |
| `/var/cache/osctl/assets/<grid>/` | The asset cache, one per grid. |
| `/run/osctl/` | Console sockets and run state while instances run. |

## Upgrading and removing

- **Upgrading:** `sudo apt upgrade`, or `sudo apt install ./osctl_<new version>_amd64.deb`. Your settings, builds
  and instances are kept. Running instances carry on and use the new osctl from their next start.
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
| `osctl region add\|list\|remove\|rename\|move\|set` | Regions. `rename` and `move` keep the region's UUID, so its contents stay. |
| `osctl list` | Every instance with its regions. |
| `osctl config show\|explain\|diff\|generate` | An instance's generated configuration. |
| `osctl db init` | Sets up MariaDB (as root). |
| `osctl start\|stop\|restart [instance…]` | All instances when none is named. `stop --force` kills straight away. |
| `osctl status [instance…]` | Running, starting, stopped, broken; PID, uptime, release. |
| `osctl enable\|disable <instance…>` | Start at boot or not (as root). |
| `osctl console <instance>` | Attaches to the OpenSim console (Ctrl-a d to leave). |
| `osctl cmd <instance> <command…>` | Runs one console command and prints its output. |
| `osctl logs <instance> [-f] [-n N] [--console\|--runner]` | The OpenSim log, the console transcript, or osctl's own messages. |
| `osctl units install [--user]\|show` | The systemd unit. |

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
