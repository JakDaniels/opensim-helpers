// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Builds;

/// <summary>
/// The builds on disk (PLAN.md "Layout on disk"):
/// <code>
/// /etc/osctl/builds/&lt;name&gt;.ini            the definition
/// /var/lib/osctl/builds/&lt;name&gt;/src/        OpenSim's git clone (osctl's own: cleaned before each build)
///                              addons/&lt;addon&gt;/  each addon's git clone
///                              releases/&lt;release&gt;/bin/, release.ini
///                              current -> releases/&lt;release&gt;
///                              logs/&lt;release&gt;.log
///                              .lock
/// </code>
/// Release names are <c>yyyyMMdd-HHmmss-&lt;commit&gt;</c>, so sorting them by name sorts them by age.
/// A release being built lives in <c>releases/&lt;release&gt;.partial</c> until it is complete.
/// </summary>
public sealed class BuildStore(OsctlPaths paths)
{
    public const string PartialSuffix = ".partial";

    public string DefinitionFile(string build) => Path.Combine(paths.BuildDefinitionsDir, build + ".ini");

    public string Dir(string build) => Path.Combine(paths.BuildsDir, build);

    public string SourceDir(string build) => Path.Combine(Dir(build), "src");

    public string AddonDir(string build, string addon) => Path.Combine(Dir(build), "addons", addon);

    public string ReleasesDir(string build) => Path.Combine(Dir(build), "releases");

    public string ReleaseDir(string build, string release) => Path.Combine(ReleasesDir(build), release);

    public string CurrentLink(string build) => Path.Combine(Dir(build), "current");

    public string LogFile(string build, string release) => Path.Combine(Dir(build), "logs", release + ".log");

    /// <summary>The bin/ directory instances of this build run from: <c>current/bin</c>.</summary>
    public string CurrentBin(string build) => Path.Combine(CurrentLink(build), "bin");

    public bool Exists(string build) => File.Exists(DefinitionFile(build));

    public IReadOnlyList<string> Names() =>
        Directory.Exists(paths.BuildDefinitionsDir)
            ? Directory.GetFiles(paths.BuildDefinitionsDir, "*.ini")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(Osctl.Names.IsValid)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    public BuildDefinition Load(string build)
    {
        Osctl.Names.Check(build, "build");
        var file = DefinitionFile(build);
        if (!File.Exists(file))
            throw new OsctlException($"There is no build '{build}' (no {file}); 'osctl build list' shows the builds");
        return BuildDefinition.Load(file);
    }

    public void Save(BuildDefinition definition) =>
        IniWriter.WriteFile(DefinitionFile(definition.Name), definition.ToIni(),
            [$"osctl build '{definition.Name}'. Edit by hand or with 'osctl build ...'; see PLAN.md \"Builds\"."]);

    /// <summary>The complete releases, oldest first.</summary>
    public IReadOnlyList<string> Releases(string build)
    {
        var dir = ReleasesDir(build);
        return Directory.Exists(dir)
            ? Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.EndsWith(PartialSuffix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
    }

    public string? Current(string build)
    {
        var link = new FileInfo(CurrentLink(build));
        return link.LinkTarget is { } target ? Path.GetFileName(target.TrimEnd('/')) : null;
    }

    public ReleaseManifest? Manifest(string build, string release)
    {
        var file = Path.Combine(ReleaseDir(build, release), ReleaseManifest.FileName);
        return File.Exists(file) ? ReleaseManifest.Load(file) : null;
    }

    /// <summary>Points <c>current</c> at a release in one step (a new link renamed over the old one).</summary>
    public void SetCurrent(string build, string release)
    {
        if (!Directory.Exists(ReleaseDir(build, release)))
            throw new OsctlException($"Build '{build}' has no release '{release}'");
        var link = CurrentLink(build);
        var temp = link + ".new";
        File.Delete(temp);
        File.CreateSymbolicLink(temp, Path.Combine("releases", release));
        Posix.Rename(temp, link);
    }

    /// <summary>
    /// Removes the oldest releases beyond <paramref name="keep"/>, never the current one. Returns what was removed.
    /// </summary>
    public IReadOnlyList<string> Prune(string build, int keep)
    {
        var current = Current(build);
        var releases = Releases(build);
        var keepSet = releases.TakeLast(keep).ToHashSet();
        if (current is not null)
            keepSet.Add(current);

        var removed = new List<string>();
        foreach (var release in releases.Where(r => !keepSet.Contains(r)))
        {
            FileTree.Delete(ReleaseDir(build, release));
            if (File.Exists(LogFile(build, release)))
                File.Delete(LogFile(build, release));
            removed.Add(release);
        }
        return removed;
    }

    /// <summary>
    /// Holds the build's lock until disposed, so two updates of one build never run at once. .NET takes an
    /// advisory lock (flock) for <see cref="FileShare.None"/> on Linux.
    /// </summary>
    public IDisposable Lock(string build)
    {
        Directory.CreateDirectory(Dir(build));
        try
        {
            return new FileStream(Path.Combine(Dir(build), ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new OsctlException($"Build '{build}' is busy: another osctl is updating it");
        }
    }

    /// <summary>Removes the definition and everything under the build's directory.</summary>
    public void Remove(string build)
    {
        File.Delete(DefinitionFile(build));
        FileTree.Delete(Dir(build));
    }
}
