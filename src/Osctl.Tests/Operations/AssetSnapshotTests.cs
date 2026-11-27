// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Osctl.Instances;
using Osctl.Operations;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Operations;

/// <summary>A grid's FSAssets files in its backups: hard-linked snapshots, checked against their hashes.</summary>
public class AssetSnapshotTests
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-1);

    /// <summary>Writes an asset as FSAssets does (data/ab/cd/ef/0123/&lt;sha256&gt;.gz); returns its path in data/.</summary>
    public static string WriteAsset(string assetsDir, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rel = Path.Combine(hash[..2], hash[2..4], hash[4..6], hash[6..10], hash + ".gz");
        var file = Path.Combine(assetsDir, "data", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var gz = new GZipStream(File.Create(file), CompressionMode.Compress))
            gz.Write(bytes);
        File.SetLastWriteTimeUtc(file, LongAgo.AddDays(-1));
        return rel;
    }

    /// <summary>An asset stored but not yet written out: in the spool, as &lt;sha256&gt;.asset.</summary>
    public static string SpoolAsset(string assetsDir, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var name = Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".asset";
        Directory.CreateDirectory(Path.Combine(assetsDir, "tmp", "spool"));
        File.WriteAllBytes(Path.Combine(assetsDir, "tmp", name), bytes);
        File.WriteAllText(Path.Combine(assetsDir, "tmp", "spool", "half-written.asset"), "x");
        return name;
    }

    [Fact]
    public void TheNextSnapshotLinksTheFilesTheLastOneHas()
    {
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "assets");
        var first = WriteAsset(source, "texture one");
        var spooled = SpoolAsset(source, "a script");

        var a = AssetSnapshot.Take(source, Path.Combine(dir.Path, "b1"), null, LongAgo, CancellationToken.None);
        Assert.Equal((2, 2, 0), (a.Files, a.Copied, a.Skipped));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b1", "data", first)));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b1", "spool", spooled)));
        Assert.False(File.Exists(Path.Combine(dir.Path, "b1", "spool", "half-written.asset")));

        var second = WriteAsset(source, "texture two");
        var b = AssetSnapshot.Take(source, Path.Combine(dir.Path, "b2"), Path.Combine(dir.Path, "b1"), LongAgo, CancellationToken.None);
        Assert.Equal((3, 2), (b.Files, b.Copied));   // the spool file and the new asset; the first is linked
        Assert.True(AssetSnapshot.Matches(Path.Combine(dir.Path, "b2", "data", second), Path.GetFileName(second)[..^3], gzipped: true));

        // A hard link: one file under two names.
        File.AppendAllText(Path.Combine(dir.Path, "b1", "data", first), "!");
        Assert.Equal(new FileInfo(Path.Combine(dir.Path, "b1", "data", first)).Length,
            new FileInfo(Path.Combine(dir.Path, "b2", "data", first)).Length);
    }

    [Fact]
    public void AFileThatDoesNotMatchItsHashIsLeftOutWhenNewAndReportedWhenOld()
    {
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "assets");
        var writing = WriteAsset(source, "being written");
        var broken = WriteAsset(source, "long broken");
        File.WriteAllBytes(Path.Combine(source, "data", writing), [0x1f, 0x8b, 8]);   // a gzip header, nothing more yet
        File.WriteAllBytes(Path.Combine(source, "data", broken), [1, 2, 3]);
        File.SetLastWriteTimeUtc(Path.Combine(source, "data", broken), LongAgo.AddDays(-1));

        var counts = AssetSnapshot.Take(source, Path.Combine(dir.Path, "b1"), null, LongAgo, CancellationToken.None);

        Assert.Equal((1, 1), (counts.Files, counts.Skipped));
        Assert.False(File.Exists(Path.Combine(dir.Path, "b1", "data", writing)));
        Assert.Equal(broken, Assert.Single(counts.Damaged));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b1", "data", broken)));
    }

    [Fact]
    public async Task AGridsBackupHoldsItsDatabasesThenItsAssetFiles()
    {
        using var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "Password", "dbpass");
        await f.Ok("grid", "add", "MyGrid", "--host", "grid.example.org");
        var source = new ConfigGenerator(f.Paths, global::Osctl.Settings.SettingsLoader.Load(f.Paths).Settings)
            .AssetsDir(f.Instances.Load("MyGrid"));
        WriteAsset(source, "texture one");
        // An asset stored while the database is dumped is in the index, so it must be in the backup too.
        string? stored = null;
        f.Databases.OnDump = database => stored ??= SpoolAsset(source, "stored during the dump");

        var output = await f.Ok("backup", "create", "MyGrid");
        Assert.Contains("2 asset files, 2 new", output);

        var backup = new BackupStore(f.Paths, global::Osctl.Settings.SettingsLoader.Load(f.Paths).Settings).List("MyGrid").Single();
        Assert.True(backup.Complete);
        Assert.Equal(["definition", "fsassets", "opensim_MyGrid.sql.gz", "opensim_estate_MyGrid.sql.gz"],
            backup.Items.Select(i => i.File).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(backup.Dir, AssetSnapshot.Dir, "spool", stored!)));
        Assert.Equal(2, backup.Assets!.Files);

        await Task.Delay(1100);   // backups are named by the second
        WriteAsset(source, "texture two");
        await f.Ok("backup", "create", "MyGrid", "--db");
        // texture one is linked; texture two and the file still in the spool are copied.
        Assert.Contains("3 assets (2 new", await f.Ok("backup", "list", "MyGrid"));
    }
}
