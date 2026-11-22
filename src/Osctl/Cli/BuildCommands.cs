// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Osctl.Builds;

namespace Osctl.Cli;

/// <summary><c>osctl build …</c> (PLAN.md "Builds").</summary>
internal static class BuildCommands
{
    public static Command Create(CliEnvironment env, GlobalOptions globals)
    {
        var build = new Command("build", "Named OpenSim builds: git source, addons, patches, and the releases built from them");
        build.Add(Add(env, globals));
        build.Add(Addon(env, globals));
        build.Add(List(env, globals));
        build.Add(Show(env, globals));
        build.Add(Update(env, globals));
        build.Add(Rollback(env, globals));
        build.Add(Remove(env, globals));
        return build;
    }

    private static Argument<string> BuildName() => new("name") { Description = "The build's name, e.g. master or sas-dev" };

    private static Command Add(CliEnvironment env, GlobalOptions globals)
    {
        var name = BuildName();
        var repo = new Option<string>("--repo") { Description = "The OpenSim git repository", Required = true };
        var gitRef = new Option<string>("--ref") { Description = "Branch, tag or commit (default: the repository's default branch)" };
        var patches = new Option<string?>("--patches") { Description = "A directory of *.patch files to apply (relative to /etc/osctl/builds)" };
        var keep = new Option<int>("--keep") { Description = "How many releases to keep", DefaultValueFactory = _ => BuildDefinition.DefaultKeepReleases };
        var command = new Command("add", "Define a new build (then 'osctl build update' builds it)") { name, repo, gitRef, patches, keep };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var buildName = Names.Check(parse.GetRequiredValue(name), "build");
            if (store.Exists(buildName))
                throw new OsctlException($"Build '{buildName}' already exists ({store.DefinitionFile(buildName)})");
            if (parse.GetValue(keep) < 1)
                throw new OsctlException("--keep must be 1 or more");

            var definition = new BuildDefinition(buildName, Repository(parse.GetRequiredValue(repo)), [],
                parse.GetValue(gitRef) ?? BuildDefinition.DefaultRef, parse.GetValue(patches), parse.GetValue(keep));
            await SaveAsync(env, globals, parse, store, definition);
            if (!globals.IsDryRun(parse))
                await env.Out.WriteLineAsync($"Build '{buildName}' defined. 'osctl build update {buildName}' builds it.");
            return 0;
        });
        return command;
    }

    private static Command Addon(CliEnvironment env, GlobalOptions globals)
    {
        var addon = new Command("addon", "Addon modules built into a build (copied into addon-modules/)");

        var build = BuildName();
        var addonName = new Argument<string>("addon") { Description = "The addon's name (its directory in addon-modules/)" };
        var repo = new Option<string>("--repo") { Description = "The addon's git repository", Required = true };
        var gitRef = new Option<string>("--ref") { Description = "Branch, tag or commit (default: the repository's default branch)" };
        var patches = new Option<string?>("--patches") { Description = "A directory in the addon whose *.patch files apply to OpenSim, e.g. core-patches" };
        var add = new Command("add", "Add an addon to a build") { build, addonName, repo, gitRef, patches };
        CliApp.SetAction(add, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var definition = store.Load(parse.GetRequiredValue(build));
            var newName = Names.Check(parse.GetRequiredValue(addonName), "addon");
            if (definition.Addons.Any(a => a.Name == newName))
                throw new OsctlException($"Build '{definition.Name}' already has addon '{newName}'");

            var updated = definition with
            {
                Addons = [.. definition.Addons, new AddonDefinition(newName, Repository(parse.GetRequiredValue(repo)),
                    parse.GetValue(gitRef) ?? BuildDefinition.DefaultRef, parse.GetValue(patches))],
            };
            await SaveAsync(env, globals, parse, store, updated);
            return 0;
        });

        var removeBuild = BuildName();
        var removeName = new Argument<string>("addon") { Description = "The addon to remove" };
        var remove = new Command("remove", "Remove an addon from a build (its next update builds without it)") { removeBuild, removeName };
        CliApp.SetAction(remove, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var definition = store.Load(parse.GetRequiredValue(removeBuild));
            var gone = parse.GetRequiredValue(removeName);
            if (definition.Addons.All(a => a.Name != gone))
                throw new OsctlException($"Build '{definition.Name}' has no addon '{gone}'");
            await SaveAsync(env, globals, parse, store, definition with { Addons = [.. definition.Addons.Where(a => a.Name != gone)] });
            return 0;
        });

        addon.Add(add);
        addon.Add(remove);
        return addon;
    }

    private static Command List(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print the builds as JSON" };
        var command = new Command("list", "List the builds and their current releases") { json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var infos = store.Names().Select(n => Describe(store, store.Load(n))).ToList();

            if (parse.GetValue(json))
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(infos, CliApp.Json));
            else if (infos.Count == 0)
                await env.Out.WriteLineAsync("No builds yet: 'osctl build add <name> --repo <url>' defines one.");
            else
                Table.Write(env.Out, ["BUILD", "REF", "CURRENT", "COMMIT", "RELEASES", "REPOSITORY"],
                    infos.Select(i => (IReadOnlyList<string>)
                        [i.Name, i.Ref, i.Current ?? "-", i.Commit ?? "-", i.Releases.Count.ToString(), i.Repository]));
            return 0;
        });
        return command;
    }

    private static Command Show(CliEnvironment env, GlobalOptions globals)
    {
        var name = BuildName();
        var json = new Option<bool>("--json") { Description = "Print the build as JSON" };
        var command = new Command("show", "Show a build's definition and releases") { name, json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var info = Describe(store, store.Load(parse.GetRequiredValue(name)));
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(info, CliApp.Json));
                return 0;
            }

            var o = env.Out;
            await o.WriteLineAsync($"Build:      {info.Name} ({store.DefinitionFile(info.Name)})");
            await o.WriteLineAsync($"Repository: {info.Repository}");
            await o.WriteLineAsync($"Ref:        {info.Ref}");
            if (info.Patches is not null)
                await o.WriteLineAsync($"Patches:    {info.Patches}");
            foreach (var addon in info.Addons)
                await o.WriteLineAsync($"Addon:      {addon.Name}: {addon.Repository} ({addon.Ref}{(addon.Patches is null ? "" : $", patches {addon.Patches}")})");
            await o.WriteLineAsync($"Keeps:      {info.KeepReleases} releases");
            await o.WriteLineAsync("Releases:");
            if (info.Releases.Count == 0)
                await o.WriteLineAsync("  none yet ('osctl build update' builds one)");
            foreach (var r in info.Releases)
                await o.WriteLineAsync($"  {(r.Current ? "*" : " ")} {r.Name}  {r.Subject}");
            return 0;
        });
        return command;
    }

    private static Command Update(CliEnvironment env, GlobalOptions globals)
    {
        var names = new Argument<string[]>("names") { Description = "The builds to update (default: all)", Arity = ArgumentArity.ZeroOrMore };
        var force = new Option<bool>("--force") { Description = "Build a new release even if nothing has changed" };
        var verbose = new Option<bool>("--verbose") { Description = "Show the prebuild and compiler output as well as logging it" };
        var restart = new Option<bool>("--restart") { Description = "Restart the running instances on a build that got a new release" };
        var command = new Command("update", "Fetch and build new releases; instances use them from their next start") { names, force, verbose, restart };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var store = new BuildStore(paths);
            var selected = parse.GetValue(names) is { Length: > 0 } given ? given : [.. store.Names()];
            if (selected.Length == 0)
                throw new OsctlException("No builds yet: 'osctl build add <name> --repo <url>' defines one");

            var updater = new BuildUpdater(globals.Runner(parse, env), paths, env.Out, env.Clock,
                globals.IsDryRun(parse), parse.GetValue(verbose));
            var instances = new Instances.InstanceStore(paths);
            var failures = 0;
            foreach (var buildName in selected)
            {
                var result = await updater.UpdateAsync(store.Load(buildName), parse.GetValue(force), ct);
                if (!parse.GetValue(restart) || result.Outcome != BuildUpdateOutcome.Built)
                    continue;

                var supervisor = RunCommands.Supervisor(env, globals, parse);
                foreach (var name in instances.Names().Where(n => instances.Load(n).Build == buildName))
                {
                    if ((await supervisor.StatusAsync(name, ct)).State is not (Running.InstanceState.Running or Running.InstanceState.Starting))
                        continue;
                    await env.Out.WriteAsync($"Restarting {name} on release {result.Release} … ");
                    try
                    {
                        await supervisor.RestartAsync(name, ct);
                        await env.Out.WriteLineAsync("restarted");
                    }
                    catch (OsctlException e)
                    {
                        failures++;
                        await env.Out.WriteLineAsync("FAILED");
                        await env.Error.WriteLineAsync($"osctl: {e.Message}");
                    }
                }
            }
            return failures == 0 ? 0 : CliApp.ExitFailure;
        });
        return command;
    }

    private static Command Rollback(CliEnvironment env, GlobalOptions globals)
    {
        var name = BuildName();
        var to = new Option<string?>("--to") { Description = "The release to go back to (default: the one before the current)" };
        var command = new Command("rollback", "Make an earlier release current; instances use it from their next start") { name, to };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var definition = store.Load(parse.GetRequiredValue(name));
            var releases = store.Releases(definition.Name);
            var current = store.Current(definition.Name);

            string target;
            if (parse.GetValue(to) is { } wanted)
            {
                target = releases.Contains(wanted) ? wanted
                    : throw new OsctlException($"Build '{definition.Name}' has no release '{wanted}'; 'osctl build show {definition.Name}' lists them");
            }
            else
            {
                var index = current is null ? -1 : releases.ToList().IndexOf(current);
                target = index > 0 ? releases[index - 1]
                    : throw new OsctlException($"Build '{definition.Name}' has no release older than {current ?? "(none)"}");
            }

            if (target == current)
            {
                await env.Out.WriteLineAsync($"Release {target} is already current.");
                return 0;
            }
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would make release {target} of build '{definition.Name}' current (now {current})");
                return 0;
            }
            store.SetCurrent(definition.Name, target);
            await env.Out.WriteLineAsync($"Release {target} of build '{definition.Name}' is now current (was {current}).");
            return 0;
        });
        return command;
    }

    private static Command Remove(CliEnvironment env, GlobalOptions globals)
    {
        var name = BuildName();
        var yes = new Option<bool>("--yes") { Description = "Really remove it" };
        var command = new Command("remove", "Remove a build: its definition, source, and all its releases") { name, yes };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new BuildStore(globals.Paths(parse));
            var definition = store.Load(parse.GetRequiredValue(name));
            if (globals.IsDryRun(parse) || !parse.GetValue(yes))
            {
                await env.Out.WriteLineAsync($"would remove {store.DefinitionFile(definition.Name)} and {store.Dir(definition.Name)}"
                    + $" ({store.Releases(definition.Name).Count} release(s))");
                if (!globals.IsDryRun(parse))
                {
                    await env.Out.WriteLineAsync("Add --yes to remove it.");
                    return CliApp.ExitFailure;
                }
                return 0;
            }
            // Holding the lock means no update is running; on Linux the lock file may be removed while held.
            using (store.Lock(definition.Name))
                store.Remove(definition.Name);
            await env.Out.WriteLineAsync($"Build '{definition.Name}' removed.");
            return 0;
        });
        return command;
    }

    /// <summary>A repository that is a local directory is stored as its full path; URLs are kept as given.</summary>
    private static string Repository(string repo) => Directory.Exists(repo) ? Path.GetFullPath(repo) : repo;

    private static async Task SaveAsync(CliEnvironment env, GlobalOptions globals, ParseResult parse, BuildStore store, BuildDefinition definition)
    {
        if (globals.IsDryRun(parse))
        {
            await env.Out.WriteLineAsync($"would write {store.DefinitionFile(definition.Name)}:");
            await env.Out.WriteAsync(Ini.IniWriter.ToText(definition.ToIni()));
            return;
        }
        store.Save(definition);
    }

    internal sealed record ReleaseInfo(string Name, bool Current, string? Commit, string? Subject, DateTimeOffset? Created);

    internal sealed record BuildInfo(
        string Name,
        string Repository,
        string Ref,
        string? Patches,
        int KeepReleases,
        IReadOnlyList<AddonDefinition> Addons,
        string? Current,
        string? Commit,
        IReadOnlyList<ReleaseInfo> Releases);

    private static BuildInfo Describe(BuildStore store, BuildDefinition definition)
    {
        var current = store.Current(definition.Name);
        var releases = store.Releases(definition.Name)
            .Select(r =>
            {
                var m = store.Manifest(definition.Name, r);
                return new ReleaseInfo(r, r == current, m?.Commit, m?.Subject, m?.Created);
            })
            .ToList();
        var currentCommit = releases.FirstOrDefault(r => r.Current)?.Commit;
        return new BuildInfo(definition.Name, definition.Repository, definition.Ref, definition.Patches, definition.KeepReleases,
            definition.Addons, current, currentCommit is { Length: > 10 } c ? c[..10] : currentCommit, releases);
    }
}
