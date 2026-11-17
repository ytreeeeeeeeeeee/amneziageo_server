using AmneziaGeo.Server.Core.Crypto;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// The values a new AmneziaWG outbound starts with.
/// </summary>
public static class AwgOutboundDefaults
{
    /// <summary>
    /// The packet size a new tunnel carries.
    /// </summary>
    public const int Mtu = 1420;

    /// <summary>
    /// How often a new tunnel holds the path to the server open.
    /// </summary>
    public const int Keepalive = 25;

    /// <summary>
    /// The port a new tunnel goes to.
    /// </summary>
    public const int Port = 51820;

    /// <summary>
    /// Returns a tunnel with a key pair of its own.
    /// </summary>
    public static AwgOutboundConfig Fresh(string name, string kind = OutboundKind.Wg)
    {
        var pair = Curve25519.Create();

        return new AwgOutboundConfig
        {
            Name = name,
            Kind = OutboundKind.IsAwg(kind) ? kind : OutboundKind.Wg,
            Port = Port,
            Mtu = Mtu,
            Keepalive = Keepalive,
            PrivateKey = pair.PrivateKey,
            PublicKey = pair.PublicKey,
            Probe = ProbeDefaults.Address,
            ProbeEvery = ProbeDefaults.Every,
        };
    }
}
