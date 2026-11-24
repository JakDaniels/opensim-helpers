// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Osctl.Operations;

/// <summary>One arrival in a region: by logging in (here or over the hypergrid), or by teleport or crossing.</summary>
public sealed record Visit(DateTime Time, string Instance, string Region, string Name, string How, string? Ip);

/// <summary>
/// <c>osctl visitors</c> (PLAN.md "What MIOS did"): arrivals read from an instance's OpenSim.log and its rotated
/// copies (OpenSim.log.*, gzipped or not). The lines, as OpenSim writes them today:
/// <code>
/// INFO  [LLOGIN SERVICE]: Login request for First Last at last using viewer …, IP 192.0.2.7, Mac …
/// DEBUG [SCENE]: Incoming client First Last in region Welcome via regular login. …   (or "via HG login")
/// INFO  [SCENE PRESENCE]: Complete movement of First Last into Welcome &lt;128, 128, 25&gt;
/// </code>
/// "Complete movement" marks every arrival; an "Incoming client" line before it says it was a login.
/// </summary>
public static partial class Visitors
{
    public const string Login = "login";
    public const string HgLogin = "hg-login";
    public const string Arrival = "arrival";

    /// <summary>The log files to read, oldest first: rotated copies by time, then OpenSim.log.</summary>
    public static IReadOnlyList<string> LogFiles(string logDir)
    {
        if (!Directory.Exists(logDir))
            return [];
        // (A pattern ending in ".*" also matches the name without it, so the prefix is checked again.)
        var rotated = Directory.GetFiles(logDir, "OpenSim.log.*")
            .Where(f => Path.GetFileName(f).StartsWith("OpenSim.log.", StringComparison.Ordinal))
            .OrderBy(File.GetLastWriteTimeUtc).ThenBy(f => f, StringComparer.Ordinal);
        var current = Path.Combine(logDir, "OpenSim.log");
        return [.. rotated, .. File.Exists(current) ? [current] : Array.Empty<string>()];
    }

    public static IEnumerable<Visit> Read(string instance, IEnumerable<string> files)
    {
        var logins = new Dictionary<string, (string Region, string How)>(StringComparer.Ordinal);
        var ips = new Dictionary<string, (DateTime Time, string Ip)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var line in Lines(file))
            {
                var stamp = Stamp().Match(line);
                if (!stamp.Success)
                    continue;
                var time = DateTime.ParseExact(stamp.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                if (LoginRequest().Match(line) is { Success: true } request)
                {
                    ips[request.Groups[1].Value] = (time, request.Groups[2].Value);
                }
                else if (Incoming().Match(line) is { Success: true } incoming)
                {
                    logins[incoming.Groups[1].Value] = (incoming.Groups[2].Value, incoming.Groups[3].Value == "HG" ? HgLogin : Login);
                }
                else if (Movement().Match(line) is { Success: true } movement)
                {
                    var name = movement.Groups[1].Value;
                    var region = movement.Groups[2].Value;
                    var how = logins.Remove(name, out var login) && login.Region == region ? login.How : Arrival;
                    string? ip = null;
                    if (how == Login && ips.Remove(name, out var seen) && time - seen.Time < TimeSpan.FromMinutes(5))
                        ip = seen.Ip;
                    yield return new Visit(time, instance, region, name, how, ip);
                }
            }
        }
    }

    private static IEnumerable<string> Lines(string file)
    {
        using var stream = File.Open(file, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite });
        using var reader = file.EndsWith(".gz", StringComparison.Ordinal)
            ? new StreamReader(new GZipStream(stream, CompressionMode.Decompress))
            : new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    [GeneratedRegex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"\[LLOGIN SERVICE\]: Login request for (\S+ \S+) at .*, IP ([0-9A-Fa-f.:]+),")]
    private static partial Regex LoginRequest();

    [GeneratedRegex(@"\[SCENE\]: Incoming client (.+) in region (.+) via (regular|HG) login")]
    private static partial Regex Incoming();

    [GeneratedRegex(@"\[SCENE PRESENCE\]: Complete movement of (.+) into (.+?) <[^>]*>\s*$")]
    private static partial Regex Movement();
}
