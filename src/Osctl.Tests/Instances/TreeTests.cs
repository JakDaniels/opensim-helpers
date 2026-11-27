// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Util;

namespace Osctl.Tests.Instances;

/// <summary><c>osctl config tree</c> and <c>osctl help --tree</c>.</summary>
public class TreeTests
{
    [Fact]
    public void ATreeIsDrawnLikeTreeWithItsNotesInAColumn()
    {
        var root = new TreeNode("root");
        var a = root.Add("a", "first");
        a.Add("a1");
        a.Add("a2", "note");
        root.Add("bb", "second");

        Assert.Equal(
            ["root",
             "├── a       first",
             "│   ├── a1",
             "│   └── a2  note",
             "└── bb      second"],
            TextTree.Render(root));
    }

    private static async Task<InstanceFixture> WithInstance()
    {
        var f = new InstanceFixture();
        await f.Ok("instance", "add", "Test1", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Welcome", "--instance", "Test1", "--location", "1000,1000");
        return f;
    }

    [Fact]
    public async Task TheConfigTreeShowsTheFilesUnderEtcWithWhatTheyAre()
    {
        using var f = await WithInstance();
        await f.Ok("config", "set", "Startup", "physics=ubODE", "--instance", "Test1");

        var tree = await f.Ok("config", "tree");

        var lines = tree.Split('\n');
        Assert.Equal(f.Paths.Etc, lines[0]);
        Assert.Matches(@"├── instances/ +instance definitions", tree);
        Assert.Matches(@"│   └── Test1/ +standalone, build b1, sqlite", tree);
        Assert.Matches(@"│       ├── overrides/ +OpenSim\.ini overrides for this instance", tree);
        Assert.Matches(@"│       │   └── 50-osctl\.ini +written by 'osctl config set'", tree);
        Assert.Matches(@"│       │   └── Regions\.ini +Welcome", tree);
        Assert.Matches(@"region-defaults\.ini +\[Region Defaults\] for every region", tree);
        Assert.Matches(@"standalone\.ini +standalone", tree);
    }

    [Fact]
    public async Task TheConfigTreeLeavesOutFilesOsctlDoesNotReadUnlessAll()
    {
        using var f = await WithInstance();
        f.Write("etc/osctl/overrides/10-physics.ini~", "editor backup");
        f.Write("etc/osctl/notes/todo.txt", "mine");
        f.Write("etc/osctl/configsets/mine/OpenSim.ini", "[Startup]\n");
        f.Write("etc/osctl/configsets/mine.ini", "[ConfigSet]\n    Mode = standalone\n    Load = OpenSimDefaults.ini, OpenSim.ini\n[Files]\n    OpenSim.ini = mine/OpenSim.ini\n");
        // An instance not yet moved: only its definition is shown, not its data next to it.
        await f.Ok("instance", "add", "Old", "--db", "sqlite", "--estate-owner", "Test User");
        foreach (var entry in new[] { "instance.ini", "secrets.ini", "overrides", "regions", "startup_commands.txt", "shutdown_commands.txt" })
            Directory.Move(Path.Combine(f.Paths.Etc, "instances", "Old", entry), Path.Combine(f.Instances.Dir("Old"), entry));
        Directory.Delete(Path.Combine(f.Paths.Etc, "instances", "Old"));
        f.Write("var/lib/osctl/instances/Old/data/fsassets/data/ab/asset.gz", "x");
        Directory.CreateDirectory(f.Paths.Run);
        new global::Osctl.Running.RunState(Environment.ProcessId, 1, "r1", DateTimeOffset.Now, DateTimeOffset.Now).Save(f.Instances.RunStateFile("Old"));

        var tree = await f.Ok("config", "tree");

        Assert.DoesNotContain("10-physics.ini~", tree);
        Assert.DoesNotContain("notes/", tree);
        Assert.DoesNotContain("README.txt", tree);
        Assert.Matches(@"mine/ *\n.*└── OpenSim\.ini +read by a config set", tree);
        Assert.Contains("instances whose definition is still here", tree);
        Assert.DoesNotContain("fsassets", tree);
        Assert.DoesNotContain("generated", tree);
        Assert.Contains("(4 other files osctl does not read: 'osctl config tree --all' shows them)", tree);   // 2 + 2 READMEs

        var all = await f.Ok("config", "tree", "--all");
        Assert.Contains("10-physics.ini~", all);
        Assert.Contains("todo.txt", all);
        Assert.DoesNotContain("fsassets", all);
        Assert.DoesNotContain("other files", all);
    }

    [Fact]
    public async Task AnInstancesConfigTreeShowsItsFilesInTheOrderTheyAreRead()
    {
        using var f = await WithInstance();
        await f.Ok("config", "set", "Startup", "physics=ubODE", "--global");
        await f.Ok("region", "defaults", "set", "MaxAgents=40", "--instance", "Test1");
        f.Write("etc/osctl/instances/Test1/regions/voice.ini", "[Welcome]\n    VoiceRoom = lobby\n");

        var tree = await f.Ok("config", "tree", "Test1");

        Assert.Matches(@"├── OpenSim\.ini +read in this order, later wins", tree);
        Assert.Matches(@"│   ├── config set standalone +configsets/standalone\.ini", tree);
        Assert.Matches(@"│   │   ├── 1\. OpenSimDefaults\.ini", tree);
        // The include sits under the file that includes it.
        Assert.Matches(@"│   │   └── 2\. OpenSim\.ini +from OpenSim\.ini\.example\n│   │       └── 3\. config-include/Standalone\.ini", tree);
        Assert.Matches(@"\d+\. overrides/50-osctl\.ini +every instance", tree);
        Assert.Matches(@"└── \d+\. set by osctl", tree);
        Assert.Contains("└── Regions.ini", tree);
        Assert.Matches(@"1\. region-defaults\.ini +\[Region Defaults\] for every region", tree);
        Assert.Matches(@"2\. instances/Test1/region-defaults\.ini", tree);
        Assert.Matches(@"3\. instances/Test1/regions/voice\.ini +settings for Welcome", tree);
        Assert.Matches(@"4\. instances/Test1/regions/Regions\.ini +Welcome", tree);
    }

    [Fact]
    public async Task HelpTreeShowsEveryCommandAndWithOptionsTheirOptions()
    {
        using var f = new InstanceFixture();

        var tree = await f.Ok("help", "--tree");
        Assert.StartsWith("osctl\n", tree);
        Assert.Matches(@"├── region +The regions of instances", tree);
        Assert.Matches(@"│   └── defaults +\[Region Defaults\]", tree);
        Assert.Matches(@"│       ├── get \[settings\.\.\.\] +Show region defaults", tree);
        Assert.Matches(@"│   ├── restore <instance> \[backup\] +Put a backup back", tree);
        Assert.DoesNotContain("--instance <", tree);

        var region = await f.Ok("help", "--tree", "--options", "region", "defaults");
        Assert.StartsWith("osctl region defaults", region);
        Assert.Matches(@"│   ├── --instance <instance> +The instance's region defaults", region);
        Assert.DoesNotContain("--dry-run", region);                    // shown once, on osctl itself
        Assert.Contains("--dry-run", await f.Ok("help", "--tree", "--options"));
        Assert.Contains("--location <location> (required)", await f.Ok("help", "--tree", "--options", "region"));

        Assert.Contains("Usage:", await f.Ok("help", "backup", "restore"));
        var (exit, _, err) = await f.Osctl("help", "--tree", "nothing");
        Assert.Equal(2, exit);
        Assert.Contains("no such command", err);
    }
}
