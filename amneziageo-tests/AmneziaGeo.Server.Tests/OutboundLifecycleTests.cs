using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AmneziaGeo.Server.Api.Auth;
using AmneziaGeo.Server.Api.Configs;
using AmneziaGeo.Server.Api.Dns;
using AmneziaGeo.Server.Api.Outbounds;
using AmneziaGeo.Server.Api.Rules;
using AmneziaGeo.Server.Auth;
using AmneziaGeo.Server.Awg.Device;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Routing.Access;
using AmneziaGeo.Server.Routing.Balance;
using AmneziaGeo.Server.Routing.Dns;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Names;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Probe;
using AmneziaGeo.Server.Routing.Route;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Server.Tests;

public class OutboundLifecycleTests
{
    [Theory]
    [InlineData("seal")]
    [InlineData("rule")]
    public async Task AFailedFirstApplyStaysUnavailableUntilMendCompletesIt(string step)
    {
        var ledger = new Ledger { Refuses = step };
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver()]);
        var outbound = LocalOutboundDefaults.Direct() with { Probe = "" };

        var failed = await host.ApplyAsync(outbound, CancellationToken.None);

        Assert.False(failed.Carries);
        Assert.Equal(failed.Fault, host.State(outbound).Fault);
        Assert.False(await host.KeepRuleAsync(outbound, CancellationToken.None));
        ledger.Refuses = "";

        Assert.True(await host.MendAsync(outbound, CancellationToken.None));
        Assert.True(host.State(outbound).Carries);
        Assert.Contains(outbound.Mark, ledger.Rules);
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("clear")]
    [InlineData("remove")]
    public async Task AFailedSwitchOffKeepsProtectionAndRetriesEvenThoughTheDesiredOutboundIsDisabled(string step)
    {
        var ledger = new Ledger();
        var kernel = new Kernel();
        var outbound = Tunnel("switch-exit");
        kernel.Hold(new AwgDevice { Name = outbound.Name,
            Peers = [new AwgPeer { PublicKey = outbound.PeerKey, LastHandshake = DateTimeOffset.UtcNow }] });
        using var host = Host(ledger, kernel);
        Assert.True((await host.ApplyAsync(outbound, CancellationToken.None)).Carries);
        ledger.Refuses = step;
        var off = outbound with { IsEnabled = false };

        var failed = await host.ApplyAsync(off, CancellationToken.None);
        await host.FirewallAsync([off], CancellationToken.None);

        Assert.False(failed.Carries);
        Assert.False(host.State(off).Carries);
        Assert.NotEmpty(host.State(off).Fault);
        Assert.Contains("iifname { \"switch-exit\" } drop", ledger.Ruleset, StringComparison.Ordinal);
        ledger.Refuses = "";

        Assert.True(await host.MendAsync(off, CancellationToken.None));
        await host.FirewallAsync([off], CancellationToken.None);
        Assert.False(ledger.HasLink(outbound.Name));
        Assert.Empty(ledger.Rules);
        Assert.Empty(host.State(off).Fault);
        Assert.DoesNotContain(outbound.Name, ledger.Ruleset, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledInterfaceFoundAtStartupIsProtectedWhileItsCleanupIsRefused()
    {
        var ledger = new Ledger { Refuses = "seal" };
        var off = Tunnel("old-exit") with { IsEnabled = false };
        ledger.Links.Add(off.Name);
        using var host = Host(ledger, new Kernel());

        Assert.False((await host.ApplyAsync(off, CancellationToken.None)).Carries);
        await host.FirewallAsync([off], CancellationToken.None);
        Assert.Contains("iifname { \"old-exit\" } drop", ledger.Ruleset, StringComparison.Ordinal);
        ledger.Refuses = "";

        Assert.True(await host.MendAsync(off, CancellationToken.None));
        Assert.False(ledger.HasLink(off.Name));
    }

    [Fact]
    public async Task AStartupSealFailureDoesNotForgetTheExistingEnabledInterfaceWhenItIsThenSwitchedOff()
    {
        var ledger = new Ledger { Refuses = "seal" };
        var outbound = Tunnel("old-exit");
        ledger.Links.Add(outbound.Name);
        using var host = Host(ledger, new Kernel());

        Assert.False((await host.ApplyAsync(outbound, CancellationToken.None)).Carries);
        var off = outbound with { IsEnabled = false };
        Assert.False((await host.ApplyAsync(off, CancellationToken.None)).Carries);
        await host.FirewallAsync([off], CancellationToken.None);
        Assert.Contains("iifname { \"old-exit\" } drop", ledger.Ruleset, StringComparison.Ordinal);
        ledger.Refuses = "";

        Assert.True(await host.MendAsync(off, CancellationToken.None));
        Assert.False(ledger.HasLink(off.Name));
    }

    [Fact]
    public async Task AnAppliedRevisionCannotStandInForAnUnappliedDatabaseChange()
    {
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver()]);
        var original = LocalOutboundDefaults.Direct();
        await host.ApplyAsync(original, CancellationToken.None);
        var saved = original with { UpdatedUtc = original.UpdatedUtc.AddTicks(1) };

        Assert.False(host.State(saved).Carries);
        Assert.False(await host.KeepRuleAsync(saved, CancellationToken.None));

        Assert.True(await host.MendAsync(saved, CancellationToken.None));
        Assert.True(host.State(saved).Carries);
    }

    [Fact]
    public async Task ARefusedPolicyRepairIsRememberedAndRecovered()
    {
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver()]);
        var outbound = LocalOutboundDefaults.Direct();
        await host.ApplyAsync(outbound, CancellationToken.None);
        ledger.Rules.Clear();
        ledger.Refuses = "rule";

        await Assert.ThrowsAsync<HostNetworkException>(() => host.KeepRuleAsync(outbound, CancellationToken.None));
        Assert.False(host.State(outbound).Carries);
        ledger.Refuses = "";

        Assert.True(await host.MendAsync(outbound, CancellationToken.None));
        Assert.True(host.State(outbound).Carries);
        Assert.Contains(outbound.Mark, ledger.Rules);
    }

    [Fact]
    public async Task AReservedNameIsRejectedBeforeApiWritesAndCannotBeUsedToDeleteAnotherOwnersInterface()
    {
        using var bench = new Bench();
        var ledger = new Ledger();
        using var host = Host(ledger, new Kernel());
        var added = await bench.Outbounds.AddAsync(Tunnel("old-exit"), CancellationToken.None);
        var original = added.Record!;
        await host.ApplyAsync(original, CancellationToken.None);
        var changed = await bench.Outbounds.ChangeAsync(original.Id,
            (AwgOutboundConfig)original with { Name = "renamed-exit" }, CancellationToken.None);
        ledger.Refuses = "clear";
        Assert.False((await host.ApplyAsync(changed.Record!, CancellationToken.None, original)).Carries);
        var request = new LocalOutboundRequest { Name = original.Name, IsEnabled = true };
        var answers = new OutboundAnswers([new LocalOutboundResponses(), new AwgOutboundResponses()]);
        var before = (await bench.Outbounds.ListAsync(CancellationToken.None)).Count;

        var refused = await Invoke("AddAsync", request, bench.Outbounds, host, null, answers,
            NullLogger<OutboundHost>.Instance, CancellationToken.None);

        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(refused).StatusCode);
        Assert.Equal(before, (await bench.Outbounds.ListAsync(CancellationToken.None)).Count);
        var local = Assert.Single(await bench.Outbounds.ListAsync(CancellationToken.None), one => one.Kind == "local");
        refused = await Invoke("ChangeAsync", local.Id, request, bench.Outbounds, bench.Balancers, bench.Rules,
            host, null, null, answers, NullLogger<OutboundHost>.Instance, CancellationToken.None);
        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(refused).StatusCode);
        Assert.Equal(local.Name, (await bench.Outbounds.FindAsync(local.Id, CancellationToken.None))!.Name);

        // An external database writer can bypass the API, but the host must still protect its existing owner.
        var bypass = await bench.Outbounds.AddAsync(Tunnel(original.Name), CancellationToken.None);
        Assert.True(bypass.IsOk);
        ledger.Refuses = "";
        ledger.Steps.Clear();
        Assert.False((await host.ApplyAsync(bypass.Record!, CancellationToken.None)).Carries);
        await Assert.ThrowsAsync<HostNetworkException>(() => host.MendAsync(bypass.Record!, CancellationToken.None));
        await host.WithdrawAsync(bypass.Record!, CancellationToken.None);
        Assert.True(ledger.HasLink(original.Name));
        Assert.DoesNotContain(ledger.Steps, step => step.StartsWith("remove ", StringComparison.Ordinal));
        await bench.Outbounds.RemoveAsync(bypass.Record!.Id, "", CancellationToken.None);

        Assert.True(await host.MendAsync(changed.Record!, CancellationToken.None));
        Assert.False(ledger.HasLink(original.Name));
        Assert.Null(host.CheckReservation(request.ToConfig()));
        var replacement = await bench.Outbounds.AddAsync(Tunnel(original.Name), CancellationToken.None);
        await host.ApplyAsync(replacement.Record!, CancellationToken.None);
        await host.MendAsync(changed.Record!, CancellationToken.None);
        Assert.True(ledger.HasLink(original.Name));
    }

    [Fact]
    public async Task ARemovedOwnersFailedCleanupIsRetriedBeforeItsMarkCanBeReused()
    {
        var ledger = new Ledger();
        using var host = Host(ledger, new Kernel());
        var original = Tunnel("old-exit") with { Id = 1 };
        await host.ApplyAsync(original, CancellationToken.None);
        ledger.Refuses = "clear";
        await Assert.ThrowsAsync<HostNetworkException>(() => host.WithdrawAsync(original, CancellationToken.None));
        var replacement = Tunnel("new-exit") with { Id = 2 };
        Assert.False((await host.ApplyAsync(replacement, CancellationToken.None)).Carries);
        Assert.True(ledger.HasLink(original.Name));
        ledger.Refuses = "";

        Assert.True(await host.RetireMissingAsync([replacement], CancellationToken.None));
        Assert.True(await host.MendAsync(replacement, CancellationToken.None));
        Assert.False(ledger.HasLink(original.Name));
        Assert.True(ledger.HasLink(replacement.Name));
        Assert.Contains(replacement.Mark, ledger.Rules);
    }

    [Fact]
    public async Task DeletingAnUnfinishedRenameOnlyCleansItsActualResourceEvenIfTheUnusedTargetWasReused()
    {
        var ledger = new Ledger();
        using var host = Host(ledger, new Kernel());
        var original = Tunnel("old-exit") with { Id = 1 };
        await host.ApplyAsync(original, CancellationToken.None);
        var renamed = original with { Name = "new-exit" };
        ledger.Refuses = "clear";
        Assert.False((await host.ApplyAsync(renamed, CancellationToken.None, original)).Carries);
        await Assert.ThrowsAsync<HostNetworkException>(() => host.WithdrawAsync(renamed, CancellationToken.None));
        var other = Tunnel(renamed.Name) with { Id = 2, Mark = original.Mark + 1, Table = original.Table + 1 };
        await host.ApplyAsync(other, CancellationToken.None);
        Assert.True(ledger.HasLink(other.Name));
        ledger.Refuses = "";

        Assert.True(await host.RetireMissingAsync([other], CancellationToken.None));
        Assert.False(ledger.HasLink(original.Name));
        Assert.True(ledger.HasLink(other.Name));
        Assert.True(await host.RetireMissingAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task UnmanagedInterfacesCanBeSharedButNotTakenOverByAnOwningDriver()
    {
        var ledger = new Ledger();
        var driver = new ExternalDriver(ledger);
        using var host = new OutboundHost(ledger, [driver, new AwgOutboundDriver(ledger, new Kernel())]);
        var first = new ExternalOutbound { Name = "first", Link = "external", Id = 1,
            Mark = OutboundRules.FirstMark, Table = OutboundRules.FirstTable };
        var second = first with { Name = "second", Id = 2, Mark = OutboundRules.FirstMark + 1,
            Table = OutboundRules.FirstTable + 1 };
        ledger.Links.Add(first.Link);

        Assert.True((await host.ApplyAsync(first, CancellationToken.None)).Carries);
        Assert.True((await host.ApplyAsync(second, CancellationToken.None)).Carries);
        Assert.Null(host.CheckReservation(second));
        Assert.NotNull(host.CheckReservation(Tunnel(first.Link) with { Id = 3, Mark = OutboundRules.FirstMark + 2 }));
        await host.WithdrawAsync(first, CancellationToken.None);
        Assert.True(ledger.HasLink(first.Link));
        Assert.True(host.State(second).Carries);
    }

    [Fact]
    public async Task CancellationAfterDriverResourcesAppearPreservesTheirOwnershipAndRetry()
    {
        var ledger = new Ledger();
        var driver = new ExternalDriver(ledger) { CancelApply = true };
        using var host = new OutboundHost(ledger, [driver]);
        var outbound = new ExternalOutbound { Name = "first", Link = "external", Id = 1,
            Mark = OutboundRules.FirstMark, Table = OutboundRules.FirstTable, Managed = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ApplyAsync(outbound, CancellationToken.None));
        Assert.False(host.State(outbound).Carries);
        Assert.NotNull(host.CheckReservation(outbound with { Name = "other", Id = 2, Mark = outbound.Mark + 1 }));
        await host.FirewallAsync([], CancellationToken.None);
        Assert.Contains("iifname { \"external\" } drop", ledger.Ruleset, StringComparison.Ordinal);
        driver.CancelApply = false;

        Assert.True(await host.MendAsync(outbound, CancellationToken.None));
        Assert.True(host.State(outbound).Carries);
    }

    [Fact]
    public async Task ABackgroundPolicyFailureSignalsRouteResettlementWithoutADatabaseChange()
    {
        using var bench = new Bench();
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver()]);
        var outbound = Assert.Single(await bench.Outbounds.ListAsync(CancellationToken.None));
        await host.ApplyAsync(outbound, CancellationToken.None);
        using var changes = new HostChanges();
        using var watch = new ProbeWatch(bench.Scopes, null!, new ProbeLive(), host, new BalanceLive(), null!,
            bench.Services.GetRequiredService<DatabaseVersion>(), changes, bench.Clock, NullLogger<ProbeWatch>.Instance);
        await Keep(watch, bench.Services, true);
        ledger.Rules.Clear();
        ledger.Refuses = "rule";

        var failed = await Keep(watch, bench.Services, true);

        Assert.True(failed.Moved);
        Assert.False(host.State(outbound).Carries);
        ledger.Refuses = "";
        bench.Clock.Pass(TimeSpan.FromMinutes(2));
        var recovered = await Keep(watch, bench.Services, false);
        Assert.True(recovered.Moved);
        Assert.True(host.State(outbound).Carries);
    }

    private static OutboundHost Host(Ledger ledger, Kernel kernel) =>
        new(ledger, [new LocalOutboundDriver(), new AwgOutboundDriver(ledger, kernel)]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirewallFailureReportsSavedStateAndEachTableRecoversWithoutAnotherDatabaseChange(bool partialRecovery)
    {
        using var bench = new Bench();
        var ct = CancellationToken.None;
        var original = Assert.Single(await bench.Outbounds.ListAsync(ct));
        var saved = await bench.Outbounds.ChangeAsync(original.Id, original with { Probe = "" }, ct);
        var outbound = Assert.IsType<LocalOutboundConfig>(saved.Record);
        var ledger = new Ledger();
        var probes = new ProbeLive();
        var runner = new ProbeRunner(new ProbeLink(), probes, bench.Clock, NullLogger<ProbeRunner>.Instance);
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver()], probes);
        using var changes = new HostChanges();
        changes.Listening();
        using var plans = new RoutePlans(bench.Clock);
        var live = new BalanceLive();
        var sets = new DnsSets(ledger);
        var state = new DnsState();
        var book = new StandInBook(bench.Clock);
        using var dns = new DnsHost(bench.Scopes, plans, sets, state, new AccessNames(), book,
            new DnsHolders(), new DnsStandIns(), bench.Clock, NullLogger<DnsHost>.Instance);
        var routes = new RouteApplier(bench.Rules, bench.Outbounds, bench.Balancers, bench.Configs, bench.Geo,
            bench.Resolver, state, bench.GeoFiles, plans, live, sets, bench.Clients, host, new AccessGate(),
            new NameGate(), bench.Templates, book, ledger, NullLogger<RouteApplier>.Instance);
        var version = bench.Services.GetRequiredService<DatabaseVersion>();
        var endpoint = new EndpointHost(ledger, new Kernel());
        using var keeper = new EndpointKeeper(ledger, endpoint, null!);
        var configWatch = new ConfigWatch(keeper, endpoint, dns, version, bench.Clock, NullLogger<ConfigWatch>.Instance);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<ITokenIssuer>(bench.Issuer);
        builder.Services.AddSingleton<TimeProvider>(bench.Clock);
        builder.Services.AddSingleton(bench.Outbounds).AddSingleton(bench.Balancers).AddSingleton(bench.Rules)
            .AddSingleton(bench.Configs).AddSingleton(host).AddSingleton(routes).AddSingleton(dns).AddSingleton(runner);
        builder.Services.AddSingleton(new OutboundAnswers([new LocalOutboundResponses()]));
        builder.Services.ConfigureHttpJsonOptions(options => OutboundJson.Configure(options.SerializerOptions));
        await using var app = builder.Build();
        app.UseBearer();
        app.MapOutbounds();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var principal = new Principal(1, "operator", AuthScheme.Bearer, 1, new HashSet<string>
            { Scopes.ReadState, Scopes.ManageRouting });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            bench.Issuer.Issue(principal, bench.Clock.Now));
        using var watch = new ProbeWatch(app.Services.GetRequiredService<IServiceScopeFactory>(), runner, probes,
            host, live, configWatch, version, changes, bench.Clock, NullLogger<ProbeWatch>.Instance);
        await watch.LookAsync(ct);
        Assert.True(host.State(outbound).Carries);

        var originalRevision = version.Read();
        ledger.Refuses = "firewall";
        var path = $"/api/outbounds/{outbound.Id}";
        using var response = partialRecovery
            ? await http.PutAsJsonAsync(path, new
            {
                outbound.Name, outbound.Kind, outbound.IsEnabled, outbound.Probe, ClosePrivate = !outbound.ClosePrivate,
            }, ct)
            : await http.PostAsync(path + "/apply", null, ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var failure = Assert.IsType<OutboundApplyFailure>(await response.Content.ReadFromJsonAsync<OutboundApplyFailure>(ct));
        Assert.Equal("host-refused", failure.Error);
        Assert.Equal(partialRecovery ? outbound.Id : (long?)null, failure.SavedId);
        var revision = version.Read();
        if (partialRecovery)
        {
            var read = await http.GetFromJsonAsync<JsonElement>(path, ct);
            Assert.Equal(!outbound.ClosePrivate, read.GetProperty("closePrivate").GetBoolean());
            outbound = Assert.IsType<LocalOutboundConfig>(await bench.Outbounds.FindAsync(outbound.Id, ct));
        }
        else
        {
            Assert.Equal(originalRevision, revision);
        }

        Assert.False(host.State(outbound).Carries);
        Assert.Contains("firewall", host.State(outbound).Fault, StringComparison.Ordinal);
        Assert.False(plans.Held!.Ways.IsAlive(outbound.Name));
        Assert.Equal(revision, version.Read());

        await watch.LookAsync(ct);
        Assert.True(host.FirewallPending);
        ledger.Refuses = "";
        await watch.LookAsync(ct);
        Assert.True(host.FirewallPending);

        var appliedTables = ledger.AppliedRouteTables;
        ledger.RefusedFirewallTable = partialRecovery ? RouteRuleset.TableName : "";
        bench.Clock.Pass(ConfigWatch.Retry);
        await watch.LookAsync(ct);

        Assert.False(host.FirewallPending);
        Assert.True(host.State(outbound).Carries);
        if (partialRecovery)
        {
            Assert.Equal(appliedTables, ledger.AppliedRouteTables);
            ledger.RefusedFirewallTable = "";
            await watch.LookAsync(ct);
            Assert.Equal(appliedTables, ledger.AppliedRouteTables);
            bench.Clock.Pass(ConfigWatch.Retry);
            await watch.LookAsync(ct);
        }

        Assert.Equal(appliedTables + 1, ledger.AppliedRouteTables);
        bench.Clock.Pass(ConfigWatch.Retry);
        await watch.LookAsync(ct);
        Assert.Equal(appliedTables + 1, ledger.AppliedRouteTables);
        Assert.True(plans.Held!.Ways.IsAlive(outbound.Name));
        Assert.Equal(revision, version.Read());
    }

    private static AwgOutboundConfig Tunnel(string name) => AwgOutboundDefaults.Fresh(name) with
    {
        Host = "127.0.0.1", PeerKey = "eOWG0GXBLrpZOZ+FCwCJTvZ5rBEKHZ8NPvxNM3nJPl4=",
        Address = ["10.80.1.2/32"], Mark = OutboundRules.FirstMark, Table = OutboundRules.FirstTable, Probe = "",
    };

    private static Task<IResult> Invoke(string method, params object?[] args) =>
        (Task<IResult>)typeof(OutboundEndpoints).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;

    private static Task<(IReadOnlyList<OutboundConfig> Outbounds, bool Moved)> Keep(
        ProbeWatch watch, IServiceProvider services, bool changed) =>
        (Task<(IReadOnlyList<OutboundConfig>, bool)>)typeof(ProbeWatch)
            .GetMethod("KeepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(watch, [services, changed, CancellationToken.None])!;

    private sealed record ExternalOutbound : OutboundConfig
    {
        public required string Link { get; init; }
        public bool Managed { get; init; }
        public override string InterfaceName => Link;
        public override bool OwnsInterface => Managed;
        public override OutboundFault? CheckSettings() => null;
    }

    private sealed class ExternalDriver(Ledger ledger) : IOutboundDriver
    {
        public bool CancelApply { get; set; }
        public bool Supports(string kind) => kind == "";
        public OutboundConfig Draft(string name, string kind) => new ExternalOutbound { Name = name, Link = "external" };
        public Task ApplyAsync(OutboundConfig outbound, CancellationToken ct)
        {
            ledger.Links.Add(outbound.InterfaceName);
            return CancelApply ? throw new OperationCanceledException() : Task.CompletedTask;
        }
        public Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct)
        {
            if (outbound.OwnsInterface)
            {
                ledger.Links.Remove(outbound.InterfaceName);
            }
            return Task.CompletedTask;
        }
        public Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct) => Task.FromResult(false);
        public OutboundState State(OutboundConfig outbound) =>
            new(outbound.Name, ledger.HasLink(outbound.InterfaceName), null, null, ledger.IsUp(outbound.InterfaceName), "");
    }
}
