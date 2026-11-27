// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Osctl.Ini;

/// <summary>
/// Changes one value in a hand-kept ini file and leaves the rest of the file as it was (comments, layout), for
/// files people edit too, such as secrets.ini. Rewriting with <see cref="IniWriter"/> would lose the comments.
/// </summary>
public static partial class IniFileEditor
{
    public static void SetValue(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var formatted = IniWriter.FormatValue(section, new IniEntry(key, value, null));

        var inSection = false;
        var sectionEnd = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var header = SectionHeader().Match(lines[i]);
            if (header.Success)
            {
                if (inSection)
                    break;
                inSection = header.Groups[1].Value.TrimEnd() == section;
                if (inSection)
                    sectionEnd = i + 1;
                continue;
            }
            if (!inSection)
                continue;

            var assignment = KeyLine().Match(lines[i]);
            if (assignment.Success && assignment.Groups[2].Value.TrimEnd() == key)
            {
                lines[i] = $"{assignment.Groups[1].Value}{key} = {formatted}";
                Save(path, lines);
                return;
            }
            if (lines[i].Trim().Length > 0)
                sectionEnd = i + 1;
        }

        if (sectionEnd < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add($"[{section}]");
            sectionEnd = lines.Count;
        }
        lines.Insert(sectionEnd, $"    {key} = {formatted}");
        Save(path, lines);
    }

    /// <summary>Removes a key from a section (every line that sets it). Returns whether there was one.</summary>
    public static bool RemoveValue(string path, string section, string key)
    {
        if (!File.Exists(path))
            return false;
        var lines = File.ReadAllLines(path).ToList();
        var inSection = false;
        var removed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var header = SectionHeader().Match(lines[i]);
            if (header.Success)
            {
                inSection = header.Groups[1].Value.TrimEnd() == section;
                continue;
            }
            if (inSection && KeyLine().Match(lines[i]) is { Success: true } assignment && assignment.Groups[2].Value.TrimEnd() == key)
            {
                lines.RemoveAt(i--);
                removed = true;
            }
        }
        if (removed)
            Save(path, lines);
        return removed;
    }

    private static void Save(string path, List<string> lines)
    {
        // Keep the file's mode (secrets.ini is 0640): write over it rather than replacing it.
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    [GeneratedRegex(@"^\s*\[([^\]]*)\]")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^(\s*)([^;=\[][^=]*?)\s*=")]
    private static partial Regex KeyLine();
}
