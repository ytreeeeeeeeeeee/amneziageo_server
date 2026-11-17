using System.Text.Json.Serialization;
using AmneziaGeo.Server.Api.Configs;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// What the host holds for an AmneziaWG outbound, as the interface reads it.
/// </summary>
public sealed record AwgOutboundStateBody(
    bool HasLink,
    ulong? RxBytes,
    ulong? TxBytes,
    bool IsAlive,
    string Fault,
    bool Carries,
    OutboundProbeBody? Probe,
    string Endpoint,
    DateTimeOffset? LastHandshake) : OutboundStateBody(HasLink, RxBytes, TxBytes, IsAlive, Fault, Carries, Probe);

/// <summary>
/// An AmneziaWG outbound, as the interface reads it.
/// </summary>
public sealed record AwgOutboundResponse : OutboundResponse
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Proxy { get; init; } = string.Empty;

    public string PublicKey { get; init; } = string.Empty;

    public string PeerKey { get; init; } = string.Empty;

    public string? PrivateKey { get; init; }

    public string? PresharedKey { get; init; }

    public string[] Address { get; init; } = [];

    public int Mtu { get; init; }

    public int Keepalive { get; init; }

    public ObfuscationBody Obfuscation { get; init; } = ConfigAnswers.Obfuscation(new());
}

/// <summary>
/// An AmneziaWG outbound, as the interface sends it.
/// </summary>
public record AwgOutboundRequest : OutboundRequest
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Kind => OutboundKind.Wg;

    public string? Host { get; init; }

    public int Port { get; init; }

    public string? Proxy { get; init; }

    public string? PrivateKey { get; init; }

    public string? PeerKey { get; init; }

    public string? PresharedKey { get; init; }

    public string[]? Address { get; init; }

    public int Mtu { get; init; }

    public int Keepalive { get; init; }

    public ObfuscationBody? Obfuscation { get; init; }

    /// <inheritdoc/>
    protected override OutboundConfig Settings() => new AwgOutboundConfig
    {
        Name = (Name ?? string.Empty).Trim(),
        Host = (Host ?? string.Empty).Trim(),
        Port = Port,
        Proxy = (Proxy ?? string.Empty).Trim(),
        PrivateKey = (PrivateKey ?? string.Empty).Trim(),
        PeerKey = (PeerKey ?? string.Empty).Trim(),
        PresharedKey = (PresharedKey ?? string.Empty).Trim(),
        Address = Address ?? [],
        Mtu = Mtu,
        Keepalive = Keepalive,
        Obfuscation = ConfigAnswers.Settings(Obfuscation),
    };
}

/// <summary>
/// An AmneziaWG outbound carried inside a websocket, as the interface sends it.
/// </summary>
public sealed record WsOutboundRequest : AwgOutboundRequest
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Kind => OutboundKind.Ws;
}

/// <summary>
/// A client configuration the interface sends to be read into an AmneziaWG outbound.
/// </summary>
public sealed record AwgOutboundImportRequest(string? Name, string? Config);

/// <summary>
/// Describes AmneziaWG outbounds and their state in the shape the interface reads.
/// </summary>
public sealed class AwgOutboundResponses : IOutboundResponseFactory
{
    /// <inheritdoc/>
    public OutboundResponse? Response(OutboundConfig config, bool secrets) => config is AwgOutboundConfig awg
        ? new AwgOutboundResponse
        {
            Host = awg.Host,
            Port = awg.Port,
            Proxy = awg.Proxy,
            PublicKey = awg.PublicKey,
            PeerKey = awg.PeerKey,
            PrivateKey = secrets ? awg.PrivateKey : null,
            PresharedKey = secrets ? awg.PresharedKey : null,
            Address = [.. awg.Address],
            Mtu = awg.Mtu,
            Keepalive = awg.Keepalive,
            Obfuscation = ConfigAnswers.Obfuscation(awg.Obfuscation),
        }
        : null;

    /// <inheritdoc/>
    public OutboundStateBody? State(OutboundState state)
    {
        if (state is not AwgOutboundState awg)
        {
            return null;
        }

        var common = OutboundAnswers.CommonState(state);

        return new AwgOutboundStateBody(common.HasLink, common.RxBytes, common.TxBytes, common.IsAlive,
            common.Fault, common.Carries, common.Probe, awg.Endpoint, awg.LastHandshake);
    }
}
