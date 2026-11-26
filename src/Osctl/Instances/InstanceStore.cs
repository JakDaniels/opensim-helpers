// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Instances;

/// <summary>
/// The instances on disk (PLAN.md "Layout on disk"):
/// <code>
/// /var/lib/osctl/instances/&lt;name&gt;/
///     instance.ini, secrets.ini (0640: the estate owner's password)
///     overrides/*.ini                  instance overrides
///     regions/Regions.ini              the regions; other regions/*.ini = per-region module settings
///     startup_commands.txt, shutdown_commands.txt
///     generated/                       OpenSim.ini, empty.ini, Regions/Regions.ini, log4net.config (rebuilt each start)
///     scriptengines/, data/ (SQLite), addins/ (Mono.Addins registry), backups/
/// /var/log/osctl/&lt;name&gt;/           OpenSim.log, console history
/// </code>
/// </summary>
public sealed class InstanceStore(OsctlPaths paths)
{
    public string Dir(string name) => paths.InstanceDir(name);

    public string DefinitionFile(string name) => Path.Combine(Dir(name), "instance.ini");

    public string SecretsFile(string name) => Path.Combine(Dir(name), "secrets.ini");

    public string OverridesDir(string name) => Path.Combine(Dir(name), "overrides");

    public string RegionsDir(string name) => Path.Combine(Dir(name), "regions");

    public string RegionsFile(string name) => Path.Combine(RegionsDir(name), "Regions.ini");

    public string GeneratedDir(string name) => Path.Combine(Dir(name), "generated");

    public string ScriptEnginesDir(string name) => Path.Combine(Dir(name), "scriptengines");

    public string DataDir(string name) => Path.Combine(Dir(name), "data");

    public string AddinsDir(string name) => Path.Combine(Dir(name), "addins");

    public string BackupsDir(string name) => Path.Combine(Dir(name), "backups");

    public string StartupCommandsFile(string name) => Path.Combine(Dir(name), "startup_commands.txt");

    public string ShutdownCommandsFile(string name) => Path.Combine(Dir(name), "shutdown_commands.txt");

    public string LogDir(string name) => Path.Combine(paths.Log, name);

    public string PidFile(string name) => Path.Combine(paths.Run, name + ".pid");

    /// <summary>
    /// The instance's tmux socket: /run/osctl/&lt;name&gt;.sock. A unix socket path must fit in 107 bytes, so under a
    /// long development root it goes to a short directory in /tmp instead, named after the root.
    /// </summary>
    public string TmuxSocket(string name)
    {
        var socket = Path.Combine(paths.Run, name + ".sock");
        if (socket.Length <= 100)
            return socket;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.Root)))[..12];
        return Path.Combine(Path.GetTempPath(), $"osctl-{hash}", name + ".sock");
    }

    public string ConsoleLog(string name) => Path.Combine(LogDir(name), "console.log");

    /// <summary>Written by 'osctl run' while the instance runs: the runner's PID, the release, and when it became ready.</summary>
    public string RunStateFile(string name) => Path.Combine(paths.Run, name + ".state");

    public bool Exists(string name) => Osctl.Names.IsValidInstance(name) && File.Exists(DefinitionFile(name));

    public IReadOnlyList<string> Names() =>
        Directory.Exists(paths.InstancesDir)
            ? Directory.GetDirectories(paths.InstancesDir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => Osctl.Names.IsValidInstance(n) && File.Exists(DefinitionFile(n)))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    public InstanceDefinition Load(string name)
    {
        Osctl.Names.CheckInstance(name);
        if (!File.Exists(DefinitionFile(name)))
            throw new OsctlException($"There is no instance '{name}'; 'osctl instance list' shows them");
        return InstanceDefinition.Load(DefinitionFile(name), name);
    }

    public void Save(InstanceDefinition instance) =>
        IniWriter.WriteFile(DefinitionFile(instance.Name), instance.ToIni(),
            [$"osctl instance '{instance.Name}'. Change it with 'osctl instance …' or by hand while it is stopped."]);

    public List<RegionDefinition> Regions(string name) => RegionDefinition.LoadAll(RegionsFile(name));

    public void SaveRegions(string name, IEnumerable<RegionDefinition> regions) =>
        IniWriter.WriteFile(RegionsFile(name), RegionDefinition.ToIni(regions),
            ["The instance's regions, written by osctl ('osctl region …'). Settings for one region can also go in",
             "other .ini files in this directory, under a [Region Name] section."]);

    public string? OwnerPassword(string name) => Secret(name, "Estate", "OwnerPassword");

    /// <summary>The password RemoteAdmin (localhost only) wants; osctl uses RemoteAdmin to shut down and broadcast.</summary>
    public string? RemoteAdminPassword(string name) => Secret(name, "RemoteAdmin", "Password");

    /// <summary>
    /// The password an IAR of <paramref name="user"/> needs (OpenSim checks it): secrets.ini [IAR] "First Last",
    /// or for the estate owner of a standalone, the owner's password.
    /// </summary>
    public string? IarPassword(string name, string user) =>
        Secret(name, "IAR", user) ?? (Load(name).Estate?.Owner == user ? OwnerPassword(name) : null);

    /// <summary>Stores the password for an IAR of <paramref name="user"/> in the instance's secrets.ini.</summary>
    public void SetIarPassword(string name, string user, string password) =>
        IniFileEditor.SetValue(SecretsFile(name), "IAR", user, password);

    private string? Secret(string name, string section, string key) =>
        File.Exists(SecretsFile(name)) && IniParser.ParseFile(SecretsFile(name)).Get(section, key) is { Length: > 0 } value ? value : null;

    /// <summary>Creates the instance's directories and files, with its secrets readable by the group only.</summary>
    public void Create(InstanceDefinition instance, string? ownerPassword, string remoteAdminPassword)
    {
        var name = instance.Name;
        foreach (var dir in new[] { Dir(name), OverridesDir(name), RegionsDir(name), GeneratedDir(name), ScriptEnginesDir(name),
                     DataDir(name), AddinsDir(name), BackupsDir(name) })
            Directory.CreateDirectory(dir);

        Save(instance);
        SaveRegions(name, []);
        File.WriteAllText(Path.Combine(OverridesDir(name), "README.txt"),
            "Put .ini files here to override OpenSim settings for this instance only. They are read in alphabetical\n" +
            "order after the global overrides in /etc/osctl/overrides, and later files win.\n");
        foreach (var file in new[] { StartupCommandsFile(name), ShutdownCommandsFile(name) })
            File.WriteAllText(file, "; Console commands, one per line, run at start-up or shutdown. Lines starting with ; are comments.\n");

        WriteSecrets(name, ownerPassword, remoteAdminPassword);
    }

    /// <summary>Writes a new secrets.ini (there must be none), readable by the group only.</summary>
    public void WriteSecrets(string name, string? ownerPassword, string remoteAdminPassword)
    {
        var secrets = new IniDocument();
        secrets.Set("Estate", "OwnerPassword", ownerPassword ?? "");
        secrets.Set("RemoteAdmin", "Password", remoteAdminPassword);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
        };
        using var stream = new FileStream(SecretsFile(name), options);
        using var writer = new StreamWriter(stream);
        IniWriter.Write(writer, secrets,
            ["The estate owner's password, used when OpenSim creates the account on a standalone's first start,",
             "and the RemoteAdmin password osctl uses (RemoteAdmin answers localhost only)."]);
    }

    public void Delete(string name)
    {
        FileTree.Delete(Dir(name));
        FileTree.Delete(LogDir(name));
    }
}
