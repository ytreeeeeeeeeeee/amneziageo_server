using System.Net;
using System.Text;
using AmneziaGeo.Server.Api.Outbounds;
using AmneziaGeo.Server.Routing.Outbound;
using AmneziaGeo.Server.Routing.Probe;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Server.Tests;

public class ProbeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheQuestionCarriesTheNameItAsksFor()
    {
        var packet = ProbeQuestion.Packet("example.com", 0x1234);

        Assert.Equal(29, packet.Length);
        Assert.Equal(0x12, packet[0]);
        Assert.Equal(0x34, packet[1]);
        Assert.Equal(7, packet[12]);
        Assert.Equal("example", Encoding.ASCII.GetString(packet, 13, 7));
        Assert.Equal(3, packet[20]);
        Assert.Equal("com", Encoding.ASCII.GetString(packet, 21, 3));
        Assert.Equal(0, packet[24]);
        Assert.Equal(1, packet[26]);
        Assert.Equal(1, packet[28]);
    }

    [Fact]
    public void AnAnswerUnderTheNumberOfTheQuestionIsTakenAsOne()
    {
        Assert.True(ProbeQuestion.Answers(Answer(0x1234), 0x1234));
    }

    [Fact]
    public void AnAnswerUnderAnotherNumberIsNotTakenAsOne()
    {
        Assert.False(ProbeQuestion.Answers(Answer(0x1234), 0x4321));
    }

    [Fact]
    public void AQuestionComingBackIsNotTakenForAnAnswer()
    {
        Assert.False(ProbeQuestion.Answers(ProbeQuestion.Packet("example.com", 0x1234), 0x1234));
    }

    [Fact]
    public void OneProbeThatMissesLeavesTheOutboundCarryingTraffic()
    {
        var live = new ProbeLive();
        live.Keep(Tunnel(), ProbeOutcome.Reached, Noon);

        Assert.False(live.Keep(Tunnel(), ProbeOutcome.Missed, Noon));
        Assert.True(live.Find(Tunnel())?.IsReached);
    }

    [Fact]
    public void TwoProbesThatMissInARowTakeTheOutboundOff()
    {
        var live = new ProbeLive();
        live.Keep(Tunnel(), ProbeOutcome.Reached, Noon);
        live.Keep(Tunnel(), ProbeOutcome.Missed, Noon);

        Assert.True(live.Keep(Tunnel(), ProbeOutcome.Missed, Noon));
        Assert.False(live.Find(Tunnel())?.IsReached);
        Assert.Equal(2, live.Find(Tunnel())?.Falls);
    }

    [Fact]
    public void AProbeThatAnswersBringsTheOutboundBack()
    {
        var live = new ProbeLive();
        live.Keep(Tunnel(), ProbeOutcome.Missed, Noon);
        live.Keep(Tunnel(), ProbeOutcome.Missed, Noon);

        Assert.True(live.Keep(Tunnel(), ProbeOutcome.Reached, Noon));
        Assert.Equal(0, live.Find(Tunnel())?.Falls);
    }

    [Fact]
    public void AProbeThatNeverWentOutLeavesTheVerdictAsItWas()
    {
        var live = new ProbeLive();
        live.Keep(Tunnel(), ProbeOutcome.Reached, Noon);

        Assert.False(live.Keep(Tunnel(), ProbeOutcome.Skipped, Noon));
        Assert.True(live.Find(Tunnel())?.IsReached);
    }

    [Fact]
    public void TheVerdictsOfTheOutboundsThatAreGoneAreDropped()
    {
        var live = new ProbeLive();
        live.Keep(Tunnel(), ProbeOutcome.Reached, Noon);
        live.Keep(Tunnel() with { Id = 2, Name = "awgoff" }, ProbeOutcome.Reached, Noon);

        live.Hold([1]);

        Assert.NotNull(live.Find(1));
        Assert.Null(live.Find(2));
    }

    [Fact]
    public void AnOutboundTheProbeDoesNotReachCarriesNothing()
    {
        var state = new OutboundState("awgbor", true, 0, 0, true, "", new ProbeReading(false, 2, Noon));

        Assert.True(state.IsAlive);
        Assert.False(state.Carries);
    }

    [Fact]
    public void AnOutboundThatWasNeverProbedCarriesWhatTheHandshakeSays()
    {
        var state = new OutboundState("awgbor", true, 0, 0, true, "");

        Assert.True(state.Carries);
    }

    [Fact]
    public void AProbeToSomethingThatIsNotANameServerIsRefused()
    {
        var fault = OutboundRules.CheckProbe(Tunnel() with { Probe = "not a server" });

        Assert.Equal("bad-probe", fault?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4000)]
    public void AProbeUnderAPeriodTheServerDoesNotTakeIsRefused(int every)
    {
        var fault = OutboundRules.CheckProbe(Tunnel() with { ProbeEvery = every });

        Assert.Equal("bad-probe", fault?.Code);
    }

    [Fact]
    public void AnOutboundWithoutAProbeHolds()
    {
        Assert.Null(OutboundRules.CheckProbe(Tunnel() with { Probe = "", ProbeEvery = 0 }));
    }

    [Fact]
    public void DisablingAProbeDoesNotKeepItsFailedVerdictOnTheHost()
    {
        var live = new ProbeLive();
        var outbound = LocalOutboundDefaults.Direct();
        live.Keep(outbound, ProbeOutcome.Missed, Noon);
        live.Keep(outbound, ProbeOutcome.Missed, Noon);
        using var host = new AmneziaGeo.Server.Routing.Host.OutboundHost(
            new Ledger(), [new LocalOutboundDriver()], live);

        Assert.False(host.State(outbound).Carries);
        var state = host.State(outbound with { Probe = "" });

        Assert.True(state.Carries);
        Assert.Null(state.Probe);
    }

    [Fact]
    public async Task AChangedProbeStartsAFreshFailureCountAndIsDueImmediately()
    {
        var live = new ProbeLive();
        var outbound = Tunnel();
        live.Keep(outbound, ProbeOutcome.Missed, Noon);
        live.Keep(outbound, ProbeOutcome.Missed, Noon);
        var changed = outbound with { Probe = "9.9.9.9" };
        var runner = Runner(new Link(ProbeOutcome.Missed), live, new Clock(Noon));

        Assert.True(runner.IsDue(changed));
        await runner.RunAsync(changed, CancellationToken.None);

        Assert.True(live.Find(changed)!.IsReached);
        Assert.Equal(1, live.Find(changed)!.Falls);
        Assert.Null(live.Find(outbound));
    }

    [Fact]
    public void AProbeOfAnOldRevisionCannotBlockItsReplacementEvenWhenItFinishesLate()
    {
        var live = new ProbeLive();
        var old = Tunnel() with { Id = 2, UpdatedUtc = Noon };
        var changed = old with { Name = "renamed", UpdatedUtc = Noon.AddSeconds(1) };
        live.Keep(changed, ProbeOutcome.Missed, Noon);
        live.Keep(changed, ProbeOutcome.Missed, Noon);

        live.Keep(old, ProbeOutcome.Missed, Noon.AddSeconds(2));
        live.Keep(old, ProbeOutcome.Missed, Noon.AddSeconds(2));

        Assert.False(live.Find(changed)!.IsReached);
        Assert.Equal(2, live.Find(changed)!.Falls);
        Assert.Null(live.Find(old));
        Assert.Null(live.Find(old with { Kind = "ws" }));
    }

    [Fact]
    public void ConcurrentMissesOfTheSameRevisionAreNotLost()
    {
        var live = new ProbeLive();
        var outbound = Tunnel();

        Parallel.For(0, 20, _ => live.Keep(outbound, ProbeOutcome.Missed, Noon));

        Assert.Equal(20, live.Find(outbound)!.Falls);
    }

    [Fact]
    public void ReusingANameDoesNotReuseOrRestoreTheRemovedOutboundsVerdict()
    {
        var live = new ProbeLive();
        var removed = Tunnel() with { Id = 2, UpdatedUtc = Noon.AddHours(1) };
        var replacement = removed with { Id = 3, UpdatedUtc = Noon };
        live.Keep(removed, ProbeOutcome.Missed, Noon);
        live.Keep(replacement, ProbeOutcome.Missed, Noon);
        live.Keep(replacement, ProbeOutcome.Missed, Noon);

        live.Keep(removed, ProbeOutcome.Reached, Noon.AddHours(2));

        Assert.False(live.Find(replacement)!.IsReached);
        Assert.Equal(2, live.Find(replacement)!.Falls);
    }

    [Fact]
    public async Task ReassigningANameToAnExistingOutboundWithALowerIdDoesNotRejectItsProbeResults()
    {
        using var bench = new Bench();
        var ct = CancellationToken.None;
        var first = await bench.Outbounds.AddAsync(
            LocalOutboundDefaults.Direct() with { Name = "first" }, ct);
        var second = await bench.Outbounds.AddAsync(
            LocalOutboundDefaults.Direct() with { Name = "reused" }, ct);
        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
        var lower = first.Record!;
        var higher = second.Record!;
        Assert.True(lower.Id < higher.Id);
        var live = new ProbeLive();
        live.Keep(higher, ProbeOutcome.Reached, Noon);

        var renamed = await bench.Outbounds.ChangeAsync(higher.Id, higher with { Name = "other" }, ct);
        var reassigned = await bench.Outbounds.ChangeAsync(lower.Id, lower with { Name = higher.Name }, ct);
        Assert.True(renamed.IsOk);
        Assert.True(reassigned.IsOk);
        var current = reassigned.Record!;
        live.Hold((await bench.Outbounds.ListAsync(ct)).Select(one => one.Id));
        var runner = Runner(new Link(ProbeOutcome.Missed), live, new Clock(Noon));

        Assert.True(runner.IsDue(current));
        Assert.False(await runner.RunAsync(current, ct));
        Assert.True(await runner.RunAsync(current, ct));
        live.Keep(higher, ProbeOutcome.Reached, Noon.AddSeconds(1));

        Assert.False(live.Find(current)!.IsReached);
        Assert.Equal(2, live.Find(current)!.Falls);
        Assert.False(runner.IsDue(current));
        using var host = new AmneziaGeo.Server.Routing.Host.OutboundHost(
            new Ledger(), [new LocalOutboundDriver()], live);
        Assert.False((await host.ApplyAsync(current, ct)).Carries);
    }

    [Fact]
    public async Task TheRunnerKeepsWhatTheProbeCameTo()
    {
        var live = new ProbeLive();
        var runner = Runner(new Link(ProbeOutcome.Missed), live, new Clock(Noon));

        Assert.False(await runner.RunAsync(Tunnel(), CancellationToken.None));
        Assert.True(await runner.RunAsync(Tunnel(), CancellationToken.None));
        Assert.False(live.Find(Tunnel())?.IsReached);
    }

    [Fact]
    public async Task AnOutboundIsDueAProbeOnlyAfterItsPeriodPassed()
    {
        var live = new ProbeLive();
        var clock = new Clock(Noon);
        var runner = Runner(new Link(ProbeOutcome.Reached), live, clock);

        Assert.True(runner.IsDue(Tunnel()));
        await runner.RunAsync(Tunnel(), CancellationToken.None);
        Assert.False(runner.IsDue(Tunnel()));

        clock.Pass(TimeSpan.FromSeconds(31));

        Assert.True(runner.IsDue(Tunnel()));
    }

    [Fact]
    public void AnOutboundThatIsOffIsNeverDueAProbe()
    {
        var runner = Runner(new Link(ProbeOutcome.Reached), new ProbeLive(), new Clock(Noon));

        Assert.False(runner.IsDue(Tunnel() with { IsEnabled = false }));
        Assert.False(runner.IsDue(Tunnel() with { Probe = "" }));
    }

    private static ProbeRunner Runner(IProbeLink link, ProbeLive live, TimeProvider time) =>
        new(link, live, time, NullLogger<ProbeRunner>.Instance);

    private static OutboundConfig Tunnel() => new AwgOutboundConfig()
    {
        Id = 1,
        Name = "awgbor",
        Kind = OutboundKind.Wg,
        Mark = 0xA602,
        Table = 42602,
        Probe = ProbeDefaults.Address,
        ProbeEvery = ProbeDefaults.Every,
    };

    private static byte[] Answer(ushort id)
    {
        var packet = ProbeQuestion.Packet("example.com", id);
        packet[2] |= 0x80;

        return packet;
    }

    private sealed class Link : IProbeLink
    {
        private readonly ProbeOutcome _outcome;

        public Link(ProbeOutcome outcome)
        {
            _outcome = outcome;
        }

        public Task<ProbeOutcome> ReachAsync(
            IPEndPoint server, uint mark, string name, TimeSpan wait, CancellationToken ct) =>
            Task.FromResult(_outcome);
    }
}
