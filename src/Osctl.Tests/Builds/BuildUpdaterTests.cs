// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Builds;

namespace Osctl.Tests.Builds;

// Real git against local repositories (BuildFixture); the compile step is a shell script.
public class BuildUpdaterTests
{
    [Fact]
    public async Task TheFirstUpdateBuildsAReleaseAndMakesItCurrent()
    {
        using var f = new BuildFixture();
        var head = BuildFixture.Head(f.Upstream);

        var result = await f.Updater().UpdateAsync(f.Definition(), force: false);

        Assert.Equal(BuildUpdateOutcome.Built, result.Outcome);
        Assert.Equal($"20261122-120000-{head[..10]}", result.Release);
        Assert.Equal(result.Release, f.Store.Current("test"));
        Assert.Equal("1", f.ReleaseFile(result.Release!, "version.txt"));
        Assert.True(File.Exists(Path.Combine(f.Store.CurrentBin("test"), "OpenSim.dll")));
        Assert.True(File.GetUnixFileMode(Path.Combine(f.Store.CurrentBin("test"), "OpenSim")).HasFlag(UnixFileMode.UserExecute));

        var manifest = f.Store.Manifest("test", result.Release!)!;
        Assert.Equal(head, manifest.Commit);
        Assert.Equal("first", manifest.Subject);
        Assert.Contains("compiling", File.ReadAllText(f.Store.LogFile("test", result.Release!)));
        Assert.Contains("is now current", f.Output.ToString());
    }

    [Fact]
    public async Task NothingChangedMeansUpToDateUnlessForced()
    {
        using var f = new BuildFixture();
        var first = await f.Updater().UpdateAsync(f.Definition(), force: false);
        f.Time.Advance(TimeSpan.FromMinutes(1));

        var again = await f.Updater().UpdateAsync(f.Definition(), force: false);
        Assert.Equal(BuildUpdateOutcome.UpToDate, again.Outcome);
        Assert.Equal(first.Release, again.Release);

        var forced = await f.Updater().UpdateAsync(f.Definition(), force: true);
        Assert.Equal(BuildUpdateOutcome.Built, forced.Outcome);
        Assert.NotEqual(first.Release, forced.Release);
    }

    [Fact]
    public async Task ANewUpstreamCommitBuildsANewReleaseAndOldOnesArePruned()
    {
        using var f = new BuildFixture();
        var definition = f.Definition(keep: 2);
        var r1 = (await f.Updater().UpdateAsync(definition, false)).Release!;
        foreach (var version in new[] { "2", "3" })
        {
            f.Time.Advance(TimeSpan.FromMinutes(1));
            f.CommitUpstream(version);
            await f.Updater().UpdateAsync(definition, false);
        }

        var releases = f.Store.Releases("test");
        Assert.Equal(2, releases.Count);
        Assert.DoesNotContain(r1, releases);
        Assert.False(File.Exists(f.Store.LogFile("test", r1)));
        Assert.Equal("3", File.ReadAllText(Path.Combine(f.Store.CurrentBin("test"), "version.txt")).Trim());
    }

    [Fact]
    public void PruningNeverRemovesTheCurrentRelease()
    {
        using var f = new BuildFixture();
        foreach (var name in new[] { "20260101-000000-a", "20260102-000000-b", "20260103-000000-c" })
            Directory.CreateDirectory(Path.Combine(f.Store.ReleaseDir("test", name), "bin"));
        f.Store.SetCurrent("test", "20260101-000000-a");

        var removed = f.Store.Prune("test", keep: 1);

        Assert.Equal(["20260102-000000-b"], removed);
        Assert.Equal(["20260101-000000-a", "20260103-000000-c"], f.Store.Releases("test"));
    }

    [Fact]
    public async Task ATagOrABranchCanBeBuilt()
    {
        using var f = new BuildFixture();
        f.TagUpstream("v1");
        f.CommitUpstream("2");

        var tagged = await f.Updater().UpdateAsync(f.Definition(gitRef: "v1"), false);
        Assert.Equal("1", f.ReleaseFile(tagged.Release!, "version.txt"));

        f.Time.Advance(TimeSpan.FromMinutes(1));
        var branch = await f.Updater().UpdateAsync(f.Definition(gitRef: "master"), false);
        Assert.Equal("2", f.ReleaseFile(branch.Release!, "version.txt"));
    }

    [Fact]
    public async Task AnUnknownRefIsAnError()
    {
        using var f = new BuildFixture();
        var e = await Assert.ThrowsAsync<OsctlException>(() => f.Updater().UpdateAsync(f.Definition(gitRef: "no-such-branch"), false));
        Assert.Contains("not a branch, tag or commit", e.Message);
    }

    [Fact]
    public async Task AddonsAreCopiedInAndTheirPatchesApplied()
    {
        using var f = new BuildFixture();
        var addon = new AddonDefinition("my-addon", f.Addon, Patches: "core-patches");

        var result = await f.Updater().UpdateAsync(f.Definition(addons: addon), false);

        Assert.Equal("1-patched", f.ReleaseFile(result.Release!, "version.txt"));
        Assert.Contains("my-addon", f.ReleaseFile(result.Release!, "addons.txt"));
        var src = f.Store.SourceDir("test");
        Assert.True(File.Exists(Path.Combine(src, "addon-modules", "my-addon", "prebuild.xml")));
        Assert.False(Directory.Exists(Path.Combine(src, "addon-modules", "my-addon", ".git")));

        var manifest = f.Store.Manifest("test", result.Release!)!;
        Assert.Equal(BuildFixture.Head(f.Addon), Assert.Single(manifest.Addons).Commit);
        Assert.Equal("addon my-addon/0001-version.patch", Assert.Single(manifest.Patches).File);
    }

    [Fact]
    public async Task APatchThatDoesNotApplyStopsTheUpdateAndKeepsTheCurrentRelease()
    {
        using var f = new BuildFixture();
        var good = (await f.Updater().UpdateAsync(f.Definition(), false)).Release;
        f.CommitUpstream("other");
        f.Time.Advance(TimeSpan.FromMinutes(1));

        var addon = new AddonDefinition("my-addon", f.Addon, Patches: "core-patches");
        var e = await Assert.ThrowsAsync<OsctlException>(() => f.Updater().UpdateAsync(f.Definition(addons: addon), false));

        Assert.Contains("does not apply", e.Message);
        Assert.Equal(good, f.Store.Current("test"));
        Assert.Single(f.Store.Releases("test"));
    }

    [Fact]
    public async Task AFailedCompileShowsTheEndOfTheLogAndChangesNothing()
    {
        using var f = new BuildFixture();
        var definition = f.Definition() with { Compile = "echo the compiler says no; exit 4" };

        var e = await Assert.ThrowsAsync<OsctlException>(() => f.Updater().UpdateAsync(definition, false));

        Assert.Contains("exit code 4", e.Message);
        Assert.Contains("the compiler says no", f.Output.ToString());
        Assert.Null(f.Store.Current("test"));
        Assert.Empty(f.Store.Releases("test"));
        Assert.False(Directory.Exists(f.Store.ReleasesDir("test")) && Directory.GetDirectories(f.Store.ReleasesDir("test")).Length > 0);
    }

    [Fact]
    public async Task ABuildWithoutTheMainFilesIsRefused()
    {
        using var f = new BuildFixture();
        var definition = f.Definition() with { Compile = "mkdir -p bin && echo x > bin/OpenSim.dll" };

        var e = await Assert.ThrowsAsync<OsctlException>(() => f.Updater().UpdateAsync(definition, false));

        Assert.Contains("OpenSim.runtimeconfig.json, Robust.dll", e.Message);
        Assert.Null(f.Store.Current("test"));
    }

    [Fact]
    public async Task ADryRunBeforeTheFirstCloneCreatesNothing()
    {
        using var f = new BuildFixture();

        var result = await f.Updater(dryRun: true).UpdateAsync(f.Definition(), false);

        Assert.Equal(BuildUpdateOutcome.DryRun, result.Outcome);
        Assert.Contains($"would run: git clone --no-checkout {f.Upstream}", f.Output.ToString());
        Assert.False(Directory.Exists(f.Paths.Root));
    }

    [Fact]
    public async Task ADryRunAfterAnUpdateSaysWhatWouldBeBuilt()
    {
        using var f = new BuildFixture();
        await f.Updater().UpdateAsync(f.Definition(), false);
        f.CommitUpstream("2");
        var before = f.Store.Releases("test");

        // The dry run does not fetch, so it sees the clone as it was: nothing new to build.
        var result = await f.Updater(dryRun: true).UpdateAsync(f.Definition(), force: true);

        Assert.Equal(BuildUpdateOutcome.DryRun, result.Outcome);
        Assert.Contains("would build a new release", f.Output.ToString());
        Assert.Equal(before, f.Store.Releases("test"));
    }

    [Fact]
    public void OnlyOneUpdateOfABuildAtATime()
    {
        using var f = new BuildFixture();
        using var held = f.Store.Lock("test");
        var e = Assert.Throws<OsctlException>(() => f.Store.Lock("test"));
        Assert.Contains("busy", e.Message);
    }
}
