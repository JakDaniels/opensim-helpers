// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using Osctl.Util;

namespace Osctl.Operations;

/// <summary>What one snapshot of a grid's asset files holds.</summary>
/// <param name="Files">Every asset file in it.</param>
/// <param name="Copied">Files copied, because the previous backup did not have them (or no hard link could be made).</param>
/// <param name="CopiedBytes">The size of the copied files: about what the snapshot adds on disk.</param>
/// <param name="Skipped">New files not taken because they did not match their hash: most likely still being written.</param>
/// <param name="Damaged">Older files in the grid that do not match their hash; they are copied as they are.</param>
public sealed record AssetCounts(int Files, int Copied, long CopiedBytes, int Skipped, IReadOnlyList<string> Damaged);

/// <summary>
/// A copy of a grid's FSAssets files in a backup (PLAN.md "Backups and scheduled restarts"). FSAssets names each file
/// after the SHA-256 of the asset (<c>data/ab/cd/ef/0123/&lt;hash&gt;.gz</c>, gzipped) and never changes or removes one,
/// so a file the previous backup holds is hard-linked to it rather than copied: each backup is whole, and only new
/// assets take space. New files are checked against their hash as they are copied.
/// <para>
/// Order matters for a consistent backup: the database (FSAssets' index) is dumped first, then the spool (assets
/// stored but not yet written out: <c>&lt;hash&gt;.asset</c>), then <c>data/</c>. A file leaves the spool only once it
/// is whole in <c>data/</c>, so every asset in the dumped index is in one of the two.
/// </para>
/// </summary>
public static class AssetSnapshot
{
    public const string Dir = "fsassets";
    private const string Data = "data";
    private const string Spool = "spool";

    /// <param name="source">The grid's assets directory (its <c>data/</c> and spool <c>tmp/</c>).</param>
    /// <param name="target">The snapshot directory to make (<c>&lt;backup&gt;/fsassets</c>).</param>
    /// <param name="previous">The previous backup's snapshot, to link to; null for none.</param>
    /// <param name="recent">Files changed since then may still be being written, so a hash mismatch is not an error.</param>
    public static AssetCounts Take(string source, string target, string? previous, DateTime recent, CancellationToken ct)
    {
        int files = 0, copied = 0, skipped = 0;
        long bytes = 0;
        var damaged = new List<string>();

        // 1. The spool: files waiting to be written out, which go once they are. Only its top level (spool/ under it
        //    holds files still being written).
        var spool = Path.Combine(source, "tmp");
        Directory.CreateDirectory(Path.Combine(target, Spool));
        if (Directory.Exists(spool))
        {
            foreach (var file in Directory.GetFiles(spool, "*.asset"))
            {
                ct.ThrowIfCancellationRequested();
                var to = Path.Combine(target, Spool, Path.GetFileName(file));
                try
                {
                    File.Copy(file, to);
                }
                catch (FileNotFoundException)
                {
                    continue;   // written out meanwhile: it is in data/ now
                }
                if (!Matches(to, Path.GetFileNameWithoutExtension(file), gzipped: false))
                {
                    File.Delete(to);
                    skipped++;
                    continue;
                }
                files++;
                copied++;
                bytes += new FileInfo(to).Length;
            }
        }

        // 2. data/.
        var data = Path.Combine(source, Data);
        Directory.CreateDirectory(Path.Combine(target, Data));
        if (Directory.Exists(data))
        {
            foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(data, file);
                var to = Path.Combine(target, Data, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                var info = new FileInfo(file);
                if (previous is not null && Path.Combine(previous, Data, rel) is var old
                    && new FileInfo(old) is { Exists: true } oldInfo && oldInfo.Length == info.Length
                    && Posix.HardLink(old, to))
                {
                    files++;
                    continue;
                }

                File.Copy(file, to);
                var gzipped = rel.EndsWith(".gz", StringComparison.Ordinal);
                var hash = Path.GetFileName(gzipped ? rel[..^3] : rel);
                if (!Matches(to, hash, gzipped))
                {
                    if (info.LastWriteTimeUtc >= recent)
                    {
                        File.Delete(to);     // still being written; its spool copy, if it has one, is taken
                        skipped++;
                        continue;
                    }
                    damaged.Add(rel);        // kept as it is: it is what the grid has
                }
                files++;
                copied++;
                bytes += info.Length;
            }
        }

        return new AssetCounts(files, copied, bytes, skipped, damaged);
    }

    /// <summary>Whether a file holds the asset its name says: the SHA-256 of its (unzipped) contents.</summary>
    public static bool Matches(string file, string hash, bool gzipped)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var content = gzipped ? new GZipStream(stream, CompressionMode.Decompress) : (Stream)stream;
            return Convert.ToHexStringLower(SHA256.HashData(content)) == hash;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return false;
        }
    }
}
