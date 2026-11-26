// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Osctl.ConfigSets;
using Osctl.Settings;

namespace Osctl.Instances;

/// <summary>Whether a port is free on this machine; a fake in tests.</summary>
public interface IPortProbe
{
    bool IsFree(int port);
}

/// <summary>
/// Tries to bind the port for TCP (the instance's HTTP port) and UDP (a region's port). Ports already given to
/// osctl's own regions are checked from the region files instead, so a running instance does not count.
/// </summary>
public sealed class PortProbe : IPortProbe
{
    public bool IsFree(int port)
    {
        try
        {
            using var tcp = new TcpListener(IPAddress.Any, port);
            tcp.Start();
            tcp.Stop();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>A region with the instance and grid it is in.</summary>
public sealed record PlacedRegion(string Instance, string Grid, RegionDefinition Region);

/// <summary>
/// Checks and allocates what regions need (PLAN.md "What MIOS did"): ports are unique on the machine and come from
/// [Ports] in osctl.ini; UUIDs are unique; names (ignoring case, as OpenSim does) and locations (no overlap, taking
/// region sizes into account) are unique within a grid.
/// </summary>
public sealed class RegionPlanner(InstanceStore instances, ConfigSetStore configSets, OsctlSettings settings, IPortProbe probe)
{
    public IReadOnlyList<PlacedRegion> AllRegions()
    {
        var all = new List<PlacedRegion>();
        foreach (var name in instances.Names())
        {
            var instance = instances.Load(name);
            var grid = GridOf(instance);
            all.AddRange(instances.Regions(name).Select(r => new PlacedRegion(name, grid, r)));
        }
        return all;
    }

    public string GridOf(InstanceDefinition instance) =>
        configSets.Exists(instance.ConfigSet)
            ? configSets.Load(instance.ConfigSet).GridFor(instance.Name)
            : $"configset-{instance.ConfigSet}";

    /// <summary>
    /// Makes the region to add: a port and UUID are allocated when not given, and everything is checked against
    /// every other region. <paramref name="ignore"/>: a region being replaced (rename or move), left out of the checks.
    /// </summary>
    public RegionDefinition Plan(
        InstanceDefinition instance,
        string name,
        int x,
        int y,
        int size,
        int? port = null,
        Guid? uuid = null,
        IReadOnlyList<KeyValuePair<string, string>>? settingsToKeep = null,
        Guid? ignore = null)
    {
        RegionDefinition.CheckName(name);
        RegionDefinition.CheckSize(size);
        var grid = GridOf(instance);
        var others = AllRegions().Where(p => p.Region.Uuid != ignore).ToList();

        var sameGrid = others.Where(p => p.Grid == grid).ToList();
        if (sameGrid.FirstOrDefault(p => string.Equals(p.Region.Name, name, StringComparison.OrdinalIgnoreCase)) is { } named)
            throw new OsctlException($"There is already a region '{named.Region.Name}' in this grid (instance {named.Instance})");

        var candidate = new RegionDefinition(name, uuid ?? Guid.Empty, x, y, port ?? 0, size, settingsToKeep ?? []);
        if (sameGrid.FirstOrDefault(p => p.Region.Overlaps(candidate)) is { } overlap)
            throw new OsctlException(
                $"Location {x},{y} (size {size}) overlaps region '{overlap.Region.Name}' at {overlap.Region.X},{overlap.Region.Y} (instance {overlap.Instance})");

        var usedPorts = others.Select(p => p.Region.Port).ToHashSet();
        foreach (var robust in instances.Names().Select(instances.Load).Select(i => i.Grid).OfType<GridDefinition>())
        {
            usedPorts.Add(robust.PublicPort);
            usedPorts.Add(robust.PrivatePort);
        }
        int chosenPort;
        if (port is { } wanted)
        {
            if (others.FirstOrDefault(p => p.Region.Port == wanted) is { } user)
                throw new OsctlException($"Port {wanted} is already used by region '{user.Region.Name}' (instance {user.Instance})");
            if (usedPorts.Contains(wanted))
                throw new OsctlException($"Port {wanted} is already used by a grid's Robust instance");
            if (!probe.IsFree(wanted))
                throw new OsctlException($"Port {wanted} is in use on this machine");
            chosenPort = wanted;
        }
        else
        {
            chosenPort = Enumerable.Range(settings.Ports.RegionPortMin, settings.Ports.RegionPortMax - settings.Ports.RegionPortMin + 1)
                .Where(p => !usedPorts.Contains(p))
                .FirstOrDefault(probe.IsFree);
            if (chosenPort == 0)
                throw new OsctlException(
                    $"No free port from {settings.Ports.RegionPortMin} to {settings.Ports.RegionPortMax} ([Ports] in osctl.ini)");
        }

        var chosenUuid = uuid ?? Guid.NewGuid();
        if (others.FirstOrDefault(p => p.Region.Uuid == chosenUuid) is { } same)
            throw new OsctlException($"UUID {chosenUuid} is already region '{same.Region.Name}' (instance {same.Instance})");
        if (chosenUuid == Guid.Empty)
            throw new OsctlException("A region's UUID cannot be all zeros");

        return candidate with { Port = chosenPort, Uuid = chosenUuid };
    }
}
