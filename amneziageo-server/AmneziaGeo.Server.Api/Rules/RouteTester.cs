using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Server.Api.Dns;
using AmneziaGeo.Server.Awg.Device;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Geo;
using AmneziaGeo.Server.Routing.Balance;
using AmneziaGeo.Server.Routing.Dns;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Route;

namespace AmneziaGeo.Server.Api.Rules;

/// <summary>
/// Tells where the rules the host carries send the traffic asked about.
/// </summary>
public sealed class RouteTester
{
    private readonly RouteApplier _applier;

    private readonly ClientStore _clients;

    private readonly ConfigStore _configs;

    private readonly BalanceLive _live;

    private readonly DnsSets _sets;

    private readonly DnsHost _dns;

    /// <summary>
    /// ctor
    /// </summary>
    public RouteTester(
        RouteApplier applier,
        ClientStore clients,
        ConfigStore configs,
        BalanceLive live,
        DnsSets sets,
        DnsHost dns)
    {
        _applier = applier;
        _clients = clients;
        _configs = configs;
        _live = live;
        _sets = sets;
        _dns = dns;
    }

    /// <summary>
    /// Returns where the rules send the traffic, or why the question is unusable.
    /// </summary>
    public async Task<(RouteFault? Fault, RouteTestResponse? Answer)> TestAsync(
        RouteTestRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = Host(request.Target);
        var name = IPAddress.TryParse(target, out var bare) ? string.Empty : target;
        if (name.Length > 0 && RouteRules.Target(name) is not { Kind: GeoRuleKind.Domain })
        {
            return (new RouteFault("bad-test-target", $"'{target}' is not a name or an address"), null);
        }

        var protocol = (request.Protocol ?? RouteProtocol.Tcp).Trim().ToLowerInvariant();
        if (protocol is not (RouteProtocol.Tcp or RouteProtocol.Udp))
        {
            return (new RouteFault("bad-protocol", $"'{protocol}' is not a protocol the traffic carries"), null);
        }

        if (Port(request.Port) is null || Port(request.SourcePort) is null)
        {
            return (new RouteFault("bad-port", "a port runs from 1 to 65535"), null);
        }

        var (fault, sources, inbound) = await FromAsync(request.Client, ct).ConfigureAwait(false);
        if (fault is not null)
        {
            return (fault, null);
        }

        var addresses = bare is null ? await AskAsync(name, ct).ConfigureAwait(false) : new[] { bare };
        var plan = await _applier.ReadAsync(ct).ConfigureAwait(false);
        var query = new RouteQuery
        {
            Name = name.ToLowerInvariant(),
            Addresses = addresses,
            Port = request.Port ?? 0,
            SourcePort = request.SourcePort ?? 0,
            Protocol = protocol,
            Sources = sources,
            Inbound = inbound,
        };

        var results = new List<RouteTestAddress>();
        foreach (var address in addresses.Distinct().OrderBy(one => one.AddressFamily).ThenBy(one => one.ToString(), StringComparer.Ordinal))
        {
            results.Add(Answer(plan, query with { Addresses = [address] }, address.ToString()));
        }

        var first = results.FirstOrDefault() ?? Answer(plan, query, "");
        return (null, new RouteTestResponse(
            first.Verdict,
            first.Guard,
            query.Name,
            [.. results.Select(one => one.Address)],
            inbound,
            [.. sources.Select(address => address.ToString())],
            first.Rule,
            first.Exit,
            first.Steps) { Results = results });
    }

    private RouteTestAddress Answer(RoutePlan plan, RouteQuery query, string address)
    {
        var verdict = RouteProbe.Test(plan, query, _sets.Holds);
        var rule = Rule(verdict.Leg, plan);
        var exit = verdict.Leg is { } leg && leg.Rule.Action == RouteAction.Out
            ? Exit(leg, plan.Ways)
            : null;
        return new(address, verdict.Verdict, verdict.Guard, rule, exit, verdict.Steps);
    }

    private static string Host(string? text)
    {
        var body = (text ?? string.Empty).Trim();

        return body.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(body, UriKind.Absolute, out var uri)
            ? uri.IdnHost.Trim('[', ']')
            : body.TrimEnd('.');
    }

    private static int? Port(int? port) => port is null or (>= 1 and <= 65535) ? port ?? 0 : null;

    private async Task<(RouteFault? Fault, IReadOnlyList<IPAddress> Sources, string Inbound)> FromAsync(
        string? client,
        CancellationToken ct)
    {
        var body = (client ?? string.Empty).Trim();
        if (body.Length == 0)
        {
            return (null, [], string.Empty);
        }

        var configs = await _configs.ListAsync(ct).ConfigureAwait(false);
        if (IPAddress.TryParse(body, out var address))
        {
            var holder = configs.FirstOrDefault(config => RouteProbe.Within(config.Address, address));

            return (null, [address], holder?.Name ?? string.Empty);
        }

        var clients = await _clients.ListAsync(ct).ConfigureAwait(false);
        var found = clients.FirstOrDefault(one => string.Equals(one.Name, body, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            return (new RouteFault("unknown-client", $"there is no client called '{body}'"), [], string.Empty);
        }

        var sources = found.Address
            .Select(text => AwgAllowedIp.TryParse(text, out var range) ? range.Address : null)
            .OfType<IPAddress>()
            .ToArray();

        return (null, sources, configs.FirstOrDefault(config => config.Id == found.ConfigId)?.Name ?? string.Empty);
    }

    private async Task<IReadOnlyList<IPAddress>> AskAsync(string name, CancellationToken ct)
    {
        var answered = await _dns.AskAsync(name, ct).ConfigureAwait(false);
        if (answered is not null)
        {
            return [.. answered.Distinct()];
        }

        try
        {
            return [.. (await System.Net.Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false)).Distinct()];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static RouteTestRule? Rule(RouteLeg? leg, RoutePlan plan)
    {
        if (leg is null)
        {
            return null;
        }

        var place = plan.Legs.Where(one => one.Rule.Id > 0).Select((one, at) => (one.Rule.Id, Place: at + 1))
            .FirstOrDefault(one => one.Id == leg.Rule.Id).Place;

        return new RouteTestRule(leg.Rule.Id, leg.Rule.Name, place, leg.Rule.Action, leg.Rule.Outbound);
    }

    private RouteTestExit Exit(RouteLeg leg, RouteWays ways)
    {
        var outbounds = ways.Outbounds;
        var balancers = ways.Balancers;
        var name = leg.Rule.Outbound;
        var group = balancers.FirstOrDefault(one => string.Equals(one.Name, name, StringComparison.Ordinal));
        var names = group?.Members ?? [name];
        var members = names
            .Select(member => outbounds.FirstOrDefault(one => string.Equals(one.Name, member, StringComparison.Ordinal)))
            .OfType<OutboundConfig>()
            .Select(outbound => new RouteTestMember(
                outbound.Name,
                outbound.IsEnabled,
                _live.Alive?.Contains(outbound.Name) ?? true,
                leg.Exit.Marks.Contains(outbound.Mark)))
            .ToArray();

        return new RouteTestExit(name, group is not null, group?.Strategy ?? string.Empty, members);
    }
}
