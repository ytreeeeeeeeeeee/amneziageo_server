using System.Net.Sockets;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>Owns routing entries, leaving the existing device and its settings untouched.</summary>
public sealed class InterfaceOutboundDriver(IHostNetwork network) : IOutboundDriver
{
    public bool Supports(string kind) => kind == OutboundKind.Interface;

    public OutboundConfig Draft(string name, string kind) => new InterfaceOutboundConfig
        { Name = name, ProbeEvery = ProbeDefaults.Every };

    public async Task ApplyAsync(OutboundConfig outbound, CancellationToken ct) =>
        await RoutesAsync(Settings(outbound), ct).ConfigureAwait(false);

    public Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct)
    {
        if (outbound.Mark is < OutboundRules.FirstMark or > OutboundRules.LastMark
            || outbound.Table != OutboundRules.TableOf(outbound.Mark, true))
        {
            throw new HostNetworkException("the interface routing table does not match its outbound mark");
        }

        return network.ClearRouteAsync(outbound.Table, ct);
    }

    public Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct) => RoutesAsync(Settings(outbound), ct);

    public OutboundState State(OutboundConfig outbound)
    {
        var present = network.HasLink(outbound.InterfaceName);
        var up = present && network.IsUp(outbound.InterfaceName);
        return new(outbound.Name, present, null, null, up,
            up ? "" : $"the interface '{outbound.InterfaceName}' is {(present ? "down" : "missing")}");
    }

    private async Task<bool> RoutesAsync(InterfaceOutboundConfig outbound, CancellationToken ct)
    {
        if (OutboundRules.Check(outbound) is { } fault)
        {
            throw new HostNetworkException(fault.Message);
        }

        if (!network.HasLink(outbound.Device) || !network.IsUp(outbound.Device))
        {
            throw new HostNetworkException($"the existing interface '{outbound.Device}' must be present and up");
        }

        var changed = false;
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var six = family == AddressFamily.InterNetworkV6;
            var enabled = six ? outbound.Ipv6 : outbound.Ipv4;
            changed |= enabled
                ? await network.DefaultRouteAsync(outbound.Device, outbound.Table, family,
                    six ? outbound.Gateway6 : outbound.Gateway4, ct).ConfigureAwait(false)
                : await network.UnreachableRouteAsync(outbound.Table, family, ct).ConfigureAwait(false);
        }

        return changed;
    }

    private static InterfaceOutboundConfig Settings(OutboundConfig outbound) => outbound as InterfaceOutboundConfig
        ?? throw new InvalidOperationException("the interface driver requires interface settings");
}
