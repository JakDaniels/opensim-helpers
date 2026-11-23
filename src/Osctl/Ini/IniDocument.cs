// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Ini;

/// <summary>Where a value came from: the file and the 1-based line number.</summary>
public readonly record struct IniOrigin(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

/// <summary>One key and its value (quotes already removed), with where it was set.</summary>
public sealed record IniEntry(string Key, string Value, IniOrigin? Origin);

/// <summary>
/// A section: keys in order of first appearance. Names are case-sensitive, as in Nini (and so in OpenSim).
/// </summary>
public sealed class IniSection
{
    private readonly List<IniEntry> entries = [];
    private readonly Dictionary<string, int> index = new(StringComparer.Ordinal);

    public IniSection(string name) => Name = name;

    public string Name { get; }

    public IReadOnlyList<IniEntry> Entries => entries;

    public IEnumerable<string> Keys => entries.Select(e => e.Key);

    public int Count => entries.Count;

    public bool Contains(string key) => index.ContainsKey(key);

    public string? this[string key] => index.TryGetValue(key, out var i) ? entries[i].Value : null;

    public IniEntry? GetEntry(string key) => index.TryGetValue(key, out var i) ? entries[i] : null;

    /// <summary>Sets a key: an existing key keeps its position and gets the new value.</summary>
    public void Set(string key, string value, IniOrigin? origin = null)
    {
        var entry = new IniEntry(key, value, origin);
        if (index.TryGetValue(key, out var i))
        {
            entries[i] = entry;
        }
        else
        {
            index[key] = entries.Count;
            entries.Add(entry);
        }
    }

    /// <summary>Adds a key only if it is not there yet (Nini's rule for a key repeated within one file).</summary>
    internal bool TryAdd(string key, string value, IniOrigin? origin)
    {
        if (index.ContainsKey(key))
            return false;
        Set(key, value, origin);
        return true;
    }

    public bool Remove(string key)
    {
        if (!index.Remove(key, out var i))
            return false;
        entries.RemoveAt(i);
        for (var j = i; j < entries.Count; j++)
            index[entries[j].Key] = j;
        return true;
    }
}

/// <summary>An ini file, or several merged: sections in order of first appearance.</summary>
public sealed class IniDocument
{
    private readonly List<IniSection> sections = [];
    private readonly Dictionary<string, IniSection> byName = new(StringComparer.Ordinal);

    public IReadOnlyList<IniSection> Sections => sections;

    public IniSection? this[string name] => byName.GetValueOrDefault(name);

    public bool Contains(string section) => byName.ContainsKey(section);

    public string? Get(string section, string key) => this[section]?[key];

    public IniSection GetOrAdd(string name)
    {
        if (!byName.TryGetValue(name, out var section))
        {
            section = new IniSection(name);
            byName[name] = section;
            sections.Add(section);
        }
        return section;
    }

    public void Set(string section, string key, string value, IniOrigin? origin = null) =>
        GetOrAdd(section).Set(key, value, origin);

    public bool Remove(string section, string key) => this[section]?.Remove(key) ?? false;

    public bool RemoveSection(string name)
    {
        if (!byName.Remove(name, out var section))
            return false;
        sections.Remove(section);
        return true;
    }

    /// <summary>
    /// Replaces a section by a new, empty one at the end (Nini's rule for a section repeated within one file).
    /// </summary>
    internal IniSection Restart(string name)
    {
        RemoveSection(name);
        return GetOrAdd(name);
    }

    /// <summary>
    /// Merges another document into this one, as Nini's <c>IConfigSource.Merge</c> does: its keys override
    /// keys already here, and sections and keys that are new are added at the end.
    /// </summary>
    public void Merge(IniDocument other)
    {
        foreach (var section in other.sections)
        {
            var target = GetOrAdd(section.Name);
            foreach (var entry in section.Entries)
                target.Set(entry.Key, entry.Value, entry.Origin);
        }
    }

    public IniDocument Clone()
    {
        var copy = new IniDocument();
        copy.Merge(this);
        return copy;
    }
}
