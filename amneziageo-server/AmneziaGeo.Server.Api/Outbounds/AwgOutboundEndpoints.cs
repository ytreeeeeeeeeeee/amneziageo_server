using AmneziaGeo.Server.Api.Configs;
using AmneziaGeo.Server.Core.Crypto;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// The routes that create keys and import configurations for AmneziaWG outbounds.
/// </summary>
public static class AwgOutboundEndpoints
{
    /// <summary>
    /// Maps the routes that create keys and read AmneziaWG client configurations.
    /// </summary>
    public static RouteGroupBuilder MapAwgOutbounds(this RouteGroupBuilder routes)
    {
        routes.MapPost("/awg/keys", () =>
        {
            var pair = Curve25519.Create();

            return Results.Ok(new KeyPairResponse(pair.PrivateKey, pair.PublicKey));
        });
        routes.MapPost("/awg/import", (AwgOutboundImportRequest request, OutboundAnswers answers) =>
        {
            var name = (request.Name ?? string.Empty).Trim();
            var result = AwgOutboundImport.Read(request.Config, name.Length > 0 ? name : "out0");

            return result.Outbound is null
                ? Results.Json(new { error = result.Fault!.Code, message = result.Fault.Message }, statusCode: 400)
                : Results.Ok(answers.Outbound(result.Outbound, null, true));
        });

        return routes;
    }
}
