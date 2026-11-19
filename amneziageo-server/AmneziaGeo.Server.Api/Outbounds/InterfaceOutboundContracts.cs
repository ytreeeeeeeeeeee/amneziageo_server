using System.Text.Json.Serialization;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>An existing interface, as the panel sends it.</summary>
public sealed record InterfaceOutboundRequest : OutboundRequest
{
    [JsonIgnore] public override string Kind => OutboundKind.Interface;
    public string? InterfaceName { get; init; }
    [JsonRequired] public bool Ipv4 { get; init; }
    [JsonRequired] public bool Ipv6 { get; init; }
    public string? Gateway4 { get; init; }
    public string? Gateway6 { get; init; }

    protected override OutboundConfig Settings() => new InterfaceOutboundConfig
    {
        Name = (Name ?? "").Trim(), Device = (InterfaceName ?? "").Trim(), Ipv4 = Ipv4, Ipv6 = Ipv6,
        Gateway4 = (Gateway4 ?? "").Trim(), Gateway6 = (Gateway6 ?? "").Trim(),
    };
}

/// <summary>An existing interface, as the panel reads it.</summary>
public sealed record InterfaceOutboundResponse : OutboundResponse
{
    public string InterfaceName { get; init; } = string.Empty;
    public bool Ipv4 { get; init; }
    public bool Ipv6 { get; init; }
    public string Gateway4 { get; init; } = string.Empty;
    public string Gateway6 { get; init; } = string.Empty;
}

/// <summary>Describes existing interfaces without protocol-specific state or keys.</summary>
public sealed class InterfaceOutboundResponses : IOutboundResponseFactory
{
    public OutboundResponse? Response(OutboundConfig config, bool secrets) =>
        config is InterfaceOutboundConfig settings ? new InterfaceOutboundResponse
        {
            InterfaceName = settings.Device, Ipv4 = settings.Ipv4, Ipv6 = settings.Ipv6,
            Gateway4 = settings.Gateway4, Gateway6 = settings.Gateway6,
        } : null;

    public OutboundStateBody? State(OutboundState state) => null;
}
