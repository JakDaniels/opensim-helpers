// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

namespace Osctl.Util;

/// <summary>Copying and removing directory trees, keeping file modes (executables) and symbolic links.</summary>
public static class FileTree
{
    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> (created if needed). Entries whose
    /// name is in <paramref name="exclude"/> (at any depth) are skipped, e.g. <c>.git</c>.
    /// </summary>
    public static void Copy(string source, string destination, IReadOnlySet<string>? exclude = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (exclude?.Contains(entry.Name) == true)
                continue;
            var target = Path.Combine(destination, entry.Name);

            if (entry.LinkTarget is { } link)
                File.CreateSymbolicLink(target, link);
            else if (entry is DirectoryInfo dir)
                Copy(dir.FullName, target, exclude);
            else
            {
                // File.Copy keeps the permission bits on Linux; set them anyway so that is not an assumption.
                File.Copy(entry.FullName, target, overwrite: true);
                File.SetUnixFileMode(target, entry.UnixFileMode);
            }
        }
    }

    /// <summary>Removes a directory tree if it exists (symbolic links are removed, not followed).</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
