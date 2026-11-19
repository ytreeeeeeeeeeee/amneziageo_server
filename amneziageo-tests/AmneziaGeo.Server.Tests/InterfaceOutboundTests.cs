using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AmneziaGeo.Server.Api.Outbounds;
using AmneziaGeo.Server.Api.Dns;
using AmneziaGeo.Server.Api.Rules;
using AmneziaGeo.Server.Routing.Access;
using AmneziaGeo.Server.Routing.Names;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Geo;
using AmneziaGeo.Server.Routing.Balance;
using AmneziaGeo.Server.Routing.Dns;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Route;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AmneziaGeo.Server.Tests;

public class InterfaceOutboundTests
{
    [Fact]
    public async Task InvalidPersistedRoutingIdentityCannotClearAnotherTable()
    {
        var ledger = new Ledger();
        var driver = new InterfaceOutboundDriver(ledger);
        await Assert.ThrowsAsync<HostNetworkException>(() => driver.WithdrawAsync(
            Settings() with { Table = OutboundRules.MainTable }, CancellationToken.None));
        await Assert.ThrowsAsync<HostNetworkException>(() => driver.WithdrawAsync(
            Settings() with { Table = OutboundRules.FirstTable + 1 }, CancellationToken.None));
        Assert.Empty(ledger.Steps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StorageRejectsDeviceOwnershipConflictsEvenWhenTheManagedOutboundIsDisabled(bool managedFirst)
    {
        using var bench = new Bench();
        var external = Settings() with { Name = "alias" };
        var managed = AwgOutboundDefaults.Fresh(external.Device) with
        {
            IsEnabled = false, Host = "127.0.0.1", Address = ["10.80.1.2/32"],
            PeerKey = "eOWG0GXBLrpZOZ+FCwCJTvZ5rBEKHZ8NPvxNM3nJPl4=",
        };
        var first = await bench.Outbounds.AddAsync(managedFirst ? managed : external, CancellationToken.None);
        Assert.True(first.IsOk);
        var conflict = await bench.Outbounds.AddAsync(managedFirst ? external : managed, CancellationToken.None);
        Assert.Equal("interface-in-use", conflict.Code);
        var changed = await bench.Outbounds.ChangeAsync(first.Record!.Id, managedFirst ? external : managed, CancellationToken.None);
        Assert.Equal("interface-ownership-change", changed.Code);
        Assert.Equal(first.Record.Kind, (await bench.Outbounds.FindAsync(first.Record.Id, CancellationToken.None))!.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATypeChangeCannotDeleteOrTakeOverTheSameExistingDevice(bool toManaged)
    {
        var ledger = new Ledger();
        var external = Settings();
        var managed = new Owned { Name = external.Name, Mark = external.Mark, Table = external.Table };
        ledger.Links.Add(external.Device);
        using var host = new OutboundHost(ledger, [new InterfaceOutboundDriver(ledger), new OwningDriver(ledger)]);
        OutboundConfig before = toManaged ? external : managed;
        OutboundConfig after = toManaged ? managed : external;
        await host.ApplyAsync(before, CancellationToken.None);
        Assert.Equal("interface-ownership-change", host.CheckReservation(after, before)?.Code);
        Assert.False((await host.ApplyAsync(after, CancellationToken.None, before)).Carries);
        Assert.True(ledger.HasLink(external.Device));
        Assert.DoesNotContain(ledger.Steps, step => step.StartsWith("remove ", StringComparison.Ordinal));
    }

    private sealed record Owned : OutboundConfig
    {
        public override string InterfaceName => "tt-ams";
        public override bool OwnsInterface => true;
        public override OutboundFault? CheckSettings() => null;
    }

    private sealed class OwningDriver(Ledger ledger) : IOutboundDriver
    {
        public bool Supports(string kind) => kind == "";
        public OutboundConfig Draft(string name, string kind) => new Owned { Name = name };
        public Task ApplyAsync(OutboundConfig outbound, CancellationToken ct) => Task.CompletedTask;
        public Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct) => ledger.RemoveLinkAsync(outbound.InterfaceName, ct);
        public Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct) => Task.FromResult(false);
        public OutboundState State(OutboundConfig outbound) => new(outbound.Name, true, null, null, true, "");
    }

    [Fact]
    public async Task DnsUsesTheNameServersFamilyAndNeverFallsBackToTheHostForAnUnsupportedFamily()
    {
        var four = Settings();
        var six = four with { Name = "six", Ipv4 = false, Ipv6 = true, Mark = four.Mark + 1 };
        var ways = new RouteWays([four, six], [new Balancer { Name = "both", Members = [four.Name, six.Name] }], null);
        Assert.Null(DnsExit.Way(DnsDefaults.Settings with { Outbound = four.Name }, ways,
            family: AddressFamily.InterNetworkV6).Mark);
        Assert.Equal(six.Mark, DnsExit.Way(DnsDefaults.Settings with { Outbound = "both" }, ways,
            family: AddressFamily.InterNetworkV6).Mark);
        var asked = new List<AddressFamily>();
        using var upstream = new DnsUpstream(["192.0.2.53", "2001:db8::53"], TimeSpan.FromSeconds(1),
            () => throw new InvalidOperationException("must not fall back to host"),
            familyWay: family => { asked.Add(family); return null; });
        Assert.Null(await upstream.AskAsync(new byte[12], false, CancellationToken.None));
        Assert.Equal([AddressFamily.InterNetwork, AddressFamily.InterNetworkV6], asked);
    }

    [Theory]
    [InlineData(false, false, "", "", "bad-ip-families")]
    [InlineData(true, false, "", "", null)]
    [InlineData(false, true, "", "", null)]
    [InlineData(true, true, "192.0.2.1", "fe80::1", null)]
    [InlineData(true, false, "2001:db8::1", "", "bad-gateway")]
    [InlineData(true, false, "", "fe80::1", "bad-gateway")]
    [InlineData(true, false, "0.0.0.0", "", "bad-gateway")]
    [InlineData(true, false, "224.0.0.1", "", "bad-gateway")]
    [InlineData(false, true, "", "ff02::1", "bad-gateway")]
    public void FamiliesAndOptionalGatewaysAreValidated(bool four, bool six, string gateway4, string gateway6,
        string? code)
    {
        var config = Settings() with { Ipv4 = four, Ipv6 = six, Gateway4 = gateway4, Gateway6 = gateway6 };
        Assert.Equal(code, OutboundRules.Check(config)?.Code);
    }

    [Theory]
    [InlineData("fe80::1%2")]
    [InlineData("fe80::1%0")]
    [InlineData("fe80::1%tt-ams")]
    [InlineData("[fe80::1%2]")]
    public async Task ZonedGatewaysAreRejectedBeforeStorageOrHostChanges(string gateway)
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Settings() with { Ipv6 = true, Gateway6 = "fe80::1" }, CancellationToken.None);
        var original = Assert.IsType<InterfaceOutboundConfig>(added.Record);
        var invalid = original with { Gateway6 = gateway };
        Assert.Equal("bad-gateway", OutboundRules.Check(invalid)?.Code);
        var created = await bench.Outbounds.AddAsync(invalid with { Name = "bad-zone" }, CancellationToken.None);
        Assert.Equal("bad-gateway", created.Code);
        Assert.Equal("bad-gateway", (await bench.Outbounds.ChangeAsync(original.Id, invalid, CancellationToken.None)).Code);
        Assert.Equal(2, await bench.Db.Outbounds.CountAsync());
        var held = Assert.IsType<InterfaceOutboundConfig>(await bench.Outbounds.FindAsync(original.Id, CancellationToken.None));
        Assert.Equal(original.Gateway6, held.Gateway6);
        Assert.Equal(original.UpdatedUtc, held.UpdatedUtc);
        var ledger = new Ledger();
        ledger.Links.Add(original.Device);
        await Assert.ThrowsAsync<HostNetworkException>(() => new InterfaceOutboundDriver(ledger)
            .ApplyAsync(invalid, CancellationToken.None));
        Assert.Empty(ledger.Steps);
    }

    [Fact]
    public async Task GatewaysAreNormalizedBeforeStorageAndHostApplication()
    {
        var config = Settings() with { Ipv6 = true, Gateway4 = "0xC0000201", Gateway6 = "[fe80::1]" };
        Assert.Null(OutboundRules.Check(config));
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(config, CancellationToken.None);
        Assert.True(added.IsOk);
        var row = await bench.Db.Set<InterfaceOutboundEntity>().SingleAsync();
        Assert.Equal("192.0.2.1", row.Gateway4);
        Assert.Equal("fe80::1", row.Gateway6);
        var saved = Assert.IsType<InterfaceOutboundConfig>(added.Record);
        var ledger = new Ledger();
        ledger.Links.Add(saved.Device);
        await new InterfaceOutboundDriver(ledger).ApplyAsync(saved, CancellationToken.None);
        Assert.Contains($"default-route tt-ams {saved.Table} InterNetwork 192.0.2.1", ledger.Steps);
        Assert.Contains($"default-route tt-ams {saved.Table} InterNetworkV6 fe80::1", ledger.Steps);
        Assert.Equal("2001:db8::1", (config with { Gateway6 = "2001:0db8:0:0:0:0:0:1" }).Gateway6);
        Assert.Equal("bad-gateway", OutboundRules.Check(config with { Gateway6 = "[not-an-ip]" })?.Code);
    }

    [Fact]
    public void ExistingDeviceNamesAndDnsFamiliesAreChecked()
    {
        Assert.Null(OutboundRules.Check(Settings() with { Device = "VLAN.100" }));
        Assert.Equal("bad-interface-name", OutboundRules.Check(Settings() with { Device = "bad\"dev" })?.Code);
        Assert.Equal("unsupported-ip-family", OutboundRules.Check(Settings() with { Dns = ["2001:db8::53"] })?.Code);
        Assert.Equal("unsupported-ip-family", OutboundRules.Check(Settings() with
            { Probe = "[2001:db8::53]:53", ProbeEvery = 30 })?.Code);
    }

    [Theory]
    [InlineData("{\"kind\":\"interface\",\"ipv4\":true}")]
    [InlineData("{\"kind\":\"interface\",\"ipv6\":true}")]
    [InlineData("{\"kind\":\"interface\",\"ipv4\":true,\"ipv6\":false,\"privateKey\":\"unused\"}")]
    public void InterfaceRequestsRequireExplicitFamiliesAndRefuseAwgFields(string text) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutboundRequest>(text, Json()));

    [Fact]
    public void TheWireContractKeepsInterfaceFieldsAndFalseFamiliesWithoutAwgFields()
    {
        var request = JsonSerializer.Deserialize<OutboundRequest>("""
            {"name":"exit-ams","ipv4":true,"ipv6":false,"gateway4":" 192.0.2.1 ",
             "interfaceName":" tt-ams ","isEnabled":true,"kind":"interface"}
            """, Json())!;
        var config = Assert.IsType<InterfaceOutboundConfig>(request.ToConfig());
        Assert.Equal("tt-ams", config.Device);
        Assert.Equal("192.0.2.1", config.Gateway4);
        var answers = new OutboundAnswers([new InterfaceOutboundResponses()]);
        var response = answers.Outbound(config, new(config.Name, true, null, null, true, ""), true);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(response, Json()));
        Assert.False(wire.RootElement.GetProperty("ipv6").GetBoolean());
        Assert.Equal("tt-ams", wire.RootElement.GetProperty("interfaceName").GetString());
        Assert.False(wire.RootElement.TryGetProperty("privateKey", out _));
    }

    [Fact]
    public async Task ApplyingAndRemovingAnOutboundLeaveTheExistingDeviceUntouched()
    {
        var ledger = new Ledger();
        var config = Settings();
        ledger.Links.Add(config.Device);
        using var host = new OutboundHost(ledger, [new InterfaceOutboundDriver(ledger)]);
        Assert.True((await host.ApplyAsync(config, CancellationToken.None)).Carries);
        await host.WithdrawAsync(config, CancellationToken.None);
        Assert.True(ledger.HasLink(config.Device));
        Assert.Contains($"clear {config.Table}", ledger.Steps);
        Assert.DoesNotContain(ledger.Steps, step => new[] { "add ", "remove ", "address ", "up " }
            .Any(prefix => step.StartsWith(prefix, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TheDriverChoosesARouteOrRejectionForEachFamily(bool ipv4, bool ipv6)
    {
        var ledger = new Ledger();
        var config = Settings() with { Ipv4 = ipv4, Ipv6 = ipv6,
            Gateway4 = ipv4 ? "192.0.2.1" : "", Gateway6 = ipv6 ? "fe80::1" : "" };
        ledger.Links.Add(config.Device);
        var driver = new InterfaceOutboundDriver(ledger);
        Assert.False(await driver.MendAsync(config, CancellationToken.None));
        Assert.Equal(new[]
        {
            ipv4 ? $"default-route tt-ams {config.Table} InterNetwork 192.0.2.1"
                : $"unreachable-route {config.Table} InterNetwork",
            ipv6 ? $"default-route tt-ams {config.Table} InterNetworkV6 fe80::1"
                : $"unreachable-route {config.Table} InterNetworkV6",
        }, ledger.Steps);
    }

    [Fact]
    public async Task MissingOrDownInterfacesAreNotCreatedOrBroughtUp()
    {
        var ledger = new Ledger();
        var driver = new InterfaceOutboundDriver(ledger);
        var draft = Assert.IsType<InterfaceOutboundConfig>(driver.Draft("new", OutboundKind.Interface));
        Assert.False(draft.Ipv4);
        Assert.False(draft.Ipv6);
        Assert.Equal(30, draft.ProbeEvery);
        await Assert.ThrowsAsync<HostNetworkException>(() => driver.ApplyAsync(Settings(), CancellationToken.None));
        ledger.Links.Add("tt-ams");
        ledger.Down.Add("tt-ams");
        await Assert.ThrowsAsync<HostNetworkException>(() => driver.MendAsync(Settings(), CancellationToken.None));
        Assert.Empty(ledger.Steps);
        Assert.False(driver.State(Settings()).Carries);
    }

    [Fact]
    public void SharedInterfacesKeepPrivateNetworkPoliciesSeparateAndDoNotBlockUnrelatedIngress()
    {
        var closed = Settings();
        var open = closed with { Id = 2, Name = "open", Mark = closed.Mark + 1, Table = closed.Table + 1, ClosePrivate = false };
        var text = OutboundRuleset.Text([closed, open], "eth0");
        Assert.Contains($"meta mark 0x{closed.Mark:x} oifname \"tt-ams\" ct state new ip daddr", text);
        Assert.DoesNotContain($"meta mark 0x{open.Mark:x} oifname \"tt-ams\" ct state new ip daddr", text);
        Assert.DoesNotContain("iifname { \"tt-ams\" } drop", text);
        Assert.Contains($"meta mark 0x{open.Mark:x} oifname \"tt-ams\" masquerade", text);
    }

    [Theory]
    [InlineData(BalanceStrategy.Priority)]
    [InlineData(BalanceStrategy.Round)]
    [InlineData(BalanceStrategy.Sticky)]
    public void BalancersAndTheRouteTesterUseOnlyMembersOfThePacketsFamily(string strategy)
    {
        var four = Settings();
        var six = four with { Id = 2, Name = "six", Mark = four.Mark + 1, Table = four.Table + 1, Ipv4 = false, Ipv6 = true };
        var rule = new RouteRule { Id = 1, Name = "both", Outbound = "pick" };
        var group = new Balancer { Name = "pick", Strategy = strategy, Members = [four.Name, six.Name] };
        var plan = RoutePlan.Build([rule], [four, six], GeoIndex.Load([], new MemoryGeoFiles()), ["inbound"],
            balancers: [group]);
        var leg = Assert.Single(plan.Legs);
        Assert.Equal([four.Mark], leg.Exit.ForFamily(false).Marks);
        Assert.Equal([six.Mark], leg.Exit.ForFamily(true).Marks);
        var lines = string.Join('\n', RouteRuleset.Lines(leg));
        Assert.Contains($"meta nfproto ipv4 meta mark set 0x{four.Mark:x}", lines);
        Assert.Contains($"meta nfproto ipv6 meta mark set 0x{six.Mark:x}", lines);
        var verdict = RouteProbe.Test(plan, new RouteQuery { Addresses = [IPAddress.Parse("2001:db8::1")] }, (_, _) => false);
        Assert.Equal(RouteProbe.Out, verdict.Verdict);
        Assert.Equal(six.Mark, verdict.Leg!.Mark);
        var single = RoutePlan.Build([rule with { Outbound = four.Name }], [four],
            GeoIndex.Load([], new MemoryGeoFiles()), ["inbound"]);
        Assert.Equal(RouteProbe.Held, RouteProbe.Test(single,
            new RouteQuery { Addresses = [IPAddress.Parse("2001:db8::1")] }, (_, _) => false).Verdict);
    }

    [Fact]
    public async Task RouteTesterReportsTheAddressFamilyAndMembersFromTheHeldPlan()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Settings(), CancellationToken.None);
        var four = Assert.IsType<InterfaceOutboundConfig>(added.Record);
        var rule = new RouteRule { Id = 1, Name = "all", Outbound = four.Name };
        var plans = new RoutePlans();
        plans.Keep(RoutePlan.Build([rule], [four], GeoIndex.Load([], new MemoryGeoFiles()), ["inbound"]));
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new InterfaceOutboundDriver(ledger)]);
        var live = new BalanceLive();
        live.Keep([four.Name]);
        var sets = new DnsSets(ledger);
        var state = new DnsState();
        var book = new StandInBook(bench.Clock);
        using var dns = new DnsHost(bench.Services.GetRequiredService<IServiceScopeFactory>(), plans, sets, state,
            new AccessNames(), book, new DnsHolders(), new DnsStandIns(), bench.Clock, NullLogger<DnsHost>.Instance);
        var applier = new RouteApplier(bench.Rules, bench.Outbounds, bench.Balancers, bench.Configs, bench.Geo,
            bench.Resolver, state, bench.GeoFiles, plans, live, sets, bench.Clients, host, new AccessGate(),
            new NameGate(), bench.Templates, book, ledger, NullLogger<RouteApplier>.Instance);
        var tester = new RouteTester(applier, bench.Clients, bench.Configs, live, sets, dns);
        foreach (var address in new[] { "192.0.2.1", "2001:db8::1" })
        {
            var (fault, answer) = await tester.TestAsync(new(address, 443, "tcp", null, null), CancellationToken.None);
            Assert.Null(fault);
            Assert.NotNull(answer);
            Assert.Equal([address], answer.Addresses);
            var result = Assert.Single(answer.Results);
            Assert.Equal(address, result.Address);
            var ipv4 = IPAddress.Parse(address).AddressFamily == AddressFamily.InterNetwork;
            Assert.Equal(ipv4 ? RouteProbe.Out : RouteProbe.Held, result.Verdict);
            Assert.Equal(four.Name, result.Rule!.Outbound);
            Assert.Equal(ipv4, Assert.Single(result.Exit!.Members).IsPicked);
            Assert.Equal(result.Verdict, answer.Verdict);
            Assert.Equal(result.Rule, answer.Rule);
        }
    }

    [Fact]
    public async Task StorageTypeChangesAndBackupKeepInterfaceSettingsAndIdentity()
    {
        using var bench = new Bench();
        var direct = Assert.Single(await bench.Outbounds.ListAsync(CancellationToken.None));
        var result = await bench.Outbounds.ChangeAsync(direct.Id, Settings() with
        {
            Ipv6 = true, Gateway4 = "192.0.2.1", Gateway6 = "fe80::1", Dns = ["1.1.1.1"],
        }, CancellationToken.None);
        Assert.True(result.IsOk);
        var saved = Assert.IsType<InterfaceOutboundConfig>(result.Record);
        Assert.Equal(direct.Id, saved.Id);
        Assert.Equal(direct.Mark, saved.Mark);
        Assert.Equal(direct.CreatedUtc, saved.CreatedUtc);
        Assert.False(bench.Db.Database.HasPendingModelChanges());
        var copy = await new DatabaseBackup(bench.Db).CopyAsync(CancellationToken.None);
        try
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={copy}").Options);
            var row = Assert.IsType<InterfaceOutboundEntity>(await db.Outbounds.AsNoTracking().SingleAsync());
            var restored = Assert.IsType<InterfaceOutboundConfig>(row.ToConfig());
            Assert.Equal(saved.Id, restored.Id);
            Assert.Equal(saved.Device, restored.Device);
            Assert.Equal(saved.Ipv4, restored.Ipv4);
            Assert.Equal(saved.Ipv6, restored.Ipv6);
            Assert.Equal(saved.Gateway4, restored.Gateway4);
            Assert.Equal(saved.Gateway6, restored.Gateway6);
            Assert.Equal(saved.Dns, restored.Dns);
        }
        finally
        {
            File.Delete(copy);
        }

        var local = await bench.Outbounds.ChangeAsync(saved.Id, LocalOutboundDefaults.Direct(), CancellationToken.None);
        Assert.True(local.IsOk);
        Assert.Equal(direct.Id, local.Record!.Id);
        Assert.Equal(OutboundRules.MainTable, local.Record.Table);
        Assert.Empty(await bench.Db.Set<InterfaceOutboundEntity>().ToListAsync());
    }

    [Fact]
    public async Task MigrationCreatesOnlyCurrentColumnsAndRefusesADestructiveDowngrade()
    {
        using var bench = new Bench();
        var added = await bench.Outbounds.AddAsync(Settings(), CancellationToken.None);
        Assert.True(added.IsOk);
        Assert.False(bench.Db.Database.HasPendingModelChanges());
        var columns = await bench.Db.Database.SqlQueryRaw<string>(
            """SELECT name AS Value FROM pragma_table_info('InterfaceOutbounds')""").ToListAsync();
        Assert.Equal(["Gateway4", "Gateway6", "Id", "InterfaceName", "Ipv4", "Ipv6"], columns.Order());
        await Assert.ThrowsAsync<SqliteException>(() => bench.Db.GetService<IMigrator>()
            .MigrateAsync("20261113152742_AddLocalOutboundType"));
        Assert.Single(await bench.Db.Set<InterfaceOutboundEntity>().ToListAsync());
    }

    private static InterfaceOutboundConfig Settings() => new()
    {
        Name = "exit-ams", Device = "tt-ams", Ipv4 = true, Mark = OutboundRules.FirstMark,
        Table = OutboundRules.FirstTable,
    };

    private static JsonSerializerOptions Json()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        OutboundJson.Configure(options);
        return options;
    }
}
