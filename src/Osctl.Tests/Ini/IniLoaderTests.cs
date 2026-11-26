// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;

namespace Osctl.Tests.Ini;

// Follows OpenSim's ConfigurationLoader (LoadConfigSettings, AddIncludes) and Util.Glob.
public class IniLoaderTests
{
    [Fact]
    public void LaterFilesOverrideEarlierOnes()
    {
        using var dir = new TempDir();
        dir.Write("a.ini", "[S]\nk = a\nonlyA = 1");
        dir.Write("b.ini", "[S]\nk = b");

        var result = IniLoader.Load(["a.ini", "b.ini"], dir.Path);

        Assert.Equal("b", result.Document.Get("S", "k"));
        Assert.Equal("1", result.Document.Get("S", "onlyA"));
    }

    [Fact]
    public void IncludesLoadAfterTheGivenFilesInTheOrderFound()
    {
        using var dir = new TempDir();
        dir.Write("master.ini", "[S]\nk = master");
        dir.Write("main.ini", "[Architecture]\nInclude-Architecture = \"config-include/arch.ini\"\n[S]\nk = main");
        dir.Write("config-include/arch.ini", "[Modules]\nInclude-Common = \"config-include/common.ini\"\n[S]\nk = arch");
        dir.Write("config-include/common.ini", "[S]\nk = common");
        dir.Write("last.ini", "[S]\nk = last");

        var result = IniLoader.Load(["master.ini", "main.ini", "last.ini"], dir.Path);

        Assert.Equal(
            ["master.ini", "main.ini", "last.ini", "config-include/arch.ini", "config-include/common.ini"],
            result.FilesRead.Select(f => Path.GetRelativePath(dir.Path, f)));
        // As in OpenSim: an included file is read after the files on the command line, so it wins.
        Assert.Equal("common", result.Document.Get("S", "k"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void EachFileIsReadOnce()
    {
        using var dir = new TempDir();
        dir.Write("a.ini", "[S]\nInclude-B = b.ini\nInclude-Self = a.ini");
        dir.Write("b.ini", "[T]\nInclude-A = a.ini\nInclude-B = b.ini");

        var result = IniLoader.Load(["a.ini", "a.ini"], dir.Path);

        Assert.Equal(2, result.FilesRead.Count);
    }

    [Fact]
    public void WildcardIncludesAreSorted()
    {
        using var dir = new TempDir();
        dir.Write("main.ini", "[Includes]\nInclude-Extra = extra/*.ini");
        dir.Write("extra/b.ini", "[S]\nk = b");
        dir.Write("extra/a.ini", "[S]\nk = a");
        dir.Write("extra/c.txt", "[S]\nk = txt");

        var result = IniLoader.Load(["main.ini"], dir.Path);

        Assert.Equal(["main.ini", "extra/a.ini", "extra/b.ini"],
            result.FilesRead.Select(f => Path.GetRelativePath(dir.Path, f)));
        Assert.Equal("b", result.Document.Get("S", "k"));
    }

    [Fact]
    public void WildcardsWorkInDirectoryNames()
    {
        using var dir = new TempDir();
        dir.Write("main.ini", "[Includes]\nInclude-Mods = addons/*/config.ini");
        dir.Write("addons/one/config.ini", "[One]\nk = 1");
        dir.Write("addons/two/config.ini", "[Two]\nk = 2");

        var result = IniLoader.Load(["main.ini"], dir.Path);

        Assert.Equal("1", result.Document.Get("One", "k"));
        Assert.Equal("2", result.Document.Get("Two", "k"));
    }

    [Fact]
    public void AMissingIncludeIsAWarning()
    {
        using var dir = new TempDir();
        dir.Write("main.ini", "[Includes]\nInclude-Cache = config-include/FlotsamCache.ini\nInclude-None = nothing/*.ini");

        var result = IniLoader.Load(["main.ini"], dir.Path);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("FlotsamCache.ini", warning);
    }

    [Fact]
    public void AnOptionalIncludeIsFollowedWhenThereAndQuietWhenNot()
    {
        using var dir = new TempDir();
        dir.Write("main.ini", "[OSSL]\nInclude-osslEnable = config-include/osslEnable.ini");
        var options = new IniLoadOptions(OptionalIncludes: new HashSet<string> { "OSSL|Include-osslEnable" });

        Assert.Empty(IniLoader.Load(["main.ini"], dir.Path, options).Warnings);

        dir.Write("config-include/osslEnable.ini", "[OSSL]\nOSFunctionThreatLevel = Low");
        Assert.Equal("Low", IniLoader.Load(["main.ini"], dir.Path, options).Document.Get("OSSL", "OSFunctionThreatLevel"));
    }

    [Fact]
    public void AnAbsoluteIncludeIsUsedAsItIs()
    {
        using var dir = new TempDir();
        var elsewhere = dir.Write("elsewhere/x.ini", "[X]\nk = 1");
        dir.Write("bin/main.ini", $"[Includes]\nInclude-X = {elsewhere}");

        var result = IniLoader.Load(["main.ini"], Path.Combine(dir.Path, "bin"));

        Assert.Equal("1", result.Document.Get("X", "k"));
    }

    [Fact]
    public void HttpIncludesAreRefused()
    {
        using var dir = new TempDir();
        dir.Write("main.ini", "[Includes]\nInclude-Web = http://example.org/x.ini");
        Assert.Throws<NotSupportedException>(() => IniLoader.Load(["main.ini"], dir.Path));
    }

    [Fact]
    public void AMissingGivenFileIsAnError()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>(() => IniLoader.Load(["absent.ini"], dir.Path));
    }
}
