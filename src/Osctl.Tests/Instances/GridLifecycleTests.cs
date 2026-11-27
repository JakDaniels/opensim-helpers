// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Tests.Instances;

/// <summary><c>osctl grid start|stop|restart</c>: a grid and its simulators together, in order.</summary>
public class GridLifecycleTests
{
    private static async Task<InstanceFixture> WithGrid()
    {
        var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SecretsFile, "Database", "Password", "dbpass");
        await f.Ok("grid", "add", "MyGrid", "--host", "grid.example.org");
        await f.Ok("instance", "add", "Sim1", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        await f.Ok("region", "add", "Welcome", "--instance", "Sim1", "--location", "1000,1000");
        await f.Ok("instance", "add", "Sim2", "--configset", "MyGrid", "--estate-owner", "Grid Owner");
        await f.Ok("region", "add", "Second", "--instance", "Sim2", "--location", "1002,1000");
        await f.Ok("instance", "add", "Other", "--db", "sqlite", "--estate-owner", "Test User");
        return f;
    }

    /// <summary>MyGrid and Sim1 running, Sim2 stopped; Other (a standalone) running.</summary>
    private static void UnitStates(InstanceFixture f)
    {
        foreach (var (name, state) in new[] { ("MyGrid", "active"), ("Sim1", "active"), ("Sim2", "inactive"), ("Other", "active") })
            f.Runner.AnswerPrefix($"systemctl show osctl@{name}.service",
                $"LoadState=loaded\nActiveState={state}\nSubState=x\nResult=success\nUnitFileState=disabled\nNRestarts=0\nActiveEnterTimestamp=@1795000000\n");
    }

    /// <summary>The systemctl start/stop calls a dry run would make, in order.</summary>
    private static List<string> Calls(string output) =>
        [.. output.Split('\n').Where(l => l.Contains("would run: systemctl")).Select(l => l[(l.IndexOf("systemctl ", StringComparison.Ordinal) + 10)..])];

    [Fact]
    public async Task AGridStartsBeforeItsSimulatorsAndStopsAfterThem()
    {
        using var f = await WithGrid();
        UnitStates(f);

        Assert.Equal(["start osctl@MyGrid.service", "start osctl@Sim1.service", "start osctl@Sim2.service"],
            Calls(await f.Ok("--dry-run", "grid", "start", "MyGrid")));

        var stop = await f.Ok("--dry-run", "grid", "stop", "MyGrid", "--warn", "5m");
        Assert.Contains("would warn Sim1 5 minutes before they shut down", stop);
        Assert.Equal(["stop osctl@Sim1.service", "stop osctl@Sim2.service", "stop osctl@MyGrid.service"], Calls(stop));
        Assert.DoesNotContain("still run on grid", stop);
    }

    [Fact]
    public async Task AGridRestartStopsItsRunningSimulatorsAndStartsThemAgainAfterTheGrid()
    {
        using var f = await WithGrid();
        UnitStates(f);

        var restart = await f.Ok("--dry-run", "grid", "restart", "MyGrid", "--warn", "10m");

        Assert.Contains("would warn Sim1 10 minutes before they restart", restart);
        // Sim2 was stopped, so it stays stopped; Other is not on the grid.
        Assert.Equal(["stop osctl@Sim1.service", "stop osctl@MyGrid.service", "start osctl@MyGrid.service", "start osctl@Sim1.service"],
            Calls(restart));
    }

    [Fact]
    public async Task OnlyAGridIsStartedOrStoppedAsOne()
    {
        using var f = await WithGrid();
        var (exit, _, err) = await f.Osctl("grid", "stop", "Sim1");
        Assert.Equal(2, exit);
        Assert.Contains("'Sim1' is not a grid; 'osctl stop Sim1' stops an instance", err);
        Assert.DoesNotContain("--scheduled", await f.Ok("help", "grid", "restart"));
    }
}
