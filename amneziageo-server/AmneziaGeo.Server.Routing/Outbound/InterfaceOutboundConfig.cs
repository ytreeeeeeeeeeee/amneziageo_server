using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Server.Routing.Dns;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>A way out through an interface managed outside this service.</summary>
public sealed record InterfaceOutboundConfig : OutboundConfig
{
    public InterfaceOutboundConfig() => Kind = OutboundKind.Interface;

    public string Device { get; init; } = string.Empty;
    public bool Ipv4 { get; init; }
    public bool Ipv6 { get; init; }
    private string _gateway4 = string.Empty;
    private string _gateway6 = string.Empty;

    public string Gateway4
    {
        get => _gateway4;
        init => _gateway4 = NormalizeGateway(value);
    }

    public string Gateway6
    {
        get => _gateway6;
        init => _gateway6 = NormalizeGateway(value);
    }
    public override string InterfaceName => Device;
    public override bool SupportsIpv4 => Ipv4;
    public override bool SupportsIpv6 => Ipv6;

    public override OutboundFault? CheckSettings()
    {
        if (Kind != OutboundKind.Interface)
        {
            return new("bad-kind", "the interface settings require the interface kind");
        }

        if (OutboundRules.CheckInterfaceName(Device) is { } name)
        {
            return name;
        }

        if (!Ipv4 && !Ipv6)
        {
            return new("bad-ip-families", "select at least one supported IP family");
        }

        if (!Gateway(Gateway4, AddressFamily.InterNetwork, Ipv4)
            || !Gateway(Gateway6, AddressFamily.InterNetworkV6, Ipv6))
        {
            return new("bad-gateway", "a gateway must be an IP address of an enabled family without a zone suffix; select the device separately");
        }

        if (Dns.Any(server => IPAddress.TryParse(server, out var address) && !Supports(address.AddressFamily))
            || (DnsRules.Upstream(Probe, out var probe) && !Supports(probe.Address.AddressFamily)))
        {
            return new("unsupported-ip-family", "DNS and probe addresses must use a supported IP family");
        }

        return null;
    }

    private static string NormalizeGateway(string value) =>
        !value.Contains('%') && IPAddress.TryParse(value, out var address) ? address.ToString() : value;

    private bool Supports(AddressFamily family) => family == AddressFamily.InterNetwork ? Ipv4 : Ipv6;

    private static bool Gateway(string value, AddressFamily family, bool enabled) => value.Length == 0
        || (enabled && !value.Contains('%') && IPAddress.TryParse(value, out var address) && address.AddressFamily == family
            && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
            && !address.Equals(IPAddress.Broadcast) && !address.IsIPv6Multicast && !IPAddress.IsLoopback(address)
            && (family != AddressFamily.InterNetwork || address.GetAddressBytes()[0] < 224));
}
