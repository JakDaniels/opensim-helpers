// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Osctl.Builds;
using Osctl.Instances;
using Osctl.Processes;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Running;

/// <summary>
/// <c>osctl run &lt;name&gt;</c>, what the systemd unit runs (PLAN.md "Running instances"): regenerate the
/// configuration, start OpenSim in the instance's own tmux server with the console copied to console.log, wait
/// until every region says "LOGINS ENABLED" (then tell systemd READY=1), and wait for OpenSim to end, exiting with
/// its exit code. So a console <c>shutdown</c> (exit 0) leaves the unit stopped, and a crash (non-zero) makes
/// systemd restart it. SIGTERM (systemd stopping the unit, or a direct stop) shuts OpenSim down gracefully.
/// </summary>
public sealed partial class InstanceRunner(
    OsctlPaths paths,
    OsctlSettings settings,
    IProcessRunner runner,
    IRemoteAdmin remoteAdmin,
    IPortProbe ports,
    TextWriter log,
    TimeProvider time)
{
    /// <summary>OpenSim's bin/opensim.sh sets the stack limit to 1 GB.</summary>
    public const ulong StackLimit = 1024UL * 1024 * 1024;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    private readonly InstanceStore instances = new(paths);
    private readonly BuildStore builds = new(paths);

    public async Task<int> RunAsync(string name, bool foreground, CancellationToken ct)
    {
        var instance = instances.Load(name);
        var regions = instances.Regions(name);
        if (regions.Count == 0)
            throw new OsctlException($"Instance '{name}' has no regions; add one with 'osctl region add … --instance {name}'");
        if (RunState.Load(instances.RunStateFile(name)) is { IsRunnerAlive: true } other && other.RunnerPid != Environment.ProcessId)
            throw new OsctlException($"Instance '{name}' is already running (runner PID {other.RunnerPid})");

        var generator = new ConfigGenerator(paths, settings);
        var generated = generator.Generate(name);
        foreach (var warning in generated.Warnings)
            await Log($"warning: {warning}");
        generator.Write(generated);

        var basePort = ConfigGenerator.BasePort(regions);
        foreach (var port in regions.Select(r => r.Port).Prepend(basePort).Distinct())
        {
            if (!ports.IsFree(port))
                throw new OsctlException($"Port {port} of instance '{name}' is in use on this machine");
        }

        var release = builds.Current(instance.Build)
            ?? throw new OsctlException($"Build '{instance.Build}' has no release; run 'osctl build update {instance.Build}'");
        // Run from the release itself, not current/: a later build update switches current without moving us.
        var bin = Path.Combine(builds.ReleaseDir(instance.Build, release), "bin");
        var command = Command(generated, instances.GeneratedDir(name));

        if (!Posix.RaiseStackLimit(StackLimit))
            await Log("warning: could not raise the stack limit to 1 GB (set LimitSTACK in the unit)");

        return foreground
            ? await RunForegroundAsync(name, bin, release, command, ct)
            : await RunInTmuxAsync(name, bin, release, command, regions, ct);
    }

    /// <summary>The command line: osctl.ini's [Run] Command, then osctl's switches for the generated files.</summary>
    public IReadOnlyList<string> Command(GeneratedConfig generated, string generatedDir) =>
    [
        .. settings.Run.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        "-inimaster=none",
        $"-inifile={Path.Combine(generatedDir, ConfigGenerator.OpenSimFile)}",
        $"-inidirectory={Path.Combine(generatedDir, "config")}",
        .. generated.Log4Net is null ? Array.Empty<string>() : [$"-logconfig={Path.Combine(generatedDir, ConfigGenerator.Log4NetFile)}"],
    ];

    private async Task<int> RunForegroundAsync(string name, string bin, string release, IReadOnlyList<string> command, CancellationToken ct)
    {
        var info = new ProcessStartInfo(command[0]) { WorkingDirectory = bin, UseShellExecute = false };
        foreach (var arg in command.Skip(1))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new OsctlException($"Could not start {command[0]}");
        new RunState(Environment.ProcessId, process.Id, release, time.GetUtcNow(), null).Save(instances.RunStateFile(name));
        try
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            File.Delete(instances.RunStateFile(name));
        }
    }

    private async Task<int> RunInTmuxAsync(string name, string bin, string release, IReadOnlyList<string> command,
        List<RegionDefinition> regions, CancellationToken ct)
    {
        var tmux = new Tmux(runner, instances.TmuxSocket(name), name);
        if (await tmux.IsRunningAsync(ct))
        {
            await Log($"a console for '{name}' is left from an earlier run; closing it");
            await tmux.KillAsync(ct);
        }

        var consoleLog = instances.ConsoleLog(name);
        Directory.CreateDirectory(Path.GetDirectoryName(consoleLog)!);
        if (File.Exists(consoleLog))
            File.Move(consoleLog, consoleLog + ".1", overwrite: true);
        File.WriteAllText(consoleLog, "");
        var config = Path.Combine(instances.GeneratedDir(name), "tmux.conf");
        File.WriteAllText(config, Tmux.Config(settings.Tmux.Prefix, settings.Tmux.HistoryLimit, name));

        // The pane waits for one line before it becomes OpenSim (exec: same PID), so the console log is attached
        // before OpenSim prints anything.
        await tmux.StartAsync(config, bin, ["sh", "-c", "read -r _ && exec \"$@\"", "osctl-start", .. command], ct);
        await tmux.PipeToAsync(consoleLog, ct);
        await tmux.SendLineAsync("", ct);
        await ShareConsoleAsync(tmux, ct);

        var pane = await tmux.StateAsync(ct);
        var started = time.GetUtcNow();
        var state = new RunState(Environment.ProcessId, pane.Pid, release, started, null);
        state.Save(instances.RunStateFile(name));
        await Log($"started '{name}' (OpenSim PID {pane.Pid}, release {release}); console: osctl console {name}");
        Posix.Notify($"MAINPID={Environment.ProcessId}\nSTATUS=starting {regions.Count} region(s)");

        // SIGTERM: systemd stopping the unit (after ExecStop), or a direct stop. Shut OpenSim down gracefully.
        var stopper = new InstanceStopper(paths, settings, runner, remoteAdmin, log, time);
        var stopping = 0;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            if (Interlocked.Exchange(ref stopping, 1) == 0)
                _ = Task.Run(() => stopper.StopAsync(name, force: false, CancellationToken.None));
        });

        try
        {
            var ready = new HashSet<string>(StringComparer.Ordinal);
            var readAt = 0L;
            while (state.Ready is null)
            {
                pane = await tmux.StateAsync(ct);
                if (!pane.Exists || pane.Dead)
                    break;

                (var text, readAt) = ReadFrom(consoleLog, readAt);
                foreach (Match m in ReadyPattern().Matches(StripAnsi(text)))
                    ready.Add(m.Groups[1].Value.Trim());

                var all = regions.All(r => ready.Contains(r.Name));
                var late = time.GetUtcNow() - started > TimeSpan.FromSeconds(settings.Timeouts.StartSeconds);
                if (all || late)
                {
                    if (!all)
                        await Log($"warning: not every region said LOGINS ENABLED within {settings.Timeouts.StartSeconds} s; carrying on");
                    state = state with { Ready = time.GetUtcNow() };
                    state.Save(instances.RunStateFile(name));
                    Posix.Notify($"READY=1\nSTATUS=running {regions.Count} region(s)");
                    await Log($"'{name}' is ready ({(state.Ready.Value - started).TotalSeconds:F0} s)");
                    break;
                }
                await Task.Delay(Poll, ct);
            }

            while (pane.Exists && !pane.Dead)
            {
                await Task.Delay(Poll, ct);
                pane = await tmux.StateAsync(ct);
            }

            var exit = !pane.Exists ? 1 : pane.Signal > 0 ? 128 + pane.Signal : pane.ExitStatus;
            Posix.Notify($"STOPPING=1\nSTATUS=OpenSim ended with exit code {exit}");
            await Log($"OpenSim for '{name}' ended with exit code {exit}{(pane.Exists ? "" : " (its console went away)")}");
            return exit;
        }
        finally
        {
            await tmux.KillAsync(CancellationToken.None);
            File.Delete(tmux.Socket);
            File.Delete(instances.PidFile(name));      // OpenSim's own, left behind when it is killed
            File.Delete(instances.RunStateFile(name));
        }
    }

    /// <summary>
    /// Opens the console to the group (PLAN.md: any member of the opensim group may attach and type): the socket's
    /// mode, and tmux's server-access list for the group's members.
    /// </summary>
    private async Task ShareConsoleAsync(Tmux tmux, CancellationToken ct)
    {
        try
        {
            File.SetUnixFileMode(tmux.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Log($"warning: could not open the console socket to the group: {e.Message}");
        }

        var group = await runner.RunAsync("getent", ["group", settings.Account.Group], cancellationToken: ct);
        if (!group.Succeeded)
            return;
        var members = group.StandardOutput.Trim().Split(':') is { Length: 4 } fields
            ? fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        foreach (var member in members.Where(m => m != Environment.UserName))
            await tmux.AllowAsync(member, ct);
    }

    internal static (string Text, long Position) ReadFrom(string file, long position)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= position)
                return ("", stream.Length < position ? 0 : position);
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return (text, stream.Length);
        }
        catch (FileNotFoundException)
        {
            return ("", 0);
        }
    }

    /// <summary>Removes the terminal colour and cursor codes OpenSim's console writes.</summary>
    public static string StripAnsi(string text) => AnsiPattern().Replace(text, "").Replace("\r", "");

    private Task Log(string text) => log.WriteLineAsync($"{time.GetLocalNow():yyyy-MM-dd HH:mm:ss} osctl: {text}");

    [GeneratedRegex(@"INITIALIZATION COMPLETE FOR (.+?) - LOGINS ENABLED")]
    private static partial Regex ReadyPattern();

    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[()][A-Za-z0-9]|\x1b[=>]")]
    private static partial Regex AnsiPattern();
}
