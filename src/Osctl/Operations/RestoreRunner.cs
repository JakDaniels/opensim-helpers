// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using Osctl.ConfigSets;
using Osctl.Databases;
using Osctl.Instances;
using Osctl.Running;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Operations;

/// <summary>
/// What to put back from a backup. <see cref="Definition"/>, <see cref="Databases"/> and <see cref="Assets"/> need
/// the instance stopped; <see cref="Oars"/> (region names) and <see cref="IarUsers"/> need it running, as OpenSim
/// loads them.
/// </summary>
public sealed record RestoreParts(bool Definition, bool Databases, bool Assets, IReadOnlyList<string> Oars,
    IReadOnlyList<string> IarUsers, bool WithEstates)
{
    public bool NeedsStopped => Definition || Databases || Assets;

    public bool NeedsRunning => Oars.Count > 0 || IarUsers.Count > 0;
}

/// <summary>One thing a restore does, as the plan shows it; <see cref="Skipped"/> says why it is left out.</summary>
public sealed record RestoreStep(string Kind, string What, string? File, string? Skipped = null);

public sealed record RestorePlan(string Instance, BackupInfo Backup, RestoreParts Parts, bool Creates, IReadOnlyList<RestoreStep> Steps);

/// <summary>
/// <c>osctl backup restore</c> (PLAN.md "Backups and scheduled restarts"): puts parts of a backup back into the
/// instance it came from. <see cref="Plan"/> checks everything first and says what would happen; <see cref="RunAsync"/>
/// does it. The backup of the instance as it is now, which comes first, is the caller's.
/// </summary>
public sealed class RestoreRunner(
    OsctlPaths paths,
    OsctlSettings settings,
    IRemoteAdmin remoteAdmin,
    IDatabaseFactory databases,
    IPortProbe probe,
    TextWriter progress,
    TimeProvider time)
{
    private const string PasswordCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    private readonly InstanceStore instances = new(paths);

    /// <summary>
    /// The backup to restore from: <paramref name="which"/> is a backup's name in the instance's backups, or a backup
    /// directory; none: the newest complete backup.
    /// </summary>
    public BackupInfo Find(string name, string? which)
    {
        var store = new BackupStore(paths, settings);
        if (which is null)
        {
            return store.List(name).LastOrDefault(b => b.Complete)
                ?? throw new OsctlException($"There is no complete backup of '{name}' in {store.Root(name)}; give a backup's directory");
        }
        if (store.List(name).FirstOrDefault(b => b.Name == which) is { } named)
            return named;
        if (File.Exists(Path.Combine(which, BackupStore.Manifest)))
            return BackupStore.Read(Path.GetFullPath(which));
        throw new OsctlException($"No backup '{which}': give a name from 'osctl backup list {name}' or a backup's directory");
    }

    /// <summary>What a restore with no parts named puts back: everything the backup has but OARs and IARs (the
    /// databases hold those too), and the definition only when the instance is gone.</summary>
    public RestoreParts Whole(string name, BackupInfo backup, bool withEstates) => new(
        Definition: !instances.Exists(name) && DefinitionCopy.IsIn(backup.Dir),
        Databases: backup.Items.Any(i => i.What == "database"),
        Assets: backup.Items.Any(i => i.What == "assets"),
        Oars: [], IarUsers: [], WithEstates: withEstates);

    public RestorePlan Plan(string name, BackupInfo backup, RestoreParts parts)
    {
        if (backup.Instance != name)
            throw new OsctlException($"That is a backup of '{backup.Instance}', not '{name}': a backup goes back into the instance it came from");
        if (parts.NeedsStopped && parts.NeedsRunning)
            throw new OsctlException("The definition, databases and assets are restored with the instance stopped, OARs and IARs with it running: restore them in two goes");
        if (!parts.NeedsStopped && !parts.NeedsRunning)
            throw new OsctlException("Nothing to restore");

        var exists = instances.Exists(name);
        var creates = parts.Definition && !exists;
        if (parts.Definition && !DefinitionCopy.IsIn(backup.Dir))
            throw new OsctlException($"Backup {backup.Name} has no definition of the instance (older backups do not)");
        if (!exists && !parts.Definition)
            throw new OsctlException(DefinitionCopy.IsIn(backup.Dir)
                ? $"There is no instance '{name}': add --definition to make it again from the backup"
                : $"There is no instance '{name}', and the backup has no definition of it: make it with 'osctl instance add' first");

        var running = exists && RunState.Load(instances.RunStateFile(name)) is { IsRunnerAlive: true };
        if (parts.NeedsStopped && running)
            throw new OsctlException($"'{name}' is running: stop it first (osctl stop {name})");
        if (parts.NeedsRunning && !(running && RunState.Load(instances.RunStateFile(name)) is { Ready: not null }))
            throw new OsctlException($"'{name}' is not running: OpenSim loads OARs and IARs, so start it first (osctl start {name})");

        var instance = parts.Definition ? DefinitionCopy.Read(backup.Dir, name) : instances.Load(name);
        var steps = new List<RestoreStep>();
        if (parts.Definition)
        {
            CheckDefinition(name, backup, instance);
            steps.Add(new RestoreStep("definition", creates
                ? $"make the instance again from the backup's definition (build {instance.Build}, config set {instance.ConfigSet})"
                : "replace instance.ini, the regions, overrides and start-up/shutdown commands with the backup's", DefinitionCopy.Dir));
        }
        if (parts.Databases)
            steps.AddRange(DatabaseSteps(name, instance, backup, parts.WithEstates));
        if (parts.Assets)
        {
            if (!instance.IsRobust)
                throw new OsctlException($"'{name}' is not a grid: only a grid's backups hold asset files (a simulator's are in its database)");
            Need(backup, AssetSnapshot.Dir, "assets");
            steps.Add(new RestoreStep("assets", $"replace the asset files in {new ConfigGenerator(paths, settings).AssetsDir(instance)}", AssetSnapshot.Dir));
        }
        if (parts.NeedsRunning && instances.RemoteAdminPassword(name) is null)
            throw new OsctlException($"'{name}' has no RemoteAdmin password, which loading OARs and IARs needs");
        var regions = exists ? instances.Regions(name).Select(r => r.Name).ToHashSet() : [];
        foreach (var region in parts.Oars)
        {
            if (!regions.Contains(region))
                throw new OsctlException($"'{name}' has no region '{region}'");
            var file = BackupRunner.FileName(region) + ".oar";
            Need(backup, file, "oar");
            steps.Add(new RestoreStep("oar", $"replace everything in region {region} with its OAR", file));
        }
        foreach (var user in parts.IarUsers)
        {
            InstanceDefinition.CheckOwner(user);
            var file = BackupRunner.FileName(user) + ".iar";
            Need(backup, file, "iar");
            if (instances.IarPassword(name, user) is null)
                throw new OsctlException($"No password for {user}: 'osctl backup iar-password {name} \"{user}\"' stores it");
            steps.Add(new RestoreStep("iar", $"add the inventory of {user} from its IAR (added to what is there, not replacing it)", file));
        }
        return new RestorePlan(name, backup, parts, creates, steps);
    }

    public async Task<IReadOnlyList<(RestoreStep Step, string? Error)>> RunAsync(RestorePlan plan, CancellationToken ct)
    {
        var name = plan.Instance;
        var results = new List<(RestoreStep, string?)>();
        foreach (var step in plan.Steps)
        {
            if (step.Skipped is not null)
                continue;
            await progress.WriteLineAsync($"{name}: {step.What} …");
            string? error;
            try
            {
                error = step.Kind switch
                {
                    "definition" => await DefinitionAsync(plan, ct),
                    "database" => await DatabaseAsync(name, plan.Backup, step, ct),
                    "assets" => await AssetsAsync(name, plan.Backup, ct),
                    "oar" => await OarAsync(name, plan.Backup, step, ct),
                    "iar" => await IarAsync(name, plan.Backup, step, ct),
                    _ => throw new InvalidOperationException(step.Kind),
                };
            }
            catch (Exception e) when (e is OsctlException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error = e.Message;
            }
            results.Add((step, error));
            // The rest depends on the definition, and a half-restored database is no base for the steps after it.
            if (error is not null && step.Kind is "definition" or "database")
                break;
        }
        return results;
    }

    // ------------------------------------------------------------ planning

    private static void Need(BackupInfo backup, string file, string what)
    {
        var item = backup.Items.FirstOrDefault(i => i.File == file && i.What == what);
        if (item is null || !File.Exists(Path.Combine(backup.Dir, file)) && !Directory.Exists(Path.Combine(backup.Dir, file)))
            throw new OsctlException($"Backup {backup.Name} has no {file}");
        if (!item.Ok)
            throw new OsctlException($"Backup {backup.Name} did not save {file}: {item.Error}");
    }

    private void CheckDefinition(string name, BackupInfo backup, InstanceDefinition instance)
    {
        var grids = new Grids(paths, settings, probe);
        var ports = DefinitionCopy.Regions(backup.Dir).Select(r => r.Port).ToList();
        if (instance.Grid is { } g)
            ports.AddRange([g.PublicPort, g.PrivatePort]);
        foreach (var port in ports)
            grids.CheckPort(port, except: name);
        if (!new Builds.BuildStore(paths).Exists(instance.Build))
            throw new OsctlException($"The backup's '{name}' uses build '{instance.Build}', which is not here: 'osctl build add' makes it first");
    }

    private List<RestoreStep> DatabaseSteps(string name, InstanceDefinition instance, BackupInfo backup, bool withEstates)
    {
        var items = backup.Items.Where(i => i.What == "database").ToList();
        if (items.Count == 0)
            throw new OsctlException($"Backup {backup.Name} has no databases");
        foreach (var item in items.Where(i => !i.Ok))
            throw new OsctlException($"Backup {backup.Name} did not save {item.File}: {item.Error}");

        var steps = new List<RestoreStep>();
        if (instance.Database == DatabaseKind.Sqlite)
        {
            if (items.Any(i => i.File.EndsWith(".sql.gz", StringComparison.Ordinal)))
                throw new OsctlException($"Backup {backup.Name} holds MariaDB dumps, but '{name}' uses SQLite");
            foreach (var item in items)
                steps.Add(new RestoreStep("database", $"replace data/{item.File[..^3]}", item.File));
            return steps;
        }

        var generator = new ConfigGenerator(paths, settings);
        var main = generator.DatabaseName(name);
        var estate = EstateDatabase(name, instance, backup.Dir);
        var sharers = instance.IsRobust ? [] : EstateSharers(name, estate);
        foreach (var item in items)
        {
            if (!item.File.EndsWith(".sql.gz", StringComparison.Ordinal))
                throw new OsctlException($"Backup {backup.Name} holds SQLite files, but '{name}' uses MariaDB");
            var database = item.File[..^".sql.gz".Length];
            if (database == main)
                steps.Add(new RestoreStep("database", $"replace database {database}", item.File));
            else if (database != estate)
                steps.Add(new RestoreStep("database", $"database {database}", item.File, $"'{name}' does not use it now"));
            else if (sharers.Count > 0 && !withEstates)
                steps.Add(new RestoreStep("database", $"estate database {database}", item.File,
                    $"shared with {string.Join(", ", sharers)}; --with-estates restores it too"));
            else
                steps.Add(new RestoreStep("database", $"replace estate database {database}", item.File));
        }
        return steps;
    }

    /// <summary>The estate database the instance uses; its config set from <paramref name="backupDir"/>'s definition when there is none here.</summary>
    private string EstateDatabase(string name, InstanceDefinition instance, string? backupDir = null)
    {
        var sets = new ConfigSetStore(paths);
        var copy = backupDir is null ? null : Path.Combine(backupDir, DefinitionCopy.Dir, "configsets", instance.ConfigSet + ".ini");
        var set = sets.Exists(instance.ConfigSet) ? sets.Load(instance.ConfigSet)
            : copy is not null && File.Exists(copy) ? ConfigSet.FromFile(copy)
            : throw new OsctlException($"There is no config set '{instance.ConfigSet}', which '{name}' uses");
        return new ConfigGenerator(paths, settings).EstateDatabaseName(set.GridFor(name));
    }

    /// <summary>The other instances whose estates are in this database: a grid's simulators share the grid's.</summary>
    private List<string> EstateSharers(string name, string estate) =>
        [.. instances.Names().Where(n => n != name && new ConfigSetStore(paths).Exists(instances.Load(n).ConfigSet)
            && EstateDatabase(n, instances.Load(n)) == estate)];

    // ------------------------------------------------------------ doing

    private async Task<string?> DefinitionAsync(RestorePlan plan, CancellationToken ct)
    {
        var name = plan.Instance;
        var sets = DefinitionCopy.Restore(paths, plan.Backup.Dir, name);
        foreach (var set in sets)
            await progress.WriteLineAsync($"{name}: config set '{set}' written from the backup");
        if (!File.Exists(instances.SecretsFile(name)))
        {
            instances.WriteSecrets(name, null, RandomNumberGenerator.GetString(PasswordCharacters, 32));
            await progress.WriteLineAsync($"{name}: new secrets.ini (a new RemoteAdmin password; IAR passwords are not in backups: 'osctl backup iar-password')");
        }
        Directory.CreateDirectory(instances.LogDir(name));
        var instance = instances.Load(name);
        if (instance.Database == DatabaseKind.MariaDb)
        {
            var admin = databases.Admin(settings.Database);
            foreach (var database in new[] { new ConfigGenerator(paths, settings).DatabaseName(name), EstateDatabase(name, instance) })
            {
                if (!await admin.ExistsAsync(database, ct))
                    await admin.CreateAsync(database, ct);
            }
        }
        return null;
    }

    private async Task<string?> DatabaseAsync(string name, BackupInfo backup, RestoreStep step, CancellationToken ct)
    {
        var file = Path.Combine(backup.Dir, step.File!);
        if (instances.Load(name).Database == DatabaseKind.MariaDb)
        {
            await databases.Admin(settings.Database).LoadAsync(step.File![..^".sql.gz".Length], file, ct);
            return null;
        }

        // SQLite: the file replaces the instance's, with any journal of the old one removed (a stale WAL would be
        // played onto the restored file at the next start).
        var target = Path.Combine(instances.DataDir(name), step.File![..^3]);
        foreach (var old in new[] { target, target + "-wal", target + "-shm", target + "-journal" })
            File.Delete(old);
        var partial = target + ".restoring";
        await using (var input = new GZipStream(File.OpenRead(file), CompressionMode.Decompress))
        await using (var output = File.Create(partial))
            await input.CopyToAsync(output, ct);
        File.Move(partial, target);
        return null;
    }

    private async Task<string?> AssetsAsync(string name, BackupInfo backup, CancellationToken ct)
    {
        var source = Path.Combine(backup.Dir, AssetSnapshot.Dir);
        var target = new ConfigGenerator(paths, settings).AssetsDir(instances.Load(name));
        var data = Path.Combine(target, "data");
        var incoming = data + ".restoring";
        FileTree.Delete(incoming);
        var count = await Task.Run(() => CopyTree(Path.Combine(source, "data"), incoming, ct), ct);
        FileTree.Delete(data);
        Directory.Move(incoming, data);
        // Assets stored but not yet written out when the backup was made: FSAssets writes them at its next start.
        var spool = Path.Combine(target, "tmp");
        Directory.CreateDirectory(Path.Combine(spool, "spool"));
        var spooled = await Task.Run(() => CopyTree(Path.Combine(source, "spool"), spool, ct), ct);
        await progress.WriteLineAsync($"{name}: {count + spooled} asset files restored");
        return null;
    }

    private static int CopyTree(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from))
            return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }

    private async Task<string?> OarAsync(string name, BackupInfo backup, RestoreStep step, CancellationToken ct)
    {
        var region = instances.Regions(name).First(r => BackupRunner.FileName(r.Name) + ".oar" == step.File).Name;
        var log = LogFile(name);
        var offset = OpenSimLog.End(log);
        var timeout = TimeSpan.FromMinutes(settings.Backups.SaveTimeoutMinutes);
        var port = ConfigGenerator.BasePort(instances.Regions(name));
        if (await remoteAdmin.LoadOarAsync(port, instances.RemoteAdminPassword(name)!, region, Path.Combine(backup.Dir, step.File!), timeout, ct) is { } refused)
            return refused;
        return await OpenSimLog.WaitForAsync(log, offset, line =>
            line.Contains("[ARCHIVER]: Successfully loaded archive", StringComparison.Ordinal) ? ""
            : line.Contains("[ARCHIVER]: Aborting load with error", StringComparison.Ordinal) ? line[(line.IndexOf("error", StringComparison.Ordinal) + 6)..]
            : null, timeout, time, ct);
    }

    private async Task<string?> IarAsync(string name, BackupInfo backup, RestoreStep step, CancellationToken ct)
    {
        var user = step.What["add the inventory of ".Length..step.What.IndexOf(" from its IAR", StringComparison.Ordinal)];
        var password = instances.IarPassword(name, user)!;
        if (password.Contains('"') || user.Contains('"'))
            return "the console cannot take a password with \" in it";
        var log = LogFile(name);
        var offset = OpenSimLog.End(log);
        var port = ConfigGenerator.BasePort(instances.Regions(name));
        // Through RemoteAdmin, so the password is not echoed, logged or kept in the console history.
        var command = $"load iar {user} / \"{password}\" \"{Path.Combine(backup.Dir, step.File!)}\"";
        if (await remoteAdmin.ConsoleCommandAsync(port, instances.RemoteAdminPassword(name)!, command, ct) is { } refused)
            return refused;
        return await OpenSimLog.WaitForAsync(log, offset, line =>
            line.Contains("[INVENTORY ARCHIVER]: Loaded ", StringComparison.Ordinal) && line.TrimEnd().EndsWith($" for {user}", StringComparison.Ordinal) ? ""
            : line.Contains($"Failed to find user info for {user}", StringComparison.Ordinal) ? $"OpenSim has no user {user}"
            : line.Contains($"Password for user {user} incorrect", StringComparison.Ordinal) ? $"OpenSim says the password for {user} is wrong"
            : line.Contains($"Archive load for {user} failed - ", StringComparison.Ordinal) ? line[(line.IndexOf(" failed - ", StringComparison.Ordinal) + 10)..]
            : null, TimeSpan.FromMinutes(settings.Backups.SaveTimeoutMinutes), time, ct);
    }

    private string LogFile(string name) => Path.Combine(instances.LogDir(name), "OpenSim.log");
}
