# osctl

Open Simulator control tool (MIOS 2.0).

osctl manages several [OpenSimulator](http://opensimulator.org) instances on one Ubuntu machine. It replaces
[MIOS](https://github.com/JakDaniels/MIOS), the old PHP scripts, and is written in C# on .NET 10.

**Status: in development.** Builds, instances, regions, configuration, databases and running under systemd work;
backups, scheduled restarts, Robust and the stats page are still to come. [PLAN.md](PLAN.md) is the design.

## What it will do

- **Builds:** build OpenSim from git as named builds (a branch or tag, plus addon modules and patches). Each
  update goes into a new release directory, so running instances are never rebuilt under them, and you can roll
  back.
- **Instances and regions:** create instances, add, move and rename regions. Ports, UUIDs and grid locations are
  allocated and checked for clashes.
- **Configuration:** each instance's configuration is built from layers of `.ini` files: a config set
  (standalone, OSGrid, …), then global overrides, then instance overrides. Later layers win, and you never edit
  OpenSim's own files. `osctl config explain` shows which file set a key.
- **Running:** one systemd unit per instance. You get start at boot, restarts after a crash, and "broken" after
  repeated quick crashes. Each instance has its OpenSim console in tmux (`osctl console <name>`).
- **Databases:** MariaDB/MySQL, or SQLite for throwaway standalones. osctl creates the users and databases.
- **Operations:** visitor reports from the logs, OAR/IAR and database backups, and scheduled restarts with an
  in-world warning.
- **Later:** Robust grid services (run a small grid of your own), and a stats web page with graphs.

## Install

Ubuntu 24.04 or later, Debian 13 or later: the .deb from the
[Releases page](https://github.com/JakDaniels/opensimtools/releases), or the apt repository at
https://jakdaniels.github.io/opensimtools/ (`sudo apt install osctl`; the page says how to add it). Then join the `opensim` group
(`sudo usermod -aG opensim $USER`, and log in again) and run `osctl doctor`. OpenSim needs the .NET 8 SDK.

## Example

```
osctl build add master --repo git://opensimulator.org/git/opensim --ref master
osctl build update master
osctl instance add Test1 --build master --configset standalone --estate "My Estate" --estate-owner "Test User"
osctl region add Welcome --instance Test1 --location 1000,1000
osctl start Test1
osctl console Test1
```

## Licence

Apache-2.0, see [LICENSE](LICENSE).
