// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Processes;

namespace Osctl.Tests;

/// <summary>A temporary directory, removed when disposed.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "osctl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes a file (making its directories) and returns its full path.</summary>
    public string Write(string relative, string text)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Answers commands from a table; anything not in it is "not found". Records what was run.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> answers = [];

    public List<string> Calls { get; } = [];

    private readonly List<(string Prefix, ProcessResult Result)> prefixes = [];

    public FakeRunner Answer(string commandLine, string output, int exitCode = 0)
    {
        answers[commandLine] = new ProcessResult(exitCode, output, "");
        return this;
    }

    /// <summary>Answers any command line starting with <paramref name="prefix"/> (when no exact answer matches).</summary>
    public FakeRunner AnswerPrefix(string prefix, string output, int exitCode = 0)
    {
        prefixes.Add((prefix, new ProcessResult(exitCode, output, "")));
        return this;
    }

    public List<ProcessRunOptions?> Options { get; } = [];

    public Task<ProcessResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var line = string.Join(' ', [program, .. arguments]);
        Calls.Add(line);
        Options.Add(options);
        if (answers.TryGetValue(line, out var result))
            return Task.FromResult(result);
        var prefixed = prefixes.FirstOrDefault(p => line.StartsWith(p.Prefix, StringComparison.Ordinal));
        return Task.FromResult(prefixed.Result ?? ProcessResult.Missing(program));
    }
}

internal static class RepoPaths
{
    /// <summary>
    /// An OpenSim <c>bin/</c> directory with the distribution's ini files, for the tests that read real files:
    /// <c>OSCTL_TEST_OPENSIM_BIN</c>, else <c>../opensim/bin</c> next to this repository (as in the
    /// SpatialAudioServer reference tree). Null when there is none.
    /// </summary>
    public static string? OpenSimBin()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OSCTL_TEST_OPENSIM_BIN");
        if (!string.IsNullOrEmpty(fromEnv))
            return Directory.Exists(fromEnv) ? fromEnv : null;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "src", "Osctl.slnx")))
            {
                var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir.FullName, "..", "opensim", "bin"));
                return File.Exists(System.IO.Path.Combine(candidate, "OpenSimDefaults.ini")) ? candidate : null;
            }
        }
        return null;
    }
}
