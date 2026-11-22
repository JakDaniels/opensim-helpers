// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Ini;

/// <summary>The result of loading files with their includes.</summary>
public sealed record IniLoadResult(IniDocument Document, IReadOnlyList<string> FilesRead, IReadOnlyList<string> Warnings);

/// <summary>
/// Loads a list of ini files and follows their <c>Include-*</c> keys the way OpenSim's ConfigurationLoader does
/// (OpenSim/Region/Application/ConfigurationLoader.cs, <c>LoadConfigSettings</c> and <c>AddIncludes</c>):
/// <list type="bullet">
/// <item>Files are merged in order; a later file's keys override an earlier one's.</item>
/// <item>After each file, every <c>Include-*</c> key in the merged result so far is looked at; files it names
/// that are not on the list yet are added to the end of the list. So included files load after the files
/// given, in the order they were found.</item>
/// <item>Include paths are relative to the base directory (OpenSim's <c>bin/</c>); <c>*</c> and <c>?</c> may be
/// used. A path without wildcards that matches nothing is a warning, as in OpenSim.</item>
/// </list>
/// One difference: OpenSim takes wildcard matches in file system order; osctl sorts them, so the result does
/// not depend on the file system. http(s) includes are not supported (OpenSim reads them as XML).
/// </summary>
public static class IniLoader
{
    private static readonly char[] Wildcards = ['*', '?'];

    public static IniLoadResult Load(IEnumerable<string> files, string baseDirectory)
    {
        var baseDir = Path.GetFullPath(baseDirectory);
        var sources = new List<string>();
        foreach (var file in files)
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, file));
            if (!sources.Contains(full))
                sources.Add(full);
        }

        var result = new IniDocument();
        var warnings = new List<string>();
        var read = new List<string>();

        for (var i = 0; i < sources.Count; i++)
        {
            if (!File.Exists(sources[i]))
                throw new FileNotFoundException($"Configuration file not found: {sources[i]}", sources[i]);
            result.Merge(IniParser.ParseFile(sources[i]));
            read.Add(sources[i]);
            AddIncludes(result, baseDir, sources, warnings);
        }

        return new IniLoadResult(result, read, warnings);
    }

    private static void AddIncludes(IniDocument doc, string baseDir, List<string> sources, List<string> warnings)
    {
        foreach (var section in doc.Sections)
        {
            foreach (var entry in section.Entries)
            {
                if (!entry.Key.StartsWith("Include-", StringComparison.Ordinal))
                    continue;

                if (Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    throw new NotSupportedException(
                        $"[{section.Name}] {entry.Key} = {entry.Value} ({entry.Origin}): http includes are not supported");

                var wildcard = entry.Value.IndexOfAny(Wildcards);
                var fixedPart = wildcard < 0 ? entry.Value : entry.Value[..wildcard];
                var wildPart = wildcard < 0 ? "" : entry.Value[wildcard..];
                var path = Path.GetFullPath(Path.Combine(baseDir, fixedPart)) + wildPart;
                var matches = Glob(path);

                if (wildcard < 0 && matches.Count == 0)
                {
                    var warning = $"Could not find include file {path} ([{section.Name}] {entry.Key}, {entry.Origin})";
                    if (!warnings.Contains(warning))
                        warnings.Add(warning);
                    continue;
                }

                foreach (var match in matches)
                {
                    if (!sources.Contains(match))
                        sources.Add(match);
                }
            }
        }
    }

    /// <summary>
    /// Expands <c>*</c> and <c>?</c> in any path component, as OpenSim's <c>Util.Glob</c> does; only the last
    /// component matches files. Matches are sorted.
    /// </summary>
    internal static List<string> Glob(string path)
    {
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { "/" };
        var found = new List<string>();

        for (var c = 0; c < components.Length; c++)
        {
            var component = components[c];
            var last = c == components.Length - 1;
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (!Directory.Exists(dir))
                    continue;
                if (last)
                    found.AddRange(Directory.GetFiles(dir, component).Order(StringComparer.Ordinal));
                else
                    next.AddRange(Directory.GetDirectories(dir, component).Order(StringComparer.Ordinal));
            }
            current = next;
        }

        return found;
    }
}
