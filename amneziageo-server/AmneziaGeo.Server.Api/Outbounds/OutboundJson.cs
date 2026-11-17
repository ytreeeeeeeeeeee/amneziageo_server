using System.Text.Json;
using AmneziaGeo.Server.Routing.Outbound;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// The composition root registers the supported wire implementations.
/// </summary>
public static class OutboundJson
{
    /// <summary>
    /// Registers the request, response and state types the outbound API carries.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.AllowOutOfOrderMetadataProperties = true;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(OutboundRequest))
            {
                info.CreateObject = () => throw new JsonException("the outbound kind must be specified");
                info.PolymorphismOptions = new()
                {
                    TypeDiscriminatorPropertyName = "kind",
                    DerivedTypes =
                    {
                        new(typeof(LocalOutboundRequest), OutboundKind.Local),
                        new(typeof(AwgOutboundRequest), OutboundKind.Wg),
                        new(typeof(WsOutboundRequest), OutboundKind.Ws),
                    },
                };
            }

            if (typeof(OutboundRequest).IsAssignableFrom(info.Type))
            {
                info.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            }

            if (info.Type == typeof(OutboundResponse))
            {
                info.PolymorphismOptions = new()
                    { DerivedTypes = { new(typeof(LocalOutboundResponse)), new(typeof(AwgOutboundResponse)) } };
            }

            if (info.Type == typeof(OutboundStateBody))
            {
                info.PolymorphismOptions = new()
                    { DerivedTypes = { new(typeof(LocalOutboundStateBody)), new(typeof(AwgOutboundStateBody)) } };
            }
        });
        options.TypeInfoResolverChain.Insert(0, resolver);
    }
}
