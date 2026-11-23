// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Cli;

/// <summary>Plain text tables for list commands: columns padded to their widest cell.</summary>
internal static class Table
{
    public static void Write(TextWriter output, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var all = new List<IReadOnlyList<string>> { headers };
        all.AddRange(rows);
        var widths = headers.Select((_, i) => all.Max(r => i < r.Count ? r[i].Length : 0)).ToArray();
        foreach (var row in all)
        {
            var cells = row.Select((cell, i) => i == row.Count - 1 ? cell : cell.PadRight(widths[i]));
            output.WriteLine(string.Join("  ", cells).TrimEnd());
        }
    }
}
