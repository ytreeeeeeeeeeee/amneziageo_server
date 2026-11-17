using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Dal;

/// <summary>
/// Creates a protocol-specific row, or returns null for another implementation.
/// </summary>
public interface IOutboundEntityFactory
{
    /// <summary>
    /// Returns a row for the settings, or null when another implementation holds them.
    /// </summary>
    OutboundEntity? Create(OutboundConfig config);
}
