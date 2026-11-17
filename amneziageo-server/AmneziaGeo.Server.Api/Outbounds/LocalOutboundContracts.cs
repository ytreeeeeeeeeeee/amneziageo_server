using System.Text.Json.Serialization;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// What the host holds for a local outbound, as the interface reads it.
/// </summary>
public sealed record LocalOutboundStateBody(
    bool HasLink,
    ulong? RxBytes,
    ulong? TxBytes,
    bool IsAlive,
    string Fault,
    bool Carries,
    OutboundProbeBody? Probe)
    : OutboundStateBody(HasLink, RxBytes, TxBytes, IsAlive, Fault, Carries, Probe);

/// <summary>
/// An outbound through the host itself, as the interface reads it.
/// </summary>
public sealed record LocalOutboundResponse : OutboundResponse;

/// <summary>
/// An outbound through the host itself, as the interface sends it.
/// </summary>
public sealed record LocalOutboundRequest : OutboundRequest
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Kind => OutboundKind.Local;

    /// <inheritdoc/>
    protected override OutboundConfig Settings() => new LocalOutboundConfig
    {
        Name = (Name ?? string.Empty).Trim(),
    };
}

/// <summary>
/// Describes local outbounds and their state in the shape the interface reads.
/// </summary>
public sealed class LocalOutboundResponses : IOutboundResponseFactory
{
    /// <inheritdoc/>
    public OutboundResponse? Response(OutboundConfig config, bool secrets) =>
        config is LocalOutboundConfig ? new LocalOutboundResponse() : null;

    /// <inheritdoc/>
    public OutboundStateBody? State(OutboundState state)
    {
        if (state is not LocalOutboundState)
        {
            return null;
        }

        var common = OutboundAnswers.CommonState(state);

        return new LocalOutboundStateBody(
            common.HasLink,
            common.RxBytes,
            common.TxBytes,
            common.IsAlive,
            common.Fault,
            common.Carries,
            common.Probe);
    }
}
