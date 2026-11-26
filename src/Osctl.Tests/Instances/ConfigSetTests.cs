// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.ConfigSets;

namespace Osctl.Tests.Instances;

public class ConfigSetTests
{
    [Fact]
    public void TheShippedConfigSetsLoad()
    {
        using var f = new InstanceFixture();
        var store = new ConfigSetStore(f.Paths);
        Assert.Equal(["osgrid", "robust-hg", "standalone", "standalone-hg"], store.Names());

        var standalone = store.Load("standalone");
        Assert.Equal(ConfigSetMode.Standalone, standalone.Mode);
        Assert.Equal("standalone-Test1", standalone.GridFor("Test1"));
        Assert.Equal(["OpenSimDefaults.ini", "OpenSim.ini"], standalone.Load);
        Assert.Contains(standalone.Files, x => x is { Path: "OpenSim.ini", Kind: FileSourceKind.Bin, Source: "OpenSim.ini.example" });

        var hg = store.Load("standalone-hg");
        Assert.Equal(new ConfigSetEdit("OpenSim.ini", "Architecture", "Include-Architecture", "config-include/StandaloneHypergrid.ini"),
            Assert.Single(hg.Edits));

        var osgrid = store.Load("osgrid");
        Assert.Equal(ConfigSetMode.Grid, osgrid.Mode);
        Assert.Equal("osgrid", osgrid.GridFor("Anything"));
        Assert.All(osgrid.Files, x => Assert.Equal(FileSourceKind.Url, x.Kind));
        Assert.All(osgrid.Files, x => Assert.StartsWith("https://download.osgrid.org/", x.Source));

        var robust = store.Load("robust-hg");
        Assert.Equal(ConfigSetMode.Robust, robust.Mode);
        Assert.Equal("MyGrid", robust.GridFor("MyGrid"));
        Assert.Equal(["Robust.HG.ini"], robust.Load);
        Assert.Contains(robust.Edits, e => e is { Section: "ServiceList", Key: "GroupsServiceConnector" });
    }

    [Theory]
    [InlineData("[ConfigSet]\nMode = grid\nLoad = a.ini\n", "Grid is needed")]
    [InlineData("[ConfigSet]\nMode = sideways\nLoad = a.ini\n", "expected standalone, grid or robust")]
    [InlineData("[ConfigSet]\nMode = standalone\n", "Load lists no files")]
    [InlineData("[ConfigSet]\nMode = standalone\nLoad = a.ini\n[Files]\n../etc/passwd = bin:x\n", "must be a path inside bin/")]
    [InlineData("[ConfigSet]\nMode = standalone\nLoad = a.ini\n[Set a.ini]\nNoBar = 1\n", "expected Section|Key")]
    public void BadConfigSetsAreErrors(string text, string message)
    {
        using var f = new InstanceFixture();
        var path = f.Write("etc/osctl/configsets/bad.ini", text);
        Assert.Contains(message, Assert.Throws<OsctlException>(() => ConfigSet.FromFile(path)).Message);
    }

    [Fact]
    public async Task FetchDownloadsAndAnUnusableDownloadKeepsTheCachedCopy()
    {
        using var f = new InstanceFixture();
        var store = new ConfigSetStore(f.Paths);
        var set = store.Load("osgrid");
        foreach (var file in set.Files)
            f.Web.Pages[file.Source] = $"[From]\n    Url = {file.Source}\n";

        Assert.False(store.IsFetched(set));
        var output = await f.Ok("configset", "fetch", "osgrid");
        Assert.Contains("downloaded https://download.osgrid.org/OpenSim.ini.txt", output);
        Assert.True(store.IsFetched(set));
        var cached = store.CacheFile("osgrid", "OpenSim.ini");
        Assert.Contains("OpenSim.ini.txt", File.ReadAllText(cached));

        f.Web.Pages["https://download.osgrid.org/OpenSim.ini.txt"] = "<html>Service unavailable</html>";
        var (exit, _, error) = await f.Osctl("configset", "fetch", "osgrid");
        Assert.Equal(2, exit);
        Assert.Contains("not a usable ini file", error);
        Assert.Contains("OpenSim.ini.txt", File.ReadAllText(cached));
    }

    [Fact]
    public async Task ListAndShow()
    {
        using var f = new InstanceFixture();
        var list = await f.Ok("configset", "list");
        Assert.Contains("osgrid         grid        osgrid  no", list);
        var show = await f.Ok("configset", "show", "osgrid");
        Assert.Contains("OpenSim.ini <- https://download.osgrid.org/OpenSim.ini.txt (not downloaded yet)", show);
    }
}
