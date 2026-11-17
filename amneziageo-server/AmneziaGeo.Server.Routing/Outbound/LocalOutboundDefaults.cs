using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// The default way out through the host.
/// </summary>
public static class LocalOutboundDefaults
{
    /// <summary>
    /// The name of the outbound that leaves through the uplink of the host.
    /// </summary>
    public const string DirectName = "direct";

    /// <summary>
    /// Returns the local outbound a fresh database is seeded with.
    /// </summary>
    public static LocalOutboundConfig Direct() => new()
    {
        Name = DirectName,
        Position = 1,
        Mark = OutboundRules.FirstMark,
        Table = OutboundRules.MainTable,
        Probe = ProbeDefaults.Address,
        ProbeEvery = ProbeDefaults.Every,
    };
}
