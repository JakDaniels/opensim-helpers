// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Osctl.Running;

/// <summary>
/// <c>osctl cmd</c> (PLAN.md "Running instances"): types a command into the instance's console and returns what it
/// printed, read from the console log, up to the next console prompt (<c>Region (…) #</c>, or Robust's
/// <c>R.O.B.U.S.T.#</c>). A command that asks questions gets <c>answers</c>: each is typed once its question
/// (the text before the colon OpenSim prints) has appeared, so answers such as passwords, which OpenSim does not
/// echo, never reach the console log or the console history.
/// </summary>
public static partial class ConsoleCommand
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    public static async Task<string> RunAsync(Tmux tmux, string consoleLog, string command, TimeSpan timeout, TimeProvider time,
        CancellationToken ct, IReadOnlyList<(string Question, string Answer)>? answers = null)
    {
        var pane = await tmux.StateAsync(ct);
        if (!pane.Exists || pane.Dead)
            throw new OsctlException($"Instance '{tmux.Session}' is not running");

        var (_, start) = InstanceRunner.ReadFrom(consoleLog, 0);
        await tmux.SendLineAsync(command, ct);

        var deadline = time.GetUtcNow() + timeout;
        var text = "";
        var answered = 0;
        var asked = 0;   // where in text the next question is looked for
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(Poll, ct);
            (var more, start) = InstanceRunner.ReadFrom(consoleLog, start);
            text += more;
            if (answers is not null && answered < answers.Count)
            {
                var (question, answer) = answers[answered];
                var at = InstanceRunner.StripAnsi(text).IndexOf(question, asked, StringComparison.Ordinal);
                if (at >= 0)
                {
                    await tmux.SendLineAsync(answer, ct);
                    asked = at + question.Length;
                    answered++;
                }
                continue;
            }
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

    [GeneratedRegex(@"^(?:Region \([^)]*\) |R\.O\.B\.U\.S\.T\.)#\s*$")]
    private static partial Regex PromptPattern();
}
