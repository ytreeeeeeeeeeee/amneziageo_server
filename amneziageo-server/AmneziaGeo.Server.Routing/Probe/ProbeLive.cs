using System.Collections.Concurrent;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Routing.Probe;

/// <summary>
/// What the probes of the outbounds came to.
/// </summary>
public sealed class ProbeLive
{
    private sealed record Entry(OutboundConfig Config, ProbeReading Reading);

    private readonly ConcurrentDictionary<long, Entry> _readings = new();

    /// <summary>
    /// Returns what the probes of an outbound came to, or null when it was never probed.
    /// </summary>
    public ProbeReading? Find(long id) => _readings.GetValueOrDefault(id)?.Reading;

    /// <summary>
    /// Returns a verdict only for the enabled probe and the configuration revision it actually checked.
    /// </summary>
    public ProbeReading? Find(OutboundConfig outbound)
    {
        if (outbound.Probe.Length == 0 || !_readings.TryGetValue(outbound.Id, out var entry))
        {
            return null;
        }

        return SameScope(entry.Config, outbound) ? entry.Reading : null;
    }

    /// <summary>
    /// Keeps what a probe came to and tells whether the outbound changed sides.
    /// </summary>
    public bool Keep(OutboundConfig outbound, ProbeOutcome outcome, DateTimeOffset at, int falls = ProbeDefaults.Falls)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        if (outcome == ProbeOutcome.Skipped)
        {
            return false;
        }

        var changed = false;
        Entry Record(Entry? current)
        {
            changed = false;
            if (current is not null && current.Config.UpdatedUtc > outbound.UpdatedUtc)
            {
                return current;
            }

            var held = current is not null && SameScope(current.Config, outbound) ? current.Reading : null;
            var missed = outcome == ProbeOutcome.Missed ? (held?.Falls ?? 0) + 1 : 0;
            var reading = new ProbeReading(missed < Math.Max(falls, 1), missed, at);
            changed = (held?.IsReached ?? true) != reading.IsReached;
            return new(outbound, reading);
        }

        _readings.AddOrUpdate(outbound.Id, _ => Record(null), (_, current) => Record(current));
        return changed;
    }

    private static bool SameScope(OutboundConfig left, OutboundConfig right) =>
        left.Id == right.Id && left.UpdatedUtc == right.UpdatedUtc
        && left.Kind == right.Kind && left.InterfaceName == right.InterfaceName
        && left.Mark == right.Mark && left.Table == right.Table && left.Probe == right.Probe;

    /// <summary>
    /// Drops what the probes of an outbound came to.
    /// </summary>
    public void Forget(long id) => _readings.TryRemove(id, out _);

    /// <summary>
    /// Drops what the probes came to for every outbound but the IDs still held.
    /// </summary>
    public void Hold(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var held = new HashSet<long>(ids);
        foreach (var id in _readings.Keys.Where(one => !held.Contains(one)))
        {
            _readings.TryRemove(id, out _);
        }
    }
}
