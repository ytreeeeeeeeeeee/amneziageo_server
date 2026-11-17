using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// AmneziaWG peer diagnostics in addition to common availability and counters.
/// </summary>
public sealed record AwgOutboundState(
    string Name,
    bool HasLink,
    string Endpoint,
    DateTimeOffset? LastHandshake,
    ulong? RxBytes,
    ulong? TxBytes,
    bool IsAlive,
    string Fault,
    ProbeReading? Probe = null) : OutboundState(Name, HasLink, RxBytes, TxBytes, IsAlive, Fault, Probe)
{
    /// <summary>
    /// How long a handshake stands before the tunnel counts as dead.
    /// </summary>
    public static readonly TimeSpan AliveFor = TimeSpan.FromMinutes(3);
}
