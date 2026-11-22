// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Osctl.Ini;

namespace Osctl.Builds;

public sealed record AddonRevision(string Name, string Repository, string Ref, string Commit);

/// <summary>A patch that was applied: its file and the SHA-256 of its contents.</summary>
public sealed record AppliedPatch(string File, string Sha256);

/// <summary>
/// What a release was built from, kept as <c>release.ini</c> in the release directory. The
/// <see cref="Fingerprint"/> covers everything that changes the result (commits, patch contents, commands), so
/// an update with the same fingerprint as the current release has nothing to do.
/// </summary>
public sealed record ReleaseManifest(
    string Name,
    string Build,
    DateTimeOffset Created,
    string Repository,
    string Ref,
    string Commit,
    string Subject,
    IReadOnlyList<AddonRevision> Addons,
    IReadOnlyList<AppliedPatch> Patches,
    string Prebuild,
    string Compile,
    string OsctlVersion)
{
    public const string FileName = "release.ini";

    public string Fingerprint => ComputeFingerprint(Commit, Addons, Patches, Prebuild, Compile);

    public string ShortCommit => Commit.Length > 10 ? Commit[..10] : Commit;

    public static string ComputeFingerprint(
        string commit, IEnumerable<AddonRevision> addons, IEnumerable<AppliedPatch> patches, string prebuild, string compile)
    {
        var parts = new List<string> { commit, prebuild, compile };
        parts.AddRange(addons.Select(a => $"addon:{a.Name}:{a.Commit}"));
        parts.AddRange(patches.Select(p => $"patch:{p.Sha256}"));
        return string.Join("\n", parts);
    }

    public IniDocument ToIni()
    {
        var doc = new IniDocument();
        const string r = "Release";
        doc.Set(r, "Name", Name);
        doc.Set(r, "Build", Build);
        doc.Set(r, "Created", Created.ToString("O", CultureInfo.InvariantCulture));
        doc.Set(r, "Repository", Repository);
        doc.Set(r, "Ref", Ref);
        doc.Set(r, "Commit", Commit);
        doc.Set(r, "Subject", Subject.Replace('"', '\''));
        doc.Set(r, "Prebuild", Prebuild);
        doc.Set(r, "Compile", Compile);
        doc.Set(r, "OsctlVersion", OsctlVersion);
        foreach (var addon in Addons)
        {
            var section = $"Addon {addon.Name}";
            doc.Set(section, "Repository", addon.Repository);
            doc.Set(section, "Ref", addon.Ref);
            doc.Set(section, "Commit", addon.Commit);
        }
        for (var i = 0; i < Patches.Count; i++)
        {
            var section = $"Patch {i + 1}";
            doc.Set(section, "File", Patches[i].File);
            doc.Set(section, "Sha256", Patches[i].Sha256);
        }
        return doc;
    }

    public static ReleaseManifest Load(string path)
    {
        var doc = IniParser.ParseFile(path);
        var r = doc["Release"] ?? throw new OsctlException($"{path}: no [Release] section");
        string Get(IniSection s, string key) => s[key] ?? "";

        var addons = doc.Sections
            .Where(s => s.Name.StartsWith("Addon ", StringComparison.Ordinal))
            .Select(s => new AddonRevision(s.Name["Addon ".Length..], Get(s, "Repository"), Get(s, "Ref"), Get(s, "Commit")))
            .ToList();
        var patches = doc.Sections
            .Where(s => s.Name.StartsWith("Patch ", StringComparison.Ordinal))
            .Select(s => new AppliedPatch(Get(s, "File"), Get(s, "Sha256")))
            .ToList();

        return new ReleaseManifest(
            Get(r, "Name"),
            Get(r, "Build"),
            DateTimeOffset.TryParse(Get(r, "Created"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                ? created : DateTimeOffset.MinValue,
            Get(r, "Repository"),
            Get(r, "Ref"),
            Get(r, "Commit"),
            Get(r, "Subject"),
            addons,
            patches,
            Get(r, "Prebuild"),
            Get(r, "Compile"),
            Get(r, "OsctlVersion"));
    }
}
