using AmneziaGeo.Server.Routing.Outbound;
using Microsoft.EntityFrameworkCore;

namespace AmneziaGeo.Server.Dal;

/// <summary>
/// What managing an outbound produced.
/// </summary>
public enum OutboundOutcome
{
    Ok = 0,
    Invalid = 1,
    NameTaken = 2,
    Unknown = 3,
    NoMark = 4,
}

/// <summary>
/// The outbound a command produced, and why it was refused.
/// </summary>
public sealed record OutboundResult(
    OutboundOutcome Outcome,
    string Code,
    string Message,
    OutboundConfig? Record)
{
    /// <summary>
    /// Tells whether the command went through.
    /// </summary>
    public bool IsOk => Outcome == OutboundOutcome.Ok;

    /// <summary>
    /// Returns the outbound a command produced.
    /// </summary>
    public static OutboundResult Done(OutboundConfig outbound) =>
        new(OutboundOutcome.Ok, string.Empty, string.Empty, outbound);

    /// <summary>
    /// Returns a refusal with the reason behind it.
    /// </summary>
    public static OutboundResult No(OutboundOutcome outcome, string code, string message) =>
        new(outcome, code, message, null);
}

/// <summary>
/// Adds, changes and removes the outbounds the panel holds.
/// </summary>
public sealed class OutboundStore
{
    private readonly IReadOnlyList<IOutboundEntityFactory> _factories;
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>
    /// ctor
    /// </summary>
    public OutboundStore(AppDbContext db, IEnumerable<IOutboundEntityFactory> factories, TimeProvider? time = null)
    {
        _db = db;
        _factories = [.. factories];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns every outbound the panel holds, in the order they are read in.
    /// </summary>
    public async Task<IReadOnlyList<OutboundConfig>> ListAsync(CancellationToken ct)
    {
        var found = await _db.Outbounds
            .AsNoTracking()
            .OrderBy(outbound => outbound.Position)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. found.Select(one => one.ToConfig())];
    }

    /// <summary>
    /// Returns one outbound, or null when the panel holds none under the number.
    /// </summary>
    public async Task<OutboundConfig?> FindAsync(long id, CancellationToken ct)
    {
        var found = await _db.Outbounds
            .AsNoTracking()
            .FirstOrDefaultAsync(outbound => outbound.Id == id, ct)
            .ConfigureAwait(false);
        return found is null ? null : found.ToConfig();
    }

    /// <summary>
    /// Adds the outbound that leaves through the uplink of the host, once, to a fresh database.
    /// </summary>
    public Task SeedAsync(CancellationToken ct) => _db.AloneAsync(() => AddDirectAsync(ct), ct);

    // Adds the outbound through the uplink of the host when the database holds no outbound.
    private async Task AddDirectAsync(CancellationToken ct)
    {
        if (await _db.Outbounds.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var now = _time.GetUtcNow();
        var entity = Create(LocalOutboundDefaults.Direct());
        entity.CreatedUtc = now;
        entity.UpdatedUtc = now;
        _db.Outbounds.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds an outbound, giving it the first free mark and the place at the end.
    /// </summary>
    public async Task<OutboundResult> AddAsync(OutboundConfig draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (await TakenAsync(draft.Name, 0, ct).ConfigureAwait(false))
        {
            return Taken(draft.Name);
        }

        var mark = await FreeMarkAsync(ct).ConfigureAwait(false);
        if (mark == 0)
        {
            return OutboundResult.No(OutboundOutcome.NoMark, "no-mark", "the panel holds as many outbounds as it can");
        }

        var full = draft with
        {
            Mark = mark,
            Table = OutboundRules.TableOf(mark, draft.UsesInterface),
            Position = await NextPositionAsync(ct).ConfigureAwait(false),
        };
        if (OutboundRules.Check(full) is { } fault)
        {
            return OutboundResult.No(OutboundOutcome.Invalid, fault.Code, fault.Message);
        }

        var now = _time.GetUtcNow();
        var entity = Create(full);
        entity.CreatedUtc = now;
        entity.UpdatedUtc = now;
        _db.Outbounds.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return OutboundResult.Done(entity.ToConfig());
    }

    /// <summary>
    /// Replaces the settings of an outbound, keeping its mark and its place and carrying a new name into what names it.
    /// </summary>
    public async Task<OutboundResult> ChangeAsync(long id, OutboundConfig draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var entity = await _db.Outbounds.FirstOrDefaultAsync(outbound => outbound.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return Missing(id);
        }

        if (await TakenAsync(draft.Name, id, ct).ConfigureAwait(false))
        {
            return Taken(draft.Name);
        }

        var mark = (uint)entity.Mark;
        var full = draft with
        {
            Mark = mark,
            Table = OutboundRules.TableOf(mark, draft.UsesInterface),
            Position = entity.Position
        };
        if (OutboundRules.Check(full) is { } fault)
        {
            return OutboundResult.No(OutboundOutcome.Invalid, fault.Code, fault.Message);
        }

        var old = entity.Name;
        var now = RevisionAfter(entity.UpdatedUtc);
        var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var disposal = transaction.ConfigureAwait(false);
        var replacement = Create(full);
        if (entity.GetType() != replacement.GetType())
        {
            replacement.Id = entity.Id;
            replacement.CreatedUtc = entity.CreatedUtc;
            _db.Outbounds.Remove(entity);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            entity = replacement;
            _db.Outbounds.Add(entity);
        }
        else
        {
            entity.Update(full);
        }

        entity.UpdatedUtc = now;
        await NameFollow.WayAsync(_db, old, entity.Name, now, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return OutboundResult.Done(entity.ToConfig());
    }

    /// <summary>
    /// Turns an outbound on or off.
    /// </summary>
    public async Task<OutboundResult> SwitchAsync(long id, bool on, CancellationToken ct)
    {
        var entity = await _db.Outbounds.FirstOrDefaultAsync(outbound => outbound.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return Missing(id);
        }

        entity.IsEnabled = on;
        entity.UpdatedUtc = RevisionAfter(entity.UpdatedUtc);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return OutboundResult.Done(entity.ToConfig());
    }

    /// <summary>
    /// Removes an outbound, unless a rule, a balancer or the resolver leaves through it; live names the outbound
    /// the running resolver asks through.
    /// </summary>
    public async Task<OutboundResult> RemoveAsync(long id, string live, CancellationToken ct)
    {
        var entity = await _db.Outbounds.FirstOrDefaultAsync(outbound => outbound.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return Missing(id);
        }

        if (await UserAsync(entity.Name, live, ct).ConfigureAwait(false) is { } user)
        {
            return OutboundResult.No(OutboundOutcome.Invalid, "outbound-in-use", user);
        }

        var gone = entity.ToConfig();
        _db.Outbounds.Remove(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return OutboundResult.Done(gone);
    }

    /// <summary>
    /// Swaps an outbound with the one above or below it.
    /// </summary>
    public async Task<OutboundResult> MoveAsync(long id, bool up, CancellationToken ct)
    {
        var entity = await _db.Outbounds.FirstOrDefaultAsync(outbound => outbound.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return Missing(id);
        }

        var neighbour = up
            ? await _db.Outbounds.Where(one => one.Position < entity.Position)
                .OrderByDescending(one => one.Position)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
            : await _db.Outbounds.Where(one => one.Position > entity.Position)
                .OrderBy(one => one.Position)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        if (neighbour is null)
        {
            return OutboundResult.Done(entity.ToConfig());
        }

        (entity.Position, neighbour.Position) = (neighbour.Position, entity.Position);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return OutboundResult.Done(entity.ToConfig());
    }

    private async Task<string?> UserAsync(string name, string live, CancellationToken ct)
    {
        if (await _db.Rules.AnyAsync(rule => rule.Outbound == name, ct).ConfigureAwait(false))
        {
            return $"a rule leaves through the outbound '{name}'";
        }

        var balancers = await _db.Balancers.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var balancer = balancers.FirstOrDefault(one => NameFollow.Names(one.Members, name, StringComparer.Ordinal));
        if (balancer is not null)
        {
            return $"the balancer '{balancer.Name}' picks from the outbound '{name}'";
        }

        if (await NameFollow.AsksThroughAsync(_db, name, ct).ConfigureAwait(false))
        {
            return $"the resolver asks through the outbound '{name}'";
        }

        return string.Equals(live, name, StringComparison.Ordinal)
            ? $"the resolver asks through the outbound '{name}' until it is restarted"
            : null;
    }

    private async Task<bool> TakenAsync(string name, long id, CancellationToken ct)
    {
        var body = name.Trim();
        if (await _db.Outbounds.AnyAsync(one => one.Name == body && one.Id != id, ct).ConfigureAwait(false))
        {
            return true;
        }

        return await _db.Balancers.AnyAsync(one => one.Name == body, ct).ConfigureAwait(false);
    }

    private async Task<uint> FreeMarkAsync(CancellationToken ct)
    {
        var taken = await _db.Outbounds.Select(outbound => outbound.Mark).ToListAsync(ct).ConfigureAwait(false);
        for (var mark = OutboundRules.FirstMark; mark <= OutboundRules.LastMark; mark++)
        {
            if (!taken.Contains(mark))
            {
                return mark;
            }
        }

        return 0;
    }

    private async Task<int> NextPositionAsync(CancellationToken ct)
    {
        var last = await _db.Outbounds.MaxAsync(outbound => (int?)outbound.Position, ct).ConfigureAwait(false);
        return (last ?? 0) + 1;
    }

    private static OutboundResult Missing(long id) =>
        OutboundResult.No(OutboundOutcome.Unknown, "unknown-outbound", $"there is no outbound under the number {id}");

    // Probe results use this timestamp as the saved revision, including when the clock stalls or moves back.
    private DateTimeOffset RevisionAfter(DateTimeOffset previous)
    {
        var now = _time.GetUtcNow();
        return now > previous ? now : previous.AddTicks(1);
    }

    private static OutboundResult Taken(string name) =>
        OutboundResult.No(
            OutboundOutcome.NameTaken,
            "name-taken",
            $"the panel already carries an outbound called '{name}'");

    private OutboundEntity Create(OutboundConfig config)
    {
        var entity = _factories
                         .Select(factory => factory.Create(config))
                         .FirstOrDefault(one => one is not null)
                     ?? throw new InvalidOperationException($"no persistence is registered for '{config.Kind}'");
        entity.Update(config);
        return entity;
    }
}
