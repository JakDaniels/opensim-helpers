// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Ini;

/// <summary>A value or name that Nini could not read back as written.</summary>
public sealed class IniWriteException(string message) : Exception(message);

/// <summary>
/// Writes ini files that <see cref="IniParser"/> (and Nini) read back to the same document. Values are quoted only
/// when they have to be: empty, leading or trailing spaces, or a <c>;</c>.
/// </summary>
public static class IniWriter
{
    /// <summary>
    /// Writes the file in one step (a temporary file renamed over it), so a reader never sees half a file.
    /// <paramref name="mode"/>: the file's permissions from the start, for files with secrets in them.
    /// </summary>
    public static void WriteFile(string path, IniDocument doc, IEnumerable<string>? headerComments = null, UnixFileMode? mode = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (mode is { } m)
            options.UnixCreateMode = m;
        File.Delete(temp);
        using (var writer = new StreamWriter(new FileStream(temp, options)))
            Write(writer, doc, headerComments);
        File.Move(temp, path, overwrite: true);
    }

    public static string ToText(IniDocument doc, IEnumerable<string>? headerComments = null)
    {
        using var writer = new StringWriter();
        Write(writer, doc, headerComments);
        return writer.ToString();
    }

    public static void Write(TextWriter writer, IniDocument doc, IEnumerable<string>? headerComments = null)
    {
        writer.NewLine = "\n";
        if (headerComments is not null)
        {
            foreach (var comment in headerComments)
                writer.WriteLine(comment.Length == 0 ? ";" : $"; {comment}");
            writer.WriteLine();
        }

        var first = true;
        foreach (var section in doc.Sections)
        {
            if (!first)
                writer.WriteLine();
            first = false;

            CheckSectionName(section.Name);
            writer.WriteLine($"[{section.Name}]");
            foreach (var entry in section.Entries)
            {
                CheckKey(section.Name, entry.Key);
                writer.WriteLine($"    {entry.Key} = {FormatValue(section.Name, entry)}");
            }
        }
    }

    internal static string FormatValue(string section, IniEntry entry)
    {
        var value = entry.Value;
        if (value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            throw new IniWriteException(
                $"[{section}] {entry.Key}: a value with a double quote or a line break cannot be written to an ini file");

        var needsQuotes = value.Length == 0
            || IsSpace(value[0])
            || IsSpace(value[^1])
            || value.Contains(';');
        return needsQuotes ? $"\"{value}\"" : value;
    }

    private static void CheckSectionName(string name)
    {
        if (name.Contains(']') || name.Contains('\n') || name.Length != name.TrimEnd().Length)
            throw new IniWriteException($"Section name '{name}' cannot be written to an ini file");
    }

    private static void CheckKey(string section, string key)
    {
        if (key.Length == 0
            || key.Contains('=')
            || key.Contains('\n')
            || key[0] is '[' or ';'
            || key.Trim().Length != key.Length)
            throw new IniWriteException($"[{section}] key '{key}' cannot be written to an ini file");
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r';
}
