// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Osctl.Util;

/// <summary>The few POSIX calls .NET has no equivalent for.</summary>
public static partial class Posix
{
    /// <summary>
    /// rename(2): replaces <paramref name="newPath"/> in one step, whatever the two are (File.Move refuses a symbolic
    /// link to a directory, and Directory.Move refuses an existing destination).
    /// </summary>
    public static void Rename(string oldPath, string newPath)
    {
        if (rename(oldPath, newPath) != 0)
            throw new IOException($"Could not rename {oldPath} to {newPath}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rename(string oldPath, string newPath);
}
