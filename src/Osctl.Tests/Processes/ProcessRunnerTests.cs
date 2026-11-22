// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Processes;

namespace Osctl.Tests.Processes;

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunsAProgramAndCapturesOutput()
    {
        using var dir = new TempDir();
        var result = await new ProcessRunner().RunAsync("sh", ["-c", "pwd; echo \"$OSCTL_X\"; echo err >&2; exit 3"],
            new ProcessRunOptions(WorkingDirectory: dir.Path, Environment: new Dictionary<string, string> { ["OSCTL_X"] = "x y" }));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal($"{dir.Path}\nx y\n", result.StandardOutput);
        Assert.Equal("err\n", result.StandardError);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AMissingProgramIsNotFound()
    {
        var result = await new ProcessRunner().RunAsync("osctl-no-such-program", []);
        Assert.True(result.NotFound);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task DryRunPrintsChangesAndRunsQueries()
    {
        var inner = new FakeRunner().Answer("git --version", "git version 2.53.0\n");
        var output = new StringWriter();
        var runner = new DryRunProcessRunner(inner, output);

        var query = await runner.RunAsync("git", ["--version"]);
        var change = await runner.RunAsync("git", ["clone", "git://opensimulator.org/git/opensim", "my src"],
            new ProcessRunOptions(ChangesSystem: true, WorkingDirectory: "/var/lib/osctl/builds/master"));

        Assert.Equal("git version 2.53.0\n", query.StandardOutput);
        Assert.True(change.Succeeded);
        Assert.Equal(["git --version"], inner.Calls);
        Assert.Equal(
            "would run: (cd /var/lib/osctl/builds/master && git clone git://opensimulator.org/git/opensim 'my src')\n",
            output.ToString());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("-c", "-c")]
    [InlineData("/a/b.ini", "/a/b.ini")]
    [InlineData("two words", "'two words'")]
    [InlineData("", "''")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$HOME", "'$HOME'")]
    public void QuotesForTheShell(string word, string quoted) => Assert.Equal(quoted, ShellQuote.Quote(word));
}
