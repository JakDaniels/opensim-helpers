// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Running;

namespace Osctl.Tests.Instances;

/// <summary>
/// An instance's definition in /etc/osctl/instances, what it makes in /var/lib/osctl/instances (PLAN.md "Layout on
/// disk"), moving instances made by earlier versions, and the commands for region defaults and overrides.
/// </summary>
public class LayoutTests
{
    private static async Task<InstanceFixture> WithInstance()
    {
        var f = new InstanceFixture();
        await f.Ok("instance", "add", "Test1", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Welcome", "--instance", "Test1", "--location", "1000,1000");
        return f;
    }

    private static string Etc(InstanceFixture f, string name) => Path.Combine(f.Paths.Etc, "instances", name);

    private static string Var(InstanceFixture f, string name) => Path.Combine(f.Paths.Lib, "instances", name);

    private static readonly string[] Definition =
        ["instance.ini", "secrets.ini", "overrides", "regions", "startup_commands.txt", "shutdown_commands.txt"];

    /// <summary>Puts an instance's definition back where earlier versions kept it.</summary>
    private static void ToOldLayout(InstanceFixture f, string name)
    {
        foreach (var entry in Definition)
            Directory.Move(Path.Combine(Etc(f, name), entry), Path.Combine(Var(f, name), entry));
        Directory.Delete(Etc(f, name));
    }

    [Fact]
    public async Task AnInstancesDefinitionIsUnderEtcAndWhatItMakesUnderVar()
    {
        using var f = await WithInstance();

        foreach (var entry in Definition)
            Assert.True(Path.Exists(Path.Combine(Etc(f, "Test1"), entry)), entry);
        foreach (var entry in new[] { "generated", "data", "scriptengines", "addins", "backups" })
            Assert.True(Directory.Exists(Path.Combine(Var(f, "Test1"), entry)), entry);
        Assert.False(File.Exists(Path.Combine(Var(f, "Test1"), "instance.ini")));
        Assert.Equal(Path.Combine(Etc(f, "Test1"), "startup_commands.txt"),
            f.Generator().Generate("Test1").OpenSim.Get("Startup", "startup_console_commands_file"));

        await f.Ok("instance", "delete", "Test1", "--yes");
        Assert.False(Directory.Exists(Etc(f, "Test1")));
        Assert.False(Directory.Exists(Var(f, "Test1")));
    }

    [Fact]
    public async Task AStoppedInstanceInTheOldLayoutIsMovedByTheNextCommand()
    {
        using var f = await WithInstance();
        var uuid = f.Instances.Regions("Test1")[0].Uuid;
        File.WriteAllText(Path.Combine(Var(f, "Test1"), "data", "OpenSim.db"), "the data");
        ToOldLayout(f, "Test1");
        Assert.True(f.Instances.IsOldLayout("Test1"));

        var (exit, output, err) = await f.Osctl("region", "list");

        Assert.Equal(0, exit);
        Assert.Contains("Welcome", output);
        Assert.Contains($"instance 'Test1': its definition (instance.ini, regions, overrides, secrets, commands) moved to {Etc(f, "Test1")}", err);
        Assert.False(f.Instances.IsOldLayout("Test1"));
        foreach (var entry in Definition)
        {
            Assert.True(Path.Exists(Path.Combine(Etc(f, "Test1"), entry)), entry);
            Assert.False(Path.Exists(Path.Combine(Var(f, "Test1"), entry)), entry);
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(Path.Combine(Etc(f, "Test1"), "secrets.ini")));
        Assert.Equal(uuid, f.Instances.Regions("Test1")[0].Uuid);
        Assert.Equal("the data", File.ReadAllText(Path.Combine(Var(f, "Test1"), "data", "OpenSim.db")));

        // Nothing more to move.
        Assert.DoesNotContain("moved", (await f.Osctl("region", "list")).Err);
    }

    [Fact]
    public async Task ARunningInstanceInTheOldLayoutIsUsedWhereItIsUntilItStops()
    {
        using var f = await WithInstance();
        ToOldLayout(f, "Test1");
        Directory.CreateDirectory(f.Paths.Run);
        var state = f.Instances.RunStateFile("Test1");
        new RunState(Environment.ProcessId, 1, "r1", DateTimeOffset.Now, DateTimeOffset.Now).Save(state);

        var (exit, output, err) = await f.Osctl("region", "list");
        Assert.Equal(0, exit);
        Assert.Contains("Welcome", output);
        Assert.Contains("is running", err);
        Assert.True(f.Instances.IsOldLayout("Test1"));
        Assert.Equal(Path.Combine(Var(f, "Test1"), "instance.ini"), f.Instances.DefinitionFile("Test1"));

        // --dry-run moves nothing either.
        File.Delete(state);
        await f.Ok("--dry-run", "region", "list");
        Assert.True(f.Instances.IsOldLayout("Test1"));
        await f.Ok("region", "list");
        Assert.False(f.Instances.IsOldLayout("Test1"));
    }

    [Fact]
    public async Task RegionDefaultsAreSetGloballyAndPerInstanceAndRegionsStillWin()
    {
        using var f = await WithInstance();
        await f.Ok("instance", "add", "Test2", "--db", "sqlite", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Other", "--instance", "Test2", "--location", "1010,1010");

        var (exit, _, err) = await f.Osctl("region", "defaults", "set", "MaxAgents=40");
        Assert.Equal(2, exit);
        Assert.Contains("--global", err);
        (exit, _, err) = await f.Osctl("region", "defaults", "set", "Location=1,1", "--instance", "Test1");
        Assert.Equal(2, exit);
        Assert.Contains("region add/move/rename", err);

        await f.Ok("region", "defaults", "set", "MaxAgents=40", "VoiceRoom=lobby", "--instance", "Test1");
        await f.Ok("region", "defaults", "set", "MaxPrims=20000", "--global");

        var welcome = f.Generator().Generate("Test1").Regions["Welcome"]!;
        Assert.Equal("40", welcome["MaxAgents"]);
        Assert.Equal("lobby", welcome["VoiceRoom"]);
        Assert.Equal("20000", welcome["MaxPrims"]);
        var other = f.Generator().Generate("Test2").Regions["Other"]!;
        Assert.Equal("100", other["MaxAgents"]);                       // the shipped global default
        Assert.Null(other["VoiceRoom"]);
        Assert.Equal("20000", other["MaxPrims"]);

        // The region's own setting wins; get shows where each value comes from.
        await f.Ok("region", "set", "Welcome", "MaxAgents=10");
        Assert.Equal("MaxAgents = 10\n", await f.Ok("region", "get", "Welcome", "MaxAgents"));
        var origin = await f.Ok("region", "defaults", "get", "--instance", "Test1", "Max*", "--origin");
        Assert.Matches(@"MaxAgents = 40 +; .*instances/Test1/region-defaults\.ini", origin);
        Assert.Matches(@"MaxPrims = 20000 +; .*etc/osctl/region-defaults\.ini", origin);
        Assert.Equal("MaxAgents = 40\nVoiceRoom = lobby\n", await f.Ok("region", "defaults", "get", "--instance", "Test1", "--own"));

        // Key= removes one; the shipped file keeps its comments.
        await f.Ok("region", "defaults", "set", "VoiceRoom=", "--instance", "Test1");
        Assert.Null(f.Generator().Generate("Test1").Regions["Welcome"]!["VoiceRoom"]);
        Assert.Contains(";", File.ReadAllText(f.Paths.RegionDefaultsFile));
    }

    [Fact]
    public async Task ConfigSetWritesAnOverrideFileForAnInstanceOrEveryone()
    {
        using var f = await WithInstance();

        await f.Ok("config", "set", "Startup", "MaxPoolThreads=200", "physics=ubODE", "--instance", "Test1");
        await f.Ok("config", "set", "Startup", "MaxPoolThreads=100", "--global");
        await f.Ok("config", "set", "Permissions", "allow_grid_gods=true", "--global");

        var c = f.Generator().Generate("Test1").OpenSim;
        Assert.Equal("200", c.Get("Startup", "MaxPoolThreads"));       // the instance's over the global one
        Assert.Equal("ubODE", c.Get("Startup", "physics"));
        Assert.Equal("true", c.Get("Permissions", "allow_grid_gods"));
        Assert.True(File.Exists(Path.Combine(Etc(f, "Test1"), "overrides", "50-osctl.ini")));
        Assert.True(File.Exists(Path.Combine(f.Paths.GlobalOverridesDir, "50-osctl.ini")));

        await f.Ok("config", "set", "Startup", "physics=", "--instance", "Test1");
        Assert.Equal("BulletSim", f.Generator().Generate("Test1").OpenSim.Get("Startup", "physics"));

        // A file read after it wins, and is pointed out.
        f.Write("etc/osctl/instances/Test1/overrides/90-mine.ini", "[Startup]\n    MaxPoolThreads = 300\n");
        var (exit, _, err) = await f.Osctl("config", "set", "Startup", "MaxPoolThreads=250", "--instance", "Test1");
        Assert.Equal(0, exit);
        Assert.Contains("90-mine.ini also sets [Startup] MaxPoolThreads, and wins", err);

        (exit, _, err) = await f.Osctl("config", "set", "Startup", "Include-Extra=x.ini", "--instance", "Test1");
        Assert.Equal(2, exit);
        Assert.Contains("not followed", err);
        (exit, _, err) = await f.Osctl("config", "set", "Startup", "A=1", "--instance", "Nope");
        Assert.Equal(2, exit);
        Assert.Contains("no instance 'Nope'", err);
    }
}
