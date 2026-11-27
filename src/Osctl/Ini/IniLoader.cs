// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Ini;

/// <summary>One file as it was merged: its path as OpenSim would see it, where it was read from, and its contents.</summary>
/// <param name="IncludedBy">The file (as read, like <paramref name="ReadFrom"/>) whose Include-* key brought this one in;
/// null for a file given to the loader, or an include added some other way (a config set's edits).</param>
public sealed record IniLayer(string Path, string ReadFrom, IniDocument Document, string? IncludedBy = null);

/// <summary>The result of loading files with their includes.</summary>
public sealed record IniLoadResult(
    IniDocument Document,
    IReadOnlyList<string> FilesRead,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<IniLayer> Layers);

/// <summary>
/// Hooks for loading a config set (PLAN.md "Config sets and grids").
/// <see cref="Overlay"/>: given a path relative to the base directory, the file to read in its place (a
/// downloaded or shipped file), or null to read the base directory's own.
/// <see cref="Edit"/>: called with each file's relative path and contents before they are merged (and so before its
/// includes are followed).
/// <see cref="IgnoredIncludes"/>: <c>Section|Key</c> include keys that are not followed.
/// <see cref="OptionalIncludes"/>: <c>Section|Key</c> include keys that are followed when the file is there, without a
/// warning when it is not.
/// </summary>
public sealed record IniLoadOptions(
    Func<string, string?>? Overlay = null,
    Action<string, IniDocument>? Edit = null,
    IReadOnlySet<string>? IgnoredIncludes = null,
    IReadOnlySet<string>? OptionalIncludes = null);

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
/// not depend on the file system. Wildcards match the base directory's files only, not overlaid ones.
/// http(s) includes are not supported (OpenSim reads them as XML).
/// </summary>
public static class IniLoader
{
    private static readonly char[] Wildcards = ['*', '?'];

    public static IniLoadResult Load(IEnumerable<string> files, string baseDirectory, IniLoadOptions? options = null)
    {
        options ??= new IniLoadOptions();
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
        var layers = new List<IniLayer>();
        var includedBy = new Dictionary<string, string?>();

        for (var i = 0; i < sources.Count; i++)
        {
            var relative = Relative(baseDir, sources[i]);
            var actual = ReadFrom(options, relative, sources[i]);
            if (!File.Exists(actual))
                throw new FileNotFoundException($"Configuration file not found: {actual}", actual);

            var doc = IniParser.ParseFile(actual);
            options.Edit?.Invoke(relative, doc);
            result.Merge(doc);
            read.Add(sources[i]);
            layers.Add(new IniLayer(relative, actual, doc, includedBy.GetValueOrDefault(sources[i])));
            AddIncludes(result, baseDir, sources, warnings, options, includedBy);
        }

        return new IniLoadResult(result, read, warnings, layers);
    }

    private static string Relative(string baseDir, string full)
    {
        var relative = Path.GetRelativePath(baseDir, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }

    private static string ReadFrom(IniLoadOptions options, string relative, string full) =>
        options.Overlay?.Invoke(relative) ?? full;

    private static void AddIncludes(IniDocument doc, string baseDir, List<string> sources, List<string> warnings, IniLoadOptions options,
        Dictionary<string, string?> includedBy)
    {
        foreach (var section in doc.Sections)
        {
            foreach (var entry in section.Entries)
            {
                if (!entry.Key.StartsWith("Include-", StringComparison.Ordinal))
                    continue;
                if (options.IgnoredIncludes?.Contains($"{section.Name}|{entry.Key}") == true)
                    continue;

                if (Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    throw new NotSupportedException(
                        $"[{section.Name}] {entry.Key} = {entry.Value} ({entry.Origin}): http includes are not supported");

                var wildcard = entry.Value.IndexOfAny(Wildcards);
                var fixedPart = wildcard < 0 ? entry.Value : entry.Value[..wildcard];
                var wildPart = wildcard < 0 ? "" : entry.Value[wildcard..];
                var path = Path.GetFullPath(Path.Combine(baseDir, fixedPart)) + wildPart;

                List<string> matches;
                if (wildcard < 0)
                {
                    var overlaid = options.Overlay?.Invoke(Relative(baseDir, path));
                    matches = overlaid is not null || File.Exists(path) ? [path] : [];
                }
                else
                {
                    matches = Glob(path);
                }

                if (wildcard < 0 && matches.Count == 0)
                {
                    if (options.OptionalIncludes?.Contains($"{section.Name}|{entry.Key}") == true)
                        continue;
                    var warning = $"Could not find include file {path} ([{section.Name}] {entry.Key}, {entry.Origin})";
                    if (!warnings.Contains(warning))
                        warnings.Add(warning);
                    continue;
                }

                foreach (var match in matches)
                {
                    if (!sources.Contains(match))
                    {
                        sources.Add(match);
                        includedBy[match] = entry.Origin?.File;
                    }
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
