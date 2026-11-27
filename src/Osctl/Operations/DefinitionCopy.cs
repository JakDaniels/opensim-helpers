// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.ConfigSets;
using Osctl.Instances;
using Osctl.Settings;

namespace Osctl.Operations;

/// <summary>
/// osctl's own definition of an instance, kept in each backup as <c>definition/</c> (PLAN.md "Backups and scheduled
/// restarts"), so that a lost instance can be made again by <c>backup restore</c>: what is in
/// /etc/osctl/instances/&lt;name&gt; (instance.ini, regions/, overrides/, region-defaults.ini, the start-up and shutdown
/// commands) and the config sets it uses.
/// Not secrets.ini: backups are often copied off the machine.
/// </summary>
public static class DefinitionCopy
{
    public const string Dir = "definition";
    private const string ConfigSets = "configsets";
    private const string RegionDefaults = "region-defaults.ini";

    /// <summary>Copies the instance's definition into <c>&lt;backup&gt;/definition</c>.</summary>
    public static void Save(OsctlPaths paths, InstanceDefinition instance, string backupDir)
    {
        var store = new InstanceStore(paths);
        var target = Path.Combine(backupDir, Dir);
        var name = instance.Name;
        CopyFile(store.DefinitionFile(name), Path.Combine(target, "instance.ini"));
        CopyFile(store.RegionDefaultsFile(name), Path.Combine(target, RegionDefaults));
        CopyFile(store.StartupCommandsFile(name), Path.Combine(target, Path.GetFileName(store.StartupCommandsFile(name))));
        CopyFile(store.ShutdownCommandsFile(name), Path.Combine(target, Path.GetFileName(store.ShutdownCommandsFile(name))));
        CopyTree(store.RegionsDir(name), Path.Combine(target, "regions"));
        CopyTree(store.OverridesDir(name), Path.Combine(target, "overrides"));
        var sets = new ConfigSetStore(paths);
        foreach (var set in SetsOf(instance))
            CopyFile(sets.DefinitionFile(set), Path.Combine(target, ConfigSets, set + ".ini"));
    }

    public static bool IsIn(string backupDir) => File.Exists(Path.Combine(backupDir, Dir, "instance.ini"));

    /// <summary>The instance as the backup defines it.</summary>
    public static InstanceDefinition Read(string backupDir, string name) =>
        InstanceDefinition.Load(Path.Combine(backupDir, Dir, "instance.ini"), name);

    /// <summary>The regions the backup defines.</summary>
    public static List<RegionDefinition> Regions(string backupDir) =>
        RegionDefinition.LoadAll(Path.Combine(backupDir, Dir, "regions", "Regions.ini"));

    /// <summary>
    /// Puts the definition back: the instance's files are replaced; a config set is written only where there is none
    /// (one changed since is kept). Returns the config sets it wrote.
    /// </summary>
    public static IReadOnlyList<string> Restore(OsctlPaths paths, string backupDir, string name)
    {
        var store = new InstanceStore(paths);
        var source = Path.Combine(backupDir, Dir);
        store.CreateDirectories(name);
        CopyFile(Path.Combine(source, "instance.ini"), store.DefinitionFile(name), overwrite: true);
        // Replaced like the rest: none in the backup means the instance had none.
        File.Delete(store.RegionDefaultsFile(name));
        CopyFile(Path.Combine(source, RegionDefaults), store.RegionDefaultsFile(name));
        foreach (var file in new[] { store.StartupCommandsFile(name), store.ShutdownCommandsFile(name) })
            CopyFile(Path.Combine(source, Path.GetFileName(file)), file, overwrite: true);
        foreach (var (from, to) in new[] { ("regions", store.RegionsDir(name)), ("overrides", store.OverridesDir(name)) })
        {
            Util.FileTree.Delete(to);
            Directory.CreateDirectory(to);
            CopyTree(Path.Combine(source, from), to);
        }

        var written = new List<string>();
        var sets = new ConfigSetStore(paths);
        var setsDir = Path.Combine(source, ConfigSets);
        if (Directory.Exists(setsDir))
        {
            foreach (var file in Directory.GetFiles(setsDir, "*.ini").Order(StringComparer.Ordinal))
            {
                var set = Path.GetFileNameWithoutExtension(file);
                if (File.Exists(sets.DefinitionFile(set)))
                    continue;
                CopyFile(file, sets.DefinitionFile(set));
                written.Add(set);
            }
        }
        return written;
    }

    private static IEnumerable<string> SetsOf(InstanceDefinition instance) =>
        instance.IsRobust ? [instance.ConfigSet, instance.Name] : [instance.ConfigSet];

    private static void CopyFile(string from, string to, bool overwrite = false)
    {
        if (!File.Exists(from))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite);
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from))
            return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
