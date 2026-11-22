// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Settings;

namespace Osctl.Tests.Settings;

public class SettingsLoaderTests
{
    [Fact]
    public void NoFilesGiveTheDefaults()
    {
        using var dir = new TempDir();
        var result = SettingsLoader.Load(new OsctlPaths(dir.Path));

        Assert.False(result.SettingsFileFound);
        Assert.Equal(OsctlSettings.Default, result.Settings);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ReadsSettingsAndSecrets()
    {
        using var dir = new TempDir();
        var paths = new OsctlPaths(dir.Path);
        dir.Write("etc/osctl/osctl.ini", "[Ports]\nRegionPortMin = 9100\nRegionPortMax = 9199\n[Database]\nHost = db.lan\n[Tmux]\nPrefix = C-b");
        dir.Write("etc/osctl/secrets.ini", "[Database]\nPassword = \"p;w\"\nAdminPassword = adm");

        var s = SettingsLoader.Load(paths).Settings;

        Assert.Equal(9100, s.Ports.RegionPortMin);
        Assert.Equal(9199, s.Ports.RegionPortMax);
        Assert.Equal("db.lan", s.Database.Host);
        Assert.Equal("p;w", s.Database.Password);
        Assert.Equal("adm", s.Database.AdminPassword);
        Assert.Equal("C-b", s.Tmux.Prefix);
        Assert.Equal(OsctlSettings.Default.Restart, s.Restart);
    }

    [Fact]
    public void UnknownKeysAndPasswordsInTheWrongFileAreWarnings()
    {
        using var dir = new TempDir();
        dir.Write("etc/osctl/osctl.ini", "[Ports]\nRegionPortMn = 9100\n[Database]\nPassword = oops");

        var result = SettingsLoader.Load(new OsctlPaths(dir.Path));

        Assert.Contains(result.Warnings, w => w.Contains("unknown setting [Ports] RegionPortMn"));
        Assert.Contains(result.Warnings, w => w.Contains("belong in"));
        Assert.Null(result.Settings.Database.Password);
    }

    [Theory]
    [InlineData("[Ports]\nRegionPortMin = lots", "expected a whole number")]
    [InlineData("[Ports]\nRegionPortMin = 70000", "expected a whole number")]
    [InlineData("[Ports]\nRegionPortMin = 9100\nRegionPortMax = 9000", "is below RegionPortMin")]
    public void BadValuesAreErrorsWithTheirLocation(string text, string message)
    {
        using var dir = new TempDir();
        dir.Write("etc/osctl/osctl.ini", text);

        var e = Assert.Throws<SettingsException>(() => SettingsLoader.Load(new OsctlPaths(dir.Path)));
        Assert.Contains(message, e.Message);
        Assert.Contains("osctl.ini", e.Message);
    }

    [Fact]
    public void PathsHangOffTheRoot()
    {
        var paths = new OsctlPaths("/tmp/r");
        Assert.Equal("/tmp/r/etc/osctl/osctl.ini", paths.SettingsFile);
        Assert.Equal("/tmp/r/var/lib/osctl/instances/Test1", paths.InstanceDir("Test1"));
        Assert.Equal("/run/osctl", new OsctlPaths("/").Run);
    }

    [Fact]
    public void TheRootOptionBeatsTheEnvironment()
    {
        Assert.Equal("/from/option", OsctlPaths.Resolve("/from/option").Root);
    }
}
