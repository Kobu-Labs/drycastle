using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The render clock of remote entities (A-06, I-rubber-banding): a filtered server-tick estimate that never runs backwards.</summary>
public class RenderClockTests
{
    const double Delay = 3.0;

    /// <summary>
    /// Snapshots of ticks 0..<paramref name="ticks"/> sent every 1/30 s with a one-way latency of <paramref name="latency"/> and an extra
    /// arrival delay per tick (<paramref name="extra"/>), drawn every 1/60 s. Returns the drawn tick of each frame and the clock.
    /// </summary>
    static (List<double> Drawn, List<double> Times, RenderClock Clock) Run(int ticks, double latency, Func<int, double> extra)
    {
        var arrivals = new List<(double T, uint Tick)>();
        for (var k = 0; k <= ticks; k++) arrivals.Add((k / 30.0 + latency + extra(k), (uint)k));
        arrivals.Sort((a, b) => a.T.CompareTo(b.T));
        var clock = new RenderClock();
        var drawn = new List<double>();
        var times = new List<double>();
        var next = 0;
        var end = ticks / 30.0 + latency;
        for (var t = 0.0; t <= end; t += 1 / 60.0)
        {
            while (next < arrivals.Count && arrivals[next].T <= t) { clock.OnSnapshot(arrivals[next].Tick, arrivals[next].T); next++; }
            if (next == 0) continue; // nothing arrived yet: the entity is not drawn
            drawn.Add(clock.RenderTick(t, Delay));
            times.Add(t);
        }
        return (drawn, times, clock);
    }

    [Fact]
    public void SteadyJitteredArrivalsDrawAtTheTickRateAndNeverBackwards()
    {
        var rng = new Random(7);
        var (drawn, times, clock) = Run(600, 0.05, _ => (rng.NextDouble() - 0.5) * 0.016); // +-8 ms of arrival jitter
        for (var i = 1; i < drawn.Count; i++) Assert.True(drawn[i] >= drawn[i - 1] - 1e-9, $"drawn tick moved back at frame {i}");
        // over the steady part the drawn tick advances at the tick rate
        var span = times[^1] - times[60];
        Assert.InRange((drawn[^1] - drawn[60]) / span, Wire.TickRate * 0.98, Wire.TickRate * 1.02);
        // and it stays at the delay behind the server's present (the latency is in the offset, so the drawn tick is the send time minus the delay)
        Assert.InRange(drawn[^1] - ((times[^1] - 0.05) * Wire.TickRate - Delay), -1.0, 1.0);
        Assert.Equal(0, clock.HoldEvents);
    }

    [Fact]
    public void ALatePacketHoldsTheDrawnTickInsteadOfSnappingBack()
    {
        // tick 200 arrives 200 ms late: the old clock, re-anchored to each arrival, drew the entity 2-3 ticks back when it came
        var (drawn, _, clock) = Run(400, 0.05, k => k == 200 ? 0.2 : 0.0);
        for (var i = 1; i < drawn.Count; i++) Assert.True(drawn[i] >= drawn[i - 1] - 1e-9, $"drawn tick moved back at frame {i}: {drawn[i - 1]:F2} -> {drawn[i]:F2}");
        Assert.True(clock.HoldEvents >= 1);
        Assert.InRange(clock.MaxHold, 0.0, 3.0);
    }

    [Fact]
    public void TheDrawnTickFollowsTheEstimateAgainAfterALateArrival()
    {
        var (drawn, times, _) = Run(600, 0.05, k => k == 200 ? 0.2 : 0.0);
        // a few seconds after the late packet the drawn tick is back on the schedule (within half a tick of the send time minus the delay)
        var i = times.FindIndex(t => t >= 0.05 + 200 / 30.0 + 3.0);
        Assert.InRange(drawn[i] - ((times[i] - 0.05) * Wire.TickRate - Delay), -0.5, 0.5);
    }

    [Fact]
    public void NoRenderBeforeTheFirstSnapshotIsUsable()
    {
        var clock = new RenderClock();
        clock.OnSnapshot(100, 2.0);
        // first sample sets the offset outright: at its arrival the drawn tick is the tick minus the delay
        Assert.Equal(100 - Delay, clock.RenderTick(2.0, Delay), 6);
    }
}
