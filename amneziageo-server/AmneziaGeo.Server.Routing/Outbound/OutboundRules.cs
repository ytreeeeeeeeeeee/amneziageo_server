using System.Net;
using System.Text.RegularExpressions;
using AmneziaGeo.Server.Routing.Dns;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Routing.Outbound;

/// <summary>
/// Validation and policy routing shared by every outbound implementation.
/// </summary>
public static partial class OutboundRules
{
    /// <summary>
    /// The longest name an outbound takes.
    /// </summary>
    public const int MaxNameLength = 15;

    /// <summary>
    /// The lowest mark an outbound takes.
    /// </summary>
    public const uint FirstMark = 0xA601;

    /// <summary>
    /// The highest mark an outbound takes.
    /// </summary>
    public const uint LastMark = 0xA6FF;

    /// <summary>
    /// The routing table the lowest mark looks the way out up in.
    /// </summary>
    public const int FirstTable = 42601;

    /// <summary>
    /// The routing table the host itself picks the way out of.
    /// </summary>
    public const int MainTable = 254;

    /// <summary>
    /// The place in the rule list the lowest mark takes.
    /// </summary>
    public const int FirstPriority = 10000;

    /// <summary>
    /// The place in the rule list that refuses a marked packet no outbound takes.
    /// </summary>
    public const int SealPriority = FirstPriority + (int)(LastMark - FirstMark) + 1;

    /// <summary>
    /// The marks the outbounds take, as the rule list matches them.
    /// </summary>
    public const string SealMarks = "0xa600/0xffffff00";

    /// <summary>
    /// Returns why the settings of an outbound are unusable, or null when they hold.
    /// </summary>
    public static OutboundFault? Check(OutboundConfig outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        return CheckName(outbound.Name)
               ?? (outbound.UsesInterface ? CheckInterfaceName(outbound.InterfaceName) : null)
               ?? CheckKind(outbound.Kind)
            ?? CheckMark(outbound.Mark)
            ?? CheckTable(outbound)
               ?? CheckProbe(outbound)
               ?? CheckServers(outbound.Dns)
               ?? outbound.CheckSettings();
    }

    /// <summary>
    /// Returns why the name of an outbound is unusable, or null when it holds.
    /// </summary>
    public static OutboundFault? CheckName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NameShape().IsMatch(name)
            ? null
            : new("bad-interface-name", "the name must be a lowercase interface name of at most 15 characters");

    /// <summary>Checks a Linux device name without restricting it to the outbound naming convention.</summary>
    public static OutboundFault? CheckInterfaceName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name is not "." and not ".."
        && DeviceShape().IsMatch(name)
            ? null
            : new("bad-interface-name", "the interface name must contain at most 15 letters, digits, dots, dashes or underscores");

    /// <summary>
    /// Returns why the kind of an outbound is unusable, or null when it holds.
    /// </summary>
    public static OutboundFault? CheckKind(string? kind) =>
        !string.IsNullOrEmpty(kind) && kind.Length <= 16 && NameShape().IsMatch(kind)
            ? null
            : new("bad-kind", "the outbound kind is not a lowercase identifier");

    /// <summary>
    /// Returns the routing table a mark looks the way out up in.
    /// </summary>
    public static int TableOf(uint mark, bool usesInterface) =>
        usesInterface ? FirstTable + (int)(mark - FirstMark) : MainTable;

    /// <summary>
    /// Returns the place a mark takes in the rule list.
    /// </summary>
    public static int PriorityOf(uint mark) => FirstPriority + (int)(mark - FirstMark);

    /// <summary>
    /// Returns why the probe of an outbound is unusable, or null when it holds.
    /// </summary>
    public static OutboundFault? CheckProbe(OutboundConfig outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        if (outbound.Probe.Length == 0)
        {
            return null;
        }

        if (!DnsRules.Upstream(outbound.Probe, out _))
        {
            return new("bad-probe", $"'{outbound.Probe}' is not an address of a name server");
        }

        return outbound.ProbeEvery is >= ProbeDefaults.MinEvery and <= ProbeDefaults.MaxEvery
            ? null
            : new("bad-probe", "the probe interval is outside the supported range");
    }

    /// <summary>
    /// Returns why a name server is unusable, or null when every address holds.
    /// </summary>
    public static OutboundFault? CheckServers(IReadOnlyList<string> servers)
    {
        foreach (var server in servers)
        {
            if (!IPAddress.TryParse(server, out _))
            {
                return new("bad-dns", $"'{server}' is not a name server address");
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex NameShape();

    [GeneratedRegex("^[a-zA-Z0-9_.-]+$")]
    private static partial Regex DeviceShape();

    private static OutboundFault? CheckMark(uint mark) =>
        mark is >= FirstMark and <= LastMark
            ? null
            : new("bad-mark", "the mark is outside the outbound range");

    private static OutboundFault? CheckTable(OutboundConfig outbound) =>
        outbound.Table == TableOf(outbound.Mark, outbound.UsesInterface)
            ? null
            : new("bad-table", "the routing table does not go with the mark");
}
