// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Osctl.Ini;
using Osctl.Processes;
using Osctl.Settings;

namespace Osctl.Doctor;

public enum CheckStatus
{
    Ok,
    Warning,
    Failed,
}

/// <summary>One check's outcome. <see cref="Hint"/>: what to do about a warning or failure.</summary>
public sealed record CheckResult(string Name, CheckStatus Status, string Detail, string? Hint = null);

/// <summary>
/// <c>osctl doctor</c>: checks that the machine has what osctl and OpenSim need. Failures are things osctl
/// cannot work without; warnings are things only some features need.
/// </summary>
public sealed partial class Doctor(IProcessRunner runner, OsctlPaths paths)
{
    /// <summary>tmux 3.3 added <c>server-access</c>, which the group-shared consoles use.</summary>
    public static readonly Version MinimumTmux = new(3, 3);

    /// <summary>OpenSim targets net8.0 (runprebuild.sh) and rolls forward only within 8.x.</summary>
    public const int OpenSimRuntimeMajor = 8;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();
        var settings = CheckSettings(results);
        results.Add(await CheckGit(cancellationToken));
        results.Add(await CheckTmux(cancellationToken));
        results.Add(await CheckDotnetSdk(cancellationToken));
        results.Add(await CheckDotnetRuntime(cancellationToken));
        results.Add(await CheckLibgdiplus(cancellationToken));
        results.Add(await CheckMariaDbClient(cancellationToken));
        results.Add(await CheckSystemd(cancellationToken));
        results.Add(await CheckAccount(settings.Account, cancellationToken));
        return results;
    }

    private OsctlSettings CheckSettings(List<CheckResult> results)
    {
        SettingsLoadResult loaded;
        try
        {
            loaded = SettingsLoader.Load(paths);
        }
        catch (Exception e) when (e is IniParseException or SettingsException or IOException or UnauthorizedAccessException)
        {
            results.Add(new("settings", CheckStatus.Failed, e.Message, $"Fix {paths.SettingsFile}"));
            return OsctlSettings.Default;
        }

        if (!loaded.SettingsFileFound)
            results.Add(new("settings", CheckStatus.Warning, $"{paths.SettingsFile} not found; defaults in use",
                "The osctl package installs it; for a development root, create it or leave the defaults"));
        else if (loaded.Warnings.Count > 0)
            results.Add(new("settings", CheckStatus.Warning, string.Join("; ", loaded.Warnings)));
        else
            results.Add(new("settings", CheckStatus.Ok, paths.SettingsFile));
        return loaded.Settings;
    }

    private async Task<CheckResult> CheckGit(CancellationToken ct)
    {
        var r = await runner.RunAsync("git", ["--version"], ct);
        return r.Succeeded
            ? new("git", CheckStatus.Ok, FirstLine(r.StandardOutput))
            : new("git", CheckStatus.Failed, "git not found", "sudo apt install git");
    }

    private async Task<CheckResult> CheckTmux(CancellationToken ct)
    {
        var r = await runner.RunAsync("tmux", ["-V"], ct);
        if (!r.Succeeded)
            return new("tmux", CheckStatus.Failed, "tmux not found", "sudo apt install tmux");
        var version = ParseVersion(r.StandardOutput);
        if (version is null)
            return new("tmux", CheckStatus.Warning, $"could not read the version from '{FirstLine(r.StandardOutput)}'");
        return version >= MinimumTmux
            ? new("tmux", CheckStatus.Ok, FirstLine(r.StandardOutput))
            : new("tmux", CheckStatus.Failed, $"{FirstLine(r.StandardOutput)} is too old; {MinimumTmux} or later is needed");
    }

    private async Task<CheckResult> CheckDotnetSdk(CancellationToken ct)
    {
        var r = await runner.RunAsync("dotnet", ["--list-sdks"], ct);
        if (r.NotFound)
            return new(".NET SDK", CheckStatus.Failed, "dotnet not found", $"sudo apt install dotnet-sdk-{OpenSimRuntimeMajor}.0");
        var sdks = Lines(r.StandardOutput).Select(l => l.Split(' ')[0]).ToList();
        var usable = sdks.Where(v => ParseVersion(v)?.Major >= OpenSimRuntimeMajor).ToList();
        return usable.Count > 0
            ? new(".NET SDK", CheckStatus.Ok, string.Join(", ", usable))
            : new(".NET SDK", CheckStatus.Failed, $"no .NET SDK {OpenSimRuntimeMajor} or later (needed to build OpenSim)",
                $"sudo apt install dotnet-sdk-{OpenSimRuntimeMajor}.0");
    }

    private async Task<CheckResult> CheckDotnetRuntime(CancellationToken ct)
    {
        const string name = ".NET 8 runtime";
        var r = await runner.RunAsync("dotnet", ["--list-runtimes"], ct);
        if (r.NotFound)
            return new(name, CheckStatus.Failed, "dotnet not found", $"sudo apt install dotnet-runtime-{OpenSimRuntimeMajor}.0");
        var versions = Lines(r.StandardOutput)
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 2 && p[0] == "Microsoft.NETCore.App" && ParseVersion(p[1])?.Major == OpenSimRuntimeMajor)
            .Select(p => p[1])
            .ToList();
        return versions.Count > 0
            ? new(name, CheckStatus.Ok, string.Join(", ", versions))
            : new(name, CheckStatus.Failed, $"Microsoft.NETCore.App {OpenSimRuntimeMajor}.x not installed (OpenSim runs on it)",
                $"sudo apt install dotnet-runtime-{OpenSimRuntimeMajor}.0");
    }

    private async Task<CheckResult> CheckLibgdiplus(CancellationToken ct)
    {
        var r = await runner.RunAsync("ldconfig", ["-p"], ct);
        if (!r.Succeeded)
            return new("libgdiplus", CheckStatus.Warning, "could not run ldconfig -p to look for it");
        return r.StandardOutput.Contains("libgdiplus.so", StringComparison.Ordinal)
            ? new("libgdiplus", CheckStatus.Ok, "installed")
            : new("libgdiplus", CheckStatus.Warning, "not installed; OpenSim needs it for map tiles and textures",
                "sudo apt install libgdiplus");
    }

    private async Task<CheckResult> CheckMariaDbClient(CancellationToken ct)
    {
        var r = await runner.RunAsync("mariadb", ["--version"], ct);
        if (r.NotFound)
            r = await runner.RunAsync("mysql", ["--version"], ct);
        return r.Succeeded
            ? new("MariaDB client", CheckStatus.Ok, FirstLine(r.StandardOutput))
            : new("MariaDB client", CheckStatus.Warning, "not found; needed only for MariaDB instances",
                "sudo apt install mariadb-client (and mariadb-server for a local database)");
    }

    private async Task<CheckResult> CheckSystemd(CancellationToken ct)
    {
        var r = await runner.RunAsync("systemctl", ["--version"], ct);
        return r.Succeeded
            ? new("systemd", CheckStatus.Ok, FirstLine(r.StandardOutput))
            : new("systemd", CheckStatus.Warning, "systemctl not found; instances can only run in the foreground");
    }

    private async Task<CheckResult> CheckAccount(AccountSettings account, CancellationToken ct)
    {
        const string name = "account";
        var user = await runner.RunAsync("getent", ["passwd", account.User], ct);
        if (!user.Succeeded)
            return new(name, CheckStatus.Warning, $"user '{account.User}' does not exist",
                "The osctl package creates it");

        var groups = await runner.RunAsync("id", ["-nG"], ct);
        var mine = groups.Succeeded
            ? groups.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        if (Environment.UserName == "root" || Environment.UserName == account.User || mine.Contains(account.Group))
            return new(name, CheckStatus.Ok, $"user '{account.User}' exists; you can manage instances");
        return new(name, CheckStatus.Warning, $"you are not in group '{account.Group}'",
            $"sudo usermod -aG {account.Group} {Environment.UserName} (then log in again)");
    }

    /// <summary>The first dotted version number in a text ("tmux 3.3a" → 3.3, "tmux next-3.4" → 3.4).</summary>
    public static Version? ParseVersion(string text)
    {
        var match = VersionPattern().Match(text);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static string FirstLine(string text) => Lines(text).FirstOrDefault() ?? "";

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionPattern();
}
