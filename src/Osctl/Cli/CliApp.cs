// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Osctl.Doctor;
using Osctl.Processes;
using Osctl.Settings;

namespace Osctl.Cli;

/// <summary>What commands need from outside: the process runner and where output goes (fakes in tests).</summary>
public sealed record CliEnvironment(IProcessRunner Runner, TextWriter Out, TextWriter Error)
{
    public static CliEnvironment Console() => new(new ProcessRunner(), System.Console.Out, System.Console.Error);
}

/// <summary>The osctl command line (PLAN.md "Commands").</summary>
public static class CliApp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken = default)
    {
        var root = Build(env);
        var config = new InvocationConfiguration { Output = env.Out, Error = env.Error };
        return root.Parse(args).InvokeAsync(config, cancellationToken);
    }

    internal static RootCommand Build(CliEnvironment env)
    {
        var rootOption = new Option<string?>("--root")
        {
            Description = $"Use this directory instead of / for /etc/osctl, /var/lib/osctl and so on (or set {OsctlPaths.RootEnvironmentVariable})",
            Recursive = true,
        };

        var root = new RootCommand("osctl: manage OpenSimulator instances (MIOS 2.0)") { rootOption };
        root.Add(DoctorCommand(env, rootOption));
        return root;
    }

    private static Command DoctorCommand(CliEnvironment env, Option<string?> rootOption)
    {
        var json = new Option<bool>("--json") { Description = "Print the results as JSON" };
        var command = new Command("doctor", "Check that this machine has what osctl and OpenSim need") { json };
        command.SetAction(async (parse, ct) =>
        {
            var paths = OsctlPaths.Resolve(parse.GetValue(rootOption));
            var results = await new Doctor.Doctor(env.Runner, paths).RunAsync(ct);

            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(results, Json));
            }
            else
            {
                var width = results.Max(r => r.Name.Length);
                foreach (var r in results)
                {
                    await env.Out.WriteLineAsync($"{Label(r.Status)}  {r.Name.PadRight(width)}  {r.Detail}");
                    if (r.Hint is not null && r.Status != CheckStatus.Ok)
                        await env.Out.WriteLineAsync($"{new string(' ', 6)}  {new string(' ', width)}  → {r.Hint}");
                }
            }

            return results.Any(r => r.Status == CheckStatus.Failed) ? 1 : 0;
        });
        return command;
    }

    private static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[ OK ]",
        CheckStatus.Warning => "[WARN]",
        _ => "[FAIL]",
    };
}
