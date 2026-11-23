// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Osctl.Ini;

namespace Osctl.Builds;

/// <summary>
/// An addon module built into OpenSim: a git repository copied into <c>addon-modules/&lt;name&gt;</c>, whose
/// <c>prebuild*.xml</c> OpenSim's prebuild includes. <see cref="Patches"/>: a directory in the addon (such as
/// os-webrtc-sas's <c>core-patches</c>) whose <c>*.patch</c> files are applied to OpenSim's source.
/// </summary>
public sealed record AddonDefinition(string Name, string Repository, string Ref = BuildDefinition.DefaultRef, string? Patches = null);

/// <summary>
/// A named OpenSim build (PLAN.md "Builds"), stored as /etc/osctl/builds/&lt;name&gt;.ini:
/// <code>
/// [Build]
///     Repository = git://opensimulator.org/git/opensim
///     Ref = master                 ; a branch, tag or commit; HEAD = the repository's default branch
///     Patches = sas-dev-patches    ; optional: a directory of *.patch files (relative to /etc/osctl/builds)
///     KeepReleases = 3
///     Prebuild = sh runprebuild.sh
///     Compile = dotnet build -c Release OpenSim.sln
/// [Addon os-webrtc-sas]
///     Repository = https://github.com/JakDaniels/os-webrtc-sas.git
///     Ref = main
///     Patches = core-patches
/// </code>
/// </summary>
public sealed record BuildDefinition(
    string Name,
    string Repository,
    IReadOnlyList<AddonDefinition> Addons,
    string Ref = BuildDefinition.DefaultRef,
    string? Patches = null,
    int KeepReleases = BuildDefinition.DefaultKeepReleases,
    string Prebuild = BuildDefinition.DefaultPrebuild,
    string Compile = BuildDefinition.DefaultCompile)
{
    public const string DefaultRef = "HEAD";
    public const int DefaultKeepReleases = 3;
    public const string DefaultPrebuild = "sh runprebuild.sh";
    public const string DefaultCompile = "dotnet build -c Release OpenSim.sln";

    private const string BuildSection = "Build";
    private const string AddonPrefix = "Addon ";

    public static BuildDefinition Load(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var doc = IniParser.ParseFile(path);
        var build = doc[BuildSection]
            ?? throw new OsctlException($"{path}: no [{BuildSection}] section");

        string Required(IniSection section, string key) =>
            section[key] is { Length: > 0 } value
                ? value
                : throw new OsctlException($"{path}: [{section.Name}] {key} is missing");

        var keepText = build["KeepReleases"];
        var keep = DefaultKeepReleases;
        if (!string.IsNullOrEmpty(keepText)
            && (!int.TryParse(keepText, NumberStyles.Integer, CultureInfo.InvariantCulture, out keep) || keep < 1))
            throw new OsctlException($"{path}: [{BuildSection}] KeepReleases = {keepText}: expected a whole number, 1 or more");

        var addons = new List<AddonDefinition>();
        foreach (var section in doc.Sections.Where(s => s.Name.StartsWith(AddonPrefix, StringComparison.Ordinal)))
        {
            var addonName = Names.Check(section.Name[AddonPrefix.Length..].Trim(), "addon");
            addons.Add(new AddonDefinition(addonName, Required(section, "Repository"),
                NonEmpty(section["Ref"]) ?? DefaultRef, NonEmpty(section["Patches"])));
        }

        return new BuildDefinition(
            Names.Check(name, "build"),
            Required(build, "Repository"),
            addons,
            NonEmpty(build["Ref"]) ?? DefaultRef,
            NonEmpty(build["Patches"]),
            keep,
            NonEmpty(build["Prebuild"]) ?? DefaultPrebuild,
            NonEmpty(build["Compile"]) ?? DefaultCompile);
    }

    public IniDocument ToIni()
    {
        var doc = new IniDocument();
        doc.Set(BuildSection, "Repository", Repository);
        doc.Set(BuildSection, "Ref", Ref);
        if (Patches is not null)
            doc.Set(BuildSection, "Patches", Patches);
        doc.Set(BuildSection, "KeepReleases", KeepReleases.ToString(CultureInfo.InvariantCulture));
        doc.Set(BuildSection, "Prebuild", Prebuild);
        doc.Set(BuildSection, "Compile", Compile);
        foreach (var addon in Addons)
        {
            var section = AddonPrefix + addon.Name;
            doc.Set(section, "Repository", addon.Repository);
            doc.Set(section, "Ref", addon.Ref);
            if (addon.Patches is not null)
                doc.Set(section, "Patches", addon.Patches);
        }
        return doc;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
