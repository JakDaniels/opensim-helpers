// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Osctl;

/// <summary>
/// Names osctl uses as directory, unit, tmux and database names: letters, digits, <c>_</c>, <c>-</c> and
/// <c>.</c>, starting with a letter or digit, at most 64 characters (PLAN.md "Layout on disk").
/// </summary>
public static partial class Names
{
    public static bool IsValid(string name) => Pattern().IsMatch(name);

    /// <summary>Throws an <see cref="OsctlException"/> naming <paramref name="what"/> if the name is not valid.</summary>
    public static string Check(string name, string what)
    {
        if (!IsValid(name))
            throw new OsctlException(
                $"'{name}' is not a valid {what} name: use letters, digits, '_', '-' and '.', starting with a letter or digit (at most 64)");
        return name;
    }

    /// <summary>
    /// Instance names are stricter: no <c>.</c>, which tmux uses in its targets. They also name the instance's
    /// database, systemd unit and log directory.
    /// </summary>
    public static string CheckInstance(string name)
    {
        if (!InstancePattern().IsMatch(name))
            throw new OsctlException(
                $"'{name}' is not a valid instance name: use letters, digits, '_' and '-', starting with a letter or digit (at most 32)");
        return name;
    }

    public static bool IsValidInstance(string name) => InstancePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,31}$")]
    private static partial Regex InstancePattern();
}
