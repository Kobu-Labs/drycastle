using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Thieves;

/// <summary>The Raid van, its Raid and the Raid sequence of the Thieves event (docs/events/thieves/03). State machines run Strict.</summary>
public class RaidVanTests
{
    const float Dt = 1f / 30f;
    const byte Bullet = 0, Rocket = 1, MountedGun = 2;

    static readonly ThievesFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Thieves;

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
    }

    sealed class FakeCrew : IRaidCrew
    {
        public int StillToSpawn { get; set; }
        public int Afoot { get; set; }
        public int Inside { get; set; }
    }

    static ThievesFile Cfg(Action<ThievesFile>? tune = null)
    {
        var f = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json")));
        tune?.Invoke(f);
        return f;
    }

    /// <summary>A 120 m road east along z = 0: the van starts at x = -20 and stops at x = 100.</summary>
    static RoadPath InRoad() => new(new[] { new Vector2(-20, 0), new Vector2(100, 0) });
    static RoadPath OutRoad() => new(new[] { new Vector2(100, 0), new Vector2(400, 0) });

    [Fact]
    public void Serialize_carries_the_crew_inside_and_none_in_a_wreck()
    {
        var van = Van();
        van.Crew = new FakeCrew { Inside = 8 };
        Assert.Equal(8, RaidVanRules.InsideOf(van.Serialize().Owner));
        Assert.Equal(0, RaidVanRules.InsideOf(Van().Serialize().Owner)); // no crew set: nobody sits inside
    }

    [Fact]
    public void Shipped_van_seats_only_the_driver_and_the_seat_next_to_it()
    {
        var seats = Shipped.Van.View.Seats;
        Assert.Equal(2, seats.Count);
        Assert.Equal(seats[0].Z, seats[1].Z, 0.01f); // side by side in the cab
        Assert.True(seats[0].X * seats[1].X < 0);    // one each side of the middle
        Assert.True(seats.Count <= RaidVanRules.MaxInside);
    }

    static RaidVan Van(VanConfig? cfg = null, FakeWorld? world = null, RoadPath? route = null, int slot = 0) =>
        new(cfg ?? Shipped.Van, world, route ?? InRoad(), new Vector3(-20, 0, 0), -MathF.PI / 2, slot, 1, 5) { Exit = OutRoad() };

    static void Run(RaidVan van, float seconds) { for (var t = 0f; t < seconds && van.Ended == null; t += Dt) van.Tick(Dt); }

    static void RunUntil(RaidVan van, RaidVanState state, float limitSeconds = 120)
    {
        for (var t = 0f; t < limitSeconds && van.State != state && van.Ended == null; t += Dt) van.Tick(Dt);
        Assert.Equal(state, van.State);
    }

    // ---- data ----

    [Fact]
    public void TheShippedNumbersAreTheTicketsAndTheFileRejectsNonsense()
    {
        var v = Shipped.Van; var r = Shipped.Raids;
        Assert.Equal((300f, 8, 240f), (v.Hits, v.Capacity, v.StaySeconds));
        Assert.Equal(80f, v.StopRadiusM);
        Assert.Equal((360f, 6f), (v.View.HonkHearM, v.View.HonkDb)); // the honk carries far and loud
        Assert.Equal((1, 3, 3f), (r.MinVans, r.MaxVans, r.ConvoyGapSeconds));
        Assert.True(v.Hit.ExplosiveWeight > 1);
        Assert.Equal(1f, v.Hit.HeadshotWeight); // a headshot is a body hit
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"van":{"capacity":0}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"van":{"speed_mps":5,"park_speed_mps":9}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"raids":{"min_vans":0}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"raids":{"min_vans":2,"max_vans":1}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"raids":{"max_vans":99}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"van":{"honk_min_seconds":10,"honk_max_seconds":5}}"""));
    }

    [Fact]
    public void TheCrewGrowsWithThePlayersThievesFirstUpToTheMaxima()
    {
        var r = new RaidsConfig { PlayersPerExtra = 2, MaxExtraThieves = 3, MaxExtraEnforcers = 1 };
        var b = new GangRaidCounts { GangBosses = 1, Enforcers = 2, Thieves = 5 };
        (int, int, int) Of(int players) { var c = r.CrewFor(b, players); return (c.GangBosses, c.Enforcers, c.Thieves); }
        Assert.Equal((1, 2, 5), Of(1));
        Assert.Equal((1, 2, 5), Of(2)); // not yet an extra
        Assert.Equal((1, 2, 6), Of(3));
        Assert.Equal((1, 2, 8), Of(7));
        Assert.Equal((1, 3, 8), Of(9)); // the Thieves are at their maximum: an Enforcer
        Assert.Equal((1, 3, 8), Of(40)); // and that is the maximum
        Assert.Equal(1, Shipped.Raids.CrewFor(Shipped.Gang.Raid, 0).GangBosses);
        Assert.True(Shipped.Gang.Raid.Size + Shipped.Raids.MaxExtraThieves + Shipped.Raids.MaxExtraEnforcers <= GangRules.MaxCrew);
    }

    // ---- layout and rules ----

    [Fact]
    public void TheEntityStateLayoutIsDoorStateAndLootWithDamage()
    {
        var van = Van();
        var s = van.Serialize();
        Assert.Equal(RaidVanRules.TypeId, van.TypeId);
        Assert.Equal(55, RaidVanRules.TypeId);
        Assert.Equal((byte)RaidVanState.Arriving, s.State);
        Assert.Equal(0f, RaidVanRules.DoorOf(s.Owner));
        Assert.Equal(0, RaidVanRules.LoadOf(s.Hp));
        Assert.Equal(0, RaidVanRules.StageOf(s.Hp));
        foreach (var (load, stage) in new[] { (0, 0), (8, 3), (31, 7), (1, 7) })
        {
            var hp = RaidVanRules.PackHp(load, stage);
            Assert.Equal((load, stage), (RaidVanRules.LoadOf(hp), RaidVanRules.StageOf(hp)));
        }
        Assert.Equal(0.5f, RaidVanRules.DoorOf(RaidVanRules.PackOwner(0.5f, false)), 0.01f);
        var packed = RaidVanRules.PackOwner(1f, true, 31, 9);
        Assert.Equal((1f, true, 31, 9), (RaidVanRules.DoorOf(packed), RaidVanRules.LeftOf(packed), RaidVanRules.SongOf(packed), RaidVanRules.InsideOf(packed)));
        Assert.Equal(RaidVanRules.MaxInside, RaidVanRules.InsideOf(RaidVanRules.PackOwner(0f, false, 0, 99)));
        Assert.True(RaidVanRules.LeftOf(RaidVanRules.PackOwner(1f, true)));
        Assert.False(RaidVanRules.LeftOf(RaidVanRules.PackOwner(1f, false)));
        Assert.Equal(RaidVanState.Parked, RaidVanRules.StateOf(200)); // a state this build does not know stands
        Assert.True(RaidVanRules.IsHitId(RaidVanRules.FirstHitId));
        Assert.False(RaidVanRules.IsHitId((ushort)(RaidVanRules.FirstHitId + RaidVanRules.HitIdCount)));
        Assert.False(RaidVanRules.IsHitId(GangRules.FirstHitId)); // clear of the Gang members
        Assert.True(GangRules.FirstHitId + GangRules.HitIdCount <= RaidVanRules.FirstHitId); // the members sit under the vans since the merge with wildlife
        Assert.True(RaidVanRules.FirstHitId + RaidVanRules.HitIdCount <= WildlifeRules.FirstHitId); // and the vans under the wild animals
    }

    [Fact]
    public void TheMachineEndsInLeavingOrDestroyedAndEveryStateIsReachable()
    {
        var spec = RaidVan.Spec;
        Assert.Equal(new[] { "Leaving", "Destroyed" }, spec.Terminal.OrderBy(x => x == "Destroyed"));
        Assert.Empty(spec.Unreachable);
        Assert.Equal("Arriving", spec.InitialName);
        Assert.True(spec.Allows(RaidVanState.Parked, RaidVanState.Closing)); // nobody afoot: no calling back
        Assert.True(spec.Allows(RaidVanState.Arriving, RaidVanState.Destroyed)); // shot down on the way in
        Assert.False(spec.Allows(RaidVanState.Arriving, RaidVanState.Leaving));
        Assert.False(spec.Allows(RaidVanState.Destroyed, RaidVanState.Leaving));
        Assert.False(spec.Allows(RaidVanState.Leaving, RaidVanState.Parked));
    }

    [Fact]
    public void ADoorPointIsBesideTheSideOfTheVanOnTheSideOfItsDoor()
    {
        var right = Van();
        var left = new RaidVan(Shipped.Van, null, InRoad(), new Vector3(-20, 0, 0), -MathF.PI / 2, 0, -1, 1);
        // facing east (yaw -90 deg): the right-hand side is south (+z), the left north (-z)
        Assert.True(right.DoorPoint.Z > 1.5f);
        Assert.True(left.DoorPoint.Z < -1.5f);
        var vc = Shipped.Van;
        Assert.Equal(vc.HalfWidthM + vc.DoorOutM, Math.Abs(right.DoorPoint.Z), 0.01f);
        Assert.Equal(-20f + vc.DoorAlongM, right.DoorPoint.X, 0.01f); // a little ahead of the centre (east)
    }

    // ---- driving ----

    [Fact]
    public void TheVanDrivesTheRoadBrakesForTheStopOnlyAndOpensItsDoor()
    {
        var van = Van();
        Run(van, 2);
        Assert.Equal(RaidVanState.Arriving, van.State);
        Assert.InRange(van.Speed, 5f, Shipped.Van.SpeedMps + 0.01f);
        Assert.True(van.Position.X > -20f && van.Position.Z == 0);
        var maxSpeed = 0f; var slowAtEnd = float.MaxValue;
        while (van.State == RaidVanState.Arriving) { van.Tick(Dt); maxSpeed = MathF.Max(maxSpeed, van.Speed); if (van.Position.X > 95) slowAtEnd = MathF.Min(slowAtEnd, van.Speed); }
        Assert.InRange(maxSpeed, 12.9f, 13.01f); // its speed
        Assert.True(slowAtEnd < 4f, "it brakes for the stop");
        Assert.Equal(RaidVanState.Opening, van.State);
        Assert.Equal(100f, van.Position.X, 0.1f);
        Assert.Equal(0f, van.Speed);
        Assert.Equal(-MathF.PI / 2, van.Yaw, 0.02f);
        Assert.Equal(0f, van.DoorOpen, 0.01f);
        Run(van, Shipped.Van.DoorSeconds / 2);
        Assert.InRange(van.DoorOpen, 0.4f, 0.6f);
        RunUntil(van, RaidVanState.Parked);
        Assert.Equal(1f, van.DoorOpen);
        Assert.True(van.Solid);
        Assert.False(Van().Solid); // not while driving
    }

    [Fact]
    public void TheHeightComesFromTheGroundAndASlopeTiltsIt()
    {
        var van = Van();
        van.Ground = (x, z) => x * 0.05f; // a rising road
        Run(van, 3);
        Assert.Equal(van.Position.X * 0.05f, van.Position.Y, 0.01f);
        Assert.InRange(van.Pitch, 0.03f, 0.07f); // nose up on the rise
        var hole = Van();
        hole.Ground = (x, z) => x < 0 ? float.NaN : 5f;
        Run(hole, 0.5f);
        Assert.Equal(0f, hole.Position.Y); // no ground yet: it keeps the height it had
    }

    [Fact]
    public void ALaneAroundACornerSlowsTheVanAndItStillFollowsThePath()
    {
        var path = new RoadPath(new[] { new Vector2(0, 0), new Vector2(50, 0), new Vector2(50, -50) });
        var van = new RaidVan(Shipped.Van, null, path, new Vector3(0, 0, 0), -MathF.PI / 2, 0, 1, 1);
        var slowest = float.MaxValue;
        while (van.State == RaidVanState.Arriving) { van.Tick(Dt); if (van.Position.X > 40 && van.Position.Z < -5) slowest = MathF.Min(slowest, van.Speed); }
        Assert.Equal(new Vector3(50, 0, -50).X, van.Position.X, 0.2f);
        Assert.Equal(-50f, van.Position.Z, 0.2f);
        Assert.Equal(0f, van.Yaw, 0.1f); // facing -z at the end
        Assert.True(slowest < 12f);
    }

    // ---- the leave rules ----

    static RaidVan Parked(FakeCrew crew, Action<VanConfig>? tune = null)
    {
        var v = Shipped.Van;
        var cfg = new VanConfig { SpeedMps = v.SpeedMps, ParkSpeedMps = v.ParkSpeedMps, StaySeconds = v.StaySeconds, BoardWaitSeconds = v.BoardWaitSeconds, DoorSeconds = v.DoorSeconds, Capacity = v.Capacity, HonkMinSeconds = 1000, HonkMaxSeconds = 1000, WreckSeconds = 5 };
        tune?.Invoke(cfg);
        var van = new RaidVan(cfg, null, InRoad(), new Vector3(-20, 0, 0), -MathF.PI / 2, 0, 1, 1) { Crew = crew, Exit = OutRoad() };
        RunUntil(van, RaidVanState.Parked);
        return van;
    }

    [Fact]
    public void ItLeavesWhenFullCallingTheCrewBackAndWaitingForThem()
    {
        var crew = new FakeCrew { StillToSpawn = 0, Afoot = 3 };
        var van = Parked(crew);
        Assert.True(van.LoadLoot(7));
        Run(van, 1);
        Assert.Equal(RaidVanState.Parked, van.State); // 7 of 8: it stays
        Assert.True(van.LoadLoot());
        Assert.True(van.Full);
        Assert.False(van.LoadLoot()); // full
        Run(van, Dt * 2);
        Assert.Equal(RaidVanState.Boarding, van.State);
        Run(van, 5);
        Assert.Equal(RaidVanState.Boarding, van.State); // the crew has not come back
        crew.Afoot = 0;
        Run(van, Dt * 2);
        Assert.Equal(RaidVanState.Closing, van.State);
        RunUntil(van, RaidVanState.Leaving);
        Assert.Equal(0f, van.DoorOpen);
        Run(van, 60);
        Assert.NotNull(van.Ended); // driven off at the map edge
        Assert.Equal(WorldDespawnReason.Removed, van.Ended);
        Assert.True(van.Position.X >= 399f);
    }

    [Fact]
    public void ItLeavesWhenTheStayTimeRanOutAndTheWaitForTheCrewIsCapped()
    {
        var van = Parked(new FakeCrew { StillToSpawn = 0, Afoot = 2 }, c => { c.StaySeconds = 10; c.BoardWaitSeconds = 4; });
        Run(van, 9);
        Assert.Equal(RaidVanState.Parked, van.State);
        Run(van, 1.5f);
        Assert.Equal(RaidVanState.Boarding, van.State);
        Assert.InRange(van.StayLeft, -1f, 0.01f);
        Run(van, 3f);
        Assert.Equal(RaidVanState.Boarding, van.State); // 3.5 s of 4
        Run(van, 0.7f);
        Assert.Equal(RaidVanState.Closing, van.State); // waited board_wait_seconds, no more: those still afoot are left behind
    }

    [Fact]
    public void ADeadCrewDoesNotSendItOffItStandsSolidUntilFullOrTheStayTimeRanOut()
    {
        var crew = new FakeCrew { StillToSpawn = 0, Afoot = 0 }; // every member killed
        var van = Parked(crew, c => c.StaySeconds = 20);
        Run(van, 15);
        Assert.Equal(RaidVanState.Parked, van.State);
        Assert.True(RaidVanRules.IsSolid(van.State));
        Run(van, 6);
        Assert.Equal(RaidVanState.Closing, van.State); // stay clock out, nobody afoot: straight to the door

        var full = Parked(new FakeCrew { StillToSpawn = 0, Afoot = 0 });
        Assert.True(full.LoadLoot(full.Capacity));
        Run(full, Dt * 2);
        Assert.Equal(RaidVanState.Closing, full.State); // full with nobody afoot: no calling back
    }

    [Fact]
    public void LootIsOnlyTakenWhileTheDoorIsOpenAndLootReturnAllEmptiesTheCounter()
    {
        var van = Van();
        Assert.False(van.LoadLoot()); // driving in
        var p = Parked(new FakeCrew { StillToSpawn = 1 });
        Assert.True(p.LoadLoot(3));
        Assert.Equal(3, RaidVanRules.LoadOf(p.Serialize().Hp));
        Assert.Equal(3, p.LootReturnAll());
        Assert.Equal(0, p.Load);
        Assert.Equal(0, p.LootReturnAll());
    }

    // ---- hits and the wreck ----

    [Fact]
    public void ABulletCountsOneAnExplosiveItsWeightAndAVanDestroyedWhileArrivingLiesAsAWreckThenGoes()
    {
        var cfg = Cfg(f => f.Van.WreckSeconds = 2).Van;
        var van = Van(cfg);
        var destroyed = 0;
        van.DestroyedEvent += _ => destroyed++;
        Run(van, 2);
        for (var i = 0; i < 100; i++) Assert.False(van.Hit(7, 1, false));
        Assert.Equal(100, van.HitsTaken);
        Assert.False(van.Hit(7, cfg.Hit.ExplosiveWeight, true)); // 125
        Run(van, Dt); // a tick between two explosions
        Assert.False(van.Hit(7, cfg.Hit.ExplosiveWeight, true)); // 150
        Run(van, Dt);
        Assert.True(van.Hit(8, cfg.Hit.ExplosiveWeight * 6, true)); // 300 reached
        Assert.Equal(1, destroyed);
        Assert.Equal(RaidVanState.Destroyed, van.State);
        Assert.Equal((ushort)8, van.Killer);
        Assert.False(van.Alive);
        Assert.False(van.Hit(8, 50, true)); // a wreck takes nothing
        Assert.Equal(0.35f, van.DoorOpen); // the door hangs
        Assert.True(van.Solid); // the wreck lies solid
        var at = van.Position;
        Run(van, 1);
        Assert.InRange(Vector3.Distance(at, van.Position), 0f, 3f); // it rolls to a stop
        Assert.Equal(0f, van.Speed);
        Assert.Null(van.Ended);
        Run(van, 2.2f);
        Assert.Equal(WorldDespawnReason.Expired, van.Ended); // the wreck lies wreck_seconds, then goes
    }

    [Fact]
    public void AnExplosionReachingBothCapsulesCountsOnceAndAnythingWithoutWeightCountsNothing()
    {
        var van = Van();
        Run(van, 1);
        var w = Shipped.Van.Hit.ExplosiveWeight;
        van.Hit(1, w, true);
        van.Hit(1, w, true); // the second capsule of the same blast, the same tick
        Assert.Equal(w, van.HitsTaken);
        van.Hit(2, w, true); // another player's blast at the same moment: it counts
        Assert.Equal(2 * w, van.HitsTaken);
        Assert.False(van.Hit(1, 0, false));
        Assert.False(van.Hit(1, -3, false));
        Assert.Equal(2 * w, van.HitsTaken);
        Assert.Equal(0, RaidVanRules.StageOf(Van().Serialize().Hp));
        Assert.True(RaidVanRules.StageOf(van.Serialize().Hp) > 0); // the damage stage shows
    }

    // ---- hit volume and routing ----

    [Fact]
    public void TheHitVolumeIsTwoProneCapsulesAlongTheVanAndRaysHitItFromTheSideAndFromAbove()
    {
        var van = Van();
        Run(van, 6);
        var (front, rear) = RaidVanRules.Capsules(van.HitCentre, Shipped.Van.HitOffsetM);
        Assert.Equal(RaidVanRules.HitIdOf(0, 0), front.Id);
        Assert.Equal(RaidVanRules.HitIdOf(0, 1), rear.Id);
        Assert.True(front.Feet.X > rear.Feet.X); // facing east: the front capsule is the eastern one
        Assert.All(new[] { front, rear }, t => { Assert.Equal(Stance.Prone, t.Stance); Assert.True(t.HeadGuarded); });
        var targets = new List<HitTarget> { front, rear };
        // a ray from the south side at 1.2 m height, across the middle of the van
        var origin = new Vector3(van.Position.X, 1.2f, 30f);
        var hit = HitMath.Raycast(origin, new Vector3(0, 0, -1), 100, float.PositiveInfinity, targets, 99);
        Assert.NotNull(hit);
        Assert.False(hit!.Value.Head); // a headshot on it is a body hit
        Assert.InRange(hit.Value.Distance, 28f, 30f);
        // from the front end, along its length
        var end = HitMath.Raycast(new Vector3(van.Position.X + 40, 1.0f, 0), new Vector3(-1, 0, 0), 100, float.PositiveInfinity, targets, 99);
        Assert.NotNull(end);
        Assert.InRange(end!.Value.Distance, 34f, 38f); // within the van's length: it ends about 2.8 m ahead of the centre
        // a ray high above the roof misses
        Assert.Null(HitMath.Raycast(new Vector3(van.Position.X, 4.5f, 30f), new Vector3(0, 0, -1), 100, float.PositiveInfinity, targets, 99));
    }

    sealed class Rig
    {
        public readonly ThievesFile Cfg;
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Gangs;
        public readonly RaidRoster Raids;
        public readonly List<Raid> Over = new();
        public readonly List<(Raid Raid, int Loaded)> Returned = new();
        public int Honks;

        public Rig(Action<ThievesFile>? tune = null)
        {
            Cfg = RaidVanTests.Cfg(tune);
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            RaidVanRules.Register(types);
            Entities = new WorldEntityManager(types);
            Gangs = new GangRoster(Cfg.Gang, World, Entities, 7);
            Raids = new RaidRoster(Cfg, World, Entities, Gangs, 9) { SourceOf = w => w switch { Rocket => HitSource.Explosive, MountedGun => HitSource.VehicleWeapon, Bullet => HitSource.Bullet, _ => HitSource.None } };
            Raids.RaidOver += Over.Add;
            Raids.LootReturned += (r, n) => Returned.Add((r, n));
            Raids.Honked += _ => Honks++;
        }

        public static RaidPlan PlanTo(Vector2 stop, RoadPath path, int side = 1) => new(null, new RoadPlace(0, 0), stop, true, path.YawAt(path.Length), path, side, 0, false);

        public Raid Launch(RoadPath? path = null, RoadPath? exit = null, int players = 1)
        {
            path ??= new RoadPath(new[] { new Vector2(-20, 0), new Vector2(60, 0) });
            var plan = PlanTo(path.End, path);
            var crew = Cfg.Raids.CrewFor(Cfg.Gang.Raid, players);
            return Raids.Launch(plan, exit ?? new RoadPath(new[] { path.End, path.End + new Vector2(300, 0) }), new Vector3(path.Start.X, 0, path.Start.Y), crew, p => p + new Vector3(0, 0, 12), p => new Vector3(1000, 0, 0), null)!; // the test's snap puts the crew 12 m south of the door (on its side); the map edge is far east
        }

        /// <summary>One tick as the server does it: who fights whom, the entities, the Raids, one route, then the poses.</summary>
        public void Step(uint tick = 0)
        {
            Gangs.Update(Dt);
            Entities.Tick(Dt);
            Raids.Tick(Dt);
            Gangs.ServeRoute((from, to) => new[] { from, to });
            if (tick != 0) Raids.RecordPoses(tick);
        }

        public void Run(float seconds) { for (var t = 0f; t < seconds; t += Dt) Step(); }

        public void RunUntil(Func<bool> done, float limit = 600) { for (var t = 0f; t < limit && !done(); t += Dt) Step(); Assert.True(done(), "gave up waiting"); }
    }

    [Fact]
    public void AHitOnACapsuleGoesToTheVanThroughTheRosterAndTheMatchAndStartsTheRaidFighting()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        rig.World.List.Add(new GangPlayer(4, new Vector3(0, 0, 8), true));
        var id = RaidVanRules.HitIdOf(raid.Van.Slot, 1);
        Assert.Equal(DamageResult.Damaged, rig.Raids.Hit(id, 4, Bullet, 14, KillCause.Headshot));
        Assert.Equal(1f, raid.Van.HitsTaken); // a headshot on a van is 1
        Assert.Equal(DamageResult.Damaged, rig.Raids.Hit(id, 4, Rocket, 90, KillCause.Standard));
        Assert.Equal(1f + rig.Cfg.Van.Hit.ExplosiveWeight, raid.Van.HitsTaken);
        Assert.Equal(DamageResult.Damaged, rig.Raids.Hit(id, 4, MountedGun, 20, KillCause.Standard));
        Assert.Equal(DamageResult.Ignored, rig.Raids.Hit(id, 4, SpecialWeapons.Fall, 50, KillCause.Standard)); // a fall counts nothing
        Assert.Equal(DamageResult.Ignored, rig.Raids.Hit(id, 4, Bullet, 14, KillCause.Roadkill)); // ramming it counts nothing
        Assert.Equal(DamageResult.Ignored, rig.Raids.Hit(RaidVanRules.HitIdOf(5, 0), 4, Bullet, 14, KillCause.Standard)); // no van in that slot
        Assert.Contains((ushort)4, raid.Gang.Aggro); // shooting the van is interfering with the Raid
        var match = new MatchSim(new MatchConfig(), () => 0, new List<Vector3> { Vector3.Zero }) { Vans = rig.Raids };
        Assert.Equal(DamageResult.Ignored, match.ApplyDamage(id, 4, Bullet, 14)); // no round running
        Assert.Equal(DamageResult.Ignored, new MatchSim(new MatchConfig(), () => 0, new List<Vector3> { Vector3.Zero }).ApplyDamage(id, 4, Bullet, 14));
    }

    [Fact]
    public void TheVansAreTargetsWhileAliveAndAreRewoundFromTheirOwnPoseRing()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        rig.Run(3);
        rig.Raids.RecordPoses(100);
        var x100 = raid.Van.Position.X;
        for (var t = 0u; t < 15; t++) { rig.Step(101 + t); }
        var now = new List<HitTarget>(); rig.Raids.AddTargets(now);
        Assert.Equal(2, now.Count);
        var past = new List<HitTarget>(); rig.Raids.AddTargets(past, 100);
        var offset = rig.Cfg.Van.HitOffsetM;
        Assert.Equal(x100, (past[0].Feet.X + past[1].Feet.X) / 2, 0.02f); // where it was at tick 100
        Assert.True((now[0].Feet.X + now[1].Feet.X) / 2 > x100 + 1f);
        Assert.Equal(2 * offset, Vector3.Distance(past[0].Feet, past[1].Feet), 0.01f);
        raid.Van.Hit(1, 1000, false);
        var dead = new List<HitTarget>(); rig.Raids.AddTargets(dead);
        Assert.Empty(dead); // a wreck is no target
    }

    // ---- the Raid: crew, boarding, flight ----

    [Fact]
    public void TheCrewGetsOutOneByOneAtTheSideDoorWhenItOpens()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Opening);
        Assert.Equal(1, raid.Gang.Count); // the first steps out as the door starts to open
        Assert.Equal(GangMemberKind.Enforcer, raid.Gang.Members[0].Kind);
        var door = raid.Van.DoorPoint;
        Assert.Equal(door.Z + 12, raid.Gang.Members[0].Position.Z, 0.01f); // at the door point (through the snap of the test)
        Assert.Equal(7, raid.StillToSpawn);
        rig.Run(rig.Cfg.Van.CrewStaggerSeconds * 2.5f);
        Assert.Equal(3, raid.Gang.Count); // one every stagger seconds: at 0.8 s and 1.6 s, not yet at 2.4 s
        rig.Run(10);
        Assert.Equal(8, raid.Gang.Count);
        Assert.Equal(0, raid.StillToSpawn);
        Assert.Equal(new[] { 1, 2, 5 }, new[] { GangMemberKind.GangBoss, GangMemberKind.Enforcer, GangMemberKind.Thief }.Select(k => raid.Gang.Members.Count(m => m.Kind == k)));
        Assert.Equal(GangMemberKind.GangBoss, raid.Gang.Members[1].Kind); // the Gang boss is the second out
        Assert.Equal(door, raid.Gang.Post);
        Assert.Equal(8, raid.Gang.Members.Select(m => m.HitId).Distinct().Count());
    }

    [Fact]
    public void AHeistsAssaultIsARivalCrewRaidThatHeadsForItsPostStaysFightsOnByItsWreckAndOnlyItsJobRemovesIt()
    {
        var rig = new Rig();
        var path = new RoadPath(new[] { new Vector2(-20, 0), new Vector2(60, 0) });
        var post = new Vector3(60, 0, 40);
        var gang = rig.Gangs.NewGang(post, rig.Cfg.Gang, GangCrew.RivalCrew);
        var stays = rig.Cfg.Van.Copy();
        stays.StaySeconds = float.PositiveInfinity;
        var assault = rig.Raids.Launch(Rig.PlanTo(path.End, path), new RoadPath(new[] { path.End, path.End + new Vector2(300, 0) }), new Vector3(-20, 0, 0),
            new[] { GangMemberKind.Enforcer, GangMemberKind.Enforcer }, gang, stays, null, p => new Vector3(1000, 0, 0), null, post)!;
        Assert.Same(gang, assault.Gang);
        Assert.Equal(0, rig.Raids.Active); // the Thieves' wave does not wait for it
        Assert.Empty(rig.Raids.UsedStops);
        rig.RunUntil(() => assault.Gang.Count == 2);
        Assert.Equal(post, assault.Gang.Post); // out of the van, the crew heads for the objective, not the door
        rig.Run(400);
        Assert.Equal(RaidVanState.Parked, assault.Van.State); // it never drives off
        assault.Van.Hit(1, 1000, false);
        rig.Run(1);
        Assert.DoesNotContain(assault.Gang.Members, m => m.State == GangMemberState.Flee); // a wreck does not send an Assault's crew off
        rig.Raids.Clear(); // the Thieves event's end
        Assert.Contains(assault, rig.Raids.Raids);
        rig.Raids.Remove(assault); // its Job ended
        Assert.Empty(rig.Raids.Raids);
        Assert.Equal(0, assault.Gang.Count);
        rig.Raids.Remove(assault); // twice does nothing
    }

    static Raid SendAssault(Rig rig, RoadPath path, Vector3 post)
    {
        var stays = rig.Cfg.Van.Copy();
        stays.StaySeconds = float.PositiveInfinity; // as RaidLauncher.LaunchAssault: it never drives off
        return rig.Raids.Launch(Rig.PlanTo(path.End, path), new RoadPath(new[] { path.End, path.End + new Vector2(300, 0) }), new Vector3(path.Start.X, 0, path.Start.Y),
            new[] { GangMemberKind.Enforcer, GangMemberKind.Enforcer }, rig.Gangs.NewGang(post, rig.Cfg.Gang, GangCrew.RivalCrew), stays, null, p => new Vector3(1000, 0, 0), null, post)!;
    }

    [Fact]
    public void AnAssaultWhoseCrewIsDeadDoesNotCountBesideItsStandingVanAndIsTakenOutOnceTheBodiesAreGone()
    {
        var rig = new Rig();
        var assaults = new HeistAssaults(1);
        var a = SendAssault(rig, new RoadPath(new[] { new Vector2(-20, 0), new Vector2(60, 0) }), new Vector3(60, 0, 40));
        assaults.Add(a);
        Assert.True(HeistAssaults.InFight(a)); // the crew is still in the van
        Assert.False(assaults.MaySend);
        rig.RunUntil(() => a.StillToSpawn == 0);
        Assert.False(assaults.MaySend);
        foreach (var m in a.Gang.Members) m.Kill(1);
        rig.Step();
        Assert.False(a.Over); // the van stands on: it never drives off
        Assert.False(HeistAssaults.InFight(a));
        Assert.True(assaults.MaySend); // a dead crew beside a standing van holds no place under the cap
        Assert.Empty(assaults.TakeSpent()); // the bodies lie their time first
        rig.RunUntil(() => a.Gang.Count == 0);
        var spent = assaults.TakeSpent();
        Assert.Equal(new[] { a }, spent);
        Assert.Empty(assaults.Raids);
        Assert.Equal(new[] { a.Plan.StopAt }, assaults.Stops); // the next still parks away from where it stood
        foreach (var r in spent) rig.Raids.Remove(r);
        Assert.Empty(rig.Raids.Raids); // its van slot is free for the Thieves and the next Assault
        Assert.Equal(1, assaults.Sent);
    }

    [Fact]
    public void AHitOnAnAssaultVanEarnsNoPartInTheThievesEvent()
    {
        var rig = new Rig();
        var thieves = rig.Launch();
        var assault = SendAssault(rig, new RoadPath(new[] { new Vector2(-20, 50), new Vector2(60, 50) }), new Vector3(60, 0, 90));
        var credit = new ThievesCredit();
        var vans = new CreditedVanHits(rig.Raids, credit, id => rig.Raids.RaidOf(id)?.Gang.Crew != GangCrew.RivalCrew); // as ThievesPayout
        Assert.Equal(DamageResult.Damaged, vans.Hit(RaidVanRules.HitIdOf(assault.Van.Slot, 0), 4, Bullet, 14, KillCause.Standard));
        Assert.Empty(credit.Credited); // the hit lands, it is not the Thieves'
        Assert.Equal(DamageResult.Damaged, vans.Hit(RaidVanRules.HitIdOf(thieves.Van.Slot, 1), 5, Bullet, 14, KillCause.Standard));
        Assert.Equal(new ushort[] { 5 }, credit.Credited);
        Assert.Same(assault, rig.Raids.RaidOf(RaidVanRules.HitIdOf(assault.Van.Slot, 1)));
        Assert.Null(rig.Raids.RaidOf(GangRules.FirstHitId));
    }

    [Fact]
    public void AVanPutAtItsStopWithNoWayInOpensAtOnceAndTheCrewComesOutAtItsDoor()
    {
        var rig = new Rig();
        var stop = new Vector2(40, 10);
        var plan = new RaidPlan(null, new RoadPlace(0, 0), stop, true, -MathF.PI / 2, RoadPath.Empty, 1, 0, true);
        var raid = rig.Raids.Launch(plan, new RoadPath(new[] { stop, stop + new Vector2(300, 0) }), new Vector3(stop.X, 0, stop.Y), rig.Cfg.Raids.CrewFor(rig.Cfg.Gang.Raid, 1), null, p => new Vector3(1000, 0, 0), null)!;
        Assert.Equal(RaidVanState.Opening, raid.Van.State);
        Assert.Equal(new Vector3(40, 0, 10), raid.Van.Position);
        rig.Step();
        Assert.Equal(1, raid.Gang.Count);
        Assert.Equal(raid.Van.DoorPoint, raid.Gang.Post);
        Assert.InRange(Vector3.Distance(raid.Gang.Members[0].Position, raid.Van.DoorPoint), 0f, 0.5f);
        rig.RunUntil(() => raid.Van.State == RaidVanState.Leaving);
        rig.RunUntil(() => raid.Over);
        Assert.True(raid.Van.Position.X > 300f);
    }

    [Fact]
    public void ACrewOfMorePlayersIsBigger()
    {
        var rig = new Rig();
        var raid = rig.Launch(players: 9);
        rig.RunUntil(() => raid.StillToSpawn == 0 && raid.Van.State == RaidVanState.Parked);
        Assert.Equal(1 + 3 + 8, raid.Gang.Count);
    }

    [Fact]
    public void AFullVanCallsTheCrewBackAndTheyBoardOneByOneThenItDrivesOffAndTheRaidIsOver()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Parked && raid.StillToSpawn == 0);
        raid.Van.LoadLoot(8);
        rig.Step();
        Assert.Equal(RaidVanState.Boarding, raid.Van.State);
        Assert.All(raid.Gang.Members, m => Assert.Equal(GangMemberState.Board, m.State));
        Assert.Equal(0, raid.StillToSpawn);
        rig.RunUntil(() => raid.Van.State == RaidVanState.Closing);
        Assert.Equal(8, raid.Boarded); // all came back
        Assert.Equal(0, raid.Gang.Count); // and are gone into the van
        Assert.False(raid.Over);
        rig.RunUntil(() => raid.Over);
        Assert.Equal(WorldDespawnReason.Removed, raid.Van.Ended);
        Assert.Equal(new[] { raid }, rig.Over);
        Assert.Empty(rig.Returned); // a van that drove off keeps its Loot: gone until the round resets
        Assert.Equal(0, rig.Entities.OfType(GangRules.TypeId).Count);
        rig.Run(1);
        Assert.Empty(rig.Raids.Raids); // forgotten once its van and crew are gone
        Assert.Equal(0, rig.Gangs.Gangs.Count);
    }

    [Fact]
    public void ACrewMemberKilledOnTheWayBackIsNotWaitedForAndThoseTheVanDidNotWaitForFlee()
    {
        var rig = new Rig(f => f.Van.BoardWaitSeconds = 0.6f); // the crew stands 12 m from the door: 2 s at a run, so not all can make it
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Parked && raid.StillToSpawn == 0);
        raid.Gang.Members[3].Kill(2);
        raid.Van.LoadLoot(8);
        rig.RunUntil(() => raid.Van.State == RaidVanState.Closing);
        Assert.True(raid.Boarded < 7, "the wait was cut short");
        rig.Step();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Leaving);
        Assert.Contains(raid.Gang.Members, m => m.Alive && m.State == GangMemberState.Flee); // left behind: runs for the edge
        Assert.DoesNotContain(raid.Gang.Members, m => m.Alive && m.State == GangMemberState.Board);
    }

    [Fact]
    public void AllTheCrewBoardsWhenTheWaitIsLongEnoughAndTheDeadAreNotWaitedFor()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Parked && raid.StillToSpawn == 0);
        raid.Gang.Members[3].Kill(2);
        raid.Van.LoadLoot(8);
        rig.RunUntil(() => raid.Van.State == RaidVanState.Closing);
        Assert.Equal(7, raid.Boarded);
        Assert.Equal(1, raid.Gang.Count); // only the corpse lies on
        rig.RunUntil(() => raid.Van.State == RaidVanState.Leaving);
        Assert.DoesNotContain(raid.Gang.Members, m => m.State == GangMemberState.Flee);
    }

    [Fact]
    public void AVanDestroyedWhileDrivingInStillEndsTheRaidAndTheCrewThatNeverCameOutIsGone()
    {
        var rig = new Rig(f => f.Van.Hits = 20);
        var raid = rig.Launch();
        rig.Run(1);
        Assert.Equal(RaidVanState.Arriving, raid.Van.State);
        for (var i = 0; i < 19; i++) rig.Raids.Hit(RaidVanRules.HitIdOf(raid.Van.Slot, 0), 3, Bullet, 14, KillCause.Standard);
        Assert.False(raid.Over);
        Assert.Equal(DamageResult.Killed, rig.Raids.Hit(RaidVanRules.HitIdOf(raid.Van.Slot, 1), 3, Bullet, 14, KillCause.Standard));
        rig.Step();
        Assert.True(raid.Over);
        Assert.Equal(new[] { raid }, rig.Over);
        Assert.Equal(0, raid.Gang.Count); // nobody was out yet
        Assert.Equal(0, raid.StillToSpawn); // and nobody will be
        Assert.Equal(0, rig.Raids.Active);
        Assert.Equal(new[] { (raid, 0) }, rig.Returned);
        rig.Run(rig.Cfg.Van.WreckSeconds + 1);
        Assert.Empty(rig.Raids.Raids); // the wreck goes, and the Raid is forgotten
        Assert.Equal(0, rig.Entities.Count);
    }

    [Fact]
    public void ADestroyedParkedVanSendsTheCrewFleeingAndReturnsTheLoadedLoot()
    {
        var rig = new Rig(f => f.Van.Hits = 20);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.State == RaidVanState.Parked && raid.StillToSpawn == 0);
        raid.Van.LoadLoot(5);
        for (var i = 0; i < 20; i++) rig.Raids.Hit(RaidVanRules.HitIdOf(raid.Van.Slot, 0), 3, Bullet, 14, KillCause.Standard);
        Assert.Equal(RaidVanState.Destroyed, raid.Van.State);
        Assert.Equal(new[] { (raid, 5) }, rig.Returned); // the hook for the Loot: everything loaded goes back
        Assert.Equal(0, raid.Van.Load);
        Assert.All(raid.Gang.Members, m => Assert.Equal(GangMemberState.Flee, m.State));
        Assert.Empty(raid.Gang.Aggro);
        rig.Step();
        Assert.True(raid.Over);
    }

    [Fact]
    public void ARaidThatIsClearedLeavesNothingInTheWorld()
    {
        var rig = new Rig();
        var a = rig.Launch();
        var b = rig.Launch(new RoadPath(new[] { new Vector2(-20, 50), new Vector2(60, 50) }));
        Assert.NotEqual(a.Van.Slot, b.Van.Slot);
        Assert.Equal(2, rig.Raids.Active);
        Assert.Equal(2, rig.Raids.UsedStops.Count);
        rig.Run(12);
        rig.Raids.Clear();
        Assert.Equal(0, rig.Entities.Count);
        Assert.Equal(0, rig.Gangs.MemberCount);
        Assert.Empty(rig.Raids.Raids);
        Assert.Empty(rig.Raids.UsedStops);
    }

    [Fact]
    public void AVanHonksAtRandomIntervalsWithNobodyNear()
    {
        var rig = new Rig(f => { f.Van.HonkMinSeconds = 5; f.Van.HonkMaxSeconds = 5; });
        rig.Launch();
        rig.Run(4.8f);
        Assert.Equal(0, rig.Honks); // not yet the random time
        rig.Run(0.5f);
        Assert.Equal(1, rig.Honks);
        rig.Run(5f);
        Assert.Equal(2, rig.Honks);
    }

    [Fact]
    public void AVanHonksWhenAPlayerIsWithinReachButNotMoreOftenThanTheGap()
    {
        var rig = new Rig(f => { f.Van.HonkMinSeconds = 1000; f.Van.HonkMaxSeconds = 1000; f.Van.HonkNearGapSeconds = 3; });
        var raid = rig.Launch();
        rig.Run(1);
        Assert.Equal(0, rig.Honks);
        rig.World.List.Add(new GangPlayer(1, raid.Van.Position + new Vector3(10, 0, 0), true));
        rig.Step();
        Assert.Equal(1, rig.Honks); // within honk_near_m
        rig.Run(2.5f);
        Assert.Equal(1, rig.Honks); // not more often than the gap
        rig.Run(1f);
        Assert.Equal(2, rig.Honks);
        rig.World.List.Clear();
        rig.World.List.Add(new GangPlayer(1, raid.Van.Position + new Vector3(90, 0, 0), true));
        rig.Run(0.2f);
        rig.World.List[0] = new GangPlayer(1, raid.Van.Position + new Vector3(90, 0, 0), true);
        var before = rig.Honks;
        rig.Run(4f);
        Assert.Equal(before, rig.Honks); // too far
    }

    // ---- the members' Board state ----

    [Fact]
    public void AMemberCalledBackRunsToTheDoorEndsThereAndNeverFights()
    {
        var rig = new Rig();
        var gang = rig.Gangs.NewGang(Vector3.Zero);
        var thief = gang.Add(GangMemberKind.Thief, Vector3.Zero, 0)!;
        var boarded = 0;
        gang.Boarded += _ => boarded++;
        rig.World.List.Add(new GangPlayer(1, new Vector3(2, 0, 0), true));
        gang.NoteHit(1);
        rig.Step(); // the Thief has free hands: it joins the fight
        Assert.Equal(GangMemberState.Chase, thief.State);
        gang.BoardAll(new Vector3(30, 0, 0));
        Assert.Equal(GangMemberState.Board, thief.State);
        Assert.False(thief.CanFight);
        Assert.Empty(gang.Aggro);
        rig.Run(0.2f);
        Assert.NotEqual(GangMemberState.Chase, thief.State); // a player at its elbow changes nothing
        rig.RunUntil(() => gang.Count == 0, 30);
        Assert.Equal(1, boarded);
        Assert.Equal(GangMemberState.Board, thief.State);
        Assert.Equal(WorldDespawnReason.Removed, thief.Ended);
        Assert.Equal(30f, thief.Position.X, rig.Cfg.Gang.BoardReachM + 0.1f);
    }

    [Fact]
    public void ABoardingMemberCanBeKilledOrToldToFleeButTheDeadAndTheFleeingAreNotCalled()
    {
        var rig = new Rig();
        var gang = rig.Gangs.NewGang(Vector3.Zero);
        var a = gang.Add(GangMemberKind.Thief, Vector3.Zero, 0)!;
        var b = gang.Add(GangMemberKind.Enforcer, Vector3.Zero, 0)!;
        var c = gang.Add(GangMemberKind.Thief, Vector3.Zero, 0)!;
        c.Kill(1);
        b.Flee(new Vector3(500, 0, 0));
        gang.BoardAll(new Vector3(100, 0, 0));
        Assert.Equal(GangMemberState.Board, a.State);
        Assert.Equal(GangMemberState.Dead, c.State); // the dead lie
        Assert.Equal(GangMemberState.Flee, b.State); // a fleeing one keeps running
        Assert.Equal(1, gang.Afoot);
        a.Flee(new Vector3(500, 0, 0)); // the van was destroyed meanwhile
        Assert.Equal(GangMemberState.Flee, a.State);
        Assert.Equal(0, gang.Afoot);
        Assert.Equal(GangMemberState.Board, GangRules.StateOf((byte)GangMemberState.Board));
    }

    // ---- the pushing out ----

    [Fact]
    public void ABodyInsideTheBoxIsPutOutThroughTheNearestSideAndOneOutsideStays()
    {
        var van = new Vector3(100, 0, 0);
        var yaw = -MathF.PI / 2; // facing east: the long side runs east-west
        var inside = new Vector3(101, 0, 0.5f);
        var to = RaidVanRules.PushOut(van, yaw, 1.1f, 2.8f, inside, 0.7f);
        Assert.Equal(inside.X, to.X, 0.01f);
        Assert.Equal(1.8f, to.Z, 0.01f); // out through the south side, 0.7 m past it
        var north = RaidVanRules.PushOut(van, yaw, 1.1f, 2.8f, new Vector3(100, 0, -0.4f), 0.7f);
        Assert.Equal(-1.8f, north.Z, 0.01f);
        var end = RaidVanRules.PushOut(van, yaw, 1.1f, 2.8f, new Vector3(102.6f, 0, 0.1f), 0.7f);
        Assert.Equal(103.5f, end.X, 0.01f); // nearer the front end than a side
        var outside = new Vector3(100, 0, 5);
        Assert.Equal(outside, RaidVanRules.PushOut(van, yaw, 1.1f, 2.8f, outside, 0.7f));
    }

    // ---- the effect ----

    [Fact]
    public void AHonkEventRoundTrips()
    {
        var m = new ThievesFxMsg(ThievesFxKind.Honk, new Vector3(12.5f, 3.25f, -80f), 77);
        var b = ThievesFxCodec.Encode(m);
        Assert.Equal(ThievesFxCodec.Bytes, b.Length);
        Assert.Equal(m, ThievesFxCodec.Decode(b));
        Assert.Null(ThievesFxCodec.Decode(new byte[3]));
        Assert.Null(ThievesFxCodec.Decode(new byte[ThievesFxCodec.Bytes].Select((_, i) => i == 0 ? (byte)99 : (byte)0).ToArray()));
        Assert.Equal(232, (int)EventKind.ThievesFx);
    }

    // ---- the convoy ----

    static RaidsConfig Convoy() => new() { MinVans = 1, MaxVans = 3, ConvoyGapSeconds = 3 };

    /// <summary>A generator that always picks <paramref name="n"/> vans.</summary>
    sealed class FixedVans(int n) : Random { public override int Next(int min, int max) => n; }
    static Random Vans(int n) => new FixedVans(n);

    [Fact]
    public void TheWaveHasARandomNumberOfVansFromTheMinimumToTheMaximum()
    {
        var c = Convoy();
        var seen = new HashSet<int>();
        var rng = new Random(7);
        for (var i = 0; i < 200; i++) seen.Add(c.VansFor(rng));
        Assert.Equal(new[] { 1, 2, 3 }, seen.OrderBy(x => x));
        Assert.Equal(2, new RaidsConfig { MinVans = 2, MaxVans = 2 }.VansFor(new Random(1)));
    }

    [Fact]
    public void TheFirstVanComesAtOnceTheOthersOneConvoyGapAfterTheOneBeforeAndNoMore()
    {
        var e = new ThievesEvent();
        Assert.Equal(ThievesStep.None, e.Tick(Dt, 0)); // idle
        Assert.True(e.Begin(Convoy(), Vans(3)));
        Assert.False(e.Begin(Convoy(), Vans(3)));
        Assert.Equal(3, e.Total);
        Assert.Equal(ThievesStep.Launch, e.Tick(Dt, 0)); // at once
        Assert.Equal(1, e.Launched);
        var t = 0f;
        for (; e.Tick(Dt, 1) != ThievesStep.Launch; t += Dt) { }
        Assert.InRange(t, 2.9f, 3.1f);
        t = 0f;
        for (; e.Tick(Dt, 2) != ThievesStep.Launch; t += Dt) { }
        Assert.InRange(t, 2.9f, 3.1f);
        Assert.Equal(3, e.Launched);
        for (var i = 0; i < 30 * 600; i++) Assert.Equal(ThievesStep.None, e.Tick(Dt, 3)); // the wave is out: no fourth van however long it takes
    }

    [Fact]
    public void TheEventIsWonWhenEveryVanOfTheWaveIsGoneNotBeforeTheLastIsLaunched()
    {
        var e = new ThievesEvent();
        e.Begin(Convoy(), Vans(2));
        Assert.Equal(ThievesStep.Launch, e.Tick(Dt, 0));
        Assert.Equal(ThievesStep.None, e.Tick(Dt, 0)); // the first van is gone already but the second is still to come
        var t = 0f;
        for (; e.Tick(Dt, 0) != ThievesStep.Launch; t += Dt) { }
        Assert.Equal(2, e.Launched);
        Assert.Equal(ThievesStep.None, e.Tick(Dt, 1));
        Assert.Equal(ThievesStep.Won, e.Tick(Dt, 0));
        Assert.True(e.Finish());
        Assert.Equal(ThievesPhase.Over, e.Phase);
        Assert.Equal(ThievesStep.None, e.Tick(Dt, 0));
    }

    [Fact]
    public void AVanThatCouldNotBePlacedShrinksTheWaveAndAWaveOfNoneIsWonAtOnce()
    {
        var e = new ThievesEvent();
        e.Begin(Convoy(), Vans(3));
        Assert.Equal(ThievesStep.Launch, e.Tick(Dt, 0));
        Assert.Equal(ThievesStep.None, e.Tick(Dt, 1));
        var t = 0f;
        for (; e.Tick(Dt, 1) != ThievesStep.Launch; t += Dt) { }
        e.Shrink(); // the second could not find a stop of its own: the wave is one van
        Assert.Equal((1, 1), (e.Launched, e.Total));
        for (var i = 0; i < 30 * 20; i++) Assert.Equal(ThievesStep.None, e.Tick(Dt, 1));
        Assert.Equal(ThievesStep.Won, e.Tick(Dt, 0));
        e.Finish();
        e.Begin(Convoy(), Vans(1));
        Assert.Equal(ThievesStep.Launch, e.Tick(Dt, 0));
        e.Shrink(); // not even the first
        Assert.Equal(ThievesStep.Won, e.Tick(Dt, 0)); // the event cannot hang
    }

    [Fact]
    public void TheWaveComesAndGoesByItselfWithNobodyNearAndTheEventEndsWon()
    {
        var rig = new Rig(f => { f.Van.StaySeconds = 20; f.Van.BoardWaitSeconds = 10; });
        var ev = new ThievesEvent();
        ev.Begin(rig.Cfg.Raids, Vans(3));
        var vans = new List<RaidVan>();
        var won = false;
        for (var i = 0; i < 30 * 900 && !won; i++)
        {
            rig.Step();
            switch (ev.Tick(Dt, rig.Raids.Active))
            {
                case ThievesStep.Launch:
                    var n = vans.Count; // the same road in, a stop of its own each
                    var raid = rig.Launch(new RoadPath(new[] { new Vector2(-20, 0), new Vector2(60 + 30 * n, 0) }), new RoadPath(new[] { new Vector2(60 + 30 * n, 0), new Vector2(360, 0) }));
                    vans.Add(raid.Van);
                    break;
                case ThievesStep.Won: won = true; break;
            }
        }
        Assert.True(won, "the event was not won");
        Assert.Equal(3, vans.Count);
        Assert.All(vans, v => { Assert.NotNull(v.Ended); Assert.Equal(RaidVanState.Leaving, v.State); }); // every van drove off by itself
        Assert.Equal(3, rig.Over.Count);
        Assert.Equal(0, rig.Raids.Active);
        Assert.True(ev.Finish());
    }

    [Fact]
    public void AVanThatIsShotUpStillLetsTheEventBeWon()
    {
        var rig = new Rig(f => { f.Van.Hits = 10; });
        var ev = new ThievesEvent();
        ev.Begin(rig.Cfg.Raids, Vans(1));
        Raid? raid = null;
        var won = false;
        for (var i = 0; i < 30 * 200 && !won; i++)
        {
            rig.Step();
            switch (ev.Tick(Dt, rig.Raids.Active))
            {
                case ThievesStep.Launch: raid = rig.Launch(); break;
                case ThievesStep.Won: won = true; break;
            }
            if (raid != null && raid.Van.Alive && raid.Van.State == RaidVanState.Arriving && i % 30 == 5)
                for (var h = 0; h < 10; h++) rig.Raids.Hit(RaidVanRules.HitIdOf(raid.Van.Slot, 0), 3, Bullet, 14, KillCause.Standard);
        }
        Assert.True(won);
        Assert.Equal(RaidVanState.Destroyed, raid!.Van.State);
    }
}
