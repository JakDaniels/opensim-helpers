// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Osctl.Operations;

/// <summary>Durations on the command line: <c>90s</c>, <c>10m</c>, <c>2h</c>, <c>7d</c>; a bare number is minutes.</summary>
public static partial class Durations
{
    public static TimeSpan Parse(string text)
    {
        var m = Pattern().Match(text.Trim());
        if (!m.Success)
            throw new OsctlException($"'{text}' is not a duration: expected e.g. 90s, 10m, 2h or 7d");
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            _ => TimeSpan.FromMinutes(n),
        };
    }

    /// <summary>"10 minutes", "1 minute", "30 seconds".</summary>
    public static string Words(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.Seconds == 0
            ? Plural((int)span.TotalMinutes, "minute")
            : Plural((int)Math.Ceiling(span.TotalSeconds), "second");

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

    [GeneratedRegex(@"^(\d{1,6})\s*([smhd]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
