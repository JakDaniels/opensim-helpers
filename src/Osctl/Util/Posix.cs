// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Osctl.Util;

/// <summary>The few POSIX calls .NET has no equivalent for.</summary>
public static partial class Posix
{
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;

    private const int RLIMIT_STACK = 3;

    /// <summary>
    /// rename(2): replaces <paramref name="newPath"/> in one step, whatever the two are (File.Move refuses a symbolic
    /// link to a directory, and Directory.Move refuses an existing destination).
    /// </summary>
    public static void Rename(string oldPath, string newPath)
    {
        if (rename(oldPath, newPath) != 0)
            throw new IOException($"Could not rename {oldPath} to {newPath}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    /// <summary>
    /// umask(2). osctl runs with 007 (PLAN.md "Packaging"): what one member of the opensim group creates, the others
    /// and the instances (running as opensim) can change; nobody else can read it.
    /// </summary>
    public static void SetUmask(int mask) => umask(mask);

    /// <summary>Sends a signal; false if the process does not exist (or is not ours).</summary>
    public static bool Kill(int pid, int signal) => pid > 0 && kill(pid, signal) == 0;

    public static bool IsAlive(int pid) => pid > 0 && Directory.Exists($"/proc/{pid}");

    /// <summary>
    /// Raises the stack size limit for processes started from here (OpenSim's bin/opensim.sh does
    /// <c>ulimit -s 1048576</c>). Under systemd the unit's LimitSTACK does it; this covers direct runs.
    /// Returns false when the hard limit does not allow it.
    /// </summary>
    public static bool RaiseStackLimit(ulong bytes)
    {
        if (getrlimit(RLIMIT_STACK, out var limit) != 0)
            return false;
        var wanted = Math.Min(bytes, limit.Max);
        if (limit.Current >= wanted)
            return true;
        limit.Current = wanted;
        return setrlimit(RLIMIT_STACK, ref limit) == 0;
    }

    /// <summary>
    /// sd_notify(3): tells systemd about the service (READY=1, STATUS=…) when it runs under a Type=notify unit.
    /// Does nothing outside systemd.
    /// </summary>
    public static void Notify(string state)
    {
        var path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(path))
            return;
        if (path[0] == '@')
            path = "\0" + path[1..];
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.UTF8.GetBytes(state));
        }
        catch (SocketException)
        {
            // systemd is gone or does not listen; nothing to tell.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Max;
    }

    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rename(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial int umask(int mask);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static partial int getrlimit(int resource, out RLimit limit);

    [LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static partial int setrlimit(int resource, ref RLimit limit);
}
