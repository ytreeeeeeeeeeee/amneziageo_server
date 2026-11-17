namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// One way out through the host itself, without a tunnel of its own.
/// </summary>
public sealed record LocalOutboundConfig : OutboundConfig
{
    /// <summary>
    /// ctor
    /// </summary>
    public LocalOutboundConfig()
    {
        Kind = OutboundKind.Local;
    }

    /// <inheritdoc/>
    public override OutboundFault? CheckSettings() => Kind == OutboundKind.Local
        ? null
        : new("bad-kind", $"'{Kind}' is not a kind the local settings carry");
}
