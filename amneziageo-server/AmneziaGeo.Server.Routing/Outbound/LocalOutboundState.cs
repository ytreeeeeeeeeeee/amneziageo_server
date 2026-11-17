namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// What the host holds for an outbound through its own uplink.
/// </summary>
public sealed record LocalOutboundState(string Name)
    : OutboundState(Name, true, null, null, true, string.Empty);
