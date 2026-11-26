// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Builds;
using Osctl.Instances;
using Osctl.Running;
using Osctl.Settings;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Running;

public class SupervisorTests
{
    private const string Show = "systemctl show osctl@Test1.service --timestamp=unix --property=";

    private static SystemdSupervisor Systemd(FakeRunner runner, InstanceFixture f, bool user = false) =>
        new(runner, new InstanceStore(f.Paths), user);

    [Theory]
    [InlineData("active", "running", "success", InstanceState.Running)]
    [InlineData("activating", "start", "success", InstanceState.Starting)]
    [InlineData("deactivating", "stop", "success", InstanceState.Stopping)]
    [InlineData("inactive", "dead", "success", InstanceState.Stopped)]
    [InlineData("failed", "failed", "exit-code", InstanceState.Failed, 0)]
    [InlineData("failed", "failed", "exit-code", InstanceState.Broken, 4)]
    [InlineData("failed", "failed", "start-limit-hit", InstanceState.Broken)]
    public async Task SystemdStatesMapToInstanceStates(string active, string sub, string result, InstanceState expected, int restarts = 2)
    {
        using var f = new InstanceFixture();
        var runner = new FakeRunner().AnswerPrefix(Show,
            $"LoadState=loaded\nActiveState={active}\nSubState={sub}\nResult={result}\nUnitFileState=enabled\nNRestarts={restarts}\nActiveEnterTimestamp=@1795000000\n");

        var status = await Systemd(runner, f).StatusAsync("Test1", default);

        Assert.Equal(expected, status.State);
        Assert.True(status.StartsAtBoot);
        Assert.Equal(restarts, status.Restarts);
        if (expected == InstanceState.Running)
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795000000), status.Since);
    }

    [Fact]
    public async Task StartingABrokenUnitResetsItFirst()
    {
        using var f = new InstanceFixture();
        var runner = new FakeRunner()
            .AnswerPrefix(Show, "LoadState=loaded\nActiveState=failed\nResult=start-limit-hit\n")
            .Answer("systemctl reset-failed osctl@Test1.service", "")
            .Answer("systemctl start osctl@Test1.service", "");

        await Systemd(runner, f).StartAsync("Test1", default);

        Assert.Equal(["systemctl reset-failed osctl@Test1.service", "systemctl start osctl@Test1.service"],
            runner.Calls.Where(c => !c.StartsWith(Show)));
    }

    [Fact]
    public async Task UserUnitsUseSystemctlUser()
    {
        using var f = new InstanceFixture();
        var runner = new FakeRunner().Answer("systemctl --user enable osctl@Test1.service", "");
        await Systemd(runner, f, user: true).SetBootAsync("Test1", true, default);
        Assert.Equal(["systemctl --user enable osctl@Test1.service"], runner.Calls);
    }

    [Fact]
    public async Task AMissingUnitSaysHowToInstallIt()
    {
        using var f = new InstanceFixture();
        var runner = new FakeRunner().AnswerPrefix("systemctl --user show", "LoadState=not-found\nActiveState=inactive\n");
        var e = await Assert.ThrowsAsync<OsctlException>(() => Systemd(runner, f, user: true).StatusAsync("Test1", default));
        Assert.Contains("osctl units install --user", e.Message);
    }

    [Fact]
    public async Task AFailedStartPointsAtTheLogs()
    {
        using var f = new InstanceFixture();
        var runner = new FakeRunner()
            .AnswerPrefix(Show, "LoadState=loaded\nActiveState=inactive\n")
            .Answer("systemctl start osctl@Test1.service", "", exitCode: 1);
        var e = await Assert.ThrowsAsync<OsctlException>(() => Systemd(runner, f).StartAsync("Test1", default));
        Assert.Contains("'osctl logs Test1' shows why", e.Message);
    }

    [Fact]
    public void TheUnitCarriesOsctlsSettings()
    {
        using var f = new InstanceFixture();
        var settings = SettingsLoader.Load(f.Paths).Settings;

        var system = Units.Template(settings, new OsctlPaths("/"), user: false, ["/usr/bin/osctl"]);
        Assert.Contains("ExecStart=/usr/bin/osctl run %i\n", system);
        Assert.Contains("ExecStop=/usr/bin/osctl run-stop %i\n", system);
        Assert.Contains("User=opensim\nGroup=opensim\nExecStart", system);
        Assert.Contains("Type=notify\n", system);
        Assert.Contains("StartLimitIntervalSec=300\nStartLimitBurst=4\n", system);
        Assert.Contains("TimeoutStartSec=240\n", system);
        Assert.Contains("RestartSec=10\n", system);
        Assert.Contains("LimitSTACK=1G\n", system);
        Assert.Contains("WantedBy=multi-user.target", system);

        var user = Units.Template(settings, f.Paths, user: true, ["dotnet", "/x/osctl.dll"]);
        Assert.DoesNotContain("User=", user);
        Assert.Contains($"ExecStart=dotnet /x/osctl.dll --root {f.Paths.Root} run %i", user);
        Assert.Contains("WantedBy=default.target", user);
    }

    [Fact]
    public void PruningKeepsReleasesInUse()
    {
        using var f = new InstanceFixture();
        var store = new BuildStore(f.Paths);
        foreach (var r in new[] { "r0", "r2", "r3" })
            Directory.CreateDirectory(Path.Combine(store.ReleaseDir("b1", r), "bin"));
        store.SetCurrent("b1", "r3");

        Assert.Equal(["r1", "r2"], store.Prune("b1", keep: 1, inUse: ["r0"]));
        Assert.Equal(["r0", "r3"], store.Releases("b1"));
    }
}

public class ConsoleAndRemoteAdminTests
{
    [Fact]
    public void ConsoleOutputIsBetweenTheEchoAndTheNextPrompt()
    {
        var raw = "Region (root) # show regions\r\n\u001b[33mName  ID\u001b[0m\r\nWelcome  abc\r\nRegion (root) # ";
        Assert.Equal("Name  ID\nWelcome  abc", ConsoleCommand.Output(raw, "show regions"));
        Assert.Null(ConsoleCommand.Output("Region (root) # show regions\r\nName  ID\r\n", "show regions"));
        Assert.Null(ConsoleCommand.Output("other output\r\n", "show regions"));
        Assert.Equal("Password reset for user A B", ConsoleCommand.Output("R.O.B.U.S.T.# reset user password A B\r\nNew password: \r\nPassword reset for user A B\r\nR.O.B.U.S.T.# ", "reset user password A B")?.Replace("New password:\n", ""));
    }

    [Fact]
    public void RemoteAdminRequestsAndReplies()
    {
        var request = HttpRemoteAdmin.Request("admin_shutdown", new Dictionary<string, string> { ["password"] = "a<b&c" });
        Assert.Contains("<methodName>admin_shutdown</methodName>", request);
        Assert.Contains("<name>password</name><value><string>a&lt;b&amp;c</string></value>", request);

        Assert.True(HttpRemoteAdmin.Succeeded("<methodResponse><params><param><value><struct><member><name>accepted</name><value><boolean>1</boolean></value></member></struct></value></param></params></methodResponse>"));
        Assert.True(HttpRemoteAdmin.Succeeded("<methodResponse><params><param><value><struct><member><name>success</name><value><boolean>true</boolean></value></member></struct></value></param></params></methodResponse>"));
        Assert.False(HttpRemoteAdmin.Succeeded("<methodResponse><fault><value><struct/></value></fault></methodResponse>"));
        Assert.False(HttpRemoteAdmin.Succeeded("not xml"));

        // Methods that report only failures: an empty struct is success.
        static string Reply(string members) => $"<methodResponse><params><param><value><struct>{members}</struct></value></param></params></methodResponse>";
        Assert.Null(HttpRemoteAdmin.Failure(Reply("")));
        Assert.Null(HttpRemoteAdmin.Failure(Reply("<member><name>saved</name><value><boolean>1</boolean></value></member>")));
        Assert.Equal("Region Nowhere not found", HttpRemoteAdmin.Failure(Reply(
            "<member><name>success</name><value><boolean>0</boolean></value></member><member><name>error</name><value><string>Region Nowhere not found</string></value></member>")));
        Assert.Equal("RemoteAdmin refused", HttpRemoteAdmin.Failure(Reply("<member><name>accepted</name><value><boolean>0</boolean></value></member>")));
        Assert.NotNull(HttpRemoteAdmin.Failure("not xml"));
    }

    [Fact]
    public void RunStateRoundTrips()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "x.state");
        var state = new RunState(123, 456, "20261122-120000-abc", new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 22, 12, 0, 5, TimeSpan.Zero), Stopping: true);
        state.Save(path);
        Assert.Equal(state, RunState.Load(path));
        Assert.Null(RunState.Load(Path.Combine(dir.Path, "missing")));
    }

    [Theory]
    [InlineData("systemd", SupervisorMode.Systemd)]
    [InlineData("systemd-user", SupervisorMode.SystemdUser)]
    [InlineData("direct", SupervisorMode.Direct)]
    public void SupervisorModes(string text, SupervisorMode mode)
    {
        using var f = new InstanceFixture();
        Osctl.Ini.IniFileEditor.SetValue(f.Paths.SettingsFile, "Supervisor", "Mode", text);
        Assert.Equal(mode, SettingsLoader.Load(f.Paths).Settings.Supervisor.Mode);
    }
}
