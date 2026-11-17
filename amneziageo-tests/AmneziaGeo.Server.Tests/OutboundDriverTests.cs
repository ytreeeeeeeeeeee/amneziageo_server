using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Probe;

namespace AmneziaGeo.Server.Tests;

public class OutboundDriverTests
{
    [Theory]
    [InlineData("local", false)]
    [InlineData("wg", true)]
    [InlineData("ws", true)]
    public void EveryExistingKindHasOneDriver(string kind, bool awg)
    {
        IOutboundDriver[] drivers = [new LocalOutboundDriver(), new AwgOutboundDriver(new Ledger(), new Kernel())];

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
        probes.Keep(outbound.Name, outcome, DateTimeOffset.UtcNow);
        probes.Keep(outbound.Name, outcome, DateTimeOffset.UtcNow);
        using var host = new OutboundHost(ledger, [new LocalOutboundDriver(), driver], probes);

        var state = await host.ApplyAsync(outbound, CancellationToken.None);
        var mended = await host.MendAsync(outbound, CancellationToken.None);
        await host.WithdrawAsync(outbound, CancellationToken.None);

        Assert.Equal(carries, state.Carries);
        Assert.Same(probes.Find(outbound.Name), state.Probe);
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

    private sealed record TestOutboundConfig : OutboundConfig
    {
        public override OutboundFault? CheckSettings() => null;
    }

    private sealed class Driver(Ledger ledger) : IOutboundDriver
    {
        public bool Refuses { get; init; }

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

            return Task.CompletedTask;
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
