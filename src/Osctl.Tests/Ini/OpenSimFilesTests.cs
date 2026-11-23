// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;

namespace Osctl.Tests.Ini;

/// <summary>
/// Reads the ini files that come with OpenSim. Skipped when no OpenSim <c>bin/</c> is available
/// (see <see cref="RepoPaths.OpenSimBin"/>).
/// </summary>
public class OpenSimFilesTests
{
    [SkippableFact]
    public void EveryDistributedIniFileParsesAndRoundTrips()
    {
        var bin = RepoPaths.OpenSimBin();
        Skip.If(bin is null, "No OpenSim bin/ (set OSCTL_TEST_OPENSIM_BIN)");

        var files = Directory.GetFiles(bin, "*.ini*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ini", StringComparison.Ordinal) || f.EndsWith(".ini.example", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var doc = IniParser.ParseFile(file);
            var back = IniParser.Parse(IniWriter.ToText(doc), file);
            foreach (var section in doc.Sections)
                foreach (var entry in section.Entries)
                    Assert.Equal(entry.Value, back.Get(section.Name, entry.Key));
        }
    }

    [SkippableFact]
    public void TheStandaloneExampleLoadsWithItsIncludes()
    {
        var bin = RepoPaths.OpenSimBin();
        Skip.If(bin is null, "No OpenSim bin/ (set OSCTL_TEST_OPENSIM_BIN)");

        // What a fresh standalone reads: the master file, then OpenSim.ini (here the example), then what it includes.
        var result = IniLoader.Load(["OpenSimDefaults.ini", "OpenSim.ini.example"], bin);
        var read = result.FilesRead.Select(f => Path.GetRelativePath(bin, f)).ToList();

        Assert.Equal("OpenSimDefaults.ini", read[0]);
        Assert.Equal("OpenSim.ini.example", read[1]);
        Assert.Contains("config-include/Standalone.ini", read);
        Assert.NotNull(result.Document.Get("Startup", "physics"));
        Assert.NotNull(result.Document.Get("Network", "http_listener_port"));
    }
}
