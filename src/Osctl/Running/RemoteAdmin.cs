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
}

/// <summary>
/// RemoteAdmin over HTTP (OpenSim/ApplicationPlugins/RemoteController/RemoteAdminPlugin.cs): a methodCall whose
/// one parameter is a struct with <c>password</c>; the reply's struct has <c>success</c> or <c>accepted</c>.
/// </summary>
public sealed class HttpRemoteAdmin : IRemoteAdmin
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

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
