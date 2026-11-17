using System.Collections.Concurrent;
using System.Net.Sockets;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Host;

/// <summary>
/// Puts the outbounds the panel holds on the host and reads back what the host carries.
/// </summary>
public sealed class OutboundHost : IDisposable
{
    private readonly IHostNetwork _network;

    private readonly IReadOnlyList<IOutboundDriver> _drivers;

    private readonly ProbeLive? _probes;

    private readonly ConcurrentDictionary<uint, bool> _ruled = new();

    private readonly HostTurn _turn = new();

    private volatile bool _sealed;

    /// <summary>
    /// ctor
    /// </summary>
    public OutboundHost(
        IHostNetwork network,
        IEnumerable<IOutboundDriver> drivers,
        ProbeLive? probes = null)
    {
        _network = network;
        _drivers = [.. drivers];
        _probes = probes;
    }

    /// <summary>
    /// Waits for the change of the outbounds that goes on and holds them until the hold is let go.
    /// </summary>
    public Task<IDisposable> HoldAsync(CancellationToken ct) => _turn.HoldAsync(ct);

    /// <summary>
    /// Lets the gate go.
    /// </summary>
    public void Dispose() => _turn.Dispose();

    /// <summary>
    /// Puts one outbound on the host, or takes it off when it is turned off.
    /// </summary>
    public async Task<OutboundState> ApplyAsync(OutboundConfig outbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        try
        {
            await _network.SealAsync(ct).ConfigureAwait(false);
            _sealed = true;
            if (!outbound.IsEnabled)
            {
                await TakeOffAsync(outbound, ct).ConfigureAwait(false);

                return OutboundState.Missing(outbound.Name);
            }

            await Driver(outbound).ApplyAsync(outbound, ct).ConfigureAwait(false);

            await _network
                .RuleAsync(outbound.Mark, outbound.Table, OutboundRules.PriorityOf(outbound.Mark), true, ct)
                .ConfigureAwait(false);
            _ruled[outbound.Mark] = true;

            return State(outbound);
        }
        catch (Exception ex)
            when (ex is HostNetworkException or SocketException or InvalidOperationException)
        {
            return OutboundState.Missing(outbound.Name, ex.Message);
        }
    }

    /// <summary>
    /// Takes one outbound off the host.
    /// </summary>
    public async Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        await _network.SealAsync(ct).ConfigureAwait(false);
        _sealed = true;
        await TakeOffAsync(outbound, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lays again the rule of an outbound the host lost, telling whether it had to.
    /// </summary>
    public async Task<bool> KeepRuleAsync(OutboundConfig outbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        if (!outbound.IsEnabled || !_ruled.ContainsKey(outbound.Mark))
        {
            return false;
        }

        return await _network
            .RuleAsync(outbound.Mark, outbound.Table, OutboundRules.PriorityOf(outbound.Mark), true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lays again the rule that refuses a marked packet no outbound takes when the host lost it, telling whether it
    /// had to.
    /// </summary>
    public async Task<bool> KeepSealAsync(CancellationToken ct) =>
        _sealed && await _network.SealAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Brings up the interface of an outbound the host holds down or lost, telling whether it had to.
    /// </summary>
    public async Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        if (!outbound.IsEnabled)
        {
            return false;
        }

        return await Driver(outbound).MendAsync(outbound, ct).ConfigureAwait(false);
    }

    private async Task TakeOffAsync(OutboundConfig outbound, CancellationToken ct)
    {
        await _network
            .RuleAsync(outbound.Mark, outbound.Table, OutboundRules.PriorityOf(outbound.Mark), false, ct)
            .ConfigureAwait(false);
        _ruled.TryRemove(outbound.Mark, out _);

        await Driver(outbound).WithdrawAsync(outbound, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts every outbound the panel holds on the host and returns what the host carries.
    /// </summary>
    public async Task<IReadOnlyList<OutboundState>> SyncAsync(
        IReadOnlyList<OutboundConfig> outbounds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbounds);

        var states = new List<OutboundState>(outbounds.Count);
        foreach (var outbound in outbounds)
        {
            states.Add(await ApplyAsync(outbound, ct).ConfigureAwait(false));
        }

        await FirewallAsync(outbounds, ct).ConfigureAwait(false);

        return states;
    }

    /// <summary>
    /// Puts the rules that let traffic out through the outbounds on the host.
    /// </summary>
    public async Task FirewallAsync(IReadOnlyList<OutboundConfig> outbounds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbounds);

        var uplink = await _network.UplinkAsync(ct).ConfigureAwait(false);
        await _network.FirewallAsync(OutboundRuleset.Text(outbounds, uplink), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns what the host holds for one outbound.
    /// </summary>
    public OutboundState State(OutboundConfig outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var reading = _probes?.Find(outbound.Name);
        try
        {
            return Driver(outbound).State(outbound) with { Probe = reading };
        }
        catch (Exception ex) when (ex is HostNetworkException or IOException or InvalidOperationException)
        {
            return OutboundState.Missing(outbound.Name, ex.Message);
        }
    }

    /// <summary>
    /// Returns what the host holds for every outbound.
    /// </summary>
    public IReadOnlyList<OutboundState> States(IReadOnlyList<OutboundConfig> outbounds)
    {
        ArgumentNullException.ThrowIfNull(outbounds);

        return [.. outbounds.Select(State)];
    }

    /// <summary>
    /// Names the outbounds the host carries traffic through right now.
    /// </summary>
    public IReadOnlyList<string> Carrying(IReadOnlyList<OutboundConfig> outbounds)
    {
        ArgumentNullException.ThrowIfNull(outbounds);

        return [.. States(outbounds).Where(state => state.Carries).Select(state => state.Name)];
    }

    private IOutboundDriver Driver(OutboundConfig outbound) =>
        _drivers.FirstOrDefault(driver => driver.Supports(outbound.Kind))
        ?? throw new InvalidOperationException($"no driver carries the outbound kind '{outbound.Kind}'");
}
