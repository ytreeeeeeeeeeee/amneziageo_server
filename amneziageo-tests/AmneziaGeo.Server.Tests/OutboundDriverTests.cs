using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Tests;

public class OutboundDriverTests
{
    [Theory]
    [InlineData("local", false)]
    [InlineData("interface", false)]
    [InlineData("wg", true)]
    [InlineData("ws", true)]
    public void EveryExistingKindHasOneDriver(string kind, bool awg)
    {
        IOutboundDriver[] drivers = [new LocalOutboundDriver(), new InterfaceOutboundDriver(new Ledger()),
            new AwgOutboundDriver(new Ledger(), new Kernel())];

        var driver = Assert.Single(drivers, one => one.Supports(kind));

        Assert.Equal(awg, driver is AwgOutboundDriver);
    }

    [Theory]
    [InlineData(ProbeOutcome.Reached, true)]
    [InlineData(ProbeOutcome.Missed, false)]
    public async Task AnotherDriverUsesTheSamePolicyRulesAndProbeVerdicts(ProbeOutcome outcome, bool carries)
    {
        var ledger = new Ledger();
        var driver = new Driver(ledger);
        var probes = new ProbeLive();
        var outbound = LocalOutboundDefaults.Direct() with { Kind = "test" };
        probes.Keep(outbound, outcome, DateTimeOffset.UtcNow);
        probes.Keep(outbound, outcome, DateTimeOffset.UtcNow);
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver(), driver], probes);

        var state = await host.ApplyAsync(outbound, CancellationToken.None);
        var mended = await host.MendAsync(outbound, CancellationToken.None);
        await host.WithdrawAsync(outbound, CancellationToken.None);

        Assert.Equal(carries, state.Carries);
        Assert.Same(probes.Find(outbound), state.Probe);
        Assert.Equal(42ul, state.RxBytes);
        Assert.True(mended);
        Assert.Equal(
            [
                "seal", "driver apply", "rule 42497 254 10000 True", "driver state", "driver mend",
                "seal", "rule 42497 254 10000 False", "driver withdraw"
            ],
            ledger.Steps);
    }

    [Fact]
    public async Task ATurnedOffOutboundWithdrawsItsDriverWithoutApplyingOrMendingIt()
    {
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new Driver(ledger)]);
        var outbound = LocalOutboundDefaults.Direct() with { Kind = "test", IsEnabled = false };

        var state = await host.ApplyAsync(outbound, CancellationToken.None);
        var mended = await host.MendAsync(outbound, CancellationToken.None);

        Assert.False(state.Carries);
        Assert.False(mended);
        Assert.Equal(["seal", "rule 42497 254 10000 False", "driver withdraw"], ledger.Steps);
    }

    [Fact]
    public async Task ADriverFailureLeavesTheReasonWithoutInstallingThePolicyRule()
    {
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new Driver(ledger) { Refuses = true }]);

        var state = await host.ApplyAsync(LocalOutboundDefaults.Direct() with { Kind = "test" },
            CancellationToken.None);

        Assert.False(state.Carries);
        Assert.Equal("the driver refused", state.Fault);
        Assert.Equal(["seal", "driver apply"], ledger.Steps);
        Assert.Empty(ledger.Rules);
    }

    [Fact]
    public async Task AnUnregisteredKindIsRefusedWithoutInstallingThePolicyRule()
    {
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver()]);
        var outbound = LocalOutboundDefaults.Direct() with { Kind = "unknown" };

        var applied = await host.ApplyAsync(outbound, CancellationToken.None);
        var state = host.State(outbound);

        Assert.False(applied.Carries);
        Assert.Equal("no driver carries the outbound kind 'unknown'", applied.Fault);
        Assert.Equal(applied.Fault, state.Fault);
        Assert.Equal(["seal"], ledger.Steps);
        Assert.Empty(ledger.Rules);
    }

    [Theory]
    [InlineData(OutboundKind.Wg)]
    [InlineData(OutboundKind.Ws)]
    public async Task AnAwgKindWithoutAwgSettingsIsRefusedBeforeChangingInterfaces(string kind)
    {
        var ledger = new Ledger();
        var outbound = LocalOutboundDefaults.Direct() with
        {
            Kind = kind,
            Table = OutboundRules.MainTable,
        };
        using var host = new OutboundHost(ledger, [new AwgOutboundDriver(ledger, new Kernel())]);

        var state = await host.ApplyAsync(outbound, CancellationToken.None);

        Assert.Equal("bad-kind", OutboundRules.Check(outbound)?.Code);
        Assert.False(state.Carries);
        Assert.Equal("the outbound 'direct' carries no AmneziaWG settings", state.Fault);
        Assert.Equal(["seal"], ledger.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedKindChangeKeepsTheOldResourcesForApplyOrMendToRetry(bool mend)
    {
        var ledger = new Ledger();
        var driver = new Driver(ledger);
        using var host = new OutboundHost(ledger, [driver, new LocalOutboundDriver()]);
        var old = LocalOutboundDefaults.Direct() with { Kind = "test" };
        var local = LocalOutboundDefaults.Direct();
        await host.ApplyAsync(old, CancellationToken.None);
        driver.RefusesWithdrawal = true;
        ledger.Steps.Clear();

        var refused = await host.ApplyAsync(local, CancellationToken.None, old);

        Assert.False(refused.Carries);
        Assert.Equal("the driver refused withdrawal", refused.Fault);
        Assert.False(host.State(local).Carries);
        Assert.False(await host.KeepRuleAsync(local, CancellationToken.None));
        Assert.DoesNotContain(ledger.Steps, step => step.EndsWith(" True", StringComparison.Ordinal));
        await Assert.ThrowsAsync<HostNetworkException>(() => host.MendAsync(local, CancellationToken.None));
        driver.RefusesWithdrawal = false;

        if (mend)
        {
            Assert.True(await host.MendAsync(local, CancellationToken.None));
        }
        else
        {
            Assert.True((await host.ApplyAsync(local, CancellationToken.None)).Carries);
        }

        Assert.True(host.State(local).Carries);
        Assert.Equal(3, driver.Withdrawals.Count);
        Assert.All(driver.Withdrawals, name => Assert.Equal(old.Name, name));
        Assert.Contains(local.Mark, ledger.Rules);
    }

    [Fact]
    public async Task AFailedPolicyRemovalDoesNotLoseTheOriginalOutboundDuringAnotherEdit()
    {
        var ledger = new Ledger();
        var driver = new Driver(ledger);
        using var host = new OutboundHost(ledger, [driver, new LocalOutboundDriver()]);
        var old = LocalOutboundDefaults.Direct() with { Name = "old", Kind = "test" };
        var local = LocalOutboundDefaults.Direct();
        await host.ApplyAsync(old, CancellationToken.None);
        ledger.Refuses = "rule";

        Assert.False((await host.ApplyAsync(local, CancellationToken.None, old)).Carries);
        ledger.Refuses = "";
        var renamed = local with { Name = "new" };

        Assert.True((await host.ApplyAsync(renamed, CancellationToken.None, local)).Carries);
        Assert.Equal(["old"], driver.Withdrawals);
        Assert.True(host.State(renamed).Carries);
    }

    [Fact]
    public async Task AFailedReplacementApplyStaysUnavailableUntilTheWholeTransitionSucceeds()
    {
        var ledger = new Ledger();
        var driver = new Driver(ledger) { Refuses = true };
        using var host = new OutboundHost(ledger, [driver, new LocalOutboundDriver()]);
        var old = LocalOutboundDefaults.Direct();
        await host.ApplyAsync(old, CancellationToken.None);
        var changed = old with { Kind = "test" };

        Assert.False((await host.ApplyAsync(changed, CancellationToken.None, old)).Carries);
        Assert.False(host.State(changed).Carries);
        driver.Refuses = false;

        Assert.True(await host.MendAsync(changed, CancellationToken.None));
        Assert.True(host.State(changed).Carries);
    }

    [Fact]
    public async Task AnotherEditCleansThePartiallyAppliedReplacementInsteadOfOnlyTheOriginal()
    {
        var ledger = new Ledger();
        var driver = new Driver(ledger);
        using var host = new OutboundHost(ledger, [driver, new LocalOutboundDriver()]);
        var old = LocalOutboundDefaults.Direct();
        var attempted = old with { Name = "partial", Kind = "test" };
        ledger.Refuses = $"rule {attempted.Mark} {attempted.Table} {OutboundRules.PriorityOf(attempted.Mark)} True";

        Assert.False((await host.ApplyAsync(attempted, CancellationToken.None, old)).Carries);
        ledger.Refuses = "";

        Assert.True((await host.ApplyAsync(old, CancellationToken.None, attempted)).Carries);
        Assert.Equal(["partial"], driver.Withdrawals);
    }

    [Fact]
    public async Task AFailedWithdrawalKeepsTheOldInterfacesFirewallProtection()
    {
        var ledger = new Ledger();
        using var host = new OutboundHost(ledger, [new AwgOutboundDriver(ledger, new Kernel()), new LocalOutboundDriver()]);
        var old = new AwgOutboundConfig { Name = "oldawg", Mark = OutboundRules.FirstMark, Table = OutboundRules.FirstTable };
        ledger.Links.Add(old.Name);
        ledger.Refuses = "clear";
        var local = LocalOutboundDefaults.Direct();

        Assert.False((await host.ApplyAsync(local, CancellationToken.None, old)).Carries);
        await host.FirewallAsync([local], CancellationToken.None);

        Assert.Contains("iifname { \"oldawg\" } drop", ledger.Ruleset, StringComparison.Ordinal);
        Assert.Contains("oifname \"oldawg\" ct state new ip daddr", ledger.Ruleset, StringComparison.Ordinal);
        ledger.Refuses = "";
        await host.MendAsync(local, CancellationToken.None);
        await host.FirewallAsync([local], CancellationToken.None);
        Assert.DoesNotContain("oldawg", ledger.Ruleset, StringComparison.Ordinal);
    }

    private sealed record TestOutboundConfig : OutboundConfig
    {
        public override OutboundFault? CheckSettings() => null;
    }

    private sealed class Driver(Ledger ledger) : IOutboundDriver
    {
        public bool Refuses { get; set; }

        public bool RefusesWithdrawal { get; set; }

        public List<string> Withdrawals { get; } = [];

        public OutboundConfig Draft(string name, string kind) => new TestOutboundConfig { Name = name, Kind = kind };

        public bool Supports(string kind) => kind == "test";

        public Task ApplyAsync(OutboundConfig outbound, CancellationToken ct)
        {
            ledger.Steps.Add("driver apply");

            return Refuses ? throw new HostNetworkException("the driver refused") : Task.CompletedTask;
        }

        public Task WithdrawAsync(OutboundConfig outbound, CancellationToken ct)
        {
            ledger.Steps.Add("driver withdraw");
            Withdrawals.Add(outbound.Name);

            return RefusesWithdrawal
                ? throw new HostNetworkException("the driver refused withdrawal")
                : Task.CompletedTask;
        }

        public Task<bool> MendAsync(OutboundConfig outbound, CancellationToken ct)
        {
            ledger.Steps.Add("driver mend");

            return Task.FromResult(true);
        }

        public OutboundState State(OutboundConfig outbound)
        {
            ledger.Steps.Add("driver state");

            return new OutboundState(outbound.Name, true, 42, 24, true, string.Empty);
        }
    }
}
