// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Osctl.Running;

/// <summary>
/// <c>osctl cmd</c> (PLAN.md "Running instances"): types a command into the instance's console and returns what it
/// printed, read from the console log, up to the next console prompt (<c>Region (…) #</c>).
/// </summary>
public static partial class ConsoleCommand
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    public static async Task<string> RunAsync(Tmux tmux, string consoleLog, string command, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        var pane = await tmux.StateAsync(ct);
        if (!pane.Exists || pane.Dead)
            throw new OsctlException($"Instance '{tmux.Session}' is not running");

        var (_, start) = InstanceRunner.ReadFrom(consoleLog, 0);
        await tmux.SendLineAsync(command, ct);

        var deadline = time.GetUtcNow() + timeout;
        var text = "";
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(Poll, ct);
            (var more, start) = InstanceRunner.ReadFrom(consoleLog, start);
            text += more;
            if (Output(text, command) is { } output)
                return output;
            // A command that ends OpenSim (shutdown, quit) never brings the prompt back.
            if ((await tmux.StateAsync(ct)) is { Exists: false } or { Dead: true })
                return Output(text + "\nRegion (root) # ", command) ?? "";
        }
        throw new OsctlException($"No console prompt after '{command}' within {timeout.TotalSeconds:F0} s; 'osctl console {tmux.Session}' shows the console");
    }

    /// <summary>The lines after the echoed command and before the next prompt, or null if the prompt has not come back yet.</summary>
    public static string? Output(string raw, string command)
    {
        var lines = InstanceRunner.StripAnsi(raw).Split('\n');
        var echo = Array.FindIndex(lines, l => l.TrimEnd().EndsWith(command, StringComparison.Ordinal));
        if (echo < 0)
            return null;
        var prompt = Array.FindIndex(lines, echo + 1, l => PromptPattern().IsMatch(l));
        if (prompt < 0)
            return null;
        return string.Join('\n', lines[(echo + 1)..prompt].Select(l => l.TrimEnd())).Trim('\n');
    }

    [GeneratedRegex(@"^Region \([^)]*\) #\s*$")]
    private static partial Regex PromptPattern();
}
