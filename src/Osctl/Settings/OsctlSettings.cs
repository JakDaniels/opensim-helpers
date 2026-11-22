// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;

namespace Osctl.Settings;

/// <summary>A setting with a bad value; the message names the file and line.</summary>
public sealed class SettingsException(string message) : Exception(message);

/// <summary>Region and instance port allocation (MIOS: INSTANCE_MIN_PORT/INSTANCE_MAX_PORT, SSL_PORT_OFFSET).</summary>
public sealed record PortSettings(int RegionPortMin = 9000, int RegionPortMax = 9099, int SslPortOffset = 500);

/// <summary>How long starting and stopping may take (MIOS: TIMEOUT, TMUX_START_TIMEOUT, TMUX_STOP_TIMEOUT).</summary>
public sealed record TimeoutSettings(int StartSeconds = 180, int StopSeconds = 90);

/// <summary>
/// Crash restarts, given to systemd (MIOS: MAX_RESTART_COUNT, MAX_RESTART_TIME_INTERVAL): more than
/// <see cref="MaxRestarts"/> starts within <see cref="IntervalSeconds"/> and the instance is "broken".
/// </summary>
public sealed record RestartSettings(int MaxRestarts = 3, int IntervalSeconds = 300, int DelaySeconds = 10);

/// <summary>The tmux console (MIOS: TMUX_CONTROL_PREFIX, TMUX_SCROLL_BUFFER).</summary>
public sealed record TmuxSettings(string Prefix = "C-a", int HistoryLimit = 10000);

/// <summary>
/// MariaDB/MySQL (MIOS: the $mysql and $opensim arrays, INSTANCE_DB_PREFIX). The passwords come from
/// secrets.ini, never from osctl.ini.
/// </summary>
public sealed record DatabaseSettings(
    string Host = "localhost",
    int Port = 3306,
    string AdminUser = "osctl_admin",
    string User = "opensim",
    string DatabasePrefix = "opensim_",
    string? AdminPassword = null,
    string? Password = null);

/// <summary>What new instances get when the command line does not say (MIOS: DEFAULT_CONFIG_SET).</summary>
public sealed record DefaultSettings(string ConfigSet = "standalone", string? Build = null);

/// <summary>The system account and group that run the instances.</summary>
public sealed record AccountSettings(string User = "opensim", string Group = "opensim");

/// <summary>osctl's own settings: /etc/osctl/osctl.ini plus /etc/osctl/secrets.ini.</summary>
public sealed record OsctlSettings(
    PortSettings Ports,
    TimeoutSettings Timeouts,
    RestartSettings Restart,
    TmuxSettings Tmux,
    DatabaseSettings Database,
    DefaultSettings Defaults,
    AccountSettings Account)
{
    public static OsctlSettings Default { get; } = new(new(), new(), new(), new(), new(), new(), new());
}

/// <summary>The settings, plus what was odd about the files (unknown keys, missing files).</summary>
public sealed record SettingsLoadResult(OsctlSettings Settings, bool SettingsFileFound, IReadOnlyList<string> Warnings);

public static class SettingsLoader
{
    public static SettingsLoadResult Load(OsctlPaths paths)
    {
        var warnings = new List<string>();
        var found = File.Exists(paths.SettingsFile);
        var settingsDoc = found ? IniParser.ParseFile(paths.SettingsFile) : new IniDocument();
        var secretsDoc = File.Exists(paths.SecretsFile) ? IniParser.ParseFile(paths.SecretsFile) : new IniDocument();

        var s = new Reader(settingsDoc, warnings);
        var d = OsctlSettings.Default;

        var ports = new PortSettings(
            s.Int("Ports", "RegionPortMin", d.Ports.RegionPortMin, 1, 65535),
            s.Int("Ports", "RegionPortMax", d.Ports.RegionPortMax, 1, 65535),
            s.Int("Ports", "SslPortOffset", d.Ports.SslPortOffset, 0, 65535));
        if (ports.RegionPortMax < ports.RegionPortMin)
            throw new SettingsException(
                $"{paths.SettingsFile}: [Ports] RegionPortMax ({ports.RegionPortMax}) is below RegionPortMin ({ports.RegionPortMin})");

        var timeouts = new TimeoutSettings(
            s.Int("Timeouts", "StartSeconds", d.Timeouts.StartSeconds, 1, 3600),
            s.Int("Timeouts", "StopSeconds", d.Timeouts.StopSeconds, 1, 3600));

        var restart = new RestartSettings(
            s.Int("Restart", "MaxRestarts", d.Restart.MaxRestarts, 0, 100),
            s.Int("Restart", "IntervalSeconds", d.Restart.IntervalSeconds, 1, 86400),
            s.Int("Restart", "DelaySeconds", d.Restart.DelaySeconds, 0, 3600));

        var tmux = new TmuxSettings(
            s.String("Tmux", "Prefix", d.Tmux.Prefix),
            s.Int("Tmux", "HistoryLimit", d.Tmux.HistoryLimit, 0, 10_000_000));

        var secrets = new Reader(secretsDoc, warnings);
        var database = new DatabaseSettings(
            s.String("Database", "Host", d.Database.Host),
            s.Int("Database", "Port", d.Database.Port, 1, 65535),
            s.String("Database", "AdminUser", d.Database.AdminUser),
            s.String("Database", "User", d.Database.User),
            s.String("Database", "DatabasePrefix", d.Database.DatabasePrefix),
            secrets.OptionalString("Database", "AdminPassword"),
            secrets.OptionalString("Database", "Password"));

        var defaults = new DefaultSettings(
            s.String("Defaults", "ConfigSet", d.Defaults.ConfigSet),
            s.OptionalString("Defaults", "Build"));

        var account = new AccountSettings(
            s.String("Account", "User", d.Account.User),
            s.String("Account", "Group", d.Account.Group));

        if (settingsDoc.Get("Database", "AdminPassword") is not null || settingsDoc.Get("Database", "Password") is not null)
            warnings.Add($"{paths.SettingsFile}: database passwords belong in {paths.SecretsFile}; they are ignored here");

        s.WarnAboutUnknownKeys(paths.SettingsFile);
        secrets.WarnAboutUnknownKeys(paths.SecretsFile);

        return new SettingsLoadResult(new OsctlSettings(ports, timeouts, restart, tmux, database, defaults, account), found, warnings);
    }

    /// <summary>Reads typed values and remembers which keys were used, so typos can be reported.</summary>
    private sealed class Reader(IniDocument doc, List<string> warnings)
    {
        private readonly HashSet<(string, string)> used = [];

        public string String(string section, string key, string fallback) =>
            OptionalString(section, key) ?? fallback;

        public string? OptionalString(string section, string key)
        {
            used.Add((section, key));
            var value = doc.Get(section, key);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public int Int(string section, string key, int fallback, int min, int max)
        {
            used.Add((section, key));
            var entry = doc[section]?.GetEntry(key);
            if (entry is null || entry.Value.Length == 0)
                return fallback;
            if (!int.TryParse(entry.Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                throw new SettingsException(
                    $"{entry.Origin}: [{section}] {key} = {entry.Value}: expected a whole number from {min} to {max}");
            return value;
        }

        public void WarnAboutUnknownKeys(string file)
        {
            foreach (var section in doc.Sections)
            {
                foreach (var entry in section.Entries)
                {
                    if (!used.Contains((section.Name, entry.Key)))
                        warnings.Add($"{entry.Origin?.ToString() ?? file}: unknown setting [{section.Name}] {entry.Key}");
                }
            }
        }
    }
}
