// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;
using Osctl.Instances;

namespace Osctl.Tests.Instances;

public class ConfigGeneratorTests
{
    private static async Task<InstanceFixture> WithInstance(string db = "sqlite", string configSet = "standalone")
    {
        var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "Password", "dbpass");
        await f.Ok("instance", "add", "Test1", "--db", db, "--configset", configSet, "--estate-owner", "Test User");
        await f.Ok("region", "add", "Welcome", "--instance", "Test1", "--location", "1000,1000");
        return f;
    }

    [Fact]
    public async Task AStandaloneFollowsItsIncludesAndGetsOsctlsSettings()
    {
        using var f = await WithInstance();
        var g = f.Generator().Generate("Test1");
        var c = g.OpenSim;
        var i = f.Instances;

        Assert.Equal("BulletSim", c.Get("Startup", "physics"));                    // OpenSim.ini over OpenSimDefaults.ini
        Assert.Equal("LocalAssetServicesConnector", c.Get("Modules", "AssetServices"));  // from the include chain
        Assert.DoesNotContain(c.Sections, s => s.Keys.Any(k => k.StartsWith("Include-")));
        Assert.Null(c["Architecture"]);                                             // emptied by removing its include

        Assert.Equal("9000", c.Get("Network", "http_listener_port"));
        Assert.Equal("9500", c.Get("Network", "http_listener_sslport"));
        Assert.Equal(i.PidFile("Test1"), c.Get("Startup", "PIDFile"));
        Assert.Equal(i.AddinsDir("Test1"), c.Get("Startup", "RegistryLocation"));
        Assert.Equal(i.ScriptEnginesDir("Test1"), c.Get("YEngine", "ScriptEnginesPath"));
        Assert.Equal(Path.Combine(f.Paths.Cache, "assets", "standalone-Test1"), c.Get("AssetCache", "CacheDirectory"));
        Assert.Equal("Test1 Estate", c.Get("Estates", "DefaultEstateName"));
        Assert.Equal(i.OwnerPassword("Test1"), c.Get("Estates", "DefaultEstateOwnerPassword"));
        Assert.Equal(i.RemoteAdminPassword("Test1"), c.Get("RemoteAdmin", "access_password"));
        Assert.Equal("127.0.0.1", c.Get("RemoteAdmin", "access_ip_addresses"));

        // SQLite files move from bin/ to the instance's data/.
        Assert.Equal($"URI=file:{Path.Combine(i.DataDir("Test1"), "OpenSim.db")};UseUTF16Encoding=True", c.Get("DatabaseService", "ConnectionString"));
        Assert.Equal($"URI=file:{Path.Combine(i.DataDir("Test1"), "Asset.db")}", c.Get("AssetService", "ConnectionString"));
    }

    [Fact]
    public async Task MariaDbSkipsTheSqliteStorageInclude()
    {
        using var f = await WithInstance(db: "mariadb");
        var c = f.Generator().Generate("Test1").OpenSim;

        Assert.Equal("OpenSim.Data.MySQL.dll", c.Get("DatabaseService", "StorageProvider"));
        Assert.Contains("Database=opensim_Test1;", c.Get("DatabaseService", "ConnectionString"));
        Assert.Contains("Password=dbpass;", c.Get("DatabaseService", "ConnectionString"));
        Assert.Contains("Database=opensim_estate_standalone-Test1;", c.Get("DatabaseService", "EstateConnectionString"));
        Assert.Null(c.Get("AssetService", "ConnectionString"));
    }

    [Fact]
    public async Task ConfigSetEditsChooseTheArchitectureBeforeIncludesAreFollowed()
    {
        using var f = await WithInstance(configSet: "standalone-hg");
        var c = f.Generator().Generate("Test1").OpenSim;
        Assert.Equal("http://localhost:9000", c.Get("Hypergrid", "HomeURI"));
    }

    [Fact]
    public async Task OverridesApplyGlobalThenInstanceThenOsctl()
    {
        using var f = await WithInstance();
        f.Write("etc/osctl/overrides/10-all.ini", "[Startup]\n    physics = ubODE\n    MaxPoolThreads = 100\n[Network]\n    http_listener_port = 1\n");
        f.Write("etc/osctl/instances/Test1/overrides/20-mine.ini", "[Startup]\n    MaxPoolThreads = 200\n    Include-Extra = x.ini\n");

        var g = f.Generator().Generate("Test1");

        Assert.Equal("ubODE", g.OpenSim.Get("Startup", "physics"));
        Assert.Equal("200", g.OpenSim.Get("Startup", "MaxPoolThreads"));
        Assert.Equal("9000", g.OpenSim.Get("Network", "http_listener_port"));   // osctl's own wins
        Assert.Contains(g.Warnings, w => w.Contains("Include-Extra is not followed"));
    }

    private const string SasVoice = "[WebRtcVoice]\n    Enabled = true\n    SpatialVoiceService = WebRtcSas.dll:SasVoiceService\n" +
        "[SasWebRtcVoice]\n    ServerURI = http://10.0.0.5:5000\n";

    [Theory]
    [InlineData("", false)]                                                          // no voice at all
    [InlineData(SasVoice, true)]
    [InlineData(SasVoice + "[SimulatorFeatures]\n    StunServers = \"stun.example.org:3478\"\n", false)]
    [InlineData(SasVoice + "[Startup]\n    StunServers = stun.example.org:3478\n", false)]
    [InlineData(SasVoice + "[Startup]\n    StunServers = stun.example.org:3478\n[SimulatorFeatures]\n    StunServers = \"\"\n", true)]   // the last section wins
    [InlineData("[WebRtcVoice]\n    Enabled = false\n[SasWebRtcVoice]\n    ServerURI = x\n", false)]
    [InlineData("[WebRtcVoice]\n    Enabled = True\n    SpatialVoiceService = WebRtcJanus.dll:WebRtcJanusService\n", false)]   // not SAS
    [InlineData("[WebRtcVoice]\n    Enabled = yes\n    NonSpatialVoiceService = WebRtcSas.dll:SasVoiceService\n", true)]
    public async Task SasVoiceWithoutStunServersIsWarnedAbout(string overrides, bool warned)
    {
        using var f = await WithInstance();
        f.Write("etc/osctl/instances/Test1/overrides/50-voice.ini", overrides);

        var g = f.Generator().Generate("Test1");

        Assert.Equal(warned, g.Warnings.Any(w => w.Contains("no StunServers")));
    }

    [Fact]
    public async Task StartShowsTheConfigurationWarnings()
    {
        using var f = await WithInstance();
        f.Write("etc/osctl/instances/Test1/overrides/50-voice.ini", SasVoice);

        var (_, _, err) = await f.Osctl("--dry-run", "start", "Test1");

        Assert.Contains("osctl: warning: Instance 'Test1' has SpatialAudioServer voice", err);
    }

    [Fact]
    public async Task RegionsGetDefaultsThenModuleFilesThenTheirOwnSettings()
    {
        using var f = await WithInstance();
        f.Write("etc/osctl/instances/Test1/regions/voice.ini", "[Welcome]\n    MaxAgents = 50\n    VoiceRoom = lobby\n");
        await f.Ok("region", "set", "Welcome", "MaxAgents=40");

        var r = f.Generator().Generate("Test1").Regions["Welcome"]!;

        Assert.Equal("SYSTEMIP", r["ExternalHostName"]);   // region-defaults.ini
        Assert.Equal("lobby", r["VoiceRoom"]);              // module file
        Assert.Equal("40", r["MaxAgents"]);                 // the region's own
        Assert.Equal("1000,1000", r["Location"]);
        Assert.Equal("9000", r["InternalPort"]);

        f.Write("etc/osctl/instances/Test1/regions/typo.ini", "[Welcom]\n    MaxAgents = 1\n");
        Assert.Contains("not a region", Assert.Throws<OsctlException>(() => f.Generator().Generate("Test1")).Message);
    }

    [Fact]
    public async Task WriteProducesTheFilesWithTheRightModesAndLogPaths()
    {
        using var f = await WithInstance();
        var generator = f.Generator();
        generator.Write(generator.Generate("Test1"));
        var dir = f.Instances.GeneratedDir("Test1");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(Path.Combine(dir, "OpenSim.ini")));
        Assert.True(File.Exists(Path.Combine(dir, "Regions", "Regions.ini")));
        Assert.True(Directory.Exists(Path.Combine(dir, "config")));
        var log4net = File.ReadAllText(Path.Combine(dir, "log4net.config"));
        Assert.Contains("encoding=\"utf-8\"", log4net);
        Assert.Contains($"value=\"{Path.Combine(f.Instances.LogDir("Test1"), "OpenSim.log")}\"", log4net);
        Assert.Contains($"value=\"{Path.Combine(f.Instances.LogDir("Test1"), "OpenSimStats.log")}\"", log4net);
    }

    [Fact]
    public async Task ExplainShowsEachLayerThatSetAKey()
    {
        using var f = await WithInstance();
        f.Write("etc/osctl/overrides/10-all.ini", "[Startup]\n    physics = ubODE\n");

        var output = await f.Ok("config", "explain", "Test1", "Startup", "physics");
        var lines = output.Trim().Split('\n');

        Assert.StartsWith("OpenSimDefaults.ini", lines[0]);
        Assert.StartsWith("OpenSim.ini (from ", lines[1]);
        Assert.Contains("10-all.ini", lines[2]);
        Assert.Equal("=> [Startup] physics = ubODE", lines[3]);

        var forced = await f.Ok("config", "explain", "Test1", "Network", "http_listener_port");
        Assert.Contains("set by osctl: http_listener_port = 9000", forced);
        var secret = await f.Ok("config", "explain", "Test1", "Estates", "DefaultEstateOwnerPassword");
        Assert.Contains(SecretMask.Hidden, secret);
        Assert.Equal(1, (await f.Osctl("config", "explain", "Test1", "Nowhere", "nothing")).Exit);
    }

    [Fact]
    public async Task ShowHidesSecretsAndDiffSeesChanges()
    {
        using var f = await WithInstance(db: "mariadb");
        var shown = await f.Ok("config", "show", "Test1", "--section", "DatabaseService", "Estates");
        Assert.Contains($"Password={SecretMask.Hidden};", shown);
        Assert.DoesNotContain("dbpass", shown);
        Assert.Contains("dbpass", await f.Ok("config", "show", "Test1", "--section", "DatabaseService", "--show-secrets"));

        Assert.Equal(1, (await f.Osctl("config", "diff", "Test1")).Exit);   // nothing written yet
        await f.Ok("config", "generate", "Test1");
        Assert.Contains("No differences.", await f.Ok("config", "diff", "Test1"));

        f.Write("etc/osctl/overrides/10-all.ini", "[Startup]\n    physics = ubODE\n");
        var (exit, diff, _) = await f.Osctl("config", "diff", "Test1");
        Assert.Equal(1, exit);
        Assert.Contains("- [Startup] physics = BulletSim", diff);
        Assert.Contains("+ [Startup] physics = ubODE", diff);
    }

    [Fact]
    public async Task ABuildWithoutAReleaseIsAnError()
    {
        using var f = await WithInstance();
        File.Delete(Path.Combine(f.Paths.BuildsDir, "b1", "current"));
        var e = Assert.Throws<OsctlException>(() => f.Generator().Generate("Test1"));
        Assert.Contains("osctl build update b1", e.Message);
    }

    [SkippableFact]
    public async Task TheRealOpenSimFilesGenerateAStandalone()
    {
        var bin = RepoPaths.OpenSimBin();
        Skip.If(bin is null, "No OpenSim bin/ (set OSCTL_TEST_OPENSIM_BIN)");
        using var f = new InstanceFixture(realBin: bin);
        await f.Ok("instance", "add", "Test1", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Welcome", "--instance", "Test1", "--location", "1000,1000");

        var g = f.Generator().Generate("Test1");

        Assert.Contains(g.Layers, l => l.Path == "config-include/Standalone.ini");
        Assert.Contains(g.Layers, l => l.Path == "config-include/storage/SQLiteStandalone.ini");
        Assert.DoesNotContain(g.OpenSim.Sections, s => s.Keys.Any(k => k.StartsWith("Include-")));
        Assert.StartsWith($"URI=file:{f.Instances.DataDir("Test1")}", g.OpenSim.Get("InventoryService", "ConnectionString"));
        Assert.NotNull(g.Log4Net);
        IniParser.Parse(IniWriter.ToText(g.OpenSim));   // writes and reads back
    }
}
