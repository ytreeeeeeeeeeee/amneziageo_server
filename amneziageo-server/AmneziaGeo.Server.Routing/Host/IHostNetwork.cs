using System.Net.Sockets;

namespace AmneziaGeo.Server.Routing.Host;

/// <summary>
/// Puts interfaces, addresses, routes and firewall rules on the host.
/// </summary>
public interface IHostNetwork
{
    /// <summary>
    /// Tells whether the host carries an interface.
    /// </summary>
    bool HasLink(string name);

    /// <summary>
    /// Tells whether an interface is on the host and up.
    /// </summary>
    bool IsUp(string name);

    /// <summary>
    /// Adds an AmneziaWG interface.
    /// </summary>
    Task AddLinkAsync(string name, CancellationToken ct);

    /// <summary>
    /// Takes an interface off the host.
    /// </summary>
    Task RemoveLinkAsync(string name, CancellationToken ct);

    /// <summary>
    /// Replaces the address ranges of an interface.
    /// </summary>
    Task AddressAsync(string name, IReadOnlyList<string> addresses, CancellationToken ct);

    /// <summary>
    /// Sets the packet size of an interface and brings it up.
    /// </summary>
    Task UpAsync(string name, int mtu, CancellationToken ct);

    /// <summary>
    /// Ensures both default routes go through the interface, telling whether either changed.
    /// </summary>
    Task<bool> RouteAsync(string name, int table, CancellationToken ct);

    /// <summary>Sets a default route through a device and optional gateway for one IP family, telling whether it changed.</summary>
    Task<bool> DefaultRouteAsync(string name, int table, AddressFamily family, string gateway, CancellationToken ct);

    /// <summary>Sets an unreachable default route for one IP family, telling whether it changed.</summary>
    Task<bool> UnreachableRouteAsync(int table, AddressFamily family, CancellationToken ct);

    /// <summary>
    /// Clears a routing table.
    /// </summary>
    Task ClearRouteAsync(int table, CancellationToken ct);

    /// <summary>
    /// Adds or removes the rule that sends a marked packet into a routing table, telling whether it had to.
    /// </summary>
    Task<bool> RuleAsync(uint mark, int table, int priority, bool present, CancellationToken ct);

    /// <summary>
    /// Adds the rule that refuses a marked packet no outbound takes, telling whether it had to.
    /// </summary>
    Task<bool> SealAsync(CancellationToken ct);

    /// <summary>
    /// Returns the interface the host reaches the internet through.
    /// </summary>
    Task<string> UplinkAsync(CancellationToken ct);

    /// <summary>
    /// Lets the host pass packets between interfaces, in both families.
    /// </summary>
    Task ForwardingAsync(CancellationToken ct);

    /// <summary>
    /// Puts a firewall ruleset on the host, replacing the one it holds.
    /// </summary>
    Task FirewallAsync(string ruleset, CancellationToken ct);

    /// <summary>
    /// Reads a firewall ruleset without putting it on the host.
    /// </summary>
    Task CheckFirewallAsync(string ruleset, CancellationToken ct);

    /// <summary>
    /// Returns an inet table of the firewall in the JSON of nft, empty when the host holds no such table.
    /// </summary>
    Task<string> ReadFirewallAsync(string table, CancellationToken ct);

    /// <summary>
    /// Has a routing table deliver what it is asked about to the host itself, in both families.
    /// </summary>
    Task LocalRouteAsync(int table, CancellationToken ct);

    /// <summary>
    /// Returns how many packets a counter of an inet table of the firewall has seen, or null when the host holds no
    /// such counter.
    /// </summary>
    Task<long?> CounterAsync(string table, string name, CancellationToken ct);
}
