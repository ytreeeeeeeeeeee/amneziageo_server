using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// Availability, counters and the probe verdict shared by all ways out.
/// </summary>
public record OutboundState(
    string Name,
    bool HasLink,
    ulong? RxBytes,
    ulong? TxBytes,
    bool IsAlive,
    string Fault,
    ProbeReading? Probe = null)
{
    /// <summary>
    /// Tells whether the outbound carries traffic, taking the probe verdict into account.
    /// </summary>
    public bool Carries => IsAlive && Probe is not { IsReached: false };

    /// <summary>
    /// Returns the state of an outbound the host does not carry.
    /// </summary>
    public static OutboundState Missing(string name, string fault = "") => new(name, false, null, null, false, fault);
}
