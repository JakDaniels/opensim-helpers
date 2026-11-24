// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Osctl.Ini;
using Osctl.Instances;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Operations;

/// <summary>One item of a backup and how it went.</summary>
public sealed record BackupItem(string File, string What, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>A backup directory, as <c>backup list</c> shows it.</summary>
public sealed record BackupInfo(string Instance, string Name, string Dir, DateTime Time, bool Complete, long Bytes, IReadOnlyList<BackupItem> Items);

/// <summary>
/// Where an instance's backups are (PLAN.md "Backups and scheduled restarts"): one directory per backup, named after
/// its local start time (<c>yyyyMMdd-HHmmss</c>), holding the files and a <c>backup.ini</c> manifest. A backup being
/// made is <c>&lt;name&gt;.partial</c> until it is done.
/// </summary>
public sealed class BackupStore(OsctlPaths paths, OsctlSettings settings)
{
    public const string Manifest = "backup.ini";
    public const string PartialSuffix = ".partial";
    public const string NameFormat = "yyyyMMdd-HHmmss";

    private readonly InstanceStore instances = new(paths);

    public string Root(string instance) =>
        settings.Backups.Dir is { } dir ? Path.Combine(dir, instance) : instances.BackupsDir(instance);

    public IReadOnlyList<BackupInfo> List(string instance)
    {
        var root = Root(instance);
        if (!Directory.Exists(root))
            return [];
        var result = new List<BackupInfo>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!DateTime.TryParseExact(name, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                continue;
            var items = new List<BackupItem>();
            var complete = false;
            var manifest = Path.Combine(dir, Manifest);
            if (File.Exists(manifest))
            {
                var doc = IniParser.ParseFile(manifest);
                complete = doc.Get("Backup", "Complete") == "true";
                foreach (var entry in doc["Items"]?.Entries ?? [])
                {
                    var parts = entry.Value.Split('|', 2);
                    items.Add(new BackupItem(entry.Key, parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null));
                }
            }
            var bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            result.Add(new BackupInfo(instance, name, dir, time, complete, bytes, items));
        }
        return [.. result.OrderBy(b => b.Time)];
    }

    public static void WriteManifest(string dir, string instance, DateTimeOffset started, DateTimeOffset finished, IReadOnlyList<BackupItem> items)
    {
        var doc = new IniDocument();
        doc.Set("Backup", "Instance", instance);
        doc.Set("Backup", "Started", started.ToString("O", CultureInfo.InvariantCulture));
        doc.Set("Backup", "Finished", finished.ToString("O", CultureInfo.InvariantCulture));
        doc.Set("Backup", "Complete", items.All(i => i.Ok) ? "true" : "false");
        foreach (var item in items)
            doc.Set("Items", item.File, item.Error is null ? item.What : $"{item.What}|{item.Error.Replace('\n', ' ')}");
        IniWriter.WriteFile(Path.Combine(dir, Manifest), doc, ["Written by 'osctl backup create'. Items: file = what|error (no error: saved)."]);
    }

    /// <summary>
    /// Which backups pruning removes: of the complete ones it keeps the newest, and the newest of each of the last
    /// <paramref name="keepDaily"/> days and <paramref name="keepWeekly"/> ISO weeks that have one. An incomplete
    /// backup goes once a newer complete one exists.
    /// </summary>
    public static IReadOnlyList<BackupInfo> ToPrune(IReadOnlyList<BackupInfo> backups, int keepDaily, int keepWeekly)
    {
        var complete = backups.Where(b => b.Complete).OrderByDescending(b => b.Time).ToList();
        var keep = new HashSet<BackupInfo>();
        if (complete.Count > 0)
            keep.Add(complete[0]);
        void KeepFirstPer<T>(Func<DateTime, T> bucket, int count)
        {
            var seen = new HashSet<T>();
            foreach (var b in complete)
            {
                if (seen.Count >= count && !seen.Contains(bucket(b.Time)))
                    break;
                if (seen.Add(bucket(b.Time)))
                    keep.Add(b);
            }
        }
        KeepFirstPer(t => t.Date, keepDaily);
        KeepFirstPer(t => (ISOWeek.GetYear(t), ISOWeek.GetWeekOfYear(t)), keepWeekly);

        var newestComplete = complete.Count > 0 ? complete[0].Time : DateTime.MaxValue;
        return [.. backups.Where(b => b.Complete ? !keep.Contains(b) : b.Time < newestComplete)];
    }

    public static void Delete(BackupInfo backup) => FileTree.Delete(backup.Dir);
}
