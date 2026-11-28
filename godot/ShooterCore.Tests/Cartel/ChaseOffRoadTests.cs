using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>
/// The Rival crew's vehicles off the roads (docs/cartel.md "Assaults", "Chase cars"): the way over roads and open ground (never through what is not open), an Assault van's last stretch, a chase car's spawn on open ground,
/// getting free of a wedge, the crew walking back to its car, the chase speed and the crews' make-up. Strict.
/// </summary>
public class ChaseOffRoadTests
{
    const float Dt = 1f / 30f;
    static readonly GameData Game = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    static readonly CartelFile Shipped = Game.Cartel;
    static readonly OffRoadData Off = new();

    public ChaseOffRoadTests() => StateMachineDiagnostics.Strict = true;

    /// <summary>One straight road along x from 0 to 1000 (y = 0).</summary>
    static RoadGraph Road() => RoadGraph.Make(new[] { new Vector2(0, 0), new Vector2(1000, 0) }, new[] { (0, 1, new[] { new Vector2(0, 0), new Vector2(1000, 0) }) });

    static readonly OpenLine All = (a, b) => true;
    static readonly OpenLine None = (a, b) => false;

    static bool Inside(Vector2 p, Vector2 min, Vector2 max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    /// <summary>Open unless the segment crosses the rectangle (a building).</summary>
    static OpenLine Around(Vector2 min, Vector2 max) => (a, b) =>
    {
        var n = Math.Max(2, (int)(Vector2.Distance(a, b) / 1f));
        for (var i = 0; i <= n; i++) if (Inside(Vector2.Lerp(a, b, i / (float)n), min, max)) return false;
        return true;
    };

    // ---- the way ----

    [Fact]
    public void AGoalOnTheRoadIsReachedByRoadAlone()
    {
        var plan = OffRoadRoute.Plan(Road(), new Vector2(0, 0), new Vector2(1000, 0), None, Off)!; // nothing is open: it needs no open ground
        Assert.True(plan.RoadM > 990f);
        Assert.False(plan.CrossesOpenGround);
        Assert.Equal(new Vector2(1000, 0), plan.Path.End);
    }

    [Fact]
    public void AGoalInAFieldIsReachedByTheRoadAndThenAcrossTheField()
    {
        var plan = OffRoadRoute.Plan(Road(), new Vector2(0, 0), new Vector2(500, 100), All, Off)!;
        Assert.True(plan.CrossesOpenGround);
        Assert.InRange(plan.RoadM, 330f, 430f); // the road while it brings it closer: it leaves it where the field is cheaper (a metre of field costs 1.3 of road)
        Assert.InRange(plan.OpenM, 120f, 200f); // then the field
        Assert.Equal(new Vector2(500, 100), plan.Path.End);
        Assert.True(plan.Path.Length < 700f); // not the 1000 m round by the road's end
    }

    [Fact]
    public void WhereTheRoadsDoNotBringItCloserItCrossesOpenGroundAllTheWay()
    {
        var bend = RoadGraph.Make(new[] { new Vector2(0, 0), new Vector2(1000, 0), new Vector2(1000, 1000) },
            new[] { (0, 1, new[] { new Vector2(0, 0), new Vector2(1000, 0) }), (1, 2, new[] { new Vector2(1000, 0), new Vector2(1000, 1000) }) });
        var plan = OffRoadRoute.Plan(bend, new Vector2(0, 50), new Vector2(0, 1000), All, Off)!; // the goal is far from every road
        Assert.Equal(0f, plan.RoadM);
        Assert.Equal(2, plan.Path.Points.Length);
        Assert.InRange(plan.OpenM, 940f, 960f);
    }

    [Fact]
    public void ABuildingInTheWayIsNeverCrossed()
    {
        var min = new Vector2(495, 0.5f); var max = new Vector2(505, 60);
        var open = Around(min, max);
        var plan = OffRoadRoute.Plan(Road(), new Vector2(0, 0), new Vector2(500, 100), open, Off)!;
        for (var s = 0f; s <= plan.Path.Length; s += 0.5f) Assert.False(Inside(plan.Path.At(s), min, max), $"{plan.Path.At(s)} is inside the building");
        Assert.Equal(new Vector2(500, 100), plan.Path.End);
        // with a wall all along the road's side no way over open ground is left
        var wall = Around(new Vector2(-1000, 5), new Vector2(2000, 95));
        Assert.Null(OffRoadRoute.Plan(Road(), new Vector2(0, 0), new Vector2(500, 100), wall, Off));
    }

    [Fact]
    public void WithNoOpenGroundAndAGoalOffTheRoadThereIsNoWay()
    {
        Assert.Null(OffRoadRoute.Plan(Road(), new Vector2(0, 0), new Vector2(500, 100), None, Off));
        Assert.Null(OffRoadRoute.Plan(RoadGraph.Empty, new Vector2(0, 0), new Vector2(500, 100), None, Off));
        Assert.NotNull(OffRoadRoute.Plan(RoadGraph.Empty, new Vector2(0, 0), new Vector2(500, 100), All, Off)); // no roads at all: straight
    }

    [Fact]
    public void ACarThatStandsInAFieldJoinsTheRoadWhenTheLineToItIsOpenAndLeavesItForTheVan()
    {
        var plan = OffRoadRoute.Plan(Road(), new Vector2(100, 60), new Vector2(900, 0), All, Off)!;
        Assert.Equal(new Vector2(100, 60), plan.Path.Start);
        Assert.Equal(new Vector2(900, 0), plan.Path.End);
        Assert.InRange(plan.OpenM, 55f, 70f); // the 60 m across the field to the road, none at the other end
        Assert.True(plan.RoadM > 700f);
    }

    [Fact]
    public void AVanThatNeverTurnsBackHasNoWayToAGoalBehindItOnItsRoad()
    {
        var g = Road();
        var at = g.Nearest(new Vector2(500, 0), 5)!.Value.Place;
        var behind = new Vector2(200, 0);
        Assert.Null(OffRoadRoute.Plan(g, new Vector2(500, 0), behind, None, Off, at, forward: true, uTurnM: float.PositiveInfinity));
        Assert.NotNull(OffRoadRoute.Plan(g, new Vector2(500, 0), behind, None, Off, at, forward: true, uTurnM: 150f)); // a turn costs, it may
        var ahead = OffRoadRoute.Plan(g, new Vector2(500, 0), new Vector2(800, 0), None, Off, at, forward: true, uTurnM: float.PositiveInfinity)!;
        Assert.InRange(ahead.RoadM, 290f, 310f);
    }

    // ---- a chase car's spot ----

    static ChaseTrail TrailTo(float x)
    {
        var t = new ChaseTrail(new Vector2(0, 0), 15f, 1500f);
        for (var p = 0f; p <= x; p += 15f) t.Note(new Vector2(p, 0));
        return t;
    }

    [Fact]
    public void ACarMayBePutDownOnOpenGroundAMapWithoutRoadsToo()
    {
        var van = new Vector2(1000, 0);
        var d = new ChaseData();
        for (var seed = 1; seed <= 20; seed++)
        {
            var spot = ChaseSpawn.Pick(RoadGraph.Empty, TrailTo(1000), van, new[] { van }, Array.Empty<Vector2>(), d, _ => false, new Random(seed), open: _ => true)!.Value;
            Assert.Null(spot.Place); // not on a road
            Assert.InRange(spot.BackM, 150f, 500f); // behind the van
            Assert.True(Vector2.Distance(spot.Ground, van) >= d.SpawnPlayerMinM);
            Assert.Equal(RoadPath.Yaw(van - spot.Ground), spot.Yaw, 0.01f); // facing the van
        }
    }

    [Fact]
    public void TheSpotsOnTheRoadAndBesideItAreBothCandidatesAndTheRulesHoldForBoth()
    {
        var van = new Vector2(1000, 0);
        var d = new ChaseData();
        var onRoad = 0; var onField = 0;
        for (var seed = 1; seed <= 60; seed++)
        {
            var spot = ChaseSpawn.Pick(Road(), TrailTo(1000), van, new[] { van, new Vector2(700, 40) }, Array.Empty<Vector2>(), d, p => p.X > 800f, new Random(seed), open: _ => true)!.Value;
            if (spot.Place != null) onRoad++; else onField++;
            Assert.True(spot.Ground.X <= 800f, "in the sight of the party: refused");
            Assert.True(Vector2.Distance(spot.Ground, new Vector2(700, 40)) >= d.SpawnPlayerMinM, "near a player: refused");
        }
        Assert.True(onRoad > 0 && onField > 0, $"{onRoad} on the road, {onField} off it");
    }

    [Fact]
    public void OnlyOpenGroundWithAWayToTheVanIsTaken()
    {
        var van = new Vector2(1000, 0);
        var d = new ChaseData();
        var asked = new List<Vector2>();
        for (var seed = 1; seed <= 20; seed++)
        {
            var spot = ChaseSpawn.Pick(RoadGraph.Empty, TrailTo(1000), van, new[] { van }, Array.Empty<Vector2>(), d, _ => false, new Random(seed),
                open: p => MathF.Abs(p.Y) < 1f, reach: p => { asked.Add(p); return p.X < 600f; })!.Value; // only the trail's own line is open, and only the far back has a way
            Assert.True(spot.Ground.X < 600f && MathF.Abs(spot.Ground.Y) < 1f);
        }
        Assert.NotEmpty(asked);
        Assert.Null(ChaseSpawn.Pick(RoadGraph.Empty, TrailTo(1000), van, new[] { van }, Array.Empty<Vector2>(), d, _ => false, new Random(1), open: _ => true, reach: _ => false)); // no way from anywhere: none
        Assert.Null(ChaseSpawn.Pick(RoadGraph.Empty, TrailTo(1000), van, new[] { van }, Array.Empty<Vector2>(), d, _ => false, new Random(1), open: _ => false)); // no open ground: none
    }

    // ---- the car's machine ----

    static ChaseFacts F(float gap = 100f, float vanSpeed = 20f, float carSpeed = 20f, bool driver = true, bool destroyed = false, bool route = true,
        float crewGap = float.PositiveInfinity, int crew = 0, bool free = false, bool aboard = false) => new(gap, vanSpeed, carSpeed, driver, destroyed, route, crewGap, crew, free, aboard);

    static ChaseCar Chasing(ChaseData? d = null)
    {
        var car = new ChaseCar(d ?? new ChaseData());
        car.Step(F(), Dt);
        Assert.Equal(ChaseState.Chasing, car.State);
        return car;
    }

    /// <summary>A car whose crew is out on foot beside it.</summary>
    static ChaseCar Out(ChaseData? d = null)
    {
        var car = Chasing(d);
        car.Step(F(gap: 10f, vanSpeed: 0f), Dt);
        Assert.True(car.Step(F(gap: 10f, vanSpeed: 0f, carSpeed: 0f), Dt).GetOut);
        Assert.Equal(ChaseState.Out, car.State);
        return car;
    }

    /// <summary>The van drives off at 16 m/s from a crew standing 50 m from it; steps the machine for <paramref name="seconds"/> and returns the step that asked for something, if any.</summary>
    static ChaseStep PullAway(ChaseCar car, float seconds, float from = 50f, float speed = 16f, bool free = true, bool driver = true, bool destroyed = false, int crew = 3)
    {
        var gap = from;
        for (var t = 0f; t < seconds; t += Dt)
        {
            gap += speed * Dt;
            var step = car.Step(F(gap: 5f, vanSpeed: speed, carSpeed: 0f, crewGap: gap, crew: crew, free: free, driver: driver, destroyed: destroyed), Dt);
            if (step != default) return step;
        }
        return default;
    }

    [Fact]
    public void AWedgedCarBacksOutAndDrivesOnUpToThreeTimesInARowThenStops()
    {
        var car = Chasing();
        for (var tries = 1; tries <= 3; tries++)
        {
            for (var t = 0f; t < 2.4f; t += Dt) Assert.Equal(default, car.Step(F(gap: 90f, carSpeed: 0.2f), Dt));
            Assert.Equal(ChaseState.Chasing, car.State);
            for (var t = 0f; t < 0.2f && car.State == ChaseState.Chasing; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
            Assert.Equal(ChaseState.Reversing, car.State); // under 1 m/s for 2.5 s with the van 90 m off: back out
            Assert.True(car.Shoots == false); // 90 m: out of range
            for (var t = 0f; t < 1.4f; t += Dt) car.Step(F(gap: 90f, carSpeed: 2f), Dt);
            Assert.Equal(ChaseState.Reversing, car.State);
            for (var t = 0f; t < 0.3f && car.State == ChaseState.Reversing; t += Dt) car.Step(F(gap: 90f, carSpeed: 2f), Dt);
            Assert.Equal(ChaseState.Chasing, car.State); // it goes on
        }
        for (var t = 0f; t < 5.8f; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Chasing, car.State); // no tries left: only the stuck clock
        for (var t = 0f; t < 0.5f && car.State == ChaseState.Chasing; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Lost, car.State); // 90 m from the van: lost, the crew stays seated (see RivalLostTests)
    }

    [Fact]
    public void TheTriesToGetFreeAreForgottenOnceTheCarHasDrivenOn()
    {
        var car = Chasing();
        for (var n = 0; n < 6; n++)
        {
            for (var t = 0f; t < 2.9f && car.State == ChaseState.Chasing; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
            Assert.Equal(ChaseState.Reversing, car.State);
            for (var t = 0f; t < 1.7f && car.State == ChaseState.Reversing; t += Dt) car.Step(F(gap: 90f, carSpeed: 2f), Dt);
            Assert.Equal(ChaseState.Chasing, car.State);
            for (var t = 0f; t < 3.2f; t += Dt) car.Step(F(gap: 90f, carSpeed: 12f), Dt); // free: driving
        }
        Assert.Equal(ChaseState.Chasing, car.State);
    }

    [Fact]
    public void ACarThatIsNotWedgedButHoldingItsGapBehindTheVanDoesNotBackOut()
    {
        var car = Chasing();
        for (var t = 0f; t < 20f; t += Dt) car.Step(F(gap: 10f, vanSpeed: 25f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Chasing, car.State);
        var noWay = Chasing();
        for (var t = 0f; t < 3.2f; t += Dt) noWay.Step(F(gap: 90f, carSpeed: 0.2f, route: false), Dt);
        Assert.Equal(ChaseState.Chasing, noWay.State); // no way: nothing to back out towards
    }

    [Fact]
    public void ABackingCarWhoseDriverDiesStops()
    {
        var car = Chasing();
        for (var t = 0f; t < 2.7f && car.State == ChaseState.Chasing; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Reversing, car.State);
        car.Step(F(gap: 90f, carSpeed: 2f, driver: false), Dt);
        Assert.Equal(ChaseState.Stopping, car.State);
        Assert.True(car.CrewInside);
    }

    [Fact]
    public void ACrewOnFootWhoseVanIsMoreThanSixtyMetresOffAndPullingAwayWalksBackToItsCar()
    {
        var car = Out();
        var step = PullAway(car, 6f);
        Assert.True(step.Remount);
        Assert.False(step.GetOut || step.Remove);
        Assert.Equal(ChaseState.Remounting, car.State);
        Assert.True(car.Remounting);
        Assert.True(car.InFight(0)); // the crew is on its way back: still in the fight
        Assert.False(car.CrewInside);
        Assert.False(car.Shoots); // the ones aboard do not shoot from a car that stands; those walking never fight
    }

    [Fact]
    public void ACrewDoesNotWalkBackWhileTheVanIsNearOrStaysOrComesCloser()
    {
        var near = Out();
        Assert.Equal(default, PullAway(near, 4f, from: 20f, speed: 5f)); // it drives off but is still within 60 m for ~8 s
        var still = Out();
        for (var t = 0f; t < 20f; t += Dt) Assert.Equal(default, still.Step(F(gap: 5f, crewGap: 120f, crew: 3, free: true), Dt)); // far, but not pulling away
        var closing = Out();
        var gap = 145f;
        for (var t = 0f; t < 20f; t += Dt) { gap -= 6f * Dt; Assert.Equal(default, closing.Step(F(gap: 5f, crewGap: gap, crew: 3, free: true), Dt)); } // the van comes back
        Assert.Equal(ChaseState.Out, closing.State);
    }

    [Fact]
    public void ACrewStaysOnFootWhenItsCarIsWreckedTakenOrItsDriverDeadOrNobodyIsLeft()
    {
        Assert.Equal(default, PullAway(Out(), 6f, destroyed: true));
        Assert.Equal(default, PullAway(Out(), 6f, free: false)); // a player sits in it
        Assert.Equal(default, PullAway(Out(), 6f, driver: false));
        Assert.Equal(default, PullAway(Out(), 6f, crew: 0));
    }

    [Fact]
    public void TheCrewIsInTheCarAndTheChaseGoesOn()
    {
        var car = Out();
        Assert.True(PullAway(car, 6f).Remount);
        Assert.Equal(default, car.Step(F(gap: 5f, crewGap: 80f, crew: 3, free: true, aboard: false), Dt)); // still walking
        var step = car.Step(F(gap: 5f, vanSpeed: 16f, carSpeed: 0f, crewGap: 80f, crew: 3, free: true, aboard: true), Dt);
        Assert.True(step.Mounted);
        Assert.Equal(ChaseState.Chasing, car.State);
        Assert.True(car.CrewInside);
        Assert.Equal(default, car.Step(F(gap: 90f, carSpeed: 3f), Dt)); // chases again
    }

    [Fact]
    public void AWalkBackThatDoesNotEndInTheCarLeavesTheCrewOnFootAndIsTriedAgainLater()
    {
        // the clock runs out
        var car = Out();
        Assert.True(PullAway(car, 6f).Remount);
        var step = default(ChaseStep);
        var gap = 150f;
        for (var t = 0f; t < 31f && step == default; t += Dt) { gap += 16f * Dt; step = car.Step(F(gap: 5f, crewGap: gap < 240f ? gap : 240f, crew: 3, free: true), Dt); }
        Assert.True(step.AbortRemount);
        Assert.Equal(ChaseState.Out, car.State);
        Assert.Equal(default, PullAway(car, 9f, from: 100f)); // not again at once
        Assert.True(PullAway(car, 4f, from: 100f).Remount); // after remount_retry_seconds
        // the car is wrecked on the way
        var wrecked = Out();
        Assert.True(PullAway(wrecked, 6f).Remount);
        Assert.True(wrecked.Step(F(gap: 5f, crewGap: 120f, crew: 3, free: true, destroyed: true), Dt).AbortRemount);
        Assert.Equal(ChaseState.Out, wrecked.State);
        // a player takes the car
        var taken = Out();
        Assert.True(PullAway(taken, 6f).Remount);
        Assert.True(taken.Step(F(gap: 5f, crewGap: 120f, crew: 3, free: false), Dt).AbortRemount);
        // the driver dies on the way
        var dead = Out();
        Assert.True(PullAway(dead, 6f).Remount);
        Assert.True(dead.Step(F(gap: 5f, crewGap: 120f, crew: 2, free: true, driver: false), Dt).AbortRemount);
    }

    [Fact]
    public void TheGiveUpRangeStillHoldsForACrewOnFootOrWalkingBack()
    {
        var car = Out();
        var step = default(ChaseStep);
        for (var t = 0f; t < 19.5f; t += Dt) Assert.Equal(default, step = car.Step(F(gap: 400f, crewGap: 260f, crew: 3, free: false), Dt)); // wrecked or taken: no walk, and far
        for (var t = 0f; t < 1f && !step.Remove; t += Dt) step = car.Step(F(gap: 400f, crewGap: 260f, crew: 3, free: false), Dt);
        Assert.Equal(new ChaseStep(false, true), step);
        Assert.Equal(ChaseState.GivingUp, car.State);
        var walking = Out();
        Assert.True(PullAway(walking, 6f).Remount);
        step = default;
        for (var t = 0f; t < 21f && !step.Remove; t += Dt) step = walking.Step(F(gap: 5f, crewGap: 300f, crew: 3, free: true), Dt);
        Assert.True(step.Remove);
        Assert.Equal(ChaseState.GivingUp, walking.State);
    }

    // ---- the member walking back ----

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        readonly List<GangShot> _shots;
        public FakeWorld(List<GangShot> shots) { _shots = shots; }
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
        public bool Clear(Vector3 from, Vector3 to) => true;
        public void Shoot(GangShot shot) => _shots.Add(shot);
    }

    sealed class Rig
    {
        public readonly FakeWorld World;
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Roster;
        public readonly GangConfig Rival = Game.Thieves.RivalCrew.GangOf(Game.Thieves.Gang);

        public Rig(List<GangShot> shots)
        {
            World = new FakeWorld(shots);
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            Entities = new WorldEntityManager(types);
            Roster = new GangRoster(Game.Thieves.Gang, World, Entities, 3);
        }

        public ThiefGang Crew(Func<ushort, bool> hostile)
        {
            var g = Roster.NewGang(Vector3.Zero, Rival, GangCrew.RivalCrew);
            g.HostileTo = hostile;
            return g;
        }

        public void Run(float seconds)
        {
            for (var t = 0f; t < seconds; t += Dt)
            {
                Roster.Update(Dt);
                Entities.Tick(Dt);
                Roster.ServeRoute((from, to) => new[] { from, to });
            }
        }
    }

    [Fact]
    public void ABayRiderSitsShutAndNeverFightsWhileItRidesThenFightsOnFootOnceOut()
    {
        var shot = new List<GangShot>();
        var rig = new Rig(shot);
        var crew = rig.Crew(p => p == 1);
        var window = crew.AddPosted(GangMemberKind.Gunman, new Vector3(0, 0, 0), 0, seat: true)!; // the cab's passenger seat: a window
        var bay = crew.AddPosted(GangMemberKind.Gunman, new Vector3(0, 0, 2), 0, seat: true, shut: true)!; // a post of the van's bay: no window
        Assert.True(bay.Seated && window.Seated);
        Assert.True(window.CanFight);
        Assert.False(bay.CanFight);
        rig.World.List.Add(new GangPlayer(1, new Vector3(10, 0, -8), true)); // a hostile player in sight
        rig.Run(5f);
        Assert.NotEmpty(shot);
        Assert.All(shot, s => Assert.Equal(window.HitId, s.MemberHitId)); // only the window fights
        Assert.Null(bay.Target);
        shot.Clear();
        bay.Unpost(new Vector3(0, 0, 4), 0f); // the van stopped: it steps out behind it
        Assert.True(bay.CanFight);
        rig.Run(8f);
        Assert.Contains(shot, s => s.MemberHitId == bay.HitId);
    }

    [Fact]
    public void ARiderIsSeatedOnTheWireAndAMemberWalkingBackIsRemountingNotFighting()
    {
        var shot = new List<GangShot>();
        var rig = new Rig(shot);
        var crew = rig.Crew(p => p == 1);
        var m = crew.AddPosted(GangMemberKind.Gunman, new Vector3(0, 0, 0), 0, seat: true)!;
        Assert.True(m.Seated);
        Assert.True(GangRules.SeatedOf(m.Serialize().State));
        Assert.Equal(GangMemberState.Posted, GangRules.StateOf(m.Serialize().State));
        m.Unpost(new Vector3(3, 0, 0), 0f);
        Assert.False(m.Seated);
        Assert.False(GangRules.SeatedOf(m.Serialize().State));
        rig.World.List.Add(new GangPlayer(1, new Vector3(20, 0, -15), true)); // a hostile player in sight
        var mounted = new List<GangMember>();
        var arrivedAt = Vector3.Zero;
        crew.Mounted += x => { mounted.Add(x); arrivedAt = x.Position; }; // the car seats it at once, in this very tick
        var door = new Vector3(-25, 0, 0);
        m.Remount(door);
        Assert.Equal(GangMemberState.Remount, m.State);
        Assert.Equal((byte)GangMemberState.Remount, m.Serialize().State);
        rig.Run(1f);
        Assert.Empty(shot); // never fights on the way
        Assert.Equal(GangMemberState.Remount, m.State);
        Assert.Null(m.Target);
        rig.Run(12f);
        Assert.Single(mounted);
        Assert.Same(m, mounted[0]);
        Assert.True(Vector3.Distance(arrivedAt, door) <= Game.Thieves.Gang.BoardReachM + 0.6f, $"{arrivedAt} against the door at {door}");
        Assert.NotEqual(GangMemberState.Remount, m.State); // it stands at the door
        m.Post(new Vector3(-25, 0, 0), 0f, seat: true); // the car seats it
        Assert.True(m.Seated);
        Assert.Equal(GangMemberState.Posted, m.State);
        var seated = m.Serialize().State;
        Assert.True(GangRules.SeatedOf(seated));
        Assert.Equal(GangMemberState.Posted, GangRules.StateOf(seated)); // the old clients' view of the state: the byte's own bits only
    }

    [Fact]
    public void TheDeadAndTheSeatedAreNotCalledBackAndAWalkBackCanBeGivenUp()
    {
        var rig = new Rig(new List<GangShot>());
        var crew = rig.Crew(_ => false);
        var seated = crew.AddPosted(GangMemberKind.Gunman, Vector3.Zero, 0, seat: true)!;
        seated.Remount(new Vector3(10, 0, 0));
        Assert.Equal(GangMemberState.Posted, seated.State); // on its seat: nothing to walk back to
        var dead = crew.Add(GangMemberKind.Enforcer, new Vector3(5, 0, 5), 0)!;
        dead.Kill(0);
        dead.Remount(new Vector3(10, 0, 0));
        Assert.Equal(GangMemberState.Dead, dead.State);
        var walker = crew.Add(GangMemberKind.Gunman, new Vector3(5, 0, 5), 0)!;
        walker.Remount(new Vector3(40, 0, 0));
        rig.Run(0.5f);
        Assert.Equal(GangMemberState.Remount, walker.State);
        walker.GiveUpRemount();
        Assert.Equal(GangMemberState.Guard, walker.State);
        Assert.Equal(GangMemberState.Remount, GangRules.StateOf(26));
        Assert.Equal(GangMemberState.Idle, GangRules.StateOf(27)); // a state this build does not know stands
    }

    // ---- the speed and the crews ----

    [Fact]
    public void ANpcChaseCarReachesEightyFourKilometresAnHourOneAndFourTenthsTheLoadedVan()
    {
        const float VanKmh = 60f;
        foreach (var kind in new[] { "harvest", "trap_house" })
        {
            var chase = DataFile.Section<HeistData>(Shipped.JobSection(kind), $"jobs.{kind}").Chase;
            Assert.Equal(VanKmh * 1.4f, chase.MaxSpeedMps * 3.6f, 0.1f);
            Assert.Equal(chase.FarSpeedMps, ChaseRules.FollowSpeed(chase, 1000f, 16.7f)); // far behind: 120 km/h, no more
            Assert.Equal(chase.MaxSpeedMps, ChaseRules.FollowSpeed(chase, 60f, 16.7f)); // closer than far_m: 84 km/h
            Assert.Equal(120f, chase.FarSpeedMps * 3.6f, 1.5f); // 33 m/s is 118.8 km/h
            Assert.True(ChaseRules.FollowSpeed(chase, chase.FollowM, VanKmh / 3.6f) < chase.MaxSpeedMps); // at its gap it keeps the van's own
            var car = Game.Vehicles.Vehicles.Single(v => v.Id == chase.VehicleType);
            Assert.True(car.TopSpeed >= chase.FarSpeedMps, "the car type reaches the far speed");
            var cartelVan = Game.Vehicles.Vehicles.Single(v => v.Id == Game.Cartel.AssaultCar.VehicleType);
            Assert.True(cartelVan.TopSpeed >= chase.FarSpeedMps, "the Assault van's type reaches the far speed");
            Assert.True(car.TopSpeed > chase.MaxSpeedMps, $"the car type's own top speed {car.TopSpeed * 3.6f:F0} km/h is above the NPC's: no allowance on the drive model is needed"); // the players' car is not limited by it
        }
    }

    [Fact]
    public void AnAssaultVanCarriesFiveAnEnforcerAndFourGunmenAndASquadAddsGunmenNeverAnotherMeleeMember()
    {
        foreach (var kind in new[] { "harvest", "trap_house" })
        {
            var d = DataFile.Section<HeistData>(Shipped.JobSection(kind), $"jobs.{kind}");
            for (var tier = 1; tier <= 4; tier++) Assert.Equal((4, 1), HeistRules.AssaultCrew(d, tier, 1));
            Assert.Equal((5, 1), HeistRules.AssaultCrew(d, 5, 1)); // the top tier adds a Gunman
            foreach (var size in new[] { 2, 3, 4 })
                for (var tier = 1; tier <= 5; tier++)
                {
                    var (gunmen, enforcers) = HeistRules.AssaultCrew(d, tier, size);
                    Assert.Equal(1, enforcers); // 1 melee
                    Assert.True(gunmen >= 4);
                    Assert.True(gunmen + enforcers <= d.MaxCrew);
                }
            Assert.Equal((5, 1), HeistRules.AssaultCrew(d, 1, 3));
        }
        var tight = new HeistData { AssaultGunmen = new[] { 4, 4, 4, 4, 4 }, AssaultEnforcers = new[] { 1, 1, 1, 1, 1 }, MaxCrew = 5, GunmenPerExtraMember = 1 };
        Assert.Equal((4, 1), HeistRules.AssaultCrew(tight, 3, 4)); // at the cap the melee member stays and the Gunmen make room
    }

    // ---- the shipped numbers ----

    [Fact]
    public void TheShippedOffRoadNumbersCheckOutAndAreTheDefaults()
    {
        var d = new OffRoadData();
        Assert.Equal((d.EnterM, d.LeaveM, d.StepM, d.OpenCost, d.SampleM, d.MaxGrade), (Shipped.OffRoad.EnterM, Shipped.OffRoad.LeaveM, Shipped.OffRoad.StepM, Shipped.OffRoad.OpenCost, Shipped.OffRoad.SampleM, Shipped.OffRoad.MaxGrade));
        Assert.Throws<DataFileException>(() => new OffRoadData { OpenCost = 0.9f }.Validate("off_road"));
        Assert.Throws<DataFileException>(() => new OffRoadData { StepM = 0 }.Validate("off_road"));
        Assert.Throws<DataFileException>(() => new OffRoadData { MaxChecks = 0 }.Validate("off_road"));
    }

    [Fact]
    public void TheShippedChaseRemountNumbersAreTheDefaultsAndCheckOut()
    {
        var d = new ChaseData();
        foreach (var kind in new[] { "harvest", "trap_house" })
        {
            var c = DataFile.Section<HeistData>(Shipped.JobSection(kind), $"jobs.{kind}").Chase;
            c.Validate($"jobs.{kind}.chase", Shipped.Tiers.Count);
            Assert.Equal((d.RemountM, d.PullMps, d.RemountSeconds, d.RemountRetrySeconds, d.UnstickAfterSeconds, d.ReverseSeconds, d.MaxUnsticks, d.SpawnSpreadM), (c.RemountM, c.PullMps, c.RemountSeconds, c.RemountRetrySeconds, c.UnstickAfterSeconds, c.ReverseSeconds, c.MaxUnsticks, c.SpawnSpreadM));
        }
        Assert.Equal(60f, d.RemountM);
        Assert.Throws<DataFileException>(() => new ChaseData { RemountM = 300 }.Validate("jobs.harvest.chase", 5)); // the walk back comes before the give-up
    }
}
