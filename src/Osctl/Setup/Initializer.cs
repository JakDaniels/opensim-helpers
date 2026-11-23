// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Osctl.Settings;

namespace Osctl.Setup;

/// <summary>The default files shipped inside osctl (src/Osctl/Defaults).</summary>
public static class DefaultFiles
{
    public const string Settings = "osctl.ini";
    public const string Secrets = "secrets.ini";
    public const string RegionDefaults = "region-defaults.ini";
    public const string OverridesReadme = "overrides-README.txt";

    /// <summary>The shipped config sets: (name, resource), from Defaults/configset-&lt;name&gt;.ini.</summary>
    public static IEnumerable<(string Name, string Resource)> ConfigSets() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(r => r.StartsWith("Osctl.Defaults.configset-", StringComparison.Ordinal) && r.EndsWith(".ini", StringComparison.Ordinal))
            .Select(r => r["Osctl.Defaults.".Length..])
            .Select(r => (r["configset-".Length..^".ini".Length], r))
            .Order();

    public static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Osctl.Defaults.{name}")
            ?? throw new InvalidOperationException($"Default file {name} is missing from osctl");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public enum InitAction
{
    Created,
    Kept,
}

/// <summary>One thing <c>osctl init</c> did (or, with dry run, would do).</summary>
public sealed record InitStep(string Path, InitAction Action, bool IsDirectory);

/// <summary>
/// <c>osctl init</c>: creates osctl's directories (PLAN.md "Layout on disk") and writes the default files that
/// are not there yet. It never overwrites a file, so it is safe to run again (the package's postinst will).
/// Ownership (opensim:opensim for the data, root:opensim for the settings) is set by the package, not here.
/// </summary>
public sealed class Initializer(OsctlPaths paths)
{
    /// <summary>secrets.ini: read and write for its owner, read for the group (root:opensim on a real system).</summary>
    public const UnixFileMode SecretsMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    public IReadOnlyList<InitStep> Run(bool dryRun)
    {
        var steps = new List<InitStep>();

        foreach (var dir in Directories())
        {
            var exists = Directory.Exists(dir);
            if (!exists && !dryRun)
                Directory.CreateDirectory(dir);
            steps.Add(new InitStep(dir, exists ? InitAction.Kept : InitAction.Created, IsDirectory: true));
        }

        WriteFile(steps, paths.SettingsFile, DefaultFiles.Settings, null, dryRun);
        WriteFile(steps, paths.SecretsFile, DefaultFiles.Secrets, SecretsMode, dryRun);
        WriteFile(steps, paths.RegionDefaultsFile, DefaultFiles.RegionDefaults, null, dryRun);
        WriteFile(steps, Path.Combine(paths.GlobalOverridesDir, "README.txt"), DefaultFiles.OverridesReadme, null, dryRun);
        foreach (var (name, resource) in DefaultFiles.ConfigSets())
            WriteFile(steps, Path.Combine(paths.ConfigSetsDir, name + ".ini"), resource, null, dryRun);
        return steps;
    }

    private IEnumerable<string> Directories() =>
    [
        paths.Etc,
        paths.ConfigSetsDir,
        paths.GlobalOverridesDir,
        paths.BuildDefinitionsDir,
        paths.Lib,
        paths.BuildsDir,
        paths.InstancesDir,
        Path.Combine(paths.Lib, "configsets"),
        paths.Cache,
        Path.Combine(paths.Cache, "assets"),
        paths.Log,
        paths.Run,
    ];

    private static void WriteFile(List<InitStep> steps, string path, string defaultName, UnixFileMode? mode, bool dryRun)
    {
        if (File.Exists(path))
        {
            steps.Add(new InitStep(path, InitAction.Kept, IsDirectory: false));
            return;
        }

        if (!dryRun)
        {
            // Create with the final mode straight away, so a secret is never readable by others, even briefly.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (mode is { } m)
                options.UnixCreateMode = m;
            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream);
            writer.Write(DefaultFiles.Read(defaultName));
        }
        steps.Add(new InitStep(path, InitAction.Created, IsDirectory: false));
    }
}
