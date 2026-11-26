// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Osctl.Databases;
using Osctl.Instances;
using Osctl.Processes;
using Osctl.Running;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Operations;

/// <summary>What one backup holds: OARs of every region, the databases, and the inventories (IARs) of some users.</summary>
public sealed record BackupContents(bool Oars, bool Databases, IReadOnlyList<string> IarUsers);

public sealed record BackupResult(string Dir, bool Complete, IReadOnlyList<BackupItem> Items, AssetCounts? Assets = null);

/// <summary>
/// <c>osctl backup create</c> (PLAN.md "Backups and scheduled restarts"). OARs and IARs are written by OpenSim
/// itself, so the instance must be running; RemoteAdmin starts each save and OpenSim.log says when it is done.
/// Databases: mariadb-dump, or sqlite3's online .backup of each SQLite file in the instance's data/; gzipped.
/// A grid's Robust: its databases, then its FSAssets files (<see cref="AssetSnapshot"/>), running or not.
/// </summary>
public sealed partial class BackupRunner(
    OsctlPaths paths,
    OsctlSettings settings,
    IProcessRunner runner,
    IRemoteAdmin remoteAdmin,
    IDatabaseFactory databases,
    TextWriter progress,
    TimeProvider time)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);
    private readonly InstanceStore instances = new(paths);
    private readonly BackupStore store = new(paths, settings);

    public async Task<BackupResult> RunAsync(string name, BackupContents contents, CancellationToken ct)
    {
        var instance = instances.Load(name);
        var started = time.GetUtcNow();
        var dir = Path.Combine(store.Root(name), time.GetLocalNow().ToString(BackupStore.NameFormat, System.Globalization.CultureInfo.InvariantCulture));
        var partial = dir + BackupStore.PartialSuffix;
        if (Directory.Exists(dir))
            throw new OsctlException($"{dir} already exists");
        FileTree.Delete(partial);
        Directory.CreateDirectory(partial);

        var items = new List<BackupItem>();
        var running = RunState.Load(instances.RunStateFile(name)) is { Ready: not null, IsRunnerAlive: true };
        var port = ConfigGenerator.BasePort(instances.Regions(name));
        var adminPassword = instances.RemoteAdminPassword(name);

        if (instance.IsRobust && contents.IarUsers.Count > 0)
            items.Add(new BackupItem("-", "iar", "a grid's inventories are saved by one of its simulators: 'osctl backup create <simulator> --iar …'"));
        else if ((contents.Oars && instances.Regions(name).Count > 0) || contents.IarUsers.Count > 0)
        {
            if (!running)
                await progress.WriteLineAsync($"{name}: not running, so no OARs or IARs (OpenSim writes them); the databases hold everything");
            else if (adminPassword is null)
                items.Add(new BackupItem("-", "oar", "the instance has no RemoteAdmin password"));
            else
            {
                if (contents.Oars)
                {
                    foreach (var region in instances.Regions(name))
                        items.Add(await OarAsync(name, port, adminPassword, region.Name, partial, ct));
                }
                foreach (var user in contents.IarUsers)
                    items.Add(await IarAsync(name, port, adminPassword, user, partial, ct));
            }
        }

        if (contents.Databases)
            items.AddRange(instance.Database == DatabaseKind.MariaDb ? await MariaDbAsync(instance, partial, ct) : await SqliteAsync(name, partial, ct));

        // After the database, which is FSAssets' index (AssetSnapshot says why).
        AssetCounts? assets = null;
        if (contents.Databases && instance.IsRobust)
        {
            (assets, var item) = await AssetsAsync(instance, partial, started, ct);
            items.Add(item);
        }

        BackupStore.WriteManifest(partial, name, started, time.GetUtcNow(), items, assets);
        Directory.Move(partial, dir);
        return new BackupResult(dir, items.All(i => i.Ok), items, assets);
    }

    /// <summary>A file name made from a region or user name.</summary>
    public static string FileName(string name) => UnsafeCharacters().Replace(name, "_");

    private async Task<BackupItem> OarAsync(string name, int port, string password, string region, string dir, CancellationToken ct)
    {
        var file = FileName(region) + ".oar";
        await progress.WriteLineAsync($"{name}: saving the OAR of {region} …");
        var log = OpenSimLog(name);
        var (_, offset) = InstanceRunner.ReadFrom(log, 0);
        if (await remoteAdmin.SaveOarAsync(port, password, region, Path.Combine(dir, file), ct) is { } refused)
            return new BackupItem(file, "oar", refused);

        var finished = $"[ARCHIVER]: Finished writing out OAR for {region}";
        var error = await WaitForAsync(log, offset, line =>
            line.TrimEnd().EndsWith(finished, StringComparison.Ordinal) ? ""
            : OarFailed().Match(line) is { Success: true } m ? m.Groups[1].Value
            : null, ct);
        error ??= CheckArchive(Path.Combine(dir, file));
        return new BackupItem(file, "oar", error);
    }

    private async Task<BackupItem> IarAsync(string name, int port, string password, string user, string dir, CancellationToken ct)
    {
        var file = FileName(user) + ".iar";
        await progress.WriteLineAsync($"{name}: saving the inventory of {user} …");
        var userPassword = instances.IarPassword(name, user);
        if (userPassword is null)
            return new BackupItem(file, "iar", $"no password for {user}: 'osctl backup iar-password {name} \"{user}\"' stores it");
        if (userPassword.Contains('"') || user.Contains('"'))
            return new BackupItem(file, "iar", "the console cannot take a password with \" in it");

        var log = OpenSimLog(name);
        var (_, offset) = InstanceRunner.ReadFrom(log, 0);
        var command = $"save iar {user} / \"{userPassword}\" \"{Path.Combine(dir, file)}\"";
        if (await remoteAdmin.ConsoleCommandAsync(port, password, command, ct) is { } refused)
            return new BackupItem(file, "iar", refused);

        var error = await WaitForAsync(log, offset, line =>
            line.Contains($"[INVENTORY ARCHIVER]: Saved archive with ", StringComparison.Ordinal) && line.Contains($" for {user}", StringComparison.Ordinal) ? ""
            : line.Contains($"Failed to find user info for {user}", StringComparison.Ordinal) ? $"OpenSim has no user {user}"
            : line.Contains($"Password for user {user} incorrect", StringComparison.Ordinal) ? $"OpenSim says the password for {user} is wrong"
            : line.Contains("[INVENTORY ARCHIVER]: Could not authenticate password", StringComparison.Ordinal) ? "OpenSim could not check the password"
            : line.Contains($"Archive save for {user} failed - ", StringComparison.Ordinal) ? line[(line.IndexOf(" failed - ", StringComparison.Ordinal) + 10)..]
            : null, ct);
        error ??= CheckArchive(Path.Combine(dir, file));
        return new BackupItem(file, "iar", error);
    }

    private async Task<List<BackupItem>> MariaDbAsync(InstanceDefinition instance, string dir, CancellationToken ct)
    {
        var generator = new ConfigGenerator(paths, settings);
        var set = new ConfigSets.ConfigSetStore(paths).Load(instance.ConfigSet);
        var names = new[] { generator.DatabaseName(instance.Name), generator.EstateDatabaseName(set.GridFor(instance.Name)) };
        var items = new List<BackupItem>();
        var admin = databases.Admin(settings.Database);
        foreach (var database in names)
        {
            var file = database + ".sql.gz";
            await progress.WriteLineAsync($"{instance.Name}: dumping database {database} …");
            try
            {
                if (!await admin.ExistsAsync(database, ct))
                {
                    await progress.WriteLineAsync($"{instance.Name}: database {database} does not exist yet; skipped");
                    continue;
                }
                var sql = Path.Combine(dir, database + ".sql");
                await admin.DumpAsync(database, sql, ct);
                await GzipAsync(sql, Path.Combine(dir, file), ct);
                items.Add(new BackupItem(file, "database", null));
            }
            catch (OsctlException e)
            {
                items.Add(new BackupItem(file, "database", e.Message));
            }
        }
        return items;
    }

    private async Task<(AssetCounts?, BackupItem)> AssetsAsync(InstanceDefinition robust, string dir, DateTimeOffset started, CancellationToken ct)
    {
        var source = new ConfigGenerator(paths, settings).AssetsDir(robust);
        var previous = store.List(robust.Name).Select(b => Path.Combine(b.Dir, AssetSnapshot.Dir))
            .LastOrDefault(Directory.Exists);
        await progress.WriteLineAsync($"{robust.Name}: copying the asset files in {source}{(previous is null ? "" : ", linking those the last backup has")} …");
        try
        {
            // Files written in the minute before the start may still be being written by FSAssets.
            var counts = await Task.Run(() => AssetSnapshot.Take(source, Path.Combine(dir, AssetSnapshot.Dir), previous,
                started.UtcDateTime - TimeSpan.FromMinutes(1), ct), ct);
            await progress.WriteLineAsync($"{robust.Name}: {counts.Files} asset files, {counts.Copied} new ({Bytes(counts.CopiedBytes)})"
                + (counts.Skipped > 0 ? $", {counts.Skipped} still being written left for the next backup" : ""));
            if (counts.Damaged.Count > 0)
                await progress.WriteLineAsync($"{robust.Name}: warning: {counts.Damaged.Count} asset file(s) in the grid do not match their hash (copied as they are), e.g. {counts.Damaged[0]}");
            return (counts, new BackupItem(AssetSnapshot.Dir, "assets", null));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, new BackupItem(AssetSnapshot.Dir, "assets", e.Message));
        }
    }

    private static string Bytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
    };

    private async Task<List<BackupItem>> SqliteAsync(string name, string dir, CancellationToken ct)
    {
        var items = new List<BackupItem>();
        var data = instances.DataDir(name);
        var files = Directory.Exists(data) ? Directory.GetFiles(data).Where(IsSqlite).Order(StringComparer.Ordinal).ToList() : [];
        foreach (var source in files)
        {
            var file = Path.GetFileName(source) + ".gz";
            await progress.WriteLineAsync($"{name}: copying {Path.GetFileName(source)} …");
            var copy = Path.Combine(dir, Path.GetFileName(source));
            // sqlite3's .backup reads a consistent copy while OpenSim keeps writing.
            var r = await runner.RunAsync("sqlite3", [source, $".backup '{copy.Replace("'", "''")}'"], ProcessRunOptions.Change, ct);
            if (r.NotFound)
                items.Add(new BackupItem(file, "database", "sqlite3 is not installed (sudo apt install sqlite3)"));
            else if (!r.Succeeded || !File.Exists(copy))
                items.Add(new BackupItem(file, "database", $"sqlite3 .backup failed: {r.StandardError.Trim()}"));
            else
            {
                await GzipAsync(copy, Path.Combine(dir, file), ct);
                items.Add(new BackupItem(file, "database", null));
            }
        }
        return items;
    }

    /// <summary>Waits for a line that says done ("") or failed (the reason) in OpenSim.log; a reason if it times out.</summary>
    private async Task<string?> WaitForAsync(string log, long offset, Func<string, string?> verdict, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(settings.Backups.SaveTimeoutMinutes);
        var deadline = time.GetUtcNow() + timeout;
        var pending = "";
        while (true)
        {
            (var more, offset) = InstanceRunner.ReadFrom(log, offset);
            pending += more;
            var lines = pending.Split('\n');
            pending = lines[^1];
            foreach (var line in lines[..^1])
            {
                if (verdict(line) is { } said)
                    return said.Length == 0 ? null : said.Trim();
            }
            if (time.GetUtcNow() >= deadline)
                return $"not finished after {Durations.Words(timeout)} (SaveTimeoutMinutes in osctl.ini [Backups])";
            await Task.Delay(Poll, ct);
        }
    }

    /// <summary>Reads an OAR or IAR to its end (a gzipped tar), which checks it is whole. Null if it is.</summary>
    public static string? CheckArchive(string file)
    {
        if (!File.Exists(file))
            return "OpenSim wrote no file";
        try
        {
            using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var tar = new TarReader(stream);
            var entries = 0;
            while (tar.GetNextEntry() is { } entry)
            {
                entries++;
                entry.DataStream?.CopyTo(Stream.Null);
            }
            return entries > 0 ? null : "the archive is empty";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException)
        {
            return $"the archive is damaged: {e.Message}";
        }
    }

    private static bool IsSqlite(string file)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(file);
        return stream.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task GzipAsync(string source, string target, CancellationToken ct)
    {
        await using (var input = File.OpenRead(source))
        await using (var output = new GZipStream(File.Create(target), CompressionLevel.Optimal))
            await input.CopyToAsync(output, ct);
        File.Delete(source);
    }

    private string OpenSimLog(string name) => Path.Combine(instances.LogDir(name), "OpenSim.log");

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex(@"\[RADMIN\]: Saving of OAR file with request ID \S+ failed with message (.*)$")]
    private static partial Regex OarFailed();
}
