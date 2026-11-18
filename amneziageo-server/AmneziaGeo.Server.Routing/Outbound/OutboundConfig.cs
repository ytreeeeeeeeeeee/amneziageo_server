namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// One way out of the host, as the panel holds it.
/// </summary>
public abstract record OutboundConfig
{
    /// <summary>
    /// The number the panel keeps the outbound under.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// The name other routing settings use to refer to this outbound.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The way traffic leaves the host.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// The network interface this way out uses, empty when it uses the host's uplink.
    /// </summary>
    public virtual string InterfaceName => string.Empty;

    /// <summary>
    /// Whether this way out uses a separate interface and routing table.
    /// </summary>
    public bool UsesInterface => InterfaceName.Length > 0;

    /// <summary>
    /// Whether the driver creates and removes this interface, rather than using an externally managed one.
    /// </summary>
    public virtual bool OwnsInterface => false;

    /// <summary>
    /// Checks the settings specific to this implementation.
    /// </summary>
    public abstract OutboundFault? CheckSettings();

    /// <summary>
    /// Where the outbound stands among the others.
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Whether the outbound is put on the host.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// The name servers reachable through the outbound.
    /// </summary>
    public IReadOnlyList<string> Dns { get; init; } = [];

    /// <summary>
    /// Whether the clients are kept out of the private networks behind the tunnel.
    /// </summary>
    public bool ClosePrivate { get; init; } = true;

    /// <summary>
    /// The name server the probe asks, empty when the outbound is not probed.
    /// </summary>
    public string Probe { get; init; } = string.Empty;

    /// <summary>
    /// How often the probe goes out, in seconds.
    /// </summary>
    public int ProbeEvery { get; init; }

    /// <summary>
    /// The mark that sends a packet out through this outbound.
    /// </summary>
    public uint Mark { get; init; }

    /// <summary>
    /// The routing table the mark looks the way out up in.
    /// </summary>
    public int Table { get; init; }

    /// <summary>
    /// When the outbound was added.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// When the outbound was last changed.
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; init; }
}
