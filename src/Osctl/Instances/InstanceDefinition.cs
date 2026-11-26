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

/// <summary>What an instance runs: OpenSim with regions, or Robust (a grid's services; PLAN.md phase 6).</summary>
public enum InstanceKind
{
    Sim,
    Robust,
}

/// <summary>
/// A grid's settings, the [Grid] section of its Robust instance (PLAN.md "Robust and local grids"): the public host
/// name viewers and other grids use, the public port (login, grid info, Hypergrid) and the private port (the
/// simulators' services), the grid's name and nickname, the region new avatars arrive in, and where FSAssets keeps
/// the asset files (null: the instance's data/fsassets).
/// </summary>
public sealed record GridDefinition(
    string Host, int PublicPort, int PrivatePort, string Name, string Nick, string? DefaultRegion = null, string? AssetsDir = null)
{
    /// <summary>The public address: http://host:port.</summary>
    public string PublicUri => $"http://{Host}:{PublicPort.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// The estate new regions join on first start (PLAN.md "Resolved decisions", first start). On a standalone,
/// OpenSim creates the owner's account with <see cref="OwnerUuid"/> and the password from the instance's
/// secrets; on a grid the owner must already have an account there.
/// </summary>
public sealed record EstateDefinition(string Name, string Owner, Guid? OwnerUuid = null, string? OwnerEmail = null);

/// <summary>
/// When osctl restarts and backs up the instance (systemd OnCalendar expressions; PLAN.md "Backups and scheduled
/// restarts"), and how many minutes before a restart people are warned (null: [Schedule] WarnMinutes).
/// </summary>
public sealed record InstanceSchedule(string? Restart = null, int? WarnMinutes = null, string? Backup = null)
{
    public static InstanceSchedule None { get; } = new();
}

/// <summary>
/// The instance's backups: whose inventories (IARs) scheduled backups save, and how many backups to keep
/// (null: osctl.ini [Backups]).
/// </summary>
public sealed record InstanceBackups(IReadOnlyList<string> IarUsers, int? KeepDaily = null, int? KeepWeekly = null)
{
    public static InstanceBackups None { get; } = new([]);
}

/// <summary>
/// An instance, stored as /var/lib/osctl/instances/&lt;name&gt;/instance.ini:
/// <code>
/// [Instance]
///     Kind = sim                  ; or robust: a grid's services, with [Grid] instead of [Estate]
///     Build = sas-dev
///     ConfigSet = standalone
///     Database = mariadb          ; or sqlite
/// [Estate]
///     Name = My Estate
///     Owner = Test User
///     OwnerUUID = …               ; standalone: the account OpenSim creates
///     OwnerEmail =
/// [Schedule]                      ; optional: 'osctl schedule …'
///     Restart = Sun 04:00
///     WarnMinutes = 10
///     Backup = *-*-* 03:00
/// [Backups]                       ; optional
///     Iar = Test User, Other Person
///     KeepDaily = 7
///     KeepWeekly = 4
/// [Grid]                          ; robust only
///     Host = grid.example.org
///     PublicPort = 8002
///     PrivatePort = 8003
///     Name = My Grid
///     Nick = mygrid
///     DefaultRegion = Welcome
///     AssetsDir = /srv/assets      ; optional: FSAssets' files (default: the instance's data/fsassets)
/// </code>
/// </summary>
public sealed partial record InstanceDefinition(
    string Name,
    string Build,
    string ConfigSet,
    DatabaseKind Database,
    EstateDefinition? Estate,
    InstanceSchedule? Schedule = null,
    InstanceBackups? Backups = null,
    InstanceKind Kind = InstanceKind.Sim,
    GridDefinition? Grid = null)
{
    public bool IsRobust => Kind == InstanceKind.Robust;

    /// <summary>A simulator's estate; it always has one (a Robust instance has none).</summary>
    public EstateDefinition SimEstate =>
        Estate ?? throw new OsctlException($"Instance '{Name}' runs Robust: it has no estate");

    /// <summary>A Robust instance's grid settings.</summary>
    public GridDefinition GridSettings =>
        Grid ?? throw new OsctlException($"Instance '{Name}' is not a grid (it runs OpenSim, not Robust)");

    public InstanceSchedule Scheduled => Schedule ?? InstanceSchedule.None;

    public InstanceBackups BackupOptions => Backups ?? InstanceBackups.None;

    public static InstanceDefinition Load(string path, string name)
    {
        var doc = IniParser.ParseFile(path);
        var head = doc["Instance"] ?? throw new OsctlException($"{path}: no [Instance] section");
        var kind = (head["Kind"] ?? "sim").ToLowerInvariant() switch
        {
            "sim" or "" => InstanceKind.Sim,
            "robust" => InstanceKind.Robust,
            var other => throw new OsctlException($"{path}: [Instance] Kind = {other}: expected sim or robust"),
        };
        var estate = doc["Estate"];
        if (estate is null && kind == InstanceKind.Sim)
            throw new OsctlException($"{path}: no [Estate] section");

        string Required(IniSection s, string key) =>
            s[key] is { Length: > 0 } v ? v : throw new OsctlException($"{path}: [{s.Name}] {key} is missing");

        var database = ParseDatabase(head["Database"] ?? "mariadb")
            ?? throw new OsctlException($"{path}: [Instance] Database = {head["Database"]}: expected mariadb or sqlite");

        Guid? uuid = null;
        if (estate?["OwnerUUID"] is { Length: > 0 } text)
            uuid = Guid.TryParse(text, out var g) ? g : throw new OsctlException($"{path}: [Estate] OwnerUUID = {text} is not a UUID");

        int? Number(IniSection? s, string key, int max)
        {
            if (s?.GetEntry(key) is not { Value.Length: > 0 } entry)
                return null;
            return int.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= max ? n
                : throw new OsctlException($"{path}: [{s.Name}] {key} = {entry.Value}: expected a whole number from 0 to {max}");
        }
        string? Text(IniSection? s, string key) => s?[key] is { Length: > 0 } v ? v : null;

        GridDefinition? grid = null;
        if (kind == InstanceKind.Robust)
        {
            var g = doc["Grid"] ?? throw new OsctlException($"{path}: no [Grid] section");
            int Port(string key) => Number(g, key, 65535) is { } n and > 0 ? n
                : throw new OsctlException($"{path}: [Grid] {key} is missing");
            grid = new GridDefinition(CheckHost(Required(g, "Host")), Port("PublicPort"), Port("PrivatePort"),
                Required(g, "Name"), Required(g, "Nick"), Text(g, "DefaultRegion"), Text(g, "AssetsDir") is { } assets ? CheckDir(path, assets) : null);
        }

        var schedule = doc["Schedule"];
        var backups = doc["Backups"];
        var iar = (Text(backups, "Iar") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(CheckOwner).ToList();

        return new InstanceDefinition(
            name,
            Names.Check(Required(head, "Build"), "build"),
            Names.Check(Required(head, "ConfigSet"), "config set"),
            database,
            estate is null ? null : new EstateDefinition(Required(estate, "Name"), CheckOwner(Required(estate, "Owner")), uuid,
                estate["OwnerEmail"] is { Length: > 0 } email ? email : null),
            new InstanceSchedule(Text(schedule, "Restart"), Number(schedule, "WarnMinutes", 24 * 60), Text(schedule, "Backup")),
            new InstanceBackups(iar, Number(backups, "KeepDaily", 1000), Number(backups, "KeepWeekly", 1000)),
            kind,
            grid);
    }

    public IniDocument ToIni()
    {
        var doc = new IniDocument();
        doc.Set("Instance", "Kind", IsRobust ? "robust" : "sim");
        doc.Set("Instance", "Build", Build);
        doc.Set("Instance", "ConfigSet", ConfigSet);
        doc.Set("Instance", "Database", Database == DatabaseKind.Sqlite ? "sqlite" : "mariadb");
        if (Estate is { } estate)
        {
            doc.Set("Estate", "Name", estate.Name);
            doc.Set("Estate", "Owner", estate.Owner);
            doc.Set("Estate", "OwnerUUID", estate.OwnerUuid?.ToString() ?? "");
            doc.Set("Estate", "OwnerEmail", estate.OwnerEmail ?? "");
        }
        if (Grid is { } grid)
        {
            doc.Set("Grid", "Host", grid.Host);
            doc.Set("Grid", "PublicPort", grid.PublicPort.ToString(CultureInfo.InvariantCulture));
            doc.Set("Grid", "PrivatePort", grid.PrivatePort.ToString(CultureInfo.InvariantCulture));
            doc.Set("Grid", "Name", grid.Name);
            doc.Set("Grid", "Nick", grid.Nick);
            doc.Set("Grid", "DefaultRegion", grid.DefaultRegion ?? "");
            if (grid.AssetsDir is { } assets)
                doc.Set("Grid", "AssetsDir", assets);
        }

        void Optional(string section, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                doc.Set(section, key, value);
        }
        string? N(int? n) => n?.ToString(CultureInfo.InvariantCulture);
        Optional("Schedule", "Restart", Scheduled.Restart);
        Optional("Schedule", "WarnMinutes", N(Scheduled.WarnMinutes));
        Optional("Schedule", "Backup", Scheduled.Backup);
        Optional("Backups", "Iar", string.Join(", ", BackupOptions.IarUsers));
        Optional("Backups", "KeepDaily", N(BackupOptions.KeepDaily));
        Optional("Backups", "KeepWeekly", N(BackupOptions.KeepWeekly));
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

    private static string CheckDir(string file, string dir) =>
        Path.IsPathRooted(dir) ? dir : throw new OsctlException($"{file}: [Grid] AssetsDir = {dir}: must be an absolute path");

    /// <summary>A host name or IP address, as it goes into http://host:port.</summary>
    public static string CheckHost(string host)
    {
        if (!HostPattern().IsMatch(host))
            throw new OsctlException($"'{host}' is not a host name or IPv4 address");
        return host;
    }

    [GeneratedRegex(@"^[^\s""]+ [^\s""]+$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();
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
