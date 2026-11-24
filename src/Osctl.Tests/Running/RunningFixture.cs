// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.Ini;
using Osctl.Processes;
using Osctl.Running;
using Osctl.Settings;
using Osctl.Tests.Instances;

namespace Osctl.Tests.Running;

/// <summary>
/// An instance 'Test1' with two regions whose "OpenSim" is a shell script run in real tmux. The script reads the
/// region names from the generated Regions.ini and says LOGINS ENABLED for each (unless told not to), then shows a
/// console prompt: 'shutdown' exits 0, 'crash' exits 3, anything else is echoed back.
/// </summary>
internal sealed class RunningFixture : IDisposable
{
    public const string FakeOpenSim = """
        for a in "$@"; do case "$a" in -inifile=*) ini="${a#-inifile=}";; esac; done
        regions="$(dirname "$ini")/Regions/Regions.ini"
        echo "fake OpenSim in $(pwd)"
        if [ ! -f no-ready ]; then
          sed -n 's/^\[\(.*\)\]$/\1/p' "$regions" | while read -r r; do echo "INITIALIZATION COMPLETE FOR $r - LOGINS ENABLED"; done
        fi
        while true; do
          printf 'Region (root) # '
          read -r line || exit 5
          case "$line" in
            shutdown) echo "Shutting down"; exit 0 ;;
            crash) exit 3 ;;
            *) echo "you said: $line" ;;
          esac
        done
        """;

    public RunningFixture(bool ready = true)
    {
        Instance = new InstanceFixture();
        var script = Instance.Write("fake-opensim.sh", FakeOpenSim);
        if (!ready)
            File.WriteAllText(Path.Combine(Instance.Bin, "no-ready"), "");
        IniFileEditor.SetValue(Instance.Paths.SettingsFile, "Run", "Command", $"sh {script}");
        IniFileEditor.SetValue(Instance.Paths.SettingsFile, "Supervisor", "Mode", "direct");
        IniFileEditor.SetValue(Instance.Paths.SettingsFile, "Timeouts", "StartSeconds", "3");
        IniFileEditor.SetValue(Instance.Paths.SettingsFile, "Timeouts", "StopSeconds", "5");
        Instance.Ok("instance", "add", "Test1", "--db", "sqlite", "--estate-owner", "Test User").GetAwaiter().GetResult();
        Instance.Ok("region", "add", "Welcome", "--instance", "Test1", "--location", "1000,1000").GetAwaiter().GetResult();
        Instance.Ok("region", "add", "Second Place", "--instance", "Test1", "--location", "1001,1000").GetAwaiter().GetResult();
        Settings = SettingsLoader.Load(Instance.Paths).Settings;
    }

    public InstanceFixture Instance { get; }

    public OsctlSettings Settings { get; }

    public StringWriter Log { get; } = new();

    public FakeRemoteAdmin Admin { get; } = new();

    public global::Osctl.Instances.InstanceStore Store => Instance.Instances;

    public Tmux Tmux => new(new ProcessRunner(), Store.TmuxSocket("Test1"), "Test1");

    public InstanceRunner Runner() =>
        new(Instance.Paths, Settings, new ProcessRunner(), Admin, Instance.Ports, TextWriter.Synchronized(Log), TimeProvider.System);

    public InstanceStopper Stopper() =>
        new(Instance.Paths, Settings, new ProcessRunner(), Admin, TextWriter.Synchronized(Log), TimeProvider.System);

    /// <summary>Waits until the run state says ready (or the run ends).</summary>
    public async Task<RunState> WaitReadyAsync(Task run)
    {
        for (var i = 0; i < 100; i++)
        {
            if (RunState.Load(Store.RunStateFile("Test1")) is { Ready: not null } state)
                return state;
            if (run.IsCompleted)
                throw new Xunit.Sdk.XunitException($"The run ended before it was ready: {Log}");
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Not ready in time: {Log}");
    }

    public static bool HasTmux() => new ProcessRunner().RunAsync("tmux", ["-V"]).GetAwaiter().GetResult().Succeeded;

    public void Dispose()
    {
        Tmux.KillAsync(CancellationToken.None).GetAwaiter().GetResult();
        Instance.Dispose();
    }
}

internal sealed class FakeRemoteAdmin : IRemoteAdmin
{
    public bool Answers { get; set; }

    public List<(int Port, string Password, TimeSpan Delay)> Shutdowns { get; } = [];

    public List<(int Port, string Message)> Broadcasts { get; } = [];

    public List<string> Commands { get; } = [];

    /// <summary>What admin_save_oar does (region, file); returns the refusal, or null.</summary>
    public Func<string, string, string?>? OnSaveOar { get; set; }

    /// <summary>What admin_console_command does; returns the refusal, or null.</summary>
    public Func<string, string?>? OnConsoleCommand { get; set; }

    public Task<bool> ShutdownAsync(int port, string password, TimeSpan delay, bool notice, CancellationToken ct)
    {
        Shutdowns.Add((port, password, delay));
        return Task.FromResult(Answers);
    }

    public Task<bool> BroadcastAsync(int port, string password, string message, CancellationToken ct)
    {
        lock (Broadcasts)
            Broadcasts.Add((port, message));
        return Task.FromResult(Answers);
    }

    public Task<string?> SaveOarAsync(int port, string password, string region, string file, CancellationToken ct) =>
        Task.FromResult(OnSaveOar is null ? "not answered" : OnSaveOar(region, file));

    public Task<string?> ConsoleCommandAsync(int port, string password, string command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.FromResult(OnConsoleCommand is null ? "not answered" : OnConsoleCommand(command));
    }
}
