// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Osctl.Ini;

namespace Osctl.Running;

/// <summary>
/// What 'osctl run' records while an instance runs (/run/osctl/&lt;name&gt;.state): its own PID, OpenSim's, the
/// release it started from (so a build update does not prune it), when it started and when it was ready.
/// Removed when the run ends.
/// </summary>
public sealed record RunState(
    int RunnerPid,
    int OpenSimPid,
    string Release,
    DateTimeOffset Started,
    DateTimeOffset? Ready,
    bool Stopping = false)
{
    public bool IsRunnerAlive => Util.Posix.IsAlive(RunnerPid);

    public void Save(string path)
    {
        var doc = new IniDocument();
        doc.Set("Run", "RunnerPid", RunnerPid.ToString(CultureInfo.InvariantCulture));
        doc.Set("Run", "OpenSimPid", OpenSimPid.ToString(CultureInfo.InvariantCulture));
        doc.Set("Run", "Release", Release);
        doc.Set("Run", "Started", Started.ToString("O", CultureInfo.InvariantCulture));
        doc.Set("Run", "Ready", Ready?.ToString("O", CultureInfo.InvariantCulture) ?? "");
        doc.Set("Run", "Stopping", Stopping ? "true" : "false");
        IniWriter.WriteFile(path, doc, ["Written by 'osctl run' while the instance runs."]);
    }

    public static RunState? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var run = IniParser.ParseFile(path)["Run"];
            if (run is null)
                return null;
            int Int(string key) => int.TryParse(run[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            DateTimeOffset? Time(string key) =>
                DateTimeOffset.TryParse(run[key], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
            return new RunState(Int("RunnerPid"), Int("OpenSimPid"), run["Release"] ?? "", Time("Started") ?? DateTimeOffset.MinValue,
                Time("Ready"), run["Stopping"] == "true");
        }
        catch (Exception e) when (e is IOException or IniParseException)
        {
            return null;
        }
    }
}
