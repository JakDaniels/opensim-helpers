// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
using Osctl.Processes;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Builds;

public enum BuildUpdateOutcome
{
    Built,
    UpToDate,
    DryRun,
}

public sealed record BuildUpdateResult(BuildUpdateOutcome Outcome, string? Release, IReadOnlyList<string> Pruned);

/// <summary>
/// <c>osctl build update</c> (PLAN.md "Builds"): fetch, check out, copy in the addons, apply patches, prebuild,
/// compile, copy <c>bin/</c> into a new release, check it, make it current, prune old releases. Nothing that
/// instances use changes until the final switch of <c>current</c>; a failure leaves the current release as it was.
/// </summary>
public sealed class BuildUpdater(
    IProcessRunner runner,
    OsctlPaths paths,
    TextWriter progress,
    TimeProvider time,
    bool dryRun = false,
    bool verbose = false)
{
    /// <summary>Files a usable OpenSim bin/ must have.</summary>
    public static readonly IReadOnlyList<string> RequiredFiles = ["OpenSim.dll", "OpenSim.runtimeconfig.json", "Robust.dll"];

    private const int TailLines = 30;

    private readonly BuildStore store = new(paths);
    private readonly Git git = new(runner);

    public async Task<BuildUpdateResult> UpdateAsync(BuildDefinition def, bool force, CancellationToken ct = default)
    {
        using var buildLock = dryRun ? null : store.Lock(def.Name);
        var src = store.SourceDir(def.Name);

        await Step($"Fetching {def.Repository}");
        await git.CloneOrFetchAsync(def.Repository, src, ct);
        var addonDirs = new List<(AddonDefinition Addon, string Dir)>();
        foreach (var addon in def.Addons)
        {
            var dir = store.AddonDir(def.Name, addon.Name);
            await Step($"Fetching addon {addon.Name} from {addon.Repository}");
            await git.CloneOrFetchAsync(addon.Repository, dir, ct);
            addonDirs.Add((addon, dir));
        }

        if (dryRun && (!Git.IsClone(src) || addonDirs.Any(a => !Git.IsClone(a.Dir))))
        {
            await progress.WriteLineAsync("    (not cloned yet: the rest of the update would follow the clone)");
            return new BuildUpdateResult(BuildUpdateOutcome.DryRun, null, []);
        }

        var commit = await git.ResolveAsync(src, def.Ref, ct);
        var subject = await git.SubjectAsync(src, commit, ct);
        await progress.WriteLineAsync($"    {def.Ref} is {Short(commit)} {subject}");

        var addonRevisions = new List<AddonRevision>();
        foreach (var (addon, dir) in addonDirs)
        {
            var addonCommit = await git.ResolveAsync(dir, addon.Ref, ct);
            await progress.WriteLineAsync($"    addon {addon.Name}: {addon.Ref} is {Short(addonCommit)} {await git.SubjectAsync(dir, addonCommit, ct)}");
            addonRevisions.Add(new AddonRevision(addon.Name, addon.Repository, addon.Ref, addonCommit));
            // Check the addon out now: its patches are read from its working tree.
            await git.CheckoutCleanAsync(dir, addonCommit, ct);
        }

        var patches = CollectPatches(def, addonDirs);
        var fingerprint = ReleaseManifest.ComputeFingerprint(commit, addonRevisions,
            patches.Select(p => p.Applied), def.Prebuild, def.Compile);

        var current = store.Current(def.Name);
        if (!force && current is not null && store.Manifest(def.Name, current)?.Fingerprint == fingerprint)
        {
            await progress.WriteLineAsync($"Build '{def.Name}' is up to date (release {current}); --force builds it again.");
            return new BuildUpdateResult(BuildUpdateOutcome.UpToDate, current, []);
        }

        if (dryRun)
        {
            await progress.WriteLineAsync(
                $"would build a new release: check out {Short(commit)}, copy {def.Addons.Count} addon(s), apply {patches.Count} patch(es), " +
                $"run '{def.Prebuild}' and '{def.Compile}', then make it current");
            return new BuildUpdateResult(BuildUpdateOutcome.DryRun, null, []);
        }

        var release = $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Short(commit)}";
        var logFile = store.LogFile(def.Name, release);
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        await using var log = new StreamWriter(logFile) { AutoFlush = true };
        var tail = new Queue<string>();

        try
        {
            await Step($"Checking out {Short(commit)}");
            await git.CheckoutCleanAsync(src, commit, ct);
            foreach (var (addon, dir) in addonDirs)
                FileTree.Copy(dir, Path.Combine(src, "addon-modules", addon.Name), new HashSet<string> { ".git" });

            foreach (var patch in patches)
            {
                await Step($"Applying {patch.Applied.File}");
                await git.ApplyAsync(src, patch.Path, ct);
            }

            await Step($"Running prebuild (log: {logFile})");
            await RunLoggedAsync(def.Prebuild, src, log, tail, ct);
            await Step("Compiling");
            await RunLoggedAsync(def.Compile, src, log, tail, ct);

            var bin = Path.Combine(src, "bin");
            var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(bin, f))).ToList();
            if (missing.Count > 0)
                throw new OsctlException($"The build finished but bin/ lacks {string.Join(", ", missing)}");

            await Step($"Creating release {release}");
            var final = store.ReleaseDir(def.Name, release);
            var partial = final + BuildStore.PartialSuffix;
            FileTree.Delete(partial);
            FileTree.Copy(bin, Path.Combine(partial, "bin"));
            var manifest = new ReleaseManifest(release, def.Name, time.GetUtcNow(), def.Repository, def.Ref, commit, subject,
                addonRevisions, patches.Select(p => p.Applied).ToList(), def.Prebuild, def.Compile, OsctlVersion());
            Ini.IniWriter.WriteFile(Path.Combine(partial, ReleaseManifest.FileName), manifest.ToIni());
            Directory.Move(partial, final);
        }
        catch (OsctlException)
        {
            FileTree.Delete(store.ReleaseDir(def.Name, release) + BuildStore.PartialSuffix);
            if (tail.Count > 0)
            {
                await progress.WriteLineAsync($"--- last lines of {logFile} ---");
                foreach (var line in tail)
                    await progress.WriteLineAsync(line);
                await progress.WriteLineAsync("---");
            }
            throw;
        }

        store.SetCurrent(def.Name, release);
        var pruned = store.Prune(def.Name, def.KeepReleases);
        await progress.WriteLineAsync($"Release {release} of build '{def.Name}' is now current.");
        foreach (var old in pruned)
            await progress.WriteLineAsync($"    removed old release {old}");
        return new BuildUpdateResult(BuildUpdateOutcome.Built, release, pruned);
    }

    private sealed record PatchFile(string Path, AppliedPatch Applied);

    /// <summary>The addons' patches (in addon order), then the build's own, each directory's *.patch sorted by name.</summary>
    private List<PatchFile> CollectPatches(BuildDefinition def, List<(AddonDefinition Addon, string Dir)> addons)
    {
        var result = new List<PatchFile>();

        void AddFrom(string dir, string label)
        {
            if (!Directory.Exists(dir))
                throw new OsctlException($"Patch directory {dir} ({label}) does not exist");
            foreach (var file in Directory.GetFiles(dir, "*.patch").Order(StringComparer.Ordinal))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
                result.Add(new PatchFile(file, new AppliedPatch($"{label}/{Path.GetFileName(file)}", hash)));
            }
        }

        foreach (var (addon, dir) in addons)
        {
            if (addon.Patches is not null)
                AddFrom(Path.Combine(dir, addon.Patches), $"addon {addon.Name}");
        }
        if (def.Patches is not null)
            AddFrom(Path.Combine(paths.BuildDefinitionsDir, def.Patches), $"build {def.Name}");
        return result;
    }

    private async Task RunLoggedAsync(string command, string dir, StreamWriter log, Queue<string> tail, CancellationToken ct)
    {
        await log.WriteLineAsync($"$ {command}");
        var options = new ProcessRunOptions(
            ChangesSystem: true,
            WorkingDirectory: dir,
            Environment: new Dictionary<string, string>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
            OutputLine: line =>
            {
                log.WriteLine(line);
                tail.Enqueue(line);
                if (tail.Count > TailLines)
                    tail.Dequeue();
                if (verbose)
                    progress.WriteLine(line);
            });

        var result = await runner.RunAsync("sh", ["-c", command], options, ct);
        if (result.NotFound)
            throw new OsctlException("sh not found");
        if (result.ExitCode != 0)
            throw new OsctlException($"'{command}' failed with exit code {result.ExitCode}");
    }

    private Task Step(string text) => progress.WriteLineAsync($"==> {text}");

    private static string Short(string commit) => commit.Length > 10 ? commit[..10] : commit;

    private static string OsctlVersion() =>
        typeof(BuildUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
