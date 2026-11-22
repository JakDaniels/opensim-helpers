// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Cli;
using Osctl.Doctor;
using Osctl.Settings;

namespace Osctl.Tests.Doctor;

public class DoctorTests
{
    /// <summary>A machine with everything installed and set up.</summary>
    private static FakeRunner HealthyMachine() => new FakeRunner()
        .Answer("git --version", "git version 2.53.0\n")
        .Answer("tmux -V", "tmux 3.6\n")
        .Answer("dotnet --list-sdks", "8.0.134 [/usr/lib/dotnet/sdk]\n10.0.115 [/usr/lib/dotnet/sdk]\n")
        .Answer("dotnet --list-runtimes",
            "Microsoft.AspNetCore.App 8.0.34 [/x]\nMicrosoft.NETCore.App 8.0.34 [/x]\nMicrosoft.NETCore.App 10.0.15 [/x]\n")
        .Answer("ldconfig -p", "\tlibgdiplus.so.0 (libc6,x86-64) => /lib/libgdiplus.so.0\n")
        .Answer("mariadb --version", "mariadb from 11.8.6-MariaDB\n")
        .Answer("systemctl --version", "systemd 259 (259.9)\n+PAM\n")
        .Answer("getent passwd opensim", "opensim:x:998:998::/var/lib/osctl:/usr/sbin/nologin\n")
        .Answer("id -nG", "someone opensim sudo\n");

    private static async Task<IReadOnlyList<CheckResult>> Run(FakeRunner runner, TempDir dir)
    {
        dir.Write("etc/osctl/osctl.ini", "[Ports]\nRegionPortMin = 9000\n");
        return await new Osctl.Doctor.Doctor(runner, new OsctlPaths(dir.Path)).RunAsync();
    }

    private static CheckResult Check(IReadOnlyList<CheckResult> results, string name) => results.Single(r => r.Name == name);

    [Fact]
    public async Task AHealthyMachinePasses()
    {
        using var dir = new TempDir();
        var results = await Run(HealthyMachine(), dir);
        Assert.All(results, r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Equal("8.0.34", Check(results, ".NET 8 runtime").Detail);
    }

    [Fact]
    public async Task OldTmuxFails()
    {
        using var dir = new TempDir();
        var results = await Run(HealthyMachine().Answer("tmux -V", "tmux 3.2a\n"), dir);
        Assert.Equal(CheckStatus.Failed, Check(results, "tmux").Status);
    }

    [Fact]
    public async Task OnlyANewerRuntimeIsNotEnoughForOpenSim()
    {
        using var dir = new TempDir();
        var results = await Run(HealthyMachine().Answer("dotnet --list-runtimes", "Microsoft.NETCore.App 10.0.15 [/x]\n"), dir);
        Assert.Equal(CheckStatus.Failed, Check(results, ".NET 8 runtime").Status);
    }

    [Fact]
    public async Task MissingOptionalThingsAreWarnings()
    {
        using var dir = new TempDir();
        var runner = HealthyMachine()
            .Answer("ldconfig -p", "\tlibc.so.6\n")
            .Answer("mariadb --version", "", exitCode: 127)
            .Answer("id -nG", "someone sudo\n");
        var results = await Run(runner, dir);

        Assert.Equal(CheckStatus.Warning, Check(results, "libgdiplus").Status);
        Assert.Equal(CheckStatus.Warning, Check(results, "MariaDB client").Status);
        Assert.Equal(CheckStatus.Warning, Check(results, "account").Status);
        Assert.DoesNotContain(results, r => r.Status == CheckStatus.Failed);
    }

    [Fact]
    public async Task TheMysqlClientWillDo()
    {
        using var dir = new TempDir();
        var runner = new FakeRunner();
        foreach (var call in new[] { "git --version", "tmux -V" })
            runner.Answer(call, "x 3.6");
        runner.Answer("mysql --version", "mysql  Ver 8.0.40\n");
        var results = await Run(runner, dir);
        Assert.Equal(CheckStatus.Ok, Check(results, "MariaDB client").Status);
    }

    [Fact]
    public async Task ABrokenSettingsFileFails()
    {
        using var dir = new TempDir();
        dir.Write("etc/osctl/osctl.ini", "[Ports\n");
        var results = await new Osctl.Doctor.Doctor(HealthyMachine(), new OsctlPaths(dir.Path)).RunAsync();
        Assert.Equal(CheckStatus.Failed, Check(results, "settings").Status);
    }

    [Theory]
    [InlineData("tmux 3.3a", "3.3")]
    [InlineData("tmux next-3.4", "3.4")]
    [InlineData("8.0.134 [/usr/lib/dotnet/sdk]", "8.0.134")]
    public void ParsesVersions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), Osctl.Doctor.Doctor.ParseVersion(text));

    [Fact]
    public async Task TheCommandExitsNonZeroOnFailure()
    {
        using var dir = new TempDir();
        var output = new StringWriter();
        var env = new CliEnvironment(HealthyMachine().Answer("tmux -V", "tmux 2.9\n"), output, new StringWriter());

        var exit = await CliApp.RunAsync(["doctor", "--root", dir.Path], env);

        Assert.Equal(1, exit);
        Assert.Contains("[FAIL]  tmux", output.ToString());
    }

    [Fact]
    public async Task TheCommandPrintsJson()
    {
        using var dir = new TempDir();
        var output = new StringWriter();
        var env = new CliEnvironment(HealthyMachine(), output, new StringWriter());

        var exit = await CliApp.RunAsync(["--root", dir.Path, "doctor", "--json"], env);

        Assert.Equal(0, exit);
        var parsed = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Contains(parsed.RootElement.EnumerateArray(), e => e.GetProperty("name").GetString() == "tmux");
    }
}
