using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Thieves;

/// <summary>Pets as Loot and the fence hop (docs/events/thieves/05): the Carried animal state, the pet target, the hop over a fence section, fences.json, a pet in a van. State machines run Strict.</summary>
public class PetLootTests
{
    const float Dt = 1f / 30f;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    // ---- the Animal ----

    sealed class Flat : IAnimalWorld
    {
        public float? GroundY(float x, float z) => 0f;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
    }

    static Vector2[] Square(float x0, float z0, float x1, float z1) => new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };
    static Pen PenAt(float x, float z) => new(new IReadOnlyList<Vector2[]>[] { new[] { Square(x, z, x + 12, z + 12) } });

    static AnimalsFile Animals() => GameData.Load(DataDir).Animals;

    static Animal Dog(IAnimalWorld world, Vector3 home, int seed = 1)
    {
        var animals = Animals();
        return new Animal(animals, animals.IndexOf("dog"), home, 0f, world, seed);
    }

    [Fact]
    public void CarriedIsReachableFromEveryLivingStateAndLeavesOnlyToIdleOrDead()
    {
        foreach (var s in new[] { AnimalState.Idle, AnimalState.Eat, AnimalState.Walk, AnimalState.Sit, AnimalState.Bark, AnimalState.Flee })
            Assert.True(Animal.Spec.Allows(s, AnimalState.Carried), s.ToString());
        Assert.True(Animal.Spec.Allows(AnimalState.Carried, AnimalState.Idle));
        Assert.True(Animal.Spec.Allows(AnimalState.Carried, AnimalState.Dead));
        Assert.False(Animal.Spec.Allows(AnimalState.Carried, AnimalState.Walk));
        Assert.False(Animal.Spec.Allows(AnimalState.Dead, AnimalState.Carried));
        Assert.Equal((byte)7, (byte)AnimalState.Carried);
        Assert.True((byte)AnimalState.Carried <= AnimalRules.StateMask);
    }

    [Fact]
    public void ACarriedAnimalDoesNothingByItselfFollowsItsCarrierAndStandsWhenReleasedInsideItsPen()
    {
        var pen = PenAt(0, 0);
        var world = new PennedWorld(new Flat(), pen);
        var dog = Dog(world, new Vector3(6, 0, 6));
        for (var i = 0; i < 40; i++) dog.Tick(Dt);
        Assert.True(dog.Carry());
        Assert.False(dog.Carry()); // already held
        Assert.Equal(AnimalState.Carried, dog.State);
        dog.MoveCarried(new Vector3(5, 0.9f, 5), 1.5f);
        for (var i = 0; i < 300; i++) dog.Tick(Dt);
        Assert.Equal(new Vector3(5, 0.9f, 5), dog.Position); // it stays where it is held
        Assert.Equal(1.5f, dog.Yaw);
        Assert.Equal(AnimalState.Carried, AnimalRules.StateOf(dog.Serialize().State));
        Assert.True(dog.Release());
        Assert.Equal(AnimalState.Idle, dog.State); // inside its pen: it just stands
        Assert.Equal(0f, dog.Position.Y);
        Assert.False(dog.Release());
        dog.MoveCarried(new Vector3(1, 1, 1), 0); // not held: ignored
        Assert.Equal(5f, dog.Position.X);
    }

    [Fact]
    public void ReleasedOutsideItsPenItWalksStraightHomeOverOpenGroundAndIsPennedAgain()
    {
        var pen = PenAt(0, 0);
        var world = new PennedWorld(new Flat(), pen);
        var home = new Vector3(6, 0, 6);
        var dog = Dog(world, home);
        dog.Carry();
        dog.MoveCarried(new Vector3(30, 1, 6), 0); // carried well out of the pen
        Assert.False(pen.Contains(30, 6));
        dog.Release();
        Assert.False(pen.Contains(dog.Position.X, dog.Position.Z));
        var back = false;
        for (var i = 0; i < 4000 && !back; i++) { dog.Tick(Dt); back = pen.Contains(dog.Position.X, dog.Position.Z); }
        Assert.True(back, "it never got home");
        // penned again: a long run never takes it out
        for (var i = 0; i < 3000; i++)
        {
            dog.Tick(Dt);
            Assert.True(pen.Contains(dog.Position.X, dog.Position.Z), $"left the pen at tick {i}");
        }
    }

    [Fact]
    public void ReleasedWhereThereIsNoGroundAtAllItIsHomeAtOnce()
    {
        var pen = PenAt(0, 0);
        var world = new PennedWorld(new NoGroundOutside(pen), pen);
        var dog = Dog(world, new Vector3(6, 0, 6));
        dog.Carry();
        dog.MoveCarried(new Vector3(30, 1, 6), 0);
        dog.Release();
        Assert.Equal(6f, dog.Position.X);
        Assert.True(pen.Contains(dog.Position.X, dog.Position.Z));
    }

    sealed class NoGroundOutside : IAnimalWorld
    {
        readonly Pen _pen;
        public NoGroundOutside(Pen pen) => _pen = pen;
        public float? GroundY(float x, float z) => _pen.Contains(x, z) ? 0f : null;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
    }

    [Fact]
    public void ACarriedAnimalStillDiesOfAHitAndADeadOneCannotBeCarried()
    {
        var dog = Dog(new Flat(), new Vector3(0, 0, 0));
        dog.Carry();
        dog.OnDamage(1, 9);
        Assert.False(dog.Alive);
        Assert.Equal(AnimalState.Dead, dog.State);
        Assert.False(dog.Carry());
        Assert.False(dog.Release());
    }

    // ---- fences.json ----

    [Fact]
    public void FencesJsonGivesTheSectionsOfEachGroupInGodotAxesAndSkipsTheRest()
    {
        var book = FenceBook.Parse("""
            {"version":1,"axes":"x","fences":[
             {"parcel":"P-1","group":"G-0003","a":[10,20],"b":[14,20],"za":5.0,"zb":5.5,"height_m":1.2,"type":"picket"},
             {"parcel":"P-2","group":null,"a":[0,0],"b":[1,0],"za":0,"zb":0,"height_m":1.2,"type":null},
             {"parcel":"P-3","group":"G-0003","a":[14,20],"b":[14,24],"za":5.5,"zb":5.5,"height_m":1.8,"type":null}]}
            """);
        Assert.Equal(1, book.Groups);
        Assert.Equal(2, book.Sections);
        var s = book.Of("G-0003");
        Assert.Equal(new Vector2(10, -20), s[0].A); // local y north becomes Godot -z
        Assert.Equal(new Vector2(14, -20), s[0].B);
        Assert.Equal((5.0f, 5.5f, 1.2f), (s[0].ZA, s[0].ZB, s[0].HeightM));
        Assert.Equal(1.8f, s[1].HeightM);
        Assert.Empty(book.Of("G-0099"));
        Assert.Equal(5.25f, s[0].GroundAt(0.5f), 0.001f);
        var (point, d, t) = s[0].Nearest(new Vector2(12, -23));
        Assert.Equal(new Vector2(12, -20), point);
        Assert.Equal(3f, d, 0.001f);
        Assert.Equal(0.5f, t, 0.001f);
        // an unknown version, or no sections: an empty book, not a failure
        Assert.Equal(0, FenceBook.Parse("""{"version":2,"fences":[{"group":"G-1","a":[0,0],"b":[1,1]}]}""").Sections);
        Assert.Equal(0, FenceBook.Parse("""{"version":1}""").Sections);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => FenceBook.Parse("{ nope"));
    }

    [Fact]
    public void ThePetNumbersShipValidAndBadOnesAreRefused()
    {
        var f = ThievesFile.Parse(File.ReadAllText(Path.Combine(DataDir, "thieves.json")));
        var p = f.Loot.Pet;
        Assert.Equal(0.7f, p.HopSeconds);
        Assert.True(p.Weight > 0 && p.StealSeconds > 0 && p.FenceCandidates >= 1);
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"pet":{"hop_seconds":0}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"pet":{"fence_candidates":0}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"pet":{"hop_clear_m":-1}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"pet":{"weight":-1}}}"""));
    }

    // ---- the target, the hop and the van ----

    static ThievesFile Cfg(Action<ThievesFile>? tune = null)
    {
        var f = ThievesFile.Parse(File.ReadAllText(Path.Combine(DataDir, "thieves.json")));
        f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 1 };
        f.Van.StaySeconds = 1000;
        tune?.Invoke(f);
        return f;
    }

    sealed class GangWorld : IGangWorld
    {
        public IReadOnlyList<GangPlayer> Players { get; } = new List<GangPlayer>();
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
    }

    sealed class PetWorld : IPetWorld
    {
        public Func<Vector3, bool> Walk = _ => true;
        public float? GroundY(float x, float z) => 0f;
        public bool Walkable(Vector3 at) => Walk(at);
    }

    /// <summary>A Raid with one Thief, one garden (30..42, 10..22) holding one pet, and the pet target over them. The fence is a closed square round it unless <paramref name="fenced"/> is false.</summary>
    sealed class Rig
    {
        public readonly ThievesFile Cfg;
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Gangs;
        public readonly RaidRoster Raids;
        public readonly LootRegistry Registry = new();
        public readonly LootBook Book;
        public readonly PetKeeper Keeper;
        public readonly PetSource Source;
        public readonly PetWorld World = new();
        public readonly Pen Pen = PenAt(30, 10);
        public FenceBook Fences;
        public const string Group = "G-0000";

        public static FenceBook Square12()
        {
            // the fence of the garden at (30..42, 10..22), as fences.json would give it in Godot axes (two sections per side, a few metres each)
            FenceSection S(float x0, float z0, float x1, float z1) => new(new Vector2(x0, z0), new Vector2(x1, z1), 0, 0, 1.2f);
            return new FenceBook(new Dictionary<string, List<FenceSection>>
            {
                [Group] = new()
                {
                    S(30, 10, 36, 10), S(36, 10, 42, 10), S(42, 10, 42, 16), S(42, 16, 42, 22),
                    S(42, 22, 36, 22), S(36, 22, 30, 22), S(30, 22, 30, 16), S(30, 16, 30, 10),
                },
            });
        }

        public Rig(Action<ThievesFile>? tune = null, bool fenced = true)
        {
            Cfg = PetLootTests.Cfg(tune);
            var types = new WorldEntityRegistry();
            GangRules.Register(types); RaidVanRules.Register(types); LootRules.Register(types); AnimalRules.Register(types); PoopRules.Register(types);
            Entities = new WorldEntityManager(types);
            Gangs = new GangRoster(Cfg.Gang, new GangWorld(), Entities, 7);
            Raids = new RaidRoster(Cfg, new GangWorld(), Entities, Gangs, 9);
            Book = new LootBook(Cfg.Loot, Registry, Entities, 3);
            var animals = Animals();
            var pets = PetsFile.Parse("""
                {"version":1,"group_chance":0,"max_pets":10,"pets_per_group_min":1,"pets_per_group_max":1,"species":[{"id":"dog","weight":1}],
                 "poop_min_seconds":1000,"poop_max_seconds":2000,"poop_lifetime_seconds":30,"poop_max_per_group":3,
                 "pinned":[{"group":"G-0000","species":"dog","count":1}]}
                """).CheckAgainst(animals);
            Keeper = new PetKeeper(pets, animals, Entities, 5);
            Assert.Equal(1, Keeper.Populate(new Dictionary<string, Pen> { [Group] = Pen }, new Flat()));
            World.Walk = p => !Pen.Contains(p.X, p.Z); // the mesh stops at the fence
            Fences = fenced ? Square12() : FenceBook.Empty;
            Source = new PetSource(Cfg.Loot.Pet, Keeper, () => Fences, World);
            Source.CarriedKilled += e => { foreach (var g in Gangs.Gangs) foreach (var m in g.Members) if (m.CarriedOrigin is { Kind: LootKind.Pet } o && o.Id == e) m.LoseCarried(); };
            Registry.Register(Source);
            Raids.Loot = Book;
        }

        public Animal Pet => Keeper.Pets.First();
        public ushort PetEntity => Keeper.EntityOf(Pet)!.Value;

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
            Keeper.Update(Dt);
            Raids.Tick(Dt);
            Gangs.ServeRoute(Route);
        }

        // the mesh goes round the garden: a straight line that would cross it goes by the corners of a ring 3 m outside it (the shorter way round)
        static readonly Vector3[] Ring = { new(27, 0, 7), new(45, 0, 7), new(45, 0, 25), new(27, 0, 25) };

        bool Crosses(Vector3 a, Vector3 b)
        {
            for (var i = 0; i <= 40; i++) { var p = Vector3.Lerp(a, b, i / 40f); if (Inside(p)) return true; }
            return false;
        }

        IReadOnlyList<Vector3> Route(Vector3 from, Vector3 to)
        {
            if (!Crosses(from, to)) return new[] { from, to };
            int Nearest(Vector3 p) { var best = 0; for (var i = 1; i < 4; i++) if (Vector3.Distance(Ring[i], p) < Vector3.Distance(Ring[best], p)) best = i; return best; }
            var i0 = Nearest(from); var i1 = Nearest(to);
            List<Vector3> Way(int dir) { var l = new List<Vector3> { from }; for (var i = i0; ; i = (i + dir + 4) % 4) { l.Add(Ring[i]); if (i == i1) break; } l.Add(to); return l; }
            float Len(List<Vector3> l) { var d = 0f; for (var i = 1; i < l.Count; i++) d += Vector3.Distance(l[i - 1], l[i]); return d; }
            var a = Way(1); var b = Way(-1);
            return Len(a) <= Len(b) ? a : b;
        }

        public void RunUntil(Func<bool> done, float limit = 300) { for (var t = 0f; t < limit && !done(); t += Dt) Step(); Assert.True(done(), "gave up waiting"); }
        public void Run(float seconds) { for (var t = 0f; t < seconds; t += Dt) Step(); }

        public GangMember ThiefOut(Raid raid)
        {
            RunUntil(() => raid.Gang.Members.Any(m => m.Kind == GangMemberKind.Thief));
            return raid.Gang.Members.First(m => m.Kind == GangMemberKind.Thief);
        }

        public bool Inside(Vector3 p) => Pen.Contains(p.X, p.Z);
    }

    [Fact]
    public void AFencedPetsTargetIsAHopPointOutsideTheNearestSectionAndALandingPointInside()
    {
        var rig = new Rig();
        var found = new List<StealTarget>();
        rig.Source.FreeNear(new Vector3(60, 0, 0), 90, found);
        var t = Assert.Single(found);
        Assert.Equal(LootKind.Pet, t.Kind);
        Assert.Equal(rig.PetEntity, t.Id);
        Assert.Equal(rig.Pet.Position, t.Position);
        var hop = t.Hop!.Value;
        Assert.Equal(hop.Out, t.WorkSpot); // the Thief stands outside the fence
        Assert.False(rig.Inside(hop.Out));
        Assert.True(rig.Inside(hop.In));
        Assert.Equal(1.2f, hop.HeightM);
        Assert.False(t.NeedsClimb);
        // the nearest section to the pet: the two points are across it, in line with the pet's nearest point of it
        var pet = new Vector2(rig.Pet.Position.X, rig.Pet.Position.Z);
        var best = rig.Fences.Of(Rig.Group).MinBy(s => s.Nearest(pet).Distance);
        var side = (Vector2 p) => Vector2.Dot(p - best.A, new Vector2(-(best.B - best.A).Y, (best.B - best.A).X));
        Assert.True(side(new Vector2(hop.Out.X, hop.Out.Z)) * side(new Vector2(hop.In.X, hop.In.Z)) < 0, "the hop does not cross the section");
        Assert.Equal(Vector2.Distance(best.Nearest(pet).Point, new Vector2(hop.Out.X, hop.Out.Z)), rig.Cfg.Loot.Pet.HopOutM, 0.01f);
        var places = new List<Vector2>();
        rig.Source.Places(places);
        Assert.Single(places);
        Assert.True(rig.Source.TryFind(rig.PetEntity, out _));
        // out of the radius: not listed
        found.Clear();
        rig.Source.FreeNear(new Vector3(500, 0, 0), 20, found);
        Assert.Empty(found);
    }

    [Fact]
    public void WithoutAFenceItIsAPlainWalkToThePetAndAnUnreachablePenIsNoTarget()
    {
        var rig = new Rig(fenced: false);
        rig.World.Walk = _ => true; // no fence: the garden's ground is on the mesh
        Assert.True(rig.Source.TryFind(rig.PetEntity, out var t));
        Assert.Null(t.Hop);
        Assert.Equal(rig.Pet.Position, t.WorkSpot);
        // a garden with no fence whose ground is off the mesh: not a target
        rig.World.Walk = _ => false;
        rig.Run(3); // the plan is rechecked once the pet has moved on
        rig.Source.Reset();
        Assert.False(rig.Source.TryFind(rig.PetEntity, out _));
        var found = new List<StealTarget>();
        rig.Source.FreeNear(Vector3.Zero, 500, found);
        Assert.Empty(found);
        // a fenced garden none of whose nearest sections has a hop point on the mesh: not a target either
        var fenced = new Rig();
        fenced.World.Walk = _ => false;
        Assert.False(fenced.Source.TryFind(fenced.PetEntity, out _));
    }

    [Fact]
    public void AThiefHopsInWalksToThePetGrabsItHopsOutAndLoadsItAndThePetEntityEnds()
    {
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        var seen = new List<GangMemberState>();
        var maxY = 0f; var hopInFrom = Vector3.Zero; var hopInTo = Vector3.Zero; var pose = new List<(GangMemberState S, Vector3 At)>();
        var last = (GangMemberState)255;
        rig.RunUntil(() =>
        {
            var s = thief.State;
            if (s != last)
            {
                seen.Add(s);
                if (s == GangMemberState.HopIn) hopInFrom = thief.Position;
                if (last == GangMemberState.HopIn) hopInTo = thief.Position;
                last = s;
            }
            if (s == GangMemberState.HopIn) maxY = Math.Max(maxY, thief.Position.Y);
            if (s is GangMemberState.Haul or GangMemberState.Steal or GangMemberState.HopOut) pose.Add((s, thief.Position));
            return raid.Van.Load == 1;
        });
        // the states, in order, one after another (Seek comes first of all; the Thief seeks again after loading)
        var order = seen.Where(s => s is GangMemberState.Approach or GangMemberState.HopIn or GangMemberState.Steal or GangMemberState.HopOut or GangMemberState.Haul or GangMemberState.Load).ToList();
        Assert.Equal(new[] { GangMemberState.Approach, GangMemberState.HopIn, GangMemberState.Steal, GangMemberState.HopOut, GangMemberState.Haul, GangMemberState.Load }, order.Take(6));
        Assert.False(rig.Inside(hopInFrom));   // took off outside
        Assert.True(rig.Inside(hopInTo));      // landed inside, the other side of the section
        Assert.True(maxY >= 1.2f, $"the arc peaked at {maxY}");
        Assert.All(pose.Where(p => p.S == GangMemberState.Steal), p => Assert.True(rig.Inside(p.At)));
        Assert.All(pose.Where(p => p.S == GangMemberState.Haul), p => Assert.False(rig.Inside(p.At))); // hauling outside the fence
        Assert.True(raid.Loaded.Count == 1 && raid.Loaded[0].Kind == LootKind.Pet);
        rig.Run(0.5f);
        Assert.Empty(rig.Entities.OfType(AnimalRules.TypeId)); // loaded in the van: the pet entity ended
        Assert.Equal(0, rig.Keeper.Count);
        Assert.Equal(1, rig.Source.HeldCount);
    }

    [Fact]
    public void ACarriedPetTravelsWithTheThiefAndRaisedAtItsSide()
    {
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        rig.Run(0.5f);
        Assert.Equal(AnimalState.Carried, rig.Pet.State);
        var d = rig.Pet.Position - thief.Position;
        var c = rig.Cfg.Loot.Pet;
        Assert.Equal(c.CarryUpM, d.Y, 0.01f);
        Assert.InRange(new Vector2(d.X, d.Z).Length(), 0.3f, 0.8f);
        Assert.Equal(thief.Yaw, rig.Pet.Yaw, 0.001f);
        Assert.Equal(LootKind.Pet, LootKinds.OfCarried(thief.Carried));
        Assert.Empty(rig.Source.FreeNearList()); // nobody else can take it
    }

    [Fact]
    public void ACarrierKilledInTheGardenLeavesAPetThatStandsAndOutsideOneThatWalksHome()
    {
        // killed inside the garden, before the hop out
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.HopOut);
        Assert.True(rig.Inside(thief.Position));
        thief.Kill(4);
        Assert.Equal(0, rig.Book.LyingCount); // no lying Loot
        Assert.Empty(rig.Entities.OfType(LootRules.TypeId));
        Assert.True(rig.Pet.Alive);
        Assert.NotEqual(AnimalState.Carried, rig.Pet.State);
        Assert.True(rig.Inside(rig.Pet.Position));
        Assert.Equal(0, rig.Source.HeldCount);
        Assert.Equal(0, rig.Registry.Claimed);

        // killed while hauling it, out on the road: it walks home
        var rig2 = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid2 = rig2.Launch();
        var thief2 = rig2.ThiefOut(raid2);
        rig2.RunUntil(() => thief2.State == GangMemberState.Haul);
        rig2.Run(1);
        Assert.False(rig2.Inside(thief2.Position));
        thief2.Kill(4);
        Assert.Equal(AnimalState.Walk, rig2.Pet.State);
        Assert.False(rig2.Inside(rig2.Pet.Position));
        rig2.RunUntil(() => rig2.Inside(rig2.Pet.Position), 120);
        rig2.Run(30);
        Assert.True(rig2.Inside(rig2.Pet.Position)); // penned again
    }

    [Fact]
    public void ACalledBackOrFleeingThiefInsideTheFenceIsPutOutsideAtOnce()
    {
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 3);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Steal);
        Assert.True(rig.Inside(thief.Position));
        thief.Board(raid.Van.DoorPoint);
        Assert.False(rig.Inside(thief.Position));
        Assert.False(thief.Hopping);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.Equal(0, rig.Source.HeldCount);
        Assert.NotEqual(AnimalState.Carried, rig.Pet.State);
    }

    [Fact]
    public void AMemberKilledInTheAirOverTheFenceLiesOnTheGround()
    {
        var rig = new Rig(f => f.Loot.Pet.HopSeconds = 3);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.HopIn);
        rig.Run(1.2f);
        Assert.True(thief.Position.Y > 0.5f);
        thief.Kill(4);
        Assert.Equal(0f, thief.RestPoint.Y, 0.05f);
        rig.Run(2);
        Assert.Equal(0f, thief.Position.Y, 0.05f); // it fell
    }

    [Fact]
    public void ThePetKilledInTheThiefsHandsEmptiesItsHandsAndItSeeksAgain()
    {
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Haul);
        var killed = new List<ushort>();
        rig.Source.CarriedKilled += killed.Add;
        var hit = new List<HitTarget>();
        rig.Keeper.AddTargets(hit);
        var target = Assert.Single(hit);
        Assert.Equal(DamageResult.Killed, rig.Keeper.Hit(target.Id, 4, 10)); // any hit that deals damage kills a pet, held or not
        Assert.Single(killed);
        Assert.Equal(0, thief.Carried);
        Assert.Equal(GangMemberState.Seek, thief.State);
        Assert.Equal(0, rig.Source.HeldCount);
        rig.Run(2);
        Assert.Equal(0, raid.Van.Load); // nothing was loaded
        Assert.Empty(raid.Loaded);
    }

    [Fact]
    public void ADestroyedVanPutsThePetBackAtTheVanWalkingHomeAndAMissedRoundDoesNotBringItBack()
    {
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => raid.Van.Load == 1);
        rig.Run(0.5f);
        Assert.Empty(rig.Entities.OfType(AnimalRules.TypeId));
        var door = raid.Van.DoorPoint;
        raid.Van.Hit(9, 1000, false); // destroyed: everything loaded goes back
        var back = rig.Entities.OfType(AnimalRules.TypeId).ToList();
        Assert.Single(back);
        Assert.Equal(1, rig.Keeper.Count);
        Assert.InRange(Vector3.Distance(new Vector3(rig.Pet.Position.X, 0, rig.Pet.Position.Z), new Vector3(door.X, 0, door.Z)), 0f, 4f); // at the van
        Assert.False(rig.Inside(rig.Pet.Position));
        Assert.Equal(AnimalState.Walk, rig.Pet.State);
        rig.RunUntil(() => rig.Inside(rig.Pet.Position), 200);
        Assert.Equal(0, rig.Source.HeldCount);
        Assert.True(rig.Source.TryFind(rig.PetEntity, out _)); // free to steal again

        // a round that was cleared since: the old record brings nothing back
        var rig2 = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid2 = rig2.Launch();
        rig2.ThiefOut(raid2);
        rig2.RunUntil(() => raid2.Van.Load == 1);
        rig2.Run(0.5f);
        rig2.Keeper.Clear(); // a new round
        raid2.Van.Hit(9, 1000, false);
        Assert.Empty(rig2.Entities.OfType(AnimalRules.TypeId));
    }

    [Fact]
    public void AResetBringsALoadedPetHomeAndAThiefThatFledWithItTakesItOffTheMapUntilThen()
    {
        var rig = new Rig(f => f.Loot.Pet.StealSeconds = 1);
        var raid = rig.Launch();
        rig.ThiefOut(raid);
        rig.RunUntil(() => raid.Van.Load == 1);
        rig.Run(0.5f);
        Assert.Equal(0, rig.Keeper.Count);
        rig.Book.Reset(); // the event was cancelled: it never happened
        Assert.Equal(1, rig.Keeper.Count);
        Assert.True(rig.Inside(rig.Pet.Position)); // at its home
        Assert.Equal(0, rig.Source.HeldCount);
    }

    [Fact]
    public void ThePetTargetDoesNotLieWhenDroppedAndAPetOutOnTheOpenGroundIsAPlainWalk()
    {
        var rig = new Rig();
        Assert.False(rig.Source.LiesWhenDropped);
        rig.Pet.Carry();
        rig.Pet.MoveCarried(new Vector3(20, 1, 12), 0);
        rig.Pet.Release(); // put down outside its garden
        Assert.False(rig.Inside(rig.Pet.Position));
        Assert.True(rig.Source.TryFind(rig.PetEntity, out var t));
        Assert.Null(t.Hop); // no fence to hop: it is on open ground
        Assert.Equal(rig.Pet.Position, t.WorkSpot);
    }
}

static class PetSourceTestExtensions
{
    public static List<StealTarget> FreeNearList(this PetSource s)
    {
        var into = new List<StealTarget>();
        s.FreeNear(Vector3.Zero, 10000, into);
        return into;
    }
}
