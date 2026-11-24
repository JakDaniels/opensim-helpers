// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Instances;
using Osctl.Running;

namespace Osctl.Operations;

/// <summary>
/// The in-world warning before a restart or a stop (PLAN.md "Backups and scheduled restarts"): a broadcast at the
/// warning time, then at 5 minutes and 1 minute before, through RemoteAdmin's admin_broadcast.
/// </summary>
public sealed class Countdown(InstanceStore instances, IRemoteAdmin remoteAdmin, TimeProvider time, TextWriter progress)
{
    private static readonly TimeSpan[] Reminders = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)];

    /// <summary>When the warnings go out, as time before the event: the warning time first, then the reminders within it.</summary>
    public static IReadOnlyList<TimeSpan> Marks(TimeSpan warn) =>
        warn <= TimeSpan.Zero ? [] : [warn, .. Reminders.Where(r => r < warn)];

    public static string Message(string verb, TimeSpan left) =>
        $"This region will {verb} in {Durations.Words(left)}. Please find a safe place.";

    /// <summary>Warns the people in the instances' regions, then returns when the warning time is over.</summary>
    public async Task RunAsync(IReadOnlyList<string> names, TimeSpan warn, string verb, CancellationToken ct)
    {
        var end = time.GetUtcNow() + warn;
        foreach (var mark in Marks(warn))
        {
            var wait = end - mark - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, time, ct);
            foreach (var name in names)
            {
                var port = ConfigGenerator.BasePort(instances.Regions(name));
                var sent = instances.RemoteAdminPassword(name) is { } password && port > 0
                    && await remoteAdmin.BroadcastAsync(port, password, Message(verb, mark), ct);
                await progress.WriteLineAsync(sent
                    ? $"{name}: warned: {verb} in {Durations.Words(mark)}"
                    : $"{name}: could not send the warning (RemoteAdmin did not answer)");
            }
        }
        var rest = end - time.GetUtcNow();
        if (rest > TimeSpan.Zero)
            await Task.Delay(rest, time, ct);
    }
}
