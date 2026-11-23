// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Security.Cryptography;
using Osctl.Databases;
using Osctl.Ini;
using Osctl.Settings;

namespace Osctl.Cli;

/// <summary><c>osctl db …</c> (PLAN.md "Databases").</summary>
internal static class DbCommands
{
    public const string DefaultSocket = "/run/mysqld/mysqld.sock";
    private const string PasswordCharacters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static Command Create(CliEnvironment env, GlobalOptions globals)
    {
        var command = new Command("db", "The MariaDB/MySQL server instances use");
        command.Add(Init(env, globals));
        return command;
    }

    private static Command Init(CliEnvironment env, GlobalOptions globals)
    {
        var socket = new Option<string>("--socket") { Description = "The server's unix socket", DefaultValueFactory = _ => DefaultSocket };
        var command = new Command("init",
            "Create the database users osctl and OpenSim use (run once, as root: sudo osctl db init)") { socket };
        CliApp.SetAction(command, env, async (parse, ct) =>
        {
            var paths = globals.Paths(parse);
            var db = globals.Settings(parse).Database;
            if (db.Host is not ("localhost" or "127.0.0.1"))
                throw new OsctlException(
                    $"'osctl db init' sets up a server on this machine; for {db.Host}, create the users '{db.AdminUser}' and '{db.User}' " +
                    $"with all rights on `{DatabaseLimits.Pattern(db.DatabasePrefix)}`.* by hand, and put their passwords in {paths.SecretsFile}");
            if (!File.Exists(paths.SecretsFile))
                throw new OsctlException($"{paths.SecretsFile} does not exist: run 'osctl init' first");

            var dryRun = globals.IsDryRun(parse);
            var adminPassword = db.AdminPassword;
            var password = db.Password;
            foreach (var (key, has) in new[] { ("AdminPassword", adminPassword is not null), ("Password", password is not null) })
            {
                if (has)
                    continue;
                var generated = RandomNumberGenerator.GetString(PasswordCharacters, 24);
                if (dryRun)
                    await env.Out.WriteLineAsync($"would generate [Database] {key} in {paths.SecretsFile}");
                else
                    IniFileEditor.SetValue(paths.SecretsFile, "Database", key, generated);
                if (key == "AdminPassword")
                    adminPassword = generated;
                else
                    password = generated;
            }

            var pattern = DatabaseLimits.Pattern(db.DatabasePrefix);
            if (dryRun)
            {
                await env.Out.WriteLineAsync($"would create users '{db.AdminUser}' and '{db.User}' with all rights on `{pattern}`.* (over {parse.GetValue(socket)})");
                return 0;
            }

            var root = env.DatabaseFactory.Root(parse.GetRequiredValue(socket));
            await root.GrantAsync(db.AdminUser, adminPassword!, pattern, ct);
            await root.GrantAsync(db.User, password!, pattern, ct);
            await env.Out.WriteLineAsync($"Users '{db.AdminUser}' and '{db.User}' have all rights on `{pattern}`.*; passwords are in {paths.SecretsFile}.");

            var packet = await root.MaxAllowedPacketAsync(ct);
            if (packet < DatabaseLimits.RecommendedMaxAllowedPacket)
                await env.Out.WriteLineAsync(
                    $"Warning: max_allowed_packet is {packet / (1024 * 1024)} MB; OpenSim needs more for large assets. " +
                    "Set max_allowed_packet = 128M under [mysqld] in /etc/mysql/mariadb.conf.d/50-server.cnf and restart MariaDB.");
            return 0;
        });
        return command;
    }
}
