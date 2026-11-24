// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Osctl.Ini;
using Osctl.Instances;
using Osctl.Running;
using Osctl.Settings;

namespace Osctl.Cli;

/// <summary>
/// Running instances (PLAN.md "Running instances"): start, stop, restart, status, enable, disable, console, cmd,
/// logs; run and run-stop (what the systemd unit calls); units install|show.
/// </summary>
internal static class RunCommands
{
    public static IEnumerable<Command> Create(CliEnvironment env, GlobalOptions globals) =>
    [
        Lifecycle(env, globals, "start", "Start instances (all when none are named) and wait until they are ready",
            (s, name, _, ct) => s.StartAsync(name, ct), "started"),
        Lifecycle(env, globals, "stop", "Stop instances (all when none are named), shutting OpenSim down cleanly",
            (s, name, force, ct) => s.StopAsync(name, force, ct), "stopped", withForce: true, warnVerb: "shut down"),
        Lifecycle(env, globals, "restart", "Restart instances (all when none are named)",
            (s, name, _, ct) => s.RestartAsync(name, ct), "restarted", warnVerb: "restart"),
        Boot(env, globals, enable: true),
        Boot(env, globals, enable: false),
        Status(env, globals),
        Console(env, globals),
        Cmd(env, globals),
        Logs(env, globals),
        Run(env, globals),
        RunStop(env, globals),
        UnitsCommand(env, globals),
    ];

    public static ISupervisor Supervisor(CliEnvironment env, GlobalOptions globals, ParseResult parse)
    {
        var paths = globals.Paths(parse);
        var settings = globals.Settings(parse);
        var runner = globals.Runner(parse, env);
        return settings.Supervisor.Mode switch
        {
            SupervisorMode.Direct => new DirectSupervisor(paths, settings, runner, env.Admin, env.Clock, env.Self),
            var mode => new SystemdSupervisor(runner, new InstanceStore(paths), user: mode == SupervisorMode.SystemdUser),
        };
    }

    private static Argument<string[]> Names(string description) =>
        new("names") { Description = description, Arity = ArgumentArity.ZeroOrMore };

    private static Argument<string> InstanceName() => new("instance") { Description = "The instance" };

    private static IReadOnlyList<string> Selected(InstanceStore store, string[]? given)
    {
        var names = given is { Length: > 0 } ? given : [.. store.Names()];
        foreach (var name in names)
            store.Load(name);
        return names;
    }

    private static Command Lifecycle(CliEnvironment env, GlobalOptions globals, string verb, string description,
        Func<ISupervisor, string, bool, CancellationToken, Task> action, string done, bool withForce = false, string? warnVerb = null)
    {
        var names = Names("The instances");
        var force = new Option<bool>("--force") { Description = "Kill OpenSim straight away instead of shutting it down" };
        var warn = new Option<string?>("--warn")
        {
            Description = "Warn the people in the regions this long before (e.g. 10m), again at 5 and 1 minutes; running instances only",
        };
        var scheduled = new Option<bool>("--scheduled")
        {
            Description = "What the restart timer runs: running instances only, warned as instance.ini [Schedule] WarnMinutes says",
        };
        var command = new Command(verb, description) { names };
        if (withForce)
            command.Add(force);
        if (warnVerb is not null)
            command.Add(warn);
        if (verb == "restart")
            command.Add(scheduled);
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var settings = globals.Settings(parse);
            var store = new InstanceStore(globals.Paths(parse));
            var supervisor = Supervisor(env, globals, parse);
            var selected = Selected(store, parse.GetValue(names));
            var isScheduled = parse.GetValue(scheduled);
            var warning = parse.GetValue(warn) is { } text ? Operations.Durations.Parse(text) : (TimeSpan?)null;

            if (warnVerb is not null && (warning is not null || isScheduled))
            {
                // Only running instances have anyone to warn; a scheduled restart leaves stopped instances stopped.
                var running = new List<string>();
                foreach (var name in selected)
                {
                    if ((await supervisor.StatusAsync(name, ct)).State == InstanceState.Running)
                        running.Add(name);
                    else if (isScheduled)
                        await env.Out.WriteLineAsync($"{name}: not running, so not restarted");
                }
                if (isScheduled)
                    selected = running;

                // Instances warned for the same time are counted down together; different times run side by side.
                var groups = running
                    .GroupBy(n => warning ?? TimeSpan.FromMinutes(store.Load(n).Scheduled.WarnMinutes ?? settings.Schedule.WarnMinutes))
                    .Where(g => g.Key > TimeSpan.Zero)
                    .ToList();
                if (globals.IsDryRun(parse))
                {
                    foreach (var group in groups)
                        await env.Out.WriteLineAsync($"would warn {string.Join(", ", group)} {Operations.Durations.Words(group.Key)} before they {warnVerb}");
                }
                else
                {
                    var countdown = new Operations.Countdown(store, env.Admin, env.Clock, TextWriter.Synchronized(env.Out));
                    await Task.WhenAll(groups.Select(g => countdown.RunAsync([.. g], g.Key, warnVerb, ct)));
                }
            }

            var failures = 0;
            foreach (var name in selected)
            {
                if (verb == "start" && store.Regions(name).Count == 0)
                {
                    await env.Out.WriteLineAsync($"{name}: no regions, not started");
                    continue;
                }
                await env.Out.WriteAsync($"{verb[..1].ToUpperInvariant()}{verb[1..]}ing {name} … ".Replace("Stoping", "Stopping"));
                try
                {
                    await action(supervisor, name, withForce && parse.GetValue(force), ct);
                    await env.Out.WriteLineAsync(globals.IsDryRun(parse) ? "(dry run)" : done);
                }
                catch (OsctlException e)
                {
                    failures++;
                    await env.Out.WriteLineAsync("FAILED");
                    await env.Error.WriteLineAsync($"osctl: {e.Message}");
                }
            }
            return failures == 0 ? 0 : CliApp.ExitFailure;
        });
        return command;
    }

    private static Command Boot(CliEnvironment env, GlobalOptions globals, bool enable)
    {
        var names = Names("The instances");
        var command = new Command(enable ? "enable" : "disable",
            enable ? "Start instances at boot" : "Do not start instances at boot") { names };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            if (globals.Settings(parse).Supervisor.Mode == SupervisorMode.Direct)
                throw new OsctlException("Nothing starts at boot with [Supervisor] Mode = direct; use systemd or systemd-user");
            var store = new InstanceStore(globals.Paths(parse));
            var supervisor = Supervisor(env, globals, parse);
            foreach (var name in Selected(store, parse.GetValue(names)))
            {
                await supervisor.SetBootAsync(name, enable, ct);
                await env.Out.WriteLineAsync($"{name}: {(enable ? "starts" : "does not start")} at boot");
            }
            return 0;
        });
        return command;
    }

    private static Command Status(CliEnvironment env, GlobalOptions globals)
    {
        var names = Names("The instances (default: all)");
        var json = new Option<bool>("--json") { Description = "Print the status as JSON" };
        var command = new Command("status", "Show whether instances are running") { names, json };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var supervisor = Supervisor(env, globals, parse);
            var statuses = new List<InstanceStatus>();
            foreach (var name in Selected(store, parse.GetValue(names)))
                statuses.Add(await supervisor.StatusAsync(name, ct));

            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(statuses, CliApp.Json));
                return 0;
            }
            if (statuses.Count == 0)
            {
                await env.Out.WriteLineAsync("No instances yet: 'osctl instance add <name> …' creates one.");
                return 0;
            }
            var now = env.Clock.GetUtcNow();
            Table.Write(env.Out, ["INSTANCE", "STATE", "BOOT", "PID", "UP", "RESTARTS", "RELEASE", "DETAIL"],
                statuses.Select(s => (IReadOnlyList<string>)
                [
                    s.Name,
                    s.State.ToString().ToLowerInvariant(),
                    s.StartsAtBoot ? "yes" : "no",
                    s.OpenSimPid > 0 ? s.OpenSimPid.ToString() : "-",
                    s.Since is { } since ? Uptime(now - since) : "-",
                    s.Restarts.ToString(),
                    s.Release ?? "-",
                    s.Detail ?? "",
                ]));
            return 0;
        });
        return command;
    }

    private static Command Console(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var command = new Command("console", "Attach to an instance's OpenSim console (Ctrl-a d leaves it running)") { name };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var store = new InstanceStore(paths);
            var instance = store.Load(parse.GetRequiredValue(name));
            var tmux = new Tmux(env.Runner, store.TmuxSocket(instance.Name), instance.Name);
            if (!await tmux.IsRunningAsync(ct))
                throw new OsctlException($"Instance '{instance.Name}' is not running ('osctl start {instance.Name}')");

            // The terminal is handed to tmux for as long as the user stays attached.
            var info = new ProcessStartInfo("tmux") { UseShellExecute = false };
            foreach (var arg in new[] { "-S", tmux.Socket, "attach-session", "-t", $"={instance.Name}" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new OsctlException("Could not start tmux");
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        });
        return command;
    }

    private static Command Cmd(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var text = new Argument<string[]>("command") { Description = "The console command, e.g. show regions", Arity = ArgumentArity.OneOrMore };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for the console prompt", DefaultValueFactory = _ => 30 };
        var command = new Command("cmd", "Run a console command in an instance and print its output") { name, text, timeout };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var instance = store.Load(parse.GetRequiredValue(name));
            var line = string.Join(' ', parse.GetRequiredValue(text));
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would type '{line}' into the console of '{instance.Name}'");
                return 0;
            }
            var tmux = new Tmux(env.Runner, store.TmuxSocket(instance.Name), instance.Name);
            var output = await ConsoleCommand.RunAsync(tmux, store.ConsoleLog(instance.Name), line,
                TimeSpan.FromSeconds(parse.GetValue(timeout)), env.Clock, ct);
            if (output.Length > 0)
                await env.Out.WriteLineAsync(output);
            return 0;
        });
        return command;
    }

    private static Command Logs(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var lines = new Option<int>("-n") { Description = "How many lines from the end", DefaultValueFactory = _ => 40 };
        var follow = new Option<bool>("-f") { Description = "Keep printing new lines (Ctrl-c stops)" };
        var console = new Option<bool>("--console") { Description = "The console transcript instead of OpenSim.log" };
        var runner = new Option<bool>("--runner") { Description = "osctl's own messages about the instance (direct mode)" };
        var command = new Command("logs", "Show an instance's log") { name, lines, follow, console, runner };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var instance = store.Load(parse.GetRequiredValue(name));
            var file = parse.GetValue(console) ? store.ConsoleLog(instance.Name)
                : parse.GetValue(runner) ? Path.Combine(store.LogDir(instance.Name), "runner.log")
                : Path.Combine(store.LogDir(instance.Name), "OpenSim.log");
            if (!File.Exists(file))
                throw new OsctlException($"{file} does not exist yet");

            var (text, position) = InstanceRunner.ReadFrom(file, 0);
            var all = InstanceRunner.StripAnsi(text).Split('\n');
            foreach (var l in all.TakeLast(parse.GetValue(lines) + 1).SkipLast(1))
                await env.Out.WriteLineAsync(l);
            while (parse.GetValue(follow) && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                (var more, position) = InstanceRunner.ReadFrom(file, position);
                if (more.Length > 0)
                    await env.Out.WriteAsync(InstanceRunner.StripAnsi(more));
            }
            return 0;
        });
        return command;
    }

    private static Command Run(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var foreground = new Option<bool>("--foreground") { Description = "Run OpenSim in this terminal, without tmux (for debugging)" };
        var command = new Command("run", "Run an instance until OpenSim ends (what the systemd unit runs)") { name, foreground };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var runner = new InstanceRunner(globals.Paths(parse), globals.Settings(parse), env.Runner, env.Admin, env.PortProbe, env.Out, env.Clock);
            return await runner.RunAsync(parse.GetRequiredValue(name), parse.GetValue(foreground), ct);
        });
        return command;
    }

    private static Command RunStop(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var command = new Command("run-stop", "Shut a running instance down (what the systemd unit's ExecStop runs)") { name };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var stopper = new InstanceStopper(globals.Paths(parse), globals.Settings(parse), env.Runner, env.Admin, env.Out, env.Clock);
            return await stopper.StopAsync(parse.GetRequiredValue(name), force: false, ct) ? 0 : CliApp.ExitFailure;
        });
        return command;
    }

    private static Command UnitsCommand(CliEnvironment env, GlobalOptions globals)
    {
        var units = new Command("units", "The systemd units osctl runs instances, scheduled restarts and backups with");

        var user = new Option<bool>("--user") { Description = "Your own systemd user units (~/.config/systemd/user), not the system's" };
        var install = new Command("install",
            "Write osctl@.service, osctl-restart@.service, osctl-backup@.service and the instances' timers, then reload systemd (system units need root)")
        { user };
        CliApp.SetAction(install, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var settings = globals.Settings(parse);
            var isUser = parse.GetValue(user);
            IReadOnlyList<string> self = isUser || !File.Exists("/usr/bin/osctl") ? env.Self : ["/usr/bin/osctl"];
            var dir = env.UnitDir(isUser);
            var files = new Dictionary<string, string>
            {
                ["osctl@.service"] = Units.Template(settings, paths, isUser, self),
                [Operations.Schedules.ServiceName(Operations.ScheduledJob.Restart)] =
                    Operations.Schedules.Service(Operations.ScheduledJob.Restart, settings, paths, isUser, self),
                [Operations.Schedules.ServiceName(Operations.ScheduledJob.Backup)] =
                    Operations.Schedules.Service(Operations.ScheduledJob.Backup, settings, paths, isUser, self),
            };
            var store = new InstanceStore(paths);
            var instances = store.Names().Select(store.Load).ToList();
            if (globals.IsDryRun(parse))
            {
                foreach (var (name, text) in files)
                {
                    await env.Out.WriteLineAsync($"would write {Path.Combine(dir, name)}:");
                    await env.Out.WriteAsync(text);
                }
                foreach (var instance in instances)
                {
                    foreach (var job in Enum.GetValues<Operations.ScheduledJob>())
                    {
                        if (Operations.Schedules.Calendar(instance.Scheduled, job) is { } calendar)
                            await env.Out.WriteLineAsync($"would write {Path.Combine(dir, Operations.Schedules.TimerName(job, instance.Name))} (OnCalendar={calendar})");
                    }
                }
                return 0;
            }
            Directory.CreateDirectory(dir);
            foreach (var (name, text) in files)
                await File.WriteAllTextAsync(Path.Combine(dir, name), text, ct);
            // Writes the timers, removes those of schedules that are gone, reloads systemd and enables them.
            var report = await Operations.Schedules.InstallTimersAsync(env.Runner, dir, isUser, instances, removeOthers: true, ct);
            await env.Out.WriteLineAsync($"Wrote {string.Join(", ", files.Keys)} in {dir} and reloaded systemd.");
            foreach (var line in report)
                await env.Out.WriteLineAsync("    " + line);

            var wanted = isUser ? SupervisorMode.SystemdUser : SupervisorMode.Systemd;
            if (settings.Supervisor.Mode != wanted && File.Exists(paths.SettingsFile))
            {
                IniFileEditor.SetValue(paths.SettingsFile, "Supervisor", "Mode", isUser ? "systemd-user" : "systemd");
                await env.Out.WriteLineAsync($"Set [Supervisor] Mode = {(isUser ? "systemd-user" : "systemd")} in {paths.SettingsFile}.");
            }
            if (isUser)
                await env.Out.WriteLineAsync("For instances to start at boot without you logged in: loginctl enable-linger " + Environment.UserName);
            return 0;
        });

        var showUser = new Option<bool>("--user") { Description = "The user unit" };
        var program = new Option<string?>("--program") { Description = "The osctl the unit runs (default: this one, or /usr/bin/osctl)" };
        var defaults = new Option<bool>("--defaults") { Description = "Use osctl's built-in settings and root /, not this machine's (the package build)" };
        var which = new Option<string>("--unit")
        {
            Description = "Which unit: instance (osctl@.service), restart (osctl-restart@.service) or backup (osctl-backup@.service)",
            DefaultValueFactory = _ => "instance",
        };
        which.AcceptOnlyFromAmong("instance", "restart", "backup");
        var show = new Command("show", "Print a unit osctl would install (the package build writes its units this way)") { showUser, program, defaults, which };
        CliApp.SetAction(show, env, async (parse, _) =>
        {
            var isUser = parse.GetValue(showUser);
            IReadOnlyList<string> self = parse.GetValue(program) is { } given ? [given]
                : isUser || !File.Exists("/usr/bin/osctl") ? env.Self : ["/usr/bin/osctl"];
            var builtIn = parse.GetValue(defaults);
            var settings = builtIn ? OsctlSettings.Default : globals.Settings(parse);
            var paths = builtIn ? new OsctlPaths("/") : globals.Paths(parse);
            await env.Out.WriteAsync(parse.GetValue(which) switch
            {
                "restart" => Operations.Schedules.Service(Operations.ScheduledJob.Restart, settings, paths, isUser, self),
                "backup" => Operations.Schedules.Service(Operations.ScheduledJob.Backup, settings, paths, isUser, self),
                _ => Units.Template(settings, paths, isUser, self),
            });
            return 0;
        });

        units.Add(install);
        units.Add(show);
        return units;
    }

    private static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{(int)span.TotalMinutes}m {span.Seconds}s";
}
