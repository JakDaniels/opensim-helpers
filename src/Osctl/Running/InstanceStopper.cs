// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Instances;
using Osctl.Processes;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Running;

/// <summary>
/// Stops a running instance (<c>osctl run-stop</c>, the unit's ExecStop, and the runner's SIGTERM): RemoteAdmin's
/// admin_shutdown, or <c>shutdown</c> typed into the console if RemoteAdmin does not answer, then waiting up to
/// [Timeouts] StopSeconds; after that (or with force) OpenSim is killed.
/// </summary>
public sealed class InstanceStopper(
    OsctlPaths paths,
    OsctlSettings settings,
    IProcessRunner runner,
    IRemoteAdmin remoteAdmin,
    TextWriter log,
    TimeProvider time)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private readonly InstanceStore instances = new(paths);

    /// <summary>True when OpenSim ended by itself; false when it had to be killed. Nothing running: true.</summary>
    public async Task<bool> StopAsync(string name, bool force, CancellationToken ct, TimeSpan? notice = null)
    {
        var tmux = new Tmux(runner, instances.TmuxSocket(name), name);
        var state = RunState.Load(instances.RunStateFile(name));
        var pane = await tmux.StateAsync(ct);
        if (!pane.Exists || pane.Dead)
            return true;
        (state is null ? null : state with { Stopping = true })?.Save(instances.RunStateFile(name));

        if (!force && instances.Load(name).IsRobust)
        {
            // Robust has no RemoteAdmin.
            await Log($"typing 'shutdown' into the console of '{name}'");
            await tmux.SendLineAsync("shutdown", ct);
            if (await WaitAsync(tmux, TimeSpan.Zero, ct))
                return true;
        }
        else if (!force)
        {
            var regions = instances.Regions(name);
            var port = ConfigGenerator.BasePort(regions);
            var password = instances.RemoteAdminPassword(name);
            var asked = password is not null && port > 0
                && await remoteAdmin.ShutdownAsync(port, password, notice ?? TimeSpan.Zero, true, ct);
            if (asked)
            {
                await Log($"asked '{name}' to shut down (RemoteAdmin)");
            }
            else
            {
                await Log($"RemoteAdmin did not answer; typing 'shutdown' into the console of '{name}'");
                await tmux.SendLineAsync("shutdown", ct);
            }

            if (await WaitAsync(tmux, notice ?? TimeSpan.Zero, ct))
                return true;
        }

        if (pane.Pid > 0)
        {
            Posix.Kill(pane.Pid, Posix.SIGTERM);
            for (var i = 0; i < 10 && Posix.IsAlive(pane.Pid); i++)
                await Task.Delay(Poll, ct);
            if (Posix.IsAlive(pane.Pid))
                Posix.Kill(pane.Pid, Posix.SIGKILL);
        }
        return false;
    }

    /// <summary>Waits for the console's program to end: true if it did within the notice and [Timeouts] StopSeconds.</summary>
    private async Task<bool> WaitAsync(Tmux tmux, TimeSpan notice, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + notice + TimeSpan.FromSeconds(settings.Timeouts.StopSeconds);
        while (time.GetUtcNow() < deadline)
        {
            var pane = await tmux.StateAsync(ct);
            if (!pane.Exists || pane.Dead)
                return true;
            await Task.Delay(Poll, ct);
        }
        await Log($"'{tmux.Session}' did not stop within {settings.Timeouts.StopSeconds} s; killing it");
        return false;
    }

    private Task Log(string text) => log.WriteLineAsync($"{time.GetLocalNow():yyyy-MM-dd HH:mm:ss} osctl: {text}");
}
