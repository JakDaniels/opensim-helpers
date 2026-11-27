// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Running;
using Osctl.Settings;
using Osctl.Util;

namespace Osctl.Instances;

/// <summary>
/// Moves instances made by earlier versions of osctl, which kept their definition in
/// /var/lib/osctl/instances/&lt;name&gt;/, to /etc/osctl/instances/&lt;name&gt;/ (PLAN.md "Layout on disk"). Only stopped
/// instances are moved: a running one reads its start-up and shutdown commands from where it started with, so it
/// is moved by the first osctl command after it stops. Until then osctl reads it where it is.
/// </summary>
public static class InstanceLayout
{
    /// <summary>The definition's files and directories, relative to the instance's directory.</summary>
    private static readonly string[] Entries =
        ["instance.ini", "secrets.ini", "overrides", "regions", "region-defaults.ini", "startup_commands.txt", "shutdown_commands.txt"];

    /// <summary>Moves every stopped instance in the old layout. Returns a line for each moved, or left for now.</summary>
    public static List<string> Migrate(OsctlPaths paths)
    {
        var report = new List<string>();
        if (!Directory.Exists(paths.InstancesDir))
            return report;
        var store = new InstanceStore(paths);
        foreach (var dir in Directory.GetDirectories(paths.InstancesDir).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (!Names.IsValidInstance(name) || !store.IsOldLayout(name))
                continue;
            if (RunState.Load(store.RunStateFile(name)) is { IsRunnerAlive: true })
            {
                report.Add($"instance '{name}' is running: its definition moves to {paths.InstanceConfigDir(name)} once it stops");
                continue;
            }
            Move(paths, name);
            report.Add($"instance '{name}': its definition (instance.ini, regions, overrides, secrets, commands) moved to {paths.InstanceConfigDir(name)}");
        }
        return report;
    }

    /// <summary>
    /// Copies the definition to a temporary directory next to its new place, renames that into place (from then on
    /// osctl reads it there), then removes the old files. /etc and /var may be different file systems.
    /// </summary>
    private static void Move(OsctlPaths paths, string name)
    {
        var from = paths.InstanceDir(name);
        var to = paths.InstanceConfigDir(name);
        var staging = Path.Combine(paths.InstanceConfigsDir, $".{name}.moving");
        FileTree.Delete(staging);
        Directory.CreateDirectory(staging);
        foreach (var entry in Entries)
        {
            var source = Path.Combine(from, entry);
            if (Directory.Exists(source))
                FileTree.Copy(source, Path.Combine(staging, entry));
            else if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(staging, entry));
                File.SetUnixFileMode(Path.Combine(staging, entry), File.GetUnixFileMode(source));
            }
        }
        if (!Directory.Exists(to))
            Directory.Move(staging, to);
        else
        {
            // A directory without instance.ini (made by hand, say): add to it, instance.ini last.
            FileTree.Copy(staging, to, new HashSet<string> { "instance.ini" });
            File.Copy(Path.Combine(staging, "instance.ini"), Path.Combine(to, "instance.ini"));
            FileTree.Delete(staging);
        }
        foreach (var entry in Entries)
        {
            var source = Path.Combine(from, entry);
            if (Directory.Exists(source))
                FileTree.Delete(source);
            else
                File.Delete(source);
        }
    }
}
