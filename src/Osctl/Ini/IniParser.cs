// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Ini;

/// <summary>A file that Nini (and so OpenSim) would refuse to read.</summary>
public sealed class IniParseException(string message, IniOrigin origin)
    : Exception($"{origin}: {message}")
{
    public IniOrigin Origin { get; } = origin;
}

/// <summary>
/// Reads ini files the way Nini's IniReader does in its standard mode, which is what OpenSim uses
/// (opensim-libs Nini, Source/Ini/IniReader.cs and IniDocument.cs):
/// <list type="bullet">
/// <item>Comments start with <c>;</c>, at the start of a line or after an unquoted value. <c>#</c> is not a comment.</item>
/// <item>A value whose first character is <c>"</c> runs to the next <c>"</c>, kept exactly (spaces and <c>;</c>
/// included); the rest of the line is ignored. A missing closing quote is an error.</item>
/// <item>An unquoted value ends at <c>;</c>, at a <c>"</c>, or at the end of the line, and loses trailing spaces.</item>
/// <item>A section name runs to <c>]</c> (anything after it is ignored) and loses trailing spaces.</item>
/// <item>A key repeated in one section of one file keeps its first value.</item>
/// <item>A section repeated in one file replaces the earlier one completely.</item>
/// <item>Names are case-sensitive.</item>
/// </list>
/// A key before the first section is an error (Nini crashes on it).
/// </summary>
public static class IniParser
{
    public static IniDocument ParseFile(string path)
    {
        // StreamReader detects and drops a byte order mark, as Nini's reader does.
        using var reader = new StreamReader(path);
        return Parse(reader, path);
    }

    public static IniDocument Parse(string text, string sourceName = "<text>") =>
        Parse(new StringReader(text), sourceName);

    public static IniDocument Parse(TextReader reader, string sourceName)
    {
        var doc = new IniDocument();
        IniSection? section = null;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var origin = new IniOrigin(sourceName, lineNumber);
            var i = SkipWhitespace(line, 0);
            if (i >= line.Length || line[i] == ';')
                continue;

            if (line[i] == '[')
            {
                var end = line.IndexOf(']', i + 1);
                if (end < 0)
                    throw new IniParseException("Expected section end (])", origin);
                section = doc.Restart(line[(i + 1)..end].TrimEnd());
                continue;
            }

            var assign = line.IndexOf('=', i);
            if (assign < 0)
                throw new IniParseException("Expected assignment operator (=)", origin);
            var key = line[i..assign].TrimEnd();
            if (section is null)
                throw new IniParseException($"Key '{key}' is outside any section", origin);

            var value = ReadValue(line, assign + 1, origin);
            section.TryAdd(key, value, origin);
        }

        return doc;
    }

    private static string ReadValue(string line, int start, IniOrigin origin)
    {
        var i = SkipWhitespace(line, start);
        if (i < line.Length && line[i] == '"')
        {
            var close = line.IndexOf('"', i + 1);
            if (close < 0)
                throw new IniParseException("Expected closing quote (\")", origin);
            return line[(i + 1)..close];
        }

        var end = line.IndexOfAny([';', '"'], i);
        return (end < 0 ? line[i..] : line[i..end]).TrimEnd();
    }

    // Nini's whitespace: space, tab and carriage return (a line feed ends the line).
    private static int SkipWhitespace(string line, int i)
    {
        while (i < line.Length && line[i] is ' ' or '\t' or '\r')
            i++;
        return i;
    }
}
