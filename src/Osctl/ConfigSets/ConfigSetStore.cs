// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;
using Osctl.Settings;

namespace Osctl.ConfigSets;

/// <summary>Downloads a URL's text; a fake in tests.</summary>
public interface IDownloader
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken);
}

public sealed class HttpDownloader : IDownloader
{
    private static readonly HttpClient Client = CreateClient();

    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken) =>
        Client.GetStringAsync(url, cancellationToken);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var version = typeof(HttpDownloader).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"osctl/{version}");
        return client;
    }
}

/// <summary>
/// The config sets: definitions in /etc/osctl/configsets/&lt;set&gt;.ini, shipped files in
/// /etc/osctl/configsets/&lt;set&gt;/ (for relative local sources), downloads cached in
/// /var/lib/osctl/configsets/&lt;set&gt;/&lt;path&gt;.
/// </summary>
public sealed class ConfigSetStore(OsctlPaths paths)
{
    public string DefinitionFile(string set) => Path.Combine(paths.ConfigSetsDir, set + ".ini");

    public string CacheDir(string set) => Path.Combine(paths.Lib, "configsets", set);

    public string CacheFile(string set, string path) => Path.Combine(CacheDir(set), path);

    public bool Exists(string set) => Osctl.Names.IsValid(set) && File.Exists(DefinitionFile(set));

    public IReadOnlyList<string> Names() =>
        Directory.Exists(paths.ConfigSetsDir)
            ? Directory.GetFiles(paths.ConfigSetsDir, "*.ini")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(Osctl.Names.IsValid)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    public ConfigSet Load(string set)
    {
        Osctl.Names.Check(set, "config set");
        var file = DefinitionFile(set);
        if (!File.Exists(file))
            throw new OsctlException($"There is no config set '{set}' (no {file}); 'osctl configset list' shows them");
        return ConfigSet.FromFile(file);
    }

    /// <summary>
    /// Where a file of the set is read from, given the build's bin/: null when it is bin/'s own file.
    /// A URL that has not been downloaded is an error naming the command that fetches it.
    /// </summary>
    public string? Resolve(ConfigSet set, string relativePath, string bin)
    {
        var file = set.Files.FirstOrDefault(f => f.Path == relativePath);
        return file?.Kind switch
        {
            null => null,
            FileSourceKind.Bin => Path.Combine(bin, file.Source),
            FileSourceKind.Local => Path.IsPathRooted(file.Source)
                ? file.Source
                : Path.Combine(paths.ConfigSetsDir, file.Source),
            FileSourceKind.Url => File.Exists(CacheFile(set.Name, file.Path))
                ? CacheFile(set.Name, file.Path)
                : throw new OsctlException(
                    $"Config set '{set.Name}': {file.Source} has not been downloaded yet; run 'osctl configset fetch {set.Name}'"),
            _ => null,
        };
    }

    public bool IsFetched(ConfigSet set) =>
        set.Files.Where(f => f.Kind == FileSourceKind.Url).All(f => File.Exists(CacheFile(set.Name, f.Path)));

    /// <summary>
    /// Downloads the set's URL files. Each must read as an ini file before it replaces the cached copy, so a
    /// broken download (an error page, say) never replaces a good one. Returns the files fetched.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchAsync(ConfigSet set, IDownloader downloader, bool dryRun, TextWriter progress, CancellationToken ct)
    {
        var fetched = new List<string>();
        foreach (var file in set.Files.Where(f => f.Kind == FileSourceKind.Url))
        {
            if (dryRun)
            {
                await progress.WriteLineAsync($"would download {file.Source} to {CacheFile(set.Name, file.Path)}");
                continue;
            }

            string text;
            try
            {
                text = await downloader.GetStringAsync(file.Source, ct);
            }
            catch (HttpRequestException e)
            {
                throw new OsctlException($"Could not download {file.Source}: {e.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OsctlException($"Could not download {file.Source}: timed out");
            }

            try
            {
                IniParser.Parse(text, file.Source);
            }
            catch (IniParseException e)
            {
                throw new OsctlException($"{file.Source} is not a usable ini file ({e.Message}); the cached copy is kept");
            }

            var target = CacheFile(set.Name, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target + ".tmp", text, ct);
            File.Move(target + ".tmp", target, overwrite: true);
            await progress.WriteLineAsync($"downloaded {file.Source} ({text.Length} bytes)");
            fetched.Add(target);
        }
        return fetched;
    }
}
