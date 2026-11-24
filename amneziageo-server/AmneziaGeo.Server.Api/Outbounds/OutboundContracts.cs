using System.Text.Json.Serialization;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// What the probes of an outbound came to, as the interface reads it.
/// </summary>
public sealed record OutboundProbeBody(
    bool IsReached,
    int Falls,
    DateTimeOffset At);

/// <summary>
/// What the host holds for an outbound, as the interface reads it.
/// </summary>
public record OutboundStateBody(
    bool HasLink,
    ulong? RxBytes,
    ulong? TxBytes,
    bool IsAlive,
    string Fault,
    bool Carries,
    OutboundProbeBody? Probe);

/// <summary>
/// Only fields every outbound implementation carries.
/// </summary>
public abstract record OutboundResponse
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public int Position { get; init; }

    public bool IsEnabled { get; init; }

    public string[] Dns { get; init; } = [];

    public string Probe { get; init; } = string.Empty;

    public int ProbeEvery { get; init; }

    public bool ClosePrivate { get; init; }

    public long Mark { get; init; }

    public int Table { get; init; }

    public OutboundStateBody? State { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>
/// Common request fields; kind selects the implementation during JSON deserialization.
/// </summary>
public abstract record OutboundRequest
{
    [JsonIgnore] public abstract string Kind { get; }

    public string? Name { get; init; }

    public bool IsEnabled { get; init; }

    public string[]? Dns { get; init; }

    public string? Probe { get; init; }

    public int ProbeEvery { get; init; }

    public bool? ClosePrivate { get; init; }

    public IReadOnlyList<long>? Balancers { get; init; }

    public IReadOnlyList<long>? Rules { get; init; }

    /// <summary>
    /// Returns the settings specific to this kind of outbound.
    /// </summary>
    protected abstract OutboundConfig Settings();

    /// <summary>
    /// Reads the outbound the interface sends, including its common settings.
    /// </summary>
    public OutboundConfig ToConfig() => Settings() with
    {
        Kind = Kind,
        IsEnabled = IsEnabled,
        Dns = Dns ?? [],
        Probe = (Probe ?? string.Empty).Trim(),
        ProbeEvery = ProbeEvery,
        ClosePrivate = ClosePrivate ?? true,
    };
}

/// <summary>
/// Where an outbound is moved to.
/// </summary>
public sealed record OutboundMoveRequest(bool Up);

/// <summary>
/// Whether an outbound is turned on.
/// </summary>
public sealed record OutboundSwitchRequest(bool? On);

/// <summary>Reports a host failure and the affected outbound ID when the database change was committed.</summary>
public sealed record OutboundApplyFailure(string Error, string Message, long? SavedId);

/// <summary>
/// Creates implementation-specific response fields without changing the common contract.
/// </summary>
public interface IOutboundResponseFactory
{
    /// <summary>
    /// Describes the settings of an implementation, or returns null for another kind.
    /// </summary>
    OutboundResponse? Response(OutboundConfig config, bool secrets);

    /// <summary>
    /// Describes the state of an implementation, or returns null for another kind.
    /// </summary>
    OutboundStateBody? State(OutboundState state);
}

/// <summary>
/// Maps common fields and delegates protocol-specific fields to registered implementations.
/// </summary>
public sealed class OutboundAnswers(IEnumerable<IOutboundResponseFactory> factories)
{
    private readonly IOutboundResponseFactory[] _factories = [.. factories];

    /// <summary>
    /// Describes an outbound, keeping keys only when the caller may read them.
    /// </summary>
    public OutboundResponse Outbound(OutboundConfig outbound, OutboundState? state, bool secrets)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var response = _factories.Select(factory => factory.Response(outbound, secrets))
                           .FirstOrDefault(one => one is not null)
                       ?? throw new InvalidOperationException(
                           $"no response contract is registered for '{outbound.Kind}'");

        return response with
        {
            Id = outbound.Id,
            Name = outbound.Name,
            Kind = outbound.Kind,
            Position = outbound.Position,
            IsEnabled = outbound.IsEnabled,
            Dns = [.. outbound.Dns],
            Probe = outbound.Probe,
            ProbeEvery = outbound.ProbeEvery,
            ClosePrivate = outbound.ClosePrivate,
            Mark = outbound.Mark,
            Table = outbound.Table,
            State = state is null ? null : State(state),
            CreatedUtc = outbound.CreatedUtc,
            UpdatedUtc = outbound.UpdatedUtc,
        };
    }

    /// <summary>
    /// Describes what the host holds for one outbound.
    /// </summary>
    public OutboundStateBody State(OutboundState state) =>
        _factories.Select(factory => factory.State(state)).FirstOrDefault(one => one is not null) ?? CommonState(state);

    /// <summary>
    /// Describes the state every kind of outbound carries.
    /// </summary>
    public static OutboundStateBody CommonState(OutboundState state) => new(
        state.HasLink, state.RxBytes, state.TxBytes, state.IsAlive, state.Fault, state.Carries,
        state.Probe is { } probe ? new(probe.IsReached, probe.Falls, probe.At) : null);

    /// <summary>
    /// Reads the outbound an interface sends.
    /// </summary>
    public static OutboundConfig Draft(OutboundRequest request) => request.ToConfig();
}
