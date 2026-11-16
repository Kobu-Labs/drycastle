using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Wildlife and pets collide (ADR 0028, the Animal row): they stop or steer for a wall, a player and another animal, never spawn or drift into a building, and ask the world's rays rarely.</summary>
public class AnimalCollisionTests
{
    const float Dt = 1f / 30;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>A flat yard with solid boxes (a building, a fence: x0, z0, x1, z1) and players standing in it. Counts the rays it is asked.</summary>
    sealed class Yard : IAnimalWorld
    {
        public readonly List<(float X0, float Z0, float X1, float Z1)> Solids = new();
        public readonly List<Vector3> Players = new();
        public readonly List<Vector3> Threats = new();
        public Func<float, float, bool>? NoGround;
        public int Rays;

        public float? GroundY(float x, float z) => NoGround?.Invoke(x, z) == true ? null : 0f;

        public Vector3? NearestThreat(Vector3 from, float radius)
        {
            foreach (var t in Threats) if (Vector3.Distance(t, from) <= radius) return t;
            return null;
        }

        public Vector3? PositionOf(ushort player) => Vector3.Zero;

        public bool Inside(float x, float z, float r = 0) => Solids.Any(s => x > s.X0 - r && x < s.X1 + r && z > s.Z0 - r && z < s.Z1 + r);

        public bool Fits(float x, float z, float radius) => GroundY(x, z) != null && !Inside(x, z, radius);

        public void PlayersNear(Vector3 at, float radius, List<Vector3> into)
        {
            foreach (var p in Players) if (Vector3.Distance(p, at) <= radius) into.Add(p);
        }

        public float SolidAhead(Vector3 from, Vector3 dir, float maxM, float height)
        {
            Rays++;
            var best = float.PositiveInfinity;
            foreach (var s in Solids)
            {
                float near = 0, far = maxM;
                if (!Slab(from.X, dir.X, s.X0, s.X1, ref near, ref far) || !Slab(from.Z, dir.Z, s.Z0, s.Z1, ref near, ref far)) continue;
                best = MathF.Min(best, near);
            }
            return best;
        }

        static bool Slab(float o, float d, float lo, float hi, ref float near, ref float far)
        {
            if (MathF.Abs(d) < 1e-7f) return o >= lo && o <= hi;
            var a = (lo - o) / d; var b = (hi - o) / d;
            if (a > b) (a, b) = (b, a);
            near = MathF.Max(near, a); far = MathF.Min(far, b);
            return near <= far;
        }
    }

    /// <summary>One species, 0.5 m wide and 1.5 m long, its body centred 0.2 m ahead of the origin (so it spans 0.55 m behind to 0.95 m ahead).</summary>
    static AnimalsFile Small(string extra = "") => AnimalsFile.Parse($$"""
        {"version":1,"idle_min_seconds":0.5,"idle_max_seconds":2,"eat_min_seconds":0.5,"eat_max_seconds":1.5,"alarm_seconds":5,"corpse_seconds":3,{{extra}}
        "animals":[{"id":"deer","name":"Deer","model":"animated-animals/deer.glb","scale":0.33,"hp":100,
        "walk_speed":1.5,"run_speed":10,"turn_rate_deg_per_s":200,"flee_radius":30,"wander_radius":20,"body_ahead":0.2,"hit_box":[0.5,1.4,1.5]}]}
        """);

    static Animal Make(IAnimalWorld w, Vector3? at = null, float yaw = 0, int seed = 1) => new(Small(), 0, at ?? Vector3.Zero, yaw, w, seed);

    static void Run(Animal a, float seconds, Action? each = null)
    {
        for (var t = 0f; t < seconds; t += Dt) { a.Tick(Dt); each?.Invoke(); }
    }

    // ---- the body box ----

    [Fact]
    public void ABoxKnowsItsDistanceToAPointAndItsOverlapWithAnotherBox()
    {
        var b = BodyBox.Of(Vector3.Zero, 0, 0.5f, 1.5f, 0.2f); // faces -Z: spans z from -0.95 to 0.55, x from -0.25 to 0.25
        Assert.Equal(0, b.DistanceTo(0, 0));
        Assert.Equal(0.35f, b.DistanceTo(0.6f, 0), 1e-4f);
        Assert.Equal(1.05f, b.DistanceTo(0, -2.0f), 1e-4f); // 2.0 - 0.95 ahead of the nose
        Assert.Equal(0.45f, b.DistanceTo(0, 1.0f), 1e-4f);  // 1.0 - 0.55 behind the tail
        var turned = BodyBox.Of(Vector3.Zero, MathF.PI / 2, 0.5f, 1.5f, 0.2f); // faces -X
        Assert.Equal(1.05f, turned.DistanceTo(-2.0f, 0), 1e-4f);

        var other = BodyBox.Of(new Vector3(0, 0, -1.9f), 0, 0.5f, 1.5f, 0); // its tail at -1.15, the nose of b at -0.95
        Assert.False(b.Overlaps(other));
        Assert.True(b.Overlaps(BodyBox.Of(new Vector3(0, 0, -1.6f), 0, 0.5f, 1.5f, 0)));
        // a box turned 45 degrees: its corner reaches where an upright box of the same size would not
        var diamond = BodyBox.Of(new Vector3(0.9f, 0, 0), MathF.PI / 4, 0.5f, 1.5f, 0);
        Assert.True(b.Overlaps(diamond));
        Assert.False(b.Overlaps(BodyBox.Of(new Vector3(1.4f, 0, 0.5f), MathF.PI / 4, 0.5f, 1.5f, 0)));
    }

    [Fact]
    public void AKnownClearRunHoldsOnlyAlongItsHeadingAndLength()
    {
        var p = new ClearPath();
        Assert.False(p.Covers(0, 0, 0, 0.1f));
        p.Set(0, 0, 0, 2.4f);
        Assert.True(p.Covers(0, 0, 0, 0.1f));
        Assert.True(p.Covers(0, -2.0f, 0, 0.3f));
        Assert.False(p.Covers(0, -2.3f, 0, 0.3f));      // past the end
        Assert.False(p.Covers(0, 0, 0.5f, 0.1f));       // a new heading
        Assert.False(p.Covers(0.2f, -1, 0, 0.1f));       // strayed sideways
        Assert.True(p.Covers(0, -0.1f, 0.2f, 0.05f));     // a small turn at the start of the run is still inside it
        Assert.False(p.Covers(0, -2.0f, 0.2f, 0.3f));     // the same turn two metres on has put it half a metre off the line
        p.Reset();
        Assert.False(p.Covers(0, 0, 0, 0.1f));
    }

    [Fact]
    public void EverySpeciesHasABodyBoxInsideItsLengthAndTheCowsReachesItsHead()
    {
        var animals = GameData.Load(DataDir).Animals;
        foreach (var d in animals.Animals)
        {
            for (var v = 0; v < d.VariantCount; v++)
                Assert.True(MathF.Abs(d.BodyAheadOf(v)) < d.HitBoxOf(v)[2] / 2, $"{d.Id}/{v}");
            Assert.InRange(d.BodyRadiusOf(0), 0.1f, 0.5f);
        }
        var cow = animals.Find("cow")!;
        var front = cow.BodyAhead + cow.HitBox[2] / 2; // measured from the model: head 1.53 m ahead of its origin, tail 0.89 m behind
        Assert.Equal(1.53f, front, 0.02f);
        Assert.Equal(-0.89f, cow.BodyAhead - cow.HitBox[2] / 2, 0.02f);
    }

    // ---- solid things: building, fence, placed solid, trunk, closed door, parked vehicle ----

    [Fact]
    public void ARunningAnimalStopsShortOfAWallInsteadOfWalkingThroughIt()
    {
        var yard = new Yard();
        yard.Solids.Add((4, -50, 5, 50));
        yard.Threats.Add(new Vector3(-10, 0, 0)); // it runs east, into the wall
        var a = Make(yard);
        var wall = BodyBox.Of(new Vector3(4.55f, 0, 0), 0, 0.9f, 100, 0); // the face is at x = 4: a few cm of swing into it is allowed, not 10
        var farthest = 0f;
        Run(a, 4, () => { farthest = MathF.Max(farthest, a.Position.X); Assert.False(a.Body.Overlaps(wall), $"inside the wall at {a.Position} yaw {a.Yaw} {a.State}"); });
        Assert.InRange(farthest, 2.4f, 3.7f); // it ran up to the wall: nose first, then (steering) along it
        Assert.True(yard.Rays > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AnAnimalInARoomNeverLeavesItWhateverItDoes(int seed)
    {
        var yard = new Yard();
        yard.Solids.Add((-6, -6, -5, 6)); yard.Solids.Add((5, -6, 6, 6)); yard.Solids.Add((-6, -6, 6, -5)); yard.Solids.Add((-6, 5, 6, 6));
        var a = Make(yard, seed: seed);
        for (var round = 0; round < 6; round++)
        {
            Run(a, 20, () => Assert.True(MathF.Abs(a.Position.X) < 5 && MathF.Abs(a.Position.Z) < 5, $"left the room at {a.Position}"));
            yard.Threats.Add(new Vector3(a.Position.X + 3, 0, a.Position.Z - 2)); // a fright now and then: it bolts round the room
            Run(a, 3, () => Assert.True(MathF.Abs(a.Position.X) < 5 && MathF.Abs(a.Position.Z) < 5, $"bolted out of the room at {a.Position}"));
            yard.Threats.Clear();
        }
        Assert.True(yard.Rays > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnAnimalNeverWanderOrFleesIntoABuilding(int seed)
    {
        var yard = new Yard();
        yard.Solids.Add((3, -3, 8, 3));
        var a = Make(yard, seed: seed);
        for (var round = 0; round < 8; round++)
        {
            Run(a, 15, () => Assert.False(yard.Inside(a.Position.X, a.Position.Z), $"inside the building at {a.Position}"));
            yard.Threats.Add(new Vector3(-6, 0, a.Position.Z)); // run east at it
            Run(a, 3, () => Assert.False(yard.Inside(a.Position.X, a.Position.Z), $"ran into the building at {a.Position}"));
            yard.Threats.Clear();
        }
    }

    // ---- players and other animals ----

    [Fact]
    public void AnAnimalStopsForAPlayerAndNeverPushesThem()
    {
        var yard = new Yard();
        var player = new Vector3(0, 0, -5);
        yard.Players.Add(player);
        yard.Threats.Add(new Vector3(0, 0, 4)); // it runs north (-Z), at the player
        var a = Make(yard);
        var closest = float.MaxValue;
        Run(a, 4, () => closest = MathF.Min(closest, a.Body.DistanceTo(player.X, player.Z)));
        Assert.True(closest >= 0.35f - 1e-3f, $"its body came within {closest} m of the player");
        Assert.True(closest < 1.0f, "it did go up to the player");
        Assert.Equal(new Vector3(0, 0, -5), yard.Players[0]); // nothing moves a player
    }

    [Fact]
    public void AnAnimalWalksAwayFromAPlayerItIsAlreadyTouching()
    {
        var yard = new Yard();
        yard.Players.Add(new Vector3(0, 0, -0.5f)); // inside its body box at spawn
        yard.Threats.Add(new Vector3(0, 0, -3)); // so it runs south, away
        var a = Make(yard);
        Run(a, 2);
        Assert.True(a.Position.Z > 5, $"it did not get away: {a.Position}");
    }

    static (Animal Runner, Animal Still) Pair(Yard yard, bool sameHerd)
    {
        var runner = Make(yard);
        var still = Make(yard, new Vector3(0, 0, -5), seed: 2); // never ticked: stands in the way
        var crowd = new AnimalCrowd();
        crowd.Add(runner); crowd.Add(still);
        if (sameHerd)
        {
            var herd = new Herd(Vector3.Zero, 10, 40, 10, 20, 1);
            herd.Add(runner); herd.Add(still);
        }
        yard.Threats.Add(new Vector3(0, 0, 4)); // the runner flees north, into the other
        return (runner, still);
    }

    [Fact]
    public void AnAnimalStopsForAnotherOfADifferentHerdOrNoHerd()
    {
        var yard = new Yard();
        var (runner, still) = Pair(yard, sameHerd: false);
        var closest = float.MaxValue;
        Run(runner, 4, () => { closest = MathF.Min(closest, runner.Body.DistanceBetweenCentres(still.Body)); Assert.False(runner.Body.Overlaps(still.Body), $"bodies overlap at {runner.Position}"); });
        Assert.True(closest < 2.1f, $"it never got up to the other one: {closest} m"); // nose to tail of two 1.5 m bodies is 1.5 m; it may then slide past
    }

    [Fact]
    public void AnAnimalPassesOneOfItsOwnHerd()
    {
        var yard = new Yard();
        var (runner, _) = Pair(yard, sameHerd: true);
        Run(runner, 4);
        Assert.True(runner.Position.Z < -8f, $"a herd mate stopped it at {runner.Position}");
    }

    [Fact]
    public void AnAnimalThatLiesOrIsDeadIsNoObstacle()
    {
        var yard = new Yard();
        var (runner, still) = Pair(yard, sameHerd: false);
        Assert.True(still.Throw(new Vector3(0, 0.1f, 0), Vector3.Zero)); // knocked off its feet and lying
        for (var i = 0; i < 60 && still.State == AnimalState.Thrown; i++) still.Tick(Dt);
        Assert.Equal(AnimalState.Down, still.State);
        Assert.False(still.Obstructs);
        Run(runner, 3);
        Assert.True(runner.Position.Z < -8f, $"it stopped for an animal lying down at {runner.Position}");
    }

    [Fact]
    public void ApetAndWildlifeOfNoHerdStopForEachOtherToo()
    {
        // pets and wildlife share one crowd; neither has a herd here, which is how a pet is
        var yard = new Yard();
        var (runner, still) = Pair(yard, sameHerd: false);
        Assert.Null(runner.Herd); Assert.Null(still.Herd);
        Run(runner, 3);
        Assert.False(runner.Body.Overlaps(still.Body));
    }

    // ---- cost ----

    [Fact]
    public void AStandingAnimalAsksNoRayAndARunningOneAsksOnePerSeveralSteps()
    {
        var yard = new Yard { };
        var a = Make(yard);
        a.OnDamage(0, 1); // nothing: damage 0 is ignored
        Run(a, 1.0f); // standing (idle at first)
        var standing = yard.Rays;
        if (a.State == AnimalState.Idle || a.State == AnimalState.Eat) Assert.Equal(0, standing);

        var b = Make(yard, at: new Vector3(0, 0, 0));
        yard.Rays = 0;
        yard.Threats.Add(new Vector3(0, 0, 9)); // runs north in the open for a while
        var steps = 0;
        var last = b.Position;
        Run(b, 3, () => { if (b.Position != last) { steps++; last = b.Position; } });
        Assert.True(steps > 60, $"it ran {steps} steps");
        Assert.True(yard.Rays / 2 * 8 <= steps, $"{yard.Rays} rays (two a cast) for {steps} running steps");
    }

    [Fact]
    public void AWanderingAnimalAsksFarFewerRaysThanItTakesSteps()
    {
        var yard = new Yard();
        var a = Make(yard, seed: 3);
        var steps = 0;
        var last = a.Position;
        Run(a, 120, () => { if (a.Position != last) { steps++; last = a.Position; } });
        Assert.True(steps > 100);
        Assert.True(yard.Rays * 10 < steps, $"{yard.Rays} rays for {steps} steps");
    }

    // ---- where animals spawn and drift ----

    sealed class House : IAnimalWorld
    {
        public Func<float, float, bool> Occupied = (_, _) => false;
        public float? GroundY(float x, float z) => 0f;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
        public bool Fits(float x, float z, float radius) => !Occupied(x, z);
    }

    [Fact]
    public void WildlifeIsNeverSpawnedInsideABuilding()
    {
        var animals = GameData.Load(DataDir).Animals;
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        var cfg = WildlifeFile.Parse("""{"version":1,"max_animals":40,"species":[{"id":"doe","count":20,"herd":{"min":3,"max":5,"spread_m":10,"range_m":50,"move_min_seconds":10,"move_max_seconds":20}},{"id":"bear","count":6}]}""").CheckAgainst(animals);
        var keeper = new WildlifeKeeper(cfg, animals, new WorldEntityManager(registry), 5);
        var world = new House { Occupied = (x, z) => x > 40 && x < 60 && z > -20 && z < 20 }; // the building covers the first habitat point
        keeper.Populate(_ => new[] { new Vector3(50, 0, 0), new Vector3(200, 0, 0) }, world);
        Assert.True(keeper.Count >= 20);
        Assert.All(keeper.Animals, a => Assert.True(a.Position.X > 100, $"spawned at {a.Position}"));
    }

    [Fact]
    public void AHerdAnchorNeverDriftsToASpotWithNoRoomForABody()
    {
        var herd = new Herd(Vector3.Zero, 10, 50, 1, 2, 7, bodyRadius: 0.4f);
        var world = new House { Occupied = (x, z) => MathF.Abs(x - 30) > 5 || MathF.Abs(z) > 5 }; // room only in a yard 10 m across, 30 m east
        for (var i = 0; i < 400; i++)
        {
            herd.Update(2.5f, world);
            Assert.True(herd.Anchor == herd.Home || !world.Occupied(herd.Anchor.X, herd.Anchor.Z), $"drifted to {herd.Anchor}");
        }
        Assert.NotEqual(herd.Home, herd.Anchor); // it did find the yard
    }

    [Fact]
    public void APetIsNeverSpawnedInsideTheHouseOfItsPen()
    {
        var animals = GameData.Load(DataDir).Animals;
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        PoopRules.Register(registry);
        var pets = PetsFile.Parse("""{"version":1,"group_chance":0,"max_pets":50,"pets_per_group_min":1,"pets_per_group_max":1,"poop_min_seconds":10,"poop_max_seconds":20,"poop_lifetime_seconds":60,"poop_max_per_group":3,"species":[{"id":"cat","weight":1}],"pinned":[{"group":"G-1","species":"cat","count":20}]}""").CheckAgainst(animals);
        var keeper = new PetKeeper(pets, animals, new WorldEntityManager(registry), 3);
        var pen = new Pen(new IReadOnlyList<Vector2[]>[] { new[] { new[] { new Vector2(0, 0), new Vector2(12, 0), new Vector2(12, 12), new Vector2(0, 12) } } });
        var world = new House { Occupied = (x, z) => x < 10 }; // the house fills most of the parcel
        Assert.Equal(20, keeper.Populate(new Dictionary<string, Pen> { ["G-1"] = pen }, world));
        Assert.All(keeper.Pets, p => Assert.True(p.Position.X >= 10, $"spawned in the house at {p.Position}"));
    }
}
