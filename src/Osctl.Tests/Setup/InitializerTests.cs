// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Cli;
using Osctl.Ini;
using Osctl.Settings;
using Osctl.Setup;

namespace Osctl.Tests.Setup;

public class InitializerTests
{
    [Fact]
    public void CreatesTheLayoutAndDefaultFiles()
    {
        using var dir = new TempDir();
        var paths = new OsctlPaths(dir.Path);

        var steps = new Initializer(paths).Run(dryRun: false);

        Assert.All(steps, s => Assert.Equal(InitAction.Created, s.Action));
        Assert.True(Directory.Exists(paths.InstancesDir));
        Assert.True(Directory.Exists(Path.Combine(paths.Cache, "assets")));
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(paths.RegionDefaultsFile));
        Assert.True(File.Exists(Path.Combine(paths.GlobalOverridesDir, "README.txt")));
        Assert.Equal(Initializer.SecretsMode, File.GetUnixFileMode(paths.SecretsFile));
    }

    [Fact]
    public void NeverOverwritesAndCanRunAgain()
    {
        using var dir = new TempDir();
        var paths = new OsctlPaths(dir.Path);
        dir.Write("etc/osctl/osctl.ini", "[Ports]\nRegionPortMin = 9500\n");

        new Initializer(paths).Run(dryRun: false);
        var again = new Initializer(paths).Run(dryRun: false);

        Assert.All(again, s => Assert.Equal(InitAction.Kept, s.Action));
        Assert.Equal("[Ports]\nRegionPortMin = 9500\n", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void DryRunChangesNothing()
    {
        using var dir = new TempDir();
        var paths = new OsctlPaths(dir.Path);

        var steps = new Initializer(paths).Run(dryRun: true);

        Assert.Contains(steps, s => s.Path == paths.SettingsFile && s.Action == InitAction.Created);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void TheDefaultSettingsFileHoldsExactlyTheDefaults()
    {
        using var dir = new TempDir();
        var paths = new OsctlPaths(dir.Path);
        new Initializer(paths).Run(dryRun: false);

        var result = SettingsLoader.Load(paths);

        Assert.True(result.SettingsFileFound);
        Assert.Empty(result.Warnings);
        Assert.Equal(OsctlSettings.Default, result.Settings);
    }

    [Fact]
    public void TheDefaultRegionSettingsAreMiosValues()
    {
        var doc = IniParser.Parse(DefaultFiles.Read(DefaultFiles.RegionDefaults));
        var section = doc["Region Defaults"]!;
        Assert.Equal("SYSTEMIP", section["ExternalHostName"]);
        Assert.Equal("15000", section["MaxPrims"]);
        Assert.Equal("100", section["MaxAgents"]);
        Assert.Equal(9, section.Count);
    }

    [Fact]
    public async Task TheCommandReportsWhatItDid()
    {
        using var dir = new TempDir();
        var output = new StringWriter();
        var env = new CliEnvironment(new FakeRunner(), output, new StringWriter());

        Assert.Equal(0, await CliApp.RunAsync(["init", "--root", dir.Path], env));
        Assert.Contains($"created file {Path.Combine(dir.Path, "etc/osctl/osctl.ini")}", output.ToString());

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await CliApp.RunAsync(["init", "--root", dir.Path], env));
        Assert.Equal("Everything is already in place.", output.ToString().Trim());
    }

    [Fact]
    public async Task TheCommandDryRunSaysWould()
    {
        using var dir = new TempDir();
        var output = new StringWriter();
        var env = new CliEnvironment(new FakeRunner(), output, new StringWriter());

        Assert.Equal(0, await CliApp.RunAsync(["--dry-run", "init", "--root", dir.Path], env));
        Assert.Contains("would create directory", output.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task ExpectedErrorsAreOneLineAndExitCodeTwo()
    {
        using var dir = new TempDir();
        dir.Write("etc/osctl", "a file where the directory should be");
        var error = new StringWriter();
        var env = new CliEnvironment(new FakeRunner(), new StringWriter(), error);

        var exit = await CliApp.RunAsync(["init", "--root", dir.Path], env);

        Assert.Equal(CliApp.ExitError, exit);
        Assert.StartsWith("osctl: ", error.ToString());
        Assert.Single(error.ToString().Trim().Split('\n'));
    }
}
