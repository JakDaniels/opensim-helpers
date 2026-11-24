// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;
using System.IO.Compression;
using Osctl.Operations;
using Osctl.Processes;
using Osctl.Running;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Operations;

public class BackupTests
{
    private static BackupInfo Backup(string when, bool complete = true) =>
        new("A", when, "/x/" + when, DateTime.ParseExact(when, BackupStore.NameFormat, null), complete, 0, []);

    [Fact]
    public void PruningKeepsTheNewestOfRecentDaysAndWeeks()
    {
        var backups = new[]
        {
            Backup("20261101-030000"),   // Sun, week 44
            Backup("20261108-030000"),   // Sun, week 45
            Backup("20261115-030000"),   // Sun, week 46
            Backup("20261120-030000"),   // Fri, week 47
            Backup("20261121-030000"),   // Sat, week 47
            Backup("20261122-030000"),   // Sun, week 47
            Backup("20261122-150000"),   // the same Sunday, later
            Backup("20261123-030000", complete: false),
            Backup("20261124-030000"),   // Tue, week 48
        };

        var pruned = BackupStore.ToPrune(backups, keepDaily: 3, keepWeekly: 3).Select(b => b.Name).ToList();

        // Days kept: 24th, 22nd (15:00), 21st. Weeks kept: 48 (24th), 47 (22nd 15:00), 46 (15th).
        Assert.Equal(["20261101-030000", "20261108-030000", "20261120-030000", "20261122-030000", "20261123-030000"], pruned);
    }

    [Fact]
    public void AnIncompleteBackupStaysUntilACompleteOneFollows()
    {
        var backups = new[] { Backup("20261122-030000"), Backup("20261123-030000", complete: false) };
        Assert.Empty(BackupStore.ToPrune(backups, 1, 0));
        Assert.Equal(["20261122-030000"], BackupStore.ToPrune([backups[0], Backup("20261124-030000")], 1, 0).Select(b => b.Name));
    }

    /// <summary>An instance 'A' with two regions that looks running (its run state names this test process).</summary>
    private static async Task<InstanceFixture> RunningInstance(string db = "mariadb")
    {
        var f = new InstanceFixture();
        await f.Ok("instance", "add", "A", "--db", db, "--estate-owner", "Test User");
        await f.Ok("region", "add", "Home Sweet", "--instance", "A", "--location", "1000,1000");
        await f.Ok("region", "add", "Away", "--instance", "A", "--location", "1001,1000");
        Directory.CreateDirectory(Path.GetDirectoryName(f.Instances.RunStateFile("A"))!);
        new RunState(Environment.ProcessId, 1, "r1", DateTimeOffset.Now, DateTimeOffset.Now).Save(f.Instances.RunStateFile("A"));
        Directory.CreateDirectory(f.Instances.LogDir("A"));
        File.WriteAllText(OpenSimLog(f), "2026-11-24 03:00:00,000 INFO  [SCENE]: started\n");
        return f;
    }

    private static string OpenSimLog(InstanceFixture f) => Path.Combine(f.Instances.LogDir("A"), "OpenSim.log");

    private static void WriteArchive(string file)
    {
        using var gz = new GZipStream(File.Create(file), CompressionLevel.Fastest);
        using var tar = new TarWriter(gz);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "archive.xml") { DataStream = new MemoryStream("<archive/>"u8.ToArray()) };
        tar.WriteEntry(entry);
    }

    /// <summary>OpenSim as RemoteAdmin and its log show it: OARs and IARs are written, then the log says so.</summary>
    private static void FakeOpenSim(InstanceFixture f, string iarPassword = "secret")
    {
        f.Admin.OnSaveOar = (region, file) =>
        {
            WriteArchive(file);
            File.AppendAllText(OpenSimLog(f), $"2026-11-24 03:00:01,000 INFO  [ARCHIVER]: Finished writing out OAR for {region}\n");
            return null;
        };
        f.Admin.OnConsoleCommand = command =>
        {
            // save iar First Last / "password" "file"
            var parts = command.Split('"');
            var user = command["save iar ".Length..command.IndexOf(" / ", StringComparison.Ordinal)];
            if (parts[1] != iarPassword)
                File.AppendAllText(OpenSimLog(f), $"2026-11-24 03:00:02,000 ERROR [INVENTORY ARCHIVER]: Password for user {user} incorrect.  Please try again.\n");
            else
            {
                WriteArchive(parts[3]);
                File.AppendAllText(OpenSimLog(f), $"2026-11-24 03:00:02,000 INFO  [INVENTORY ARCHIVER]: Saved archive with 12 items for {user}\n");
            }
            return null;
        };
    }

    [Fact]
    public async Task ARunningInstanceIsBackedUpWithOarsDatabasesAndTheOwnersInventory()
    {
        using var f = await RunningInstance();
        FakeOpenSim(f, iarPassword: f.Instances.OwnerPassword("A")!);
        f.Databases.Existing.UnionWith(["opensim_A", "opensim_estate_standalone-A"]);

        var output = await f.Ok("backup", "create", "A", "--oar", "--db", "--iar", "Test User");

        var backup = new BackupStore(f.Paths, Settings(f)).List("A").Single();
        Assert.True(backup.Complete, output);
        Assert.Equal(["Away.oar", "Home_Sweet.oar", "Test_User.iar", "opensim_A.sql.gz", "opensim_estate_standalone-A.sql.gz"],
            backup.Items.Select(i => i.File).Order(StringComparer.Ordinal));
        Assert.Null(BackupRunner.CheckArchive(Path.Combine(backup.Dir, "Home_Sweet.oar")));
        await using (var gz = new GZipStream(File.OpenRead(Path.Combine(backup.Dir, "opensim_A.sql.gz")), CompressionMode.Decompress))
            Assert.Equal("-- dump of opensim_A\n", await new StreamReader(gz).ReadToEndAsync());
        Assert.DoesNotContain(Directory.GetFiles(backup.Dir), p => p.EndsWith(".sql", StringComparison.Ordinal));
        // The password went through RemoteAdmin, not the console.
        Assert.Contains($"\"{f.Instances.OwnerPassword("A")}\"", f.Admin.Commands.Single());

        Assert.Contains("1 iar", await f.Ok("backup", "list"));
    }

    [Fact]
    public async Task AWrongIarPasswordMakesTheBackupIncomplete()
    {
        using var f = await RunningInstance();
        FakeOpenSim(f, iarPassword: "the real one");
        f.Input = "not it\n";
        await f.Ok("backup", "iar-password", "A", "Other Person");
        Assert.Equal("not it", f.Instances.IarPassword("A", "Other Person"));

        var (exit, output, error) = await f.Osctl("backup", "create", "A", "--iar", "Other Person");

        Assert.Equal(1, exit);
        Assert.Contains("INCOMPLETE", output);
        Assert.Contains("Other_Person.iar: OpenSim says the password for Other Person is wrong", error);
        Assert.False(new BackupStore(f.Paths, Settings(f)).List("A").Single().Complete);
    }

    [Fact]
    public async Task AStoppedSqliteInstanceIsBackedUpFromItsDatabaseFiles()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "A", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Home", "--instance", "A", "--location", "1000,1000");
        var data = f.Instances.DataDir("A");
        File.WriteAllBytes(Path.Combine(data, "OpenSim.db"), [.. "SQLite format 3\0"u8, .. new byte[100]]);
        File.WriteAllText(Path.Combine(data, "notes.txt"), "not a database");
        f.Runner.Handler = (program, args) =>
        {
            if (program != "sqlite3")
                return null;
            File.Copy(args[0], args[1][".backup '".Length..^1]);
            return new ProcessResult(0, "", "");
        };

        var output = await f.Ok("backup", "create", "A");

        Assert.Contains("not running, so no OARs", output);
        var backup = new BackupStore(f.Paths, Settings(f)).List("A").Single();
        Assert.True(backup.Complete);
        Assert.Equal("OpenSim.db.gz", backup.Items.Single().File);
    }

    [Fact]
    public async Task AScheduledBackupPrunesAfterwards()
    {
        using var f = await RunningInstance();
        FakeOpenSim(f);
        var root = new BackupStore(f.Paths, Settings(f)).Root("A");
        foreach (var old in new[] { "20200101-030000", "20200102-030000" })
        {
            Directory.CreateDirectory(Path.Combine(root, old));
            BackupStore.WriteManifest(Path.Combine(root, old), "A", DateTimeOffset.Now, DateTimeOffset.Now, []);
        }
        Global(f, "Backups", "KeepWeekly", "0");
        Global(f, "Backups", "KeepDaily", "2");

        var output = await f.Ok("backup", "create", "--scheduled", "A");

        Assert.Contains("removed backup 20200101-030000", output);
        Assert.Equal(2, new BackupStore(f.Paths, Settings(f)).List("A").Count);
    }

    private static void Global(InstanceFixture f, string section, string key, string value) =>
        global::Osctl.Ini.IniFileEditor.SetValue(f.Paths.SettingsFile, section, key, value);

    private static global::Osctl.Settings.OsctlSettings Settings(InstanceFixture f) => global::Osctl.Settings.SettingsLoader.Load(f.Paths).Settings;
}
