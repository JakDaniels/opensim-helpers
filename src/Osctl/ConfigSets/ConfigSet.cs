// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;

namespace Osctl.ConfigSets;

public enum ConfigSetMode
{
    /// <summary>The instance runs its own services, and OpenSim creates the estate owner's account.</summary>
    Standalone,

    /// <summary>The services are a grid's; the estate owner must already exist there.</summary>
    Grid,

    /// <summary>A Robust instance: the services of a grid of its own, named after the instance (PLAN.md phase 6).</summary>
    Robust,
}

public enum FileSourceKind
{
    /// <summary>Another file in the build's bin/ (<c>bin:&lt;path&gt;</c>).</summary>
    Bin,

    /// <summary>An http(s) URL, downloaded by <c>osctl configset fetch</c>.</summary>
    Url,

    /// <summary>A file path, absolute or relative to /etc/osctl/configsets/.</summary>
    Local,
}

/// <summary>Where one of a config set's files comes from instead of the build's bin/.</summary>
public sealed record ConfigSetFile(string Path, FileSourceKind Kind, string Source);

/// <summary>An edit made to one file before its includes are followed (<c>[Set &lt;file&gt;]</c>).</summary>
public sealed record ConfigSetEdit(string File, string Section, string Key, string Value);

/// <summary>
/// A config set (PLAN.md "Config sets and grids"): the base files an instance's configuration starts from, stored
/// as /etc/osctl/configsets/&lt;name&gt;.ini:
/// <code>
/// [ConfigSet]
///     Description = …
///     Mode = standalone | grid | robust
///     Grid = osgrid                        ; grid mode: which grid (shared estate DB, asset cache, clash checks)
///     Robust = mygrid                      ; grid mode: the grid is osctl's own Robust instance of that name
///     Load = OpenSimDefaults.ini, OpenSim.ini
/// [Files]
///     OpenSim.ini = https://download.osgrid.org/OpenSim.ini.txt
///     config-include/StandaloneCommon.ini = bin:config-include/StandaloneCommon.ini.example
/// [Set OpenSim.ini]
///     Architecture|Include-Architecture = config-include/StandaloneHypergrid.ini
/// </code>
/// Every path is the one OpenSim would read from bin/; a file not under [Files] comes from bin/ itself.
/// </summary>
public sealed record ConfigSet(
    string Name,
    string Description,
    ConfigSetMode Mode,
    string? Grid,
    IReadOnlyList<string> Load,
    IReadOnlyList<ConfigSetFile> Files,
    IReadOnlyList<ConfigSetEdit> Edits,
    string? Robust = null)
{
    /// <summary>
    /// The grid an instance using this set belongs to: a standalone is a grid of its own, and a Robust instance is
    /// the grid named after it.
    /// </summary>
    public string GridFor(string instance) => Mode switch
    {
        ConfigSetMode.Standalone => $"standalone-{instance}",
        ConfigSetMode.Robust => instance,
        _ => Grid!,
    };

    public static ConfigSet FromFile(string path)
    {
        var name = Names.Check(System.IO.Path.GetFileNameWithoutExtension(path), "config set");
        var doc = IniParser.ParseFile(path);
        var head = doc["ConfigSet"] ?? throw new OsctlException($"{path}: no [ConfigSet] section");

        var mode = (head["Mode"] ?? "").ToLowerInvariant() switch
        {
            "standalone" => ConfigSetMode.Standalone,
            "grid" => ConfigSetMode.Grid,
            "robust" => ConfigSetMode.Robust,
            var other => throw new OsctlException($"{path}: [ConfigSet] Mode = {other}: expected standalone, grid or robust"),
        };
        var grid = head["Grid"] is { Length: > 0 } g ? Names.Check(g, "grid") : null;
        if (mode == ConfigSetMode.Grid && grid is null)
            throw new OsctlException($"{path}: [ConfigSet] Grid is needed for a grid config set");
        var robust = head["Robust"] is { Length: > 0 } r ? Names.CheckInstance(r) : null;
        if (robust is not null && mode != ConfigSetMode.Grid)
            throw new OsctlException($"{path}: [ConfigSet] Robust is only for a grid config set");

        var load = (head["Load"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (load.Length == 0)
            throw new OsctlException($"{path}: [ConfigSet] Load lists no files");

        var files = new List<ConfigSetFile>();
        foreach (var entry in doc["Files"]?.Entries ?? [])
        {
            CheckRelative(path, entry.Key);
            var value = entry.Value;
            files.Add(value switch
            {
                _ when value.StartsWith("bin:", StringComparison.Ordinal) =>
                    new ConfigSetFile(entry.Key, FileSourceKind.Bin, CheckRelative(path, value[4..])),
                _ when value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) =>
                    new ConfigSetFile(entry.Key, FileSourceKind.Url, value),
                _ when value.Length > 0 => new ConfigSetFile(entry.Key, FileSourceKind.Local, value),
                _ => throw new OsctlException($"{entry.Origin}: [Files] {entry.Key} has no source"),
            });
        }

        var edits = new List<ConfigSetEdit>();
        foreach (var section in doc.Sections.Where(s => s.Name.StartsWith("Set ", StringComparison.Ordinal)))
        {
            var file = section.Name[4..].Trim();
            foreach (var entry in section.Entries)
            {
                var bar = entry.Key.IndexOf('|');
                if (bar <= 0 || bar == entry.Key.Length - 1)
                    throw new OsctlException($"{entry.Origin}: [{section.Name}] {entry.Key}: expected Section|Key");
                edits.Add(new ConfigSetEdit(file, entry.Key[..bar], entry.Key[(bar + 1)..], entry.Value));
            }
        }

        return new ConfigSet(name, head["Description"] ?? "", mode, grid, load, files, edits, robust);
    }

    private static string CheckRelative(string file, string path)
    {
        if (path.Length == 0 || System.IO.Path.IsPathRooted(path) || path.Split('/').Contains(".."))
            throw new OsctlException($"{file}: '{path}' must be a path inside bin/");
        return path;
    }
}
