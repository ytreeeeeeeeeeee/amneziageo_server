namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// Leaves through the host's main routing table without an interface of its own.
/// </summary>
public sealed class LocalOutboundDriver : IOutboundDriver
{
    /// <inheritdoc/>
    public OutboundConfig Draft(string name, string kind) => LocalOutboundDefaults.Direct() with { Name = name };

    /// <inheritdoc/>
    public bool Supports(string kind) => kind == OutboundKind.Local;

    /// <inheritdoc/>
    public Task ApplyAsync(OutboundConfig outbound, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct) => Task.FromResult(false);

    /// <inheritdoc/>
    public OutboundState State(OutboundConfig outbound) =>
        new LocalOutboundState(outbound.Name);
}
