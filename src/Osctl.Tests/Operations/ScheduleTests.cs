// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Operations;
using Osctl.Running;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Operations;

public class ScheduleTests
{
    [Theory]
    [InlineData("Sun 04:00", "0 4 * * 0 osctl x")]
    [InlineData("Mon,Thu 05:30", "30 5 * * 1,4 osctl x")]
    [InlineData("*-*-* 03:15:00", "15 3 * * * osctl x")]
    [InlineData("daily", "0 0 * * * osctl x")]
    [InlineData("weekly", "0 0 * * 1 osctl x")]
    [InlineData("*-*-01 03:00", null)]
    [InlineData("Funday 03:00", null)]
    public void CalendarExpressionsBecomeCronLinesWhenTheyCan(string calendar, string? cron) =>
        Assert.Equal(cron, Schedules.CronLine(calendar, "osctl x"));

    [Fact]
    public void WarningsCountDown()
    {
        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)], Countdown.Marks(TimeSpan.FromMinutes(10)));
        Assert.Equal([TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1)], Countdown.Marks(TimeSpan.FromMinutes(3)));
        Assert.Equal([TimeSpan.FromSeconds(30)], Countdown.Marks(TimeSpan.FromSeconds(30)));
        Assert.Empty(Countdown.Marks(TimeSpan.Zero));
        Assert.Equal("This region will restart in 1 minute. Please find a safe place.", Countdown.Message("restart", TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("10m", 600)]
    [InlineData("10", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void DurationsParse(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Durations.Parse(text));

    [Fact]
    public async Task TheCountdownBroadcastsToTheInstancesThenWaits()
    {
        using var f = new InstanceFixture();
        await f.Ok("instance", "add", "A", "--estate-owner", "Test User");
        await f.Ok("region", "add", "Home", "--instance", "A", "--location", "1000,1000", "--port", "9005");
        f.Admin.Answers = true;
        var started = DateTime.UtcNow;

        await new Countdown(f.Instances, f.Admin, TimeProvider.System, TextWriter.Null).RunAsync(["A"], TimeSpan.FromSeconds(1), "restart", default);

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(0.9));
        Assert.Equal((9005, "This region will restart in 1 second. Please find a safe place."), f.Admin.Broadcasts.Single());
    }

    private static async Task<InstanceFixture> WithInstance(string mode)
    {
        var f = new InstanceFixture();
        global::Osctl.Ini.IniFileEditor.SetValue(f.Paths.SettingsFile, "Supervisor", "Mode", mode);
        await f.Ok("instance", "add", "A", "--estate-owner", "Test User");
        f.Runner.AnswerPrefix("systemd-analyze calendar", "  Original form: x\nNext elapse: Sun 2026-11-29 04:00:00 GMT\n");
        f.Runner.AnswerPrefix("systemctl", "");
        return f;
    }

    [Fact]
    public async Task ScheduleWritesInstanceIniAndTheTimers()
    {
        using var f = await WithInstance("systemd-user");

        var output = await f.Ok("schedule", "restart", "A", "Sun 04:00", "--warn", "15m");
        await f.Ok("schedule", "backup", "A", "*-*-* 03:00", "--iar", "Test User", "--keep-daily", "3");

        Assert.Contains("next Sun 2026-11-29 04:00:00 GMT", output);
        var instance = f.Instances.Load("A");
        Assert.Equal(new InstanceScheduleView("Sun 04:00", 15, "*-*-* 03:00"),
            new InstanceScheduleView(instance.Scheduled.Restart, instance.Scheduled.WarnMinutes, instance.Scheduled.Backup));
        Assert.Equal(["Test User"], instance.BackupOptions.IarUsers);
        Assert.Equal(3, instance.BackupOptions.KeepDaily);
        Assert.Contains("OnCalendar=Sun 04:00", File.ReadAllText(Path.Combine(f.UnitDir, "osctl-restart@A.timer")));
        Assert.Contains("OnCalendar=*-*-* 03:00", File.ReadAllText(Path.Combine(f.UnitDir, "osctl-backup@A.timer")));
        Assert.Contains("systemctl --user enable --now osctl-backup@A.timer", f.Runner.Calls);

        var list = await f.Ok("schedule", "list");
        Assert.Contains("warn 15m", list);
        Assert.Contains("oar, db, iar Test User", list);

        await f.Ok("schedule", "remove", "A", "restart");
        Assert.False(File.Exists(Path.Combine(f.UnitDir, "osctl-restart@A.timer")));
        Assert.Contains("systemctl --user disable --now osctl-restart@A.timer", f.Runner.Calls);
        Assert.Null(f.Instances.Load("A").Scheduled.Restart);
        Assert.Equal("*-*-* 03:00", f.Instances.Load("A").Scheduled.Backup);
    }

    private sealed record InstanceScheduleView(string? Restart, int? Warn, string? Backup);

    [Fact]
    public async Task ABadCalendarExpressionIsRefused()
    {
        using var f = await WithInstance("systemd-user");
        f.Runner.Answer("systemd-analyze calendar Funday", "", exitCode: 1);
        var (exit, _, error) = await f.Osctl("schedule", "restart", "A", "Funday");
        Assert.Equal(2, exit);
        Assert.Contains("is not a systemd calendar expression", error);
        Assert.Null(f.Instances.Load("A").Scheduled.Restart);
    }

    [Fact]
    public async Task DirectModePrintsCronLines()
    {
        using var f = await WithInstance("direct");
        var output = await f.Ok("schedule", "restart", "A", "Sun 04:00");
        Assert.Contains($"0 4 * * 0 /usr/bin/osctl --root {f.Paths.Root} restart --scheduled A", output);
        Assert.False(Directory.Exists(f.UnitDir));
    }

    [Fact]
    public async Task UnitsInstallWritesTheServicesAndEveryInstancesTimers()
    {
        using var f = await WithInstance("systemd-user");
        await f.Ok("schedule", "backup", "A", "daily");
        File.WriteAllText(Path.Combine(f.UnitDir, "osctl-restart@Gone.timer"), "old");

        await f.Ok("units", "install", "--user");

        Assert.True(File.Exists(Path.Combine(f.UnitDir, "osctl@.service")));
        var backup = File.ReadAllText(Path.Combine(f.UnitDir, "osctl-backup@.service"));
        Assert.Contains("backup create --scheduled %i", backup);
        Assert.Contains("TimeoutStartSec=infinity", backup);
        Assert.True(File.Exists(Path.Combine(f.UnitDir, "osctl-backup@A.timer")));
        Assert.False(File.Exists(Path.Combine(f.UnitDir, "osctl-restart@Gone.timer")));
    }

    [Fact]
    public async Task AScheduledRestartLeavesAStoppedInstanceAlone()
    {
        using var f = await WithInstance("direct");
        var (exit, output, _) = await f.Osctl("restart", "--scheduled", "A");
        Assert.Equal(0, exit);
        Assert.Contains("A: not running, so not restarted", output);
        Assert.Empty(f.Admin.Broadcasts);
    }
}
