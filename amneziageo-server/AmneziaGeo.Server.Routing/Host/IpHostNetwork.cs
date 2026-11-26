using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AmneziaGeo.Server.Awg.Netlink;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Routing.Host;

/// <summary>
/// Puts interfaces, addresses, routes and firewall rules on the host through the ip and nft tools, and reads the
/// counters of the firewall and the rules of the routing policy over netlink.
/// </summary>
public sealed class IpHostNetwork : IHostNetwork
{
    private const string NetworkClass = "/sys/class/net";
    private const string Ip = "ip";
    private const string Nft = "nft";
    private const string LinkKind = "amneziawg";
    private const string Flags = "flags";
    private const int UpFlag = 0x1;

    private static readonly char[] Blanks = [' ', '\t', '\n', '\r'];

    private static readonly string[] Forwarding =
    [
        "/proc/sys/net/ipv4/ip_forward",
        "/proc/sys/net/ipv6/conf/all/forwarding",
    ];

    private readonly IHostCommands _commands;

    private readonly Func<string, string, long?> _counter;

    private volatile bool _counterRefused;

    private readonly Func<IReadOnlyList<PolicyRule>> _rules;

    private volatile bool _rulesRefused;

    /// <summary>
    /// ctor
    /// </summary>
    public IpHostNetwork(IHostCommands commands)
        : this(commands, NetfilterCounter.Read, PolicyRules.Read)
    {
    }

    /// <summary>
    /// ctor
    /// </summary>
    public IpHostNetwork(IHostCommands commands, Func<string, string, long?> counter)
        : this(commands, counter, PolicyRules.Read)
    {
    }

    /// <summary>
    /// ctor
    /// </summary>
    public IpHostNetwork(IHostCommands commands, Func<string, string, long?> counter, Func<IReadOnlyList<PolicyRule>> rules)
    {
        _commands = commands;
        _counter = counter;
        _rules = rules;
    }

    /// <summary>
    /// Tells whether the host carries an interface.
    /// </summary>
    public bool HasLink(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Directory.Exists(Path.Combine(NetworkClass, name));
    }

    /// <summary>
    /// Tells whether an interface is on the host and up.
    /// </summary>
    public bool IsUp(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var text = File.ReadAllText(Path.Combine(NetworkClass, name, Flags)).Trim();

            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags)
                && (flags & UpFlag) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds an AmneziaWG interface.
    /// </summary>
    public Task AddLinkAsync(string name, CancellationToken ct) =>
        RunAsync([Ip, "link", "add", name, "type", LinkKind], ct);

    /// <summary>
    /// Takes an interface off the host.
    /// </summary>
    public Task RemoveLinkAsync(string name, CancellationToken ct) =>
        RunAsync([Ip, "link", "del", name], ct);

    /// <summary>
    /// Replaces the address ranges of an interface, leaving the ones it already carries in place.
    /// </summary>
    public async Task AddressAsync(string name, IReadOnlyList<string> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var wanted = addresses.Select(Canonical).ToHashSet(StringComparer.Ordinal);
        var held = await AddressesAsync(name, ct).ConfigureAwait(false) ?? await FlushAsync(name, ct).ConfigureAwait(false);
        var dropped = held.Where(address => !wanted.Contains(address)).ToList();
        foreach (var address in dropped)
        {
            await RunAsync([Ip, "address", "del", address, "dev", name], ct).ConfigureAwait(false);
        }

        var left = dropped.Count == 0 ? held : await AddressesAsync(name, ct).ConfigureAwait(false) ?? held;
        foreach (var address in addresses.Where(address => !left.Contains(Canonical(address))))
        {
            await RunAsync([Ip, "address", "add", address, "dev", name], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets the packet size of an interface and brings it up.
    /// </summary>
    public async Task UpAsync(string name, int mtu, CancellationToken ct)
    {
        if (mtu > 0)
        {
            await RunAsync([Ip, "link", "set", "dev", name, "mtu", mtu.ToString()], ct).ConfigureAwait(false);
        }

        await RunAsync([Ip, "link", "set", "dev", name, "up"], ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RouteAsync(string name, int table, CancellationToken ct)
    {
        OutboundTable(table);
        var changed = false;
        foreach (var six in new[] { false, true })
        {
            var held = (await TableRoutesAsync(table, six, ct).ConfigureAwait(false)).Where(IsDefault).ToArray();
            if (held.Length == 1 && DefaultOn(held[0], name, ""))
            {
                continue;
            }

            if (held.Length > 0)
            {
                await RunAsync([Ip, .. Family(six), "route", "flush", "table", table.ToString(), "exact", "default"], ct)
                    .ConfigureAwait(false);
            }

            await RunAsync([Ip, .. Family(six), "route", "replace", "default", "dev", name, "table", table.ToString()], ct)
                .ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Clears a routing table.
    /// </summary>
    public async Task ClearRouteAsync(int table, CancellationToken ct)
    {
        OutboundTable(table);
        foreach (var six in new[] { false, true })
        {
            if ((await TableRoutesAsync(table, six, ct).ConfigureAwait(false)).Length > 0)
            {
                await RunAsync([Ip, .. Family(six), "route", "flush", "table", table.ToString()], ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public Task<bool> DefaultRouteAsync(string name, int table, AddressFamily family, string gateway, CancellationToken ct) =>
        SetDefaultRouteAsync(name, table, family, gateway, ct);

    /// <inheritdoc/>
    public Task<bool> UnreachableRouteAsync(int table, AddressFamily family, CancellationToken ct) =>
        SetDefaultRouteAsync(null, table, family, "", ct);

    private async Task<bool> SetDefaultRouteAsync(string? name, int table, AddressFamily family, string gateway,
        CancellationToken ct)
    {
        OutboundTable(table);
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new HostNetworkException("a route requires IPv4 or IPv6");
        }

        var six = family == AddressFamily.InterNetworkV6;
        var held = await TableRoutesAsync(table, six, ct).ConfigureAwait(false);
        if (held.Length == 1 && (name is not null
                ? DefaultOn(held[0], name, gateway)
                : IsDefault(held[0]) && Field(held[0], "type") is "7" or "unreachable"))
        {
            return false;
        }

        if (held.Length > 0)
        {
            await RunAsync([Ip, .. Family(six), "route", "flush", "table", table.ToString()], ct)
                .ConfigureAwait(false);
        }

        string[] next = gateway.Length == 0 ? [] : ["via", gateway, "onlink"];
        string[] route = name is not null ? ["default", .. next, "dev", name] : ["unreachable", "default"];
        await RunAsync([Ip, .. Family(six), "route", "add", .. route, "table", table.ToString()], ct)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<JsonElement[]> TableRoutesAsync(int table, bool six, CancellationToken ct)
    {
        var result = await _commands.RunAsync(Ip, [six ? "-6" : "-4", "-N", "-j", "route", "show", "table", "all"],
            null, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            throw new HostNetworkException($"the routing tables did not read: {result.Complaint}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return [.. document.RootElement.EnumerateArray()
                .Where(route => route.TryGetProperty("table", out var number) && RuleNumber(number) == (uint)table)
                .Select(route => route.Clone())];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new HostNetworkException("the host returned an unreadable routing table", ex);
        }
    }

    private static string[] Family(bool six) => six ? ["-6"] : [];

    private static void OutboundTable(int table)
    {
        if (table < OutboundRules.FirstTable || table > OutboundRules.TableOf(OutboundRules.LastMark, true))
        {
            throw new HostNetworkException("only allocated outbound routing tables can be changed or cleared");
        }
    }

    private static string Field(JsonElement route, string name) =>
        route.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static bool IsDefault(JsonElement route) => Field(route, "dst") == "default";

    private static bool DefaultOn(JsonElement route, string name, string gateway) =>
        IsDefault(route) && Field(route, "from").Length == 0 && Field(route, "dev") == name
        && SameAddress(Field(route, "gateway"), gateway) && Field(route, "type") is "" or "1";

    private static bool SameAddress(string left, string right) => left == right
        || (IPAddress.TryParse(left, out var one) && IPAddress.TryParse(right, out var other) && one.Equals(other));

    /// <summary>
    /// Adds or removes the rule that sends a marked packet into a routing table, telling whether it had to.
    /// </summary>
    public async Task<bool> RuleAsync(uint mark, int table, int priority, bool present, CancellationToken ct)
    {
        var rules = Rules();
        var changed = false;
        foreach (var family in new[] { PolicyRules.Inet, PolicyRules.Inet6 })
        {
            var read = rules ?? await ReadRulesAsync(family, priority, ct).ConfigureAwait(false);
            var held = read.Where(rule => rule.Family == family && rule.Priority == (uint)priority && rule.Mark == mark)
                .ToArray();
            if (present && held.Length == 1 && held[0].Table == (uint)table
                && held[0].Action == PolicyRules.ToTable && (held[0].Mask ?? uint.MaxValue) == uint.MaxValue)
            {
                continue;
            }

            string[] prefix = family == PolicyRules.Inet6 ? [Ip, "-6"] : [Ip];
            foreach (var rule in held)
            {
                var mask = (rule.Mask ?? uint.MaxValue) == uint.MaxValue ? "" : $"/0x{rule.Mask:x}";
                await RunAsync([.. prefix, "rule", "del", "pref", priority.ToString(), "fwmark", $"{mark}{mask}"], ct)
                    .ConfigureAwait(false);
                changed = true;
            }

            if (present)
            {
                await RunAsync([.. prefix, "rule", "add", "pref", priority.ToString(), "fwmark", mark.ToString(),
                    "lookup", table.ToString()], ct).ConfigureAwait(false);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Adds the rule that refuses a marked packet no outbound takes, telling whether it had to.
    /// </summary>
    public async Task<bool> SealAsync(CancellationToken ct)
    {
        var order = OutboundRules.SealPriority.ToString(CultureInfo.InvariantCulture);
        var words = new[] { "rule", "add", "pref", order, "fwmark", OutboundRules.SealMarks, "unreachable" };
        var rules = Rules();
        var laid = false;
        if (!await HasSealAsync(rules, PolicyRules.Inet, ["rule", "show", "pref", order], ct).ConfigureAwait(false))
        {
            laid |= await TakenAsync([Ip, .. words], ct).ConfigureAwait(false);
        }

        if (!await HasSealAsync(rules, PolicyRules.Inet6, ["-6", "rule", "show", "pref", order], ct).ConfigureAwait(false))
        {
            laid |= await TakenAsync([Ip, "-6", .. words], ct).ConfigureAwait(false);
        }

        return laid;
    }

    /// <summary>
    /// Returns the interface the host reaches the internet through.
    /// </summary>
    public async Task<string> UplinkAsync(CancellationToken ct)
    {
        var result = await _commands.RunAsync(Ip, ["route", "show", "default"], null, ct).ConfigureAwait(false);
        var words = result.Output.Split(Blanks, StringSplitOptions.RemoveEmptyEntries);
        for (var at = 0; at < words.Length - 1; at++)
        {
            if (words[at] == "dev")
            {
                return words[at + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Lets the host pass packets between interfaces, in both families.
    /// </summary>
    public async Task ForwardingAsync(CancellationToken ct)
    {
        foreach (var knob in Forwarding)
        {
            if (File.Exists(knob) && (await File.ReadAllTextAsync(knob, ct).ConfigureAwait(false)).Trim() == "1")
            {
                continue;
            }

            try
            {
                await File.WriteAllTextAsync(knob, "1", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new HostNetworkException(
                    $"forwarding is off in {knob} and the host keeps the panel from turning it on",
                    ex);
            }
        }
    }

    /// <summary>
    /// Puts a firewall ruleset on the host, replacing the one it holds.
    /// </summary>
    public Task FirewallAsync(string ruleset, CancellationToken ct) => NftAsync(ruleset, false, ct);

    /// <summary>
    /// Reads a firewall ruleset without putting it on the host.
    /// </summary>
    public Task CheckFirewallAsync(string ruleset, CancellationToken ct) => NftAsync(ruleset, true, ct);

    /// <summary>
    /// Returns an inet table of the firewall in the JSON of nft, empty when the host holds no such table.
    /// </summary>
    public async Task<string> ReadFirewallAsync(string table, CancellationToken ct)
    {
        var result = await _commands.RunAsync(Nft, ["-j", "list", "table", "inet", table], null, ct)
            .ConfigureAwait(false);

        return result.IsOk ? result.Output : string.Empty;
    }

    /// <summary>
    /// Has a routing table deliver what it is asked about to the host itself, in both families.
    /// </summary>
    public async Task LocalRouteAsync(int table, CancellationToken ct)
    {
        var number = table.ToString(CultureInfo.InvariantCulture);
        await RunAsync([Ip, "route", "replace", "local", "0.0.0.0/0", "dev", "lo", "table", number], ct).ConfigureAwait(false);
        await Quietly([Ip, "-6", "route", "replace", "local", "::/0", "dev", "lo", "table", number], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns how many packets a counter of an inet table of the firewall has seen, or null when the host holds no
    /// such counter.
    /// </summary>
    public async Task<long?> CounterAsync(string table, string name, CancellationToken ct)
    {
        if (!_counterRefused)
        {
            try
            {
                return _counter(table, name);
            }
            catch (Exception ex) when (ex is NetlinkException or DllNotFoundException or EntryPointNotFoundException)
            {
                _counterRefused = true;
            }
        }

        var result = await _commands.RunAsync(Nft, ["-j", "list", "counter", "inet", table, name], null, ct)
            .ConfigureAwait(false);
        if (!result.IsOk)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            foreach (var item in document.RootElement.GetProperty("nftables").EnumerateArray())
            {
                if (item.TryGetProperty("counter", out var counter) && counter.TryGetProperty("packets", out var packets))
                {
                    return packets.GetInt64();
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task NftAsync(string ruleset, bool dry, CancellationToken ct)
    {
        var arguments = dry ? (string[])["-c", "-f", "-"] : ["-f", "-"];
        var result = await _commands.RunAsync(Nft, arguments, ruleset, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            throw new HostNetworkException($"the firewall refused the ruleset: {result.Complaint}");
        }
    }

    private async Task<IReadOnlyList<PolicyRule>> ReadRulesAsync(byte family, int priority, CancellationToken ct)
    {
        string[] prefix = family == PolicyRules.Inet6 ? ["-6"] : [];
        var result = await _commands.RunAsync(Ip,
            [.. prefix, "-N", "-j", "rule", "show", "pref", priority.ToString()], null, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            throw new HostNetworkException($"the routing policy did not read: {result.Complaint}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return [.. document.RootElement.EnumerateArray().Select(rule => new PolicyRule(
                family,
                rule.TryGetProperty("priority", out var place) ? RuleNumber(place) : 0,
                rule.TryGetProperty("fwmark", out var mark) ? RuleNumber(mark) : null,
                rule.TryGetProperty("fwmask", out var mask) ? RuleNumber(mask) : uint.MaxValue,
                rule.TryGetProperty("action", out var action) ? checked((byte)RuleNumber(action)) : PolicyRules.ToTable,
                rule.TryGetProperty("table", out var table) ? RuleNumber(table) : 0))];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new HostNetworkException("the host returned an unreadable routing policy", ex);
        }
    }

    private static uint RuleNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetUInt32();
        }

        var text = value.GetString() ?? string.Empty;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
    }

    private async Task<bool> HasSealAsync(
        IReadOnlyList<PolicyRule>? rules,
        byte family,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        if (rules is not null)
        {
            return rules.Any(rule =>
                rule.Family == family
                && rule.Priority == (uint)OutboundRules.SealPriority
                && rule.Action == PolicyRules.Unreachable);
        }

        var result = await _commands.RunAsync(Ip, arguments, null, ct).ConfigureAwait(false);

        return result.IsOk && result.Output.Contains("unreachable", StringComparison.OrdinalIgnoreCase);
    }

    // Reads the rules of the host from the kernel, null once the kernel refused them and the tool is asked instead.
    private IReadOnlyList<PolicyRule>? Rules()
    {
        if (_rulesRefused)
        {
            return null;
        }

        try
        {
            return _rules();
        }
        catch (Exception ex) when (ex is NetlinkException or DllNotFoundException or EntryPointNotFoundException)
        {
            _rulesRefused = true;

            return null;
        }
    }

    private async Task<HashSet<string>?> AddressesAsync(string name, CancellationToken ct)
    {
        var result = await _commands.RunAsync(Ip, ["-j", "address", "show", "dev", name], null, ct)
            .ConfigureAwait(false);
        if (!result.IsOk)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in document.RootElement.EnumerateArray())
            {
                if (!link.TryGetProperty("addr_info", out var infos))
                {
                    continue;
                }

                foreach (var info in infos.EnumerateArray())
                {
                    if (info.TryGetProperty("scope", out var scope) && scope.GetString() == "link")
                    {
                        continue;
                    }

                    held.Add(Canonical($"{info.GetProperty("local").GetString()}/{info.GetProperty("prefixlen").GetInt32()}"));
                }
            }

            return held;
        }
        catch (Exception ex)
            when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private async Task<HashSet<string>> FlushAsync(string name, CancellationToken ct)
    {
        await RunAsync([Ip, "address", "flush", "dev", name], ct).ConfigureAwait(false);

        return [];
    }

    private static string Canonical(string address)
    {
        var slash = address.IndexOf('/', StringComparison.Ordinal);
        var text = slash < 0 ? address.Trim() : address[..slash].Trim();
        if (!IPAddress.TryParse(text, out var parsed))
        {
            return address.Trim();
        }

        var full = parsed.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;

        return $"{parsed}/{(slash < 0 ? full : Length(address[(slash + 1)..], full))}";
    }

    private static int Length(string text, int full) => int.TryParse(text.Trim(), out var length) ? length : full;

    private async Task RunAsync(IReadOnlyList<string> words, CancellationToken ct)
    {
        var result = await _commands.RunAsync(words[0], [.. words.Skip(1)], null, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            throw new HostNetworkException(string.Join(" ", words) + " was refused: " + result.Complaint);
        }
    }

    private async Task Quietly(IReadOnlyList<string> words, CancellationToken ct)
    {
        await _commands.RunAsync(words[0], [.. words.Skip(1)], null, ct).ConfigureAwait(false);
    }

    // Runs a command and tells whether the host took it.
    private async Task<bool> TakenAsync(IReadOnlyList<string> words, CancellationToken ct)
    {
        var result = await _commands.RunAsync(words[0], [.. words.Skip(1)], null, ct).ConfigureAwait(false);

        return result.IsOk;
    }
}
