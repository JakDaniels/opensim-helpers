// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Instances;

namespace Osctl.Tests.Instances;

public class InstanceTests
{
    [Fact]
    public async Task AStandaloneGetsAGeneratedOwnerPasswordShownOnce()
    {
        using var f = new InstanceFixture();
        var output = await f.Ok("instance", "add", "Test1", "--estate-owner", "Test User");

        var instance = f.Instances.Load("Test1");
        Assert.Equal("b1", instance.Build);                       // the only build
        Assert.Equal("standalone", instance.ConfigSet);           // [Defaults] ConfigSet
        Assert.Equal(DatabaseKind.MariaDb, instance.Database);
        Assert.Equal("Test1 Estate", instance.Estate.Name);
        Assert.NotNull(instance.Estate.OwnerUuid);

        var password = f.Instances.OwnerPassword("Test1")!;
        Assert.Equal(20, password.Length);
        Assert.Contains($"with the password {password}", output);
        Assert.Equal(32, f.Instances.RemoteAdminPassword("Test1")!.Length);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(f.Instances.SecretsFile("Test1")));
    }

    [Fact]
    public async Task AGridInstanceFetchesItsFilesAndNeedsNoOwnerPassword()
    {
        using var f = new InstanceFixture();
        foreach (var url in new[] { "OpenSim.ini.txt", "GridCommon.ini.txt", "FlotsamCache.ini.txt" })
            f.Web.Pages[$"https://download.osgrid.org/{url}"] = "[Startup]\n";

        var output = await f.Ok("instance", "add", "Grid1", "--configset", "osgrid", "--db", "sqlite", "--estate-owner", "Jak Daniels");

        Assert.Equal(3, f.Web.Requests.Count);
        Assert.DoesNotContain("password", output);
        Assert.Null(f.Instances.OwnerPassword("Grid1"));
        Assert.Null(f.Instances.Load("Grid1").Estate.OwnerUuid);
    }

    [Theory]
    [InlineData(new[] { "instance", "add", "bad.name", "--estate-owner", "A B" }, "not a valid instance name")]
    [InlineData(new[] { "instance", "add", "X", "--estate-owner", "Onename" }, "not an avatar name")]
    [InlineData(new[] { "instance", "add", "X", "--estate-owner", "A B", "--build", "nope" }, "no build 'nope'")]
    [InlineData(new[] { "instance", "add", "X", "--estate-owner", "A B", "--configset", "nope" }, "no config set 'nope'")]
    [InlineData(new[] { "instance", "add", "X", "--estate-owner", "A B", "--db", "oracle" }, "expected mariadb or sqlite")]
    public async Task BadAddsAreErrors(string[] args, string message)
    {
        using var f = new InstanceFixture();
        var (exit, _, error) = await f.Osctl(args);
        Assert.Equal(2, exit);
        Assert.Contains(message, error);
    }

    [Fact]
    public async Task AnInstanceCannotBeAddedTwiceAndDeleteNeedsYes()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "Test1", "--estate-owner", "Test User");
        Assert.Contains("already exists", (await f.Osctl("instance", "add", "Test1", "--estate-owner", "Test User")).Err);

        Assert.Equal(1, (await f.Osctl("instance", "delete", "Test1")).Exit);
        Assert.True(f.Instances.Exists("Test1"));
        await f.Ok("instance", "delete", "Test1", "--yes");
        Assert.False(Directory.Exists(f.Instances.Dir("Test1")));
    }

    [Fact]
    public async Task DryRunAddCreatesNothing()
    {
        using var f = new InstanceFixture();
        var output = await f.Ok("--dry-run", "instance", "add", "Test1", "--estate-owner", "Test User");
        Assert.Contains("would create instance 'Test1'", output);
        Assert.False(Directory.Exists(f.Instances.Dir("Test1")));
    }
}

public class RegionTests
{
    private static async Task<InstanceFixture> WithInstances(params string[] names)
    {
        var f = new InstanceFixture();
        foreach (var name in names)
            await f.Ok("instance", "add", name, "--estate-owner", "Test User");
        return f;
    }

    [Fact]
    public async Task PortsComeFromTheRangeSkippingUsedAndBusyOnes()
    {
        using var f = await WithInstances("A", "B");
        f.Ports.Busy.Add(9000);
        await f.Ok("region", "add", "One", "--instance", "A", "--location", "1000,1000");
        await f.Ok("region", "add", "Two", "--instance", "B", "--location", "1000,1000");

        Assert.Equal(9001, f.Instances.Regions("A").Single().Port);
        Assert.Equal(9002, f.Instances.Regions("B").Single().Port);

        var taken = await f.Osctl("region", "add", "Three", "--instance", "B", "--location", "1001,1000", "--port", "9001");
        Assert.Contains("already used by region 'One'", taken.Err);
        var busy = await f.Osctl("region", "add", "Three", "--instance", "B", "--location", "1001,1000", "--port", "9000");
        Assert.Contains("in use on this machine", busy.Err);
    }

    [Fact]
    public async Task LocationsAndNamesClashOnlyWithinAGrid()
    {
        using var f = await WithInstances("A", "B");
        await f.Ok("region", "add", "Big", "--instance", "A", "--location", "1000,1000", "--size", "512");

        // Each standalone is its own grid: the same place and name are fine in another standalone.
        await f.Ok("region", "add", "big", "--instance", "B", "--location", "1001,1001");

        var overlap = await f.Osctl("region", "add", "Inside", "--instance", "A", "--location", "1001,1001");
        Assert.Contains("overlaps region 'Big'", overlap.Err);
        var name = await f.Osctl("region", "add", "BIG", "--instance", "A", "--location", "2000,2000");
        Assert.Contains("already a region 'Big'", name.Err);
        await f.Ok("region", "add", "Beside", "--instance", "A", "--location", "1002,1000");
    }

    [Theory]
    [InlineData("--size", "300", "multiple of 256")]
    [InlineData("--location", "10,x", "expected x,y")]
    public async Task BadRegionValues(string option, string value, string message)
    {
        using var f = await WithInstances("A");
        var args = new List<string> { "region", "add", "R", "--instance", "A", "--location", "1000,1000" };
        var i = args.IndexOf(option);
        if (i >= 0)
            args[i + 1] = value;
        else
            args.AddRange([option, value]);
        Assert.Contains(message, (await f.Osctl([.. args])).Err);
    }

    [Fact]
    public async Task RenameMoveSetAndRemove()
    {
        using var f = await WithInstances("A", "B");
        await f.Ok("region", "add", "Old Name", "--instance", "A", "--location", "1000,1000");
        var uuid = f.Instances.Regions("A").Single().Uuid;

        await f.Ok("region", "rename", "Old Name", "New Name");
        Assert.Equal(("New Name", uuid), (f.Instances.Regions("A").Single().Name, f.Instances.Regions("A").Single().Uuid));

        await f.Ok("region", "set", "New Name", "MaxAgents=40", "RegionType=Mainland");
        await f.Ok("region", "set", "New Name", "RegionType=");
        Assert.Equal([KeyValuePair.Create("MaxAgents", "40")], f.Instances.Regions("A").Single().Settings);
        Assert.Contains("use 'osctl region add/move/rename'", (await f.Osctl("region", "set", "New Name", "InternalPort=1")).Err);

        await f.Ok("region", "move", "New Name", "--to", "B", "--location", "1005,1005");
        Assert.Empty(f.Instances.Regions("A"));
        var moved = f.Instances.Regions("B").Single();
        Assert.Equal((uuid, 1005, 1005, "40"), (moved.Uuid, moved.X, moved.Y, moved.Settings.Single().Value));

        Assert.Equal(1, (await f.Osctl("region", "remove", "New Name")).Exit);
        await f.Ok("region", "remove", "New Name", "--yes");
        Assert.Empty(f.Instances.Regions("B"));
    }

    [Fact]
    public async Task ARegionNameInTwoGridsNeedsTheInstance()
    {
        using var f = await WithInstances("A", "B");
        await f.Ok("region", "add", "Same", "--instance", "A", "--location", "1000,1000");
        await f.Ok("region", "add", "Same", "--instance", "B", "--location", "1000,1000");

        Assert.Contains("add --instance", (await f.Osctl("region", "rename", "Same", "Other")).Err);
        await f.Ok("region", "rename", "Same", "Other", "--instance", "B");
        Assert.Equal("Other", f.Instances.Regions("B").Single().Name);
    }

    [Fact]
    public async Task ListShowsInstancesWithTheirRegions()
    {
        using var f = await WithInstances("A");
        await f.Ok("region", "add", "Welcome", "--instance", "A", "--location", "1000,1000");
        var list = await f.Ok("list");
        Assert.Contains("A  (build b1, standalone, mariadb, port 9000)", list);
        Assert.Contains("Welcome", list);

        var json = System.Text.Json.JsonDocument.Parse(await f.Ok("region", "list", "--json"));
        Assert.Equal("Welcome", json.RootElement[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Welcome Island", true)]
    [InlineData("Jak's Place (2)", true)]
    [InlineData(" Leading", false)]
    [InlineData("Bad]Name", false)]
    [InlineData("Semi;colon", false)]
    public void RegionNames(string name, bool valid)
    {
        if (valid)
            Assert.Equal(name, RegionDefinition.CheckName(name));
        else
            Assert.Throws<OsctlException>(() => RegionDefinition.CheckName(name));
    }
}
