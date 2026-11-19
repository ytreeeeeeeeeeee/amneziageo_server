using System.Net.Sockets;
using AmneziaGeo.Server.Api.Configs;
using AmneziaGeo.Server.Api.Rules;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Routing.Balance;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// Probes the outbounds that are due one and lays the rules again when a verdict changes; the same round lays again
/// the interfaces and the rules of the outbounds and the interfaces of the endpoints the host lost, looking at the host
/// when the kernel told of a change.
/// </summary>
public sealed class ProbeWatch : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ProbeRunner _runner;
    private readonly ProbeLive _live;
    private readonly OutboundHost _host;
    private readonly BalanceLive _carrying;
    private readonly ConfigWatch _endpoints;
    private readonly DatabaseVersion _version;
    private readonly HostChanges _changes;
    private readonly TimeProvider _time;
    private readonly ILogger<ProbeWatch> _logger;
    private readonly Dictionary<string, DateTimeOffset> _resting = new(StringComparer.Ordinal);
    private IReadOnlyList<OutboundConfig> _outbounds = [];
    private long? _seen;
    private DateTimeOffset? _rulesAgain;
    private DateTimeOffset? _retireAgain;
    private bool _firewallPending;

    /// <summary>
    /// ctor
    /// </summary>
    public ProbeWatch(
        IServiceScopeFactory scopes,
        ProbeRunner runner,
        ProbeLive live,
        OutboundHost host,
        BalanceLive carrying,
        ConfigWatch endpoints,
        DatabaseVersion version,
        HostChanges changes,
        TimeProvider time,
        ILogger<ProbeWatch> logger)
    {
        _scopes = scopes;
        _runner = runner;
        _live = live;
        _host = host;
        _carrying = carrying;
        _endpoints = endpoints;
        _version = version;
        _changes = changes;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Probes every outbound that is due one.
    /// </summary>
    public async Task LookAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var services = scope.ServiceProvider;
            var changed = _changes.Take();
            await _endpoints.LookAsync(services, changed, ct).ConfigureAwait(false);
            var (outbounds, moved) = await KeepAsync(services, changed, ct).ConfigureAwait(false);
            foreach (var outbound in outbounds.Where(_runner.IsDue))
            {
                moved |= await _runner.RunAsync(outbound, ct).ConfigureAwait(false);
            }

            if (!moved)
            {
                return;
            }

            _carrying.Keep(_host.Carrying(outbounds));
            await services.GetRequiredService<RouteApplier>().SettleAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "the outbounds were not probed");
        }
    }

    // Reads the outbounds when the database changed and, when the host or the outbounds changed or something the host
    // refused is due to be tried again, lays again what the host lost of them while a change of the outbounds waits.
    private async Task<(IReadOnlyList<OutboundConfig> Outbounds, bool Moved)> KeepAsync(
        IServiceProvider services,
        bool changed,
        CancellationToken ct)
    {
        using var hold = await _host.HoldAsync(ct).ConfigureAwait(false);
        var version = _version.Read();
        var fresh = version is null || version != _seen;
        var carrying = fresh ? _host.Carrying(_outbounds) : null;
        var settingsChanged = false;
        if (fresh)
        {
            var reloaded = await services.GetRequiredService<OutboundStore>().ListAsync(ct).ConfigureAwait(false);
            settingsChanged = !_outbounds.Select(outbound => (outbound.Id, outbound.Mark, outbound.UpdatedUtc))
                .SequenceEqual(reloaded.Select(outbound => (outbound.Id, outbound.Mark, outbound.UpdatedUtc)));
            _outbounds = reloaded;
            _seen = version;
        }

        var outbounds = _outbounds;
        _live.Hold(outbounds.Select(one => one.Id));
        var now = _time.GetUtcNow();
        if (!changed && !fresh && !_resting.Values.Any(again => again <= now) && !(_rulesAgain <= now)
            && !(_retireAgain <= now))
        {
            return (outbounds, false);
        }

        if (fresh)
        {
            Forget(outbounds);
        }

        carrying ??= _host.Carrying(outbounds);
        var moved = false;
        if (fresh || !(_retireAgain > now))
        {
            try
            {
                moved = await _host.RetireMissingAsync(outbounds, ct).ConfigureAwait(false);
                _retireAgain = null;
            }
            catch (Exception ex) when (ex is HostNetworkException or SocketException)
            {
                _retireAgain = now + ConfigWatch.Retry;
                _logger.LogWarning(ex, "the resources of removed outbounds could not be released");
            }
        }

        moved |= await MendAsync(outbounds, fresh, now, ct).ConfigureAwait(false);
        _firewallPending |= moved || settingsChanged;
        if (fresh || !(_rulesAgain > now))
        {
            await KeepRulesAsync(outbounds, now, ct).ConfigureAwait(false);
        }

        moved |= !carrying.ToHashSet(StringComparer.Ordinal).SetEquals(_host.Carrying(outbounds));
        return (outbounds, moved);
    }

    // Lets go of the waits of the outbounds that are gone.
    private void Forget(IReadOnlyList<OutboundConfig> outbounds)
    {
        foreach (var gone in _resting.Keys.Where(name => outbounds.All(one => one.Name != name)).ToArray())
        {
            _resting.Remove(gone);
        }
    }

    // Restores outbound resources, retrying a refused operation within the wait only when the outbounds changed.
    private async Task<bool> MendAsync(IReadOnlyList<OutboundConfig> outbounds, bool fresh, DateTimeOffset now,
        CancellationToken ct)
    {
        var mended = false;
        foreach (var outbound in outbounds)
        {
            if (!fresh && _resting.TryGetValue(outbound.Name, out var again) && again > now)
            {
                continue;
            }

            try
            {
                var laid = await _host.MendAsync(outbound, ct).ConfigureAwait(false);
                _resting.Remove(outbound.Name);
                if (!laid)
                {
                    continue;
                }

                mended = true;
                _logger.LogWarning("the network resources of '{Outbound}' were restored", outbound.Name);
            }
            catch (Exception ex) when (ex is HostNetworkException or SocketException)
            {
                _resting[outbound.Name] = now + ConfigWatch.Retry;
                _logger.LogWarning(ex, "the network resources of '{Outbound}' could not be restored", outbound.Name);
            }
        }

        return mended;
    }

    // Lays again the rules of the outbounds the host lost.
    private async Task KeepRulesAsync(IReadOnlyList<OutboundConfig> outbounds, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (await _host.KeepSealAsync(ct).ConfigureAwait(false))
            {
                _logger.LogWarning("the rule that refuses marked packets no outbound takes was gone and is laid again");
            }

            foreach (var outbound in outbounds)
            {
                if (await _host.KeepRuleAsync(outbound, ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("the policy rule of '{Outbound}' was missing or differed and is applied again",
                        outbound.Name);
                }
            }

            if (_firewallPending)
            {
                await _host.FirewallAsync(outbounds, ct).ConfigureAwait(false);
                _firewallPending = false;
            }

            _rulesAgain = null;
        }
        catch (Exception ex) when (ex is HostNetworkException or SocketException)
        {
            _rulesAgain = now + ConfigWatch.Retry;
            _logger.LogWarning(ex, "the routing rules or firewall of the outbounds were not applied again");
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await LookAsync(stopping).ConfigureAwait(false);
                await _changes.WaitAsync(ProbeDefaults.Tick, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
