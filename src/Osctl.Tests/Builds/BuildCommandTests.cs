// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Builds;
using Osctl.Cli;
using Osctl.Processes;

namespace Osctl.Tests.Builds;

public class BuildDefinitionTests
{
    [Fact]
    public void RoundTripsThroughItsFile()
    {
        using var dir = new TempDir();
        var definition = new BuildDefinition("sas-dev", "git://opensimulator.org/git/opensim",
            [new AddonDefinition("os-webrtc-sas", "git@github.com:JakDaniels/os-webrtc-sas.git", "main", "core-patches")],
            "master", "local-patches", 5);
        var path = Path.Combine(dir.Path, "sas-dev.ini");
        Osctl.Ini.IniWriter.WriteFile(path, definition.ToIni());

        var back = BuildDefinition.Load(path);

        Assert.Equal(definition with { Addons = [] }, back with { Addons = [] });
        Assert.Equal(definition.Addons, back.Addons);
    }

    [Fact]
    public void MissingValuesGetDefaults()
    {
        using var dir = new TempDir();
        var path = dir.Write("b.ini", "[Build]\nRepository = /src/opensim\n[Addon x]\nRepository = /src/x\n");

        var d = BuildDefinition.Load(path);

        Assert.Equal(BuildDefinition.DefaultRef, d.Ref);
        Assert.Equal(BuildDefinition.DefaultCompile, d.Compile);
        Assert.Equal(BuildDefinition.DefaultKeepReleases, d.KeepReleases);
        Assert.Equal(BuildDefinition.DefaultRef, Assert.Single(d.Addons).Ref);
    }

    [Theory]
    [InlineData("[Build]\nRef = master\n", "Repository is missing")]
    [InlineData("[Other]\nx = 1\n", "no [Build] section")]
    [InlineData("[Build]\nRepository = r\nKeepReleases = 0\n", "KeepReleases")]
    [InlineData("[Build]\nRepository = r\n[Addon bad/name]\nRepository = r\n", "not a valid addon name")]
    public void BadFilesAreErrors(string text, string message)
    {
        using var dir = new TempDir();
        var path = dir.Write("b.ini", text);
        Assert.Contains(message, Assert.Throws<OsctlException>(() => BuildDefinition.Load(path)).Message);
    }

    [Theory]
    [InlineData("master", true)]
    [InlineData("0.9.3", true)]
    [InlineData("sas_dev-2", true)]
    [InlineData("-x", false)]
    [InlineData(".hidden", false)]
    [InlineData("a/b", false)]
    [InlineData("with space", false)]
    [InlineData("", false)]
    public void NameRules(string name, bool valid) => Assert.Equal(valid, Names.IsValid(name));
}

public class BuildCommandTests
{
    private static async Task<(int Exit, string Out, string Err)> Osctl(BuildFixture f, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnvironment(new ProcessRunner(), output, error, f.Time);
        var exit = await CliApp.RunAsync([.. args, "--root", f.Paths.Root], env);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task AddUpdateListShowRollbackRemove()
    {
        using var f = new BuildFixture();
        Assert.Equal(0, (await Osctl(f, "build", "add", "test", "--repo", f.Upstream, "--keep", "4")).Exit);
        // The fake compile, by hand as a user would edit the file.
        var file = f.Store.DefinitionFile("test");
        File.WriteAllText(file, File.ReadAllText(file).Replace(BuildDefinition.DefaultCompile, BuildFixture.FakeCompile));
        Assert.Equal(0, (await Osctl(f, "build", "addon", "add", "test", "my-addon", "--repo", f.Addon, "--patches", "core-patches")).Exit);

        var update = await Osctl(f, "build", "update", "test");
        Assert.Equal(0, update.Exit);
        Assert.Contains("==> Applying addon my-addon/0001-version.patch", update.Out);
        var first = f.Store.Current("test")!;

        f.Time.Advance(TimeSpan.FromMinutes(1));
        f.CommitUpstreamOther("2");
        var update2 = await Osctl(f, "build", "update");
        Assert.True(update2.Exit == 0, update2.Err);
        var second = f.Store.Current("test")!;
        Assert.NotEqual(first, second);

        var list = await Osctl(f, "build", "list");
        Assert.Contains("BUILD", list.Out);
        Assert.Contains(second, list.Out);

        var show = await Osctl(f, "build", "show", "test");
        Assert.Contains($"* {second}", show.Out);
        Assert.Contains($"  {first}", show.Out);
        Assert.Contains("Addon:      my-addon", show.Out);

        var json = System.Text.Json.JsonDocument.Parse((await Osctl(f, "build", "list", "--json")).Out);
        Assert.Equal(2, json.RootElement[0].GetProperty("releases").GetArrayLength());

        var rollback = await Osctl(f, "build", "rollback", "test");
        Assert.True(rollback.Exit == 0, rollback.Err);
        Assert.Equal(first, f.Store.Current("test"));
        Assert.Equal(0, (await Osctl(f, "build", "rollback", "test", "--to", second)).Exit);
        Assert.Equal(second, f.Store.Current("test"));

        var refused = await Osctl(f, "build", "remove", "test");
        Assert.Equal(CliApp.ExitFailure, refused.Exit);
        Assert.Contains("--yes", refused.Out);
        Assert.True(f.Store.Exists("test"));

        Assert.Equal(0, (await Osctl(f, "build", "remove", "test", "--yes")).Exit);
        Assert.False(f.Store.Exists("test"));
        Assert.False(Directory.Exists(f.Store.Dir("test")));
    }

    [Fact]
    public async Task ErrorsAreOneLine()
    {
        using var f = new BuildFixture();
        await Osctl(f, "build", "add", "test", "--repo", f.Upstream);

        var duplicate = await Osctl(f, "build", "add", "test", "--repo", f.Upstream);
        Assert.Equal(CliApp.ExitError, duplicate.Exit);
        Assert.Contains("already exists", duplicate.Err);

        var bad = await Osctl(f, "build", "add", "a b", "--repo", f.Upstream);
        Assert.Contains("not a valid build name", bad.Err);

        var missing = await Osctl(f, "build", "show", "nope");
        Assert.Contains("There is no build 'nope'", missing.Err);

        var rollback = await Osctl(f, "build", "rollback", "test");
        Assert.Contains("no release older", rollback.Err);
    }

    [Fact]
    public async Task ALocalRepositoryIsStoredAsAFullPath()
    {
        using var f = new BuildFixture();
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(f.Upstream, "..", "upstream"));
        await Osctl(f, "build", "add", "test", "--repo", relative);
        Assert.Equal(f.Upstream, f.Store.Load("test").Repository);

        await Osctl(f, "build", "add", "web", "--repo", "git://opensimulator.org/git/opensim");
        Assert.Equal("git://opensimulator.org/git/opensim", f.Store.Load("web").Repository);
    }

    [Fact]
    public async Task DryRunAddWritesNothing()
    {
        using var f = new BuildFixture();
        var r = await Osctl(f, "--dry-run", "build", "add", "test", "--repo", f.Upstream, "--ref", "master");
        Assert.Equal(0, r.Exit);
        Assert.Contains("would write", r.Out);
        Assert.Contains("Ref = master", r.Out);
        Assert.False(f.Store.Exists("test"));
    }

    [Fact]
    public async Task UpdateWithNoBuildsSaysHowToAddOne()
    {
        using var f = new BuildFixture();
        var r = await Osctl(f, "build", "update");
        Assert.Equal(CliApp.ExitError, r.Exit);
        Assert.Contains("osctl build add", r.Err);
    }
}
