// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Osctl.Builds;
using Osctl.ConfigSets;
using Osctl.Instances;
using Osctl.Running;

namespace Osctl.Cli;

/// <summary>
/// <c>osctl grid …</c> (a grid of your own: a Robust instance and the config set its simulators use) and
/// <c>osctl user …</c> (accounts on a grid or a standalone); PLAN.md "Robust and local grids".
/// </summary>
internal static class GridCommands
{
    private const string PasswordCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static Command Create(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("grid", "Grids of your own: Robust services on this machine, with simulator instances on them");
        command.Add(Add(env, globals));
        command.Add(GridList(env, globals));
        command.Add(Show(env, globals));
        command.Add(Set(env, globals));
        return command;
    }

    private static Argument<string> GridName() => new("grid") { Description = "The grid's name (letters, digits, _ and -): also its Robust instance's" };

    private static Grids GridsOf(CliEnvironment env, GlobalOptions globals, ParseResult parse) =>
        new(globals.Paths(parse), globals.Settings(parse), env.PortProbe);

    private static Command Add(CliEnvironment env, GlobalOptions globals)
    {
        var name = GridName();
        var build = new Option<string?>("--build") { Description = "The build Robust runs (default: [Defaults] Build in osctl.ini, or the only build)" };
        var host = new Option<string>("--host") { Description = "The public host name or address viewers and other grids reach it at", Required = true };
        var gridName = new Option<string?>("--name") { Description = "The grid's long name, as viewers show it (default: the grid's name)" };
        var nick = new Option<string?>("--nick") { Description = "The grid's short name (default: the grid's name in lower case)" };
        var publicPort = new Option<int?>("--public-port") { Description = "Login, grid info and Hypergrid (default: the first free pair in [Ports] RobustPortMin–Max)" };
        var privatePort = new Option<int?>("--private-port") { Description = "The simulators' services; keep it off the internet (default: public port + 1)" };
        var assetsDir = AssetsDirOption();
        var command = new Command("add", "Create a grid: a Robust instance, and a config set of the same name for its simulators")
            { name, build, host, gridName, nick, publicPort, privatePort, assetsDir };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var settings = globals.Settings(parse);
            var store = new InstanceStore(paths);
            var sets = new ConfigSetStore(paths);
            var builds = new BuildStore(paths);
            var grids = GridsOf(env, globals, parse);

            var grid = Names.CheckInstance(parse.GetRequiredValue(name));
            if (store.Exists(grid) || store.AnyOnDisk(grid))
                throw new OsctlException($"There is already an instance '{grid}'");
            if (sets.Exists(grid))
                throw new OsctlException($"There is already a config set '{grid}' ({sets.DefinitionFile(grid)})");

            var buildName = parse.GetValue(build) ?? settings.Defaults.Build
                ?? (builds.Names() is [var only] ? only
                    : throw new OsctlException("Which build? Give --build (or set [Defaults] Build in osctl.ini)"));
            if (!builds.Exists(buildName))
                throw new OsctlException($"There is no build '{buildName}'; 'osctl build list' shows the builds");
            var robustSet = sets.Load(Grids.RobustConfigSet);
            if (robustSet.Mode != ConfigSetMode.Robust)
                throw new OsctlException($"Config set '{Grids.RobustConfigSet}' is not a Robust one (Mode = robust)");

            int pub, priv;
            if (parse.GetValue(publicPort) is { } p)
            {
                grids.CheckPort(p);
                pub = p;
                priv = parse.GetValue(privatePort) ?? p + 1;
                grids.CheckPort(priv);
            }
            else if (parse.GetValue(privatePort) is not null)
            {
                throw new OsctlException("--private-port needs --public-port too");
            }
            else
            {
                (pub, priv) = grids.AllocatePorts();
            }
            if (pub == priv)
                throw new OsctlException("The public and private ports must differ");

            var definition = new GridDefinition(InstanceDefinition.CheckHost(parse.GetRequiredValue(host).Trim()), pub, priv,
                CheckText(parse.GetValue(gridName) ?? grid, "--name"), CheckText(parse.GetValue(nick) ?? grid.ToLowerInvariant(), "--nick"),
                AssetsDir: CheckAssetsDir(parse.GetValue(assetsDir)));
            var instance = new InstanceDefinition(grid, buildName, robustSet.Name, DatabaseKind.MariaDb, null,
                Kind: InstanceKind.Robust, Grid: definition);

            var generator = new ConfigGenerator(paths, settings);
            var databases = new[] { generator.DatabaseName(grid), generator.EstateDatabaseName(grid) };
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would create grid '{grid}' in {store.ConfigDir(grid)}:");
                await env.Out.WriteAsync(Ini.IniWriter.ToText(instance.ToIni()));
                await env.Out.WriteLineAsync($"would create the databases {string.Join(" and ", databases)}");
                await env.Out.WriteLineAsync($"would write the config set {sets.DefinitionFile(grid)}");
                return 0;
            }

            var admin = env.DatabaseFactory.Admin(settings.Database);
            foreach (var database in databases)
            {
                if (!await admin.ExistsAsync(database, ct))
                {
                    await admin.CreateAsync(database, ct);
                    await env.Out.WriteLineAsync($"Created database {database}.");
                }
            }
            store.Create(instance, null, RandomNumberGenerator.GetString(PasswordCharacters, 32));
            Directory.CreateDirectory(paths.ConfigSetsDir);
            await File.WriteAllTextAsync(sets.DefinitionFile(grid), Grids.ConfigSetText(grid), ct);

            await env.Out.WriteLineAsync($"Grid '{grid}' created: {definition.PublicUri} (private port {priv}), build {buildName}.");
            await env.Out.WriteLineAsync("Next:");
            await env.Out.WriteLineAsync($"  osctl start {grid}");
            await env.Out.WriteLineAsync($"  osctl user create {grid} \"First Last\"");
            await env.Out.WriteLineAsync($"  osctl instance add <sim> --configset {grid} --estate-owner \"First Last\"");
            await env.Out.WriteLineAsync($"  osctl region add <region> --instance <sim> --location <x,y>   (the first becomes the default region)");
            await env.Out.WriteLineAsync($"Port {pub} must be reachable from outside, and port {priv} must not be; 'osctl grid show {grid}' lists every port.");
            return 0;
        });
        return command;
    }

    private static Option<string?> AssetsDirOption() =>
        new("--assets-dir") { Description = "Where FSAssets keeps the grid's asset files (default: the grid instance's data/fsassets)" };

    private static string? CheckAssetsDir(string? dir) =>
        dir is null ? null
        : Path.IsPathRooted(dir) && !dir.Contains('"') ? Path.TrimEndingDirectorySeparator(dir)
        : throw new OsctlException("--assets-dir: must be an absolute path");

    private static string CheckText(string text, string option)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Contains('"') || text.Contains('\n'))
            throw new OsctlException($"{option}: must not be empty or contain a double quote");
        return text;
    }

    internal sealed record GridInfo(
        string Name, string Host, int PublicPort, int PrivatePort, string GridName, string Nick, string? DefaultRegion,
        string LoginUri, IReadOnlyList<string> Simulators);

    private static GridInfo Describe(Grids grids, InstanceDefinition robust)
    {
        var g = robust.GridSettings;
        return new GridInfo(robust.Name, g.Host, g.PublicPort, g.PrivatePort, g.Name, g.Nick, g.DefaultRegion, g.PublicUri + "/",
            grids.Simulators(robust.Name).Select(s => s.Name).ToList());
    }

    private static Command GridList(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print the grids as JSON" };
        var command = new Command("list", "List the grids") { json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var grids = GridsOf(env, globals, parse);
            var infos = grids.All().Select(r => Describe(grids, r)).ToList();
            if (parse.GetValue(json))
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(infos, CliApp.Json));
            else if (infos.Count == 0)
                await env.Out.WriteLineAsync("No grids yet: 'osctl grid add <grid> --host <name> …' creates one.");
            else
                Table.Write(env.Out, ["GRID", "LOGIN URI", "PRIVATE", "NAME", "DEFAULT REGION", "SIMULATORS"],
                    infos.Select(i => (IReadOnlyList<string>)
                        [i.Name, i.LoginUri, i.PrivatePort.ToString(), i.GridName, i.DefaultRegion ?? "-", string.Join(", ", i.Simulators)]));
            return 0;
        });
        return command;
    }

    private static Command Show(CliEnvironment env, GlobalOptions globals)
    {
        var name = GridName();
        var json = new Option<bool>("--json") { Description = "Print the grid as JSON" };
        var command = new Command("show", "Show a grid, its simulators and the ports to open") { name, json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var grids = GridsOf(env, globals, parse);
            var store = new InstanceStore(globals.Paths(parse));
            var robust = grids.Load(parse.GetRequiredValue(name));
            var info = Describe(grids, robust);
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(info, CliApp.Json));
                return 0;
            }
            await env.Out.WriteLineAsync($"Grid:           {info.Name} (Robust instance, build {robust.Build})");
            await env.Out.WriteLineAsync($"Name:           {info.GridName} ({info.Nick})");
            await env.Out.WriteLineAsync($"Login URI:      {info.LoginUri}");
            await env.Out.WriteLineAsync($"Private port:   {info.PrivatePort} (this machine only)");
            await env.Out.WriteLineAsync($"Assets:         {new ConfigGenerator(globals.Paths(parse), globals.Settings(parse)).AssetsDir(robust)} (FSAssets)");
            await env.Out.WriteLineAsync($"Default region: {info.DefaultRegion ?? "- (nobody can log in for the first time: 'osctl grid set " + info.Name + " --default-region <region>')"}");
            await env.Out.WriteLineAsync();
            await env.Out.WriteLineAsync("Ports to open to the internet (forward them to this machine):");
            await env.Out.WriteLineAsync($"  {info.PublicPort}/tcp  Robust: login, grid info, map, Hypergrid");
            foreach (var sim in grids.Simulators(info.Name))
            {
                var regions = store.Regions(sim.Name);
                if (regions.Count == 0)
                    continue;
                await env.Out.WriteLineAsync($"  {ConfigGenerator.BasePort(regions)}/tcp  {sim.Name}: HTTP");
                foreach (var region in regions)
                    await env.Out.WriteLineAsync($"  {region.Port}/udp  {sim.Name}: region {region.Name}");
            }
            await env.Out.WriteLineAsync($"Keep {info.PrivatePort}/tcp closed: it is the grid's services, unprotected.");
            return 0;
        });
        return command;
    }

    private static Command Set(CliEnvironment env, GlobalOptions globals)
    {
        var name = GridName();
        var host = new Option<string?>("--host") { Description = "The public host name or address" };
        var gridName = new Option<string?>("--name") { Description = "The grid's long name" };
        var nick = new Option<string?>("--nick") { Description = "The grid's short name" };
        var defaultRegion = new Option<string?>("--default-region") { Description = "The region new avatars and Hypergrid visitors arrive in" };
        var publicPort = new Option<int?>("--public-port") { Description = "The public port" };
        var privatePort = new Option<int?>("--private-port") { Description = "The private port" };
        var assetsDir = AssetsDirOption();
        var command = new Command("set", "Change a grid's settings (they take effect when it and its simulators restart)")
            { name, host, gridName, nick, defaultRegion, publicPort, privatePort, assetsDir };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var grids = GridsOf(env, globals, parse);
            var store = new InstanceStore(globals.Paths(parse));
            var robust = grids.Load(parse.GetRequiredValue(name));
            var g = robust.GridSettings;
            if (parse.GetValue(host) is { } h)
                g = g with { Host = InstanceDefinition.CheckHost(h.Trim()) };
            if (parse.GetValue(gridName) is { } n)
                g = g with { Name = CheckText(n, "--name") };
            if (parse.GetValue(nick) is { } k)
                g = g with { Nick = CheckText(k, "--nick") };
            if (parse.GetValue(publicPort) is { } pp && pp != g.PublicPort)
            {
                grids.CheckPort(pp, robust.Name);
                g = g with { PublicPort = pp };
            }
            if (parse.GetValue(privatePort) is { } vp && vp != g.PrivatePort)
            {
                grids.CheckPort(vp, robust.Name);
                g = g with { PrivatePort = vp };
            }
            if (g.PublicPort == g.PrivatePort)
                throw new OsctlException("The public and private ports must differ");
            if (CheckAssetsDir(parse.GetValue(assetsDir)) is { } dir && dir != g.AssetsDir)
            {
                g = g with { AssetsDir = dir };
                await env.Out.WriteLineAsync("The asset files are not moved: move the old directory's contents there while the grid is stopped.");
            }
            if (parse.GetValue(defaultRegion) is { } region)
            {
                var found = grids.Simulators(robust.Name).SelectMany(s => store.Regions(s.Name))
                    .FirstOrDefault(r => string.Equals(r.Name, region, StringComparison.OrdinalIgnoreCase))
                    ?? throw new OsctlException($"Grid '{robust.Name}' has no region '{region}'");
                g = g with { DefaultRegion = found.Name };
            }
            if (g == robust.GridSettings)
            {
                await env.Out.WriteLineAsync("Nothing to change.");
                return 0;
            }
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would change grid '{robust.Name}':");
                await env.Out.WriteAsync(Ini.IniWriter.ToText((robust with { Grid = g }).ToIni()));
                return 0;
            }
            store.Save(robust with { Grid = g });
            await env.Out.WriteLineAsync($"Grid '{robust.Name}' changed; restart it ('osctl restart {robust.Name}') and, " +
                "if the host or ports changed, its simulators too.");
            return 0;
        });
        return command;
    }

    // ---------------------------------------------------------------- users

    public static Command Users(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("user", "Avatar accounts on a grid or a standalone (which must be running)");
        command.Add(UserCreate(env, globals));
        command.Add(UserPassword(env, globals));
        return command;
    }

    private static Argument<string> AccountInstance() =>
        new("instance") { Description = "A grid, a standalone, or a simulator on one of your grids (its grid is used)" };

    private static Argument<string> AvatarName() => new("avatar") { Description = "The avatar's name, \"First Last\"" };

    /// <summary>Where accounts for <paramref name="name"/> live: a grid, or a standalone; a simulator on a local grid means its grid.</summary>
    private static InstanceDefinition AccountsOf(CliEnvironment env, GlobalOptions globals, ParseResult parse, string name)
    {
        var paths = globals.Paths(parse);
        var store = new InstanceStore(paths);
        var instance = store.Load(name);
        if (instance.IsRobust)
            return instance;
        if (GridsOf(env, globals, parse).GridOf(instance) is { } grid)
            return grid;
        var sets = new ConfigSetStore(paths);
        if (sets.Exists(instance.ConfigSet) && sets.Load(instance.ConfigSet).Mode == ConfigSetMode.Standalone)
            return instance;
        throw new OsctlException($"Instance '{name}' is on another grid; accounts are made there");
    }

    private static async Task<string> ConsoleAsync(CliEnvironment env, GlobalOptions globals, ParseResult parse, InstanceDefinition target,
        string command, IReadOnlyList<(string, string)> answers, CancellationToken ct)
    {
        var store = new InstanceStore(globals.Paths(parse));
        var tmux = new Tmux(env.Runner, store.TmuxSocket(target.Name), target.Name);
        if (!await tmux.IsRunningAsync(ct))
            throw new OsctlException($"'{target.Name}' is not running ('osctl start {target.Name}')");
        var output = await ConsoleCommand.RunAsync(tmux, store.ConsoleLog(target.Name), command, TimeSpan.FromSeconds(60), env.Clock, ct, answers);
        // The questions answered are part of the output: leave them out.
        return string.Join('\n', output.Split('\n').Where(l => !answers.Any(a => l.TrimStart().StartsWith(a.Item1, StringComparison.Ordinal))));
    }

    private static (string First, string Last) SplitName(string avatar)
    {
        var parts = InstanceDefinition.CheckOwner(avatar.Trim()).Split(' ');
        foreach (var part in parts)
        {
            if (part.IndexOfAny(['@', '.', ':']) >= 0)
                throw new OsctlException($"'{avatar}': names cannot contain @ . or : (they would read as Hypergrid addresses)");
        }
        return (parts[0], parts[1]);
    }

    private static string Password(Option<string?> option, ParseResult parse, out bool generated)
    {
        generated = false;
        if (parse.GetValue(option) is { } given)
        {
            if (given.Length == 0 || given.Contains('"') || given.Any(char.IsWhiteSpace))
                throw new OsctlException("--password: must not be empty or contain spaces or double quotes");
            return given;
        }
        generated = true;
        return RandomNumberGenerator.GetString(PasswordCharacters, 16);
    }

    private static Command UserCreate(CliEnvironment env, GlobalOptions globals)
    {
        var instance = AccountInstance();
        var avatar = AvatarName();
        var password = new Option<string?>("--password") { Description = "The password (default: generated and shown once)" };
        var email = new Option<string?>("--email") { Description = "The e-mail address" };
        var command = new Command("create", "Create an avatar account") { instance, avatar, password, email };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var target = AccountsOf(env, globals, parse, parse.GetRequiredValue(instance));
            var (first, last) = SplitName(parse.GetRequiredValue(avatar));
            var mail = parse.GetValue(email)?.Trim() ?? "";
            if (mail.Contains(' ') || mail.Contains('"'))
                throw new OsctlException("--email: not an e-mail address");
            var secret = Password(password, parse, out var generated);
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would create the account '{first} {last}' on '{target.Name}'");
                return 0;
            }
            var output = await ConsoleAsync(env, globals, parse, target, $"create user {first} {last}",
                [("Password", secret), ("Email", mail), ("User ID", ""), ("Model name", "")], ct);
            if (output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                throw new OsctlException($"'{first} {last}' already has an account on '{target.Name}'");
            if (!output.Contains("created", StringComparison.OrdinalIgnoreCase))
                throw new OsctlException($"'{target.Name}' did not say the account was created: {output.Trim()}");
            await env.Out.WriteLineAsync($"Account '{first} {last}' created on '{target.Name}'.");
            if (generated)
                await env.Out.WriteLineAsync($"Its password is {secret} (shown once).");
            return 0;
        });
        return command;
    }

    private static Command UserPassword(CliEnvironment env, GlobalOptions globals)
    {
        var instance = AccountInstance();
        var avatar = AvatarName();
        var password = new Option<string?>("--password") { Description = "The new password (default: generated and shown once)" };
        var command = new Command("password", "Set an avatar's password") { instance, avatar, password };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var target = AccountsOf(env, globals, parse, parse.GetRequiredValue(instance));
            var (first, last) = SplitName(parse.GetRequiredValue(avatar));
            var secret = Password(password, parse, out var generated);
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would set the password of '{first} {last}' on '{target.Name}'");
                return 0;
            }
            var output = await ConsoleAsync(env, globals, parse, target, $"reset user password {first} {last}", [("New password", secret)], ct);
            if (output.Contains("No such user", StringComparison.OrdinalIgnoreCase))
                throw new OsctlException($"'{first} {last}' has no account on '{target.Name}'");
            if (!output.Contains("reset", StringComparison.OrdinalIgnoreCase) || output.Contains("Unable", StringComparison.OrdinalIgnoreCase))
                throw new OsctlException($"'{target.Name}' did not say the password was set: {output.Trim()}");
            await env.Out.WriteLineAsync($"Password of '{first} {last}' set on '{target.Name}'.");
            if (generated)
                await env.Out.WriteLineAsync($"It is {secret} (shown once).");
            return 0;
        });
        return command;
    }
}
