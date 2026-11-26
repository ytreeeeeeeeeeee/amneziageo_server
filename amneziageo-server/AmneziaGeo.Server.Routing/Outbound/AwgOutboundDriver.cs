using System.Net;
using AmneziaGeo.Server.Awg.Device;
using AmneziaGeo.Server.Awg.Netlink;
using AmneziaGeo.Server.Routing.Carrier;
using AmneziaGeo.Server.Routing.Host;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// Puts AmneziaWG outbounds on the host, including tunnels carried inside a websocket.
/// </summary>
public sealed class AwgOutboundDriver : IOutboundDriver
{
    private readonly IHostNetwork _network;
    private readonly IAwgDevices _devices;
    private readonly TimeProvider _time;
    private readonly CarrierHost? _carriers;

    /// <summary>
    /// ctor
    /// </summary>
    public AwgOutboundDriver(
        IHostNetwork network,
        IAwgDevices devices,
        TimeProvider? time = null,
        CarrierHost? carriers = null)
    {
        _network = network;
        _devices = devices;
        _time = time ?? TimeProvider.System;
        _carriers = carriers;
    }

    /// <inheritdoc/>
    public OutboundConfig Draft(string name, string kind) => AwgOutboundDefaults.Fresh(name, kind);

    /// <inheritdoc/>
    public bool Supports(string kind) => kind is OutboundKind.Wg or OutboundKind.Ws;

    /// <summary>
    /// Puts the interface, its keys, addresses and route on the host.
    /// </summary>
    public async Task ApplyAsync(OutboundConfig outbound, CancellationToken ct)
    {
        try
        {
            var awg = Settings(outbound);
            var server = await ServerAsync(awg, ct).ConfigureAwait(false);
            var update = AwgOutboundDevice.Update(awg, server);
            if (Differs(outbound.Name, update.Obfuscation))
            {
                await _network.RemoveLinkAsync(outbound.Name, ct).ConfigureAwait(false);
            }

            if (!_network.HasLink(outbound.Name))
            {
                await _network.AddLinkAsync(outbound.Name, ct).ConfigureAwait(false);
            }

            _devices.Apply(update with { ReplacePeers = !Holds(awg) });
            await _network.AddressAsync(outbound.Name, AwgOutboundDevice.Hosts(awg.Address), ct).ConfigureAwait(false);
            await _network.UpAsync(outbound.Name, awg.Mtu, ct).ConfigureAwait(false);
            await _network.RouteAsync(outbound.Name, outbound.Table, ct).ConfigureAwait(false);
        }
        catch (NetlinkException ex)
        {
            throw new HostNetworkException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Takes the carrier, route and interface off the host.
    /// </summary>
    public async Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct)
    {
        _carriers?.Withdraw(outbound.Name);
        await _network.ClearRouteAsync(outbound.Table, ct).ConfigureAwait(false);
        if (_network.HasLink(outbound.Name))
        {
            await _network.RemoveLinkAsync(outbound.Name, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores an interface the host lost or brought down, telling whether it had to.
    /// </summary>
    public async Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct)
    {
        var awg = Settings(outbound);
        // Lays a gone interface again with its keys and its carrier.
        if (!_network.HasLink(outbound.Name))
        {
            await ApplyAsync(outbound, ct).ConfigureAwait(false);

            return true;
        }

        if (!IsDown(outbound))
        {
            return await _network.RouteAsync(outbound.Name, outbound.Table, ct).ConfigureAwait(false);
        }

        await _network.AddressAsync(outbound.Name, AwgOutboundDevice.Hosts(awg.Address), ct).ConfigureAwait(false);
        await _network.UpAsync(outbound.Name, awg.Mtu, ct).ConfigureAwait(false);
        await _network.RouteAsync(outbound.Name, outbound.Table, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Reads the handshake, counters and interface state, without the shared probe verdict.
    /// </summary>
    public OutboundState State(OutboundConfig outbound)
    {
        try
        {
            var state = AwgOutboundDevice.State(Settings(outbound), _devices.Find(outbound.Name), _time.GetUtcNow());
            if (outbound.IsEnabled && !_network.HasLink(outbound.Name))
            {
                return state with { IsAlive = false, Fault = $"the interface '{outbound.Name}' is gone" };
            }

            return IsDown(outbound)
                ? state with { IsAlive = false, Fault = $"the interface '{outbound.Name}' is down" }
                : state;
        }
        catch (NetlinkException ex)
        {
            throw new HostNetworkException(ex.Message, ex);
        }
    }

    private static AwgOutboundConfig Settings(OutboundConfig outbound) =>
        outbound as AwgOutboundConfig
        ?? throw new InvalidOperationException($"the outbound '{outbound.Name}' carries no AmneziaWG settings");

    private bool IsDown(OutboundConfig outbound) =>
        _network.HasLink(outbound.Name) && !_network.IsUp(outbound.Name);

    private bool Holds(AwgOutboundConfig outbound) =>
        _devices.Find(outbound.Name)?.Peers.All(peer => peer.PublicKey == outbound.PeerKey) == true;

    // Tells whether the interface carries another obfuscation.
    private bool Differs(string name, AwgObfuscation? wanted) =>
        _network.HasLink(name) && _devices.Find(name) is { } device && !device.Obfuscation.SameAs(wanted);

    private async Task<IPEndPoint> ServerAsync(AwgOutboundConfig outbound, CancellationToken ct)
    {
        if (!OutboundKind.HasProxy(outbound.Kind))
        {
            var address = await HostAddress.ResolveAsync(outbound.Host, ct).ConfigureAwait(false);

            return new IPEndPoint(address, outbound.Port);
        }

        var carriers = _carriers
                       ?? throw new HostNetworkException(
                           $"'{outbound.Name}' asks for a websocket the panel does not carry");
        var port = await carriers.RaiseAsync(outbound, ct).ConfigureAwait(false);

        return new IPEndPoint(IPAddress.Loopback, port);
    }
}
