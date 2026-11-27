// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Osctl.Builds;
using Osctl.ConfigSets;
using Osctl.Ini;
using Osctl.Settings;

namespace Osctl.Instances;

/// <summary>
/// An instance's configuration, made but not yet written. <see cref="OpenSim"/> is the main file, written as
/// <see cref="MainFile"/>: OpenSim.ini for a simulator, Robust.ini for a grid's Robust instance (which has no regions).
/// </summary>
public sealed record GeneratedConfig(
    string Instance,
    string Bin,
    IniDocument OpenSim,
    IniDocument Regions,
    string? Log4Net,
    IReadOnlyList<IniLayer> Layers,
    IReadOnlyList<string> Warnings,
    string MainFile = ConfigGenerator.OpenSimFile);

/// <summary>
/// Makes an instance's OpenSim configuration (PLAN.md "Configuration generation"), in this order, later wins:
/// the config set's files with their includes (loaded as OpenSim loads them, from the build's current bin/),
/// /etc/osctl/overrides/*.ini, the instance's overrides/*.ini, then the settings osctl sets itself ("osctl" layer).
/// <c>Include-*</c> keys are removed from the result (they have been followed). Regions: /etc/osctl/region-defaults.ini,
/// the instance's region-defaults.ini, its other regions/*.ini (per-region module settings), then regions/Regions.ini.
/// </summary>
public sealed partial class ConfigGenerator(OsctlPaths paths, OsctlSettings settings)
{
    public const string OsctlLayer = "osctl";
    public const string OpenSimFile = "OpenSim.ini";
    public const string RobustFile = "Robust.ini";

    /// <summary>The flags the grid's default region gets: where new avatars and Hypergrid visitors arrive.</summary>
    public const string DefaultRegionFlags = "DefaultRegion, DefaultHGRegion, FallbackRegion";

    /// <summary>
    /// Includes OpenSim's own files always make but whose file is a local choice, shipped only as a .example:
    /// osslDefaultEnable.ini's <c>[OSSL] Include-osslEnable</c> (a config set's [Files] or overrides may supply it).
    /// </summary>
    private static readonly HashSet<string> OptionalIncludes = ["OSSL|Include-osslEnable"];
    public const string Log4NetFile = "log4net.config";

    private readonly InstanceStore instances = new(paths);
    private readonly ConfigSetStore configSets = new(paths);
    private readonly BuildStore builds = new(paths);

    public GeneratedConfig Generate(string name)
    {
        var instance = instances.Load(name);
        var set = configSets.Load(instance.ConfigSet);
        var bin = builds.CurrentBin(instance.Build);
        if (!builds.Exists(instance.Build))
            throw new OsctlException($"Instance '{name}' uses build '{instance.Build}', which does not exist");
        if (!Directory.Exists(bin))
            throw new OsctlException($"Build '{instance.Build}' has no release yet; run 'osctl build update {instance.Build}'");

        if (instance.IsRobust != (set.Mode == ConfigSetMode.Robust))
            throw new OsctlException(instance.IsRobust
                ? $"Instance '{name}' runs Robust, but config set '{set.Name}' is not a Robust one (Mode = robust)"
                : $"Config set '{set.Name}' is for Robust instances; instance '{name}' runs OpenSim");

        var regions = instance.IsRobust ? new List<RegionDefinition>() : instances.Regions(name);
        var warnings = new List<string>();
        if (regions.Count == 0 && !instance.IsRobust)
            warnings.Add($"Instance '{name}' has no regions yet ('osctl region add … --instance {name}')");

        // 1. The config set, as OpenSim would load it.
        var ignored = instance.Database == DatabaseKind.MariaDb
            ? new HashSet<string> { "DatabaseService|Include-Storage" }
            : null;
        var options = new IniLoadOptions(
            Overlay: rel => configSets.Resolve(set, rel, bin),
            Edit: (rel, doc) =>
            {
                foreach (var edit in set.Edits.Where(e => e.File == rel))
                    doc.Set(edit.Section, edit.Key, edit.Value, new IniOrigin(configSets.DefinitionFile(set.Name), 0));
            },
            IgnoredIncludes: ignored,
            OptionalIncludes: OptionalIncludes);
        var loaded = IniLoader.Load(set.Load, bin, options);
        warnings.AddRange(loaded.Warnings);
        var layers = new List<IniLayer>(loaded.Layers);
        var config = loaded.Document;

        // 2. and 3. The overrides, global then the instance's.
        foreach (var file in IniFiles(paths.GlobalOverridesDir).Concat(IniFiles(instances.OverridesDir(name))))
        {
            var doc = IniParser.ParseFile(file);
            foreach (var section in doc.Sections)
            {
                foreach (var include in section.Keys.Where(IsInclude).ToList())
                {
                    warnings.Add($"{section.GetEntry(include)!.Origin}: [{section.Name}] {include} is not followed in an override file");
                    section.Remove(include);
                }
            }
            config.Merge(doc);
            layers.Add(new IniLayer(file, file, doc));
        }

        // 4. What osctl sets itself.
        var grid = set.GridFor(name);
        var forced = instance.IsRobust
            ? ForcedRobust(instance, warnings)
            : Forced(instance, set, grid, regions, config, warnings);
        config.Merge(forced);
        layers.Add(new IniLayer(OsctlLayer, OsctlLayer, forced));

        // Includes have been followed, so they go; a section that held only includes ([Architecture]) goes with
        // them. Other empty sections stay: OpenSim keeps them, and some modules are on only when their section
        // exists ([NPC]: NPCModule checks config != null before reading Enabled, whose default is true).
        foreach (var section in config.Sections.ToList())
        {
            var includes = section.Keys.Where(IsInclude).ToList();
            foreach (var key in includes)
                section.Remove(key);
            if (section.Count == 0 && includes.Count > 0)
                config.RemoveSection(section.Name);
        }

        if (MissesStunServers(config))
            warnings.Add($"Instance '{name}' has SpatialAudioServer voice (os-webrtc-sas) but no StunServers: WebRTC viewers " +
                "get an empty STUN list and their voice never starts. Set it in an override, e.g. [Startup] " +
                "StunServers = stun.l.google.com:19302, stun.cloudflare.com:3478");

        return instance.IsRobust
            ? new GeneratedConfig(name, bin, config, new IniDocument(), Log4Net(name, bin, "Robust.exe.config", warnings), layers, warnings, RobustFile)
            : new GeneratedConfig(name, bin, config, RegionsConfig(name, regions), Log4Net(name, bin, "OpenSim.exe.config", warnings), layers, warnings);
    }

    /// <summary>
    /// The local grid a config set's simulators use (its <c>Robust =</c>), with its settings; null for any other set.
    /// </summary>
    public (InstanceDefinition Robust, GridDefinition Grid)? LocalGrid(ConfigSet set)
    {
        if (set.Robust is not { } robust)
            return null;
        if (!instances.Exists(robust))
            throw new OsctlException($"Config set '{set.Name}' is for grid '{robust}', which is not an instance here ('osctl grid list')");
        var definition = instances.Load(robust);
        return (definition, definition.GridSettings);
    }

    /// <summary>Writes the configuration to the instance's generated/ directory.</summary>
    /// <summary>
    /// True when the configuration sets up os-webrtc-sas (WebRTC voice enabled, and a [SasWebRtcVoice] section or a
    /// WebRtcSas voice service) without any STUN servers. OpenSim sends viewers <c>StunServers</c> (from [Const],
    /// [Startup], [GridInfo] or [SimulatorFeatures]) in the simulator features; with none, WebRTC viewers get an empty
    /// STUN list, cannot create their connection and never ask for voice.
    /// </summary>
    public static bool MissesStunServers(IniDocument config)
    {
        var voice = config["WebRtcVoice"];
        if (voice is null || !IsTrue(voice["Enabled"]))
            return false;
        var usesSas = config.Contains("SasWebRtcVoice")
            || new[] { "SpatialVoiceService", "NonSpatialVoiceService" }
                .Any(k => voice[k]?.Contains("WebRtcSas", StringComparison.OrdinalIgnoreCase) == true);
        if (!usesSas)
            return false;
        // As OpenSim's Util.GetConfigVarFromSections: the last of these sections that has the key wins, even if empty.
        var stuns = new[] { "Const", "Startup", "GridInfo", "SimulatorFeatures" }
            .Select(s => config.Get(s, "StunServers"))
            .LastOrDefault(v => v is not null);
        return stuns is null || stuns.Split(',').All(s => string.IsNullOrWhiteSpace(s.Trim('"')));
    }

    /// <summary>Nini's GetBoolean: true, yes, on or 1, in any case.</summary>
    private static bool IsTrue(string? value) =>
        value?.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1";

    public void Write(GeneratedConfig generated)
    {
        var dir = instances.GeneratedDir(generated.Instance);
        // It holds passwords (database, estate owner, RemoteAdmin): owner and group only.
        IniWriter.WriteFile(Path.Combine(dir, generated.MainFile), generated.OpenSim,
            [$"Generated by osctl for instance '{generated.Instance}': do not edit, it is rewritten at every start.",
             "Change settings in /etc/osctl/overrides or the instance's overrides/; 'osctl config explain' shows where a value comes from."],
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        if (generated.MainFile == OpenSimFile)
            IniWriter.WriteFile(Path.Combine(dir, "Regions", "Regions.ini"), generated.Regions,
                [$"Generated by osctl for instance '{generated.Instance}' from its regions/ directory: do not edit."]);
        // OpenSim reads *.ini from its -inidirectory after everything else; osctl points it at this empty directory.
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        if (generated.Log4Net is not null)
            File.WriteAllText(Path.Combine(dir, Log4NetFile), generated.Log4Net);
        Directory.CreateDirectory(instances.LogDir(generated.Instance));
    }

    public static int BasePort(IReadOnlyList<RegionDefinition> regions) => regions.Count == 0 ? 0 : regions.Min(r => r.Port);

    private IniDocument Forced(InstanceDefinition instance, ConfigSet set, string grid, List<RegionDefinition> regions,
        IniDocument loaded, List<string> warnings)
    {
        var name = instance.Name;
        var doc = new IniDocument();
        var origin = new IniOrigin(OsctlLayer, 0);
        void Set(string section, string key, string value) => doc.Set(section, key, value, origin);
        string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

        var basePort = BasePort(regions);
        var databaseName = DatabaseName(name);
        var estateDatabase = EstateDatabaseName(grid);

        Set("Const", "InstanceName", name);
        Set("Const", "InstancePort", Num(basePort));
        Set("Const", "InstanceDBName", databaseName);
        Set("Const", "EstateDBName", estateDatabase);
        Set("Const", "InstanceDir", instances.Dir(name));
        Set("Const", "GridName", grid);

        // On osctl's own grid: its public host and ports, and its private services on this machine.
        if (LocalGrid(set) is { Grid: var local })
        {
            Set("Const", "BaseHostname", local.Host);
            Set("Const", "PublicPort", Num(local.PublicPort));
            Set("Const", "PrivURL", "http://127.0.0.1");
            Set("Const", "PrivatePort", Num(local.PrivatePort));
        }

        var log = instances.LogDir(name);
        Set("Startup", "PIDFile", instances.PidFile(name));
        Set("Startup", "ConsoleHistoryFileEnabled", "true");
        Set("Startup", "ConsoleHistoryFile", Path.Combine(log, "OpenSimConsoleHistory.txt"));
        Set("Startup", "crash_dir", Path.Combine(log, "crashes"));
        Set("Startup", "regionload_regionsdir", Path.Combine(instances.GeneratedDir(name), "Regions") + "/");
        Set("Startup", "startup_console_commands_file", instances.StartupCommandsFile(name));
        Set("Startup", "shutdown_console_commands_file", instances.ShutdownCommandsFile(name));
        Set("Startup", "RegistryLocation", instances.AddinsDir(name));
        Set("Startup", "ManagedStatsRemoteFetchURI", "ManagedStats");

        Set("YEngine", "ScriptEnginesPath", instances.ScriptEnginesDir(name));

        // Files OpenSim otherwise writes into its working directory, the release's bin/ that instances share.
        Set("Mesh", "MeshFileCachePath", Path.Combine(instances.Dir(name), "meshcache"));
        Set("MapImageService", "TilesStoragePath", Path.Combine(instances.DataDir(name), "maptiles"));
        Set("GridService", "MapTileDirectory", Path.Combine(instances.DataDir(name), "maptiles"));

        Set("Network", "http_listener_port", Num(basePort));
        Set("Network", "http_listener_sslport", Num(basePort + settings.Ports.SslPortOffset));

        Set("AssetCache", "CacheDirectory", Path.Combine(paths.Cache, "assets", grid));

        var estate = instance.SimEstate;
        Set("Estates", "DefaultEstateName", estate.Name);
        Set("Estates", "DefaultEstateOwnerName", estate.Owner);
        if (set.Mode == ConfigSetMode.Standalone)
        {
            if (estate.OwnerUuid is { } uuid)
                Set("Estates", "DefaultEstateOwnerUUID", uuid.ToString());
            Set("Estates", "DefaultEstateOwnerEMail", estate.OwnerEmail ?? "");
            var password = instances.OwnerPassword(name);
            if (string.IsNullOrEmpty(password))
                warnings.Add($"Instance '{name}' has no estate owner password ({instances.SecretsFile(name)}): OpenSim asks for one " +
                    $"if it has to make the estate, and the owner's IARs need it ('osctl backup iar-password {name} \"{estate.Owner}\"')");
            else
                Set("Estates", "DefaultEstateOwnerPassword", password);
        }

        // RemoteAdmin, for osctl's own shutdowns and broadcasts: on, answering localhost only.
        if (instances.RemoteAdminPassword(name) is { } adminPassword)
        {
            Set("RemoteAdmin", "enabled", "true");
            Set("RemoteAdmin", "port", "0");
            Set("RemoteAdmin", "access_password", adminPassword);
            Set("RemoteAdmin", "access_ip_addresses", "127.0.0.1");
        }
        else
        {
            warnings.Add($"Instance '{name}' has no RemoteAdmin password ({instances.SecretsFile(name)}); osctl cannot use RemoteAdmin");
        }

        if (instance.Database == DatabaseKind.MariaDb)
        {
            var db = settings.Database;
            if (string.IsNullOrEmpty(db.Password))
                warnings.Add($"No database password for user '{db.User}' in {paths.SecretsFile}; run 'osctl db init'");
            Set("DatabaseService", "StorageProvider", "OpenSim.Data.MySQL.dll");
            Set("DatabaseService", "ConnectionString", MySqlConnectionString(db, databaseName));
            Set("DatabaseService", "EstateConnectionString", MySqlConnectionString(db, estateDatabase));
        }
        else
        {
            // SQLite files are named relative to the working directory (bin/); put them in the instance's data/.
            var data = instances.DataDir(name);
            foreach (var section in loaded.Sections)
            {
                foreach (var entry in section.Entries.Where(e => e.Key is "ConnectionString" or "EstateConnectionString"))
                {
                    var rewritten = SqliteFilePattern().Replace(entry.Value,
                        m => Path.IsPathRooted(m.Groups[1].Value) ? m.Value : $"URI=file:{Path.Combine(data, m.Groups[1].Value)}");
                    if (rewritten != entry.Value)
                        Set(section.Name, entry.Key, rewritten);
                }
            }
        }

        return doc;
    }

    /// <summary>
    /// What osctl sets for a grid's Robust instance (PLAN.md "Robust and local grids"): the grid's host and ports,
    /// the files Robust otherwise writes into bin/, its database, the grid's name, and the default region's flags.
    /// </summary>
    private IniDocument ForcedRobust(InstanceDefinition instance, List<string> warnings)
    {
        var name = instance.Name;
        var grid = instance.GridSettings;
        var doc = new IniDocument();
        var origin = new IniOrigin(OsctlLayer, 0);
        void Set(string section, string key, string value) => doc.Set(section, key, value, origin);
        string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

        Set("Const", "BaseHostname", grid.Host);
        Set("Const", "PublicPort", Num(grid.PublicPort));
        Set("Const", "PrivatePort", Num(grid.PrivatePort));
        Set("Const", "InstanceName", name);
        Set("Const", "InstanceDir", instances.Dir(name));
        Set("Const", "GridName", name);

        var log = instances.LogDir(name);
        Set("Startup", "PIDFile", instances.PidFile(name));
        Set("Startup", "ConsoleHistoryFileEnabled", "true");
        Set("Startup", "ConsoleHistoryFile", Path.Combine(log, "RobustConsoleHistory.txt"));
        Set("Startup", "RegistryLocation", instances.AddinsDir(name));

        Set("MapImageService", "TilesStoragePath", Path.Combine(instances.DataDir(name), "maptiles"));
        Set("GridService", "MapTileDirectory", Path.Combine(instances.DataDir(name), "linkedmaptiles"));

        // FSAssets (the config set's choice): the files and their spool, which must be on the same file system.
        var assets = AssetsDir(instance);
        Set("AssetService", "BaseDirectory", Path.Combine(assets, "data"));
        Set("AssetService", "SpoolDirectory", Path.Combine(assets, "tmp"));

        Set("GridInfoService", "gridname", grid.Name);
        Set("GridInfoService", "gridnick", grid.Nick);

        if (grid.DefaultRegion is { } defaultRegion)
        {
            var region = GridRegions(name).FirstOrDefault(r => string.Equals(r.Name, defaultRegion, StringComparison.OrdinalIgnoreCase));
            if (region is null)
                warnings.Add($"Grid '{name}': its default region '{defaultRegion}' is not a region of any instance on it");
            else
                Set("GridService", $"Region_{region.Uuid}", DefaultRegionFlags);
        }
        else
        {
            warnings.Add($"Grid '{name}' has no default region, so nobody can log in for the first time; " +
                $"'osctl grid set {name} --default-region <region>'");
        }

        if (instance.Database != DatabaseKind.MariaDb)
            throw new OsctlException($"Grid '{name}' must use MariaDB: Robust's SQLite support is incomplete");
        var db = settings.Database;
        if (string.IsNullOrEmpty(db.Password))
            warnings.Add($"No database password for user '{db.User}' in {paths.SecretsFile}; run 'osctl db init'");
        Set("DatabaseService", "StorageProvider", "OpenSim.Data.MySQL.dll");
        Set("DatabaseService", "ConnectionString", MySqlConnectionString(db, DatabaseName(name)));
        return doc;
    }

    /// <summary>Where a grid's FSAssets keeps its files: [Grid] AssetsDir, or the instance's data/fsassets.</summary>
    public string AssetsDir(InstanceDefinition robust) =>
        robust.GridSettings.AssetsDir ?? Path.Combine(instances.DataDir(robust.Name), "fsassets");

    /// <summary>The regions of every simulator instance on the local grid <paramref name="grid"/>.</summary>
    public IEnumerable<RegionDefinition> GridRegions(string grid) =>
        instances.Names()
            .Select(instances.Load)
            .Where(i => !i.IsRobust && configSets.Exists(i.ConfigSet) && configSets.Load(i.ConfigSet).Robust == grid)
            .SelectMany(i => instances.Regions(i.Name));

    /// <summary>
    /// Every region's effective settings (as written to generated/Regions/Regions.ini), each with the file it comes
    /// from. Unlike <see cref="Generate"/>, this needs no build.
    /// </summary>
    public IniDocument RegionSettings(string name) => RegionsConfig(name, instances.Regions(name));

    private IniDocument RegionsConfig(string name, List<RegionDefinition> regions)
    {
        var defaults = new[] { paths.RegionDefaultsFile, instances.RegionDefaultsFile(name) }
            .Where(File.Exists)
            .Select(f => IniParser.ParseFile(f)["Region Defaults"])
            .OfType<IniSection>()
            .SelectMany(d => d.Entries)
            .ToList();
        var modules = new IniDocument();
        foreach (var file in IniFiles(instances.RegionsDir(name)).Where(f => Path.GetFileName(f) != "Regions.ini"))
            modules.Merge(IniParser.ParseFile(file));
        // Read from the file itself (the regions were loaded from it) so that each value keeps its line.
        var regionsFile = instances.RegionsFile(name);
        var own = File.Exists(regionsFile) ? IniParser.ParseFile(regionsFile) : RegionDefinition.ToIni(regions);

        var doc = new IniDocument();
        foreach (var region in regions)
        {
            foreach (var entry in defaults)
                doc.Set(region.Name, entry.Key, entry.Value, entry.Origin);
            foreach (var entry in modules[region.Name]?.Entries ?? [])
                doc.Set(region.Name, entry.Key, entry.Value, entry.Origin);
            foreach (var entry in own[region.Name]!.Entries)
                doc.Set(region.Name, entry.Key, entry.Value, entry.Origin);
        }
        foreach (var unknown in modules.Sections.Where(s => regions.All(r => r.Name != s.Name)))
            throw new OsctlException($"{instances.RegionsDir(name)}: settings for '{unknown.Name}', which is not a region of instance '{name}'");
        return doc;
    }

    /// <summary>
    /// OpenSim's or Robust's log4net configuration (bin/OpenSim.exe.config, bin/Robust.exe.config) with every log
    /// file moved to the instance's log directory.
    /// </summary>
    private string? Log4Net(string name, string bin, string configFile, List<string> warnings)
    {
        var source = Path.Combine(bin, configFile);
        if (!File.Exists(source))
        {
            warnings.Add($"{source} not found: it will log with its built-in settings");
            return null;
        }

        var xml = new XmlDocument { PreserveWhitespace = true };
        xml.Load(source);
        var log = instances.LogDir(name);
        foreach (XmlElement file in xml.SelectNodes("//appender/file")!)
        {
            var value = file.GetAttribute("value");
            if (value.Length > 0)
                file.SetAttribute("value", Path.Combine(log, Path.GetFileName(value)));
        }
        // A StringWriter would declare the XML as UTF-16; the file is written as UTF-8, which log4net checks.
        using var writer = new Utf8StringWriter();
        xml.Save(writer);
        return writer.ToString();
    }

    public string DatabaseName(string instance) => settings.Database.DatabasePrefix + instance;

    public string EstateDatabaseName(string grid) => settings.Database.DatabasePrefix + "estate_" + grid;

    private static string MySqlConnectionString(DatabaseSettings db, string database) =>
        $"Data Source={db.Host};Port={db.Port.ToString(CultureInfo.InvariantCulture)};Database={database};" +
        $"User ID={db.User};Password={db.Password ?? ""};Old Guids=true;SslMode=None;";

    private static bool IsInclude(string key) => key.StartsWith("Include-", StringComparison.Ordinal);

    private static IEnumerable<string> IniFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ini").Order(StringComparer.Ordinal) : [];

    [GeneratedRegex(@"URI=file:([^;]+)")]
    private static partial Regex SqliteFilePattern();
}

/// <summary>A StringWriter whose XML declaration says UTF-8.</summary>
internal sealed class Utf8StringWriter : StringWriter
{
    public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
