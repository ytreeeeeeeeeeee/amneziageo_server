using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Dal;

/// <summary>
/// Creates the row that holds an outbound through the host itself.
/// </summary>
public sealed class LocalOutboundEntityFactory : IOutboundEntityFactory
{
    /// <inheritdoc/>
    public OutboundEntity? Create(OutboundConfig config) =>
        config is LocalOutboundConfig ? new LocalOutboundEntity() : null;
}
