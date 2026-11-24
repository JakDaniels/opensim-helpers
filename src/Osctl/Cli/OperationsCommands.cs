// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Osctl.Instances;
using Osctl.Operations;
using Osctl.Running;
using Osctl.Settings;

namespace Osctl.Cli;

/// <summary>
/// Operations (PLAN.md "Backups and scheduled restarts"): <c>visitors</c>, <c>backup create|list|prune|iar-password</c>
/// and <c>schedule restart|backup|remove|list</c>.
/// </summary>
internal static class OperationsCommands
{
    public static IEnumerable<Command> Create(CliEnvironment env, GlobalOptions globals) =>
        [VisitorsCommand(env, globals), BackupCommand(env, globals), ScheduleCommand(env, globals)];

    private static Argument<string[]> Names(string description) =>
        new("names") { Description = description, Arity = ArgumentArity.ZeroOrMore };

    private static Argument<string> InstanceName() => new("instance") { Description = "The instance" };

    private static IReadOnlyList<string> Selected(InstanceStore store, string[]? given)
    {
        var names = given is { Length: > 0 } ? given : [.. store.Names()];
        foreach (var name in names)
            store.Load(name);
        return names;
    }

    // ---------------------------------------------------------------- visitors

    private static Command VisitorsCommand(CliEnvironment env, GlobalOptions globals)
    {
        var names = Names("The instances (default: all)");
        var since = new Option<string?>("--since") { Description = "Only arrivals since a date (2026-11-01, '2026-11-01 18:00') or for a time back (7d, 12h)" };
        var csv = new Option<bool>("--csv") { Description = "Print CSV" };
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("visitors", "Who arrived in the regions, and when, from the instances' logs") { names, since, csv, json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            DateTime? from = parse.GetValue(since) is { } text ? Since(text, env.Clock) : null;
            var visits = Selected(store, parse.GetValue(names))
                .SelectMany(n => Visitors.Read(n, Visitors.LogFiles(store.LogDir(n))))
                .Where(v => from is null || v.Time >= from)
                .OrderBy(v => v.Time)
                .ToList();

            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(visits.Select(v => new
                {
                    Time = v.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), v.Instance, v.Region, v.Name, v.How, v.Ip,
                }), CliApp.Json));
                return 0;
            }
            if (parse.GetValue(csv))
            {
                await env.Out.WriteLineAsync("time,instance,region,name,how,ip");
                foreach (var v in visits)
                    await env.Out.WriteLineAsync(string.Join(',', new[] { v.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), v.Instance, v.Region, v.Name, v.How, v.Ip ?? "" }.Select(Csv)));
                return 0;
            }
            if (visits.Count == 0)
            {
                await env.Out.WriteLineAsync("No arrivals in the logs.");
                return 0;
            }
            Table.Write(env.Out, ["TIME", "INSTANCE", "REGION", "NAME", "HOW", "IP"],
                visits.Select(v => (IReadOnlyList<string>)
                    [v.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), v.Instance, v.Region, v.Name, v.How, v.Ip ?? "-"]));
            return 0;
        });
        return command;
    }

    private static DateTime Since(string text, TimeProvider time)
    {
        if (char.IsDigit(text[^1]) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        return time.GetLocalNow().DateTime - Durations.Parse(text);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // ---------------------------------------------------------------- backup

    private static Command BackupCommand(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("backup", "Back up instances: OARs of their regions, their databases, and inventories (IARs)");
        command.Add(BackupCreate(env, globals));
        command.Add(BackupList(env, globals));
        command.Add(BackupPrune(env, globals));
        command.Add(BackupIarPassword(env, globals));
        return command;
    }

    private static Command BackupCreate(CliEnvironment env, GlobalOptions globals)
    {
        var names = Names("The instances (default: all)");
        var oar = new Option<bool>("--oar") { Description = "OARs of the regions" };
        var db = new Option<bool>("--db") { Description = "The databases" };
        var iar = new Option<string[]>("--iar") { Description = "The inventory of this user (\"First Last\"; repeat for more)", AllowMultipleArgumentsPerToken = false };
        var scheduled = new Option<bool>("--scheduled")
        {
            Description = "What the backup timer runs: OARs, databases and instance.ini [Backups] Iar users, then prune",
        };
        var command = new Command("create",
            "Make a backup now. With none of --oar, --db, --iar: OARs, databases and the IARs instance.ini [Backups] lists")
        { names, oar, db, iar, scheduled };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var settings = globals.Settings(parse);
            var store = new InstanceStore(paths);
            var users = parse.GetValue(iar) ?? [];
            foreach (var user in users)
                InstanceDefinition.CheckOwner(user);
            var picked = parse.GetValue(oar) || parse.GetValue(db) || users.Length > 0;
            var failures = 0;
            foreach (var name in Selected(store, parse.GetValue(names)))
            {
                var instance = store.Load(name);
                var contents = picked && !parse.GetValue(scheduled)
                    ? new BackupContents(parse.GetValue(oar), parse.GetValue(db), users)
                    : new BackupContents(true, true, [.. instance.BackupOptions.IarUsers, .. users]);
                if (globals.IsDryRun(parse))
                {
                    var what = new List<string>();
                    if (contents.Oars)
                        what.Add("OARs");
                    if (contents.Databases)
                        what.Add("databases");
                    what.AddRange(contents.IarUsers.Select(u => $"IAR of {u}"));
                    await env.Out.WriteLineAsync($"would back up {name} ({string.Join(", ", what)}) to {new BackupStore(paths, settings).Root(name)}");
                    continue;
                }

                var runner = new BackupRunner(paths, settings, env.Runner, env.Admin, env.DatabaseFactory, env.Out, env.Clock);
                var result = await runner.RunAsync(name, contents, ct);
                foreach (var item in result.Items.Where(i => !i.Ok))
                    await env.Error.WriteLineAsync($"osctl: {name}: {item.File}: {item.Error}");
                await env.Out.WriteLineAsync(result.Complete
                    ? $"{name}: backed up to {result.Dir} ({result.Items.Count} files)"
                    : $"{name}: backup INCOMPLETE in {result.Dir}");
                if (!result.Complete)
                    failures++;
                else if (parse.GetValue(scheduled))
                    await PruneAsync(env, new BackupStore(paths, settings), settings, instance, null, null, dryRun: false);
            }
            return failures == 0 ? 0 : CliApp.ExitFailure;
        });
        return command;
    }

    private static Command BackupList(CliEnvironment env, GlobalOptions globals)
    {
        var names = Names("The instances (default: all)");
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("list", "List the backups") { names, json };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var backups = new BackupStore(paths, globals.Settings(parse));
            var all = Selected(new InstanceStore(paths), parse.GetValue(names)).SelectMany(backups.List).ToList();
            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(all.Select(b => new
                {
                    b.Instance, b.Name, b.Dir, b.Complete, b.Bytes,
                    Items = b.Items.Select(i => new { i.File, i.What, i.Error }),
                }), CliApp.Json));
                return 0;
            }
            if (all.Count == 0)
            {
                await env.Out.WriteLineAsync("No backups yet: 'osctl backup create <instance>' makes one.");
                return 0;
            }
            Table.Write(env.Out, ["INSTANCE", "BACKUP", "SIZE", "COMPLETE", "CONTENTS"],
                all.Select(b => (IReadOnlyList<string>)
                [
                    b.Instance, b.Name, Size(b.Bytes), b.Complete ? "yes" : "NO",
                    string.Join(", ", b.Items.GroupBy(i => i.What).Select(g => $"{g.Count()} {g.Key}")),
                ]));
            return 0;
        });
        return command;
    }

    private static Command BackupPrune(CliEnvironment env, GlobalOptions globals)
    {
        var names = Names("The instances (default: all)");
        var daily = new Option<int?>("--keep-daily") { Description = "Keep the newest backup of each of the last N days (default: instance.ini, then osctl.ini [Backups])" };
        var weekly = new Option<int?>("--keep-weekly") { Description = "Keep the newest backup of each of the last N weeks" };
        var command = new Command("prune", "Remove old backups, keeping the newest of each recent day and week") { names, daily, weekly };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var paths = globals.Paths(parse);
            var settings = globals.Settings(parse);
            var store = new InstanceStore(paths);
            foreach (var name in Selected(store, parse.GetValue(names)))
                await PruneAsync(env, new BackupStore(paths, settings), settings, store.Load(name), parse.GetValue(daily), parse.GetValue(weekly), globals.IsDryRun(parse));
            return 0;
        });
        return command;
    }

    private static async Task PruneAsync(CliEnvironment env, BackupStore backups, OsctlSettings settings, InstanceDefinition instance,
        int? keepDaily, int? keepWeekly, bool dryRun)
    {
        var daily = keepDaily ?? instance.BackupOptions.KeepDaily ?? settings.Backups.KeepDaily;
        var weekly = keepWeekly ?? instance.BackupOptions.KeepWeekly ?? settings.Backups.KeepWeekly;
        var prune = BackupStore.ToPrune(backups.List(instance.Name), daily, weekly);
        foreach (var backup in prune)
        {
            if (!dryRun)
                BackupStore.Delete(backup);
            await env.Out.WriteLineAsync($"{instance.Name}: {(dryRun ? "would remove" : "removed")} backup {backup.Name}{(backup.Complete ? "" : " (incomplete)")}");
        }
    }

    private static Command BackupIarPassword(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var user = new Argument<string>("user") { Description = "The user, \"First Last\"" };
        var command = new Command("iar-password",
            "Store the password an IAR backup of a user needs (asked for, or one line on stdin), in the instance's secrets.ini")
        { name, user };
        CliApp.SetAction(command, env, async (parse, _) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var instance = store.Load(parse.GetRequiredValue(name));
            var who = InstanceDefinition.CheckOwner(parse.GetRequiredValue(user).Trim());
            var password = env.ReadSecret($"Password of {who}: ");
            if (password.Length == 0)
                throw new OsctlException("No password given");
            if (password.Contains('"'))
                throw new OsctlException("OpenSim's console cannot take a password with \" in it");
            if (globals.IsDryRun(parse))
            {
                await env.Out.WriteLineAsync($"would store the IAR password of {who} in {store.SecretsFile(instance.Name)}");
                return 0;
            }
            store.SetIarPassword(instance.Name, who, password);
            await env.Out.WriteLineAsync($"Stored the IAR password of {who} for instance '{instance.Name}'.");
            return 0;
        });
        return command;
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1}G"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F1}M"
        : bytes >= 1L << 10 ? $"{bytes / 1024.0:F0}K"
        : $"{bytes}B";

    // ---------------------------------------------------------------- schedule

    private static Command ScheduleCommand(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("schedule", "Scheduled restarts (with in-world warnings) and backups, as systemd timers");
        command.Add(ScheduleSet(env, globals, ScheduledJob.Restart));
        command.Add(ScheduleSet(env, globals, ScheduledJob.Backup));
        command.Add(ScheduleRemove(env, globals));
        command.Add(ScheduleList(env, globals));
        return command;
    }

    private static Command ScheduleSet(CliEnvironment env, GlobalOptions globals, ScheduledJob job)
    {
        var name = InstanceName();
        var when = new Argument<string>("when") { Description = "A systemd calendar expression: 'Sun 04:00', '*-*-* 03:30', 'Mon,Thu 05:00', 'daily'" };
        var warn = new Option<string?>("--warn") { Description = "Warn the people in the regions this long before (e.g. 15m; default: osctl.ini [Schedule] WarnMinutes)" };
        var iar = new Option<string[]>("--iar") { Description = "Also save this user's inventory (\"First Last\"; repeat for more)" };
        var daily = new Option<int?>("--keep-daily") { Description = "Keep the newest backup of each of the last N days" };
        var weekly = new Option<int?>("--keep-weekly") { Description = "Keep the newest backup of each of the last N weeks" };
        var command = job == ScheduledJob.Restart
            ? new Command("restart", "Restart an instance on a schedule, warning the people in its regions first") { name, when, warn }
            : new Command("backup", "Back an instance up on a schedule (OARs, databases, IARs), pruning old backups after") { name, when, iar, daily, weekly };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var instance = store.Load(parse.GetRequiredValue(name));
            var calendar = parse.GetRequiredValue(when).Trim();
            if (calendar.Length == 0 || calendar.Contains('\n'))
                throw new OsctlException("Give a calendar expression, e.g. 'Sun 04:00'");
            var next = await Schedules.NextAsync(env.Runner, calendar, ct);

            var updated = job == ScheduledJob.Restart
                ? instance with
                {
                    Schedule = instance.Scheduled with
                    {
                        Restart = calendar,
                        WarnMinutes = parse.GetValue(warn) is { } w ? (int)Math.Ceiling(Durations.Parse(w).TotalMinutes) : instance.Scheduled.WarnMinutes,
                    },
                }
                : instance with
                {
                    Schedule = instance.Scheduled with { Backup = calendar },
                    Backups = new InstanceBackups(
                        parse.GetValue(iar) is { Length: > 0 } users ? [.. users.Select(u => InstanceDefinition.CheckOwner(u.Trim()))] : instance.BackupOptions.IarUsers,
                        parse.GetValue(daily) ?? instance.BackupOptions.KeepDaily,
                        parse.GetValue(weekly) ?? instance.BackupOptions.KeepWeekly),
                };
            return await ApplyAsync(env, globals, parse, store, updated,
                $"{instance.Name}: {Schedules.Kind(job)} at '{calendar}'{(next is null ? "" : $", next {next}")}", ct);
        });
        return command;
    }

    private static Command ScheduleRemove(CliEnvironment env, GlobalOptions globals)
    {
        var name = InstanceName();
        var job = new Argument<string>("job") { Description = "restart or backup" };
        job.AcceptOnlyFromAmong("restart", "backup");
        var command = new Command("remove", "Stop restarting or backing up an instance on a schedule") { name, job };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var store = new InstanceStore(globals.Paths(parse));
            var instance = store.Load(parse.GetRequiredValue(name));
            var restart = parse.GetRequiredValue(job) == "restart";
            var updated = instance with
            {
                Schedule = restart ? instance.Scheduled with { Restart = null } : instance.Scheduled with { Backup = null },
            };
            return await ApplyAsync(env, globals, parse, store, updated, $"{instance.Name}: no scheduled {parse.GetRequiredValue(job)}", ct);
        });
        return command;
    }

    /// <summary>Saves the schedule in instance.ini, then puts the timers in place (or says how, if osctl cannot).</summary>
    private static async Task<int> ApplyAsync(CliEnvironment env, GlobalOptions globals, ParseResult parse, InstanceStore store,
        InstanceDefinition updated, string summary, CancellationToken ct)
    {
        var settings = globals.Settings(parse);
        if (globals.IsDryRun(parse))
        {
            await env.Out.WriteLineAsync($"would save in {store.DefinitionFile(updated.Name)}: {summary}");
            return 0;
        }
        store.Save(updated);
        await env.Out.WriteLineAsync(summary);

        if (settings.Supervisor.Mode == SupervisorMode.Direct)
        {
            await WriteCronLinesAsync(env, globals, parse, [updated]);
            return 0;
        }
        var user = settings.Supervisor.Mode == SupervisorMode.SystemdUser;
        try
        {
            foreach (var line in await Schedules.InstallTimersAsync(env.Runner, env.UnitDir(user), user, [updated], removeOthers: false, ct))
                await env.Out.WriteLineAsync("    " + line);
        }
        catch (UnauthorizedAccessException)
        {
            await env.Out.WriteLineAsync("Saved in instance.ini; the timers need root: run 'sudo osctl units install' to put them in place.");
        }
        return 0;
    }

    private static async Task WriteCronLinesAsync(CliEnvironment env, GlobalOptions globals, ParseResult parse, IEnumerable<InstanceDefinition> instances)
    {
        var paths = globals.Paths(parse);
        var self = string.Join(' ', env.Self.Select(Processes.ShellQuote.Quote));
        var root = paths.Root == "/" ? "" : $" --root {Processes.ShellQuote.Quote(paths.Root)}";
        await env.Out.WriteLineAsync("[Supervisor] Mode = direct has no systemd timers; add these lines to your crontab ('crontab -e'):");
        foreach (var instance in instances)
        {
            foreach (var job in Enum.GetValues<ScheduledJob>())
            {
                if (Schedules.Calendar(instance.Scheduled, job) is not { } calendar)
                    continue;
                var command = $"{self}{root} {(job == ScheduledJob.Restart ? "restart" : "backup create")} --scheduled {Processes.ShellQuote.Quote(instance.Name)}";
                await env.Out.WriteLineAsync(Schedules.CronLine(calendar, command)
                    ?? $"# '{calendar}' has no simple cron form: write the five time fields yourself\n# m h dom mon dow {command}");
            }
        }
    }

    private static Command ScheduleList(CliEnvironment env, GlobalOptions globals)
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var cron = new Option<bool>("--cron") { Description = "Print crontab lines for the schedules (for [Supervisor] Mode = direct)" };
        var command = new Command("list", "Show the scheduled restarts and backups") { json, cron };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var settings = globals.Settings(parse);
            var store = new InstanceStore(globals.Paths(parse));
            var instances = store.Names().Select(store.Load).ToList();
            if (parse.GetValue(cron))
            {
                await WriteCronLinesAsync(env, globals, parse, instances);
                return 0;
            }

            var user = settings.Supervisor.Mode == SupervisorMode.SystemdUser;
            var rows = new List<(string Instance, string Job, string When, string Detail, string Timer, string? Next)>();
            foreach (var instance in instances)
            {
                foreach (var job in Enum.GetValues<ScheduledJob>())
                {
                    if (Schedules.Calendar(instance.Scheduled, job) is not { } calendar)
                        continue;
                    var detail = job == ScheduledJob.Restart
                        ? $"warn {instance.Scheduled.WarnMinutes ?? settings.Schedule.WarnMinutes}m"
                        : string.Join(", ", new[] { "oar", "db" }.Concat(instance.BackupOptions.IarUsers.Select(u => $"iar {u}")));
                    string? next;
                    try
                    {
                        next = await Schedules.NextAsync(env.Runner, calendar, ct);
                    }
                    catch (OsctlException)
                    {
                        next = "invalid";
                    }
                    var timer = settings.Supervisor.Mode == SupervisorMode.Direct ? "cron" : await TimerStateAsync(env, user, Schedules.TimerName(job, instance.Name), ct);
                    rows.Add((instance.Name, Schedules.Kind(job), calendar, detail, timer, next));
                }
            }

            if (parse.GetValue(json))
            {
                await env.Out.WriteLineAsync(JsonSerializer.Serialize(rows.Select(r => new { r.Instance, r.Job, r.When, r.Detail, r.Timer, r.Next }), CliApp.Json));
                return 0;
            }
            if (rows.Count == 0)
            {
                await env.Out.WriteLineAsync("Nothing scheduled: 'osctl schedule restart|backup <instance> <when>' adds a schedule.");
                return 0;
            }
            Table.Write(env.Out, ["INSTANCE", "JOB", "WHEN", "NEXT", "TIMER", "DETAIL"],
                rows.Select(r => (IReadOnlyList<string>)[r.Instance, r.Job, r.When, r.Next ?? "-", r.Timer, r.Detail]));
            return 0;
        });
        return command;
    }

    private static async Task<string> TimerStateAsync(CliEnvironment env, bool user, string timer, CancellationToken ct)
    {
        var r = await env.Runner.RunAsync("systemctl", [.. user ? ["--user"] : Array.Empty<string>(), "show", timer, "--property=LoadState,ActiveState"],
            cancellationToken: ct);
        if (!r.Succeeded)
            return "?";
        var p = r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1].Trim());
        return p.GetValueOrDefault("LoadState") == "not-found" ? "not installed" : p.GetValueOrDefault("ActiveState") ?? "?";
    }
}
