using System.Net.Sockets;
using AmneziaGeo.Server.Awg.Netlink;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Tests;

public class IpHostNetworkTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(254)]
    [InlineData(255)]
    public async Task InterfaceRoutingCannotTouchSystemTables(int table)
    {
        var tools = new Tools();
        var network = new IpHostNetwork(tools);
        await Assert.ThrowsAsync<HostNetworkException>(() => network.ClearRouteAsync(table, CancellationToken.None));
        await Assert.ThrowsAsync<HostNetworkException>(() => network.DefaultRouteAsync("tt-ams", table,
            AddressFamily.InterNetwork, "", CancellationToken.None));
        await Assert.ThrowsAsync<HostNetworkException>(() => network.UnreachableRouteAsync(table,
            AddressFamily.InterNetworkV6, CancellationToken.None));
        Assert.Empty(tools.Calls);
    }

    [Theory]
    [InlineData(AddressFamily.InterNetwork, "", "ip route add default dev tt-ams table 42601")]
    [InlineData(AddressFamily.InterNetworkV6, "fe80::1", "ip -6 route add default via fe80::1 onlink dev tt-ams table 42601")]
    [InlineData(AddressFamily.InterNetwork, "192.0.2.1", "ip route add default via 192.0.2.1 onlink dev tt-ams table 42601")]
    public async Task DefaultRoutesConfigureOnlyTheRequestedFamily(AddressFamily family, string gateway, string expected)
    {
        var tools = Routes("[]", "[]");
        var network = new IpHostNetwork(tools);
        Assert.True(await network.DefaultRouteAsync("tt-ams", 42601, family, gateway, CancellationToken.None));
        Assert.Equal(2, tools.Calls.Count);
        Assert.Equal(expected, tools.Calls[1]);
    }

    [Theory]
    [InlineData(AddressFamily.InterNetwork, "ip route add unreachable default table 42601")]
    [InlineData(AddressFamily.InterNetworkV6, "ip -6 route add unreachable default table 42601")]
    public async Task UnreachableRoutesConfigureOnlyTheRequestedFamily(AddressFamily family, string expected)
    {
        var tools = Routes("[]", "[]");
        Assert.True(await new IpHostNetwork(tools).UnreachableRouteAsync(42601, family, CancellationToken.None));
        Assert.Equal(2, tools.Calls.Count);
        Assert.Equal(expected, tools.Calls[1]);
    }

    [Fact]
    public async Task NonIpFamiliesAreRefusedBeforeCallingTheHost()
    {
        var tools = new Tools();
        var network = new IpHostNetwork(tools);
        await Assert.ThrowsAsync<HostNetworkException>(() => network.DefaultRouteAsync("tt-ams", 42601,
            AddressFamily.Unix, "", CancellationToken.None));
        await Assert.ThrowsAsync<HostNetworkException>(() => network.UnreachableRouteAsync(42601,
            AddressFamily.Unspecified, CancellationToken.None));
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task MatchingDefaultAndUnreachableRoutesDoNotGenerateEndlessKernelChanges()
    {
        var tools = Routes("""[{"table":"42601","dst":"default","dev":"tt-ams","gateway":"192.0.2.1"}]""",
            """[{"table":42601,"dst":"default","type":"7"}]""");
        var network = new IpHostNetwork(tools);
        Assert.False(await network.DefaultRouteAsync("tt-ams", 42601, AddressFamily.InterNetwork,
            "192.0.2.1", CancellationToken.None));
        Assert.False(await network.UnreachableRouteAsync(42601, AddressFamily.InterNetworkV6, CancellationToken.None));
        Assert.Equal(["ip -4 -N -j route show table all", "ip -6 -N -j route show table all"], tools.Calls);
    }

    [Fact]
    public async Task ARouteTypeChangeRemovesTheOldRouteBeforeInstallingItsReplacement()
    {
        var tools = Routes("""[{"table":"42601","dst":"default","dev":"tt-ams"}]""", "[]");
        await new IpHostNetwork(tools).UnreachableRouteAsync(42601, AddressFamily.InterNetwork, CancellationToken.None);
        Assert.True(tools.Calls.IndexOf("ip route flush table 42601")
            < tools.Calls.IndexOf("ip route add unreachable default table 42601"));
    }

    [Fact]
    public async Task FailedRouteCleanupIsReportedSoOwnershipCanBeRetained()
    {
        var tools = Routes("""[{"table":"42601","dst":"default","dev":"tt-ams"}]""", "[]");
        tools.Answers["ip route flush"] = new(1, "", "permission denied");
        await Assert.ThrowsAsync<HostNetworkException>(() => new IpHostNetwork(tools)
            .ClearRouteAsync(42601, CancellationToken.None));
    }

    private static Tools Routes(string four, string six)
    {
        var tools = new Tools();
        tools.Answers["ip -4 -N -j route show table all"] = new(0, four, "");
        tools.Answers["ip -6 -N -j route show table all"] = new(0, six, "");
        return tools;
    }

    private const string Carried =
        """[{"ifname":"awgbor","addr_info":[{"family":"inet","local":"10.9.0.2","prefixlen":24,"scope":"global"},{"family":"inet","local":"10.9.0.3","prefixlen":24,"scope":"global"},{"family":"inet6","local":"fd00::2","prefixlen":112,"scope":"global"},{"family":"inet6","local":"fe80::1","prefixlen":64,"scope":"link"}]}]""";

    [Fact]
    public async Task ARuleTheHostAlreadyHoldsIsNotAddedAgain()
    {
        var tools = new Tools();
        var rule = Marked(10000, 0xA601, 254);
        var network = Holding(tools, rule, rule with { Family = PolicyRules.Inet6 });

        await network.RuleAsync(0xA601, 254, 10000, true, CancellationToken.None);

        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task ARuleTheHostDoesNotHoldIsAddedUnderItsPlace()
    {
        var tools = new Tools();
        var network = Holding(tools);

        await network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None);

        Assert.True(tools.Called("ip rule add pref 10001 fwmark 42498 lookup 42602"));
        Assert.False(tools.Called("ip rule show"));
    }

    [Fact]
    public async Task ARuleCountsOnlyWithItsMarkAtItsPlaceInTheIPv4Policy()
    {
        var tools = new Tools();
        var network = Holding(
            tools,
            Marked(10001, 0xA6020, 42602),
            Marked(10002, 0xA602, 42602),
            Marked(10001, 0xA602, 42602) with { Family = PolicyRules.Inet6 });

        await network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None);

        Assert.True(tools.Called("ip rule add pref 10001 fwmark 42498 lookup 42602"));
    }

    [Fact]
    public async Task ARuleTheHostRefusesToTakeOffReportsTheFailure()
    {
        var tools = new Tools();
        tools.Answers["ip rule del"] = new CommandResult(2, string.Empty, "RTNETLINK answers: No such file or directory");
        var network = Holding(tools, Marked(10001, 0xA602, 254));

        await Assert.ThrowsAsync<HostNetworkException>(
            () => network.RuleAsync(0xA602, 42602, 10001, false, CancellationToken.None));

        Assert.True(tools.Called("ip rule del pref 10001 fwmark 42498"));
    }

    [Theory]
    [InlineData(254u, uint.MaxValue, PolicyRules.ToTable)]
    [InlineData(42602u, 0xFFFFFF00u, PolicyRules.ToTable)]
    [InlineData(42602u, uint.MaxValue, PolicyRules.Unreachable)]
    public async Task ARuleWithTheWrongTableMaskOrActionIsReplaced(uint table, uint mask, byte action)
    {
        var tools = new Tools();
        var network = Holding(tools, Marked(10001, 0xA602, table) with { Mask = mask, Action = action });

        Assert.True(await network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None));

        Assert.StartsWith("ip rule del pref 10001 fwmark 42498", tools.Calls[0]);
        Assert.Equal("ip rule add pref 10001 fwmark 42498 lookup 42602", tools.Calls[1]);
        Assert.Equal("ip -6 rule add pref 10001 fwmark 42498 lookup 42602", tools.Calls[2]);
    }

    [Fact]
    public async Task AnExistingIPv4RuleDoesNotHideAMissingIPv6Rule()
    {
        var tools = new Tools();
        var network = Holding(tools, Marked(10001, 0xA602, 42602));

        await network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None);

        Assert.Equal(["ip -6 rule add pref 10001 fwmark 42498 lookup 42602"], tools.Calls);
    }

    [Fact]
    public async Task APartialFamilyFailureIsRepairedOnTheNextAttempt()
    {
        var tools = new Tools();
        tools.Answers["ip -6 rule add"] = new CommandResult(2, "", "refused");
        var held = new List<PolicyRule>();
        var network = new IpHostNetwork(tools, (_, _) => null, () => held);
        await Assert.ThrowsAsync<HostNetworkException>(
            () => network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None));
        held.Add(Marked(10001, 0xA602, 42602));
        tools.Answers.Clear();
        tools.Calls.Clear();

        Assert.True(await network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None));

        Assert.Equal(["ip -6 rule add pref 10001 fwmark 42498 lookup 42602"], tools.Calls);
    }

    [Fact]
    public async Task DuplicateOwnedRulesAreReplacedByOneExactRule()
    {
        var tools = new Tools();
        var network = Holding(tools, Marked(10001, 0xA602, 254), Marked(10001, 0xA602, 42602));

        await network.RuleAsync(0xA602, 42602, 10001, true, CancellationToken.None);

        Assert.Equal(2, tools.Calls.Count(call => call.StartsWith("ip rule del", StringComparison.Ordinal)));
        Assert.Equal(1, tools.Calls.Count(call => call.StartsWith("ip rule add", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheSealIsAddedInBothFamiliesWhenTheHostLacksIt()
    {
        var tools = new Tools();
        var network = Holding(tools, Marked(10000, 0xA601, 254));

        await network.SealAsync(CancellationToken.None);

        Assert.Equal(
            [
                "ip rule add pref 10255 fwmark 0xa600/0xffffff00 unreachable",
                "ip -6 rule add pref 10255 fwmark 0xa600/0xffffff00 unreachable",
            ],
            tools.Calls);
    }

    [Fact]
    public async Task ASealTheHostHoldsIsNotAddedAgain()
    {
        var tools = new Tools();
        var network = Holding(tools, Seal(PolicyRules.Inet), Seal(PolicyRules.Inet6));

        await network.SealAsync(CancellationToken.None);

        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task ASealTheHostHoldsInOneFamilyIsAddedInTheOther()
    {
        var tools = new Tools();
        var network = Holding(tools, Seal(PolicyRules.Inet));

        await network.SealAsync(CancellationToken.None);

        Assert.Equal(["ip -6 rule add pref 10255 fwmark 0xa600/0xffffff00 unreachable"], tools.Calls);
    }

    [Fact]
    public async Task OnlyARuleThatRefusesAtThePlaceOfTheSealCountsAsTheSeal()
    {
        var tools = new Tools();
        var network = Holding(
            tools,
            Seal(PolicyRules.Inet) with { Priority = (uint)OutboundRules.SealPriority + 1 },
            Seal(PolicyRules.Inet6) with { Action = PolicyRules.ToTable });

        await network.SealAsync(CancellationToken.None);

        Assert.True(tools.Called("ip rule add pref 10255"));
        Assert.True(tools.Called("ip -6 rule add pref 10255"));
    }

    [Fact]
    public void TheSealStandsRightBelowTheLastMark()
    {
        Assert.Equal(OutboundRules.SealPriority, OutboundRules.PriorityOf(OutboundRules.LastMark) + 1);
    }

    [Fact]
    public async Task ARuleIsTakenOffOnlyWhenTheHostHoldsIt()
    {
        var tools = new Tools();
        var network = Holding(tools, Marked(10001, 0xA602, 42602));

        await network.RuleAsync(0xA602, 42602, 10001, false, CancellationToken.None);
        var empty = new Tools();
        await Holding(empty).RuleAsync(0xA602, 42602, 10001, false, CancellationToken.None);

        Assert.True(tools.Called("ip rule del pref 10001"));
        Assert.False(empty.Called("ip rule del"));
    }

    [Fact]
    public async Task TheUplinkIsTakenOutOfTheDefaultRoute()
    {
        var tools = new Tools();
        tools.Answers["ip route show default"] =
            new CommandResult(0, "default via 192.168.1.1 dev eth0 proto dhcp src 192.168.1.37 metric 100\n", string.Empty);
        var network = new IpHostNetwork(tools);

        Assert.Equal("eth0", await network.UplinkAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARulesetTheFirewallRefusesIsToldAbout()
    {
        var tools = new Tools();
        tools.Answers["nft"] = new CommandResult(1, string.Empty, "syntax error, unexpected junk\n");
        var network = new IpHostNetwork(tools);

        var refused = await Assert.ThrowsAsync<HostNetworkException>(
            () => network.FirewallAsync("junk", CancellationToken.None));

        Assert.Contains("syntax error", refused.Message, StringComparison.Ordinal);
        Assert.Equal("junk", tools.Input);
    }

    [Fact]
    public async Task AnInterfaceThatDoesNotReadIsGivenItsAddressesAnew()
    {
        var tools = new Tools();
        var network = new IpHostNetwork(tools);

        await network.AddressAsync("awgbor", ["10.9.0.2/32", "fd00::2/128"], CancellationToken.None);

        Assert.Equal(
            [
                "ip -j address show dev awgbor",
                "ip address flush dev awgbor",
                "ip address add 10.9.0.2/32 dev awgbor",
                "ip address add fd00::2/128 dev awgbor",
            ],
            tools.Calls);
    }

    [Fact]
    public async Task TheAddressesAnInterfaceCarriesAreLeftInPlace()
    {
        var tools = new Tools();
        tools.Answers["ip -j address show"] = new CommandResult(0, Carried, string.Empty);
        var network = new IpHostNetwork(tools);

        await network.AddressAsync("awgbor", ["10.9.0.2/24", "10.9.0.3/24", "fd00:0:0::2/112"], CancellationToken.None);

        Assert.Equal(["ip -j address show dev awgbor"], tools.Calls);
    }

    [Fact]
    public async Task OnlyTheAddressesThatDifferAreChanged()
    {
        var tools = new Tools();
        tools.Answers["ip -j address show"] = new CommandResult(0, Carried, string.Empty);
        var network = new IpHostNetwork(tools);

        await network.AddressAsync("awgbor", ["10.9.0.3/24", "fd00::2/112", "10.9.0.4/24"], CancellationToken.None);

        Assert.Equal(
            [
                "ip -j address show dev awgbor",
                "ip address del 10.9.0.2/24 dev awgbor",
                "ip -j address show dev awgbor",
                "ip address add 10.9.0.4/24 dev awgbor",
            ],
            tools.Calls);
    }

    [Fact]
    public async Task ATableOfTheFirewallIsReadAsTheHostGivesIt()
    {
        var tools = new Tools();
        tools.Answers["nft -j list table inet amneziageo_rt"] = new CommandResult(0, "{\"nftables\": []}", string.Empty);
        tools.Answers["nft -j list table inet nope"] = new CommandResult(1, string.Empty, "Error: No such file or directory");
        var network = new IpHostNetwork(tools);

        Assert.Equal("{\"nftables\": []}", await network.ReadFirewallAsync("amneziageo_rt", CancellationToken.None));
        Assert.Empty(await network.ReadFirewallAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task ACounterIsReadFromTheKernelWithoutATool()
    {
        var tools = new Tools();
        var network = new IpHostNetwork(tools, (table, name) => table == "amneziageo_rt" && name == "taken" ? 42 : null);

        Assert.Equal(42, await network.CounterAsync("amneziageo_rt", "taken", CancellationToken.None));
        Assert.Null(await network.CounterAsync("amneziageo_rt", "taken6", CancellationToken.None));
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task ACounterTheKernelDoesNotGiveIsAskedOfTheToolFromThenOn()
    {
        var asked = 0;
        var tools = new Tools();
        tools.Answers["nft -j list counter inet amneziageo_rt taken"] = new CommandResult(
            0,
            """{"nftables": [{"metainfo": {"json_schema_version": 1}}, {"counter": {"family": "inet", "name": "taken", "table": "amneziageo_rt", "handle": 7, "packets": 5, "bytes": 300}}]}""",
            string.Empty);
        var network = new IpHostNetwork(tools, (_, _) =>
        {
            asked++;
            throw new NetlinkException("the kernel refused the netlink request", 1);
        });

        Assert.Equal(5, await network.CounterAsync("amneziageo_rt", "taken", CancellationToken.None));
        Assert.Equal(5, await network.CounterAsync("amneziageo_rt", "taken", CancellationToken.None));
        Assert.Equal(1, asked);
        Assert.Equal(2, tools.Calls.Count);
    }

    [Fact]
    public async Task TheRulesTheKernelDoesNotGiveAreAskedOfTheToolFromThenOn()
    {
        var asked = 0;
        var tools = new Tools();
        tools.Answers["ip -N -j rule show pref 10000"] = new CommandResult(0,
            """[{"priority":10000,"fwmark":"0xa601","table":"254"}]""", string.Empty);
        tools.Answers["ip -6 -N -j rule show pref 10000"] = new CommandResult(0,
            """[{"priority":10000,"fwmark":"0xa601","table":"254"}]""", string.Empty);
        tools.Answers["ip rule show pref 10255"] = new CommandResult(0, "10255:\tfrom all fwmark 0xa600/0xffffff00 unreachable\n", string.Empty);
        var network = new IpHostNetwork(tools, (_, _) => null, () =>
        {
            asked++;
            throw new NetlinkException("the kernel refused the netlink request", 1);
        });

        await network.RuleAsync(0xA601, 254, 10000, true, CancellationToken.None);
        await network.SealAsync(CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.Equal(
            [
                "ip -N -j rule show pref 10000",
                "ip -6 -N -j rule show pref 10000",
                "ip rule show pref 10255",
                "ip -6 rule show pref 10255",
                "ip -6 rule add pref 10255 fwmark 0xa600/0xffffff00 unreachable",
            ],
            tools.Calls);
    }

    [Theory]
    [InlineData("0xa6010", "254", null, null, false)]
    [InlineData("0xa601", "42601", null, null, true)]
    [InlineData("0xa601", "254", "0xffffff00", null, true)]
    [InlineData("0xa601", null, null, "7", true)]
    public async Task TheToolFallbackChecksTheWholeRule(string mark, string? table, string? mask, string? action, bool remove)
    {
        var tools = new Tools();
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { new { priority = 10000, fwmark = mark,
            table = table ?? "0", fwmask = mask ?? "0xffffffff", action = action ?? "1" } });
        tools.Answers["ip -N -j rule show"] = new CommandResult(0, json, "");
        tools.Answers["ip -6 -N -j rule show"] = new CommandResult(0, "[]", "");
        var network = new IpHostNetwork(tools, (_, _) => null, () => throw new NetlinkException("refused", 1));

        await network.RuleAsync(0xA601, 254, 10000, true, CancellationToken.None);

        Assert.Equal(remove, tools.Called("ip rule del"));
        Assert.True(tools.Called("ip rule add pref 10000 fwmark 42497 lookup 254"));
    }

    [Theory]
    [InlineData(1, "", "refused")]
    [InlineData(0, "not json", "")]
    public async Task AnUnreadablePolicyIsNotTreatedAsAnEmptyOne(int code, string output, string error)
    {
        var tools = new Tools();
        tools.Answers["ip -N -j rule show"] = new CommandResult(code, output, error);
        var network = new IpHostNetwork(tools, (_, _) => null, () => throw new NetlinkException("refused", 1));

        await Assert.ThrowsAsync<HostNetworkException>(
            () => network.RuleAsync(0xA601, 254, 10000, true, CancellationToken.None));

        Assert.Single(tools.Calls);
    }

    // A network whose kernel holds the rules given.
    private static IpHostNetwork Holding(Tools tools, params PolicyRule[] rules) => new(tools, (_, _) => null, () => rules);

    // A rule of the IPv4 policy that sends a marked packet to a table, as the panel lays it.
    private static PolicyRule Marked(uint priority, uint mark, uint table) =>
        new(PolicyRules.Inet, priority, mark, uint.MaxValue, PolicyRules.ToTable, table);

    // The rule that refuses a marked packet no outbound takes, as the panel lays it in a family.
    private static PolicyRule Seal(byte family) =>
        new(family, (uint)OutboundRules.SealPriority, 0xA600, 0xFFFFFF00, PolicyRules.Unreachable, 0);
}
