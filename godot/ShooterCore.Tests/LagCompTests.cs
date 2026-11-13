using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Lag compensation for players (ADR 0013): the view tick the shooter's screen showed, the cap, the respawn and teleport epoch, and the pose ring.</summary>
public class LagCompTests
{
    const int Rate = 30;
    const int Interp = 3;

    static HitTarget At(ushort id, float x) => new(id, new Vector3(x, 0, 0), Stance.Stand, 0);

    [Fact]
    public void CapIsSevenTicksAt30Hz()
    {
        Assert.Equal(7, LagComp.CapTicks(Rate)); // 250 ms rounds down to 7 ticks (233 ms)
    }

    [Theory]
    [InlineData(0f, 3)]      // no round trip yet (a bot): the interpolation delay alone
    [InlineData(27f, 3)]     // RTT/2 = 13.5 ms, 0.4 ticks, rounds to 0
    [InlineData(72f, 4)]     // 36 ms, 1.08 ticks
    [InlineData(130f, 5)]    // 65 ms, 1.95 ticks
    [InlineData(232f, 6)]    // 116 ms, 3.48 ticks
    [InlineData(1000f, 7)]   // capped at 250 ms
    public void BackTicksIsHalfTheRoundTripPlusTheInterpolationDelay(float rtt, int expected)
    {
        Assert.Equal(expected, LagComp.BackTicks(rtt, Rate, Interp));
    }

    [Fact]
    public void NegativeRoundTripIsThePresent()
    {
        Assert.Equal(0, LagComp.BackTicks(-5f, Rate, 0));
    }

    [Fact]
    public void ViewTickIsTheTickBackFromNow()
    {
        Assert.Equal(96u, LagComp.ViewTick(100, 4, 0));
    }

    [Fact]
    public void ViewTickNeverUnderflows()
    {
        Assert.Equal(0u, LagComp.ViewTick(2, 5, 0));
    }

    [Fact]
    public void ViewTickIsNotBeforeTheShootersLife()
    {
        // the shooter spawned at tick 98: a rewind of 4 ticks from 100 would show a world it could not have seen
        Assert.Equal(98u, LagComp.ViewTick(100, 4, 98));
    }

    [Fact]
    public void ViewTickInsideTheLifeIsUnchanged()
    {
        Assert.Equal(99u, LagComp.ViewTick(100, 1, 98));
    }

    [Fact]
    public void RingReturnsTheRecordedPose()
    {
        var ring = new PoseRing();
        ring.Record(10, true, At(1, 5));
        Assert.True(ring.TryGet(10, out var t));
        Assert.Equal(5f, t.Feet.X);
    }

    [Fact]
    public void RingMissesAnUnrecordedTick()
    {
        var ring = new PoseRing();
        ring.Record(10, true, At(1, 5));
        Assert.False(ring.TryGet(11, out _));
        Assert.False(new PoseRing().TryGet(0, out _)); // never recorded
    }

    [Fact]
    public void RingDoesNotReturnAnOverwrittenTick()
    {
        var ring = new PoseRing();
        ring.Record(10, true, At(1, 5));
        ring.Record(10 + PoseRing.Size, true, At(1, 9)); // same slot, newer tick
        Assert.False(ring.TryGet(10, out _));
        Assert.True(ring.TryGet(10 + PoseRing.Size, out var t));
        Assert.Equal(9f, t.Feet.X);
    }

    [Fact]
    public void RingIsNotValidWhenThePlayerWasNotATarget()
    {
        var ring = new PoseRing();
        ring.Record(10, false, At(1, 5)); // dead, or in a seat, at tick 10
        Assert.False(ring.TryGet(10, out _));
    }

    [Fact]
    public void RewindUsesThePoseOfTheViewTick()
    {
        var ring = new PoseRing();
        for (uint t = 0; t < 20; t++) ring.Record(t, true, At(1, t)); // walking at 1 unit a tick
        var view = LagComp.ViewTick(20, 4, 0); // 16
        Assert.True(LagComp.TryTargetAt(ring, view, 0, out var past));
        Assert.Equal(16f, past.Feet.X);
    }

    [Fact]
    public void RespawnDoesNotRewindIntoTheOldLife()
    {
        // the target stood at x=5 in its old life (ticks 5-7), died, and respawned at x=50 at tick 8; the shooter's view is tick 5
        var ring = new PoseRing();
        ring.Record(5, true, At(1, 5));
        ring.Record(7, true, At(1, 5));
        ring.Record(8, true, At(1, 50));
        Assert.True(LagComp.TryTargetAt(ring, 5, 8, out var t));
        Assert.Equal(50f, t.Feet.X); // the spawn point, not the old place
    }

    [Fact]
    public void RespawnBeforeTheSpawnPoseIsRecordedFallsBack()
    {
        // the life began at tick 8 but the pose at tick 8 is not in the ring: the caller takes the present position
        var ring = new PoseRing();
        ring.Record(5, true, At(1, 5));
        Assert.False(LagComp.TryTargetAt(ring, 5, 8, out _));
    }

    [Fact]
    public void TargetDeadAtTheViewTickIsNotAHit()
    {
        var ring = new PoseRing();
        ring.Record(12, false, At(1, 3));
        Assert.False(LagComp.TryTargetAt(ring, 12, 0, out _));
    }

    [Fact]
    public void TeleportStartsANewLifeForTheRewind()
    {
        // a teleport at tick 10 (a seat exit, a sandbox move) puts the target somewhere else: a shot from a view of tick 7 sees the new place, not the old one
        var ring = new PoseRing();
        for (uint t = 4; t < 10; t++) ring.Record(t, true, At(1, 0));
        ring.Record(10, true, At(1, 40));
        Assert.True(LagComp.TryTargetAt(ring, 7, 10, out var t2));
        Assert.Equal(40f, t2.Feet.X);
    }
}
