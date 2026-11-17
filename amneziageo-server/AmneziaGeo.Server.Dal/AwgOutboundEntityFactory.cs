using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Dal;

/// <summary>
/// Creates the row that holds an AmneziaWG outbound.
/// </summary>
public sealed class AwgOutboundEntityFactory : IOutboundEntityFactory
{
    /// <inheritdoc/>
    public OutboundEntity? Create(OutboundConfig config) =>
        config is AwgOutboundConfig ? new AwgOutboundEntity() : null;
}
