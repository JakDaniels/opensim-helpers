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
/// How to run a command. <see cref="ChangesSystem"/>: the command changes something (builds, starts, writes);
/// under <c>--dry-run</c> such commands are printed instead of run, while queries still run.
/// </summary>
/// <see cref="OutputLine"/>: when set, each line of output (stdout and stderr, as they come) is passed to it
/// instead of being collected, for long outputs such as builds.
/// <see cref="Input"/>: opens a stream that is copied to the program's standard input (else it gets none).
public sealed record ProcessRunOptions(
    bool ChangesSystem = false,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    Action<string>? OutputLine = null,
    Func<Stream>? Input = null)
{
    public static ProcessRunOptions Query { get; } = new();

    public static ProcessRunOptions Change { get; } = new(ChangesSystem: true);
}

/// <summary>
/// Runs external programs (git, dotnet, tmux, systemctl, mariadb, …). Everything osctl runs goes through this,
/// so commands can be tested with a fake and printed by <c>--dry-run</c>.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ProcessRunOptions.Query;
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        if (options.WorkingDirectory is not null)
            info.WorkingDirectory = options.WorkingDirectory;
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        // Untranslated messages, so osctl can read them.
        info.Environment["LC_ALL"] = "C";
        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment)
                info.Environment[key] = value;
        }

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
            var input = options.Input is { } open ? FeedAsync(process, open, cancellationToken) : null;
            if (input is null)
                process.StandardInput.Close();
            if (options.OutputLine is { } sink)
                return await StreamAsync(process, sink, cancellationToken);

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                if (input is not null)
                    await input;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            return new ProcessResult(process.ExitCode, await output, await error);
        }
    }

    /// <summary>Copies the input to the program, then closes its standard input; a program that stops reading early ends it.</summary>
    private static async Task FeedAsync(Process process, Func<Stream> open, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = open();
            await source.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
        }
        catch (IOException)
        {
            // The program exited without reading it all; its exit code says why.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
    }

    private static async Task<ProcessResult> StreamAsync(Process process, Action<string> sink, CancellationToken cancellationToken)
    {
        var gate = new object();

        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lock (gate)
                    sink(line);
            }
        }

        try
        {
            await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return new ProcessResult(process.ExitCode, "", "");
    }
}

/// <summary>
/// <c>--dry-run</c>: queries run through the inner runner; commands that change the system are printed
/// ("would run: …") and reported as succeeded with no output.
/// </summary>
public sealed class DryRunProcessRunner(IProcessRunner inner, TextWriter output) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is not { ChangesSystem: true })
            return await inner.RunAsync(program, arguments, options, cancellationToken);

        await output.WriteLineAsync($"would run: {ShellQuote.Describe(program, arguments, options)}");
        return new ProcessResult(0, "", "");
    }
}

/// <summary>Shows commands the way a shell would take them, for <c>--dry-run</c> and error messages.</summary>
public static class ShellQuote
{
    public static string Describe(string program, IReadOnlyList<string> arguments, ProcessRunOptions? options = null)
    {
        var parts = new List<string>();
        if (options?.WorkingDirectory is { } dir)
            parts.Add($"(cd {Quote(dir)} &&");
        if (options?.Environment is { Count: > 0 } env)
            parts.AddRange(env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={Quote(e.Value)}"));
        parts.Add(Quote(program));
        parts.AddRange(arguments.Select(Quote));
        var line = string.Join(' ', parts);
        return options?.WorkingDirectory is null ? line : line + ")";
    }

    public static string Quote(string word)
    {
        if (word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,@%+".Contains(c)))
            return word;
        return "'" + word.Replace("'", "'\\''") + "'";
    }
}
