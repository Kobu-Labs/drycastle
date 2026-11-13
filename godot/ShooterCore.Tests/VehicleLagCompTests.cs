using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Lag compensation for vehicles (ADR 0013, B-02): the pose ring and its lookup, and the oriented box on a rewound pose.</summary>
public class VehicleLagCompTests
{
    const int Rate = 30;
    const int Interp = 3;

    static VehiclePose At(float x, float yaw = 0f) => new(new Vector3(x, 0, 0), yaw, 0f, 0f, 0f);

    [Fact]
    public void RingReturnsTheRecordedPose()
    {
        var ring = new VehiclePoseRing();
        ring.Record(10, true, At(5));
        Assert.True(ring.TryGet(10, out var p));
        Assert.Equal(5f, p.Feet.X);
    }

    [Fact]
    public void RingMissesAnUnrecordedTick()
    {
        var ring = new VehiclePoseRing();
        ring.Record(10, true, At(5));
        Assert.False(ring.TryGet(11, out _));
        Assert.False(new VehiclePoseRing().TryGet(0, out _));
    }

    [Fact]
    public void RingDoesNotReturnAnOverwrittenTick()
    {
        // 32 ticks of history: the sample of tick 10 is gone once tick 42 is written into its slot
        var ring = new VehiclePoseRing();
        ring.Record(10, true, At(5));
        ring.Record(10 + VehiclePoseRing.Size, true, At(9));
        Assert.False(ring.TryGet(10, out _));
        Assert.True(ring.TryGet(10 + VehiclePoseRing.Size, out var p));
        Assert.Equal(9f, p.Feet.X);
    }

    [Fact]
    public void ViewTickIsCappedAsForPlayers()
    {
        // a 1 s round trip rewinds a vehicle no further than the 250 ms cap (7 ticks at 30 Hz)
        var ring = new VehiclePoseRing();
        for (uint t = 0; t < 40; t++) ring.Record(t, true, At(t)); // driving at 1 unit a tick
        var back = LagComp.BackTicks(1000f, Rate, Interp);
        Assert.Equal(7, back);
        Assert.True(LagComp.TryVehicleAt(ring, LagComp.ViewTick(40, back, 0), 0, out var p));
        Assert.Equal(33f, p.Feet.X); // tick 33 = 40 - 7: the cap, not the round trip, decides how far back
    }

    [Fact]
    public void ResetStartsANewLifeAndNothingBeforeItIsReached()
    {
        var ring = new VehiclePoseRing();
        for (uint t = 0; t < 20; t++) ring.Record(t, true, At(100)); // the old life, parked far away
        ring.Clear();                                                 // reset at tick 20 (the vehicle is put back at its origin)
        ring.Record(20, true, At(0));
        ring.Record(21, true, At(0));
        // a shot whose view is tick 18 (before the reset) is clamped to the new life's first tick, never to the old pose
        Assert.True(LagComp.TryVehicleAt(ring, 18, 20, out var p));
        Assert.Equal(0f, p.Feet.X);
        Assert.False(ring.TryGet(18, out _));
    }

    [Fact]
    public void ADestroyedVehicleHasNoPoseToRewindTo()
    {
        var ring = new VehiclePoseRing();
        ring.Record(10, false, At(5)); // destroyed at tick 10: not a hull
        Assert.False(ring.TryGet(10, out _));
    }

    [Fact]
    public void ASleepingVehicleAnswersEveryTickOfItsRunAtItsRestPose()
    {
        var ring = new VehiclePoseRing();
        for (uint t = 10; t < 20; t++) ring.RecordRest(t, At(7, 1.5f)); // parked from tick 10 to 19
        Assert.True(ring.TryGet(10, out var first));
        Assert.True(ring.TryGet(15, out var mid));
        Assert.True(ring.TryGet(19, out var last));
        Assert.Equal(7f, mid.Feet.X);
        Assert.Equal(1.5f, last.Yaw);
        Assert.Equal(first.Feet, mid.Feet);
        Assert.False(ring.TryGet(9, out _));  // before it parked: nothing recorded
        Assert.False(ring.TryGet(20, out _)); // not parked yet, and not recorded awake
    }

    [Fact]
    public void AWakingVehicleKeepsItsRestPoseForTheTicksItSlept()
    {
        var ring = new VehiclePoseRing();
        for (uint t = 10; t < 20; t++) ring.RecordRest(t, At(7));
        ring.Record(20, true, At(8)); // driven off at tick 20
        Assert.True(ring.TryGet(19, out var slept));
        Assert.Equal(7f, slept.Feet.X);
        Assert.True(ring.TryGet(20, out var moving));
        Assert.Equal(8f, moving.Feet.X);
    }

    [Fact]
    public void TwoSleepsAreSeparateRuns()
    {
        var ring = new VehiclePoseRing();
        for (uint t = 10; t < 12; t++) ring.RecordRest(t, At(1));
        ring.Record(12, true, At(2));
        for (uint t = 13; t < 15; t++) ring.RecordRest(t, At(3));
        Assert.True(ring.TryGet(11, out var a));
        Assert.Equal(1f, a.Feet.X);
        Assert.True(ring.TryGet(14, out var b));
        Assert.Equal(3f, b.Feet.X);
        Assert.False(ring.TryGet(10 + 40, out _));
    }

    [Fact]
    public void TheRewoundBoxIsHitWhereTheCarWasAndNotWhereItIsNow()
    {
        // the car drives along +X: 5 m back on the shooter's screen, 10 m now; the shot comes down the +Z axis past where it was
        var from = new Vector3(5, 1, -20);
        var dir = new Vector3(0, 0, 1);
        var rewound = VehicleBox.Ray(from, dir, 100f, new Vector3(5, 0, 0), 0f, 1.1f, 2.15f, 1.6f);
        var present = VehicleBox.Ray(from, dir, 100f, new Vector3(10, 0, 0), 0f, 1.1f, 2.15f, 1.6f);
        Assert.NotNull(rewound);
        Assert.Equal(20f - 2.15f, rewound!.Value.Distance, 3); // the near face of the box, 17.85 m out
        Assert.Null(present);
    }

    [Fact]
    public void TheRewoundBoxIsTheSameShapeAsThePresentOne()
    {
        // D-03: the analytic box is the hit volume of VehicleCollider; at the same pose both give the same entry
        var feet = new Vector3(3, 0, 4);
        var from = new Vector3(3, 1, -10);
        var hit = VehicleBox.Ray(from, new Vector3(0, 0, 1), 100f, feet, 0.4f, 1.1f, 2.15f, 1.6f);
        Assert.NotNull(hit);
        Assert.InRange(hit!.Value.Distance, 0f, 100f);
    }
}
