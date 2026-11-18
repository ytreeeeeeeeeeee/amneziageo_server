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

    private sealed record Standing(OutboundConfig Desired, OutboundConfig? Resources, bool Applied, string Fault);

    // shortcut: resource ownership lives in memory; persist it if orphan cleanup must survive a process crash.
    private readonly ConcurrentDictionary<(long Id, uint Mark), Standing> _standing = new();

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
    /// Refuses a name, mark or managed interface still held by another outbound, including unfinished cleanup.
    /// The optional owner identifies an existing outbound while its draft has no identity yet.
    /// </summary>
    public OutboundFault? CheckReservation(OutboundConfig candidate, OutboundConfig? owner = null)
    {
        var own = Owner(owner ?? candidate);
        foreach (var (key, entry) in _standing)
        {
            if (key == own || entry.Resources is not { } resource)
            {
                continue;
            }

            if ((candidate.Mark != 0 && candidate.Mark == resource.Mark)
                || candidate.Name == resource.Name
                || (candidate.UsesInterface && candidate.InterfaceName == resource.InterfaceName
                    && (candidate.OwnsInterface || resource.OwnsInterface)))
            {
                return new("host-refused", $"the host still holds resources of outbound '{resource.Name}'");
            }
        }

        return null;
    }

    /// <summary>
    /// Puts one outbound on the host, or takes it off when it is turned off.
    /// </summary>
    public async Task<OutboundState> ApplyAsync(OutboundConfig outbound, CancellationToken ct, OutboundConfig? previous = null)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var key = Owner(outbound);
        var known = _standing.GetValueOrDefault(key);
        var standing = new Standing(outbound, known?.Resources, false, "the outbound operation has not finished");
        _standing[key] = standing;

        try
        {
            if (!outbound.IsEnabled && known is not null && standing.Resources is null)
            {
                _standing.TryRemove(key, out _);
                return OutboundState.Missing(outbound.Name);
            }

            if (known is null)
            {
                var adopted = previous ?? outbound;
                RequireAvailable(adopted, outbound);
                standing = standing with { Resources = adopted };
                _standing[key] = standing;
            }

            if (outbound.IsEnabled)
            {
                RequireAvailable(outbound);
            }

            await _network.SealAsync(ct).ConfigureAwait(false);
            _sealed = true;
            if (standing.Resources is { } retiring && (!outbound.IsEnabled || DifferentResources(retiring, outbound)))
            {
                RequireAvailable(retiring, outbound);
                await TakeOffAsync(retiring, ct).ConfigureAwait(false);
                standing = standing with { Resources = null };
                _standing[key] = standing;
            }

            if (!outbound.IsEnabled)
            {
                _standing.TryRemove(key, out _);
                return OutboundState.Missing(outbound.Name);
            }

            RequireAvailable(outbound);
            var driver = Driver(outbound);
            standing = standing with { Resources = outbound };
            _standing[key] = standing;
            await driver.ApplyAsync(outbound, ct).ConfigureAwait(false);

            await _network
                .RuleAsync(outbound.Mark, outbound.Table, OutboundRules.PriorityOf(outbound.Mark), true, ct)
                .ConfigureAwait(false);
            _standing[key] = standing with { Applied = true, Fault = string.Empty };

            return State(outbound);
        }
        catch (OperationCanceledException)
        {
            _standing[key] = standing with { Fault = "the outbound operation was canceled before completion" };
            throw;
        }
        catch (Exception ex)
            when (ex is HostNetworkException or SocketException or InvalidOperationException)
        {
            _standing[key] = standing with { Fault = ex.Message };
            return OutboundState.Missing(outbound.Name, ex.Message);
        }
    }

    /// <summary>
    /// Takes one outbound off the host.
    /// </summary>
    public async Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var state = await ApplyAsync(outbound with { IsEnabled = false }, ct).ConfigureAwait(false);
        if (state.Fault.Length > 0)
        {
            throw new HostNetworkException(state.Fault);
        }
    }

    /// <summary>
    /// Lays again the rule of an outbound the host lost, telling whether it had to.
    /// </summary>
    public async Task<bool> KeepRuleAsync(OutboundConfig outbound, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var key = Owner(outbound);
        if (!outbound.IsEnabled || !_standing.TryGetValue(key, out var standing) || !standing.Applied
            || !Matches(standing, outbound))
        {
            return false;
        }

        try
        {
            RequireAvailable(outbound);
            return await _network
                .RuleAsync(outbound.Mark, outbound.Table, OutboundRules.PriorityOf(outbound.Mark), true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostNetworkException or SocketException or OperationCanceledException)
        {
            _standing[key] = standing with { Applied = false, Fault = ex.Message };
            throw;
        }
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

        var key = Owner(outbound);
        var standing = _standing.GetValueOrDefault(key);
        if ((standing is not null && (!standing.Applied || !Matches(standing, outbound)))
            || (standing is null && outbound.IsEnabled))
        {
            var state = await ApplyAsync(outbound, ct).ConfigureAwait(false);
            if (state.Fault.Length > 0)
            {
                throw new HostNetworkException(state.Fault);
            }

            return true;
        }

        if (!outbound.IsEnabled)
        {
            return false;
        }

        try
        {
            RequireAvailable(outbound);
            return await Driver(outbound).MendAsync(outbound, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostNetworkException or SocketException or OperationCanceledException)
        {
            _standing[key] = standing! with { Applied = false, Fault = ex.Message };
            throw;
        }
    }

    /// <summary>
    /// Retries cleanup even after the database row was removed; a reused mark belongs to a different owner.
    /// </summary>
    public async Task<bool> RetireMissingAsync(IReadOnlyList<OutboundConfig> outbounds, CancellationToken ct)
    {
        var owners = outbounds.Select(Owner).ToHashSet();
        var changed = false;
        foreach (var (key, standing) in _standing.ToArray())
        {
            if (owners.Contains(key))
            {
                continue;
            }

            await WithdrawAsync(standing.Desired, ct).ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }

    private async Task TakeOffAsync(OutboundConfig outbound, CancellationToken ct)
    {
        await _network
            .RuleAsync(outbound.Mark, outbound.Table, OutboundRules.PriorityOf(outbound.Mark), false, ct)
            .ConfigureAwait(false);
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
        var retained = _standing.Values.Where(entry => !entry.Applied && entry.Resources is not null)
            .Select(entry => entry.Resources! with { IsEnabled = true });
        await _network.FirewallAsync(OutboundRuleset.Text([.. outbounds, .. retained], uplink), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns what the host holds for one outbound.
    /// </summary>
    public OutboundState State(OutboundConfig outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        if (_standing.TryGetValue(Owner(outbound), out var standing) && (!standing.Applied || !Matches(standing, outbound)))
        {
            return OutboundState.Missing(outbound.Name,
                standing.Applied ? "the saved outbound has not been applied" : standing.Fault);
        }

        if (!outbound.IsEnabled)
        {
            return OutboundState.Missing(outbound.Name);
        }

        if (CheckReservation(outbound) is { } conflict)
        {
            return OutboundState.Missing(outbound.Name, conflict.Message);
        }

        var reading = outbound.Probe.Length == 0 ? null : _probes?.Find(outbound);
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

    private void RequireAvailable(OutboundConfig candidate, OutboundConfig? owner = null)
    {
        if (CheckReservation(candidate, owner) is { } fault)
        {
            throw new HostNetworkException(fault.Message);
        }
    }

    private static (long Id, uint Mark) Owner(OutboundConfig outbound) => (outbound.Id, outbound.Mark);

    private static bool DifferentResources(OutboundConfig left, OutboundConfig right) =>
        left.Name != right.Name || left.Kind != right.Kind || left.InterfaceName != right.InterfaceName || left.Table != right.Table;

    private static bool Matches(Standing standing, OutboundConfig outbound) =>
        standing.Desired.UpdatedUtc == outbound.UpdatedUtc && standing.Desired.IsEnabled == outbound.IsEnabled
        && !DifferentResources(standing.Desired, outbound);
}
