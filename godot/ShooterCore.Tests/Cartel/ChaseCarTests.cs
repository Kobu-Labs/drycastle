using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Rival chase cars of the Heists (docs/cartel.md "Chase cars", GLOSSARY "Chase car"): the numbers, the spawn clock and its cap, the spawn spot, the car's machine (shoot range, stop and get out, off the road, give up, a dead driver), the driving over a road path, and the crew's seat (a posted member that moves, aims worse, steps out). Strict.</summary>
public class ChaseCarTests
{
    const float Dt = 1f / 30f;

    static readonly CartelFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel;
    static readonly GameData Game = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    public ChaseCarTests() => StateMachineDiagnostics.Strict = true;

    // ---- the numbers ----

    [Fact]
    public void BothHeistsShipTheDefaultChaseNumbersAndTheyCheckOut()
    {
        var d = new ChaseData();
        foreach (var (kind, section) in new[] { ("harvest", DataFile.Section<HarvestData>(Shipped.JobSection("harvest"), "jobs.harvest").Chase), ("trap_house", DataFile.Section<HeistData>(Shipped.JobSection("trap_house"), "jobs.trap_house").Chase) })
        {
            section.Validate($"jobs.{kind}.chase", Shipped.Tiers.Count);
            Assert.Equal((d.VehicleType, d.Skin, d.FirstAfterSeconds, d.IntervalSeconds, d.MaxAlive), (section.VehicleType, section.Skin, section.FirstAfterSeconds, section.IntervalSeconds, section.MaxAlive));
            Assert.Equal(d.Gunmen, section.Gunmen);
            Assert.Equal((d.SpawnAheadMinM, d.SpawnAheadMaxM), (section.SpawnAheadMinM, section.SpawnAheadMaxM));
            Assert.Equal((d.SpawnPlayerMinM, d.ShootRangeM, d.MovingAimErrorDeg, d.StopSpeedMps, d.StopWithinM, d.GiveUpM, d.GiveUpSeconds), (section.SpawnPlayerMinM, section.ShootRangeM, section.MovingAimErrorDeg, section.StopSpeedMps, section.StopWithinM, section.GiveUpM, section.GiveUpSeconds));
            Assert.Equal((d.FollowM, d.MaxSpeedMps, d.RemountM, d.UnstickAfterSeconds, d.WreckSeconds, d.SeatDropM), (section.FollowM, section.MaxSpeedMps, section.RemountM, section.UnstickAfterSeconds, section.WreckSeconds, section.SeatDropM));
        }
        Assert.Equal((150f, 40f, 5f, 30f, 250f, 20f, 3, 0f, 22f), (d.SpawnPlayerMinM, d.ShootRangeM, d.StopSpeedMps, d.StopWithinM, d.GiveUpM, d.GiveUpSeconds, d.MaxAlive, d.FirstAfterSeconds, d.IntervalSeconds)); // the user's numbers: the first the moment the van leaves, every 22 s, 3 alive
        Assert.Equal((150f, 400f), (d.SpawnAheadMinM, d.SpawnAheadMaxM)); // 150 to 400 m ahead along the way to the drop
    }

    [Fact]
    public void AChaseCarIsTheCarTypeInADarkSkinThatTheVehiclesFileHas()
    {
        var d = new ChaseData();
        var car = Game.Vehicles.Vehicles.Single(v => v.Id == d.VehicleType);
        Assert.True(d.Skin < car.Skins.Count);
        Assert.EndsWith("_red", car.SkinModel(d.Skin)); // the darkest of yellow, blue, green, red, grey (catalogue renders' mean luminance)
        Assert.True(car.Seats.Count >= 1 + d.MaxGunmen, "a driver and the Gunmen each have a seat");
        Assert.NotNull(car.Drive); // on the drive model: an NPC's inputs step it as a player's
        Assert.True(d.WreckSeconds < Game.Vehicles.DestroyedReturnTime); // the wreck is gone before the type's return brings the car back
    }

    [Fact]
    public void ABadSectionIsRefusedWithTheFieldNamed()
    {
        static string Why(Action<ChaseData> bad) { var d = new ChaseData(); bad(d); return Assert.Throws<DataFileException>(() => d.Validate("jobs.harvest.chase", 5)).Message; }
        Assert.Contains("gunmen", Why(d => d.Gunmen = new[] { 1, 2 }));
        Assert.Contains("max_gunmen", Why(d => d.MaxGunmen = 4));
        Assert.Contains("max_alive", Why(d => d.MaxAlive = 0));
        Assert.Contains("give_up_seconds", Why(d => d.GiveUpSeconds = 0));
        Assert.Contains("spawn_back_max_m", Why(d => d.SpawnBackMaxM = 100));
        Assert.Contains("give_up_m", Why(d => d.GiveUpM = 20));
        Assert.Contains("vehicle_type", Why(d => d.VehicleType = ""));
        Assert.Contains("moving_aim_error_deg", Why(d => d.MovingAimErrorDeg = float.NaN));
    }

    [Fact]
    public void AShippedChaseCarCarriesFourAnEnforcerAtTheWheelAndThreeGunmenWhateverTheTierOrSquad()
    {
        var d = new ChaseData();
        foreach (var kind in new[] { "harvest", "trap_house" })
        {
            var section = DataFile.Section<HeistData>(Shipped.JobSection(kind), $"jobs.{kind}").Chase;
            for (var tier = 1; tier <= 5; tier++)
                foreach (var size in new[] { 1, 2, 4 })
                {
                    Assert.Equal(3, ChaseRules.Gunmen(section, tier, size)); // the car's three other seats, full: more cannot be added
                    Assert.Equal(4, ChaseRules.Crew(section, tier, size));   // 1 melee (the Enforcer at the wheel) and 3 Gunmen
                }
        }
        Assert.True(Game.Vehicles.Vehicles.Single(v => v.Id == d.VehicleType).Seats.Count >= ChaseRules.Crew(d, 1, 1));
    }

    [Fact]
    public void TheGunmenGrowWithTheTierAndTheSquadWithinOneToThree()
    {
        var d = new ChaseData();
        var few = new ChaseData { Gunmen = new[] { 1, 1, 2, 2, 3 } }; // the numbers a lighter car could carry
        Assert.Equal(1, ChaseRules.Gunmen(few, 1, 1));
        Assert.Equal(3, ChaseRules.Gunmen(few, 5, 1));
        Assert.Equal(2, ChaseRules.Gunmen(few, 1, 2)); // plus one per extra member
        Assert.Equal(3, ChaseRules.Gunmen(few, 5, 4)); // never more than three
        Assert.Equal(1, ChaseRules.Gunmen(new ChaseData { Gunmen = new[] { 0, 0, 0, 0, 0 } }, 1, 1)); // never fewer than one
    }

    [Fact]
    public void TheFollowSpeedKeepsTheGapAndNeverClosesOnTheVan()
    {
        var d = new ChaseData();
        Assert.Equal(20f, ChaseRules.FollowSpeed(d, d.FollowM, 20f)); // at the following distance: the van's speed
        Assert.True(ChaseRules.FollowSpeed(d, 100f, 20f) > 20f); // far: catches up
        Assert.Equal(d.MaxSpeedMps, ChaseRules.FollowSpeed(d, 400f, 20f));
        Assert.True(ChaseRules.FollowSpeed(d, 5f, 20f) < 20f); // too close: slower than the van
        Assert.Equal(0f, ChaseRules.FollowSpeed(d, 1f, 5f));
    }

    [Fact]
    public void AMovingCarAimsWorseThanAStandingOne()
    {
        var d = new ChaseData();
        Assert.Equal(d.MovingAimErrorDeg, ChaseRules.AimErrorDeg(d, 20f));
        Assert.Equal(d.MovingAimErrorDeg, ChaseRules.AimErrorDeg(d, -20f));
        Assert.Null(ChaseRules.AimErrorDeg(d, 0.5f)); // the gun's own
    }

    // ---- the clock ----

    [Fact]
    public void TheClockStartsWhenTheVanLeavesTheObjectiveTheFirstAtOnceThenEveryTwentyTwo()
    {
        var c = new ChaseClock(new ChaseData());
        Assert.False(c.Tick(100f, 0)); // idle: nothing, whatever the time
        Assert.True(c.Start());
        Assert.False(c.Start()); // a second leave of the same drive changes nothing
        Assert.True(c.Tick(Dt, 0)); // first_after_seconds 0: the first the moment the van left
        c.Sent();
        Assert.False(c.Tick(21.8f, 0));
        Assert.True(c.Tick(0.4f, 0)); // 22 s on
        c.Sent();
        Assert.False(c.Tick(10f, 0));
        Assert.Equal(12f, c.Left, 0.01f);
    }

    [Fact]
    public void AtMostThreeAreAliveAndTheNextComesTheMomentOneIsOutOfTheFight()
    {
        var c = new ChaseClock(new ChaseData());
        c.Start();
        Assert.True(c.Tick(Dt, 0)); c.Sent();
        Assert.True(c.Tick(22f, 1)); c.Sent();
        Assert.True(c.Tick(22f, 2)); c.Sent();
        Assert.False(c.Tick(22f, 3)); // the interval ran out but three are alive
        Assert.False(c.Tick(100f, 3));
        Assert.True(c.Tick(Dt, 2)); // one died: the due car comes at once
        c.Sent();
        Assert.False(c.Tick(1f, 2)); // and the interval starts over
    }

    [Fact]
    public void ACarThatFoundNoSpotAsksAgainSoonWithoutRestartingTheInterval()
    {
        var c = new ChaseClock(new ChaseData());
        c.Start();
        Assert.True(c.Tick(Dt, 0));
        c.NoSpot();
        Assert.False(c.Tick(2.9f, 0));
        Assert.True(c.Tick(0.2f, 0)); // 3 s later again
        c.Sent();
        Assert.False(c.Tick(21f, 0));
    }

    [Fact]
    public void NoNewCarComesOnceTheVanReachesTheDropOrTheHeistEnds()
    {
        var c = new ChaseClock(new ChaseData());
        c.Start();
        Assert.True(c.Tick(20f, 0)); c.Sent();
        c.Stop(); // the van reached the drop
        Assert.Equal(ChaseClockState.Over, c.State);
        Assert.False(c.Tick(500f, 0));
        Assert.False(c.Start()); // leaving the drop again starts nothing
        Assert.Equal(ChaseClockState.Over, c.State);
        var never = new ChaseClock(new ChaseData());
        never.Stop();
        Assert.False(never.Tick(100f, 0));
    }

    [Fact]
    public void TheSpawnClockHangsOnTheHeistsPhaseChanges()
    {
        var h = new Heist(2, 5, 100);
        var clock = new ChaseClock(new ChaseData());
        h.PhaseChanged += (from, to) =>
        {
            if (to == HeistPhase.ToDrop && from == HeistPhase.AtObjective) clock.Start();
            if (to == HeistPhase.AtDrop) clock.Stop();
        };
        h.Step(new HeistFacts(false, true, false, 0), Dt); // at the objective
        Assert.Equal(ChaseClockState.Idle, clock.State);
        h.Step(new HeistFacts(false, false, false, 1), Dt); // away with fewer than the minimum: no drive yet
        Assert.Equal(ChaseClockState.Idle, clock.State);
        h.Step(new HeistFacts(false, false, false, 2), Dt); // away with the minimum
        Assert.Equal(ChaseClockState.Running, clock.State);
        h.Step(new HeistFacts(false, false, true, 2), Dt); // the drop's area
        Assert.Equal(ChaseClockState.Over, clock.State);
    }

    // ---- the spot ----

    // A straight road east along z = 0 from x = 0 to 1500 in 100 m legs, and a side road north from x = 700.
    static RoadGraph Straight()
    {
        var nodes = Enumerable.Range(0, 16).Select(i => new Vector2(i * 100f, 0)).Append(new Vector2(700, 300)).ToArray();
        var edges = new List<(int, int, Vector2[])>();
        for (var i = 0; i < 15; i++) edges.Add((i, i + 1, new[] { nodes[i], nodes[i + 1] }));
        edges.Add((7, 16, new[] { nodes[7], nodes[16] }));
        return RoadGraph.Make(nodes, edges);
    }

    /// <summary>The van's way: from the objective at x = 0 along the road to x = 1000, a point every 100 m.</summary>
    static ChaseTrail TrailTo(float x)
    {
        var t = new ChaseTrail(new Vector2(0, 0), 15f, 1500f);
        for (var p = 0f; p <= x; p += 15f) t.Note(new Vector2(p, 0));
        return t;
    }

    static ChaseSpot? Pick(Vector2 van, IReadOnlyList<Vector2> players, IReadOnlyList<Vector2>? cars = null, Func<Vector2, bool>? blocked = null, ChaseTrail? trail = null, int seed = 1) =>
        ChaseSpawn.Pick(Straight(), trail ?? TrailTo(van.X), van, players, cars ?? Array.Empty<Vector2>(), new ChaseData(), blocked ?? (_ => false), new Random(seed));

    [Fact]
    public void TheTrailListsItsPointsBehindTheVanNearestFirstWithinTheRange()
    {
        var trail = TrailTo(1000);
        var behind = trail.Behind(new Vector2(1000, 0), 150, 500).ToList();
        Assert.NotEmpty(behind);
        Assert.All(behind, b => Assert.InRange(b.BackM, 150f, 500f));
        Assert.All(behind, b => Assert.True(b.Point.X < 1000f)); // behind: back along its way
        Assert.Equal(behind.OrderBy(b => b.BackM).Select(b => b.BackM), behind.Select(b => b.BackM));
        Assert.Empty(new ChaseTrail(new Vector2(0, 0)).Behind(new Vector2(50, 0), 150, 500)); // a van that has hardly moved has nothing 150 m behind it
    }

    [Fact]
    public void TheTrailKeepsOnlyTheRecentWay()
    {
        var t = new ChaseTrail(new Vector2(0, 0), 10f, 100f);
        for (var x = 0f; x <= 1000f; x += 10f) t.Note(new Vector2(x, 0));
        Assert.True(t.Count <= 12);
        Assert.Empty(t.Behind(new Vector2(1000, 0), 400, 900)); // forgotten
    }

    [Fact]
    public void ASpotIsOnTheRoadBehindTheVanAtLeastTheMinimumFromEveryPlayer()
    {
        var van = new Vector2(1000, 0);
        for (var seed = 1; seed <= 20; seed++)
        {
            var spot = Pick(van, new[] { van, new Vector2(990, 5) }, seed: seed)!.Value;
            Assert.InRange(spot.BackM, 150f, 500f);
            Assert.InRange(spot.Ground.X, 500f, 850f); // behind the van along its way
            Assert.Equal(0f, spot.Ground.Y, 0.01f); // on the road
            Assert.True(Vector2.Distance(spot.Ground, van) >= 150f);
            Assert.Equal(RoadPath.Yaw(van - spot.Ground), spot.Yaw, 0.01f); // facing along the road towards the van
        }
    }

    [Fact]
    public void ASpotAheadOnTheWayToTheDropMeetsTheVanHeadOnAndANearDropOrASightedWayFallsBackBehind()
    {
        var van = new Vector2(500, 0);
        var toDrop = new RoadPath(new[] { van, new Vector2(1500, 0) }); // the van's way to a drop 1000 m on
        for (var seed = 1; seed <= 20; seed++)
        {
            var spot = ChaseSpawn.Pick(Straight(), TrailTo(500), van, new[] { van }, Array.Empty<Vector2>(), new ChaseData(), _ => false, new Random(seed), ahead: toDrop)!.Value;
            Assert.InRange(spot.BackM, -400f, -150f); // negative: that far ahead of the van
            Assert.InRange(spot.Ground.X, 650f, 900f); // on its way to the drop
            Assert.Equal(RoadPath.Yaw(van - spot.Ground), spot.Yaw, 0.01f); // facing the van: head-on
        }
        var near = new RoadPath(new[] { van, new Vector2(600, 0) }); // the drop is 100 m on: nothing 150 m ahead
        Assert.True(ChaseSpawn.Pick(Straight(), TrailTo(500), van, new[] { van }, Array.Empty<Vector2>(), new ChaseData(), _ => false, new Random(1), ahead: near)!.Value.BackM > 0);
        // all of the way ahead is in the party's sight: the old rule behind the van
        Assert.True(ChaseSpawn.Pick(Straight(), TrailTo(500), van, new[] { van }, Array.Empty<Vector2>(), new ChaseData(), p => p.X > 500f, new Random(1), ahead: toDrop)!.Value.BackM > 0);
    }

    [Fact]
    public void ASpotIsFarFromEveryPlayerNotJustTheParty()
    {
        var van = new Vector2(1000, 0);
        var stranger = new Vector2(700, 0); // a player on foot beside the road behind
        for (var seed = 1; seed <= 20; seed++)
        {
            var spot = Pick(van, new[] { van, stranger }, seed: seed)!.Value;
            Assert.True(Vector2.Distance(spot.Ground, stranger) >= 150f, $"{spot.Ground} is near the stranger");
        }
        Assert.Null(Pick(van, new[] { van, new Vector2(800, 0), new Vector2(560, 0), new Vector2(680, 0) })); // every candidate is within 150 m of someone
    }

    [Fact]
    public void ASpotInTheSightOfThePartyIsNotTaken()
    {
        var van = new Vector2(1000, 0);
        var seen = new List<Vector2>();
        Func<Vector2, bool> blocked = p => { seen.Add(p); return p.X > 650f; };
        for (var seed = 1; seed <= 20; seed++) Assert.True(Pick(van, new[] { van }, blocked: blocked, seed: seed)!.Value.Ground.X <= 650f);
        Assert.NotEmpty(seen);
        Assert.Null(Pick(van, new[] { van }, blocked: _ => true)); // all of them seen: none now
    }

    [Fact]
    public void ASpotIsApartFromTheOtherChaseCars()
    {
        var van = new Vector2(1000, 0);
        var other = new Vector2(700, 0);
        for (var seed = 1; seed <= 20; seed++) Assert.True(Vector2.Distance(Pick(van, new[] { van }, cars: new[] { other }, seed: seed)!.Value.Ground, other) >= 25f);
    }

    [Fact]
    public void ThereIsNoSpotBeforeTheVanHasDrivenFarEnoughOrOnAMapWithoutRoads()
    {
        Assert.Null(Pick(new Vector2(100, 0), new[] { new Vector2(100, 0) })); // 100 m of way: nothing 150 m behind
        Assert.Null(ChaseSpawn.Pick(RoadGraph.Empty, TrailTo(1000), new Vector2(1000, 0), Array.Empty<Vector2>(), Array.Empty<Vector2>(), new ChaseData(), _ => false, new Random(1)));
        // a van that left the road: the trail's points are off it, the nearest road within spawn_road_m is used, further it is not
        var off = new ChaseTrail(new Vector2(0, 600), 15f, 1500f);
        for (var x = 0f; x <= 1000f; x += 15f) off.Note(new Vector2(x, 600));
        Assert.Null(ChaseSpawn.Pick(Straight(), off, new Vector2(1000, 600), Array.Empty<Vector2>(), Array.Empty<Vector2>(), new ChaseData(), _ => false, new Random(1)));
    }

    [Fact]
    public void TheRealVillageRoadsGiveASpotBehindAVanThatDrivesOne()
    {
        var g = RoadGraph.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "roads_village.json")), 3);
        var from = g.Nearest(new Vector2(0, -400), 2000)!.Value.Place;
        var far = g.Nearest(new Vector2(900, -300), 2000)!.Value.Place;
        var route = g.RouteBetween(from, far)!.Value.Path;
        Assert.True(route.Length > 800f, $"{route.Length:F0} m of road");
        var trail = new ChaseTrail(route.Start);
        for (var s = 0f; s <= route.Length; s += 5f) trail.Note(route.At(s));
        var van = route.End;
        var spot = ChaseSpawn.Pick(g, trail, van, new[] { van }, Array.Empty<Vector2>(), new ChaseData(), _ => false, new Random(3));
        Assert.NotNull(spot);
        Assert.True(Vector2.Distance(spot!.Value.Ground, van) >= 150f);
        Assert.True(g.Nearest(spot.Value.Ground, 1f) != null, "on a road");
    }

    // ---- the machine ----

    static ChaseFacts F(float gap = 100f, float vanSpeed = 20f, float carSpeed = 20f, bool driver = true, bool destroyed = false, bool route = true,
        float crewGap = float.PositiveInfinity, int crew = 0, bool free = false, bool aboard = false) =>
        new(gap, vanSpeed, carSpeed, driver, destroyed, route, crewGap, crew, free, aboard);

    static ChaseCar Chasing(ChaseData? d = null)
    {
        var car = new ChaseCar(d ?? new ChaseData());
        Assert.Equal(ChaseState.Spawned, car.State);
        Assert.Equal(default, car.Step(F(), Dt));
        Assert.Equal(ChaseState.Chasing, car.State);
        return car;
    }

    [Fact]
    public void ACarShootsOnlyWithinFortyMetresOfTheVanWhileItIsInIt()
    {
        var car = new ChaseCar(new ChaseData());
        Assert.False(car.Shoots); // Spawned
        car.Step(F(gap: 30f), Dt);
        Assert.Equal(ChaseState.Chasing, car.State);
        car.Step(F(gap: 41f), Dt);
        Assert.False(car.Shoots);
        car.Step(F(gap: 39f), Dt);
        Assert.True(car.Shoots);
        car.Step(F(gap: 40.1f), Dt);
        Assert.False(car.Shoots);
    }

    [Fact]
    public void AVanThatStopsOrCrawlsWithinThirtyMetresStopsTheCarAndTheCrewGetsOut()
    {
        var car = Chasing();
        car.Step(F(gap: 31f, vanSpeed: 0f), Dt); // slow but 31 m away: the car goes on
        Assert.Equal(ChaseState.Chasing, car.State);
        car.Step(F(gap: 20f, vanSpeed: 6f), Dt); // near but at 6 m/s: goes on
        Assert.Equal(ChaseState.Chasing, car.State);
        car.Step(F(gap: 29f, vanSpeed: 4.9f), Dt);
        Assert.Equal(ChaseState.Stopping, car.State);
        Assert.True(car.Shoots); // still in the windows' range
        Assert.Equal(default, car.Step(F(gap: 25f, vanSpeed: 0f, carSpeed: 9f), Dt));
        var out1 = car.Step(F(gap: 22f, vanSpeed: 0f, carSpeed: 0.2f), Dt); // standing
        Assert.Equal(new ChaseStep(true, false), out1);
        Assert.Equal(ChaseState.Out, car.State);
        Assert.True(car.Shoots); // on foot: fights
        Assert.Equal(default, car.Step(F(gap: 22f, vanSpeed: 0f, carSpeed: 0f), Dt)); // the crew gets out once
        Assert.False(car.CrewInside);
    }

    [Fact]
    public void ABrakingCarThatNeverQuiteStopsLetsTheCrewOutAfterTheStopSeconds()
    {
        var car = Chasing();
        car.Step(F(gap: 10f, vanSpeed: 0f), Dt);
        Assert.Equal(ChaseState.Stopping, car.State);
        var step = default(ChaseStep);
        for (var t = 0f; t < 11.5f; t += Dt) Assert.Equal(default, step = car.Step(F(gap: 10f, vanSpeed: 0f, carSpeed: 3f), Dt));
        for (var t = 0f; t < 1f && step == default; t += Dt) step = car.Step(F(gap: 10f, vanSpeed: 0f, carSpeed: 3f), Dt);
        Assert.True(step.GetOut);
        Assert.Equal(ChaseState.Out, car.State);
    }

    [Fact]
    public void ADeadDriverMakesTheCarRollToAStopAndTheGunmenGetOut()
    {
        var car = Chasing();
        car.Step(F(gap: 60f, driver: false), Dt);
        Assert.Equal(ChaseState.Stopping, car.State);
        Assert.Equal(default, car.Step(F(gap: 60f, driver: false, carSpeed: 18f), Dt)); // it rolls
        var step = default(ChaseStep);
        for (var speed = 18f; !step.GetOut; speed = MathF.Max(0.2f, speed - 1.5f)) step = car.Step(F(gap: 60f, driver: false, carSpeed: speed), Dt); // 1.5 m/s of coast a tick: stands at the end
        Assert.True(step.GetOut);
        Assert.Equal(ChaseState.Out, car.State);
    }

    [Fact]
    public void ACarThatIsStuckForGoodStopsAndTheCrewGetsOut()
    {
        var noTries = new ChaseData { MaxUnsticks = 0 }; // it has tried to get free already (see the wedge tests)
        var car = Chasing(noTries);
        for (var t = 0f; t < 5.8f; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt); // against a wall for under six seconds
        Assert.Equal(ChaseState.Chasing, car.State);
        car.Step(F(gap: 90f, carSpeed: 20f), Dt); // it moved: the clock is off
        for (var t = 0f; t < 5.8f; t += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
        Assert.Equal(ChaseState.Chasing, car.State);
        var more = 0f;
        for (; more < 1f && car.State == ChaseState.Chasing; more += Dt) car.Step(F(gap: 90f, carSpeed: 0.2f), Dt);
        Assert.NotEqual(ChaseState.Chasing, car.State); // six seconds: it stops, the crew gets out
        var lost = Chasing(noTries);
        for (var t = 0f; t < 6.5f && lost.State == ChaseState.Chasing; t += Dt) lost.Step(F(gap: 90f, route: false), Dt); // no way over the roads
        Assert.NotEqual(ChaseState.Chasing, lost.State);
        var near = Chasing(noTries);
        for (var t = 0f; t < 20f; t += Dt) near.Step(F(gap: 10f, vanSpeed: 25f, carSpeed: 0.2f), Dt); // right behind the van: it is not stuck, it holds its gap
        Assert.Equal(ChaseState.Chasing, near.State);
    }

    [Fact]
    public void ACarMoreThanTwoFiftyMetresFromTheVanForTwentySecondsGivesUpAndIsRemoved()
    {
        var car = Chasing();
        var step = default(ChaseStep);
        for (var t = 0f; t < 19.5f; t += Dt) Assert.Equal(default, step = car.Step(F(gap: 251f), Dt));
        car.Step(F(gap: 240f), Dt); // back in reach: the clock starts over
        for (var t = 0f; t < 19.5f; t += Dt) Assert.Equal(default, step = car.Step(F(gap: 260f), Dt));
        Assert.Equal(ChaseState.Chasing, car.State);
        for (var t = 0f; t < 1f && !step.Remove; t += Dt) step = car.Step(F(gap: 260f), Dt);
        Assert.Equal(new ChaseStep(false, true), step);
        Assert.Equal(ChaseState.GivingUp, car.State);
        car.End();
        Assert.Equal(ChaseState.Gone, car.State);
        Assert.False(car.InFight(3));
    }

    [Fact]
    public void ACarThatStopsFarFromTheVanGivesUpWhileItsCrewIsStillInside()
    {
        var car = Chasing();
        for (var t = 0f; t < 15f; t += Dt) car.Step(F(gap: 300f), Dt); // the clock of the far car runs
        car.Step(F(gap: 300f, driver: false), Dt);
        Assert.Equal(ChaseState.Stopping, car.State);
        var step = default(ChaseStep);
        for (var t = 0f; t < 6f && !step.Remove; t += Dt) step = car.Step(F(gap: 300f, driver: false, carSpeed: 15f), Dt);
        Assert.True(step.Remove);
        Assert.Equal(ChaseState.GivingUp, car.State);
    }

    [Fact]
    public void ADestroyedCarIsGoneAndItsCrewWentWithIt()
    {
        var car = Chasing();
        Assert.Equal(default, car.Step(F(gap: 50f, destroyed: true), Dt));
        Assert.Equal(ChaseState.Gone, car.State);
        var fresh = new ChaseCar(new ChaseData());
        fresh.Step(F(destroyed: true), Dt);
        Assert.Equal(ChaseState.Gone, fresh.State);
        var stopping = Chasing();
        stopping.Step(F(gap: 10f, vanSpeed: 0f), Dt);
        stopping.Step(F(gap: 10f, vanSpeed: 0f, destroyed: true), Dt);
        Assert.Equal(ChaseState.Gone, stopping.State);
    }

    [Fact]
    public void ACarIsInTheFightWhileItsCrewRidesOrAMemberOutsideLives()
    {
        var car = new ChaseCar(new ChaseData());
        Assert.True(car.InFight(0)); // Spawned
        car.Step(F(), Dt);
        Assert.True(car.InFight(4));
        car.Step(F(gap: 10f, vanSpeed: 0f), Dt);
        Assert.True(car.InFight(0)); // Stopping
        car.Step(F(gap: 10f, vanSpeed: 0f, carSpeed: 0f), Dt);
        Assert.Equal(ChaseState.Out, car.State);
        Assert.True(car.InFight(1));
        Assert.False(car.InFight(0)); // the crew is dead: it frees the slot
        car.End();
        Assert.False(car.InFight(2));
    }

    [Fact]
    public void ACarChasingTheMovingVanKeepsDrivingAtAnyDistanceUnderTheGiveUpRange()
    {
        var car = Chasing();
        for (var t = 0f; t < 60f; t += Dt) Assert.Equal(default, car.Step(F(gap: 200f, vanSpeed: 25f), Dt));
        for (var t = 0f; t < 60f; t += Dt) Assert.Equal(default, car.Step(F(gap: 14f, vanSpeed: 25f), Dt)); // right behind it at speed: no ramming, no stop
        Assert.Equal(ChaseState.Chasing, car.State);
    }

    // ---- the driving ----

    /// <summary>A car as the pilot sees it: a point that turns by its steer (yaw rate = steer x speed / radius, the radius widening with speed) and speeds up and brakes by the throttle.</summary>
    sealed class Toy
    {
        public Vector2 Pos; public float Yaw, Speed;
        public static float Radius(float speed) => 3.6f + 0.7f * MathF.Abs(speed);
        public void Step(DriveInput i, float dt)
        {
            Speed = Math.Clamp(Speed + (i.Handbrake ? -12f : i.Throttle * (i.Throttle < 0 ? 9.5f : 5.5f)) * dt, 0f, 44f);
            Yaw += -i.Steer * Speed / Radius(Speed) * dt;
            Pos += new Vector2(-MathF.Sin(Yaw), -MathF.Cos(Yaw)) * Speed * dt;
        }
    }

    [Fact]
    public void ThePilotFollowsARoadThroughABendAndHoldsItsSpeed()
    {
        var path = new RoadPath(new[] { new Vector2(0, 0), new Vector2(300, 0), new Vector2(300, 200), new Vector2(600, 200), new Vector2(600, 600) });
        var car = new Toy { Pos = new Vector2(0, 3), Yaw = RoadPath.Yaw(new Vector2(1, 0)), Speed = 15f };
        var worst = 0f;
        for (var t = 0f; t < 150f && Vector2.Distance(car.Pos, path.End) > 20f; t += Dt)
        {
            car.Step(ChasePilot.Follow(path, car.Pos, car.Yaw, car.Speed, Toy.Radius(car.Speed), 22f), Dt);
            worst = MathF.Max(worst, path.DistanceTo(car.Pos));
        }
        Assert.True(Vector2.Distance(car.Pos, path.End) <= 20f, $"stopped {Vector2.Distance(car.Pos, path.End):F0} m short");
        Assert.True(worst < 8f, $"{worst:F1} m off the road");
    }

    [Fact]
    public void ThePilotTurnsTheRightWayAndAFarPointBehindItGetsAFullLock()
    {
        var path = new RoadPath(new[] { new Vector2(0, 0), new Vector2(200, 0), new Vector2(200, 200) });
        var yaw = RoadPath.Yaw(new Vector2(1, 0)); // heading east
        var left = ChasePilot.Follow(path, new Vector2(10, 12), yaw, 10f, 10f, 10f); // heading east (+x) the right hand is +z: 12 m south of the road, so the road is on the left
        Assert.True(left.Steer < 0, "steer -1 is left: the road is to the left");
        var right = ChasePilot.Follow(path, new Vector2(10, -12), yaw, 10f, 10f, 10f); // 12 m north of it: the road is on the right
        Assert.True(right.Steer > 0, "steer +1 is right");
        var back = ChasePilot.Follow(path, new Vector2(100, 0), RoadPath.Yaw(new Vector2(-1, 0)), 10f, 10f, 10f); // facing the wrong way
        Assert.Equal(1f, MathF.Abs(back.Steer));
    }

    [Fact]
    public void ThePilotBrakesAndNeverReversesAndAnEmptyPathStops()
    {
        Assert.Equal(-1f, ChasePilot.Stop(20f).Throttle);
        Assert.False(ChasePilot.Stop(20f).Handbrake);
        Assert.True(ChasePilot.Stop(0.4f).Handbrake);
        Assert.Equal(0f, ChasePilot.Stop(0.4f).Throttle);
        Assert.Equal(1f, ChasePilot.Stop(-5f).Throttle);
        Assert.Equal(-1f, ChasePilot.Follow(RoadPath.Empty, Vector2.Zero, 0f, 15f, 10f, 20f).Throttle);
        var car = new Toy { Pos = Vector2.Zero, Yaw = 0f, Speed = 30f };
        for (var t = 0f; t < 10f; t += Dt) car.Step(ChasePilot.Stop(car.Speed), Dt);
        Assert.Equal(0f, car.Speed);
    }

    [Fact]
    public void APathKnowsHowFarAlongACarIs()
    {
        var path = new RoadPath(new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100) });
        Assert.Equal(0f, path.Project(new Vector2(-5, 3)), 0.01f);
        Assert.Equal(40f, path.Project(new Vector2(40, 7)), 0.01f);
        Assert.Equal(150f, path.Project(new Vector2(110, 50)), 0.01f);
        Assert.Equal(200f, path.Project(new Vector2(100, 500)), 0.01f);
        Assert.Equal(0f, RoadPath.Empty.Project(Vector2.One));
    }

    // ---- the crew on its seats ----

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public readonly List<GangShot> Shots = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
        public bool Clear(Vector3 from, Vector3 to) => true;
        public void Shoot(GangShot shot) => Shots.Add(shot);
    }

    sealed class Rig
    {
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Roster;
        public readonly GangConfig Rival = Game.Thieves.RivalCrew.GangOf(Game.Thieves.Gang);

        public Rig()
        {
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

    static float AngleDeg(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;

    [Fact]
    public void ARiderIsPostedOnItsSeatAndTheSeatMovesWithTheCar()
    {
        var rig = new Rig();
        var crew = rig.Crew(_ => false);
        var m = crew.AddPosted(GangMemberKind.Gunman, new Vector3(0, 0, 0), 0)!;
        for (var i = 1; i <= 30; i++)
        {
            m.MovePost(new Vector3(0, 0, -i * 0.5f), 0.3f);
            rig.Run(Dt);
        }
        Assert.Equal(new Vector3(0, 0, -15f), m.Position);
        Assert.Equal(GangMemberState.Posted, m.State);
        Assert.Equal(new Vector3(0, 0, -15f), m.PostedAt!.Value.At);
        Assert.Equal(new Vector3(0, 0, -15f), m.Home);
        var walker = crew.Add(GangMemberKind.Gunman, new Vector3(5, 0, 5), 0)!;
        walker.MovePost(Vector3.Zero, 0); // not posted: nothing moves it
        Assert.Equal(new Vector3(5, 0, 5), walker.Position);
    }

    [Fact]
    public void ARidingGunmanAimsAndFiresAtAHostilePlayerWhileTheCarMoves()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.AddPosted(GangMemberKind.Gunman, new Vector3(0, 0, 0), 0)!;
        rig.World.List.Add(new GangPlayer(1, new Vector3(30, 0, -10), true));
        for (var i = 0; i < 120; i++)
        {
            m.MovePost(new Vector3(0, 0, -i * 0.3f), 0); // the car drives on at 9 m/s
            rig.Run(Dt);
        }
        Assert.NotEmpty(rig.World.Shots);
        Assert.Contains(m.State, new[] { GangMemberState.Aim, GangMemberState.Fire }); // never leaves the seat
        Assert.True(m.PostedAt != null && m.Position == m.PostedAt.Value.At);
    }

    [Fact]
    public void TheAimErrorOfARidingGunmanCanBeSetWiderThanTheGunsOwn()
    {
        static float Spread(float? error)
        {
            var rig = new Rig();
            var crew = rig.Crew(p => p == 1);
            var m = crew.AddPosted(GangMemberKind.Gunman, Vector3.Zero, 0)!;
            m.AimErrorDeg = error;
            rig.World.List.Add(new GangPlayer(1, new Vector3(0, 0, -15), true));
            rig.Run(30f);
            var aim = new Vector3(0, 1.4f, -15f) - new Vector3(0, 1.4f, 0);
            Assert.True(rig.World.Shots.Count >= 20);
            return rig.World.Shots.Max(s => AngleDeg(s.Dir, new Vector3(0, 1.0f, -15f) - s.From));
        }
        var standing = Spread(null);
        var moving = Spread(25f);
        Assert.True(standing < Game.Thieves.RivalCrew.Gunman.Gun!.AimErrorDeg + 0.5f);
        Assert.True(moving > standing + 3f, $"{moving:F1} against {standing:F1}");
        Assert.True(Spread(0f) < 0.5f); // a zero error is a straight line
    }

    [Fact]
    public void ACrewThatGetsOutIsNoLongerPostedAndHeadsForTheGangsPostAndFights()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.AddPosted(GangMemberKind.Gunman, Vector3.Zero, 0)!;
        var driver = crew.AddPosted(GangMemberKind.Enforcer, new Vector3(1, 0, 0), 0)!;
        rig.Run(0.5f);
        var out1 = new Vector3(3, 0, 0);
        m.Unpost(out1, 1f);
        driver.Unpost(new Vector3(-3, 0, 0), 1f);
        Assert.Null(m.PostedAt);
        Assert.Equal(out1, m.Position);
        Assert.Equal(GangMemberState.Guard, m.State);
        crew.Post = new Vector3(60, 0, 0); // the van's place
        rig.Run(4f);
        Assert.True(m.Position.X > 10f, $"walked to x {m.Position.X:F1}"); // on its way: no longer held by a seat
        Assert.True(driver.Position.X > -3f);
        m.MovePost(Vector3.Zero, 0); // released: the seat no longer moves it
        Assert.True(m.Position.X > 10f);
        // and it fights a hostile player as any member of a Rival crew
        rig.World.List.Add(new GangPlayer(1, new Vector3(m.Position.X + 3f, 0, m.Position.Z - 10f), true));
        rig.Run(3f);
        Assert.NotEmpty(rig.World.Shots);
    }

    [Fact]
    public void ACalmedCrewFightsNobodyUntilItIsHitOrSeesTheirHostileAgain()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.AddPosted(GangMemberKind.Gunman, Vector3.Zero, 0)!;
        rig.World.List.Add(new GangPlayer(1, new Vector3(0, 0, -20), true));
        rig.Run(1f);
        Assert.NotNull(m.Target);
        crew.Calm();
        crew.HostileTo = _ => false;
        rig.Roster.Update(Dt);
        Assert.Null(m.Target);
        Assert.Empty(crew.Aggro);
    }
}
