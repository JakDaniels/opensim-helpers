// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Osctl.Builds;
using Osctl.ConfigSets;
using Osctl.Databases;
using Osctl.Instances;

namespace Osctl.Cli;

/// <summary><c>osctl instance …</c>, <c>osctl region …</c> and <c>osctl list</c> (PLAN.md "Commands").</summary>
internal static class InstanceCommands
{
    private const string PasswordCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static Command Create(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("instance", "OpenSim instances: each runs one or more regions from one build");
        command.Add(Add(env, globals));
        command.Add(InstanceList(env, globals));
        command.Add(Show(env, globals));
        command.Add(Delete(env, globals));
        return command;
    }

    private static Argument<string> InstanceName() => new("name") { Description = "The instance's name (letters, digits, _ and -)" };

    private static Command Add(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var build = new Option<string?>("--build") { Description = "The build it runs (default: [Defaults] Build in osctl.ini, or the only build)" };
        var configSet = new Option<string?>("--configset") { Description = "Its base configuration (default: [Defaults] ConfigSet in osctl.ini)" };
        var db = new Option<string>("--db") { Description = "mariadb or sqlite", DefaultValueFactory = _ => "mariadb" };
        var estate = new Option<string?>("--estate") { Description = "The estate its regions join (default: '<name> Estate')" };
        var owner = new Option<string>("--estate-owner") { Description = "The estate owner's avatar name, \"First Last\"", Required = true };
        var email = new Option<string?>("--estate-owner-email") { Description = "Standalone: the owner's e-mail address" };
        var password = new Option<string?>("--estate-owner-password") { Description = "Standalone: the owner's password (default: generated and shown once)" };
        var command = new Command("add", "Create an instance (then add regions with 'osctl region add')")
            { name, build, configSet, db, estate, owner, email, password };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var settings = globals.Settings(parse);
            var store = new InstanceStore(paths);
            var builds = new BuildStore(paths);
            var sets = new ConfigSetStore(paths);

            var instanceName = Names.CheckInstance(parse.GetRequiredValue(name));
            if (store.Exists(instanceName) || store.AnyOnDisk(instanceName))
                throw new OsctlException($"Instance '{instanceName}' already exists ({store.ConfigDir(instanceName)})");

            var buildName = parse.GetValue(build) ?? settings.Defaults.Build
                ?? (builds.Names() is [var only] ? only
                    : throw new OsctlException("Which build? Give --build (or set [Defaults] Build in osctl.ini)"));
            if (!builds.Exists(buildName))
                throw new OsctlException($"There is no build '{buildName}'; 'osctl build list' shows the builds");

            var set = sets.Load(parse.GetValue(configSet) ?? settings.Defaults.ConfigSet);
            if (set.Mode == ConfigSetMode.Robust)
                throw new OsctlException($"Config set '{set.Name}' is for a grid's Robust services: 'osctl grid add' makes those");
            var database = InstanceDefinition.ParseDatabase(parse.GetRequiredValue(db))
                ?? throw new OsctlException("--db: expected mariadb or sqlite");
            var ownerName = InstanceDefinition.CheckOwner(parse.GetRequiredValue(owner).Trim());

            string? ownerPassword = null;
            var generated = false;
            if (set.Mode == ConfigSetMode.Standalone)
            {
                ownerPassword = parse.GetValue(password);
                if (ownerPassword is null)
                {
                    ownerPassword = RandomNumberGenerator.GetString(PasswordCharacters, 20);
                    generated = true;
                }
                else if (ownerPassword.Contains('"') || ownerPassword.Length == 0)
                {
                    throw new OsctlException("--estate-owner-password: must not be empty or contain a double quote");
                }
            }
            else if (parse.GetValue(password) is not null)
            {
                await env.Error.WriteLineAsync($"osctl: --estate-owner-password is not used: on grid '{set.Grid}' the owner's account already exists");
            }

            var instance = new InstanceDefinition(instanceName, buildName, set.Name, database,
                new EstateDefinition(parse.GetValue(estate) ?? $"{instanceName} Estate", ownerName,
                    set.Mode == ConfigSetMode.Standalone ? Guid.NewGuid() : null, parse.GetValue(email)));

            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would create instance '{instanceName}' in {store.ConfigDir(instanceName)}:");
                await env.Out.WriteAsync(Ini.IniWriter.ToText(instance.ToIni()));
                if (!sets.IsFetched(set))
                    await sets.FetchAsync(set, env.Web, dryRun: true, env.Out, ct);
                return 0;
            }

            if (!sets.IsFetched(set))
                await sets.FetchAsync(set, env.Web, dryRun: false, env.Out, ct);
            if (database == DatabaseKind.MariaDb)
            {
                var generator = new ConfigGenerator(paths, settings);
                var admin = env.DatabaseFactory.Admin(settings.Database);
                foreach (var dbName in new[] { generator.DatabaseName(instanceName), generator.EstateDatabaseName(set.GridFor(instanceName)) })
                {
                    if (!await admin.ExistsAsync(dbName, ct))
                    {
                        await admin.CreateAsync(dbName, ct);
                        await env.Out.WriteLineAsync($"Created database {dbName}.");
                    }
                }
            }
            store.Create(instance, ownerPassword, RandomNumberGenerator.GetString(PasswordCharacters, 32));
            await env.Out.WriteLineAsync($"Instance '{instanceName}' created ({set.Name}, build {buildName}, {parse.GetRequiredValue(db)}).");
            if (generated)
                await env.Out.WriteLineAsync(
                    $"The estate owner '{ownerName}' will be created with the password {ownerPassword} " +
                    $"(shown once; it is kept in {store.SecretsFile(instanceName)}).");
            if (set.Robust is { } grid)
                await env.Out.WriteLineAsync(
                    $"On grid '{grid}' the estate owner needs an account: osctl user create {grid} \"{ownerName}\" (once, with the grid running).");
            await env.Out.WriteLineAsync($"Next: osctl region add <region> --instance {instanceName} --location <x,y>");
            return 0;
        });
        return command;
    }

    private static Command InstanceList(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print the instances as JSON" };
        var command = new Command("list", "List the instances") { json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var infos = store.Names().Select(n => Describe(store, n)).ToList();
            if (parse.GetValue(json))
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(infos, CliApp.Json));
            else if (infos.Count == 0)
                await env.Out.WriteLineAsync("No instances yet: 'osctl instance add <name> …' creates one.");
            else
                Table.Write(env.Out, ["INSTANCE", "BUILD", "CONFIGSET", "DB", "PORT", "REGIONS"],
                    infos.Select(i => (IReadOnlyList<string>)
                        [i.Name, i.Build, i.ConfigSet, i.Database, i.Port == 0 ? "-" : i.Port.ToString(), i.Regions.Count.ToString()]));
            return 0;
        });
        return command;
    }

    private static Command Show(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var json = new Option<bool>("--json") { Description = "Print the instance as JSON" };
        var command = new Command("show", "Show an instance and its regions") { name, json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var info = Describe(store, parse.GetRequiredValue(name));
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(info, CliApp.Json));
                return 0;
            }
            await env.Out.WriteLineAsync($"Instance:   {info.Name} ({store.ConfigDir(info.Name)}, {store.Dir(info.Name)})");
            await env.Out.WriteLineAsync($"Build:      {info.Build}");
            await env.Out.WriteLineAsync($"Config set: {info.ConfigSet}");
            await env.Out.WriteLineAsync($"Database:   {info.Database}");
            if (info.Kind == "robust")
            {
                await env.Out.WriteLineAsync($"Runs:       Robust, the services of grid '{info.Name}' ('osctl grid show {info.Name}')");
                await env.Out.WriteLineAsync($"Port:       {info.Port} (public)");
                return 0;
            }
            await env.Out.WriteLineAsync($"Estate:     {info.Estate} (owner {info.EstateOwner})");
            await env.Out.WriteLineAsync($"HTTP port:  {(info.Port == 0 ? "- (no regions)" : info.Port.ToString())}");
            await env.Out.WriteLineAsync();
            WriteRegions(env.Out, info.Regions);
            return 0;
        });
        return command;
    }

    private static Command Delete(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var yes = new Option<bool>("--yes") { Description = "Really delete it" };
        var keepDatabase = new Option<bool>("--keep-database") { Description = "MariaDB: leave the instance's database in place" };
        var command = new Command("delete",
            "Delete an instance: its configuration, regions, scripts and logs, and (MariaDB) its database after dumping it") { name, yes, keepDatabase };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var instance = store.Load(parse.GetRequiredValue(name));
            var regions = store.Regions(instance.Name);
            if (instance.IsRobust
                && new Grids(globals.Paths(parse), globals.Settings(parse), env.PortProbe).Simulators(instance.Name) is { Count: > 0 } sims)
                throw new OsctlException(
                    $"Grid '{instance.Name}' still has simulator instances ({string.Join(", ", sims.Select(i => i.Name))}); delete them first");
            if (globals.IsDryRun(parse) || !parse.GetValue(yes))
            {
                await env.Out.WriteLineAsync($"would delete instance '{instance.Name}' ({regions.Count} region(s)): {store.ConfigDir(instance.Name)}, {store.Dir(instance.Name)} and {store.LogDir(instance.Name)}");
                if (globals.IsDryRun(parse))
                    return 0;
                await env.Out.WriteLineAsync("Add --yes to delete it.");
                return CliApp.ExitFailure;
            }
            if (instance.Database == DatabaseKind.MariaDb && !parse.GetValue(keepDatabase))
                await DropDatabasesAsync(env, globals, parse, instance, ct);
            var onGrid = new Grids(globals.Paths(parse), globals.Settings(parse), env.PortProbe).GridOf(instance);
            store.Delete(instance.Name);
            if (onGrid is { Grid.DefaultRegion: { } defaultRegion } && regions.Any(r => r.Name == defaultRegion))
            {
                store.Save(onGrid with { Grid = onGrid.GridSettings with { DefaultRegion = null } });
                await env.Out.WriteLineAsync(
                    $"Its region '{defaultRegion}' was grid '{onGrid.Name}''s default region; choose another: osctl grid set {onGrid.Name} --default-region <region>");
            }
            if (instance.IsRobust)
            {
                var sets = new ConfigSetStore(globals.Paths(parse));
                if (sets.Exists(instance.Name) && sets.Load(instance.Name).Robust == instance.Name)
                    File.Delete(sets.DefinitionFile(instance.Name));
            }
            await env.Out.WriteLineAsync($"Instance '{instance.Name}' deleted.");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Dumps the instance's database (and a standalone's own estate database) to
    /// /var/lib/osctl/deleted/&lt;name&gt;-&lt;time&gt;/, then drops them. A grid's shared estate database stays.
    /// </summary>
    private static async Task DropDatabasesAsync(CliEnvironment env, GlobalOptions globals, ParseResult parse, InstanceDefinition instance, CancellationToken ct)
    {
        var paths = globals.Paths(parse);
        var settings = globals.Settings(parse);
        var generator = new ConfigGenerator(paths, settings);
        var sets = new ConfigSetStore(paths);
        var databases = new List<string> { generator.DatabaseName(instance.Name) };
        // A standalone's estate database is its own, and so is a grid's (its simulators are gone by now).
        if (sets.Exists(instance.ConfigSet) && sets.Load(instance.ConfigSet) is { Mode: ConfigSetMode.Standalone or ConfigSetMode.Robust } set)
            databases.Add(generator.EstateDatabaseName(set.GridFor(instance.Name)));

        var admin = env.DatabaseFactory.Admin(settings.Database);
        var dumps = Path.Combine(paths.Lib, "deleted", $"{instance.Name}-{env.Clock.GetUtcNow():yyyyMMdd-HHmmss}");
        foreach (var database in databases)
        {
            if (!await admin.ExistsAsync(database, ct))
                continue;
            var file = Path.Combine(dumps, database + ".sql");
            await admin.DumpAsync(database, file, ct);
            await admin.DropAsync(database, ct);
            await env.Out.WriteLineAsync($"Database {database} dumped to {file} and dropped.");
        }
    }

    // ---------------------------------------------------------------- regions

    public static Command Regions(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("region", "The regions of instances");
        command.Add(RegionAdd(env, globals));
        command.Add(RegionList(env, globals));
        command.Add(RegionRemove(env, globals));
        command.Add(RegionRename(env, globals));
        command.Add(RegionMove(env, globals));
        command.Add(RegionGet(env, globals));
        command.Add(RegionSet(env, globals));
        command.Add(RegionDefaults(env, globals));
        return command;
    }

    private static Argument<string> RegionName() => new("region") { Description = "The region's name" };

    private static Option<string?> InstanceOption(string description) => new("--instance") { Description = description };

    private static Command RegionAdd(CliEnvironment env, GlobalOptions globals)
    {
        var region = RegionName();
        var instanceOption = new Option<string>("--instance") { Description = "The instance it runs in", Required = true };
        var location = new Option<string>("--location") { Description = "Its grid location x,y (in 256 m cells)", Required = true };
        var size = new Option<int>("--size") { Description = "Its size in metres: 256, 512, … 4096", DefaultValueFactory = _ => 256 };
        var port = new Option<int?>("--port") { Description = "Its UDP port (default: the first free one in [Ports])" };
        var uuid = new Option<Guid?>("--uuid") { Description = "Its UUID (default: a new one)" };
        var command = new Command("add", "Add a region to an instance") { region, instanceOption, location, size, port, uuid };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (store, planner) = Planner(env, globals, parse);
            var instance = store.Load(parse.GetRequiredValue(instanceOption));
            if (instance.IsRobust)
                throw new OsctlException($"'{instance.Name}' is a grid's Robust instance: regions go in its simulator instances");
            var (x, y) = RegionDefinition.ParseLocation(parse.GetRequiredValue(location));
            var planned = planner.Plan(instance, parse.GetRequiredValue(region).Trim(), x, y, parse.GetValue(size),
                parse.GetValue(port), parse.GetValue(uuid));

            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would add region '{planned.Name}' to instance '{instance.Name}' at {x},{y}, port {planned.Port}, UUID {planned.Uuid}");
                return 0;
            }
            store.SaveRegions(instance.Name, [.. store.Regions(instance.Name), planned]);
            await env.Out.WriteLineAsync($"Region '{planned.Name}' added to instance '{instance.Name}': {x},{y}, {planned.Size} m, port {planned.Port}, UUID {planned.Uuid}.");
            // The first region on one of osctl's grids is where new avatars arrive, until 'osctl grid set' says otherwise.
            var grids = new Grids(globals.Paths(parse), globals.Settings(parse), env.PortProbe);
            if (grids.GridOf(instance) is { Grid.DefaultRegion: null } grid)
            {
                store.Save(grid with { Grid = grid.GridSettings with { DefaultRegion = planned.Name } });
                await env.Out.WriteLineAsync($"It is grid '{grid.Name}''s default region (restart the grid for it to take effect).");
            }
            return 0;
        });
        return command;
    }

    private static Command RegionList(CliEnvironment env, GlobalOptions globals)
    {
        var instanceOption = InstanceOption("Only this instance's regions");
        var json = new Option<bool>("--json") { Description = "Print the regions as JSON" };
        var command = new Command("list", "List the regions") { instanceOption, json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (_, planner) = Planner(env, globals, parse);
            var regions = planner.AllRegions()
                .Where(p => parse.GetValue(instanceOption) is not { } only || p.Instance == only)
                .ToList();
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(regions.Select(p => new
                {
                    p.Instance, p.Grid, p.Region.Name, p.Region.Uuid, p.Region.X, p.Region.Y, p.Region.Size, p.Region.Port,
                }), CliApp.Json));
                return 0;
            }
            Table.Write(env.Out, ["REGION", "INSTANCE", "GRID", "LOCATION", "SIZE", "PORT", "UUID"],
                regions.Select(p => (IReadOnlyList<string>)
                    [p.Region.Name, p.Instance, p.Grid, $"{p.Region.X},{p.Region.Y}", p.Region.Size.ToString(), p.Region.Port.ToString(), p.Region.Uuid.ToString()]));
            return 0;
        });
        return command;
    }

    private static Command RegionRemove(CliEnvironment env, GlobalOptions globals)
    {
        var region = RegionName();
        var instanceOption = InstanceOption("The instance, if region names repeat in different grids");
        var yes = new Option<bool>("--yes") { Description = "Really remove it" };
        var command = new Command("remove", "Remove a region from its instance (its objects stay in the database)") { region, instanceOption, yes };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (store, planner) = Planner(env, globals, parse);
            var found = Find(planner, parse.GetRequiredValue(region), parse.GetValue(instanceOption));
            if (globals.IsDryRun(parse) || !parse.GetValue(yes))
            {
                await env.Out.WriteLineAsync($"would remove region '{found.Region.Name}' from instance '{found.Instance}'");
                if (globals.IsDryRun(parse))
                    return 0;
                await env.Out.WriteLineAsync("Add --yes to remove it.");
                return CliApp.ExitFailure;
            }
            store.SaveRegions(found.Instance, store.Regions(found.Instance).Where(r => r.Uuid != found.Region.Uuid));
            await env.Out.WriteLineAsync($"Region '{found.Region.Name}' removed from instance '{found.Instance}'.");
            DefaultRegionChanged(env, globals, parse, store, found.Instance, found.Region.Name, null);
            return 0;
        });
        return command;
    }

    private static Command RegionRename(CliEnvironment env, GlobalOptions globals)
    {
        var region = RegionName();
        var newName = new Argument<string>("new-name") { Description = "The new name" };
        var instanceOption = InstanceOption("The instance, if region names repeat in different grids");
        var command = new Command("rename", "Rename a region (it keeps its UUID, so its contents stay)") { region, newName, instanceOption };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (store, planner) = Planner(env, globals, parse);
            var found = Find(planner, parse.GetRequiredValue(region), parse.GetValue(instanceOption));
            var r = found.Region;
            var renamed = planner.Plan(store.Load(found.Instance), parse.GetRequiredValue(newName).Trim(), r.X, r.Y, r.Size,
                r.Port, r.Uuid, r.Settings, ignore: r.Uuid);
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would rename region '{r.Name}' to '{renamed.Name}'");
                return 0;
            }
            store.SaveRegions(found.Instance, store.Regions(found.Instance).Select(x => x.Uuid == r.Uuid ? renamed : x));
            await env.Out.WriteLineAsync($"Region '{r.Name}' is now '{renamed.Name}'.");
            DefaultRegionChanged(env, globals, parse, store, found.Instance, r.Name, renamed.Name);
            return 0;
        });
        return command;
    }

    private static Command RegionMove(CliEnvironment env, GlobalOptions globals)
    {
        var region = RegionName();
        var instanceOption = InstanceOption("The instance it is in now, if region names repeat in different grids");
        var to = new Option<string?>("--to") { Description = "The instance to move it to (default: where it is)" };
        var location = new Option<string?>("--location") { Description = "A new grid location x,y (default: where it is)" };
        var command = new Command("move", "Move a region to another instance, or to another grid location") { region, instanceOption, to, location };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (store, planner) = Planner(env, globals, parse);
            var found = Find(planner, parse.GetRequiredValue(region), parse.GetValue(instanceOption));
            var r = found.Region;
            var target = store.Load(parse.GetValue(to) ?? found.Instance);
            if (target.IsRobust)
                throw new OsctlException($"'{target.Name}' is a grid's Robust instance: regions go in its simulator instances");
            var (x, y) = parse.GetValue(location) is { } text ? RegionDefinition.ParseLocation(text) : (r.X, r.Y);
            if (target.Name == found.Instance && (x, y) == (r.X, r.Y))
                throw new OsctlException("Give --to and/or --location: the region would stay where it is");

            var moved = planner.Plan(target, r.Name, x, y, r.Size, r.Port, r.Uuid, r.Settings, ignore: r.Uuid);
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would move region '{r.Name}' to instance '{target.Name}' at {x},{y}");
                return 0;
            }
            store.SaveRegions(found.Instance, store.Regions(found.Instance).Where(o => o.Uuid != r.Uuid));
            store.SaveRegions(target.Name, [.. store.Regions(target.Name), moved]);
            await env.Out.WriteLineAsync($"Region '{r.Name}' is now in instance '{target.Name}' at {x},{y}.");
            return 0;
        });
        return command;
    }

    private static Command RegionGet(CliEnvironment env, GlobalOptions globals)
    {
        var region = RegionName();
        var keys = new Argument<string[]>("settings")
        {
            Description = "Only these settings (case-insensitive; * and ? match any text and one character)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var instanceOption = InstanceOption("The instance, if region names repeat in different grids");
        var own = new Option<bool>("--own") { Description = "Only the region's own settings (regions/Regions.ini), not those it inherits" };
        var origin = new Option<bool>("--origin") { Description = "Show the file (and line) each value comes from" };
        var json = new Option<bool>("--json") { Description = "Print the settings as JSON" };
        var secrets = new Option<bool>("--show-secrets") { Description = "Show passwords instead of ********" };
        var command = new Command("get",
            "Show a region's settings: the global and the instance's [Region Defaults], its other regions/*.ini, then the region's own, later wins")
        { region, keys, instanceOption, own, origin, json, secrets };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (store, planner) = Planner(env, globals, parse);
            var found = Find(planner, parse.GetRequiredValue(region), parse.GetValue(instanceOption));
            var settings = new ConfigGenerator(globals.Paths(parse), globals.Settings(parse)).RegionSettings(found.Instance);
            var regionsFile = store.RegionsFile(found.Instance);
            var entries = settings[found.Region.Name]!.Entries
                .Where(e => !parse.GetValue(own) || e.Origin?.File == regionsFile)
                .ToList();

            return await PrintSettingsAsync(env, parse, entries, parse.GetValue(keys) ?? [], parse.GetValue(origin), parse.GetValue(json),
                parse.GetValue(secrets), $"region '{found.Region.Name}'");
        });
        return command;
    }

    /// <summary>
    /// Prints settings as <c>Key = Value</c> lines (or JSON), only those matching <paramref name="patterns"/> when
    /// there are any; exit code 1 if a pattern matches nothing.
    /// </summary>
    private static async Task<int> PrintSettingsAsync(CliEnvironment env, ParseResult parse, List<Ini.IniEntry> entries, string[] patterns,
        bool origin, bool json, bool secrets, string what)
    {
        var missing = new List<string>();
        if (patterns.Length > 0)
        {
            var matchers = patterns.Select(p => (Pattern: p, Regex: KeyPattern(p))).ToList();
            missing.AddRange(matchers.Where(m => !entries.Any(e => m.Regex.IsMatch(e.Key))).Select(m => m.Pattern));
            entries = entries.Where(e => matchers.Any(m => m.Regex.IsMatch(e.Key))).ToList();
        }

        string Value(Ini.IniEntry e) => secrets ? e.Value : SecretMask.Mask(e.Key, e.Value);
        if (json)
        {
            object shown = origin
                ? entries.Select(e => new { e.Key, Value = Value(e), Origin = e.Origin?.ToString() })
                : entries.ToDictionary(e => e.Key, Value);
            await env.Out.WriteLineAsync(JsonSerializer.Serialize(shown, CliApp.Json));
        }
        else
        {
            var width = entries.Count == 0 ? 0 : entries.Max(e => $"{e.Key} = {Value(e)}".Length);
            foreach (var e in entries)
            {
                var line = $"{e.Key} = {Value(e)}";
                await env.Out.WriteLineAsync(origin && e.Origin is { } o ? $"{line.PadRight(width)}  ; {o}" : line);
            }
        }

        foreach (var pattern in missing)
            await env.Error.WriteLineAsync($"osctl: {what} has no setting matching '{pattern}'");
        return missing.Count == 0 ? 0 : CliApp.ExitFailure;
    }

    /// <summary>A setting name pattern: case-insensitive, with * and ? as wildcards.</summary>
    private static Regex KeyPattern(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Command RegionSet(CliEnvironment env, GlobalOptions globals)
    {
        var region = RegionName();
        var values = new Argument<string[]>("settings") { Description = "Key=Value region settings (Key= removes one)", Arity = ArgumentArity.OneOrMore };
        var instanceOption = InstanceOption("The instance, if region names repeat in different grids");
        var command = new Command("set", "Set region settings, e.g. MaxAgents=40 (those not managed by osctl's other commands)") { region, values, instanceOption };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var (store, planner) = Planner(env, globals, parse);
            var found = Find(planner, parse.GetRequiredValue(region), parse.GetValue(instanceOption));
            var settings = found.Region.Settings.ToList();
            foreach (var (key, value) in RegionPairs(parse.GetRequiredValue(values)))
            {
                settings.RemoveAll(s => s.Key == key);
                if (value.Length > 0)
                    settings.Add(KeyValuePair.Create(key, value));
            }
            var updated = found.Region with { Settings = settings };
            Ini.IniWriter.ToText(RegionDefinition.ToIni([updated]));   // refuses values an ini file cannot hold
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would set {string.Join(", ", settings.Select(s => $"{s.Key}={s.Value}"))} on region '{found.Region.Name}'");
                return 0;
            }
            store.SaveRegions(found.Instance, store.Regions(found.Instance).Select(x => x.Uuid == updated.Uuid ? updated : x));
            await env.Out.WriteLineAsync($"Region '{found.Region.Name}' updated.");
            return 0;
        });
        return command;
    }

    /// <summary>Key=Value arguments (Key= to remove one), refusing what other commands manage.</summary>
    internal static List<(string Key, string Value)> Pairs(IEnumerable<string> pairs)
    {
        var result = new List<(string, string)>();
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw new OsctlException($"'{pair}': expected Key=Value");
            var key = pair[..eq].Trim();
            if (key.Length == 0 || key.Contains(' ') || key.Contains(';') || key.StartsWith('['))
                throw new OsctlException($"'{key}' is not a setting name");
            if (key.StartsWith("Include-", StringComparison.OrdinalIgnoreCase))
                throw new OsctlException($"{key}: includes are not followed in overrides");
            result.Add((key, pair[(eq + 1)..].Trim()));
        }
        return result;
    }

    private static List<(string Key, string Value)> RegionPairs(IEnumerable<string> pairs)
    {
        var result = Pairs(pairs);
        foreach (var (key, _) in result)
        {
            if (key is "RegionUUID" or "Location" or "InternalPort" or "SizeX" or "SizeY")
                throw new OsctlException($"{key}: use 'osctl region add/move/rename' for a region's UUID, location, port and size");
        }
        return result;
    }

    // ---------------------------------------------------------------- osctl region defaults

    private const string RegionDefaultsSection = "Region Defaults";

    /// <summary>--instance A or --global: which [Region Defaults] (or overrides) a command works on.</summary>
    internal static (Option<string?> Instance, Option<bool> Global) TargetOptions(string instanceText, string globalText) =>
        (new Option<string?>("--instance") { Description = instanceText }, new Option<bool>("--global") { Description = globalText });

    /// <summary>The instance named by --instance (which must exist), or null for --global; exactly one of them.</summary>
    internal static string? Target(ParseResult parse, InstanceStore store, (Option<string?> Instance, Option<bool> Global) options)
    {
        var instance = parse.GetValue(options.Instance);
        var global = parse.GetValue(options.Global);
        if ((instance is null) == !global)
            throw new OsctlException("Name the instance (--instance <name>) or add --global for every instance");
        return instance is null ? null : store.Load(instance).Name;
    }

    private static Command RegionDefaults(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("defaults",
            "[Region Defaults]: settings every region starts with (--global, /etc/osctl/region-defaults.ini) or an instance's regions (--instance)");
        command.Add(RegionDefaultsGet(env, globals));
        command.Add(RegionDefaultsSet(env, globals));
        return command;
    }

    private static Command RegionDefaultsGet(CliEnvironment env, GlobalOptions globals)
    {
        var keys = new Argument<string[]>("settings")
        {
            Description = "Only these settings (case-insensitive; * and ? match any text and one character)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var target = TargetOptions("The instance's region defaults: the global ones with its own over them", "The global region defaults");
        var own = new Option<bool>("--own") { Description = "With --instance: only the instance's own defaults, not the global ones" };
        var origin = new Option<bool>("--origin") { Description = "Show the file (and line) each value comes from" };
        var json = new Option<bool>("--json") { Description = "Print the settings as JSON" };
        var secrets = new Option<bool>("--show-secrets") { Description = "Show passwords instead of ********" };
        var command = new Command("get", "Show region defaults") { keys, target.Instance, target.Global, own, origin, json, secrets };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var store = new InstanceStore(paths);
            var instance = Target(parse, store, target);
            var files = instance is null ? [paths.RegionDefaultsFile]
                : parse.GetValue(own) ? [store.RegionDefaultsFile(instance)]
                : new[] { paths.RegionDefaultsFile, store.RegionDefaultsFile(instance) };
            var merged = new Ini.IniDocument();
            foreach (var file in files.Where(File.Exists))
                foreach (var e in Ini.IniParser.ParseFile(file)[RegionDefaultsSection]?.Entries ?? [])
                    merged.Set(RegionDefaultsSection, e.Key, e.Value, e.Origin);
            var entries = merged[RegionDefaultsSection]?.Entries.ToList() ?? [];
            return await PrintSettingsAsync(env, parse, entries, parse.GetValue(keys) ?? [], parse.GetValue(origin), parse.GetValue(json),
                parse.GetValue(secrets), instance is null ? "the global region defaults" : $"instance '{instance}''s region defaults");
        });
        return command;
    }

    private static Command RegionDefaultsSet(CliEnvironment env, GlobalOptions globals)
    {
        var values = new Argument<string[]>("settings") { Description = "Key=Value settings (Key= removes one)", Arity = ArgumentArity.OneOrMore };
        var target = TargetOptions("For this instance's regions", "For every region (/etc/osctl/region-defaults.ini)");
        var command = new Command("set", "Set region defaults, e.g. MaxAgents=40; a region's own settings ('osctl region set') still win")
        { values, target.Instance, target.Global };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var store = new InstanceStore(paths);
            var instance = Target(parse, store, target);
            var pairs = RegionPairs(parse.GetRequiredValue(values));
            foreach (var (key, value) in pairs)
                Ini.IniWriter.FormatValue(RegionDefaultsSection, new Ini.IniEntry(key, value, null));   // refuses what an ini file cannot hold
            var file = instance is null ? paths.RegionDefaultsFile : store.RegionDefaultsFile(instance);
            var what = instance is null ? "every region" : $"instance '{instance}''s regions";
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would set {string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"))} in {file} for {what}");
                return 0;
            }
            if (!File.Exists(file))
                File.WriteAllText(file, instance is null
                    ? $"; Settings every region starts with. Change them with 'osctl region defaults set --global Key=Value' or by hand.\n\n[{RegionDefaultsSection}]\n"
                    : $"; Settings instance '{instance}''s regions start with, over /etc/osctl/region-defaults.ini; a region's own settings win.\n" +
                      $"; Change them with 'osctl region defaults set --instance {instance} Key=Value' or by hand.\n\n[{RegionDefaultsSection}]\n");
            foreach (var (key, value) in pairs)
            {
                if (value.Length == 0)
                    Ini.IniFileEditor.RemoveValue(file, RegionDefaultsSection, key);
                else
                    Ini.IniFileEditor.SetValue(file, RegionDefaultsSection, key, value);
            }
            await env.Out.WriteLineAsync($"Region defaults for {what} updated ({file}); they take effect at the next start.");
            return 0;
        });
        return command;
    }

    // ---------------------------------------------------------------- osctl list

    public static Command List(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print the instances and regions as JSON" };
        var command = new Command("list", "Show every instance with its regions") { json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var infos = store.Names().Select(n => Describe(store, n)).ToList();
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(infos, CliApp.Json));
                return 0;
            }
            if (infos.Count == 0)
            {
                await env.Out.WriteLineAsync("No instances yet: 'osctl instance add <name> …' creates one.");
                return 0;
            }
            var first = true;
            foreach (var info in infos)
            {
                if (!first)
                    await env.Out.WriteLineAsync();
                first = false;
                if (info.Kind == "robust")
                {
                    await env.Out.WriteLineAsync($"{info.Name}  (grid: Robust, build {info.Build}, {info.Database}, port {info.Port})");
                    continue;
                }
                await env.Out.WriteLineAsync($"{info.Name}  (build {info.Build}, {info.ConfigSet}, {info.Database}, port {(info.Port == 0 ? "-" : info.Port.ToString())})");
                WriteRegions(env.Out, info.Regions, indent: "  ");
            }
            return 0;
        });
        return command;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A renamed or removed region that was its grid's default region: the grid follows the rename, or has none.</summary>
    private static void DefaultRegionChanged(CliEnvironment env, GlobalOptions globals, ParseResult parse, InstanceStore store,
        string instance, string oldName, string? newName)
    {
        var grids = new Grids(globals.Paths(parse), globals.Settings(parse), env.PortProbe);
        if (grids.GridOf(store.Load(instance)) is not { } grid || grid.GridSettings.DefaultRegion != oldName)
            return;
        store.Save(grid with { Grid = grid.GridSettings with { DefaultRegion = newName } });
        env.Out.WriteLine(newName is null
            ? $"It was grid '{grid.Name}''s default region; choose another: osctl grid set {grid.Name} --default-region <region>"
            : $"It stays grid '{grid.Name}''s default region.");
    }

    internal sealed record RegionInfo(string Name, Guid Uuid, int X, int Y, int Size, int Port);

    /// <summary>An instance as list and show print it. A grid's Robust instance has no estate; its port is the public one.</summary>
    internal sealed record InstanceInfo(
        string Name, string Kind, string Build, string ConfigSet, string Database, string Estate, string EstateOwner, int Port,
        IReadOnlyList<RegionInfo> Regions);

    private static InstanceInfo Describe(InstanceStore store, string name)
    {
        var instance = store.Load(name);
        var regions = store.Regions(name);
        return new InstanceInfo(instance.Name, instance.IsRobust ? "robust" : "sim", instance.Build, instance.ConfigSet,
            instance.Database == DatabaseKind.Sqlite ? "sqlite" : "mariadb", instance.Estate?.Name ?? "", instance.Estate?.Owner ?? "",
            instance.Grid?.PublicPort ?? ConfigGenerator.BasePort(regions),
            regions.Select(r => new RegionInfo(r.Name, r.Uuid, r.X, r.Y, r.Size, r.Port)).ToList());
    }

    private static void WriteRegions(TextWriter output, IReadOnlyList<RegionInfo> regions, string indent = "")
    {
        if (regions.Count == 0)
        {
            output.WriteLine($"{indent}No regions.");
            return;
        }
        var lines = new StringWriter();
        Table.Write(lines, ["REGION", "LOCATION", "SIZE", "PORT", "UUID"],
            regions.Select(r => (IReadOnlyList<string>)[r.Name, $"{r.X},{r.Y}", $"{r.Size}", $"{r.Port}", r.Uuid.ToString()]));
        foreach (var line in lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            output.WriteLine(indent + line);
    }

    private static (InstanceStore Store, RegionPlanner Planner) Planner(CliEnvironment env, GlobalOptions globals, ParseResult parse)
    {
        var paths = globals.Paths(parse);
        var store = new InstanceStore(paths);
        return (store, new RegionPlanner(store, new ConfigSetStore(paths), globals.Settings(parse), env.PortProbe));
    }

    private static PlacedRegion Find(RegionPlanner planner, string name, string? instance)
    {
        var matches = planner.AllRegions()
            .Where(p => p.Region.Name == name && (instance is null || p.Instance == instance))
            .ToList();
        return matches switch
        {
            [var one] => one,
            [] => throw new OsctlException(instance is null
                ? $"There is no region '{name}'; 'osctl region list' shows them"
                : $"Instance '{instance}' has no region '{name}'"),
            _ => throw new OsctlException(
                $"Region '{name}' is in more than one instance ({string.Join(", ", matches.Select(m => m.Instance))}); add --instance"),
        };
    }
}
