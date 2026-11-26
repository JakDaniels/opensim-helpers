// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Osctl.ConfigSets;
using Osctl.Databases;
using Osctl.Doctor;
using Osctl.Ini;
using Osctl.Instances;
using Osctl.Processes;
using Osctl.Running;
using Osctl.Settings;
using Osctl.Setup;

namespace Osctl.Cli;

/// <summary>What commands need from outside: the process runner and where output goes (fakes in tests).</summary>
public sealed record CliEnvironment(
    IProcessRunner Runner,
    TextWriter Out,
    TextWriter Error,
    TimeProvider? Time = null,
    IDownloader? Downloader = null,
    IPortProbe? Ports = null,
    IDatabaseFactory? Databases = null,
    IRemoteAdmin? RemoteAdmin = null,
    IReadOnlyList<string>? OsctlCommand = null,
    TextReader? Input = null,
    string? SystemdUnitDir = null,
    IGridServices? GridServices = null)
{
    public IGridServices Grid => GridServices ?? new HttpGridServices();

    /// <summary>Where systemd units go: /etc/systemd/system, ~/.config/systemd/user, or a test directory.</summary>
    public string UnitDir(bool user) => SystemdUnitDir ?? Operations.Schedules.UnitDir(user);

    /// <summary>
    /// Reads a secret: from <see cref="Input"/> when given (tests) or when stdin is not a terminal (one line), else
    /// typed at the terminal without echo.
    /// </summary>
    public string ReadSecret(string prompt)
    {
        if (Input is not null || System.Console.IsInputRedirected)
            return (Input ?? System.Console.In).ReadLine() ?? "";
        Error.Write(prompt);
        Error.Flush();
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = System.Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                    text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                text.Append(key.KeyChar);
        }
        Error.WriteLine();
        return text.ToString();
    }

    public IDatabaseFactory DatabaseFactory => Databases ?? new MySqlDatabaseFactory(Runner);

    public IRemoteAdmin Admin => RemoteAdmin ?? new HttpRemoteAdmin();

    /// <summary>How to run osctl again (the direct supervisor starts 'osctl run' this way).</summary>
    public IReadOnlyList<string> Self => OsctlCommand ?? Units.Self();

    public TimeProvider Clock => Time ?? TimeProvider.System;

    public IDownloader Web => Downloader ?? new HttpDownloader();

    public IPortProbe PortProbe => Ports ?? new PortProbe();

    public static CliEnvironment Console() => new(new ProcessRunner(), System.Console.Out, System.Console.Error);
}

/// <summary>
/// The osctl command line (PLAN.md "Commands"). Exit codes: 0 success, 1 the command ran but found or reported
/// a failure, 2 a problem osctl can explain (bad settings, missing file, …), printed as one line on stderr.
/// </summary>
public static class CliApp
{
    public const int ExitFailure = 1;
    public const int ExitError = 2;

    internal static readonly JsonSerializerOptions Json = new()
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
        var globals = new GlobalOptions(
            new Option<string?>("--root")
            {
                Description = $"Use this directory instead of / for /etc/osctl, /var/lib/osctl and so on (or set {OsctlPaths.RootEnvironmentVariable})",
                Recursive = true,
            },
            new Option<bool>("--dry-run")
            {
                Description = "Show what would be changed, and change nothing",
                Recursive = true,
            });

        var root = new RootCommand("osctl: manage OpenSimulator instances (MIOS 2.0)") { globals.Root, globals.DryRun };
        root.Add(InitCommand(env, globals));
        root.Add(DoctorCommand(env, globals));
        root.Add(BuildCommands.Create(env, globals));
        root.Add(ConfigSetCommands.Create(env, globals));
        root.Add(InstanceCommands.Create(env, globals));
        root.Add(InstanceCommands.Regions(env, globals));
        root.Add(GridCommands.Create(env, globals));
        root.Add(GridCommands.Users(env, globals));
        root.Add(InstanceCommands.List(env, globals));
        root.Add(ConfigCommands.Create(env, globals));
        root.Add(DbCommands.Create(env, globals));
        foreach (var command in RunCommands.Create(env, globals))
            root.Add(command);
        foreach (var command in OperationsCommands.Create(env, globals))
            root.Add(command);
        return root;
    }

    private static Command InitCommand(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("init",
            "Create osctl's directories and default settings files; existing files are never changed");
        SetAction(command, env, async (parse, _) =>
        {
            var dryRun = globals.IsDryRun(parse);
            var steps = new Initializer(globals.Paths(parse)).Run(dryRun);
            foreach (var step in steps.Where(s => s.Action == InitAction.Created))
            {
                var what = step.IsDirectory ? "directory" : "file";
                await env.Out.WriteLineAsync(dryRun ? $"would create {what} {step.Path}" : $"created {what} {step.Path}");
            }
            if (steps.All(s => s.Action == InitAction.Kept))
                await env.Out.WriteLineAsync("Everything is already in place.");
            return 0;
        });
        return command;
    }

    private static Command DoctorCommand(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print the results as JSON" };
        var command = new Command("doctor", "Check that this machine has what osctl and OpenSim need") { json };
        SetAction(command, env, async (parse, ct) =>
        {
            var results = await new Doctor.Doctor(globals.Runner(parse, env), globals.Paths(parse), env.DatabaseFactory).RunAsync(ct);

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

            return results.Any(r => r.Status == CheckStatus.Failed) ? ExitFailure : 0;
        });
        return command;
    }

    /// <summary>
    /// Sets a command's action so that the errors osctl expects (bad settings, unreadable files, refused
    /// values) end as one line on stderr and exit code 2, instead of a stack trace.
    /// </summary>
    internal static void SetAction(Command command, CliEnvironment env, Func<ParseResult, CancellationToken, Task<int>> action) =>
        command.SetAction(async (parse, ct) =>
        {
            try
            {
                return await action(parse, ct);
            }
            catch (Exception e) when (IsExpected(e))
            {
                await env.Error.WriteLineAsync($"osctl: {e.Message}");
                return ExitError;
            }
        });

    internal static bool IsExpected(Exception e) => e
        is OsctlException
        or IniParseException
        or IniWriteException
        or SettingsException
        or IOException
        or UnauthorizedAccessException
        or NotSupportedException;

    private static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[ OK ]",
        CheckStatus.Warning => "[WARN]",
        _ => "[FAIL]",
    };
}

/// <summary>Options every command takes, and what follows from them.</summary>
internal sealed record GlobalOptions(Option<string?> Root, Option<bool> DryRun)
{
    public OsctlPaths Paths(ParseResult parse) => OsctlPaths.Resolve(parse.GetValue(Root));

    public bool IsDryRun(ParseResult parse) => parse.GetValue(DryRun);

    public OsctlSettings Settings(ParseResult parse) => SettingsLoader.Load(Paths(parse)).Settings;

    public IProcessRunner Runner(ParseResult parse, CliEnvironment env) =>
        IsDryRun(parse) ? new DryRunProcessRunner(env.Runner, env.Out) : env.Runner;
}
