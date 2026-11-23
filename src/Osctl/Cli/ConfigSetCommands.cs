// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Osctl.ConfigSets;

namespace Osctl.Cli;

/// <summary><c>osctl configset …</c> (PLAN.md "Config sets and grids").</summary>
internal static class ConfigSetCommands
{
    public static Command Create(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("configset", "The base configurations instances start from (standalone, osgrid, …)");
        command.Add(List(env, globals));
        command.Add(Show(env, globals));
        command.Add(Fetch(env, globals));
        return command;
    }

    private static Command List(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print the config sets as JSON" };
        var command = new Command("list", "List the config sets") { json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new ConfigSetStore(globals.Paths(parse));
            var sets = store.Names().Select(store.Load).ToList();
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(
                    sets.Select(s => new { s.Name, s.Description, s.Mode, s.Grid, Fetched = store.IsFetched(s) }), CliApp.Json));
                return 0;
            }
            if (sets.Count == 0)
            {
                await env.Out.WriteLineAsync("No config sets: 'osctl init' installs the shipped ones.");
                return 0;
            }
            Table.Write(env.Out, ["CONFIGSET", "MODE", "GRID", "FETCHED", "DESCRIPTION"],
                sets.Select(s => (IReadOnlyList<string>)
                [
                    s.Name,
                    s.Mode.ToString().ToLowerInvariant(),
                    s.Grid ?? "(own)",
                    s.Files.Any(f => f.Kind == FileSourceKind.Url) ? (store.IsFetched(s) ? "yes" : "no") : "-",
                    s.Description,
                ]));
            return 0;
        });
        return command;
    }

    private static Command Show(CliEnvironment env, GlobalOptions globals)
    {
        var name = new Argument<string>("name") { Description = "The config set" };
        var command = new Command("show", "Show where a config set's files come from") { name };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new ConfigSetStore(globals.Paths(parse));
            var set = store.Load(parse.GetRequiredValue(name));
            await env.Out.WriteLineAsync($"Config set: {set.Name} ({store.DefinitionFile(set.Name)})");
            await env.Out.WriteLineAsync($"            {set.Description}");
            await env.Out.WriteLineAsync($"Mode:       {set.Mode.ToString().ToLowerInvariant()}");
            await env.Out.WriteLineAsync($"Grid:       {set.Grid ?? "each instance is its own"}");
            await env.Out.WriteLineAsync($"Loads:      {string.Join(", ", set.Load)} (then their includes)");
            foreach (var file in set.Files)
            {
                var from = file.Kind switch
                {
                    FileSourceKind.Bin => $"bin/{file.Source}",
                    FileSourceKind.Url => file.Source + (File.Exists(store.CacheFile(set.Name, file.Path)) ? "" : " (not downloaded yet)"),
                    _ => file.Source,
                };
                await env.Out.WriteLineAsync($"File:       {file.Path} <- {from}");
            }
            foreach (var edit in set.Edits)
                await env.Out.WriteLineAsync($"Sets:       {edit.File}: [{edit.Section}] {edit.Key} = {edit.Value}");
            return 0;
        });
        return command;
    }

    private static Command Fetch(CliEnvironment env, GlobalOptions globals)
    {
        var names = new Argument<string[]>("names") { Description = "The config sets (default: all with downloaded files)", Arity = ArgumentArity.ZeroOrMore };
        var command = new Command("fetch", "Download the files config sets take from the web (e.g. OSGrid's)") { names };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new ConfigSetStore(globals.Paths(parse));
            var selected = parse.GetValue(names) is { Length: > 0 } given
                ? given.Select(store.Load).ToList()
                : store.Names().Select(store.Load).Where(s => s.Files.Any(f => f.Kind == FileSourceKind.Url)).ToList();
            if (selected.Count == 0)
            {
                await env.Out.WriteLineAsync("No config set downloads anything.");
                return 0;
            }
            foreach (var set in selected)
                await store.FetchAsync(set, env.Web, globals.IsDryRun(parse), env.Out, ct);
            return 0;
        });
        return command;
    }
}
