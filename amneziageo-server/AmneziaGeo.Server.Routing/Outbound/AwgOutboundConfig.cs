using AmneziaGeo.Server.Awg.Config;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// An AmneziaWG way out, optionally carried inside a websocket.
/// </summary>
public sealed record AwgOutboundConfig : OutboundConfig
{
    /// <inheritdoc/>
    public override string InterfaceName => Name;
    /// <inheritdoc/>
    public override bool OwnsInterface => true;
    /// <inheritdoc/>
    public override OutboundFault? CheckSettings() => AwgOutboundRules.Check(this);

    /// <summary>
    /// ctor
    /// </summary>
    public AwgOutboundConfig()
    {
        Kind = OutboundKind.Wg;
    }

    /// <summary>
    /// The address of the server the tunnel goes to.
    /// </summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// The port of the server the tunnel goes to.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// The wstunnel proxy the tunnel travels through, as a host or a wss:// address.
    /// </summary>
    public string Proxy { get; init; } = string.Empty;

    /// <summary>
    /// The private key of the interface, in base64.
    /// </summary>
    public string PrivateKey { get; init; } = string.Empty;

    /// <summary>
    /// The public key of the interface, in base64.
    /// </summary>
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>
    /// The public key of the server, in base64.
    /// </summary>
    public string PeerKey { get; init; } = string.Empty;

    /// <summary>
    /// The key added on top of the handshake, in base64.
    /// </summary>
    public string PresharedKey { get; init; } = string.Empty;

    /// <summary>
    /// The address ranges the interface carries.
    /// </summary>
    public IReadOnlyList<string> Address { get; init; } = [];

    /// <summary>
    /// The packet size of the interface, zero when the kernel picks it.
    /// </summary>
    public int Mtu { get; init; }

    /// <summary>
    /// How often an empty packet holds the path to the server open, in seconds.
    /// </summary>
    public int Keepalive { get; init; }

    /// <summary>
    /// The obfuscation the tunnel carries.
    /// </summary>
    public ObfuscationSettings Obfuscation { get; init; } = new();
}
