using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Dal;

/// <summary>Creates rows for externally managed interfaces.</summary>
public sealed class InterfaceOutboundEntityFactory : IOutboundEntityFactory
{
    public OutboundEntity? Create(OutboundConfig config) =>
        config is InterfaceOutboundConfig ? new InterfaceOutboundEntity() : null;
}
