// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Builds;
using Osctl.Cli;
using Osctl.ConfigSets;
using Osctl.Databases;
using Osctl.Instances;
using Osctl.Settings;
using Osctl.Setup;

namespace Osctl.Tests.Instances;

/// <summary>
/// An osctl root after 'osctl init', with a build 'b1' whose current release's bin/ holds small stand-ins for
/// OpenSim's ini files, wired with the same includes as the real ones (Standalone.ini → StandaloneCommon.ini →
/// storage/SQLiteStandalone.ini and FlotsamCache.ini).
/// </summary>
internal sealed class InstanceFixture : IDisposable
{
    private readonly TempDir dir = new();

    public InstanceFixture(string? realBin = null)
    {
        Paths = new OsctlPaths(dir.Path);
        new Initializer(Paths).Run(dryRun: false);
        global::Osctl.Ini.IniFileEditor.SetValue(Paths.SecretsFile, "Database", "AdminPassword", "adminpass");

        var builds = new BuildStore(Paths);
        builds.Save(new BuildDefinition("b1", "/nowhere", []));
        var bin = Path.Combine(builds.ReleaseDir("b1", "r1"), "bin");
        Directory.CreateDirectory(bin);
        if (realBin is not null)
            Util.FileTree.Copy(realBin, bin);
        else
            WriteFakeBin(bin);
        builds.SetCurrent("b1", "r1");
        Bin = builds.CurrentBin("b1");
    }

    public OsctlPaths Paths { get; }

    public string Bin { get; }

    public InstanceStore Instances => new(Paths);

    public FakePorts Ports { get; } = new();

    public FakeDownloader Web { get; } = new();

    public FakeDatabases Databases { get; } = new();

    public ConfigGenerator Generator(OsctlSettings? settings = null) =>
        new(Paths, settings ?? SettingsLoader.Load(Paths).Settings);

    /// <summary>What osctl runs (empty tables: every program is "not found" unless a test answers it).</summary>
    public FakeRunner Runner { get; } = new();

    public Running.FakeRemoteAdmin Admin { get; } = new();

    /// <summary>Lines given to osctl as its input (passwords).</summary>
    public string? Input { get; set; }

    /// <summary>Where 'units install' and 'schedule' write systemd units in these tests.</summary>
    public string UnitDir => System.IO.Path.Combine(dir.Path, "units");

    public async Task<(int Exit, string Out, string Err)> Osctl(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new CliEnvironment(Runner, output, error, Downloader: Web, Ports: Ports, Databases: Databases, RemoteAdmin: Admin,
            OsctlCommand: ["/usr/bin/osctl"], Input: new StringReader(Input ?? ""), SystemdUnitDir: UnitDir);
        var exit = await CliApp.RunAsync([.. args, "--root", Paths.Root], env);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Runs osctl and fails the test with its error output unless it exits 0.</summary>
    public async Task<string> Ok(params string[] args)
    {
        var (exit, output, error) = await Osctl(args);
        Assert.True(exit == 0, $"osctl {string.Join(' ', args)} exited {exit}: {error}{output}");
        return output;
    }

    public string Write(string relative, string text) => dir.Write(relative, text);

    public void Dispose() => dir.Dispose();

    private static void WriteFakeBin(string bin)
    {
        void W(string rel, string text)
        {
            var path = Path.Combine(bin, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        W("OpenSimDefaults.ini", "[Startup]\n    physics = ubODE\n    crash_dir = crashes\n[Network]\n    http_listener_port = 9000\n[Mesh]\n    MeshFileCache = true\n");
        W("OpenSim.ini.example",
            "[Startup]\n    physics = BulletSim\n[Architecture]\n    Include-Architecture = \"config-include/Standalone.ini\"\n");
        W("config-include/Standalone.ini", "[Modules]\n    AssetServices = LocalAssetServicesConnector\n    Include-Common = \"config-include/StandaloneCommon.ini\"\n");
        W("config-include/StandaloneHypergrid.ini", "[Hypergrid]\n    HomeURI = http://localhost:9000\n[Modules]\n    Include-Common = \"config-include/StandaloneCommon.ini\"\n");
        W("config-include/GridHypergrid.ini", "[Modules]\n    AssetServices = RemoteAssetServicesConnector\n    Include-Common = \"config-include/GridCommon.ini\"\n");
        W("config-include/StandaloneCommon.ini.example",
            "[DatabaseService]\n    Include-Storage = \"config-include/storage/SQLiteStandalone.ini\";\n" +
            "[Modules]\n    Include-FlotsamCache = \"config-include/FlotsamCache.ini\"\n");
        W("config-include/storage/SQLiteStandalone.ini",
            "[DatabaseService]\n    StorageProvider = \"OpenSim.Data.SQLite.dll\"\n    ConnectionString = \"URI=file:OpenSim.db;UseUTF16Encoding=True\"\n" +
            "[AssetService]\n    ConnectionString = \"URI=file:Asset.db\"\n");
        W("config-include/FlotsamCache.ini.example", "[AssetCache]\n    CacheDirectory = ./assetcache\n");
        W("OpenSim.exe.config", """
            <?xml version="1.0" encoding="utf-8" ?>
            <configuration>
              <log4net>
                <appender name="LogFileAppender" type="log4net.Appender.FileAppender">
                  <file value="OpenSim.log" />
                </appender>
                <appender name="StatsLogFileAppender" type="log4net.Appender.FileAppender">
                  <file value="OpenSimStats.log"/>
                </appender>
              </log4net>
            </configuration>
            """);
    }
}

internal sealed class FakePorts : IPortProbe
{
    public HashSet<int> Busy { get; } = [];

    public bool IsFree(int port) => !Busy.Contains(port);
}

internal sealed class FakeDownloader : IDownloader
{
    public Dictionary<string, string> Pages { get; } = [];

    public List<string> Requests { get; } = [];

    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        Requests.Add(url);
        return Pages.TryGetValue(url, out var text)
            ? Task.FromResult(text)
            : throw new HttpRequestException($"404 for {url}");
    }
}

/// <summary>A database server in memory: databases, grants, and dumps written as small files.</summary>
internal sealed class FakeDatabases : IDatabaseFactory, IDatabaseAdmin, IDatabaseRoot
{
    public HashSet<string> Existing { get; } = [];

    public List<(string User, string Password, string Pattern)> Grants { get; } = [];

    public long MaxAllowedPacket { get; set; } = DatabaseLimits.RecommendedMaxAllowedPacket;

    public string? Socket { get; private set; }

    public IDatabaseAdmin Admin(Osctl.Settings.DatabaseSettings settings) =>
        string.IsNullOrEmpty(settings.AdminPassword) ? throw new OsctlException("No database admin password in secrets.ini: run 'sudo osctl db init' first") : this;

    public IDatabaseRoot Root(string socket)
    {
        Socket = socket;
        return this;
    }

    public Task<bool> ExistsAsync(string database, CancellationToken ct) => Task.FromResult(Existing.Contains(database));

    public Task CreateAsync(string database, CancellationToken ct)
    {
        Existing.Add(database);
        return Task.CompletedTask;
    }

    public Task DropAsync(string database, CancellationToken ct)
    {
        Existing.Remove(database);
        return Task.CompletedTask;
    }

    public Task DumpAsync(string database, string file, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"-- dump of {database}\n");
        return Task.CompletedTask;
    }

    public Task<(string Version, long MaxAllowedPacket)> ProbeAsync(CancellationToken ct) => Task.FromResult(("11.8.6-MariaDB", MaxAllowedPacket));

    public Task GrantAsync(string user, string password, string databasePattern, CancellationToken ct)
    {
        Grants.Add((user, password, databasePattern));
        return Task.CompletedTask;
    }

    public Task<long> MaxAllowedPacketAsync(CancellationToken ct) => Task.FromResult(MaxAllowedPacket);
}
