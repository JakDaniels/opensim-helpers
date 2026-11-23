// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Cli;
using Osctl.Running;

namespace Osctl.Tests.Running;

// Real tmux, fake OpenSim (RunningFixture). Skipped where tmux is not installed.
public class InstanceRunnerTests
{
    [SkippableFact]
    public async Task RunsUntilShutdownAndExitsWithOpenSimsCode()
    {
        Skip.IfNot(RunningFixture.HasTmux(), "tmux is not installed");
        using var f = new RunningFixture();
        var run = f.Runner().RunAsync("Test1", foreground: false, CancellationToken.None);

        var state = await f.WaitReadyAsync(run);
        Assert.True(state.OpenSimPid > 0);
        Assert.Equal("r1", state.Release);
        Assert.Contains("fake OpenSim in", await File.ReadAllTextAsync(f.Store.ConsoleLog("Test1")));
        Assert.Equal("you said: hello there",
            await ConsoleCommand.RunAsync(f.Tmux, f.Store.ConsoleLog("Test1"), "hello there", TimeSpan.FromSeconds(10), TimeProvider.System, default));

        // RemoteAdmin does not answer here, so 'shutdown' is typed into the console.
        Assert.True(await f.Stopper().StopAsync("Test1", force: false, CancellationToken.None));
        Assert.Equal(0, await run);
        Assert.Equal(9000, Assert.Single(f.Admin.Shutdowns).Port);
        Assert.Contains("typing 'shutdown'", f.Log.ToString());
        Assert.False(File.Exists(f.Store.RunStateFile("Test1")));
        Assert.False(await f.Tmux.IsRunningAsync(default));
    }

    [SkippableFact]
    public async Task ACrashIsANonZeroExit()
    {
        Skip.IfNot(RunningFixture.HasTmux(), "tmux is not installed");
        using var f = new RunningFixture();
        var run = f.Runner().RunAsync("Test1", foreground: false, CancellationToken.None);
        await f.WaitReadyAsync(run);

        await f.Tmux.SendLineAsync("crash", default);

        Assert.Equal(3, await run);
        Assert.Contains("ended with exit code 3", f.Log.ToString());
    }

    [SkippableFact]
    public async Task NoReadinessWithinTheStartTimeCarriesOnWithAWarning()
    {
        Skip.IfNot(RunningFixture.HasTmux(), "tmux is not installed");
        using var f = new RunningFixture(ready: false);
        var run = f.Runner().RunAsync("Test1", foreground: false, CancellationToken.None);

        await f.WaitReadyAsync(run);
        Assert.Contains("not every region said LOGINS ENABLED within 3 s", f.Log.ToString());
        await f.Stopper().StopAsync("Test1", force: true, CancellationToken.None);
        Assert.NotEqual(0, await run);
    }

    [Fact]
    public async Task ABusyPortStopsTheStart()
    {
        using var f = new RunningFixture();
        f.Instance.Ports.Busy.Add(9001);
        var e = await Assert.ThrowsAsync<OsctlException>(() => f.Runner().RunAsync("Test1", false, CancellationToken.None));
        Assert.Contains("Port 9001 of instance 'Test1' is in use", e.Message);
    }

    [Fact]
    public async Task AnInstanceWithoutRegionsDoesNotRun()
    {
        using var f = new RunningFixture();
        await f.Instance.Ok("instance", "add", "Empty", "--db", "sqlite", "--estate-owner", "Test User");
        var e = await Assert.ThrowsAsync<OsctlException>(() => f.Runner().RunAsync("Empty", false, CancellationToken.None));
        Assert.Contains("has no regions", e.Message);
    }

    [Fact]
    public void TheCommandLinePointsOpenSimAtTheGeneratedFiles()
    {
        using var f = new RunningFixture();
        var generated = new global::Osctl.Instances.ConfigGenerator(f.Instance.Paths, f.Settings).Generate("Test1");
        var dir = f.Store.GeneratedDir("Test1");
        var command = f.Runner().Command(generated, dir);

        Assert.Equal("sh", command[0]);
        Assert.Contains("-inimaster=none", command);
        Assert.Contains($"-inifile={Path.Combine(dir, "OpenSim.ini")}", command);
        Assert.Contains($"-inidirectory={Path.Combine(dir, "config")}", command);
        Assert.Contains($"-logconfig={Path.Combine(dir, "log4net.config")}", command);
    }

    [SkippableFact]
    public async Task TheDirectSupervisorStartsStopsAndReports()
    {
        Skip.IfNot(RunningFixture.HasTmux(), "tmux is not installed");
        using var f = new RunningFixture();
        // 'osctl start' runs 'osctl run' in the background: this build of osctl, through dotnet.
        var osctl = new[] { "dotnet", typeof(CliApp).Assembly.Location };
        async Task<(int Exit, string Out, string Err)> Osctl(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var env = new CliEnvironment(new global::Osctl.Processes.ProcessRunner(), output, error, Ports: f.Instance.Ports, RemoteAdmin: f.Admin, OsctlCommand: osctl);
            var exit = await CliApp.RunAsync([.. args, "--root", f.Instance.Paths.Root], env);
            return (exit, output.ToString(), error.ToString());
        }

        var start = await Osctl("start", "Test1");
        Assert.True(start.Exit == 0, start.Err + start.Out);
        Assert.Contains("Test1     running", (await Osctl("status")).Out);
        Assert.Equal("you said: show regions", (await Osctl("cmd", "Test1", "show", "regions")).Out.Trim());

        var stop = await Osctl("stop", "Test1");
        Assert.True(stop.Exit == 0, stop.Err + stop.Out);
        Assert.Contains("Test1     stopped", (await Osctl("status")).Out);
        Assert.Contains("ended with exit code 0", (await Osctl("logs", "Test1", "--runner")).Out);
    }
}
