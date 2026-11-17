using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Routing.Balance;
using AmneziaGeo.Server.Routing.Dns;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Route;

namespace AmneziaGeo.Server.Tests;

public class OutboundStoreTests
{
    [Fact]
    public async Task AFreshInstallCarriesTheWayOutThroughTheHost()
    {
        using var bench = new Bench();

        var held = Assert.Single(await bench.Outbounds.ListAsync(CancellationToken.None));

        Assert.Equal(LocalOutboundDefaults.DirectName, held.Name);
        Assert.Equal(OutboundKind.Local, held.Kind);
        Assert.Equal(OutboundRules.MainTable, held.Table);
        Assert.True(held.IsEnabled);
        Assert.IsType<LocalOutboundConfig>(held);
    }

    [Fact]
    public async Task ATunnelTakesTheFirstFreeMarkAndThePlaceAtTheEnd()
    {
        using var bench = new Bench();

        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        Assert.True(added.IsOk, added.Message);
        Assert.Equal(OutboundRules.FirstMark + 1, added.Record!.Mark);
        Assert.Equal(OutboundRules.FirstTable + 1, added.Record.Table);
        Assert.Equal(2, added.Record.Position);
        var awg = Assert.IsType<AwgOutboundConfig>(added.Record);
        Assert.Equal(awg.PublicKey, AmneziaGeo.Server.Core.Crypto.Curve25519.PublicOf(awg.PrivateKey));
    }

    [Fact]
    public async Task AnOutboundUnderATakenNameIsRefused()
    {
        using var bench = new Bench();
        await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        var again = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        Assert.Equal(OutboundOutcome.NameTaken, again.Outcome);
    }

    [Fact]
    public async Task ASettingOutOfShapeIsRefusedWithItsCode()
    {
        using var bench = new Bench();

        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor") with { Host = string.Empty },
            CancellationToken.None);

        Assert.Equal(OutboundOutcome.Invalid, added.Outcome);
        Assert.Equal("bad-host", added.Code);
    }

    [Fact]
    public async Task ChangingAnOutboundKeepsItsMarkAndItsPlace()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        var changed = await bench.Outbounds.ChangeAsync(
            added.Record!.Id,
            Tunnel("awgfar") with { Port = 51830 },
            CancellationToken.None);

        Assert.True(changed.IsOk, changed.Message);
        Assert.Equal("awgfar", changed.Record!.Name);
        Assert.Equal(51830, Assert.IsType<AwgOutboundConfig>(changed.Record).Port);
        Assert.Equal(added.Record.Mark, changed.Record.Mark);
        Assert.Equal(added.Record.Position, changed.Record.Position);
    }

    [Fact]
    public async Task AnOutboundIsTurnedOffAndBackOn()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        var off = await bench.Outbounds.SwitchAsync(added.Record!.Id, false, CancellationToken.None);
        var on = await bench.Outbounds.SwitchAsync(added.Record.Id, true, CancellationToken.None);

        Assert.False(off.Record!.IsEnabled);
        Assert.True(on.Record!.IsEnabled);
    }

    [Fact]
    public async Task AnOutboundSwapsPlacesWithItsNeighbourAndStopsAtTheEdge()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        await bench.Outbounds.MoveAsync(added.Record!.Id, true, CancellationToken.None);
        var order = await bench.Outbounds.ListAsync(CancellationToken.None);
        await bench.Outbounds.MoveAsync(added.Record.Id, true, CancellationToken.None);
        var again = await bench.Outbounds.ListAsync(CancellationToken.None);

        Assert.Equal(["awgbor", LocalOutboundDefaults.DirectName], order.Select(one => one.Name));
        Assert.Equal(["awgbor", LocalOutboundDefaults.DirectName], again.Select(one => one.Name));
    }

    [Fact]
    public async Task ARemovedOutboundIsGoneAndItsMarkComesBack()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        await bench.Outbounds.RemoveAsync(added.Record!.Id, string.Empty, CancellationToken.None);
        var next = await bench.Outbounds.AddAsync(Tunnel("awgfar"), CancellationToken.None);

        Assert.Null(await bench.Outbounds.FindAsync(added.Record.Id, CancellationToken.None));
        Assert.Equal(added.Record.Mark, next.Record!.Mark);
    }

    [Fact]
    public async Task ARenamedOutboundCarriesItsNameIntoTheRulesTheBalancersAndTheResolver()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);
        var rule = await bench.Rules.AddAsync(Rule("through bor", "awgbor"), CancellationToken.None);
        var other = await bench.Rules.AddAsync(Rule("straight", LocalOutboundDefaults.DirectName),
            CancellationToken.None);
        var group = await bench.Balancers.AddAsync(
            BalanceDefaults.Fresh("both") with { Members = ["awgbor", LocalOutboundDefaults.DirectName] },
            CancellationToken.None);
        await bench.Resolver.SaveAsync(DnsDefaults.Settings with { Outbound = "awgbor" }, CancellationToken.None);

        var changed = await bench.Outbounds.ChangeAsync(added.Record!.Id, Tunnel("awgfar"), CancellationToken.None);

        Assert.True(changed.IsOk, changed.Message);
        Assert.Equal("awgfar", (await bench.Rules.FindAsync(rule.Record!.Id, CancellationToken.None))?.Outbound);
        Assert.Equal(
            LocalOutboundDefaults.DirectName,
            (await bench.Rules.FindAsync(other.Record!.Id, CancellationToken.None))?.Outbound);
        Assert.Equal(
            ["awgfar", LocalOutboundDefaults.DirectName],
            (await bench.Balancers.FindAsync(group.Record!.Id, CancellationToken.None))!.Members);
        Assert.Equal("awgfar", (await bench.Resolver.ReadAsync(CancellationToken.None)).Outbound);
    }

    [Fact]
    public async Task AnOutboundARuleABalancerOrTheResolverLeavesThroughIsNotRemoved()
    {
        using var bench = new Bench();
        var ruled = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);
        var grouped = await bench.Outbounds.AddAsync(Tunnel("awgfar"), CancellationToken.None);
        var asked = await bench.Outbounds.AddAsync(Tunnel("awgdns"), CancellationToken.None);
        await bench.Rules.AddAsync(Rule("through bor", "awgbor"), CancellationToken.None);
        await bench.Balancers.AddAsync(BalanceDefaults.Fresh("both") with { Members = ["awgfar"] },
            CancellationToken.None);
        await bench.Resolver.SaveAsync(DnsDefaults.Settings with { Outbound = "awgdns" }, CancellationToken.None);

        var rule = await bench.Outbounds.RemoveAsync(ruled.Record!.Id, string.Empty, CancellationToken.None);
        var member = await bench.Outbounds.RemoveAsync(grouped.Record!.Id, string.Empty, CancellationToken.None);
        var resolver = await bench.Outbounds.RemoveAsync(asked.Record!.Id, string.Empty, CancellationToken.None);

        Assert.Equal("outbound-in-use", rule.Code);
        Assert.Contains("a rule leaves through", rule.Message, StringComparison.Ordinal);
        Assert.Equal("outbound-in-use", member.Code);
        Assert.Contains("the balancer 'both'", member.Message, StringComparison.Ordinal);
        Assert.Equal("outbound-in-use", resolver.Code);
        Assert.Contains("the resolver", resolver.Message, StringComparison.Ordinal);
        Assert.Equal(4, (await bench.Outbounds.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task AnOutboundTheRunningResolverAsksThroughIsNotRemoved()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgdns"), CancellationToken.None);
        await bench.Resolver.SaveAsync(DnsDefaults.Settings with { Outbound = "awgbor" }, CancellationToken.None);

        var held = await bench.Outbounds.RemoveAsync(added.Record!.Id, "awgdns", CancellationToken.None);
        var free = await bench.Outbounds.RemoveAsync(added.Record.Id, "awgbor", CancellationToken.None);

        Assert.Equal("outbound-in-use", held.Code);
        Assert.Contains("until it is restarted", held.Message, StringComparison.Ordinal);
        Assert.True(free.IsOk, free.Message);
    }

    [Fact]
    public async Task ACommandAgainstAnOutboundThePanelDoesNotHoldIsRefused()
    {
        using var bench = new Bench();

        Assert.Equal(OutboundOutcome.Unknown,
            (await bench.Outbounds.RemoveAsync(404, string.Empty, CancellationToken.None)).Outcome);
        Assert.Equal(OutboundOutcome.Unknown,
            (await bench.Outbounds.MoveAsync(404, true, CancellationToken.None)).Outcome);
        Assert.Equal(
            OutboundOutcome.Unknown,
            (await bench.Outbounds.SwitchAsync(404, false, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(OutboundKind.Wg)]
    [InlineData(OutboundKind.Ws)]
    public async Task AwgSettingsSurviveStorageThroughTheCommonConfig(string kind)
    {
        using var bench = new Bench();
        var tunnel = Tunnel("awgbor") with
        {
            Kind = kind,
            Proxy = kind == OutboundKind.Ws ? "wss://proxy.example.net:443" : string.Empty,
            PresharedKey = "mDrTJlNmwLJvJHm1TQKHKKFAF0eRDhOOP4nHDXBVOgo=",
            Dns = ["1.1.1.1"],
            Probe = "1.1.1.1",
            ProbeEvery = 30,
            ClosePrivate = false,
            Obfuscation = new AmneziaGeo.Server.Awg.Config.ObfuscationSettings
            {
                Jc = 4, Jmin = 50, Jmax = 1000, S1 = 76, S2 = 45, H1 = "5", H2 = "6", H3 = "7", H4 = "8",
            },
        };

        var added = await bench.Outbounds.AddAsync(tunnel, CancellationToken.None);
        Assert.True(added.IsOk, added.Message);
        var held = Assert.IsType<AwgOutboundConfig>(await bench.Outbounds.FindAsync(added.Record!.Id,
            CancellationToken.None));

        Assert.Equal(tunnel.Host, held.Host);
        Assert.Equal(tunnel.Port, held.Port);
        Assert.Equal(tunnel.Proxy, held.Proxy);
        Assert.Equal(tunnel.PrivateKey, held.PrivateKey);
        Assert.Equal(tunnel.PeerKey, held.PeerKey);
        Assert.Equal(tunnel.PresharedKey, held.PresharedKey);
        Assert.Equal(tunnel.Address, held.Address);
        Assert.Equal(tunnel.Mtu, held.Mtu);
        Assert.Equal(tunnel.Keepalive, held.Keepalive);
        Assert.Equal(tunnel.Obfuscation, held.Obfuscation);
        Assert.Equal(tunnel.Dns, held.Dns);
        Assert.Equal(tunnel.Probe, held.Probe);
        Assert.Equal(tunnel.ProbeEvery, held.ProbeEvery);
        Assert.False(held.ClosePrivate);
    }

    [Fact]
    public async Task ChangingATunnelToLocalClearsTheStoredAwgSettings()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Tunnel("awgbor"), CancellationToken.None);

        var changed = await bench.Outbounds.ChangeAsync(
            added.Record!.Id,
            LocalOutboundDefaults.Direct() with { Name = "awgbor" },
            CancellationToken.None);

        Assert.True(changed.IsOk, changed.Message);
        var local = Assert.IsType<LocalOutboundConfig>(changed.Record);
        Assert.Equal(OutboundRules.MainTable, local.Table);
        Assert.Equal(added.Record.Mark, local.Mark);
        var entity = await bench.Db.Outbounds.FindAsync(local.Id);
        Assert.NotNull(entity);
        Assert.IsType<LocalOutboundEntity>(entity);
        Assert.Empty(bench.Db.Set<AwgOutboundEntity>().Where(one => one.Id == local.Id));
    }

    [Fact]
    public async Task ALocalOutboundCanBecomeAwgWithoutLosingItsIdentityOrHolders()
    {
        using var bench = new Bench();
        var local = Assert.Single(await bench.Outbounds.ListAsync(CancellationToken.None));
        await bench.Rules.AddAsync(Rule("through direct", local.Name), CancellationToken.None);
        await bench.Balancers.AddAsync(BalanceDefaults.Fresh("both") with { Members = [local.Name] },
            CancellationToken.None);
        await bench.Resolver.SaveAsync(DnsDefaults.Settings with { Outbound = local.Name }, CancellationToken.None);

        var changed = await bench.Outbounds.ChangeAsync(local.Id, Tunnel("awgdirect"), CancellationToken.None);

        Assert.True(changed.IsOk, changed.Message);
        var awg = Assert.IsType<AwgOutboundConfig>(changed.Record);
        Assert.Equal(local.Id, awg.Id);
        Assert.Equal(local.Mark, awg.Mark);
        Assert.Equal(local.Position, awg.Position);
        Assert.Equal(local.CreatedUtc, awg.CreatedUtc);
        Assert.Equal("awgdirect", Assert.Single(await bench.Rules.ListAsync(CancellationToken.None)).Outbound);
        Assert.Equal(["awgdirect"], Assert.Single(await bench.Balancers.ListAsync(CancellationToken.None)).Members);
        Assert.Equal("awgdirect", (await bench.Resolver.ReadAsync(CancellationToken.None)).Outbound);
    }

    [Fact]
    public async Task AFailedSubtypeChangeRollsBackTheOriginalRow()
    {
        using var bench = new Bench();
        var local = Assert.Single(await bench.Outbounds.ListAsync(CancellationToken.None));
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(bench.Db.Database,
            "CREATE TRIGGER refuse_awg BEFORE INSERT ON AwgOutbounds BEGIN SELECT RAISE(ABORT, 'refused'); END");

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            bench.Outbounds.ChangeAsync(local.Id, Tunnel("awgdirect"), CancellationToken.None));

        bench.Db.ChangeTracker.Clear();
        var restored = Assert.IsType<LocalOutboundConfig>(await bench.Outbounds.FindAsync(local.Id, CancellationToken.None));
        Assert.Equal(local, restored with { Dns = local.Dns });
        Assert.Empty(bench.Db.Set<AwgOutboundEntity>());
    }

    private static RouteRule Rule(string name, string outbound) => RouteDefaults.Fresh(name) with
    {
        Outbound = outbound,
        Targets = ["geoip:ru"],
    };

    private static AwgOutboundConfig Tunnel(string name) => new AwgOutboundConfig()
    {
        Name = name,
        Kind = OutboundKind.Wg,
        IsEnabled = true,
        Host = "bor.sytes.net",
        Port = 51821,
        PrivateKey = "OMMbTfBGZmzVOenkLmL2hFRuMbAG9pOZzTZ4L1H+wF0=",
        PeerKey = "eOWG0GXBLrpZOZ+FCwCJTvZ5rBEKHZ8NPvxNM3nJPl4=",
        Address = ["10.8.1.5/32"],
        Mtu = 1380,
        Keepalive = 25,
    };
}
