// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Osctl.Ini;

namespace Osctl.Instances;

public enum DatabaseKind
{
    MariaDb,
    Sqlite,
}

/// <summary>
/// The estate new regions join on first start (PLAN.md "Resolved decisions", first start). On a standalone,
/// OpenSim creates the owner's account with <see cref="OwnerUuid"/> and the password from the instance's
/// secrets; on a grid the owner must already have an account there.
/// </summary>
public sealed record EstateDefinition(string Name, string Owner, Guid? OwnerUuid = null, string? OwnerEmail = null);

/// <summary>
/// An instance, stored as /var/lib/osctl/instances/&lt;name&gt;/instance.ini:
/// <code>
/// [Instance]
///     Build = sas-dev
///     ConfigSet = standalone
///     Database = mariadb          ; or sqlite
/// [Estate]
///     Name = My Estate
///     Owner = Test User
///     OwnerUUID = …               ; standalone: the account OpenSim creates
///     OwnerEmail =
/// </code>
/// </summary>
public sealed partial record InstanceDefinition(string Name, string Build, string ConfigSet, DatabaseKind Database, EstateDefinition Estate)
{
    public static InstanceDefinition Load(string path, string name)
    {
        var doc = IniParser.ParseFile(path);
        var head = doc["Instance"] ?? throw new OsctlException($"{path}: no [Instance] section");
        var estate = doc["Estate"] ?? throw new OsctlException($"{path}: no [Estate] section");

        string Required(IniSection s, string key) =>
            s[key] is { Length: > 0 } v ? v : throw new OsctlException($"{path}: [{s.Name}] {key} is missing");

        var database = ParseDatabase(head["Database"] ?? "mariadb")
            ?? throw new OsctlException($"{path}: [Instance] Database = {head["Database"]}: expected mariadb or sqlite");

        Guid? uuid = null;
        if (estate["OwnerUUID"] is { Length: > 0 } text)
            uuid = Guid.TryParse(text, out var g) ? g : throw new OsctlException($"{path}: [Estate] OwnerUUID = {text} is not a UUID");

        return new InstanceDefinition(
            name,
            Names.Check(Required(head, "Build"), "build"),
            Names.Check(Required(head, "ConfigSet"), "config set"),
            database,
            new EstateDefinition(Required(estate, "Name"), CheckOwner(Required(estate, "Owner")), uuid,
                estate["OwnerEmail"] is { Length: > 0 } email ? email : null));
    }

    public IniDocument ToIni()
    {
        var doc = new IniDocument();
        doc.Set("Instance", "Build", Build);
        doc.Set("Instance", "ConfigSet", ConfigSet);
        doc.Set("Instance", "Database", Database == DatabaseKind.Sqlite ? "sqlite" : "mariadb");
        doc.Set("Estate", "Name", Estate.Name);
        doc.Set("Estate", "Owner", Estate.Owner);
        doc.Set("Estate", "OwnerUUID", Estate.OwnerUuid?.ToString() ?? "");
        doc.Set("Estate", "OwnerEmail", Estate.OwnerEmail ?? "");
        return doc;
    }

    public static DatabaseKind? ParseDatabase(string text) => text.ToLowerInvariant() switch
    {
        "mariadb" or "mysql" => DatabaseKind.MariaDb,
        "sqlite" => DatabaseKind.Sqlite,
        _ => null,
    };

    /// <summary>OpenSim takes an avatar name as exactly two words, first and last.</summary>
    public static string CheckOwner(string owner)
    {
        if (!OwnerPattern().IsMatch(owner))
            throw new OsctlException($"'{owner}' is not an avatar name: expected \"First Last\"");
        return owner;
    }

    [GeneratedRegex(@"^[^\s""]+ [^\s""]+$")]
    private static partial Regex OwnerPattern();
}

/// <summary>
/// A region, one section of the instance's regions/Regions.ini (MIOS kept the same file): RegionUUID, Location,
/// InternalPort, SizeX/SizeY, and any other region settings in <see cref="Settings"/>.
/// </summary>
public sealed partial record RegionDefinition(
    string Name,
    Guid Uuid,
    int X,
    int Y,
    int Port,
    int Size,
    IReadOnlyList<KeyValuePair<string, string>> Settings)
{
    public const int MinSize = 256;
    public const int MaxSize = 4096;

    private static readonly HashSet<string> OwnKeys = ["RegionUUID", "Location", "InternalPort", "SizeX", "SizeY"];

    /// <summary>The grid cells (256 m each) the region covers.</summary>
    public int Cells => Size / 256;

    public bool Overlaps(RegionDefinition other) =>
        X < other.X + other.Cells && other.X < X + Cells && Y < other.Y + other.Cells && other.Y < Y + Cells;

    public static string CheckName(string name)
    {
        if (!NamePattern().IsMatch(name) || name != name.Trim())
            throw new OsctlException(
                $"'{name}' is not a valid region name: letters, digits, spaces and _ - * ( ) ! . ' (at most 64), not starting or ending with a space");
        return name;
    }

    public static int CheckSize(int size)
    {
        if (size < MinSize || size > MaxSize || size % 256 != 0)
            throw new OsctlException($"Region size {size}: must be a multiple of 256 from {MinSize} to {MaxSize}");
        return size;
    }

    public static (int X, int Y) ParseLocation(string text)
    {
        var parts = text.Trim().Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            && x <= 65535 && y <= 65535)
            return (x, y);
        throw new OsctlException($"Location '{text}': expected x,y (grid cells, 0 to 65535)");
    }

    public static List<RegionDefinition> LoadAll(string path)
    {
        if (!File.Exists(path))
            return [];
        var regions = new List<RegionDefinition>();
        foreach (var section in IniParser.ParseFile(path).Sections)
        {
            string Required(string key) =>
                section[key] is { Length: > 0 } v ? v : throw new OsctlException($"{path}: [{section.Name}] {key} is missing");

            var (x, y) = ParseLocation(Required("Location"));
            var uuid = Guid.TryParse(Required("RegionUUID"), out var g) ? g
                : throw new OsctlException($"{path}: [{section.Name}] RegionUUID is not a UUID");
            var port = int.TryParse(Required("InternalPort"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p
                : throw new OsctlException($"{path}: [{section.Name}] InternalPort is not a port number");
            var size = int.TryParse(section["SizeX"] ?? "256", NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s
                : throw new OsctlException($"{path}: [{section.Name}] SizeX is not a number");
            if (section["SizeY"] is { } sy && sy != size.ToString(CultureInfo.InvariantCulture))
                throw new OsctlException($"{path}: [{section.Name}] SizeX and SizeY differ; osctl manages square regions");

            regions.Add(new RegionDefinition(CheckName(section.Name), uuid, x, y, port, CheckSize(size),
                section.Entries.Where(e => !OwnKeys.Contains(e.Key)).Select(e => KeyValuePair.Create(e.Key, e.Value)).ToList()));
        }
        return regions;
    }

    public static IniDocument ToIni(IEnumerable<RegionDefinition> regions)
    {
        var doc = new IniDocument();
        foreach (var r in regions)
        {
            doc.Set(r.Name, "RegionUUID", r.Uuid.ToString());
            doc.Set(r.Name, "Location", $"{r.X},{r.Y}");
            doc.Set(r.Name, "InternalPort", r.Port.ToString(CultureInfo.InvariantCulture));
            doc.Set(r.Name, "SizeX", r.Size.ToString(CultureInfo.InvariantCulture));
            doc.Set(r.Name, "SizeY", r.Size.ToString(CultureInfo.InvariantCulture));
            foreach (var (key, value) in r.Settings)
                doc.Set(r.Name, key, value);
        }
        return doc;
    }

    [GeneratedRegex(@"^[A-Za-z0-9 _\-*()!.']{1,64}$")]
    private static partial Regex NamePattern();
}
