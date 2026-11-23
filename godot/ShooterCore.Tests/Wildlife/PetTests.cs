using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Tests.Wildlife;

/// <summary>Pets v1: the pen, a penned animal that never leaves it or flees, pets.json, the poop rules and the keeper that puts pets into property groups.</summary>
public class PetTests
{
    const float Dt = 0.05f;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    // ---- the pen ----

    static Vector2[] Square(float x0, float z0, float x1, float z1) => new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1) };

    /// <summary>Two parcels: a 10 x 10 square with a 2 x 2 hole in the middle (4..6), and another 10 x 10 square far to the east (20..30).</summary>
    static Pen TwoParcelPen() => new(new IReadOnlyList<Vector2[]>[]
    {
        new[] { Square(0, 0, 10, 10), Square(4, 4, 6, 6) },
        new[] { Square(20, 0, 30, 10) },
    });

    [Fact]
    public void PenContainsTheParcelsAndNotTheHoleOrTheGapBetween()
    {
        var pen = TwoParcelPen();
        Assert.True(pen.Contains(1, 1));
        Assert.True(pen.Contains(9, 9));
        Assert.True(pen.Contains(25, 5));
        Assert.False(pen.Contains(5, 5));    // the hole
        Assert.False(pen.Contains(15, 5));   // between the parcels
        Assert.False(pen.Contains(-1, 5));
        Assert.False(pen.Contains(25, 11));
        Assert.Equal(96 + 100, pen.AreaM2, 0.01f);
    }

    [Fact]
    public void PenRandomPointsAreInsideBothParcelsNeverInTheHoleAndFollowTheArea()
    {
        var pen = TwoParcelPen();
        var rng = new Random(7);
        int west = 0, east = 0;
        for (var i = 0; i < 4000; i++)
        {
            var p = pen.RandomPoint(rng) ?? throw new Exception("no point");
            Assert.True(pen.Contains(p.X, p.Y), $"({p.X}, {p.Y})");
            if (p.X < 15) west++; else east++;
        }
        Assert.InRange(west / 4000.0, 96.0 / 196 - 0.05, 96.0 / 196 + 0.05); // parcels weighted by area, the hole taken out
        Assert.True(east > 1500);
    }

    [Fact]
    public void EmptyPenHasNoPointAndContainsNothing()
    {
        var pen = new Pen(new IReadOnlyList<Vector2[]>[] { Array.Empty<Vector2[]>(), new[] { new[] { new Vector2(0, 0), new Vector2(1, 1) } } });
        Assert.True(pen.IsEmpty);
        Assert.Null(pen.RandomPoint(new Random(1)));
        Assert.False(pen.Contains(0, 0));
    }

    [Fact]
    public void PropertyGroupsReadTheGroupsOfAnEditsFile()
    {
        var groups = PropertyGroups.Parse("""
            { "version": 1, "objects": [], // comment
              "groups": { "G-0001": { "fence": {"mode":"custom"}, "parcels": ["P-0026", "P-0027"] }, "G-0002": { "parcels": [] }, "G-0003": { "parcels": ["P-0505"] }, } }
            """);
        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "P-0026", "P-0027" }, groups["G-0001"]);
        Assert.Empty(PropertyGroups.Parse("{\"version\":1}"));
    }

    // ---- a penned animal ----

    sealed class FlatWorld : IAnimalWorld
    {
        public readonly List<Vector3> Threats = new();
        public Func<float, float, bool>? NoGround;
        public float? GroundY(float x, float z) => NoGround?.Invoke(x, z) == true ? null : 0f;

        public Vector3? NearestThreat(Vector3 from, float radius)
        {
            foreach (var t in Threats) if (Vector3.Distance(t, from) <= radius) return t;
            return null;
        }

        public Vector3? PositionOf(ushort player) => new Vector3(-5, 0, 0);
    }

    static AnimalsFile Species(bool flees, float wander = 20) => AnimalsFile.Parse($$"""
        {"version":1,"idle_min_seconds":0.5,"idle_max_seconds":2,"eat_min_seconds":0.5,"eat_max_seconds":1.5,"alarm_seconds":5,"corpse_seconds":3,
        "animals":[{"id":"dog","name":"Dog","model":"animated-animals/dog.glb","scale":0.2,"hp":30,"flees":{{(flees ? "true" : "false")}},
        "walk_speed":1.4,"run_speed":6,"turn_rate_deg_per_s":300,"flee_radius":20,"wander_radius":{{wander}},"hit_box":[0.2,0.55,0.7]}]}
        """);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void APennedPetStaysInsideItsPenOverALongSeededRun(int seed)
    {
        // a pen much smaller than the wander radius, so most wander targets lie outside it
        var pen = new Pen(new IReadOnlyList<Vector2[]>[] { new[] { Square(0, 0, 8, 6) } });
        var world = new PennedWorld(new FlatWorld(), pen);
        var pet = new Animal(Species(false), 0, new Vector3(4, 0, 3), 0f, world, seed);
        var walked = false;
        var farthest = 0f;
        for (var t = 0f; t < 1800; t += Dt) // half an hour
        {
            pet.Tick(Dt);
            Assert.True(pen.Contains(pet.Position.X, pet.Position.Z), $"seed {seed} t={t}: left the pen at ({pet.Position.X}, {pet.Position.Z})");
            walked |= pet.State == AnimalState.Walk;
            farthest = MathF.Max(farthest, Vector3.Distance(pet.Position, pet.Home));
        }
        Assert.True(walked);
        Assert.True(farthest > 2f, "it should actually wander about the pen");
    }

    [Fact]
    public void PennedWorldRefusesGroundOutsideAndFearsNothing()
    {
        var flat = new FlatWorld();
        flat.Threats.Add(new Vector3(1, 0, 1));
        var world = new PennedWorld(flat, new Pen(new IReadOnlyList<Vector2[]>[] { new[] { Square(0, 0, 8, 6) } }));
        Assert.Equal(0f, world.GroundY(1, 1));
        Assert.Null(world.GroundY(9, 1));
        Assert.Null(world.NearestThreat(new Vector3(1, 0, 1), 50));
        Assert.NotNull(world.PositionOf(3));
    }

    [Fact]
    public void ASpeciesThatDoesNotFleeNeverRunsFromThreatsOrShots()
    {
        var world = new FlatWorld();
        var pet = new Animal(Species(false), 0, Vector3.Zero, 0f, world, 5);
        world.Threats.Add(new Vector3(2, 0, 0)); // a player right next to it, for good
        var seen = new HashSet<AnimalState>();
        for (var t = 0f; t < 60; t += Dt) { pet.Tick(Dt); seen.Add(pet.State); }
        Assert.DoesNotContain(AnimalState.Flee, seen);

        var before = pet.Serialize().State & AnimalRules.HitToggle;
        pet.OnDamage(5, 7); // shot, not dead
        Assert.NotEqual(before, pet.Serialize().State & AnimalRules.HitToggle); // the reaction still plays
        for (var t = 0f; t < 30; t += Dt) { pet.Tick(Dt); seen.Add(pet.State); }
        Assert.DoesNotContain(AnimalState.Flee, seen);
        Assert.Equal(25, pet.Hp);
    }

    [Fact]
    public void ASpeciesThatFleesStillFlees()
    {
        var world = new FlatWorld();
        var deer = new Animal(Species(true), 0, Vector3.Zero, 0f, world, 5);
        deer.OnDamage(5, 7);
        Assert.Equal(AnimalState.Flee, deer.State);
    }

    [Fact]
    public void ANonFleeingPetStillDiesOfAShotAndIsRemovedAfterItsCorpseTime()
    {
        var world = new FlatWorld();
        var pet = new Animal(Species(false), 0, Vector3.Zero, 0f, world, 5);
        ushort killer = 0;
        pet.Died += (_, by) => killer = by;
        pet.OnDamage(10, 3);
        pet.OnDamage(100, 4);
        Assert.False(pet.Alive);
        Assert.Equal(AnimalState.Dead, pet.State);
        Assert.Equal((ushort)4, killer);
        for (var t = 0f; t < 2.5f; t += Dt) pet.Tick(Dt);
        Assert.Null(pet.Ended);
        for (var t = 0f; t < 1f; t += Dt) pet.Tick(Dt);
        Assert.Equal(WorldDespawnReason.Expired, pet.Ended);
    }

    // ---- pets.json ----

    [Fact]
    public void ShippedPetsLoadAndNameRealSpecies()
    {
        var data = GameData.Load(DataDir);
        Assert.True(data.Pets.GroupChance > 0);
        Assert.NotEmpty(data.Pets.Species);
        foreach (var s in data.Pets.Species)
        {
            var def = data.Animals.Find(s.Id);
            Assert.NotNull(def);
            Assert.False(def!.Flees, s.Id); // a pet does not flee
        }
    }

    const string GoodPets = """
        {"version":1,"group_chance":0.5,"max_pets":10,"pets_per_group_min":1,"pets_per_group_max":2,"species":[{"id":"dog","weight":1}],
         "poop_min_seconds":10,"poop_max_seconds":20,"poop_lifetime_seconds":30,"poop_max_per_group":3}
        """;

    [Theory]
    [InlineData("\"group_chance\":0.5", "\"group_chance\":1.5")]
    [InlineData("\"group_chance\":0.5", "\"group_chance\":-0.1")]
    [InlineData("\"max_pets\":10", "\"max_pets\":-1")]
    [InlineData("\"max_pets\":10", "\"max_pets\":1000")]
    [InlineData("\"pets_per_group_min\":1", "\"pets_per_group_min\":0")]
    [InlineData("\"pets_per_group_min\":1", "\"pets_per_group_min\":3")]
    [InlineData("\"poop_min_seconds\":10", "\"poop_min_seconds\":0")]
    [InlineData("\"poop_min_seconds\":10", "\"poop_min_seconds\":25")]
    [InlineData("\"poop_lifetime_seconds\":30", "\"poop_lifetime_seconds\":0")]
    [InlineData("\"poop_max_per_group\":3", "\"poop_max_per_group\":0")]
    [InlineData("\"weight\":1", "\"weight\":0")]
    [InlineData("\"species\":[{\"id\":\"dog\",\"weight\":1}]", "\"species\":[]")]
    [InlineData("\"species\":[{\"id\":\"dog\",\"weight\":1}]", "\"species\":[{\"id\":\"dog\",\"weight\":1},{\"id\":\"dog\",\"weight\":2}]")]
    [InlineData("\"version\":1", "\"version\":9")]
    public void BadPetsAreRefused(string good, string bad)
    {
        PetsFile.Parse(GoodPets);
        Assert.Contains(good, GoodPets);
        Assert.Throws<DataFileException>(() => PetsFile.Parse(GoodPets.Replace(good, bad)));
    }

    [Fact]
    public void ASpeciesThatIsNotInAnimalsJsonIsRefused()
    {
        var animals = Species(false);
        PetsFile.Parse(GoodPets).CheckAgainst(animals);
        var ex = Assert.Throws<DataFileException>(() => PetsFile.Parse(GoodPets.Replace("\"dog\"", "\"dragon\"")).CheckAgainst(animals));
        Assert.Contains("dragon", ex.Message);
    }

    [Fact]
    public void GameDataRefusesPetsOfAnUnknownSpeciesAndDefaultsToNoPets()
    {
        Assert.Equal(0, new PetsFile().GroupChance);
        Assert.Throws<DataFileException>(() => PetsFile.Parse(GoodPets.Replace("\"dog\"", "\"dragon\"")).CheckAgainst(GameData.Load(DataDir).Animals));
    }

    [Fact]
    public void ASpeciesIsPickedByWeight()
    {
        var animals = GameData.Load(DataDir).Animals;
        var pets = PetsFile.Parse(GoodPets.Replace("[{\"id\":\"dog\",\"weight\":1}]", "[{\"id\":\"dog\",\"weight\":3},{\"id\":\"cat\",\"weight\":1}]"));
        Assert.Equal(animals.IndexOf("dog"), pets.Pick(animals, 0.0));
        Assert.Equal(animals.IndexOf("dog"), pets.Pick(animals, 0.74));
        Assert.Equal(animals.IndexOf("cat"), pets.Pick(animals, 0.76));
        Assert.Equal(animals.IndexOf("cat"), pets.Pick(animals, 0.9999));
    }

    // ---- poop ----

    static PetsFile Rules(float min = 10, float max = 10, float life = 30, int cap = 3) => PetsFile.Parse($$"""
        {"version":1,"group_chance":1,"max_pets":50,"pets_per_group_min":1,"pets_per_group_max":1,"species":[{"id":"dog","weight":1}],
         "poop_min_seconds":{{min}},"poop_max_seconds":{{max}},"poop_lifetime_seconds":{{life}},"poop_max_per_group":{{cap}}}
        """);

    [Fact]
    public void PoopComesOnTheCadenceAndRestartsItsWait()
    {
        var s = new PoopSchedule(Rules(10, 10), new Random(1));
        var drops = new List<float>();
        for (var t = 0f; t < 35; t += Dt)
            if (s.Tick(Dt, true, AnimalState.Idle)) drops.Add(t);
        Assert.Equal(3, drops.Count);
        Assert.InRange(drops[0], 9.8f, 10.3f);
        Assert.InRange(drops[1] - drops[0], 9.8f, 10.3f);
    }

    [Fact]
    public void PoopWaitsForAPetThatWalksAndNeverComesFromADeadOne()
    {
        var s = new PoopSchedule(Rules(5, 5), new Random(1));
        for (var t = 0f; t < 20; t += Dt) Assert.False(s.Tick(Dt, true, AnimalState.Walk)); // due long ago, but walking
        Assert.Equal(0f, s.Left);
        Assert.False(s.Tick(Dt, false, AnimalState.Idle)); // dead
        Assert.True(s.Tick(Dt, true, AnimalState.Eat));    // eating counts as standing still
        Assert.True(s.Left > 4f); // the wait started over
        var again = new PoopSchedule(Rules(5, 5), new Random(1));
        for (var t = 0f; t < 20; t += Dt) Assert.False(again.Tick(Dt, true, AnimalState.Flee));
        Assert.True(again.Tick(Dt, true, AnimalState.Idle));
    }

    [Fact]
    public void ThePoopWaitIsRandomBetweenTheLimits()
    {
        var rng = new Random(3);
        var waits = Enumerable.Range(0, 200).Select(_ => new PoopSchedule(Rules(60, 180), rng).Left).ToList();
        Assert.All(waits, w => Assert.InRange(w, 60f, 180f));
        Assert.True(waits.Max() - waits.Min() > 60f);
    }

    [Fact]
    public void AGroupKeepsAtMostItsCapAndTheOldestGoFirst()
    {
        var book = new PoopBook(3);
        Assert.Empty(book.Add("G-1", 1));
        Assert.Empty(book.Add("G-1", 2));
        Assert.Empty(book.Add("G-2", 3));
        Assert.Empty(book.Add("G-1", 4));
        Assert.Equal(new ushort[] { 1 }, book.Add("G-1", 5));
        Assert.Equal(3, book.Count("G-1"));
        Assert.Equal(1, book.Count("G-2"));
        book.Forget(2); // expired by itself
        Assert.Empty(book.Add("G-1", 6));
        Assert.Equal(new ushort[] { 4 }, book.Add("G-1", 7));
        book.Forget(99);
        Assert.Equal(4, book.Total);
    }

    [Fact]
    public void APoopExpiresAfterItsLifetimeAndHasNoHitPoints()
    {
        var registry = new WorldEntityRegistry();
        PoopRules.Register(registry);
        Assert.False(registry.Find(PoopRules.TypeId)!.HasHp);
        var poop = new Poop(new Vector3(1, 2, 3), 0.5f, 30);
        for (var t = 0f; t < 29.5f; t += Dt) poop.Tick(Dt);
        Assert.Null(poop.Ended);
        poop.OnDamage(1000, 1);
        Assert.Null(poop.Ended);
        for (var t = 0f; t < 1f; t += Dt) poop.Tick(Dt);
        Assert.Equal(WorldDespawnReason.Expired, poop.Ended);
        Assert.Equal(new Vector3(1, 2, 3), poop.Serialize().Position, new V3Comparer());
        Assert.Equal(PoopRules.TypeId, poop.TypeId);
    }

    sealed class V3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.02f;
        public int GetHashCode(Vector3 v) => 0;
    }

    [Fact]
    public void PoopTypeIdIsFreeOnTheWire()
    {
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        CitizenRules.Register(registry);
        DemonRules.Register(registry);
        PoopRules.Register(registry); // throws when the id is taken
        Assert.NotEqual(DemonRules.TypeId, PoopRules.TypeId);
    }

    // ---- the keeper ----

    static (PetKeeper Keeper, WorldEntityManager Entities, AnimalsFile Animals) MakeKeeper(PetsFile pets, int seed = 1)
    {
        var animals = GameData.Load(DataDir).Animals;
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        PoopRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        return (new PetKeeper(pets.CheckAgainst(animals), animals, entities, seed), entities, animals);
    }

    static Dictionary<string, Pen> Pens(int groups) => Enumerable.Range(0, groups).ToDictionary(
        i => $"G-{i:D4}", i => new Pen(new IReadOnlyList<Vector2[]>[] { new[] { Square(i * 100, 0, i * 100 + 12, 12) } }));

    static PetsFile Pinned(string pinned, float groupChance = 0) => PetsFile.Parse(GoodPets
        .Replace("\"group_chance\":0.5", FormattableString.Invariant($"\"group_chance\":{groupChance},\"pinned\":{pinned}")));

    [Fact]
    public void APinnedPetIsInItsGroupEvenWhenNoGroupRollsPets()
    {
        var (keeper, _, animals) = MakeKeeper(Pinned("""[{"group":"G-0003","species":"cat","count":2}]"""));
        Assert.Equal(2, keeper.Populate(Pens(10), new FlatWorld()));
        Assert.All(keeper.Pets, p => { Assert.Equal("G-0003", keeper.GroupOf(p)); Assert.Equal(animals.IndexOf("cat"), p.Species); });
    }

    [Fact]
    public void APinnedPetsChanceIsRolledEachRound()
    {
        var (never, _, _) = MakeKeeper(Pinned("""[{"group":"G-0003","species":"cat","chance":0}]"""));
        Assert.Equal(0, never.Populate(Pens(10), new FlatWorld()));
        var (half, _, _) = MakeKeeper(Pinned("""[{"group":"G-0003","species":"cat","chance":0.5}]"""));
        var rounds = Enumerable.Range(0, 200).Sum(_ => half.Populate(Pens(10), new FlatWorld()));
        Assert.InRange(rounds, 60, 140);
    }

    [Fact]
    public void PinnedPetsComeBeforeTheRandomOnesUnderTheCapAndAnUnknownGroupIsLogged()
    {
        var pets = Pinned("""[{"group":"G-0009","species":"cat","count":3},{"group":"G-9999","species":"cat"}]""", groupChance: 1);
        var (keeper, _, animals) = MakeKeeper(pets);
        var log = new List<string>();
        keeper.Log = log.Add;
        Assert.Equal(10, keeper.Populate(Pens(10), new FlatWorld())); // max_pets
        Assert.Equal(3, keeper.Pets.Count(p => p.Species == animals.IndexOf("cat")));
        Assert.Contains(log, m => m.Contains("G-9999"));
    }

    static AnimalsFile WithVariants() => AnimalsFile.Parse($$"""
        {"version":1,"animals":[{"id":"dog","name":"Dog","model":"a/dog.glb","hp":40,"walk_speed":1,"run_speed":2,"flee_radius":5,"wander_radius":8,
          "hit_box":[0.3,0.6,0.8],"flees":false,"variants":[{"id":"corgi","model":"a/corgi.glb"},{"id":"pug","model":"a/pug.glb"}]}]}
        """);

    static PetKeeper VariantKeeper(PetsFile pets, AnimalsFile animals)
    {
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        PoopRules.Register(registry);
        return new PetKeeper(pets.CheckAgainst(animals), animals, new WorldEntityManager(registry), 1);
    }

    [Fact]
    public void APinnedPetWearsTheAssetItNamesAndTheWireCarriesIt()
    {
        var animals = WithVariants();
        var keeper = VariantKeeper(Pinned("""[{"group":"G-0003","species":"dog","asset":"pug","count":4},{"group":"G-0004","species":"dog","asset":"base"}]"""), animals);
        Assert.Equal(5, keeper.Populate(Pens(10), new FlatWorld()));
        foreach (var pet in keeper.Pets)
        {
            var want = keeper.GroupOf(pet) == "G-0003" ? 2 : 0;
            Assert.Equal(want, pet.Variant);
            var look = pet.Serialize().Owner;
            Assert.Equal((0, want), (AnimalRules.SpeciesOf(look), AnimalRules.VariantOf(look)));
        }
        Assert.Equal("a/pug.glb", animals.Animals[0].ModelOf(2));
        Assert.Equal("a/dog.glb", animals.Animals[0].ModelOf(0));
    }

    [Fact]
    public void APetWithNoNamedAssetWearsARandomLookOfItsSpecies()
    {
        var keeper = VariantKeeper(Pinned("""[{"group":"G-0003","species":"dog","count":10}]"""), WithVariants());
        keeper.Populate(Pens(10), new FlatWorld());
        Assert.Equal(new[] { 0, 1, 2 }, keeper.Pets.Select(p => p.Variant).Distinct().Order());
    }

    [Fact]
    public void AVariantMayNotTakeTheReservedBaseId() =>
        Assert.Throws<DataFileException>(() => AnimalsFile.Parse("""
            {"version":1,"animals":[{"id":"dog","name":"Dog","model":"a/dog.glb","hp":40,"walk_speed":1,"run_speed":2,"flee_radius":5,"wander_radius":8,
              "hit_box":[0.3,0.6,0.8],"flees":false,"variants":[{"id":"base","model":"a/corgi.glb"}]}]}
            """));

    [Fact]
    public void ADegenerateOuterRingDropsTheWholeParcelAndNoHoleStandsInForIt()
    {
        var pen = new Pen(new IReadOnlyList<Vector2[]>[]
        {
            new[] { new[] { new Vector2(0, 0), new Vector2(1, 1) }, Square(4, 4, 6, 6) }, // outer ring too short; the hole must not become the parcel
            new[] { Square(20, 0, 30, 10) },
        });
        Assert.False(pen.Contains(5, 5));
        Assert.True(pen.Contains(25, 5));
        Assert.Equal(100f, pen.AreaM2, 3);
    }

    [Fact]
    public void AFullEntityManagerStopsPopulatingWithOneLogLineAndAGroupWithoutGroundOnlySkipsItself()
    {
        var pets = PetsFile.Parse(GoodPets.Replace("\"group_chance\":0.5", "\"group_chance\":1").Replace("\"max_pets\":10", "\"max_pets\":100").Replace("\"pets_per_group_min\":1,\"pets_per_group_max\":2", "\"pets_per_group_min\":3,\"pets_per_group_max\":3"));
        var (keeper, entities, _) = MakeKeeper(pets);
        var log = new List<string>();
        keeper.Log = log.Add;
        for (var i = 0; i < WorldEntityManager.MaxEntities - 4; i++) Assert.NotNull(entities.Spawn(new Poop(Vector3.Zero, 0, 100))); // room for 4 pets
        Assert.Equal(4, keeper.Populate(Pens(10), new FlatWorld()));
        Assert.Single(log, m => m.Contains("full of entities"));

        // a pen without ground is skipped, the other groups still get pets
        var (keeper2, _, _) = MakeKeeper(pets);
        var pens = Pens(3);
        var world = new FlatWorld { NoGround = (x, _) => x < 50 }; // G-0000 only
        Assert.Equal(6, keeper2.Populate(pens, world));
    }

    [Fact]
    public void AnUnknownAssetIsRefused() =>
        Assert.Throws<DataFileException>(() => Pinned("""[{"group":"G-0003","species":"dog","asset":"poodle"}]""").CheckAgainst(WithVariants()));

    [Theory]
    [InlineData("""[{"group":"","species":"cat"}]""")]
    [InlineData("""[{"group":"G-0001","species":"cat","chance":1.5}]""")]
    [InlineData("""[{"group":"G-0001","species":"cat","count":0}]""")]
    public void ABadPinnedPetIsRefused(string pinned) => Assert.Throws<DataFileException>(() => Pinned(pinned));

    [Fact]
    public void APinnedSpeciesMustBeInAnimalsJson() =>
        Assert.Throws<DataFileException>(() => Pinned("""[{"group":"G-0001","species":"dragon"}]""").CheckAgainst(GameData.Load(DataDir).Animals));

    [Fact]
    public void TheKeeperPutsPetsOnlyIntoTheirGroupsPensWithinTheCaps()
    {
        var pets = PetsFile.Parse(GoodPets.Replace("\"group_chance\":0.5", "\"group_chance\":1").Replace("\"max_pets\":10", "\"max_pets\":7").Replace("\"pets_per_group_max\":2", "\"pets_per_group_max\":3"));
        var (keeper, entities, _) = MakeKeeper(pets);
        var pens = Pens(10);
        var n = keeper.Populate(pens, new FlatWorld());
        Assert.Equal(7, n); // the map's cap
        Assert.Equal(7, entities.Count);
        foreach (var pet in keeper.Pets)
        {
            var group = keeper.GroupOf(pet)!;
            Assert.True(pens[group].Contains(pet.Position.X, pet.Position.Z), $"{group}: pet at {pet.Position}");
            Assert.Equal(AnimalRules.TypeId, pet.TypeId);
        }
        Assert.All(keeper.Pets.GroupBy(keeper.GroupOf), g => Assert.InRange(g.Count(), 1, 3));
    }

    [Fact]
    public void WithNoChanceOrNoGroupsThereAreNoPets()
    {
        var (none, e1, _) = MakeKeeper(PetsFile.Parse(GoodPets.Replace("\"group_chance\":0.5", "\"group_chance\":0")));
        Assert.Equal(0, none.Populate(Pens(10), new FlatWorld()));
        Assert.Equal(0, e1.Count);
        var (some, e2, _) = MakeKeeper(PetsFile.Parse(GoodPets));
        Assert.Equal(0, some.Populate(new Dictionary<string, Pen>(), new FlatWorld()));
        Assert.Equal(0, e2.Count);
    }

    [Fact]
    public void AGroupChanceBelowOneGivesAShareOfTheGroupsPets()
    {
        var pets = PetsFile.Parse(GoodPets.Replace("\"max_pets\":10", "\"max_pets\":100").Replace("\"pets_per_group_max\":2", "\"pets_per_group_max\":1"));
        var (keeper, _, _) = MakeKeeper(pets, seed: 9);
        var n = keeper.Populate(Pens(100), new FlatWorld());
        Assert.InRange(n, 30, 70); // half of 100 groups, one pet each
    }

    sealed class NoGround : IAnimalWorld
    {
        public float? GroundY(float x, float z) => null;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
    }

    [Fact]
    public void AGroupWhosePenHasNoGroundGetsNoPets()
    {
        var (keeper, entities, _) = MakeKeeper(PetsFile.Parse(GoodPets.Replace("\"group_chance\":0.5", "\"group_chance\":1")));
        Assert.Equal(0, keeper.Populate(Pens(5), new NoGround()));
        Assert.Equal(0, entities.Count);
    }

    [Fact]
    public void PetsLeavePoopAtTheirPositionTheGroupKeepsAtMostTheCapAndItExpires()
    {
        var pets = PetsFile.Parse(GoodPets.Replace("\"group_chance\":0.5", "\"group_chance\":1").Replace("\"pets_per_group_min\":1", "\"pets_per_group_min\":2")
            .Replace("\"poop_min_seconds\":10", "\"poop_min_seconds\":5").Replace("\"poop_max_seconds\":20", "\"poop_max_seconds\":6").Replace("\"poop_lifetime_seconds\":30", "\"poop_lifetime_seconds\":100"));
        var (keeper, entities, _) = MakeKeeper(pets);
        keeper.Populate(Pens(1), new FlatWorld());
        Assert.Equal(2, keeper.Count);
        var peak = 0;
        var spawned = 0;
        entities.Spawned += s => { if (s.Type == PoopRules.TypeId) spawned++; };
        for (var t = 0f; t < 90; t += Dt)
        {
            entities.Tick(Dt);
            keeper.Update(Dt);
            peak = Math.Max(peak, keeper.PoopCount("G-0000"));
            Assert.True(keeper.PoopCount("G-0000") <= 3);
        }
        Assert.Equal(3, peak);
        Assert.True(spawned > 6, $"{spawned} poops dropped");
        Assert.Equal(3, entities.OfType(PoopRules.TypeId).Count); // the evicted ones were despawned
        // the pets die: no more poop; what lies expires (lifetime 100 s)
        foreach (var pet in keeper.Pets.ToList()) pet.OnDamage(1000, 1);
        var before = spawned;
        for (var t = 0f; t < 110; t += Dt) { entities.Tick(Dt); keeper.Update(Dt); }
        Assert.Equal(before, spawned);
        Assert.Empty(entities.OfType(PoopRules.TypeId));
        Assert.Equal(0, keeper.PoopTotal);
        Assert.Equal(0, keeper.Count); // corpses removed too
    }

    [Fact]
    public void APetIsAHitTargetAndDiesOfOneHitWithoutScoring()
    {
        var (keeper, _, _) = MakeKeeper(PetsFile.Parse(GoodPets.Replace("\"group_chance\":0.5", "\"group_chance\":1")));
        keeper.Populate(Pens(1), new FlatWorld());
        var targets = new List<HitTarget>();
        keeper.AddTargets(targets);
        Assert.Equal(keeper.Count, targets.Count);
        var t = targets[0];
        Assert.True(PetRules.IsHitId(t.Id));
        Assert.True(t.HeadGuarded);
        var pet = keeper.Pets.First();
        Assert.InRange(t.Scale * PlayerHitBox.StandHeight, pet.HitBox[1] - 0.001f, pet.HitBox[1] + 0.001f); // the box of its own look
        Assert.True(pet.Def.OneHit);
        ushort by = 0;
        keeper.Killed += (_, k) => by = k;
        Assert.Equal(DamageResult.Killed, keeper.Hit(t.Id, 9, 1)); // one hit of any size kills; it never ran
        Assert.Equal((ushort)9, by);
        Assert.Equal(AnimalState.Dead, pet.State);
        Assert.Equal(DamageResult.Ignored, keeper.Hit(t.Id, 1, 5)); // gone, nothing left to hit
        var left = new List<HitTarget>();
        keeper.AddTargets(left);
        Assert.DoesNotContain(left, x => x.Id == t.Id);
        Assert.Equal(DamageResult.Ignored, keeper.Hit(PetRules.FirstHitId + 500, 1, 5)); // no such pet
    }

    [Fact]
    public void HitIdRangeIsApartFromEveryOtherBody()
    {
        Assert.False(CitizenRules.IsHitId(PetRules.FirstHitId));
        Assert.False(CitizenRules.IsHitId((ushort)(PetRules.FirstHitId + PetRules.HitIdCount - 1)));
        Assert.False(PetRules.IsHitId(CitizenRules.FirstHitId));
        Assert.False(PetRules.IsHitId(DemonRules.HitId));
        Assert.False(PetRules.IsHitId(TargetDummies.FirstId));
        Assert.False(PetRules.IsHitId(BigfootRules.TargetId));
        Assert.False(PetRules.IsHitId(0x8000)); // rockets
        Assert.False(PetRules.IsHitId(0x4000)); // vehicles
        Assert.True(PetRules.FirstHitId >= FlakRecord.FirstId + 0x400); // above the turret records of any plausible vehicle count
    }
}
