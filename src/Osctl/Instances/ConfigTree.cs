// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.ConfigSets;
using Osctl.Ini;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Instances;

/// <summary>
/// <c>osctl config tree</c>: the configuration as a tree. Without an instance, the files under /etc/osctl, each with
/// a note on what it is; for an instance, the files its OpenSim.ini (or Robust.ini) and Regions.ini are made from,
/// numbered in the order they are read (later wins), with each include under the file that includes it.
/// </summary>
public sealed class ConfigTree(OsctlPaths paths, OsctlSettings settings)
{
    private readonly InstanceStore instances = new(paths);
    private readonly ConfigSetStore configSets = new(paths);

    // ---------------------------------------------------------------- /etc/osctl

    /// <summary>
    /// The files under /etc/osctl that osctl reads (all of them with <paramref name="all"/>; dot files never), and
    /// the instances whose definition is not yet moved there. Other files are counted on a last line.
    /// </summary>
    public TreeNode Files(bool all = false)
    {
        var root = new TreeNode(paths.Etc);
        var localSources = LocalSources();
        var hidden = 0;
        AddDirectory(root, paths.Etc, "", all, localSources, ref hidden);
        var old = instances.Names().Where(instances.IsOldLayout).ToList();
        if (old.Count > 0)
        {
            // Only the definition: the rest of that directory is the instance's data, as for a moved one.
            var node = root.Add(paths.InstancesDir + "/", "instances whose definition is still here (moved once they are stopped)");
            foreach (var name in old)
            {
                var hiddenThere = 0;
                AddDirectory(node.Add(name + "/", InstanceNote(name)), instances.ConfigDir(name), $"instances/{name}/", false, localSources, ref hiddenThere);
            }
        }
        if (hidden > 0)
            root.Add($"({hidden} other file{(hidden == 1 ? "" : "s")} osctl does not read: 'osctl config tree --all' shows them)");
        return root;
    }

    /// <summary>Files under /etc/osctl that a config set takes in place of one from bin/ ([Files] path = local file).</summary>
    private HashSet<string> LocalSources()
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in configSets.Names())
        {
            try
            {
                foreach (var file in configSets.Load(name).Files.Where(f => f.Kind == FileSourceKind.Local))
                    sources.Add(Path.GetFullPath(Path.IsPathRooted(file.Source) ? file.Source : Path.Combine(paths.ConfigSetsDir, file.Source)));
            }
            catch (Exception e) when (CliExpected(e))
            {
                // Shown as unreadable on its own line.
            }
        }
        return sources;
    }

    /// <summary>Adds a directory's entries; returns whether it added any file (so empty branches can be left out).</summary>
    private bool AddDirectory(TreeNode node, string dir, string relative, bool all, HashSet<string> localSources, ref int hidden)
    {
        var any = false;
        var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Where(e => !e.Name.StartsWith('.'))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = relative + entry.Name;
            if (entry is DirectoryInfo sub)
            {
                var child = new TreeNode(entry.Name + "/", Note(path + "/"));
                if (AddDirectory(child, sub.FullName, path + "/", all, localSources, ref hidden) || all || IsOsctlDirectory(path))
                {
                    node.Add(child);
                    any = true;
                }
            }
            else if (all || IsRead(path) || localSources.Contains(Path.GetFullPath(entry.FullName)))
            {
                node.Add(entry.Name, Note(path) ?? (localSources.Contains(Path.GetFullPath(entry.FullName)) ? "read by a config set ([Files])" : null));
                any = true;
            }
            else
                hidden++;
        }
        return any;
    }

    /// <summary>Directories osctl keeps, shown even when empty.</summary>
    private static bool IsOsctlDirectory(string path) => path.Split('/') switch
    {
        ["configsets" or "overrides" or "builds" or "instances"] => true,
        ["instances", _] => true,
        ["instances", _, "overrides" or "regions"] => true,
        _ => false,
    };

    /// <summary>Whether osctl reads this file (relative to /etc/osctl) when it makes an instance's configuration.</summary>
    private static bool IsRead(string path)
    {
        static bool Ini(string name) => name.EndsWith(".ini", StringComparison.Ordinal);
        return path.Split('/') switch
        {
            ["osctl.ini" or "secrets.ini" or "region-defaults.ini"] => true,
            ["configsets" or "overrides" or "builds", var file] => Ini(file),
            ["instances", _, "instance.ini" or "secrets.ini" or "region-defaults.ini" or "startup_commands.txt" or "shutdown_commands.txt"] => true,
            ["instances", _, "overrides" or "regions", var file] => Ini(file),
            _ => false,
        };
    }

    /// <summary>What a file or directory under /etc/osctl is, from its path relative to it.</summary>
    private string? Note(string path)
    {
        var parts = path.TrimEnd('/').Split('/');
        try
        {
            return parts switch
            {
                ["osctl.ini"] => "osctl's settings",
                ["secrets.ini"] => "database passwords (group-readable only)",
                ["region-defaults.ini"] => "[Region Defaults] for every region",
                ["configsets"] => "config sets: the base configurations instances start from",
                ["configsets", var file] when file.EndsWith(".ini", StringComparison.Ordinal) => ConfigSetNote(file[..^4]),
                ["overrides"] => "OpenSim.ini overrides for every instance, alphabetical",
                ["builds"] => "build definitions",
                ["instances"] => "instance definitions",
                ["instances", var name] => InstanceNote(name),
                ["instances", _, "instance.ini"] => "build, config set, database, estate, schedule",
                ["instances", _, "secrets.ini"] => "estate owner, RemoteAdmin and IAR passwords (group-readable only)",
                ["instances", _, "overrides"] => "OpenSim.ini overrides for this instance, after the global ones",
                ["instances", _, "region-defaults.ini"] => "[Region Defaults] for this instance's regions",
                ["instances", _, "regions"] => "the regions",
                ["instances", var name, "regions", "Regions.ini"] => RegionsNote(instances.Regions(name).Select(r => r.Name)),
                ["instances", _, "regions", var file] when file.EndsWith(".ini", StringComparison.Ordinal) =>
                    "settings for " + RegionsNote(IniParser.ParseFile(Path.Combine(paths.Etc, path)).Sections.Select(s => s.Name)),
                ["instances", _, "startup_commands.txt"] => "console commands run at start-up",
                ["instances", _, "shutdown_commands.txt"] => "console commands run at shutdown",
                [.., "overrides", "50-osctl.ini"] => "written by 'osctl config set'",
                _ => null,
            };
        }
        catch (Exception e) when (CliExpected(e))
        {
            return $"(unreadable: {e.Message})";
        }
    }

    private string ConfigSetNote(string name)
    {
        var set = configSets.Load(name);
        var mode = set.Mode switch
        {
            ConfigSetMode.Standalone => "standalone",
            ConfigSetMode.Robust => "Robust grid services",
            _ when set.Robust is { } robust => $"simulators on grid {robust} (this machine)",
            _ => $"simulators on grid {set.Grid}",
        };
        return set.Description.Length > 0 ? $"{mode}: {set.Description}" : mode;
    }

    private string? InstanceNote(string name)
    {
        if (!instances.Exists(name))
            return null;
        var instance = instances.Load(name);
        if (instance.IsRobust)
            return $"grid (Robust), build {instance.Build}";
        var set = configSets.Exists(instance.ConfigSet) ? configSets.Load(instance.ConfigSet) : null;
        var kind = set switch
        {
            { Mode: ConfigSetMode.Standalone } => "standalone",
            { Robust: { } robust } => $"simulator on grid {robust}",
            { Grid: { } grid } => $"simulator on grid {grid}",
            _ => $"config set {instance.ConfigSet}",
        };
        return $"{kind}, build {instance.Build}, {instance.Database.ToString().ToLowerInvariant()}";
    }

    private static string RegionsNote(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "no regions" : string.Join(", ", list);
    }

    private static bool CliExpected(Exception e) => e is OsctlException or IniParseException or IOException or UnauthorizedAccessException;

    // ---------------------------------------------------------------- one instance

    /// <summary>
    /// The files an instance's configuration is made from, in the order they are read (later wins). Needs its
    /// build, as <c>config show</c> does.
    /// </summary>
    public TreeNode Instance(string name)
    {
        var instance = instances.Load(name);
        var generator = new ConfigGenerator(paths, settings);
        var generated = generator.Generate(name);
        var root = new TreeNode(name, InstanceNote(name));
        var order = 0;
        string N(string label) => $"{++order}. {label}";
        // Paths are shown relative to /etc/osctl, or to the build's bin/ for the config set's files.
        string Short(string path, string under) =>
            Path.GetRelativePath(under, path) is var rel && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel) ? rel : path;
        string Etc(string path) => Short(path, paths.Etc);

        root.Add("paths relative to", paths.Etc);
        root.Add("config set files from", generated.Bin);
        var main = root.Add(generated.MainFile, "read in this order, later wins");
        var setNode = main.Add($"config set {instance.ConfigSet}", Etc(configSets.DefinitionFile(instance.ConfigSet)));
        var nodes = new Dictionary<string, TreeNode>();
        var overrideDirs = new[] { paths.GlobalOverridesDir, instances.OverridesDir(name) };
        foreach (var layer in generated.Layers)
        {
            if (layer.Path == ConfigGenerator.OsctlLayer)
            {
                main.Add(N("set by osctl"), "ports, paths, databases, estate, RemoteAdmin: 'osctl config explain' shows them");
                continue;
            }
            if (overrideDirs.Contains(Path.GetDirectoryName(layer.Path)))
            {
                main.Add(N(Etc(layer.Path)), Path.GetDirectoryName(layer.Path) == paths.GlobalOverridesDir ? "every instance" : "this instance");
                continue;
            }
            // The config set's files, each under the file that included it.
            var parent = layer.IncludedBy is { } by && nodes.TryGetValue(by, out var including) ? including : setNode;
            var notes = new List<string>();
            if (layer.ReadFrom != Path.Combine(generated.Bin, layer.Path))
                notes.Add($"from {Etc(Short(layer.ReadFrom, generated.Bin))}");
            if (parent == setNode && layer.IncludedBy is { } editedBy)
                notes.Add($"included by {Etc(editedBy)}");   // a config set's [Set …] edit
            var node = parent.Add(N(layer.Path), notes.Count == 0 ? null : string.Join("; ", notes));
            nodes.TryAdd(layer.ReadFrom, node);
        }

        if (!instance.IsRobust)
        {
            order = 0;
            var regions = root.Add("Regions.ini", "for each region, in this order, later wins");
            if (File.Exists(paths.RegionDefaultsFile))
                regions.Add(N(Etc(paths.RegionDefaultsFile)), "[Region Defaults] for every region");
            if (File.Exists(instances.RegionDefaultsFile(name)))
                regions.Add(N(Etc(instances.RegionDefaultsFile(name))), "[Region Defaults] for this instance's regions");
            var dir = instances.RegionsDir(name);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.ini").Where(f => Path.GetFileName(f) != "Regions.ini").Order(StringComparer.Ordinal))
                    regions.Add(N(Etc(file)), "settings for " + RegionsNote(IniParser.ParseFile(file).Sections.Select(s => s.Name)));
            }
            regions.Add(N(Etc(instances.RegionsFile(name))), RegionsNote(instances.Regions(name).Select(r => r.Name)));
        }
        return root;
    }
}
