// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Data.Common;
using System.Globalization;
using MySqlConnector;
using Osctl.Processes;
using Osctl.Settings;

namespace Osctl.Databases;

/// <summary>
/// What osctl does to MariaDB/MySQL (PLAN.md "Databases"), as <c>osctl_admin</c>; a fake in tests.
/// Database names are always quoted, as instance names may contain '-'.
/// </summary>
public interface IDatabaseAdmin
{
    Task<bool> ExistsAsync(string database, CancellationToken ct);

    Task CreateAsync(string database, CancellationToken ct);

    Task DropAsync(string database, CancellationToken ct);

    /// <summary>Dumps a database to a file with mariadb-dump.</summary>
    Task DumpAsync(string database, string file, CancellationToken ct);

    /// <summary>
    /// Replaces a database with a dump (<see cref="DumpAsync"/>'s, gzipped or not): drops it, creates it empty, then
    /// feeds the dump to the mariadb client.
    /// </summary>
    Task LoadAsync(string database, string dumpFile, CancellationToken ct);

    /// <summary>The server's version and max_allowed_packet, to check that osctl can connect.</summary>
    Task<(string Version, long MaxAllowedPacket)> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// Sets up the server for osctl (<c>osctl db init</c>), connected as the server's root account; a fake in tests.
/// </summary>
public interface IDatabaseRoot
{
    /// <summary>Creates (or updates the password of) a user with all rights on databases matching a pattern.</summary>
    Task GrantAsync(string user, string password, string databasePattern, CancellationToken ct);

    Task<long> MaxAllowedPacketAsync(CancellationToken ct);
}

/// <summary>Makes the database objects; replaced by fakes in tests.</summary>
public interface IDatabaseFactory
{
    IDatabaseAdmin Admin(DatabaseSettings settings);

    /// <summary>Root over the unix socket (works when osctl runs as the system root, as Ubuntu's MariaDB allows).</summary>
    IDatabaseRoot Root(string socket);
}

public sealed class MySqlDatabaseFactory(IProcessRunner runner) : IDatabaseFactory
{
    public IDatabaseAdmin Admin(DatabaseSettings settings) => new MySqlAdmin(settings, runner);

    public IDatabaseRoot Root(string socket) => new MySqlRoot(socket);
}

/// <summary>OpenSim wants big packets (assets): 128 MB is the usual advice.</summary>
public static class DatabaseLimits
{
    public const long RecommendedMaxAllowedPacket = 128L * 1024 * 1024;

    /// <summary>A database pattern for GRANT: the prefix with <c>_</c> and <c>%</c> escaped, then <c>%</c>.</summary>
    public static string Pattern(string prefix) => prefix.Replace(@"\", @"\\").Replace("_", @"\_").Replace("%", @"\%") + "%";

    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public static string Literal(string text) => "'" + text.Replace(@"\", @"\\").Replace("'", "''") + "'";
}

internal sealed class MySqlAdmin(DatabaseSettings settings, IProcessRunner runner) : IDatabaseAdmin
{
    public async Task<bool> ExistsAsync(string database, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name", connection);
        command.Parameters.AddWithValue("@name", database);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    public Task CreateAsync(string database, CancellationToken ct) =>
        ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {DatabaseLimits.Quote(database)} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", ct);

    public Task DropAsync(string database, CancellationToken ct) =>
        ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseLimits.Quote(database)}", ct);

    public async Task DumpAsync(string database, string file, CancellationToken ct)
    {
        var credentials = await CredentialsFileAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var result = await runner.RunAsync("mariadb-dump",
                [$"--defaults-extra-file={credentials}", "--single-transaction", "--routines", $"--result-file={file}", database],
                ProcessRunOptions.Change, ct);
            if (result.NotFound)
                throw new OsctlException("mariadb-dump is not installed (sudo apt install mariadb-client)");
            if (!result.Succeeded)
                throw new OsctlException($"mariadb-dump {database} failed: {result.StandardError.Trim()}");
        }
        finally
        {
            File.Delete(credentials);
        }
    }

    public async Task LoadAsync(string database, string dumpFile, CancellationToken ct)
    {
        await DropAsync(database, ct);
        await CreateAsync(database, ct);
        var credentials = await CredentialsFileAsync();
        try
        {
            Stream Open()
            {
                var file = File.OpenRead(dumpFile);
                return dumpFile.EndsWith(".gz", StringComparison.Ordinal) ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress) : file;
            }
            var result = await runner.RunAsync("mariadb",
                [$"--defaults-extra-file={credentials}", $"--max-allowed-packet={DatabaseLimits.RecommendedMaxAllowedPacket}", database],
                new ProcessRunOptions(ChangesSystem: true, Input: Open), ct);
            if (result.NotFound)
                throw new OsctlException("mariadb is not installed (sudo apt install mariadb-client)");
            if (!result.Succeeded)
                throw new OsctlException($"loading {Path.GetFileName(dumpFile)} into {database} failed: {result.StandardError.Trim()}");
        }
        finally
        {
            File.Delete(credentials);
        }
    }

    /// <summary>The password goes in a file only the owner can read, not on the command line (where ps shows it).</summary>
    private async Task<string> CredentialsFileAsync()
    {
        var credentials = Path.Combine(Path.GetTempPath(), $"osctl-{Guid.NewGuid():N}.cnf");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        await using (var writer = new StreamWriter(new FileStream(credentials, options)))
            await writer.WriteAsync($"[client]\nuser={settings.AdminUser}\npassword=\"{settings.AdminPassword}\"\nhost={settings.Host}\nport={settings.Port}\n");
        return credentials;
    }

    public async Task<(string Version, long MaxAllowedPacket)> ProbeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT VERSION(), @@max_allowed_packet", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.AdminPassword))
            throw new OsctlException("No database admin password in secrets.ini: run 'sudo osctl db init' first");
        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Host,
            Port = (uint)settings.Port,
            UserID = settings.AdminUser,
            Password = settings.AdminPassword,
            SslMode = MySqlSslMode.Preferred,
        };
        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (DbException e)
        {
            await connection.DisposeAsync();
            throw new OsctlException($"Could not connect to the database server {settings.Host}:{settings.Port} as {settings.AdminUser}: {e.Message}");
        }
        return connection;
    }
}

internal sealed class MySqlRoot(string socket) : IDatabaseRoot
{
    public async Task GrantAsync(string user, string password, string databasePattern, CancellationToken ct)
    {
        var account = $"{DatabaseLimits.Literal(user)}@'localhost'";
        await ExecuteAsync($"CREATE USER IF NOT EXISTS {account} IDENTIFIED BY {DatabaseLimits.Literal(password)}", ct);
        await ExecuteAsync($"ALTER USER {account} IDENTIFIED BY {DatabaseLimits.Literal(password)}", ct);
        await ExecuteAsync($"GRANT ALL PRIVILEGES ON {DatabaseLimits.Quote(databasePattern)}.* TO {account}", ct);
    }

    public async Task<long> MaxAllowedPacketAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand("SELECT @@max_allowed_packet", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = socket,
            ConnectionProtocol = MySqlConnectionProtocol.UnixSocket,
            UserID = "root",
        };
        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (DbException e)
        {
            await connection.DisposeAsync();
            throw new OsctlException(
                $"Could not connect to MariaDB as root over {socket}: {e.Message}. Run 'sudo osctl db init' (root's socket login), " +
                "or create the users by hand (PLAN.md \"Databases\")");
        }
        return connection;
    }
}
