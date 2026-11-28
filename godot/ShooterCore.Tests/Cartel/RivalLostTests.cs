using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>
/// Rival vehicles that get no further (docs/cartel.md "Chase cars", "Assaults"): a car stuck far from the van is Lost and keeps its crew seated, near it the crew gets out, a crew far from the van walks back to its car,
/// the relocation gate, the Assault hard cap, the pilot's speed through a bend and the nearest-first spawn order. Strict.
/// </summary>
public class RivalLostTests
{
    const float Dt = 1f / 30f;

    public RivalLostTests() => StateMachineDiagnostics.Strict = true;

    static ChaseFacts F(float gap = 100f, float vanSpeed = 20f, float carSpeed = 20f, bool route = true, bool toVan = true, float crewGap = float.PositiveInfinity, int crew = 0, bool free = false) =>
        new(gap, vanSpeed, carSpeed, true, false, route, crewGap, crew, free, ToVan: toVan);

    static ChaseCar Chasing()
    {
        var car = new ChaseCar(new ChaseData { MaxUnsticks = 0 });
        car.Step(F(), Dt);
        return car;
    }

    [Fact]
    public void ACarStuckFarFromTheVanIsLostAndItsCrewStaysSeated()
    {
        var car = Chasing();
        var step = default(ChaseStep);
        for (var t = 0f; t < 7f && car.State == ChaseState.Chasing; t += Dt) step = car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Lost, car.State);
        Assert.Equal("stuck", car.Why);
        Assert.False(step.GetOut);
        Assert.True(car.CrewInside);
        Assert.True(car.InFight(3));
        for (var t = 0f; t < 20f; t += Dt) Assert.Equal(default, car.Step(F(gap: 90f, carSpeed: 0f, route: true, toVan: false), Dt)); // a way to a point short of the van: it holds
        Assert.Equal(ChaseState.Lost, car.State);
        car.Step(F(gap: 90f, carSpeed: 0f), Dt); // the van is reachable again
        Assert.Equal(ChaseState.Chasing, car.State);
        var noWay = Chasing();
        for (var t = 0f; t < 7f && noWay.State == ChaseState.Chasing; t += Dt) noWay.Step(F(gap: 90f, route: false, toVan: false), Dt);
        Assert.Equal((ChaseState.Lost, "no way to the van"), (noWay.State, noWay.Why));
    }

    [Fact]
    public void ACarStuckNearTheVanStopsAndTheCrewGetsOut()
    {
        var car = Chasing();
        var step = default(ChaseStep);
        for (var t = 0f; t < 7f && car.State == ChaseState.Chasing; t += Dt) step = car.Step(F(gap: 40f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Stopping, car.State);
        for (var t = 0f; t < 3f && !step.GetOut; t += Dt) step = car.Step(F(gap: 40f, carSpeed: 0f), Dt);
        Assert.True(step.GetOut);
        Assert.Equal(ChaseState.Out, car.State);
    }

    [Fact]
    public void ACrewOnFootFarFromTheVanWalksBackToItsCarWithoutTheVanPullingAway()
    {
        var car = Chasing();
        car.Step(F(gap: 10f, vanSpeed: 0f), Dt);
        Assert.True(car.Step(F(gap: 10f, vanSpeed: 0f, carSpeed: 0f), Dt).GetOut);
        var step = default(ChaseStep);
        for (var t = 0f; t < 0.9f; t += Dt) Assert.Equal(default, step = car.Step(F(gap: 5f, crewGap: 140f, crew: 3, free: true), Dt)); // under remount_far_m: it stays
        for (var t = 0f; t < 0.9f; t += Dt) Assert.Equal(default, step = car.Step(F(gap: 5f, crewGap: 200f, crew: 3, free: true), Dt)); // confirming
        for (var t = 0f; t < 0.5f && !step.Remount; t += Dt) step = car.Step(F(gap: 5f, crewGap: 200f, crew: 3, free: true), Dt);
        Assert.True(step.Remount);
        Assert.Equal(ChaseState.Remounting, car.State);
    }

    [Fact]
    public void ALostCarIsRelocatedAtOnceAndAFailedSpotIsRetriedAndFiveInARowRemoveIt()
    {
        var gate = new RelocateGate(3f, 5);
        Assert.False(gate.Due(false, Dt));
        Assert.True(gate.Due(true, Dt)); // wants to move: due at once
        gate.Deferred(); // seen: keeps trying where it is, no failure counted
        Assert.False(gate.Due(true, Dt));
        for (var t = 0f; t < 3.1f; t += Dt) gate.Due(true, Dt);
        Assert.True(gate.Due(true, Dt));
        Assert.Equal(0, gate.Failures);
        for (var n = 1; n < 5; n++)
        {
            Assert.Equal(RelocateVerdict.No, gate.Failed()); // no fresh spot: again after 3 s
            Assert.False(gate.Due(true, Dt));
            for (var t = 0f; t < 3.1f; t += Dt) gate.Due(true, Dt);
            Assert.True(gate.Due(true, Dt));
        }
        Assert.Equal(RelocateVerdict.Remove, gate.Failed()); // the fifth in a row
        gate.Due(false, Dt); // it is no longer lost: forgotten
        Assert.Equal(0, gate.Failures);
    }

    [Fact]
    public void NeverMoreThanTheCapPlusOneAssaultVansExistAtOnce()
    {
        Assert.True(AssaultRules.MaySend(counting: 2, existing: 3, maxAlive: 3));  // one stalled and not relocated: the next goes
        Assert.False(AssaultRules.MaySend(counting: 2, existing: 4, maxAlive: 3)); // that makes four: no fifth, whatever counts
        Assert.False(AssaultRules.MaySend(counting: 3, existing: 3, maxAlive: 3)); // the cap itself
    }

    [Fact]
    public void ACarBrakesBeforeATightBendAheadAndNotOnAStraight()
    {
        var corner = new ChasePilot.CornerNumbers(MaxLateralAccel: 10, Brake: 9.5f);
        var bend = new RoadPath(new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 10) }); // a right-angle corner at 100 m
        var east = RoadPath.Yaw(new Vector2(1, 0));
        Assert.True(float.IsPositiveInfinity(ChasePilot.CurveSpeed(bend, 0f, 20f, corner))); // far off: no limit yet
        var limit = ChasePilot.CurveSpeed(bend, 85f, 20f, corner); // 15 m before it
        Assert.InRange(limit, 10f, 19f);
        Assert.True(ChasePilot.Follow(bend, new Vector2(85, 0), east, 20f, 10f, 30f, corner).Throttle < 0f); // it brakes
        Assert.True(ChasePilot.Follow(bend, new Vector2(85, 0), east, 20f, 10f, 30f).Throttle > 0f); // without the bend it would drive on
        var straight = new RoadPath(new[] { new Vector2(0, 0), new Vector2(300, 0) });
        Assert.True(float.IsPositiveInfinity(ChasePilot.CurveSpeed(straight, 50f, 20f, corner)));
    }

    [Fact]
    public void AnAssaultSpotIsTheNearestOneInTheBandNotARandomOne()
    {
        var road = RoadGraph.Make(new[] { new Vector2(0, 0), new Vector2(3000, 0) }, new[] { (0, 1, new[] { new Vector2(0, 0), new Vector2(3000, 0) }) });
        var van = new Vector2(0, 1);
        var d = new AssaultCarData { SpawnChecks = 6 };
        for (var seed = 1; seed <= 25; seed++)
        {
            var spot = AssaultSpawn.Pick(road, van, Array.Empty<Vector2>(), Array.Empty<Vector2>(), d, _ => false, null, p => Vector2.Distance(p, van), new Random(seed), out _)!.Value;
            Assert.InRange(spot.Ground.X, 250f, 300f); // the band's nearest bucket, never the far end of its 250 to 400 m
        }
    }
}
