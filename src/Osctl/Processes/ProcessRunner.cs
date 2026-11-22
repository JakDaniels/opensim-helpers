// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;

namespace Osctl.Processes;

/// <summary>What a finished command gave back. <see cref="NotFound"/>: the program does not exist.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool NotFound = false)
{
    public bool Succeeded => !NotFound && ExitCode == 0;

    public static ProcessResult Missing(string program) =>
        new(-1, "", $"{program}: not found", NotFound: true);
}

/// <summary>
/// Runs external programs (git, dotnet, tmux, systemctl, mariadb, …). Everything osctl runs goes through this,
/// so commands can be tested with a fake and printed by <c>--dry-run</c>.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        // Untranslated messages, so osctl can read them.
        info.Environment["LC_ALL"] = "C";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {program}");
        }
        catch (Win32Exception)
        {
            return ProcessResult.Missing(program);
        }

        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            return new ProcessResult(process.ExitCode, await output, await error);
        }
    }
}
