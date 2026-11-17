namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// Prepares one kind of outbound and reads its state; policy rules, firewall and probes are shared by the host.
/// </summary>
public interface IOutboundDriver
{
    /// <summary>
    /// Tells whether this driver carries the outbound kind.
    /// </summary>
    bool Supports(string kind);

    /// <summary>
    /// Returns initial settings for one of this driver's kinds.
    /// </summary>
    OutboundConfig Draft(string name, string kind);

    /// <summary>
    /// Prepares the enabled outbound and its route before the host installs its policy rule.
    /// </summary>
    Task ApplyAsync(OutboundConfig outbound, CancellationToken ct);

    /// <summary>
    /// Releases the resources the driver owns after the host removes the policy rule.
    /// </summary>
    Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct);

    /// <summary>
    /// Restores resources the host lost, telling whether it had to; called only for enabled outbounds.
    /// </summary>
    Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct);

    /// <summary>
    /// Reads availability and counters without the shared probe verdict.
    /// </summary>
    OutboundState State(OutboundConfig outbound);
}
