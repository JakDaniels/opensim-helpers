// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Osctl.Ini;
using Osctl.Instances;

namespace Osctl.Cli;

/// <summary><c>osctl config …</c>: an instance's generated configuration (PLAN.md "Configuration generation").</summary>
internal static class ConfigCommands
{
    public static Command Create(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("config", "An instance's OpenSim configuration, as osctl generates it");
        command.Add(Show(env, globals));
        command.Add(Explain(env, globals));
        command.Add(Diff(env, globals));
        command.Add(Generate(env, globals));
        command.Add(Set(env, globals));
        command.Add(Tree(env, globals));
        return command;
    }

    private static Command Tree(CliEnvironment env, GlobalOptions globals)
    {
        var instance = new Argument<string?>("instance")
        {
            Description = "An instance: the files its configuration is made from, in the order they are read",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var all = new Option<bool>("--all") { Description = "Without an instance: every file under /etc/osctl, not only those osctl reads" };
        var command = new Command("tree",
            "Show the configuration as a tree: the files under /etc/osctl osctl reads, or for an instance the files its configuration is made from")
        { instance, all };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var tree = new ConfigTree(globals.Paths(parse), globals.Settings(parse));
            var name = parse.GetValue(instance);
            if (name is not null && parse.GetValue(all))
                throw new OsctlException("--all goes without an instance (an instance's tree shows only the files it is made from)");
            foreach (var line in Util.TextTree.Render(name is null ? tree.Files(parse.GetValue(all)) : tree.Instance(name)))
                await env.Out.WriteLineAsync(line);
            return 0;
        });
        return command;
    }

    /// <summary>The override file 'osctl config set' writes, in the instance's or the global overrides directory.</summary>
    public const string SetFile = "50-osctl.ini";

    private static Command Set(CliEnvironment env, GlobalOptions globals)
    {
        var section = new Argument<string>("section") { Description = "The section, e.g. Startup" };
        var values = new Argument<string[]>("settings") { Description = "Key=Value settings (Key= removes one)", Arity = ArgumentArity.OneOrMore };
        var target = InstanceCommands.TargetOptions("For this instance (its overrides/)", "For every instance (/etc/osctl/overrides)");
        var command = new Command("set",
            $"Override OpenSim settings, e.g. 'config set Startup MaxPoolThreads=200 --instance A' (written to {SetFile}; takes effect at the next start)")
        { section, values, target.Instance, target.Global };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var store = new InstanceStore(paths);
            var instance = InstanceCommands.Target(parse, store, target);
            var name = parse.GetRequiredValue(section).Trim();
            if (name.Length == 0 || name.Contains('[') || name.Contains(']'))
                throw new OsctlException($"'{name}' is not a section name");
            var pairs = InstanceCommands.Pairs(parse.GetRequiredValue(values));
            foreach (var (key, value) in pairs)
                IniWriter.FormatValue(name, new IniEntry(key, value, null));   // refuses what an ini file cannot hold
            var dir = instance is null ? paths.GlobalOverridesDir : store.OverridesDir(instance);
            var file = Path.Combine(dir, SetFile);
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would set [{name}] {string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"))} in {file}");
                return 0;
            }
            Directory.CreateDirectory(dir);
            if (!File.Exists(file))
                File.WriteAllText(file,
                    "; Written by 'osctl config set' (Key= removes a setting); you may edit it too. Override files are read in\n" +
                    "; alphabetical order and later ones win, so a file named after this one overrides what is here.\n");
            foreach (var (key, value) in pairs)
            {
                if (value.Length == 0)
                    IniFileEditor.RemoveValue(file, name, key);
                else
                    IniFileEditor.SetValue(file, name, key, value);
            }
            await env.Out.WriteLineAsync($"[{name}] updated in {file}; it takes effect at the next start.");

            // A later file in the same directory that sets the same key wins over this one.
            foreach (var later in Directory.GetFiles(dir, "*.ini").Where(f => StringComparer.Ordinal.Compare(Path.GetFileName(f), SetFile) > 0).Order(StringComparer.Ordinal))
            {
                var doc = IniParser.ParseFile(later);
                foreach (var (key, _) in pairs.Where(p => doc.Get(name, p.Key) is not null))
                    await env.Error.WriteLineAsync($"osctl: warning: {later} also sets [{name}] {key}, and wins");
            }
            return 0;
        });
        return command;
    }

    private static Argument<string> InstanceName() => new("instance") { Description = "The instance" };

    private static Option<bool> ShowSecrets() => new("--show-secrets") { Description = "Show passwords instead of ********" };

    private static Command Show(CliEnvironment env, GlobalOptions globals)
    {
        var instance = InstanceName();
        var sections = new Option<string[]>("--section") { Description = "Only these sections", AllowMultipleArgumentsPerToken = true };
        var regions = new Option<bool>("--regions") { Description = "Show the generated Regions.ini instead" };
        var secrets = ShowSecrets();
        var command = new Command("show", "Show the configuration the instance would start with now") { instance, sections, regions, secrets };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var generated = Generator(globals, parse).Generate(parse.GetRequiredValue(instance));
            await WarnAsync(env, generated);
            var doc = parse.GetValue(regions) ? generated.Regions : generated.OpenSim;
            var only = parse.GetValue(sections) is { Length: > 0 } wanted ? wanted.ToHashSet() : null;
            var shown = new IniDocument();
            foreach (var section in doc.Sections.Where(s => only is null || only.Contains(s.Name)))
            {
                foreach (var entry in section.Entries)
                    shown.Set(section.Name, entry.Key, parse.GetValue(secrets) ? entry.Value : SecretMask.Mask(entry.Key, entry.Value));
            }
            await env.Out.WriteAsync(IniWriter.ToText(shown));
            return 0;
        });
        return command;
    }

    private static Command Explain(CliEnvironment env, GlobalOptions globals)
    {
        var instance = InstanceName();
        var section = new Argument<string>("section") { Description = "The section, e.g. Startup" };
        var key = new Argument<string>("key") { Description = "The key, e.g. physics" };
        var secrets = ShowSecrets();
        var command = new Command("explain", "Show which files set a value, in the order they were read") { instance, section, key, secrets };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var generated = Generator(globals, parse).Generate(parse.GetRequiredValue(instance));
            var s = parse.GetRequiredValue(section);
            var k = parse.GetRequiredValue(key);
            string Show(string value) => parse.GetValue(secrets) ? value : SecretMask.Mask(k, value);

            var found = false;
            foreach (var layer in generated.Layers)
            {
                if (layer.Document[s]?.GetEntry(k) is not { } entry)
                    continue;
                found = true;
                var where = layer.Path == ConfigGenerator.OsctlLayer
                    ? "set by osctl"
                    : entry.Origin is { } o ? $"{(layer.Path == layer.ReadFrom ? layer.Path : $"{layer.Path} (from {layer.ReadFrom})")}:{o.Line}" : layer.Path;
                await env.Out.WriteLineAsync($"{where}: {k} = {Show(entry.Value)}");
            }

            var final = generated.OpenSim.Get(s, k);
            if (!found)
            {
                await env.Out.WriteLineAsync($"[{s}] {k} is not set anywhere; OpenSim uses its built-in default.");
                return CliApp.ExitFailure;
            }
            await env.Out.WriteLineAsync(final is null
                ? $"=> [{s}] {k} is removed from the result (osctl follows includes itself)"
                : $"=> [{s}] {k} = {Show(final)}");
            return 0;
        });
        return command;
    }

    private static Command Diff(CliEnvironment env, GlobalOptions globals)
    {
        var instance = InstanceName();
        var secrets = ShowSecrets();
        var command = new Command("diff", "Compare the configuration written at the last start with what would be generated now") { instance, secrets };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var name = parse.GetRequiredValue(instance);
            var generated = Generator(globals, parse).Generate(name);
            var file = Path.Combine(new InstanceStore(paths).GeneratedDir(name), ConfigGenerator.OpenSimFile);
            if (!File.Exists(file))
            {
                await env.Out.WriteLineAsync($"{file} has not been written yet ('osctl config generate {name}', or the first start, writes it).");
                return CliApp.ExitFailure;
            }

            var old = IniParser.ParseFile(file);
            string Show(string key, string value) => parse.GetValue(secrets) ? value : SecretMask.Mask(key, value);
            var changes = 0;
            foreach (var section in old.Sections.Select(s => s.Name).Union(generated.OpenSim.Sections.Select(s => s.Name)))
            {
                var before = old[section];
                var after = generated.OpenSim[section];
                foreach (var key in (before?.Keys ?? []).Union(after?.Keys ?? []))
                {
                    var a = before?[key];
                    var b = after?[key];
                    if (a == b)
                        continue;
                    changes++;
                    if (a is not null)
                        await env.Out.WriteLineAsync($"- [{section}] {key} = {Show(key, a)}");
                    if (b is not null)
                        await env.Out.WriteLineAsync($"+ [{section}] {key} = {Show(key, b)}");
                }
            }
            if (changes == 0)
                await env.Out.WriteLineAsync("No differences.");
            return changes == 0 ? 0 : CliApp.ExitFailure;
        });
        return command;
    }

    private static Command Generate(CliEnvironment env, GlobalOptions globals)
    {
        var instances = new Argument<string[]>("instances") { Description = "The instances (default: all)", Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("generate", "Write instances' configuration files now (a start does this too)") { instances };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var store = new InstanceStore(paths);
            var names = parse.GetValue(instances) is { Length: > 0 } given ? given : [.. store.Names()];
            var generator = Generator(globals, parse);
            foreach (var name in names)
            {
                var generated = generator.Generate(name);
                await WarnAsync(env, generated);
                if (globals.IsDryRun(parse))
                {
                    await env.Out.WriteLineAsync($"would write {store.GeneratedDir(name)}");
                    continue;
                }
                generator.Write(generated);
                await env.Out.WriteLineAsync($"Wrote {store.GeneratedDir(name)} ({generated.Layers.Count} files merged).");
            }
            return 0;
        });
        return command;
    }

    private static ConfigGenerator Generator(GlobalOptions globals, ParseResult parse) =>
        new(globals.Paths(parse), globals.Settings(parse));

    private static async Task WarnAsync(CliEnvironment env, GeneratedConfig generated)
    {
        foreach (var warning in generated.Warnings)
            await env.Error.WriteLineAsync($"osctl: warning: {warning}");
    }
}
