// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Osctl.Instances;

/// <summary>Hides passwords when configurations are shown, unless asked not to (<c>--show-secrets</c>).</summary>
public static partial class SecretMask
{
    public const string Hidden = "********";

    public static string Mask(string key, string value)
    {
        if (value.Length == 0)
            return value;
        if (key.Contains("pass", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase))
            return Hidden;
        return PasswordInValue().Replace(value, m => m.Groups[1].Value + Hidden);
    }

    [GeneratedRegex(@"((?:Password|Pwd)\s*=)[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInValue();
}
