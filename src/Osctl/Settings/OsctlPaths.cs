// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Settings;

/// <summary>
/// Where osctl keeps things (PLAN.md "Layout on disk"). Everything hangs off a root, normally <c>/</c>; tests and
/// development use another root (<c>--root</c> or <c>OSCTL_ROOT</c>) so nothing touches the real system.
/// </summary>
public sealed class OsctlPaths
{
    public const string RootEnvironmentVariable = "OSCTL_ROOT";

    public OsctlPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Etc = Under("etc/osctl");
        Lib = Under("var/lib/osctl");
        Cache = Under("var/cache/osctl");
        Log = Under("var/log/osctl");
        Run = Under("run/osctl");
    }

    public string Root { get; }

    /// <summary>/etc/osctl: osctl.ini, secrets.ini, configsets/, overrides/, builds/, region-defaults.ini, instances/.</summary>
    public string Etc { get; }

    /// <summary>/var/lib/osctl: builds/, configsets/ (cached downloads), instances/ (what instances make: data, backups, …).</summary>
    public string Lib { get; }

    /// <summary>/var/cache/osctl: asset caches per grid.</summary>
    public string Cache { get; }

    /// <summary>/var/log/osctl: one directory per instance.</summary>
    public string Log { get; }

    /// <summary>/run/osctl: tmux sockets and PID files.</summary>
    public string Run { get; }

    public string SettingsFile => Path.Combine(Etc, "osctl.ini");

    public string SecretsFile => Path.Combine(Etc, "secrets.ini");

    public string ConfigSetsDir => Path.Combine(Etc, "configsets");

    public string GlobalOverridesDir => Path.Combine(Etc, "overrides");

    public string BuildDefinitionsDir => Path.Combine(Etc, "builds");

    public string RegionDefaultsFile => Path.Combine(Etc, "region-defaults.ini");

    public string BuildsDir => Path.Combine(Lib, "builds");

    public string InstancesDir => Path.Combine(Lib, "instances");

    /// <summary>/var/lib/osctl/instances/&lt;name&gt;: what the instance makes (generated config, data, backups).</summary>
    public string InstanceDir(string name) => Path.Combine(InstancesDir, name);

    /// <summary>/etc/osctl/instances: each instance's definition.</summary>
    public string InstanceConfigsDir => Path.Combine(Etc, "instances");

    /// <summary>/etc/osctl/instances/&lt;name&gt;: the instance's definition (instance.ini, regions, overrides, …).</summary>
    public string InstanceConfigDir(string name) => Path.Combine(InstanceConfigsDir, name);

    /// <summary>The root from the command line, else OSCTL_ROOT, else <c>/</c>.</summary>
    public static OsctlPaths Resolve(string? rootOption)
    {
        if (!string.IsNullOrEmpty(rootOption))
            return new OsctlPaths(rootOption);
        var fromEnvironment = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        return new OsctlPaths(string.IsNullOrEmpty(fromEnvironment) ? "/" : fromEnvironment);
    }

    private string Under(string relative) => Path.Combine(Root, relative);
}
