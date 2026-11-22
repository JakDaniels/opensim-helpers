// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Builds;
using Osctl.Processes;
using Osctl.Settings;

namespace Osctl.Tests.Builds;

/// <summary>
/// A fake OpenSim upstream (and an addon) as local git repositories, and an osctl root. The fake "build" is a
/// shell script that makes the files a real bin/ has, plus a few that tell the tests what went in:
/// <c>bin/version.txt</c> (from the source, so patches show), <c>bin/addons.txt</c> (what is in addon-modules/).
/// </summary>
internal sealed class BuildFixture : IDisposable
{
    public const string FakeCompile = "sh fakebuild.sh";

    private readonly TempDir dir = new();

    public BuildFixture()
    {
        Paths = new OsctlPaths(Path.Combine(dir.Path, "root"));
        Upstream = Path.Combine(dir.Path, "upstream");
        Addon = Path.Combine(dir.Path, "addon");

        dir.Write("upstream/version.txt", "1\n");
        dir.Write("upstream/runprebuild.sh", "echo prebuild ran > prebuild.txt\n");
        dir.Write("upstream/fakebuild.sh", """
            set -e
            echo compiling
            mkdir -p bin
            echo built > bin/OpenSim.dll
            echo '{}' > bin/OpenSim.runtimeconfig.json
            echo built > bin/Robust.dll
            printf '#!/bin/sh\n' > bin/OpenSim
            chmod 755 bin/OpenSim
            cp version.txt bin/version.txt
            ls addon-modules 2>/dev/null > bin/addons.txt || true
            """);
        dir.Write("upstream/addon-modules/README", "addons go here\n");
        GitIn(Upstream, "init -q -b master", "add -A", "commit -q -m first");

        dir.Write("addon/prebuild.xml", "<Project/>\n");
        dir.Write("addon/core-patches/0001-version.patch", """
            diff --git a/version.txt b/version.txt
            --- a/version.txt
            +++ b/version.txt
            @@ -1 +1 @@
            -1
            +1-patched
            """ + "\n");
        GitIn(Addon, "init -q -b main", "add -A", "commit -q -m addon");
    }

    public OsctlPaths Paths { get; }

    public string Upstream { get; }

    public string Addon { get; }

    public BuildStore Store => new(Paths);

    public StringWriter Output { get; } = new();

    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public BuildDefinition Definition(string name = "test", string gitRef = "HEAD", int keep = 3, params AddonDefinition[] addons) =>
        new(name, Upstream, addons, gitRef, KeepReleases: keep, Compile: FakeCompile);

    public BuildUpdater Updater(bool dryRun = false, IProcessRunner? runner = null)
    {
        var real = runner ?? new ProcessRunner();
        return new BuildUpdater(dryRun ? new DryRunProcessRunner(real, Output) : real, Paths, Output, Time, dryRun);
    }

    /// <summary>Changes version.txt upstream and commits it; returns the new commit.</summary>
    public string CommitUpstream(string version)
    {
        File.WriteAllText(Path.Combine(Upstream, "version.txt"), version + "\n");
        GitIn(Upstream, "add -A", $"commit -q -m v{version}");
        return Head(Upstream);
    }

    /// <summary>Commits a change to a file the fake build ignores (so addon patches still apply).</summary>
    public void CommitUpstreamOther(string text)
    {
        File.WriteAllText(Path.Combine(Upstream, "other.txt"), text + "\n");
        GitIn(Upstream, "add -A", "commit -q -m other");
    }

    public void TagUpstream(string tag) => GitIn(Upstream, $"tag {tag}");

    public static string Head(string repo) => Git(repo, "rev-parse HEAD").Trim();

    public string ReleaseFile(string release, string file) =>
        File.ReadAllText(Path.Combine(Store.ReleaseDir("test", release), "bin", file)).Trim();

    public void Dispose() => dir.Dispose();

    private static void GitIn(string repo, params string[] commands)
    {
        foreach (var command in commands)
            Git(repo, command);
    }

    private static string Git(string repo, string command)
    {
        var info = new System.Diagnostics.ProcessStartInfo("sh", ["-c", $"git {command}"])
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["GIT_AUTHOR_NAME"] = info.Environment["GIT_COMMITTER_NAME"] = "osctl tests";
        info.Environment["GIT_AUTHOR_EMAIL"] = info.Environment["GIT_COMMITTER_EMAIL"] = "tests@example.invalid";
        info.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {command} failed: {error}");
        return output;
    }
}

/// <summary>A clock the tests move by hand, so release names are known.</summary>
internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
