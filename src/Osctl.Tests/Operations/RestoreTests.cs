// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Instances;
using Osctl.Operations;
using Osctl.Processes;
using Osctl.Running;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Operations;

/// <summary><c>osctl backup restore</c>: parts of a backup back into the instance it came from.</summary>
public class RestoreTests
{
    private static global::Osctl.Settings.OsctlSettings Settings(InstanceFixture f) => global::Osctl.Settings.SettingsLoader.Load(f.Paths).Settings;

    private static List<BackupInfo> Backups(InstanceFixture f, string name) => [.. new BackupStore(f.Paths, Settings(f)).List(name)];

    /// <summary>sqlite3 .backup as a plain copy.</summary>
    private static void FakeSqlite(InstanceFixture f) => f.Runner.Handler = (program, args) =>
    {
        if (program != "sqlite3")
            return null;
        File.Copy(args[0], args[1][".backup '".Length..^1]);
        return new ProcessResult(0, "", "");
    };

    private static void MarkRunning(InstanceFixture f, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(f.Instances.RunStateFile(name))!);
        new RunState(Environment.ProcessId, 1, "r1", DateTimeOffset.Now, DateTimeOffset.Now).Save(f.Instances.RunStateFile(name));
        Directory.CreateDirectory(f.Instances.LogDir(name));
        File.AppendAllText(Path.Combine(f.Instances.LogDir(name), "OpenSim.log"), "2026-11-26 03:00:00,000 INFO  [SCENE]: started\n");
    }

    private static async Task<InstanceFixture> SqliteStandalone()
    {
        var f = new InstanceFixture();
        await f.Ok("instance", "add", "A", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Home", "--instance", "A", "--location", "1000,1000");
        File.WriteAllBytes(Path.Combine(f.Instances.DataDir("A"), "OpenSim.db"), [.. "SQLite format 3\0"u8, .. "the backed-up one"u8]);
        FakeSqlite(f);
        await f.Ok("backup", "create", "A");
        return f;
    }

    [Fact]
    public async Task AStandaloneIsRestoredFromItsNewestBackupAfterBeingBackedUpAsItIs()
    {
        using var f = await SqliteStandalone();
        var db = Path.Combine(f.Instances.DataDir("A"), "OpenSim.db");
        File.WriteAllBytes(db, [.. "SQLite format 3\0"u8, .. "changed since"u8]);
        File.WriteAllText(db + "-wal", "a stale journal");

        var plan = await f.Ok("backup", "restore", "A");
        Assert.Contains("replace data/OpenSim.db", plan);
        Assert.Contains("Add --yes", plan);
        Assert.Contains("changed since", File.ReadAllText(db));

        var output = await f.Ok("backup", "restore", "A", "--yes");

        Assert.Contains("restored from", output);
        Assert.Contains("the backed-up one", File.ReadAllText(db));
        Assert.False(File.Exists(db + "-wal"));
        // The backup made first holds the database as it was.
        var before = Backups(f, "A")[^1];
        Assert.Equal(2, Backups(f, "A").Count);
        Assert.True(before.Complete);
        Assert.Contains(before.Items, i => i.File == "OpenSim.db.gz");
    }

    [Fact]
    public async Task ARestoreIsRefusedWhileTheInstanceRunsOrForAnotherInstance()
    {
        using var f = await SqliteStandalone();
        await f.Ok("instance", "add", "B", "--db", "sqlite", "--estate-owner", "Test User");
        MarkRunning(f, "A");

        var (exit, _, err) = await f.Osctl("backup", "restore", "A", "--yes");
        Assert.Equal(2, exit);
        Assert.Contains("stop it first", err);

        (exit, _, err) = await f.Osctl("backup", "restore", "B", Backups(f, "A")[0].Dir, "--yes");
        Assert.Equal(2, exit);
        Assert.Contains("a backup goes back into the instance it came from", err);

        (exit, _, err) = await f.Osctl("backup", "restore", "A", "--db", "--oar", "Home");
        Assert.Equal(2, exit);
        Assert.Contains("two goes", err);
    }

    [Fact]
    public async Task ALostInstanceIsMadeAgainFromTheBackupsDefinition()
    {
        using var f = await SqliteStandalone();
        var backup = Backups(f, "A")[0];
        Assert.True(File.Exists(Path.Combine(backup.Dir, "definition", "instance.ini")));
        var copy = Path.Combine(f.Paths.Root, "elsewhere", backup.Name);
        Osctl.Util.FileTree.Copy(backup.Dir, copy);
        var uuid = f.Instances.Regions("A")[0].Uuid;
        await f.Ok("instance", "delete", "A", "--yes");

        var (exit, _, err) = await f.Osctl("backup", "restore", "A", copy, "--db", "--yes");
        Assert.Equal(2, exit);
        Assert.Contains("--definition", err);

        var output = await f.Ok("backup", "restore", "A", copy, "--yes");

        Assert.Contains("make the instance again", output);
        Assert.Equal(uuid, f.Instances.Regions("A").Single().Uuid);
        Assert.NotNull(f.Instances.RemoteAdminPassword("A"));
        Assert.Contains("the backed-up one", File.ReadAllText(Path.Combine(f.Instances.DataDir("A"), "OpenSim.db")));
        Assert.Contains("osctl start A", output);
    }

    [Fact]
    public async Task ASimulatorOnAGridLeavesTheSharedEstateDatabaseUnlessAsked()
    {
        using var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "Password", "dbpass");
        await f.Ok("grid", "add", "MyGrid", "--host", "grid.example.org");
        await f.Ok("instance", "add", "Sim1", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        await f.Ok("region", "add", "Welcome", "--instance", "Sim1", "--location", "1000,1000");
        await f.Ok("backup", "create", "Sim1");

        var plan = await f.Ok("backup", "restore", "Sim1");
        Assert.Contains("replace database opensim_Sim1", plan);
        Assert.Contains("not estate database opensim_estate_MyGrid: shared with MyGrid", plan);

        await f.Ok("backup", "restore", "Sim1", "--yes", "--no-backup");
        Assert.Equal(["opensim_Sim1"], f.Databases.Loaded.Select(l => l.Database));
        Assert.Equal("-- dump of opensim_Sim1\n", f.Databases.Loaded[0].Text);

        f.Databases.Loaded.Clear();
        await f.Ok("backup", "restore", "Sim1", "--db", "--with-estates", "--yes", "--no-backup");
        Assert.Equal(["opensim_Sim1", "opensim_estate_MyGrid"], f.Databases.Loaded.Select(l => l.Database));
    }

    [Fact]
    public async Task AGridGetsItsDatabasesAndAssetFilesBack()
    {
        using var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "Password", "dbpass");
        await f.Ok("grid", "add", "MyGrid", "--host", "grid.example.org");
        var assets = new ConfigGenerator(f.Paths, Settings(f)).AssetsDir(f.Instances.Load("MyGrid"));
        var kept = AssetSnapshotTests.WriteAsset(assets, "texture one");
        await f.Ok("backup", "create", "MyGrid");
        var later = AssetSnapshotTests.WriteAsset(assets, "made after the backup");

        await f.Ok("backup", "restore", "MyGrid", "--yes", "--no-backup");

        Assert.Equal(["opensim_MyGrid", "opensim_estate_MyGrid"], f.Databases.Loaded.Select(l => l.Database));
        Assert.True(File.Exists(Path.Combine(assets, "data", kept)));
        Assert.False(File.Exists(Path.Combine(assets, "data", later)));
    }

    [Fact]
    public async Task ARunningInstanceLoadsARegionsOarAndAUsersIar()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "A", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Home Sweet", "--instance", "A", "--location", "1000,1000");
        MarkRunning(f, "A");
        var log = Path.Combine(f.Instances.LogDir("A"), "OpenSim.log");
        f.Admin.OnSaveOar = (region, file) =>
        {
            File.WriteAllBytes(file, [1]);
            File.AppendAllText(log, $"2026-11-26 03:00:01,000 INFO  [ARCHIVER]: Finished writing out OAR for {region}\n");
            return null;
        };
        var loaded = new List<string>();
        f.Admin.OnLoadOar = (region, file) =>
        {
            loaded.Add($"{region} {Path.GetFileName(file)}");
            File.AppendAllText(log, "2026-11-26 03:00:02,000 INFO  [ARCHIVER]: Successfully loaded archive\n");
            return null;
        };
        f.Admin.OnConsoleCommand = command =>
        {
            if (command.StartsWith("load iar Test User / ", StringComparison.Ordinal))
                File.AppendAllText(log, "2026-11-26 03:00:03,000 INFO  [INVENTORY ARCHIVER]: Loaded 12 items from archive / for Test User\n");
            return null;
        };
        // A backup with an OAR and an IAR in it (the archives' contents do not matter here).
        var dir = Path.Combine(new BackupStore(f.Paths, Settings(f)).Root("A"), "20261126-030000");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Home_Sweet.oar"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "Test_User.iar"), [1]);
        BackupStore.WriteManifest(dir, "A", DateTimeOffset.Now, DateTimeOffset.Now,
            [new BackupItem("Home_Sweet.oar", "oar", null), new BackupItem("Test_User.iar", "iar", null)]);

        var (exit, _, err) = await f.Osctl("backup", "restore", "A", "--oar", "Nowhere");
        Assert.Equal(2, exit);
        Assert.Contains("no region 'Nowhere'", err);

        var output = await f.Ok("backup", "restore", "A", "20261126-030000", "--oar", "Home Sweet", "--iar", "Test User", "--no-backup", "--yes");

        Assert.Contains("restored from 20261126-030000", output);
        Assert.Equal(["Home Sweet Home_Sweet.oar"], loaded);
        // The password went through RemoteAdmin, not the console.
        Assert.Contains($"\"{f.Instances.OwnerPassword("A")}\"", f.Admin.Commands.Single());
    }
}
