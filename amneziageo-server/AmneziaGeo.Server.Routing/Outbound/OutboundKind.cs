namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// The ways traffic leaves the host.
/// </summary>
public static class OutboundKind
{
    /// <summary>
    /// Leaves through the uplink of the host itself.
    /// </summary>
    public const string Local = "local";

    /// <summary>
    /// Leaves through an AmneziaWG tunnel the host holds as a client.
    /// </summary>
    public const string Wg = "wg";

    /// <summary>
    /// Leaves through an AmneziaWG tunnel carried inside a websocket.
    /// </summary>
    public const string Ws = "ws";

    /// <summary>
    /// Tells whether a kind carries an AmneziaWG tunnel.
    /// </summary>
    public static bool IsAwg(string? kind) => kind is Wg or Ws;

    /// <summary>
    /// Tells whether a kind speaks to its server through a websocket proxy.
    /// </summary>
    public static bool HasProxy(string? kind) => kind == Ws;
}
