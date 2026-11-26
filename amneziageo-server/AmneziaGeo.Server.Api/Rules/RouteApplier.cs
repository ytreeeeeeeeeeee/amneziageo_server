using System.Net.Sockets;
using AmneziaGeo.Server.Api.Configs;
using AmneziaGeo.Server.Api.Dns;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Geo;
using AmneziaGeo.Server.Routing.Balance;
using AmneziaGeo.Server.Geo.Files;
using AmneziaGeo.Server.Routing.Access;
using AmneziaGeo.Server.Routing.Dns;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Names;
using AmneziaGeo.Server.Routing.Route;

namespace AmneziaGeo.Server.Api.Rules;

/// <summary>
/// Holds the plan the host was last given.
/// </summary>
public sealed class RoutePlans(TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HostTurn _turn = new();
    private long _rulesAgain = long.MaxValue;
    private RoutePlan? _held;

    internal bool RulesPending => Volatile.Read(ref _rulesAgain) != long.MaxValue;

    internal bool RulesDue => Volatile.Read(ref _rulesAgain) <= _time.GetUtcNow().UtcTicks;

    internal Task<IDisposable> HoldAsync(CancellationToken ct) => _turn.HoldAsync(ct);

    internal void Refused() => Volatile.Write(ref _rulesAgain, (_time.GetUtcNow() + ConfigWatch.Retry).UtcTicks);

    internal void Applied() => Volatile.Write(ref _rulesAgain, long.MaxValue);

    /// <summary>
    /// Returns the plan built last, or null when none was built yet.
    /// </summary>
    public RoutePlan? Held => Volatile.Read(ref _held);

    /// <summary>
    /// Raised when a plan is kept.
    /// </summary>
    public event Action? Kept;

    /// <summary>
    /// Keeps a plan as the one built last.
    /// </summary>
    public void Keep(RoutePlan plan)
    {
        Volatile.Write(ref _held, plan);
        Kept?.Invoke();
    }

    /// <inheritdoc/>
    public void Dispose() => _turn.Dispose();
}

/// <summary>
/// Builds the routing plan out of what the panel holds and puts it on the host.
/// </summary>
public sealed class RouteApplier
{
    private readonly RouteStore _rules;

    private readonly OutboundStore _outbounds;

    private readonly BalanceStore _balancers;

    private readonly ConfigStore _configs;

    private readonly GeoStore _geo;

    private readonly DnsStore _dns;

    private readonly DnsState _resolver;

    private readonly IGeoFileStore _files;

    private readonly RoutePlans _plans;

    private readonly BalanceLive _live;

    private readonly DnsSets _sets;

    private readonly ClientStore _clients;

    private readonly OutboundHost _host;

    private readonly AccessGate _gate;

    private readonly NameGate _names;

    private readonly TemplateStore _templates;

    private readonly StandInBook _book;

    private readonly IHostNetwork _network;

    private readonly ILogger<RouteApplier> _logger;

    /// <summary>
    /// ctor
    /// </summary>
    public RouteApplier(
        RouteStore rules,
        OutboundStore outbounds,
        BalanceStore balancers,
        ConfigStore configs,
        GeoStore geo,
        DnsStore dns,
        DnsState resolver,
        IGeoFileStore files,
        RoutePlans plans,
        BalanceLive live,
        DnsSets sets,
        ClientStore clients,
        OutboundHost host,
        AccessGate gate,
        NameGate names,
        TemplateStore templates,
        StandInBook book,
        IHostNetwork network,
        ILogger<RouteApplier> logger)
    {
        _network = network;
        _logger = logger;
        _names = names;
        _templates = templates;
        _book = book;
        _rules = rules;
        _outbounds = outbounds;
        _balancers = balancers;
        _configs = configs;
        _geo = geo;
        _dns = dns;
        _resolver = resolver;
        _files = files;
        _plans = plans;
        _live = live;
        _sets = sets;
        _clients = clients;
        _host = host;
        _gate = gate;
    }

    /// <summary>
    /// Expands the rules the panel holds over the geo databases and the outbounds.
    /// </summary>
    public async Task<RoutePlan> BuildAsync(CancellationToken ct)
    {
        var rules = await _rules.ListAsync(ct).ConfigureAwait(false);
        var outbounds = await _outbounds.ListAsync(ct).ConfigureAwait(false);
        var balancers = await _balancers.ListAsync(ct).ConfigureAwait(false);
        var configs = await _configs.ListAsync(ct).ConfigureAwait(false);
        var sources = await _geo.ListAsync(ct).ConfigureAwait(false);
        var resolver = _resolver.Settings ?? (await _dns.ReadAsync(ct).ConfigureAwait(false)) with { IsEnabled = false };
        var clients = await _clients.ListAsync(ct).ConfigureAwait(false);
        var basic = await _rules.ReadBasicAsync(ct).ConfigureAwait(false);
        var standIns = await StandInsAsync(resolver, ct).ConfigureAwait(false);
        _live.Keep(_host.Carrying(outbounds));
        var plan = RoutePlan.Build(
            rules,
            outbounds,
            GeoIndex.Load(sources, _files),
            [.. configs.Select(config => config.Name)],
            resolver,
            balancers,
            _live.Alive,
            clients,
            basic) with { Journal = _gate.Group, Reading = _names.Offer, StandIns = standIns, Unanswered = _resolver.KeptOut };

        _plans.Keep(plan);

        return plan;
    }

    /// <summary>
    /// Returns the plan built last, building one when there is none.
    /// </summary>
    public async Task<RoutePlan> ReadAsync(CancellationToken ct) =>
        _plans.Held ?? await BuildAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Puts the rules on the host.
    /// </summary>
    public Task<RoutePlan> ApplyAsync(CancellationToken ct) => ApplyAsync(false, ct);

    /// <summary>
    /// Puts the rules on the host anew when one of them names clients.
    /// </summary>
    public async Task FollowClientsAsync(CancellationToken ct)
    {
        _book.Stir();
        var plan = await ReadAsync(ct).ConfigureAwait(false);
        if (plan.Legs.Any(leg => leg.Rule.Clients.Count > 0))
        {
            await SettleAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Has the resolver take the templates anew and puts the rules on the host anew when the templates turned the
    /// stand-in addresses on or off.
    /// </summary>
    public async Task FollowStandInsAsync(CancellationToken ct)
    {
        _book.Stir();
        var plan = await ReadAsync(ct).ConfigureAwait(false);
        if (plan.StandIns != await StandInsAsync(plan.Dns, ct).ConfigureAwait(false))
        {
            await SettleAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts the rules on the host, scheduling a retry if it refuses.
    /// </summary>
    public Task<RoutePlan> SettleAsync(CancellationToken ct) => ApplyAsync(true, ct);

    internal async Task RetryAsync(CancellationToken ct)
    {
        if (!_plans.RulesDue)
        {
            return;
        }

        using var hold = await _plans.HoldAsync(ct).ConfigureAwait(false);
        if (_plans.RulesDue)
        {
            await ApplyHeldAsync(true, ct).ConfigureAwait(false);
        }
    }

    private async Task<RoutePlan> ApplyAsync(bool settle, CancellationToken ct)
    {
        using var hold = await _plans.HoldAsync(ct).ConfigureAwait(false);
        return await ApplyHeldAsync(settle, ct).ConfigureAwait(false);
    }

    // Tells whether the resolver answers some clients with stand-in addresses.
    private async Task<bool> StandInsAsync(DnsSettings resolver, CancellationToken ct) =>
        resolver.IsEnabled
        && (await _templates.ListAsync(ct).ConfigureAwait(false)).Any(template => template.StandIns);

    private async Task<RoutePlan> ApplyHeldAsync(bool settle, CancellationToken ct)
    {
        var plan = await BuildAsync(ct).ConfigureAwait(false);
        RoutePlan laid;
        try
        {
            laid = await RulesAsync(plan, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostNetworkException or SocketException)
        {
            _plans.Refused();
            _logger.LogWarning(ex, "[FIX] the host refused {Table}; the routing rules will be retried", RouteRuleset.TableName);
            if (settle)
            {
                return plan;
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            _plans.Refused();
            throw;
        }

        if (_plans.RulesPending)
        {
            _plans.Applied();
            _logger.LogInformation("[FIX] the routing rules in {Table} were restored", RouteRuleset.TableName);
        }

        await GuardAsync(laid, ct).ConfigureAwait(false);

        return laid;
    }

    // Puts the guards of the resolver on the host, leaving the rules as they were laid when the host refuses them.
    private async Task GuardAsync(RoutePlan plan, CancellationToken ct)
    {
        try
        {
            await _network.FirewallAsync(RouteRuleset.Guarding(plan), ct).ConfigureAwait(false);
        }
        catch (HostNetworkException ex)
        {
            _logger.LogWarning(ex, "the host did not take the guards of the resolver");
        }
    }

    private async Task<RoutePlan> RulesAsync(RoutePlan plan, CancellationToken ct)
    {
        try
        {
            await _sets.LayAsync(RouteRuleset.Text(plan), plan, ct).ConfigureAwait(false);

            return plan;
        }
        catch (HostNetworkException) when (plan.Reading is { Port: not null, Queue: not null })
        {
            _names.ClosePort();
            var laid = await RulesAsync(plan with { Reading = plan.Reading with { Port = null } }, ct).ConfigureAwait(false);
            _plans.Keep(laid);

            return laid;
        }
        catch (HostNetworkException ex) when (plan.Reading is not null)
        {
            var plain = plan with { Reading = null };
            var laid = await RulesAsync(plain, ct).ConfigureAwait(false);
            _names.CloseQueue($"the host did not take the rules that show connections to the panel: {ex.Message}");
            _names.ClosePort();
            _plans.Keep(laid);

            return laid;
        }
        catch (HostNetworkException ex) when (plan.Journal is not null)
        {
            var bare = plan with { Journal = null };
            await _sets.LayAsync(RouteRuleset.Text(bare), bare, ct).ConfigureAwait(false);
            _gate.Close($"the host did not take the rules that log the connections: {ex.Message}");
            _plans.Keep(bare);

            return bare;
        }
    }
}
