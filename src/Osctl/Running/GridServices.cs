// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Running;

/// <summary>What osctl asks a local grid's Robust services (its private port, on this machine); a fake in tests.</summary>
public interface IGridServices
{
    /// <summary>Whether the grid has an account "First Last": null when Robust does not answer.</summary>
    Task<bool?> AccountExistsAsync(int privatePort, string first, string last, CancellationToken ct);
}

/// <summary>
/// Robust's user account service over HTTP (OpenSim's UserAccountServerPostHandler): <c>POST /accounts</c> with
/// <c>METHOD=getaccount</c> answers <c>&lt;result&gt;null&lt;/result&gt;</c> when there is no such account.
/// </summary>
public sealed class HttpGridServices : IGridServices
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<bool?> AccountExistsAsync(int privatePort, string first, string last, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["METHOD"] = "getaccount",
            ["ScopeID"] = Guid.Empty.ToString(),
            ["FirstName"] = first,
            ["LastName"] = last,
        });
        try
        {
            using var response = await Client.PostAsync($"http://127.0.0.1:{privatePort}/accounts", form, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!text.Contains("<result", StringComparison.Ordinal))
                return null;
            return !text.Contains("<result>null</result>", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
