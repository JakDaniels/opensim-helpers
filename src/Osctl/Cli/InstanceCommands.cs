// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
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
            if (store.Exists(instanceName) || Directory.Exists(store.Dir(instanceName)))
                throw new OsctlException($"Instance '{instanceName}' already exists ({store.Dir(instanceName)})");

            var buildName = parse.GetValue(build) ?? settings.Defaults.Build
                ?? (builds.Names() is [var only] ? only
                    : throw new OsctlException("Which build? Give --build (or set [Defaults] Build in osctl.ini)"));
            if (!builds.Exists(buildName))
                throw new OsctlException($"There is no build '{buildName}'; 'osctl build list' shows the builds");

            var set = sets.Load(parse.GetValue(configSet) ?? settings.Defaults.ConfigSet);
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
                await env.Out.WriteLineAsync($"would create instance '{instanceName}' in {store.Dir(instanceName)}:");
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
            await env.Out.WriteLineAsync($"Instance:   {info.Name} ({store.Dir(info.Name)})");
            await env.Out.WriteLineAsync($"Build:      {info.Build}");
            await env.Out.WriteLineAsync($"Config set: {info.ConfigSet}");
            await env.Out.WriteLineAsync($"Database:   {info.Database}");
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
            if (globals.IsDryRun(parse) || !parse.GetValue(yes))
            {
                await env.Out.WriteLineAsync($"would delete instance '{instance.Name}' ({regions.Count} region(s)): {store.Dir(instance.Name)} and {store.LogDir(instance.Name)}");
                if (globals.IsDryRun(parse))
                    return 0;
                await env.Out.WriteLineAsync("Add --yes to delete it.");
                return CliApp.ExitFailure;
            }
            if (instance.Database == DatabaseKind.MariaDb && !parse.GetValue(keepDatabase))
                await DropDatabasesAsync(env, globals, parse, instance, ct);
            store.Delete(instance.Name);
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
        if (sets.Exists(instance.ConfigSet) && sets.Load(instance.ConfigSet) is { Mode: ConfigSetMode.Standalone } set)
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
        command.Add(RegionSet(env, globals));
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
            foreach (var pair in parse.GetRequiredValue(values))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    throw new OsctlException($"'{pair}': expected Key=Value");
                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                if (key is "RegionUUID" or "Location" or "InternalPort" or "SizeX" or "SizeY" || key.Contains(' '))
                    throw new OsctlException($"{key}: use 'osctl region add/move/rename' for a region's UUID, location, port and size");
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
                await env.Out.WriteLineAsync($"{info.Name}  (build {info.Build}, {info.ConfigSet}, {info.Database}, port {(info.Port == 0 ? "-" : info.Port.ToString())})");
                WriteRegions(env.Out, info.Regions, indent: "  ");
            }
            return 0;
        });
        return command;
    }

    // ---------------------------------------------------------------- helpers

    internal sealed record RegionInfo(string Name, Guid Uuid, int X, int Y, int Size, int Port);

    internal sealed record InstanceInfo(
        string Name, string Build, string ConfigSet, string Database, string Estate, string EstateOwner, int Port, IReadOnlyList<RegionInfo> Regions);

    private static InstanceInfo Describe(InstanceStore store, string name)
    {
        var instance = store.Load(name);
        var regions = store.Regions(name);
        return new InstanceInfo(instance.Name, instance.Build, instance.ConfigSet,
            instance.Database == DatabaseKind.Sqlite ? "sqlite" : "mariadb", instance.Estate.Name, instance.Estate.Owner,
            ConfigGenerator.BasePort(regions),
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
