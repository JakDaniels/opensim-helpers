// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.ConfigSets;
using Osctl.Instances;

namespace Osctl.Tests.Instances;

/// <summary>Grids of osctl's own (PLAN.md "Robust and local grids"): grid add, its config set, generation, regions.</summary>
public class GridTests
{
    private static async Task<InstanceFixture> WithGrid()
    {
        var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "Password", "dbpass");
        await f.Ok("grid", "add", "MyGrid", "--host", "grid.example.org", "--name", "My Grid");
        return f;
    }

    [Fact]
    public async Task GridAddMakesARobustInstanceAConfigSetAndDatabases()
    {
        using var f = await WithGrid();

        var robust = f.Instances.Load("MyGrid");
        Assert.True(robust.IsRobust);
        Assert.Null(robust.Estate);
        Assert.Equal(new GridDefinition("grid.example.org", 8002, 8003, "My Grid", "mygrid"), robust.Grid);
        Assert.Equal(Grids.RobustConfigSet, robust.ConfigSet);
        Assert.Contains("opensim_MyGrid", f.Databases.Existing);
        Assert.Contains("opensim_estate_MyGrid", f.Databases.Existing);

        var set = new ConfigSetStore(f.Paths).Load("MyGrid");
        Assert.Equal(ConfigSetMode.Grid, set.Mode);
        Assert.Equal("MyGrid", set.Grid);
        Assert.Equal("MyGrid", set.Robust);

        // The next grid takes the next free pair, skipping a port in use on the machine.
        f.Ports.Busy.Add(8005);
        await f.Ok("grid", "add", "Other", "--host", "10.0.0.5");
        Assert.Equal((8006, 8007), (f.Instances.Load("Other").GridSettings.PublicPort, f.Instances.Load("Other").GridSettings.PrivatePort));
        Assert.Contains("http://grid.example.org:8002/", await f.Ok("grid", "list"));
    }

    [Theory]
    [InlineData("--host", "not a host")]
    [InlineData("--public-port", "8003")]
    public async Task GridAddRefusesBadValues(string option, string value)
    {
        using var f = await WithGrid();
        var args = new List<string> { "grid", "add", "Second", "--host", "h.example.org" };
        if (option == "--host")
            args[4] = value;
        else
            args.AddRange([option, value]);
        var (exit, _, err) = await f.Osctl([.. args]);
        Assert.Equal(2, exit);
        Assert.Contains("osctl:", err);
    }

    [Fact]
    public async Task RobustGetsTheGridsHostPortsDatabaseAndName()
    {
        using var f = await WithGrid();
        var g = f.Generator().Generate("MyGrid");
        var c = g.OpenSim;

        Assert.Equal("Robust.ini", g.MainFile);
        Assert.Empty(g.Regions.Sections);
        Assert.Equal("grid.example.org", c.Get("Const", "BaseHostname"));
        Assert.Equal("8002", c.Get("Const", "PublicPort"));
        Assert.Equal("8003", c.Get("Const", "PrivatePort"));
        Assert.Equal("My Grid", c.Get("GridInfoService", "gridname"));
        Assert.Equal("mygrid", c.Get("GridInfoService", "gridnick"));
        Assert.Contains("Database=opensim_MyGrid;", c.Get("DatabaseService", "ConnectionString"));
        Assert.Equal(f.Instances.PidFile("MyGrid"), c.Get("Startup", "PIDFile"));
        // The shipped config set's edits: groups and offline IM on.
        Assert.Contains("GroupsServiceRobustConnector", c.Get("ServiceList", "GroupsServiceConnector"));
        Assert.Contains("OfflineIMServiceRobustConnector", c.Get("ServiceList", "OfflineIMServiceConnector"));
        // FSAssets, with its files in the instance's data/.
        Assert.Contains("FSAssetConnector", c.Get("AssetService", "LocalServiceModule"));
        Assert.Contains("HGFSAssetService", c.Get("HGAssetService", "LocalServiceModule"));
        Assert.Equal(Path.Combine(f.Instances.DataDir("MyGrid"), "fsassets", "data"), c.Get("AssetService", "BaseDirectory"));
        Assert.Equal(Path.Combine(f.Instances.DataDir("MyGrid"), "fsassets", "tmp"), c.Get("AssetService", "SpoolDirectory"));
        Assert.Contains(g.Warnings, w => w.Contains("no default region"));
        Assert.Contains(Path.Combine(f.Instances.LogDir("MyGrid"), "Robust.log"), g.Log4Net);

        var command = new global::Osctl.Running.InstanceRunner(f.Paths, global::Osctl.Settings.SettingsLoader.Load(f.Paths).Settings, f.Runner, f.Admin, f.Ports,
            TextWriter.Null, TimeProvider.System).Command(g, "/gen");
        Assert.Equal(["dotnet", "Robust.dll", "-inifile=/gen/Robust.ini", "-logconfig=/gen/log4net.config"], command);
    }

    [Fact]
    public async Task ASimulatorOnTheGridUsesItAndItsFirstRegionIsTheDefault()
    {
        using var f = await WithGrid();
        var added = await f.Ok("instance", "add", "Sim1", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        Assert.Contains("osctl user create MyGrid \"Grid Owner\"", added);
        var region = await f.Ok("region", "add", "Welcome", "--instance", "Sim1", "--location", "1000,1000");
        Assert.Contains("default region", region);
        Assert.Equal("Welcome", f.Instances.Load("MyGrid").GridSettings.DefaultRegion);

        var c = f.Generator().Generate("Sim1").OpenSim;
        Assert.Equal("grid.example.org", c.Get("Const", "BaseHostname"));
        Assert.Equal("8002", c.Get("Const", "PublicPort"));
        Assert.Equal("http://127.0.0.1", c.Get("Const", "PrivURL"));
        Assert.Equal("8003", c.Get("Const", "PrivatePort"));
        Assert.Equal("RemoteAssetServicesConnector", c.Get("Modules", "AssetServices"));   // GridHypergrid.ini
        Assert.Equal("Groups Module V2", c.Get("Groups", "Module"));
        Assert.Null(c.Get("Estates", "DefaultEstateOwnerPassword"));                      // the owner exists on the grid
        Assert.Contains("Database=opensim_estate_MyGrid;", c.Get("DatabaseService", "EstateConnectionString"));

        var uuid = f.Instances.Regions("Sim1")[0].Uuid;
        var robust = f.Generator().Generate("MyGrid");
        Assert.Equal(ConfigGenerator.DefaultRegionFlags, robust.OpenSim.Get("GridService", $"Region_{uuid}"));
        Assert.DoesNotContain(robust.Warnings, w => w.Contains("default region"));

        // Renaming the default region keeps it; removing it leaves the grid without one.
        await f.Ok("region", "rename", "Welcome", "Arrivals");
        Assert.Equal("Arrivals", f.Instances.Load("MyGrid").GridSettings.DefaultRegion);
        await f.Ok("region", "remove", "Arrivals", "--yes");
        Assert.Null(f.Instances.Load("MyGrid").GridSettings.DefaultRegion);
    }

    [Fact]
    public async Task GridsAndRegionsDoNotShareCommandsOrPorts()
    {
        using var f = await WithGrid();
        var (exit, _, err) = await f.Osctl("region", "add", "R", "--instance", "MyGrid", "--location", "1,1");
        Assert.Equal(2, exit);
        Assert.Contains("Robust", err);

        (exit, _, err) = await f.Osctl("instance", "add", "X", "--configset", Grids.RobustConfigSet, "--estate-owner", "A B");
        Assert.Equal(2, exit);
        Assert.Contains("grid add", err);

        await f.Ok("instance", "add", "Sim1", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        (exit, _, err) = await f.Osctl("region", "add", "R", "--instance", "Sim1", "--location", "1,1", "--port", "8002");
        Assert.Equal(2, exit);
        Assert.Contains("8002", err);
    }

    [Fact]
    public async Task AGridIsDeletedOnlyWithoutSimulators()
    {
        using var f = await WithGrid();
        await f.Ok("instance", "add", "Sim1", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        var (exit, _, err) = await f.Osctl("instance", "delete", "MyGrid", "--yes");
        Assert.Equal(2, exit);
        Assert.Contains("Sim1", err);

        await f.Ok("region", "add", "Welcome", "--instance", "Sim1", "--location", "1000,1000");
        Assert.Contains("default region", await f.Ok("instance", "delete", "Sim1", "--yes"));
        Assert.Null(f.Instances.Load("MyGrid").GridSettings.DefaultRegion);
        await f.Ok("instance", "delete", "MyGrid", "--yes");
        Assert.False(new ConfigSetStore(f.Paths).Exists("MyGrid"));
        Assert.DoesNotContain("opensim_MyGrid", f.Databases.Existing);
        Assert.DoesNotContain("opensim_estate_MyGrid", f.Databases.Existing);
    }

    [Fact]
    public async Task GridSetChangesTheGridAndChecksTheDefaultRegion()
    {
        using var f = await WithGrid();
        await f.Ok("grid", "set", "MyGrid", "--host", "new.example.org", "--nick", "mine", "--assets-dir", "/srv/assets/");
        Assert.Equal("/srv/assets/data", f.Generator().Generate("MyGrid").OpenSim.Get("AssetService", "BaseDirectory"));
        Assert.Equal(("new.example.org", "mine"), (f.Instances.Load("MyGrid").GridSettings.Host, f.Instances.Load("MyGrid").GridSettings.Nick));

        var (exit, _, err) = await f.Osctl("grid", "set", "MyGrid", "--default-region", "Nowhere");
        Assert.Equal(2, exit);
        Assert.Contains("no region 'Nowhere'", err);
    }

    [Fact]
    public async Task UsersNeedARunningGridOrStandalone()
    {
        using var f = await WithGrid();
        await f.Ok("instance", "add", "Sim1", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        // A simulator on the grid means its grid; it is not running here (no tmux answers).
        var (exit, _, err) = await f.Osctl("user", "create", "Sim1", "Grid Owner");
        Assert.Equal(2, exit);
        Assert.Contains("'MyGrid' is not running", err);

        (exit, _, err) = await f.Osctl("user", "create", "MyGrid", "Bad.Name Here");
        Assert.Equal(2, exit);
        Assert.Contains("Hypergrid", err);
    }
}
