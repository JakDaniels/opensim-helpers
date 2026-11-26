// Copyright 2026 Simon Annetts/Jak Daniels
// SPDX-License-Identifier: Apache-2.0

using Osctl.ConfigSets;
using Osctl.Settings;

namespace Osctl.Instances;

/// <summary>
/// osctl's own grids (PLAN.md "Robust and local grids"): each is a Robust instance plus a config set of the same name
/// that its simulators use.
/// </summary>
public sealed class Grids(OsctlPaths paths, OsctlSettings settings, IPortProbe probe)
{
    /// <summary>The config set Robust instances are made from.</summary>
    public const string RobustConfigSet = "robust-hg";

    private readonly InstanceStore instances = new(paths);
    private readonly ConfigSetStore configSets = new(paths);

    public IReadOnlyList<InstanceDefinition> All() =>
        instances.Names().Select(instances.Load).Where(i => i.IsRobust).ToList();

    public InstanceDefinition Load(string grid)
    {
        if (!instances.Exists(grid) || instances.Load(grid) is not { IsRobust: true } robust)
            throw new OsctlException($"There is no grid '{grid}'; 'osctl grid list' shows them");
        return robust;
    }

    /// <summary>The simulator instances whose config set is this grid's.</summary>
    public IReadOnlyList<InstanceDefinition> Simulators(string grid) =>
        instances.Names().Select(instances.Load)
            .Where(i => !i.IsRobust && configSets.Exists(i.ConfigSet) && configSets.Load(i.ConfigSet).Robust == grid)
            .ToList();

    /// <summary>
    /// The grid a simulator instance is on, when that is one of osctl's own; null for standalones and other grids.
    /// </summary>
    public InstanceDefinition? GridOf(InstanceDefinition instance) =>
        !instance.IsRobust && configSets.Exists(instance.ConfigSet) && configSets.Load(instance.ConfigSet).Robust is { } grid
            && instances.Exists(grid)
            ? instances.Load(grid)
            : null;

    /// <summary>
    /// Two ports for a new grid from [Ports] RobustPortMin–RobustPortMax: the first free pair (public, private = public
    /// + 1) that no other grid or region uses and nothing on the machine listens on.
    /// </summary>
    public (int Public, int Private) AllocatePorts()
    {
        var used = UsedPorts();
        for (var port = settings.Ports.RobustPortMin; port < settings.Ports.RobustPortMax; port++)
        {
            if (!used.Contains(port) && !used.Contains(port + 1) && probe.IsFree(port) && probe.IsFree(port + 1))
                return (port, port + 1);
        }
        throw new OsctlException(
            $"No two free ports from {settings.Ports.RobustPortMin} to {settings.Ports.RobustPortMax} ([Ports] in osctl.ini)");
    }

    /// <summary>Checks ports given by hand: unused by osctl's grids and regions (except <paramref name="except"/>'s) and free.</summary>
    public void CheckPort(int port, string? except = null)
    {
        if (port is < 1 or > 65535)
            throw new OsctlException($"Port {port} is not a port number");
        if (UsedPorts(except).Contains(port))
            throw new OsctlException($"Port {port} is already used by another grid or region");
        if (except is null && !probe.IsFree(port))
            throw new OsctlException($"Port {port} is in use on this machine");
    }

    private HashSet<int> UsedPorts(string? except = null)
    {
        var used = new HashSet<int>();
        foreach (var name in instances.Names())
        {
            var instance = instances.Load(name);
            if (instance.Grid is { } g && name != except)
            {
                used.Add(g.PublicPort);
                used.Add(g.PrivatePort);
            }
            foreach (var region in instances.Regions(name))
                used.Add(region.Port);
        }
        return used;
    }

    /// <summary>
    /// The config set a grid's simulators use: OpenSim's own grid files with Hypergrid, and Groups V2 and offline IM
    /// through the grid's Robust. The grid's host and ports are set by osctl itself when it generates a simulator's
    /// configuration, so this file does not change when they do.
    /// </summary>
    public static string ConfigSetText(string grid) => $$"""
        ; Config set '{{grid}}': regions on osctl's own grid '{{grid}}' (its Robust instance of the same name), written by
        ; 'osctl grid add'. You may change it; 'osctl instance delete {{grid}}' removes it. osctl sets the grid's host and
        ; ports ([Const] BaseHostname, PublicPort, PrivURL, PrivatePort) itself. See PLAN.md "Robust and local grids".

        [ConfigSet]
            Description = Regions on the grid '{{grid}}' (osctl's own Robust)
            Mode = grid
            Grid = {{grid}}
            Robust = {{grid}}
            Load = OpenSimDefaults.ini, OpenSim.ini

        [Files]
            OpenSim.ini = bin:OpenSim.ini.example
            config-include/GridCommon.ini = bin:config-include/GridCommon.ini.example
            config-include/FlotsamCache.ini = bin:config-include/FlotsamCache.ini.example

        [Set OpenSim.ini]
            Architecture|Include-Architecture = config-include/GridHypergrid.ini
            Groups|Enabled = true
            Groups|Module = "Groups Module V2"
            Groups|ServicesConnectorModule = "Groups HG Service Connector"
            Groups|LocalService = remote
            Groups|GroupsServerURI = "${Const|PrivURL}:${Const|PrivatePort}"
            Groups|HomeURI = "${Const|BaseURL}:${Const|PublicPort}"
            Groups|MessagingEnabled = true
            Groups|MessagingModule = "Groups Messaging Module V2"
            Groups|MessageOnlineUsersOnly = true
            Messaging|OfflineMessageModule = "Offline Message Module V2"
            Messaging|OfflineMessageURL = "${Const|PrivURL}:${Const|PrivatePort}"
            Messaging|MuteListModule = MuteListModule
            Messaging|ForwardOfflineGroupMessages = true

        """;
}
