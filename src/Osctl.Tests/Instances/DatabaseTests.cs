// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Databases;
using Osctl.Ini;
using Osctl.Settings;

namespace Osctl.Tests.Instances;

public class DatabaseTests
{
    [Fact]
    public async Task DbInitGeneratesMissingPasswordsKeepsCommentsAndGrants()
    {
        using var f = new InstanceFixture();
        var before = File.ReadAllText(f.Paths.SecretsFile);

        var output = await f.Ok("db", "init");

        var settings = SettingsLoader.Load(f.Paths).Settings.Database;
        Assert.Equal("adminpass", settings.AdminPassword);          // already there: kept
        Assert.Equal(24, settings.Password!.Length);                 // missing: generated
        var after = File.ReadAllText(f.Paths.SecretsFile);
        Assert.Contains("; Passwords for osctl and OpenSim.", after);
        Assert.Equal(before.Split('\n').Length, after.Split('\n').Length);
        Assert.Equal(Osctl.Setup.Initializer.SecretsMode, File.GetUnixFileMode(f.Paths.SecretsFile));

        Assert.Equal("/run/mysqld/mysqld.sock", f.Databases.Socket);
        Assert.Equal([("osctl_admin", "adminpass", @"opensim\_%"), ("opensim", settings.Password, @"opensim\_%")], f.Databases.Grants);
        Assert.Contains("have all rights", output);
        Assert.DoesNotContain("Warning", output);
    }

    [Fact]
    public async Task DbInitWarnsAboutASmallPacketLimitAndRefusesARemoteServer()
    {
        using var f = new InstanceFixture();
        f.Databases.MaxAllowedPacket = 16 * 1024 * 1024;
        Assert.Contains("max_allowed_packet is 16 MB", await f.Ok("db", "init"));

        IniFileEditor.SetValue(f.Paths.SettingsFile, "Database", "Host", "db.example.lan");
        var (exit, _, error) = await f.Osctl("db", "init");
        Assert.Equal(2, exit);
        Assert.Contains("by hand", error);
    }

    [Fact]
    public async Task AMariaDbInstanceGetsItsDatabasesAndDeleteDumpsThemFirst()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "Test1", "--estate-owner", "Test User");
        Assert.Equal(["opensim_Test1", "opensim_estate_standalone-Test1"], f.Databases.Existing.Order().Reverse());

        var output = await f.Ok("instance", "delete", "Test1", "--yes");

        Assert.Empty(f.Databases.Existing);
        var dumps = Directory.GetFiles(Path.Combine(f.Paths.Lib, "deleted"), "*.sql", SearchOption.AllDirectories);
        Assert.Equal(2, dumps.Length);
        Assert.Contains("dumped to", output);
    }

    [Fact]
    public async Task KeepDatabaseLeavesIt()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "Test1", "--estate-owner", "Test User");
        await f.Ok("instance", "delete", "Test1", "--yes", "--keep-database");
        Assert.Contains("opensim_Test1", f.Databases.Existing);
    }

    [Fact]
    public async Task MariaDbWithoutDbInitIsRefusedBeforeAnythingIsCreated()
    {
        using var f = new InstanceFixture();
        IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "AdminPassword", "");

        var (exit, _, error) = await f.Osctl("instance", "add", "Test1", "--estate-owner", "Test User");

        Assert.Equal(2, exit);
        Assert.Contains("sudo osctl db init", error);
        Assert.False(Directory.Exists(f.Instances.Dir("Test1")));
    }

    [Fact]
    public async Task SqliteNeedsNoDatabaseServer()
    {
        using var f = new InstanceFixture();
        IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "AdminPassword", "");
        await f.Ok("instance", "add", "Test1", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("instance", "delete", "Test1", "--yes");
        Assert.Empty(f.Databases.Existing);
    }

    [Theory]
    [InlineData("opensim_", @"opensim\_%")]
    [InlineData("os%", @"os\%%")]
    public void GrantPatternsEscapeWildcards(string prefix, string pattern) => Assert.Equal(pattern, DatabaseLimits.Pattern(prefix));

    [Fact]
    public void IdentifiersAndLiteralsAreQuoted()
    {
        Assert.Equal("`opensim_a-b`", DatabaseLimits.Quote("opensim_a-b"));
        Assert.Equal("`x``y`", DatabaseLimits.Quote("x`y"));
        Assert.Equal(@"'it''s \\'", DatabaseLimits.Literal(@"it's \"));
    }
}

public class IniFileEditorTests
{
    [Fact]
    public void ReplacesAValueAndKeepsEverythingElse()
    {
        using var dir = new TempDir();
        var path = dir.Write("s.ini", "; top comment\n[Database]\n    ; why\n    AdminPassword =\n    Password = old ; note\n[Other]\n    Password = keep\n");

        IniFileEditor.SetValue(path, "Database", "Password", "new;pw");
        IniFileEditor.SetValue(path, "Database", "AdminPassword", "adm");

        Assert.Equal("; top comment\n[Database]\n    ; why\n    AdminPassword = adm\n    Password = \"new;pw\"\n[Other]\n    Password = keep\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void AddsMissingKeysAndSections()
    {
        using var dir = new TempDir();
        var path = dir.Write("s.ini", "[Database]\n    Host = x\n\n[Tmux]\n    Prefix = C-a\n");

        IniFileEditor.SetValue(path, "Database", "Port", "3307");
        IniFileEditor.SetValue(path, "Ports", "RegionPortMin", "9100");

        var doc = IniParser.ParseFile(path);
        Assert.Equal("3307", doc.Get("Database", "Port"));
        Assert.Equal("9100", doc.Get("Ports", "RegionPortMin"));
        Assert.Equal("C-a", doc.Get("Tmux", "Prefix"));
        Assert.StartsWith("[Database]\n    Host = x\n    Port = 3307\n", File.ReadAllText(path));
    }
}
