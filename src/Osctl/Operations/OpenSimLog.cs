// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Running;
using Osctl.Util;

namespace Osctl.Operations;

/// <summary>Waiting for OpenSim.log to say how something it was asked to do (save or load an archive) went.</summary>
public static class OpenSimLog
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    /// <summary>The log's end now: what <see cref="WaitForAsync"/> reads from.</summary>
    public static long End(string log) => File.Exists(log) ? new FileInfo(log).Length : 0;

    /// <summary>
    /// Reads the log from <paramref name="offset"/> until <paramref name="verdict"/> says done ("") or failed (the
    /// reason) for a line. Null when done; the reason, or a timeout, when not.
    /// </summary>
    public static async Task<string?> WaitForAsync(string log, long offset, Func<string, string?> verdict, TimeSpan timeout,
        TimeProvider time, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + timeout;
        var pending = "";
        while (true)
        {
            (var more, offset) = InstanceRunner.ReadFrom(log, offset);
            pending += more;
            var lines = pending.Split('\n');
            pending = lines[^1];
            foreach (var line in lines[..^1])
            {
                if (verdict(line) is { } said)
                    return said.Length == 0 ? null : said.Trim();
            }
            if (time.GetUtcNow() >= deadline)
                return $"not finished after {Durations.Words(timeout)} (SaveTimeoutMinutes in osctl.ini [Backups])";
            await Task.Delay(Poll, ct);
        }
    }
}
