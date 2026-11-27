// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Osctl.Running;

/// <summary>Calls OpenSim's RemoteAdmin (XML-RPC on the instance's HTTP port); a fake in tests.</summary>
public interface IRemoteAdmin
{
    /// <summary>
    /// Asks the instance to shut down after <paramref name="delay"/>; <paramref name="notice"/>: tell the people in
    /// its regions. False if RemoteAdmin could not be reached or refused.
    /// </summary>
    Task<bool> ShutdownAsync(int port, string password, TimeSpan delay, bool notice, CancellationToken ct);

    /// <summary>Sends a message to everyone in the instance's regions.</summary>
    Task<bool> BroadcastAsync(int port, string password, string message, CancellationToken ct);

    /// <summary>
    /// Starts saving a region's OAR to <paramref name="file"/> (admin_save_oar). OpenSim answers after at most 5 s,
    /// finished or not: the log says when it is done. Null when accepted, else why not.
    /// </summary>
    Task<string?> SaveOarAsync(int port, string password, string region, string file, CancellationToken ct);

    /// <summary>
    /// Replaces a region's contents with an OAR (admin_load_oar, not merged). OpenSim answers when it has loaded it,
    /// which can take minutes, so this waits up to <paramref name="timeout"/>. Null when loaded, else why not.
    /// </summary>
    Task<string?> LoadOarAsync(int port, string password, string region, string file, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs a console command (admin_console_command). Unlike typing it into the console, the command is not echoed,
    /// logged or kept in the history, so it may hold a password. Null when accepted, else why not.
    /// </summary>
    Task<string?> ConsoleCommandAsync(int port, string password, string command, CancellationToken ct);
}

/// <summary>
/// RemoteAdmin over HTTP (OpenSim/ApplicationPlugins/RemoteController/RemoteAdminPlugin.cs): a methodCall whose
/// one parameter is a struct with <c>password</c>; the reply's struct has <c>success</c> or <c>accepted</c>.
/// </summary>
public sealed class HttpRemoteAdmin : IRemoteAdmin
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>For calls that answer when the work is done (admin_load_oar); their own deadline ends them.</summary>
    private static readonly HttpClient LongClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public Task<bool> ShutdownAsync(int port, string password, TimeSpan delay, bool notice, CancellationToken ct)
    {
        var members = new Dictionary<string, string> { ["password"] = password, ["noticetype"] = notice ? "alert" : "none" };
        if (delay > TimeSpan.Zero)
        {
            members["shutdown"] = "delayed";
            members["milliseconds"] = ((int)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        }
        return CallAsync(port, "admin_shutdown", members, ct);
    }

    public Task<bool> BroadcastAsync(int port, string password, string message, CancellationToken ct) =>
        CallAsync(port, "admin_broadcast", new Dictionary<string, string> { ["password"] = password, ["message"] = message }, ct);

    public Task<string?> SaveOarAsync(int port, string password, string region, string file, CancellationToken ct) =>
        CallForErrorAsync(port, "admin_save_oar",
            new Dictionary<string, string> { ["password"] = password, ["region_name"] = region, ["filename"] = file }, ct);

    public async Task<string?> LoadOarAsync(int port, string password, string region, string file, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            return await CallForErrorAsync(port, "admin_load_oar",
                new Dictionary<string, string> { ["password"] = password, ["region_name"] = region, ["filename"] = file }, deadline.Token, LongClient);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"OpenSim had not loaded it after {timeout.TotalMinutes:0} minutes";
        }
    }

    public Task<string?> ConsoleCommandAsync(int port, string password, string command, CancellationToken ct) =>
        CallForErrorAsync(port, "admin_console_command", new Dictionary<string, string> { ["password"] = password, ["command"] = command }, ct);

    /// <summary>
    /// Why a reply says the call failed, or null if it does not. RemoteAdmin only reports failures (a fault, or
    /// success/accepted/saved false with an error); a method that worked may answer an empty struct.
    /// </summary>
    public static string? Failure(string reply)
    {
        try
        {
            var xml = XDocument.Parse(reply);
            if (xml.Descendants("fault").Any())
                return "RemoteAdmin answered with a fault: " + string.Join(" ", xml.Descendants("fault").Descendants("string").Select(e => e.Value));
            var members = xml.Descendants("member")
                .Where(m => m.Element("name") is not null)
                .ToDictionary(m => m.Element("name")!.Value, m => m.Element("value")?.Value.Trim() ?? "");
            var failed = new[] { "success", "accepted", "saved", "loaded" }
                .Any(k => members.TryGetValue(k, out var v) && v.ToLowerInvariant() is "0" or "false");
            if (!failed)
                return null;
            return members.TryGetValue("error", out var error) && error.Length > 0 ? error : "RemoteAdmin refused";
        }
        catch (System.Xml.XmlException)
        {
            return "RemoteAdmin's answer is not XML-RPC";
        }
    }

    public static string Request(string method, IReadOnlyDictionary<string, string> members)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?><methodCall><methodName>").Append(SecurityElement.Escape(method))
            .Append("</methodName><params><param><value><struct>");
        foreach (var (name, value) in members)
            body.Append("<member><name>").Append(SecurityElement.Escape(name)).Append("</name><value><string>")
                .Append(SecurityElement.Escape(value)).Append("</string></value></member>");
        body.Append("</struct></value></param></params></methodCall>");
        return body.ToString();
    }

    /// <summary>True when the reply has success or accepted set, and no fault.</summary>
    public static bool Succeeded(string reply)
    {
        try
        {
            var xml = XDocument.Parse(reply);
            if (xml.Descendants("fault").Any())
                return false;
            foreach (var member in xml.Descendants("member"))
            {
                var name = member.Element("name")?.Value;
                if (name is "success" or "accepted")
                {
                    var value = member.Element("value")?.Value.Trim().ToLowerInvariant();
                    return value is "1" or "true";
                }
            }
            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static async Task<string?> CallForErrorAsync(int port, string method, IReadOnlyDictionary<string, string> members,
        CancellationToken ct, HttpClient? client = null)
    {
        try
        {
            using var content = new StringContent(Request(method, members), Encoding.UTF8, "text/xml");
            // admin_save_oar holds the reply for up to 5 s while the save starts.
            using var response = await (client ?? Client).PostAsync($"http://127.0.0.1:{port}/", content, ct);
            if (!response.IsSuccessStatusCode)
                return $"RemoteAdmin answered HTTP {(int)response.StatusCode}";
            return Failure(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"RemoteAdmin on port {port} did not answer ({e.Message})";
        }
    }

    private static async Task<bool> CallAsync(int port, string method, IReadOnlyDictionary<string, string> members, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(Request(method, members), Encoding.UTF8, "text/xml");
            using var response = await Client.PostAsync($"http://127.0.0.1:{port}/", content, ct);
            return response.IsSuccessStatusCode && Succeeded(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
