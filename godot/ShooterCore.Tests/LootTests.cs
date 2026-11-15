using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Loot and tires of the Thieves event (docs/events/thieves/04): the target registry, the Thief's stealing states, lying Loot and its return, the van's count, the tire target and the wheel bits, the generic climb (with a fake target). State machines run Strict.</summary>
public class LootTests
{
    internal const float Dt = 1f / 30f;
    const byte Bullet = 0;

    internal static ThievesFile Cfg(Action<ThievesFile>? tune = null)
    {
        var f = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json")));
        f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 1 };
        tune?.Invoke(f);
        return f;
    }

    internal sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
    }

    /// <summary>Cars as the server's adapter lists them: the wheel bits are kept here, as the lifecycle keeps them there.</summary>
    internal sealed class FakeCars : ITireCars
    {
        public readonly Dictionary<int, TireCar> Cars_ = new();
        public FakeCars Add(int id, float x, float z, bool atRest = true) { Cars_[id] = new TireCar(id, new Vector3(x, 0, z), 0, atRest, 0); return this; }
        public IEnumerable<TireCar> Cars() => Cars_.Values.ToList();
        public bool TryGet(int carId, out TireCar car) => Cars_.TryGetValue(carId, out car);
        public void SetWheel(int carId, int wheel, bool off)
        {
            var c = Cars_[carId];
            Cars_[carId] = c with { WheelsOff = (byte)(off ? c.WheelsOff | (1 << wheel) : c.WheelsOff & ~(1 << wheel)) };
        }
        public void RestoreAll() { foreach (var id in Cars_.Keys.ToList()) Cars_[id] = Cars_[id] with { WheelsOff = 0 }; }
        public byte Off(int id) => Cars_[id].WheelsOff;
        public void Drive(int id) => Cars_[id] = Cars_[id] with { AtRest = false };
    }

    /// <summary>A target kind with a climb: one dish at the top of a wall, its base on the ground below.</summary>
    internal sealed class FakeClimbSource : IStealSource
    {
        public readonly Vector3 Base, Top;
        public bool Gone;
        public int Taken, Returned;
        public FakeClimbSource(Vector3 b, Vector3 t) { Base = b; Top = t; }
        public LootKind Kind => LootKind.Dish;
        StealTarget Target => new(LootKind.Dish, 1, Top, Base, Top);
        public void FreeNear(Vector3 near, float radius, List<StealTarget> into) { if (!Gone && Vector3.Distance(new Vector3(near.X, 0, near.Z), new Vector3(Base.X, 0, Base.Z)) <= radius) into.Add(Target); }
        public void Places(List<Vector2> into) { if (!Gone) into.Add(new Vector2(Base.X, Base.Z)); }
        public bool TryFind(int id, out StealTarget target) { target = Target; return !Gone && id == 1; }
        public void Take(int id) { Gone = true; Taken++; }
        public void Return(int id) { Gone = false; Returned++; }
        public void Reset() => Gone = false;
    }

    /// <summary>A kind that does not lie when dropped and hears where its carrier is (a pet, as ticket 05 will have it).</summary>
    internal sealed class FakePetSource : IStealSource
    {
        public bool Gone;
        public int Returned;
        public readonly List<(Vector3 At, float Yaw)> Carry = new();
        public LootKind Kind => LootKind.Pet;
        StealTarget Target => new(LootKind.Pet, 7, new Vector3(30, 0, 12), new Vector3(30, 0, 14));
        public void FreeNear(Vector3 near, float radius, List<StealTarget> into) { if (!Gone) into.Add(Target); }
        public void Places(List<Vector2> into) { if (!Gone) into.Add(new Vector2(30, 12)); }
        public bool TryFind(int id, out StealTarget target) { target = Target; return !Gone && id == 7; }
        public void Take(int id) => Gone = true;
        public void Return(int id) { Gone = false; Returned++; }
        public void Reset() => Gone = false;
        public bool LiesWhenDropped => false;
        public void Carried(int id, Vector3 carrierAt, float carrierYaw) => Carry.Add((carrierAt, carrierYaw));
    }

    internal sealed class Rig
    {
        public readonly ThievesFile Cfg;
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Gangs;
        public readonly RaidRoster Raids;
        public readonly LootRegistry Registry = new();
        public readonly LootBook Book;
        public readonly FakeCars Cars = new();
        public readonly List<Raid> Over = new();

        public Rig(Action<ThievesFile>? tune = null, bool tires = true)
        {
            Cfg = LootTests.Cfg(tune);
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            RaidVanRules.Register(types);
            LootRules.Register(types);
            Entities = new WorldEntityManager(types);
            Gangs = new GangRoster(Cfg.Gang, World, Entities, 7);
            Raids = new RaidRoster(Cfg, World, Entities, Gangs, 9);
            Book = new LootBook(Cfg.Loot, Registry, Entities, 3);
            if (tires) Registry.Register(new TireSource(Cfg.Loot.Tire, Cars));
            Raids.Loot = Book;
            Raids.RaidOver += Over.Add;
        }

        public Raid Launch()
        {
            var path = new RoadPath(new[] { new Vector2(-20, 0), new Vector2(60, 0) });
            var plan = new RaidPlan(null, new RoadPlace(0, 0), path.End, true, path.YawAt(path.Length), path, 1, 0, false);
            var crew = Cfg.Raids.CrewFor(Cfg.Gang.Raid, 1);
            return Raids.Launch(plan, new RoadPath(new[] { path.End, path.End + new Vector2(300, 0) }), new Vector3(path.Start.X, 0, path.Start.Y), crew, p => p + new Vector3(0, 0, 8), p => new Vector3(1000, 0, 0), null)!;
        }

        public void Step()
        {
            Gangs.Update();
            Entities.Tick(Dt);
            Raids.Tick(Dt);
            Gangs.ServeRoute((from, to) => new[] { from, to });
        }

        public void Run(float seconds) { for (var t = 0f; t < seconds; t += Dt) Step(); }

        public void RunUntil(Func<bool> done, float limit = 300) { for (var t = 0f; t < limit && !done(); t += Dt) Step(); Assert.True(done(), "gave up waiting"); }

        /// <summary>Runs until the van's door is open and the crew's Thief is out.</summary>
        public GangMember ThiefOut(Raid raid)
        {
            RunUntil(() => raid.Gang.Members.Any(m => m.Kind == GangMemberKind.Thief));
            return raid.Gang.Members.First(m => m.Kind == GangMemberKind.Thief);
        }
    }

    // ---- data and layout ----

    [Fact]
    public void ShippedNumbersAndTheLayoutOfTheLootEntityAndTheVehicleRecord()
    {
        var l = Cfg().Loot;
        Assert.Equal((24, 1f), (l.MaxLying, l.ReturnSeconds));
        Assert.All(LootKinds.All, k => Assert.True(l.Of(k).Weight > 0 && l.Of(k).StealSeconds > 0));
        Assert.Equal(new[] { "car" }, l.Tire.VehicleIds);
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"max_lying":0}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"tire":{"steal_seconds":0}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"pet":{"weight":-1}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"tire":{"wheel_side_m":2,"work_side_m":1}}}"""));
        // the entity: state byte = kind, owner = the origin id's low bits
        var lying = new LyingLoot(new LootOrigin(LootKind.Tire, 0x4001 * 4 + 2), new Vector3(3, 0.5f, 4), 1f);
        var s = lying.Serialize();
        Assert.Equal((byte)56, lying.TypeId);
        Assert.Equal((byte)LootKind.Tire, s.State);
        Assert.Equal((ushort)((0x4001 * 4 + 2) & 0xFFFF), s.Owner);
        Assert.Equal(new Vector3(3, 0.5f, 4), s.Position);
        Assert.Equal(LootKind.Dish, LootRules.KindOf((byte)LootKind.Dish));
        Assert.Null(LootRules.KindOf(99));
        // carried bytes: kind + 1, 0 nothing, an unknown value nothing
        Assert.Equal(1, LootKinds.CarriedOf(LootKind.Tire));
        Assert.Equal(LootKind.Connector, LootKinds.OfCarried(4));
        Assert.Null(LootKinds.OfCarried(0));
        Assert.Null(LootKinds.OfCarried(7));
        // the vehicle record: the wheel mask sits in bits 2-5 of the health byte next to the stage and the lights
        var r = VehicleRecord.Make(0x4001, Vector3.Zero, 0, 0, 0, stage: 2, lights: true, wheelsOff: 0b1010);
        Assert.Equal((byte)0b1010, VehicleRecord.Wheels(r));
        Assert.Equal(2, VehicleRecord.Stage(r));
        Assert.True(VehicleRecord.Lights(r));
        Assert.Equal((byte)0, VehicleRecord.Wheels(VehicleRecord.Make(0x4001, Vector3.Zero, 0, 0, 0, stage: 3, lights: true)));
        // the van's song travels beside the door and the side
        var owner = RaidVanRules.PackOwner(0.5f, true, 21);
        Assert.Equal(21, RaidVanRules.SongOf(owner));
        Assert.Equal(0.5f, RaidVanRules.DoorOf(owner), 0.01f);
        Assert.True(RaidVanRules.LeftOf(owner));
        Assert.Equal(0, RaidVanRules.SongOf(RaidVanRules.PackOwner(1f, false)));
    }

    // ---- the registry ----

    [Fact]
    public void AClaimIsExclusiveUntilReleasedAndOneSourcePerKind()
    {
        var cars = new FakeCars().Add(1, 10, 0);
        var reg = new LootRegistry();
        reg.Register(new TireSource(Cfg().Loot.Tire, cars));
        Assert.Throws<InvalidOperationException>(() => reg.Register(new TireSource(Cfg().Loot.Tire, cars)));
        var found = new List<StealTarget>();
        reg.FreeNear(Vector3.Zero, 50, found);
        Assert.Equal(4, found.Count); // one per wheel
        Assert.Equal(4, found.Select(t => t.Id).Distinct().Count());
        Assert.True(reg.Claim(found[0]));
        Assert.False(reg.Claim(found[0])); // a second Thief cannot have it
        Assert.Equal(1, reg.Claimed);
        found.Clear();
        reg.FreeNear(Vector3.Zero, 50, found);
        Assert.Equal(3, found.Count); // the claimed one is not offered
        reg.Release(found[0]); // not the claimed one: nothing happens to the claim
        Assert.Equal(1, reg.Claimed);
        reg.Reset();
        Assert.Equal(0, reg.Claimed);
    }

    [Fact]
    public void ThePickChoosesAKindByWeightThenTheNearestTargetAndNeverTheSameTwice()
    {
        var rig = new Rig(f => { f.Loot.Tire.Weight = 1; f.Loot.Dish.Weight = 0; });
        rig.Cars.Add(1, 10, 0).Add(2, 40, 0);
        rig.Registry.Register(new FakeClimbSource(new Vector3(5, 0, 0), new Vector3(5, 4, 0)));
        // dish weight 0: only tires, and the nearest wheel to the Thief
        var a = rig.Book.Pick(Vector3.Zero, 90, new Vector3(9, 0, 0))!.Value;
        Assert.Equal((LootKind.Tire, 1), (a.Kind, TireSource.CarOf(a.Id)));
        var seen = new HashSet<int> { a.Id };
        for (var i = 0; i < 7; i++) Assert.True(seen.Add(rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value.Id)); // eight wheels, each claimed once
        Assert.Null(rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)); // nothing left
        // allow filters
        rig.Registry.Reset();
        Assert.Null(rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero, _ => false));
        // a kind with weight and nothing near is not chosen: out of the radius
        Assert.Null(rig.Book.Pick(new Vector3(500, 0, 0), 20, new Vector3(500, 0, 0)));
    }

    // ---- the tire target ----

    [Fact]
    public void TheTireTargetIsAWheelOfACarAtRestWithAWorkSpotBesideItAndTakeSetsTheBit()
    {
        var cars = new FakeCars().Add(0x4001, 10, 20);
        var src = new TireSource(Cfg().Loot.Tire, cars);
        var found = new List<StealTarget>();
        src.FreeNear(new Vector3(10, 0, 20), 10, found);
        Assert.Equal(4, found.Count);
        var fl = found.First(t => TireSource.WheelOf(t.Id) == 0);
        Assert.Equal(TireSource.IdOf(0x4001, 0), fl.Id);
        Assert.Equal(new Vector3(10 - 0.72f, 0, 20 - 1.12f), fl.Position); // front left: the car faces -Z
        Assert.Equal(new Vector3(10 - 1.15f, 0, 20 - 1.12f), fl.WorkSpot); // kneels outside the wheel
        Assert.False(fl.NeedsClimb);
        var br = found.First(t => TireSource.WheelOf(t.Id) == 3);
        Assert.Equal(new Vector3(10 + 0.72f, 0, 20 + 1.12f), br.Position);
        src.Take(fl.Id);
        Assert.Equal((byte)1, cars.Off(0x4001));
        Assert.False(src.TryFind(fl.Id, out _));
        found.Clear();
        src.FreeNear(new Vector3(10, 0, 20), 10, found);
        Assert.Equal(3, found.Count);
        src.Return(fl.Id);
        Assert.Equal((byte)0, cars.Off(0x4001));
        Assert.True(src.TryFind(fl.Id, out _));
        cars.Drive(0x4001); // driven off: nothing to steal
        Assert.False(src.TryFind(fl.Id, out _));
        var places = new List<Vector2>();
        src.Places(places);
        Assert.Empty(places);
    }

    [Fact]
    public void TheWheelBitsLiveAndDieWithTheVehicleLifeAndAnAbandonReturnKeepsThem()
    {
        var life = new VehicleLifecycle(300);
        life.TakeWheel(0); life.TakeWheel(3);
        Assert.Equal((byte)0b1001, life.WheelsOff);
        Assert.True(life.WheelOff(3));
        life.ReturnWheel(3);
        Assert.False(life.WheelOff(3));
        Assert.False(life.Reset(false)); // the abandon return: the car stays as it was
        Assert.Equal((byte)1, life.WheelsOff);
        Assert.True(life.Reset(true)); // a round start repairs and puts the wheels back
        Assert.Equal((byte)0, life.WheelsOff);
        life.TakeWheel(2); life.TakeWheel(9); life.ReturnWheel(-1); // out of range: ignored
        Assert.Equal((byte)4, life.WheelsOff);
        life.Damage(1000, 0, 0); // doomed: any return repairs it, wheels included
        Assert.True(life.Reset(false));
        Assert.Equal((byte)0, life.WheelsOff);
    }

    // ---- the loop ----

    [Fact]
    public void AThiefSeeksApproachesStealsHaulsAndLoadsATireAndSeeksAgain()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 20);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        var visited = new List<GangMemberState>();
        var wasWorking = false;
        var tireOffWhileHauling = false;
        rig.RunUntil(() =>
        {
            if (visited.Count == 0 || visited[^1] != thief.State) visited.Add(thief.State);
            if (thief.State == GangMemberState.Steal) wasWorking |= thief.Working && thief.Busy;
            if (thief.State == GangMemberState.Haul) tireOffWhileHauling |= rig.Cars.Off(0x4001) != 0 && thief.Carried == LootKinds.CarriedOf(LootKind.Tire);
            return raid.Van.Load >= 1 && thief.State == GangMemberState.Seek;
        });
        var core = visited.Where(s => s is GangMemberState.Seek or GangMemberState.Approach or GangMemberState.Steal or GangMemberState.Haul or GangMemberState.Load).ToList();
        Assert.Contains(GangMemberState.Approach, core);
        Assert.Contains(GangMemberState.Steal, core);
        Assert.Contains(GangMemberState.Haul, core);
        Assert.Contains(GangMemberState.Load, core);
        Assert.True(core.IndexOf(GangMemberState.Approach) < core.IndexOf(GangMemberState.Steal) && core.IndexOf(GangMemberState.Steal) < core.IndexOf(GangMemberState.Haul) && core.IndexOf(GangMemberState.Haul) < core.IndexOf(GangMemberState.Load));
        Assert.True(wasWorking);
        Assert.True(tireOffWhileHauling); // the wheel was gone from the car while the Thief carried it
        Assert.Equal(1, raid.Van.Load);
        Assert.Single(raid.Loaded);
        Assert.Equal(new LootOrigin(LootKind.Tire, raid.Loaded[0].Id), raid.Loaded[0]);
        Assert.Equal(0, thief.Carried);
        Assert.Equal(1, new[] { 0, 1, 2, 3 }.Count(w => (rig.Cars.Off(0x4001) & (1 << w)) != 0)); // one wheel gone
        Assert.Equal(0, rig.Registry.Claimed); // the claim ended with the take
        Assert.Equal(0, rig.Book.LyingCount);
    }

    [Fact]
    public void TheThievesStealOneAtATimeFromTheSameCarAndStopWhenTheVanIsFull()
    {
        var rig = new Rig(f => { f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 3 }; f.Van.Capacity = 2; f.Van.StaySeconds = 1000; f.Loot.Tire.StealSeconds = 1; });
        rig.Cars.Add(0x4001, 30, 12).Add(0x4002, 34, 12);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.Load >= 2);
        Assert.Equal(2, raid.Van.Load);
        Assert.Equal(2, raid.Loaded.Select(o => o.Id).Distinct().Count()); // two different wheels
        // full: the Thieves take no more, the van calls the crew back and leaves
        rig.Run(2);
        Assert.Equal(2, raid.Van.Load);
        var wheels = rig.Cars.Cars_.Values.Sum(c => Enumerable.Range(0, 4).Count(w => (c.WheelsOff & (1 << w)) != 0));
        Assert.Equal(2, wheels);
    }

    [Fact]
    public void AThiefDoesNotStartWhileTheVanIsNotOpenAndStandsInSeekWhenNothingIsNear()
    {
        var rig = new Rig();
        var raid = rig.Launch(); // no cars
        var thief = rig.ThiefOut(raid);
        rig.Run(6);
        Assert.Equal(GangMemberState.Seek, thief.State);
        Assert.Equal(0, rig.Registry.Claimed);
        rig.Cars.Add(0x4001, 30, 12); // a car appears: it asks again after the retry
        rig.RunUntil(() => thief.State == GangMemberState.Approach);
        Assert.NotNull(thief.Job);
    }

    [Fact]
    public void AThiefGivesUpATargetThatWentAndLooksForAnother()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12).Add(0x4002, 70, 30);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Approach);
        var first = thief.Job!.Value;
        rig.Cars.Drive(TireSource.CarOf(first.Id)); // the car is driven off
        rig.Step();
        rig.Step();
        Assert.True(thief.Job is not { } j || TireSource.CarOf(j.Id) != TireSource.CarOf(first.Id)); // released, or already on another car
        Assert.True(rig.Registry.Claimed <= 1);
        rig.RunUntil(() => thief.Job is { } n && n.Id != first.Id && TireSource.CarOf(n.Id) != TireSource.CarOf(first.Id));
    }

    [Fact]
    public void ANoRouteMakesAThiefAvoidThatTargetForAWhile()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        // the first route asked for is answered with none: the target is given up, and not chosen again while avoided
        var served = false;
        StealTarget? first = null;
        for (var i = 0; i < 400 && !served; i++)
        {
            rig.Gangs.Update(); rig.Entities.Tick(Dt); rig.Raids.Tick(Dt);
            first ??= thief.Job;
            served = rig.Gangs.ServeRoute((_, _) => Array.Empty<Vector3>());
        }
        Assert.True(served);
        Assert.NotNull(first);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal(GangMemberState.Seek, thief.State);
        for (var i = 0; i < 3 * 30; i++) { rig.Step(); Assert.True(thief.Job is not { } j || j.Id != first!.Value.Id); } // another wheel, never that one
        Assert.NotNull(thief.Job);
    }

    // ---- killed carrying: lying Loot ----

    static (Rig Rig, Raid Raid, GangMember Thief) CarryingThief(out StealTarget? taken)
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        taken = null;
        return (rig, raid, thief);
    }

    [Fact]
    public void ACarrierKilledLeavesTheLootWhereItFellTheWheelStaysOffAndTheLyingLootIsAnEntity()
    {
        var (rig, raid, thief) = CarryingThief(out _);
        rig.Run(1);
        var at = thief.Position;
        var killed = new List<GangKilled>();
        raid.Gang.Killed += killed.Add;
        Assert.True(thief.Kill(5));
        var lying = Assert.Single(rig.Book.Lying);
        Assert.Equal(LootKind.Tire, lying.Kind);
        Assert.Equal(at, lying.Position);
        Assert.Single(rig.Entities.OfType(LootRules.TypeId));
        Assert.NotEqual((byte)0, rig.Cars.Off(0x4001)); // still taken until someone sends it back
        Assert.Equal(0, raid.Van.Load);
        Assert.Equal(lying.Origin.Id, killed[0].CarriedId);
        Assert.Equal(LootKinds.CarriedOf(LootKind.Tire), killed[0].Carried);
        Assert.Equal(at, killed[0].Where);
    }

    [Fact]
    public void AnotherThiefTakesLyingLootUpHaulsAndLoadsIt()
    {
        var rig = new Rig(f => { f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 2 }; });
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Gang.Members.Count(m => m.Kind == GangMemberKind.Thief) == 2);
        var a = raid.Gang.Members[0]; var b = raid.Gang.Members[1];
        // put a tire down at the van's side (as a killed carrier would): it is a free target of its kind
        var wheel = Enumerable.Range(0, 4).First(w => rig.Book.Pieces.Of(new LootOrigin(LootKind.Tire, TireSource.IdOf(0x4001, w))) == LootPieceState.AtPlace); // not one a Thief already claimed
        var origin = new LootOrigin(LootKind.Tire, TireSource.IdOf(0x4001, wheel));
        rig.Cars.SetWheel(0x4001, wheel, true);
        rig.Book.Drop(origin, new Vector3(58, 0, 10)); // nearer the Thieves than the car's own wheels
        rig.RunUntil(() => raid.Van.Load >= 1);
        Assert.Equal(0, rig.Book.LyingCount); // taken up
        Assert.Contains(origin, raid.Loaded.ToList()); // the very same wheel, loaded
        Assert.True(a.Alive && b.Alive);
    }

    [Fact]
    public void ThePlayerHoldOnLyingLootSendsItBackOnlyAfterTheFullHoldInReachAndAHitInterruptsIt()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12);
        rig.Cars.SetWheel(0x4001, 2, true);
        var id = rig.Book.Drop(new LootOrigin(LootKind.Tire, TireSource.IdOf(0x4001, 2)), new Vector3(5, 0, 5))!.Value;
        var reach = rig.Cfg.Loot.ReachM;
        Assert.Equal(id, rig.Book.NearestLying(new Vector3(5, 0, 5 + reach - 0.1f), reach));
        Assert.Null(rig.Book.NearestLying(new Vector3(5, 0, 5 + reach + 0.5f), reach));
        Assert.Null(rig.Book.NearestLying(new Vector3(5, 3, 5), reach)); // another floor
        var hold = new LootReturnHold();
        var secs = rig.Cfg.Loot.ReturnSeconds;
        var done = false;
        for (var t = 0.0; t < secs - 0.05 && !done; t += Dt) done = hold.Tick(true, id, Dt, secs);
        Assert.False(done);
        Assert.InRange(hold.Held, 0.5, secs);
        hold.Tick(true, null, Dt, secs); // moved off: starts over
        Assert.Equal(0, hold.Held);
        for (var t = 0.0; t < secs / 2; t += Dt) hold.Tick(true, id, Dt, secs);
        hold.Interrupt(); // a hit
        for (var t = 0.0; t < secs * 2; t += Dt) Assert.False(hold.Tick(true, id, Dt, secs)); // the key is still down: nothing
        hold.Tick(false, id, Dt, secs); // released
        for (var t = 0.0; t < secs * 2 && !done; t += Dt) done = hold.Tick(true, id, Dt, secs);
        Assert.True(done); // once, after the full hold
        Assert.False(hold.Tick(true, id, Dt, secs)); // and not again while the key stays down
        Assert.True(rig.Book.ReturnLying(id));
        Assert.Equal((byte)0, rig.Cars.Off(0x4001) & 4); // the wheel is back on
        Assert.Equal(0, rig.Book.LyingCount);
        Assert.Empty(rig.Entities.OfType(LootRules.TypeId));
        Assert.False(rig.Book.ReturnLying(id)); // gone
    }

    [Fact]
    public void LyingLootIsCappedAndTheOldestGoesBackByItself()
    {
        var rig = new Rig(f => f.Loot.MaxLying = 3);
        for (var c = 1; c <= 3; c++) rig.Cars.Add(c, c * 10, 0);
        var returned = new List<LootOrigin>();
        rig.Book.Returned += returned.Add;
        var origins = Enumerable.Range(0, 5).Select(i => new LootOrigin(LootKind.Tire, TireSource.IdOf(1 + i / 4 + i % 4 / 4, i % 4))).ToList();
        foreach (var o in origins) { rig.Cars.SetWheel(TireSource.CarOf(o.Id), TireSource.WheelOf(o.Id), true); rig.Book.Drop(o, new Vector3(o.Id, 0, 0)); }
        Assert.Equal(3, rig.Book.LyingCount);
        Assert.Equal(origins.Take(2), returned); // the two oldest went back
        Assert.Equal(origins.Skip(2), rig.Book.Lying.Select(l => l.Origin));
        Assert.Equal(0, rig.Cars.Off(1) & 3); // their wheels are back on
    }

    // ---- vans ----

    [Fact]
    public void ADestroyedVanReturnsEverythingLoadedAndADrivenOffVanKeepsIt()
    {
        var rig = new Rig(f => { f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 2 }; f.Van.StaySeconds = 1000; });
        rig.Cars.Add(0x4001, 30, 12).Add(0x4002, 34, 14);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Van.Load >= 2);
        var wheelsOff = rig.Cars.Cars_.Values.Sum(c => Enumerable.Range(0, 4).Count(w => (c.WheelsOff & (1 << w)) != 0));
        Assert.Equal(2, wheelsOff);
        var returned = new List<int>();
        raid.LootReturned += (_, n) => returned.Add(n);
        var loaded = raid.Loaded.ToList();
        raid.Van.Hit(9, 1000, false); // destroyed
        Assert.Equal(new[] { 2 }, returned);
        Assert.Equal(0, raid.Van.Load);
        Assert.Empty(raid.Loaded);
        Assert.Equal(0, rig.Cars.Cars_.Values.Sum(c => c.WheelsOff)); // both wheels are back on their cars
        Assert.Equal(2, loaded.Count);

        // a second Raid whose van drives off with its load: the Loot stays gone
        var rig2 = new Rig(f => { f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 2 }; f.Van.Capacity = 2; f.Loot.Tire.StealSeconds = 1; });
        rig2.Cars.Add(0x4001, 30, 12).Add(0x4002, 34, 14);
        var raid2 = rig2.Launch();
        rig2.RunUntil(() => raid2.Van.Load >= 2);
        rig2.RunUntil(() => raid2.Van.Ended != null || raid2.Over, 400);
        Assert.Equal(2, rig2.Cars.Cars_.Values.Sum(c => Enumerable.Range(0, 4).Count(w => (c.WheelsOff & (1 << w)) != 0)));
        Assert.Equal(2, rig2.Cars.Cars_.Values.Sum(c => Enumerable.Range(0, 4).Count(w => (c.WheelsOff & (1 << w)) != 0)));
        rig2.Book.Reset(); // the round resets: everything back
        Assert.Equal(0, rig2.Cars.Cars_.Values.Sum(c => c.WheelsOff));
    }

    [Fact]
    public void AMemberThatBoardsStillCarryingUnloadsIntoTheVan()
    {
        var rig = new Rig(f => f.Van.StaySeconds = 1000);
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        thief.Board(raid.Van.DoorPoint); // called back mid-haul: drops nothing, boards
        Assert.Equal(GangMemberState.Board, thief.State);
        Assert.NotEqual(0, thief.Carried);
        rig.RunUntil(() => raid.Boarded == 1);
        Assert.Equal(1, raid.Van.Load);
        Assert.Single(raid.Loaded);
        Assert.Equal(0, rig.Book.LyingCount);
        Assert.Equal(0, rig.Registry.Claimed);
    }

    [Fact]
    public void ACalledBackThiefDropsItsClaimAndAFleeingOneLetsTheTargetGo()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Approach);
        Assert.Equal(1, rig.Registry.Claimed);
        thief.Flee(new Vector3(1000, 0, 0));
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal(GangMemberState.Flee, thief.State);
        Assert.Equal((byte)0, rig.Cars.Off(0x4001)); // nothing was taken
    }

    [Fact]
    public void AThiefWithFreeHandsJoinsAFightAndReleasesItsTargetButAWorkingOneDoesNot()
    {
        var rig = new Rig(f => f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 2 });
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Gang.Members.Count(m => m.Kind == GangMemberKind.Thief) == 2 && raid.Gang.Members.Any(m => m.State == GangMemberState.Steal));
        var worker = raid.Gang.Members.First(m => m.State == GangMemberState.Steal);
        Assert.True(worker.Working);
        Assert.False(worker.CanFight);
        // a player hits the Raid: aggro (crowding a working Thief is none)
        rig.World.List.Add(new GangPlayer(4, worker.Position + new Vector3(2, 0, 0), true));
        raid.Gang.NoteHit(4);
        rig.Step();
        Assert.Contains((ushort)4, raid.Gang.Aggro);
        Assert.Equal(GangMemberState.Steal, worker.State);
        var idler = raid.Gang.Members.First(m => m != worker);
        rig.Run(0.5f);
        Assert.NotNull(idler.Target); // the other one has free hands, so it fights
        Assert.DoesNotContain(GangMemberState.Steal, new[] { idler.State });
        Assert.Equal(1, rig.Registry.Claimed); // only the worker's target is held: the fighter let its own go
    }

    // ---- the climb ----

    [Fact]
    public void AClimbTargetIsClimbedWorkedAloftAndClimbedDownAtClimbSpeedThenHauled()
    {
        var rig = new Rig(f => { f.Loot.Tire.Weight = 0; f.Loot.Dish.StealSeconds = 3; f.Loot.ClimbSpeed = 2f; }, tires: false);
        var src = new FakeClimbSource(new Vector3(30, 0, 12), new Vector3(30, 5, 12));
        rig.Registry.Register(src);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        var states = new List<GangMemberState>();
        var maxY = 0f;
        var upSeconds = 0f; var downSeconds = 0f; var workSeconds = 0f;
        var offNav = true;
        rig.RunUntil(() =>
        {
            if (states.Count == 0 || states[^1] != thief.State) states.Add(thief.State);
            maxY = MathF.Max(maxY, thief.Position.Y);
            if (thief.State == GangMemberState.ClimbUp) { upSeconds += Dt; offNav &= thief.Position.X == 30 && thief.Position.Z == 12; } // straight up
            if (thief.State == GangMemberState.WorkAloft) { workSeconds += Dt; Assert.Equal(new Vector3(30, 5, 12), thief.Position); Assert.True(thief.Working); }
            if (thief.State == GangMemberState.ClimbDown) { downSeconds += Dt; Assert.Equal(LootKinds.CarriedOf(LootKind.Dish), thief.Carried); } // with the thing
            return raid.Van.Load >= 1;
        });
        Assert.Equal(5f, maxY, 0.01f);
        Assert.True(offNav);
        Assert.InRange(upSeconds, 5f / 2f - 0.2f, 5f / 2f + 0.2f); // 5 m at 2 m/s
        Assert.InRange(downSeconds, 5f / 2f - 0.2f, 5f / 2f + 0.2f);
        Assert.InRange(workSeconds, 3f - 0.2f, 3f + 0.2f);
        var core = states.Where(s => s is GangMemberState.ClimbUp or GangMemberState.WorkAloft or GangMemberState.ClimbDown or GangMemberState.Haul or GangMemberState.Load or GangMemberState.Steal).ToList();
        Assert.Equal(new[] { GangMemberState.ClimbUp, GangMemberState.WorkAloft, GangMemberState.ClimbDown, GangMemberState.Haul, GangMemberState.Load }, core);
        Assert.Equal(1, src.Taken);
        Assert.Equal(new LootOrigin(LootKind.Dish, 1), Assert.Single(raid.Loaded));
    }

    [Fact]
    public void AMemberKilledAloftFallsToTheGroundAndLiesDeadThereItsLootLyingBeside()
    {
        var rig = new Rig(f => { f.Loot.Dish.StealSeconds = 3; f.Loot.ClimbSpeed = 2f; }, tires: false);
        var src = new FakeClimbSource(new Vector3(30, 0, 12), new Vector3(30, 5, 12));
        rig.Registry.Register(src);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.WorkAloft);
        rig.Run(1.5f); // some work done
        Assert.Equal(5f, thief.Position.Y, 0.01f);
        var killed = new List<GangKilled>();
        raid.Gang.Killed += killed.Add;
        thief.Kill(3);
        Assert.Equal(GangMemberState.Dead, thief.State);
        Assert.Equal(0, rig.Registry.Claimed); // the claim ended: the dish is free again
        Assert.Equal(new Vector3(30, 0, 12), killed[0].Where); // where it lands, on the ground
        Assert.Equal(5f, thief.Position.Y, 0.01f); // still up there this tick
        rig.Run(0.2f);
        Assert.True(thief.Position.Y < 5f && thief.Position.Y > 0f); // falling
        rig.Run(1.5f);
        Assert.Equal(0f, thief.Position.Y, 0.001f); // lies on the ground
        Assert.Equal(30f, thief.Position.X, 0.001f);
        Assert.Equal(12f, thief.Position.Z, 0.001f);
        Assert.Equal(new Vector3(30, 0, 12), thief.RestPoint);
        Assert.False(src.Gone); // killed before it had it: the dish is still on its wall
        Assert.Equal(0, rig.Book.LyingCount);
    }

    [Fact]
    public void AMemberKilledAloftWithTheThingDropsItAtTheBase()
    {
        var rig = new Rig(f => { f.Loot.Dish.StealSeconds = 1; f.Loot.ClimbSpeed = 2f; }, tires: false);
        var src = new FakeClimbSource(new Vector3(30, 0, 12), new Vector3(30, 5, 12));
        rig.Registry.Register(src);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.ClimbDown);
        rig.Run(1);
        Assert.True(thief.Position.Y > 0.5f);
        thief.Kill(3);
        var lying = Assert.Single(rig.Book.Lying);
        Assert.Equal(new Vector3(30, 0, 12), lying.Position); // at the base, not in the air
        Assert.True(src.Gone); // still taken: lying
        Assert.True(rig.Book.ReturnLying((ushort)rig.Entities.OfType(LootRules.TypeId)[0].Id));
        Assert.Equal(1, src.Returned);
        Assert.False(src.Gone);
    }

    [Fact]
    public void ACalledBackMemberAloftIsDownAtOnceAndATargetThatWentSendsItDown()
    {
        var rig = new Rig(f => { f.Loot.Dish.StealSeconds = 5; f.Loot.ClimbSpeed = 2f; }, tires: false);
        var src = new FakeClimbSource(new Vector3(30, 0, 12), new Vector3(30, 5, 12));
        rig.Registry.Register(src);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.WorkAloft);
        src.Gone = true; // someone else got it
        rig.Step(); rig.Step();
        Assert.Equal(GangMemberState.ClimbDown, thief.State);
        Assert.Equal(0, thief.Carried);
        rig.RunUntil(() => thief.State == GangMemberState.Seek);
        Assert.Equal(0f, thief.Position.Y);
        src.Gone = false;
        rig.RunUntil(() => thief.State == GangMemberState.WorkAloft);
        thief.Board(raid.Van.DoorPoint);
        Assert.Equal(0f, thief.Position.Y); // no time to climb by the book
        Assert.Equal(0, rig.Registry.Claimed);
    }

    [Fact]
    public void AKindThatDoesNotLieGoesBackAtOnceAndHearsWhereItsCarrierIs()
    {
        var rig = new Rig(f => { f.Loot.Tire.Weight = 0; f.Loot.Pet.StealSeconds = 1; }, tires: false);
        var pet = new FakePetSource();
        rig.Registry.Register(pet);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        rig.Run(1);
        Assert.NotEmpty(pet.Carry);
        Assert.Equal(thief.Position.X, pet.Carry[^1].At.X, 0.2f); // the source is told where the carrier is
        Assert.True(pet.Gone);
        thief.Kill(4);
        Assert.Equal(0, rig.Book.LyingCount); // no lying Loot: it walks home by itself
        Assert.Empty(rig.Entities.OfType(LootRules.TypeId));
        Assert.Equal(1, pet.Returned);
        Assert.False(pet.Gone);
        Assert.Equal(0, rig.Registry.Claimed);
    }

    // ---- reset, credit ----

    [Fact]
    public void AResetPutsEverythingBackLyingLootGoesAndClaimsEnd()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12);
        rig.Cars.SetWheel(0x4001, 0, true); rig.Cars.SetWheel(0x4001, 3, true);
        rig.Book.Drop(new LootOrigin(LootKind.Tire, TireSource.IdOf(0x4001, 0)), new Vector3(1, 0, 1));
        var t = rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!;
        Assert.Equal(1, rig.Registry.Claimed);
        rig.Book.Reset();
        Assert.Equal(0, rig.Book.LyingCount);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal((byte)0, rig.Cars.Off(0x4001));
        Assert.Empty(rig.Entities.OfType(LootRules.TypeId));
    }

    [Fact]
    public void LyingLootStaysAfterTheEventAndStaysReturnable()
    {
        var rig = new Rig();
        rig.Cars.Add(0x4001, 30, 12);
        rig.Cars.SetWheel(0x4001, 1, true);
        var id = rig.Book.Drop(new LootOrigin(LootKind.Tire, TireSource.IdOf(0x4001, 1)), new Vector3(5, 0, 5))!.Value;
        rig.Gangs.Clear(); rig.Raids.Clear(); // the event ended and its crews and vans are gone
        rig.Run(5);
        Assert.Equal(1, rig.Book.LyingCount);
        Assert.True(rig.Book.ReturnLying(id));
        Assert.Equal((byte)0, rig.Cars.Off(0x4001));
    }

    [Fact]
    public void AHitOnAVanCreditsTheShooterOnlyWhenItCounted()
    {
        var rig = new Rig();
        rig.Raids.SourceOf = w => w == Bullet ? HitSource.Bullet : HitSource.None;
        var raid = rig.Launch();
        var credit = new ThievesCredit();
        var vans = new CreditedVanHits(rig.Raids, credit);
        var id = RaidVanRules.HitIdOf(raid.Van.Slot, 0);
        Assert.Equal(DamageResult.Ignored, vans.Hit(id, 4, SpecialWeapons.Fall, 50, KillCause.Standard)); // a fall counts nothing
        Assert.Equal(DamageResult.Ignored, vans.Hit(id, 5, Bullet, 14, KillCause.Roadkill)); // ramming counts nothing
        Assert.Empty(credit.Credited);
        Assert.Equal(DamageResult.Damaged, vans.Hit(id, 4, Bullet, 14, KillCause.Standard));
        Assert.Equal(new ushort[] { 4 }, credit.Credited);
        var lying = rig.Book.Drop(new LootOrigin(LootKind.Tire, 4), Vector3.Zero);
        credit.NoteLootReturned(6);
        Assert.Equal(new ushort[] { 4, 6 }, credit.Credited);
        Assert.NotNull(lying);
    }

    // ---- vehicles first ----

    static VehiclePriorityConfig Vp(float share = 0.5f, int max = 4) => new() { ThiefShare = share, MaxThievesPerVehicle = max };

    [Theory]
    [InlineData(1, 4, 0, 1)]    // a lone Thief: at least one
    [InlineData(2, 4, 0, 1)]    // half of 2
    [InlineData(3, 4, 0, 2)]    // half of 3 rounded up
    [InlineData(7, 4, 0, 4)]    // half of 7 is 4: the wheel cap
    [InlineData(20, 4, 0, 4)]   // never more than the wheels
    [InlineData(20, 2, 0, 2)]   // nor than the free tires
    [InlineData(20, 4, 3, 1)]   // three are on the car already
    [InlineData(0, 4, 0, 0)]    // nobody free
    [InlineData(5, 0, 0, 0)]    // nothing to take
    public void TheVehicleShareIsRoundedUpAndCappedByWheelsAndFreeTires(int free, int tires, int already, int expected) =>
        Assert.Equal(expected, VehiclePriority.Count(free, tires, already, Vp()));

    [Fact]
    public void TheVehicleShareNeverPassesTheConfiguredMaxNorFourWheels()
    {
        Assert.Equal(2, VehiclePriority.Count(20, 4, 0, Vp(1f, 2)));
        Assert.Equal(4, VehiclePriority.Count(20, 4, 0, Vp(1f, 9))); // 4 wheels even if the file said more
    }

    [Fact]
    public void TheNearestThievesTakeTheNearestTiresOneEach()
    {
        var thieves = new[] { new Vector3(50, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0) };
        var tires = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, -1) };
        var pairs = VehiclePriority.Assign(thieves, tires, Vector3.Zero, 2);
        Assert.Equal(2, pairs.Count);
        Assert.Equal(new[] { 1, 2 }, pairs.Select(p => p.Thief).OrderBy(i => i)); // the far one stays out
        Assert.Equal(2, pairs.Select(p => p.Tire).Distinct().Count());
    }

    [Fact]
    public void ACarIsNewOnlyOnceUntilItLeavesAndComesBack()
    {
        var a = new VehicleArrivals();
        var got = new List<int>();
        TireCar Car(int id, float x, bool rest = true, byte off = 0) => new(id, new Vector3(x, 0, 0), 0, rest, off);
        a.Scan(new[] { Car(1, 10) }, Vector3.Zero, 90, got);
        Assert.Empty(got); // the first scan only notes what stands there
        a.Scan(new[] { Car(1, 10), Car(2, 20) }, Vector3.Zero, 90, got);
        Assert.Equal(new[] { 2 }, got); // a new one
        got.Clear();
        a.Scan(new[] { Car(1, 10), Car(2, 20) }, Vector3.Zero, 90, got);
        Assert.Empty(got); // handled
        a.Scan(new[] { Car(1, 10), Car(2, 20, rest: false) }, Vector3.Zero, 90, got); // 2 drives
        a.Scan(new[] { Car(1, 10), Car(2, 20) }, Vector3.Zero, 90, got); // and stops again
        Assert.Equal(new[] { 2 }, got);
        got.Clear();
        a.Scan(new[] { Car(1, 10), Car(2, 500) }, Vector3.Zero, 90, got); // out of the radius
        a.Scan(new[] { Car(1, 10), Car(2, 80) }, Vector3.Zero, 90, got); // back in
        Assert.Equal(new[] { 2 }, got);
        got.Clear();
        a.Scan(new[] { Car(3, 5, off: 0b1111) }, Vector3.Zero, 90, got); // stripped: nothing to take
        Assert.Empty(got);
    }

    [Fact]
    public void ATireStillOnACarIsPickedBeforeADishAndAnyOtherKindWhenTiresAreFree()
    {
        var rig = new Rig(f => { f.Loot.Tire.Weight = 1; f.Loot.Dish.Weight = 1000; f.Loot.Pet.Weight = 1000; });
        rig.Cars.Add(1, 60, 0);
        rig.Registry.Register(new FakeClimbSource(new Vector3(5, 0, 0), new Vector3(5, 4, 0)));
        rig.Registry.Register(new FakePetSource());
        for (var i = 0; i < 4; i++) Assert.Equal(LootKind.Tire, rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value.Kind); // the dish and the pet are nearer and weigh far more
        Assert.NotEqual(LootKind.Tire, rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value.Kind); // no tire left: the others
        rig.Registry.Reset();
        rig.Cfg.Loot.Tire.Weight = 0; // a kind switched off is not preferred
        Assert.NotEqual(LootKind.Tire, rig.Book.Pick(Vector3.Zero, 90, Vector3.Zero)!.Value.Kind);
    }

    static RaidWork WorkOf(Raid raid) => (RaidWork)raid.Gang.Work!;

    [Fact]
    public void ANewCarPullsAShareOfTheFreeThievesToItsTiresAndLeavesTheBusyOnesAlone()
    {
        var rig = new Rig(f => { f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 4 }; f.Van.Capacity = 20; f.Van.StaySeconds = 1000; f.Loot.Dish.Weight = 1; });
        rig.Registry.Register(new FakeClimbSource(new Vector3(20, 0, 15), new Vector3(20, 4, 15))); // something else to do
        var raid = rig.Launch();
        rig.RunUntil(() => raid.Gang.Members.Count(m => m.Kind == GangMemberKind.Thief) == 4);
        rig.RunUntil(() => raid.Gang.Members.Any(m => m.State == GangMemberState.Steal || m.State == GangMemberState.ClimbUp || m.State == GangMemberState.WorkAloft)); // one is at work: it keeps going
        var work = WorkOf(raid);
        work.ScanVehicles(1f); // primes: nothing new
        var busy = raid.Gang.Members.Where(m => m.Working).ToList();
        var busyJobs = busy.Select(m => m.Job).ToList();
        rig.Cars.Add(0x4001, 30, 12); // a car spawns
        Assert.Equal(0, work.ScanVehicles(0.1f)); // not time yet
        var free = raid.Gang.Members.Count(m => m.Kind == GangMemberKind.Thief && m.CanDelegate);
        var sent = work.ScanVehicles(1f);
        Assert.Equal(VehiclePriority.Count(free, 4, 0, rig.Cfg.Loot.VehiclePriority), sent);
        Assert.InRange(sent, 1, 4);
        Assert.True(busy.All(m => m.Working)); // untouched
        Assert.Equal(busyJobs, busy.Select(m => m.Job));
        Assert.Equal(0, work.ScanVehicles(1f)); // the car is handled
        rig.Step(); rig.Step();
        var onCar = raid.Gang.Members.Where(m => m.Job is { Kind: LootKind.Tire } j && TireSource.CarOf(j.Id) == 0x4001).ToList();
        Assert.Equal(sent, onCar.Count);
        Assert.Equal(sent, onCar.Select(m => m.Job!.Value.Id).Distinct().Count()); // one Thief per wheel
        Assert.Equal(new HashSet<(LootKind, int, bool)>(raid.Gang.Members.Where(m => m.Job != null).Select(m => m.Job!.Value.Key)).Count, rig.Registry.Claimed); // claims stay exclusive and none is left behind
    }

    [Fact]
    public void AThiefHandedATireReleasesItsOldClaimAndGoesForTheTire()
    {
        var rig = new Rig(f => { f.Van.Capacity = 20; f.Van.StaySeconds = 1000; });
        rig.Cars.Add(0x4001, 70, 30);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Approach);
        var old = thief.Job!.Value;
        var work = WorkOf(raid);
        work.ScanVehicles(1f); // primes
        rig.Cars.Add(0x4002, 25, 12); // a nearer car
        Assert.Equal(1, work.ScanVehicles(1f));
        Assert.False(rig.Registry.IsClaimed(old) && TireSource.CarOf(old.Id) != 0x4002); // the old claim ended
        rig.Step(); rig.Step();
        Assert.Equal(GangMemberState.Approach, thief.State);
        Assert.Equal(0x4002, TireSource.CarOf(thief.Job!.Value.Id));
        Assert.Equal(1, rig.Registry.Claimed);
    }

    [Fact]
    public void AThiefCarryingOrStealingIsNeverHandedATire()
    {
        var rig = new Rig(f => { f.Van.Capacity = 20; f.Van.StaySeconds = 1000; });
        rig.Cars.Add(0x4001, 30, 12);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Steal);
        var work = WorkOf(raid);
        work.ScanVehicles(1f);
        rig.Cars.Add(0x4002, 25, 12);
        Assert.False(thief.CanDelegate);
        Assert.Equal(0, work.ScanVehicles(1f));
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        Assert.False(thief.CanDelegate); // carrying
    }
}
