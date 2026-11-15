using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Wildlife v1: wildlife.json, the hit-id range and the keeper that puts the wild species at their habitat points.</summary>
public class WildlifeTests
{
    const float Dt = 0.05f;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    sealed class Flat : IAnimalWorld
    {
        public readonly List<Vector3> Threats = new();
        public Func<float, float, bool>? NoGround;
        /// <summary>When set, only this animal sees the threats (the others are "out of sight").</summary>
        public Animal? OnlyNear;
        public float? GroundY(float x, float z) => NoGround?.Invoke(x, z) == true ? null : 0f;

        public Vector3? NearestThreat(Vector3 from, float radius)
        {
            if (OnlyNear != null && Vector3.Distance(OnlyNear.Position, from) > 0.001f) return null;
            foreach (var t in Threats) if (Vector3.Distance(t, from) <= radius) return t;
            return null;
        }

        public Vector3? PositionOf(ushort player) => Vector3.Zero;
    }

    static (WildlifeKeeper Keeper, WorldEntityManager Entities, AnimalsFile Animals) Make(string json, int seed = 1)
    {
        var animals = GameData.Load(DataDir).Animals;
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        return (new WildlifeKeeper(WildlifeFile.Parse(json).CheckAgainst(animals), animals, entities, seed), entities, animals);
    }

    static IReadOnlyList<Vector3> Points(params (float X, float Z)[] at) => at.Select(p => new Vector3(p.X, 0, p.Z)).ToArray();

    const string Two = """{"version":1,"max_animals":10,"species":[{"id":"bear","count":2},{"id":"rabbit","count":3}]}""";

    // ---- wildlife.json ----

    [Fact]
    public void ShippedFileLoadsAndNamesOnlyFleeingSpecies()
    {
        var data = GameData.Load(DataDir);
        Assert.True(data.Wildlife.MaxAnimals > 0);
        Assert.NotEmpty(data.Wildlife.Species);
        Assert.All(data.Wildlife.Species, s => Assert.True(data.Animals.Find(s.Id)!.Flees, s.Id));
        Assert.True(data.Wildlife.MaxAnimals + data.Pets.MaxPets < WorldEntityManager.MaxEntities / 2, "wildlife and pets must leave room for every other entity");
    }

    [Theory]
    [InlineData("""{"version":1,"max_animals":101,"species":[]}""", "max_animals")]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"","count":1}]}""", "no id")]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"bear","count":1},{"id":"bear","count":1}]}""", "twice")]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"bear","count":0}]}""", "count")]
    public void BadFilesAreRefused(string json, string why) =>
        Assert.Contains(why, Assert.Throws<DataFileException>(() => WildlifeFile.Parse(json)).Message);

    [Fact]
    public void AnUnknownSpeciesOrAPetIsRefused()
    {
        var animals = GameData.Load(DataDir).Animals;
        Assert.Contains("not in animals.json", Assert.Throws<DataFileException>(() => WildlifeFile.Parse("""{"version":1,"max_animals":5,"species":[{"id":"dragon","count":1}]}""").CheckAgainst(animals)).Message);
        Assert.Contains("is a pet", Assert.Throws<DataFileException>(() => WildlifeFile.Parse("""{"version":1,"max_animals":5,"species":[{"id":"dog","count":1}]}""").CheckAgainst(animals)).Message);
    }

    [Fact]
    public void WithoutAFileThereIsNoWildlife()
    {
        var f = new WildlifeFile();
        Assert.Equal(0, f.MaxAnimals);
        Assert.Empty(f.Species);
    }

    // ---- hit ids ----

    [Fact]
    public void HitIdRangeIsApartFromEveryOtherBody()
    {
        Assert.False(WildlifeRules.IsHitId((ushort)(WildlifeRules.FirstHitId - 1)));
        Assert.False(WildlifeRules.IsHitId((ushort)(WildlifeRules.FirstHitId + WildlifeRules.HitIdCount)));
        Assert.Equal(PetRules.FirstHitId, WildlifeRules.FirstHitId + WildlifeRules.HitIdCount); // the pets start where wildlife ends
        Assert.False(PetRules.IsHitId(WildlifeRules.FirstHitId));
        Assert.False(WildlifeRules.IsHitId(PetRules.FirstHitId));
        Assert.True(WildlifeRules.FirstHitId >= FlakRecord.FirstId + 0x400); // above the turret records of any plausible vehicle count
        Assert.False(WildlifeRules.IsHitId(CitizenRules.FirstHitId));
        Assert.False(WildlifeRules.IsHitId(DemonRules.HitId));
        Assert.False(WildlifeRules.IsHitId(TargetDummies.FirstId));
        Assert.False(WildlifeRules.IsHitId(BigfootRules.TargetId));
        Assert.True(NonEnemyTargets.IsNonEnemy(WildlifeRules.FirstHitId)); // bots do not hunt it and a team shot does not count
    }

    // ---- the keeper ----

    [Fact]
    public void PopulatePutsEachSpeciesAtItsOwnHabitatPoints()
    {
        var (keeper, _, animals) = Make(Two);
        var habitat = new Dictionary<string, IReadOnlyList<Vector3>> { ["bear"] = Points((10, 10), (20, 20)), ["rabbit"] = Points((-50, 5)) };
        Assert.Equal(5, keeper.Populate(s => habitat[s], new Flat()));
        Assert.Equal(2, keeper.Animals.Count(a => a.Species == animals.IndexOf("bear")));
        Assert.Equal(3, keeper.Animals.Count(a => a.Species == animals.IndexOf("rabbit")));
        Assert.All(keeper.Animals.Where(a => a.Def.Id == "rabbit"), a => Assert.Equal(new Vector3(-50, 0, 5), a.Home));
        Assert.All(keeper.Animals.Where(a => a.Def.Id == "bear"), a => Assert.Contains(a.Home, habitat["bear"]));
    }

    [Fact]
    public void ASpeciesWithNoHabitatPointGetsNoneAndIsLogged()
    {
        var (keeper, _, _) = Make(Two);
        var log = new List<string>();
        keeper.Log = log.Add;
        Assert.Equal(3, keeper.Populate(s => s == "rabbit" ? Points((1, 1)) : Array.Empty<Vector3>(), new Flat()));
        Assert.Contains(log, l => l.Contains("bear"));
    }

    [Fact]
    public void PointsWithoutGroundAreSkippedAndAnAreaOfNoneSpawnsNothing()
    {
        var (keeper, _, _) = Make(Two);
        var world = new Flat { NoGround = (x, _) => x > 0 }; // every east point is water or off the map
        Assert.Equal(0, keeper.Populate(_ => Points((10, 0), (20, 0)), world));
        Assert.Equal(5, keeper.Populate(_ => Points((10, 0), (-20, 0)), world)); // the west point has ground: the draws find it
        Assert.All(keeper.Animals, a => Assert.Equal(-20, a.Home.X));
    }

    [Fact]
    public void TheMapCapStopsPopulate()
    {
        var (keeper, _, _) = Make("""{"version":1,"max_animals":4,"species":[{"id":"bear","count":3},{"id":"rabbit","count":3}]}""");
        Assert.Equal(4, keeper.Populate(_ => Points((1, 1)), new Flat()));
    }

    [Fact]
    public void ARoundStartForgetsTheLastRoundsAnimals()
    {
        var (keeper, entities, _) = Make(Two);
        keeper.Populate(_ => Points((1, 1)), new Flat());
        entities.Clear();
        keeper.Clear();
        Assert.Equal(0, keeper.Count);
        Assert.Equal(5, keeper.Populate(_ => Points((1, 1)), new Flat()));
    }

    [Fact]
    public void AWildAnimalIsAHitTargetWithHitPointsAndLiesAsACorpseWithoutScoring()
    {
        var (keeper, entities, animals) = Make("""{"version":1,"max_animals":5,"species":[{"id":"wolf","count":1}]}""");
        keeper.Populate(_ => Points((1, 1)), new Flat());
        var wolf = keeper.Animals.Single();
        Assert.False(wolf.Def.OneHit); // a species with hit points (the animated-animals ones); the Synty forest species are one-hit
        var targets = new List<HitTarget>();
        keeper.AddTargets(targets);
        var t = Assert.Single(targets);
        Assert.True(WildlifeRules.IsHitId(t.Id));
        Assert.InRange(t.Scale * PlayerHitBox.StandHeight, wolf.HitBox[1] - 0.001f, wolf.HitBox[1] + 0.001f);
        ushort by = 0;
        keeper.Killed += (_, k) => by = k;
        Assert.Equal(DamageResult.Damaged, keeper.Hit(t.Id, 9, wolf.Def.Hp - 1));
        Assert.Equal(AnimalState.Flee, wolf.State); // a shot that hurt it: it runs
        Assert.Equal(DamageResult.Killed, keeper.Hit(t.Id, 9, 5));
        Assert.Equal((ushort)9, by);
        Assert.Equal(AnimalState.Dead, wolf.State);
        Assert.Equal(DamageResult.Ignored, keeper.Hit(t.Id, 9, 5)); // dead: nothing left to hit
        var left = new List<HitTarget>();
        keeper.AddTargets(left);
        Assert.Empty(left);
        Assert.Equal(1, keeper.Count); // the corpse still lies
        for (var s = 0f; s < animals.CorpseSeconds + 1; s += Dt) entities.Tick(Dt);
        Assert.Equal(0, keeper.Count); // removed with the entity
        Assert.Equal(DamageResult.Ignored, keeper.Hit(WildlifeRules.FirstHitId + 500, 1, 5)); // no such animal
    }

    [Fact]
    public void AOneHitSpeciesIsGoneAtOnce()
    {
        var (keeper, entities, _) = Make("""{"version":1,"max_animals":5,"species":[{"id":"rabbit","count":1}]}""");
        keeper.Populate(_ => Points((1, 1)), new Flat());
        var rabbit = keeper.Animals.Single();
        Assert.True(rabbit.Def.OneHit);
        var targets = new List<HitTarget>();
        keeper.AddTargets(targets);
        Assert.Equal(DamageResult.Killed, keeper.Hit(targets[0].Id, 3, 1));
        Assert.Equal(0, keeper.Count);
        Assert.Empty(entities.OfType(AnimalRules.TypeId));
    }

    [Fact]
    public void WildAnimalsRunFromAThreatAndStayAwayFromItsHome()
    {
        var (keeper, entities, _) = Make("""{"version":1,"max_animals":5,"species":[{"id":"doe","count":1}]}""");
        var world = new Flat();
        keeper.Populate(_ => Points((0, 0)), world);
        var doe = keeper.Animals.Single();
        Assert.True(doe.Def.Flees);
        world.Threats.Add(new Vector3(-5, 0, 0)); // a player close by, to the west
        for (var s = 0f; s < 3; s += Dt) entities.Tick(Dt);
        Assert.Equal(AnimalState.Flee, doe.State);
        Assert.True(doe.Position.X > 5, $"it ran away from the player: {doe.Position}");
    }

    // ---- herds ----

    const string Herds = """{"version":1,"max_animals":40,"species":[{"id":"doe","count":13,"herd":{"min":3,"max":5,"spread_m":10,"range_m":50,"move_min_seconds":10,"move_max_seconds":20}}]}""";

    [Fact]
    public void AHerdSpeciesIsSplitIntoHerdsStartingCloseTogether()
    {
        var (keeper, _, _) = Make(Herds);
        Assert.Equal(13, keeper.Populate(_ => Points((0, 0), (500, 500), (-500, 300), (300, -400), (-300, -300)), new Flat()));
        Assert.Equal(13, keeper.Herds.Sum(h => h.Members.Count));
        Assert.All(keeper.Herds.SkipLast(1), h => Assert.InRange(h.Members.Count, 3, 5));
        Assert.All(keeper.Herds, h => Assert.All(h.Members, m =>
        {
            Assert.Same(h, m.Herd);
            Assert.True(Vector3.Distance(m.Position, h.Home) <= 10.01f, $"{m.Position} is not within the spread of {h.Home}");
        }));
    }

    [Fact]
    public void ASpeciesWithoutHerdRulesStaysSolitary()
    {
        var (keeper, _, _) = Make(Two);
        keeper.Populate(_ => Points((1, 1)), new Flat());
        Assert.Empty(keeper.Herds);
        Assert.All(keeper.Animals, a => Assert.Null(a.Herd));
    }

    [Fact]
    public void WhenOneMemberTakesFrightTheWholeHerdRuns()
    {
        var (keeper, entities, _) = Make(Herds);
        var world = new Flat();
        keeper.Populate(_ => Points((0, 0)), world);
        var herd = keeper.Herds[0];
        var first = herd.Members[0];
        world.OnlyNear = first; // only the first member sees the threat; the others run because the herd does
        world.Threats.Add(first.Position + new Vector3(-3, 0, 0));
        entities.Tick(Dt);
        Assert.All(herd.Members, m => Assert.Equal(AnimalState.Flee, m.State));
    }

    [Fact]
    public void ShootingOneMemberDeadSendsTheRestRunning()
    {
        var (keeper, entities, _) = Make(Herds);
        keeper.Populate(_ => Points((0, 0)), new Flat());
        var herd = keeper.Herds[0];
        var targets = new List<HitTarget>();
        keeper.AddTargets(targets);
        var victim = herd.Members[0];
        var id = targets.First(t => ReferenceEquals(keeper.Animals.First(a => a.Position == t.Feet), victim)).Id;
        Assert.Equal(DamageResult.Killed, keeper.Hit(id, 1, 1000));
        Assert.All(herd.Members.Where(m => m.Alive), m => Assert.Equal(AnimalState.Flee, m.State));
        Assert.True(herd.AliveCount == herd.Members.Count - 1);
        entities.Tick(Dt);
    }

    [Fact]
    public void TheHerdDriftsAndItsMembersFollowTheAnchor()
    {
        var (keeper, entities, _) = Make(Herds);
        var world = new Flat();
        keeper.Populate(_ => Points((0, 0)), world);
        var herd = keeper.Herds[0];
        var moved = false;
        for (var t = 0f; t < 400; t += Dt)
        {
            entities.Tick(Dt);
            keeper.Update(Dt, world);
            if (herd.Anchor != herd.Home) moved = true;
            Assert.True(Vector3.Distance(herd.Anchor, herd.Home) <= 50.01f, "the anchor stays within range of home");
        }
        Assert.True(moved, "the anchor drifted at least once in 400 s");
        // a calm herd ends up around its anchor (frozen now), not scattered over the species' whole wander range
        for (var t = 0f; t < 150; t += Dt) entities.Tick(Dt);
        Assert.All(herd.Members, m => Assert.True(Vector3.Distance(m.Position, herd.Anchor) < 10 * 3, $"{m.Position} vs anchor {herd.Anchor}"));
    }

    [Theory]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"doe","count":3,"herd":{"min":1,"max":3}}]}""", "min is at least 2")]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"doe","count":3,"herd":{"min":4,"max":3}}]}""", "not above max")]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"doe","count":3,"herd":{"spread_m":0}}]}""", "spread_m")]
    [InlineData("""{"version":1,"max_animals":5,"species":[{"id":"doe","count":3,"herd":{"move_min_seconds":9,"move_max_seconds":3}}]}""", "move_min_seconds")]
    public void BadHerdRulesAreRefused(string json, string why) =>
        Assert.Contains(why, Assert.Throws<DataFileException>(() => WildlifeFile.Parse(json)).Message);

    // ---- bird flocks ----

    static FlockSpecies Crow() => WildlifeFile.Parse("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":"crow","flocks":2,"birds_min":8,"birds_max":12,"altitude_min_m":20,"altitude_max_m":30,"range_m":100,"speed_mps":7,"scatter_radius_m":30,"scatter_seconds":5}]}""").Flocks[0];

    [Fact]
    public void BoidsKeepApartTogetherAndNearTheirGoal()
    {
        var rules = new BoidRules();
        var swarm = new BoidSwarm(14, new Vector3(0, 30, 0), rules, 5);
        var goal = new Vector3(200, 30, 0);
        for (var t = 0f; t < 120; t += 0.05f) swarm.Step(0.05f, goal);
        for (var i = 0; i < swarm.Count; i++)
        {
            Assert.InRange(swarm.Velocities[i].Length(), rules.MinSpeed - 0.01f, rules.MaxSpeed + 0.01f);
            Assert.True(Vector3.Distance(swarm.Positions[i], goal) < rules.LeashM * 3, $"bird {i} lost its flock: {swarm.Positions[i]}");
            Assert.InRange(swarm.Positions[i].Y, 30 - rules.BandM * 2, 30 + rules.BandM * 2);
        }
        var closest = float.MaxValue;
        for (var i = 0; i < swarm.Count; i++) for (var j = i + 1; j < swarm.Count; j++) closest = MathF.Min(closest, Vector3.Distance(swarm.Positions[i], swarm.Positions[j]));
        Assert.True(closest > 0.3f, $"two birds are on top of each other ({closest} m)"); // they keep apart
        Assert.True(swarm.Count == 14);
    }

    [Fact]
    public void BoidsAreDeterministicForASeedAndFasterWhenScattering()
    {
        BoidSwarm Run(int seed, bool panic)
        {
            var s = new BoidSwarm(10, Vector3.Zero, new BoidRules(), seed);
            for (var t = 0f; t < 10; t += 0.05f) s.Step(0.05f, new Vector3(50, 0, 0), panic);
            return s;
        }
        Assert.Equal(Run(3, false).Positions, Run(3, false).Positions);
        Assert.NotEqual(Run(3, false).Positions, Run(4, false).Positions);
        Assert.True(Run(3, true).Velocities.Average(v => v.Length()) > Run(3, false).Velocities.Average(v => v.Length()) * 1.2f);
        var one = new BoidSwarm(1, Vector3.Zero, new BoidRules(), 1);
        one.Step(0.05f, new Vector3(10, 0, 0)); // a lone bird needs no neighbours
        Assert.Equal(1, one.Count);
        one.Step(0, Vector3.Zero);
    }

    [Fact]
    public void AFlockCruisesAboveTheGroundNearHomeAndScattersFromAThreat()
    {
        var kind = Crow();
        var world = new Flat();
        var flock = new Flock(kind, 0, 10, new Vector3(0, 0, 0), world, 7);
        Assert.Equal(FlockState.Cruise, flock.State);
        Assert.InRange(flock.Position.Y, 20, 30);
        for (var t = 0f; t < 120; t += Dt)
        {
            flock.Tick(Dt);
            Assert.True(Vector3.Distance(new Vector3(flock.Position.X, 0, flock.Position.Z), Vector3.Zero) <= kind.RangeM + 15, $"{flock.Position}");
            Assert.True(flock.Position.Y >= 19, "it flies above the ground");
        }
        world.Threats.Add(flock.Position - new Vector3(0, flock.Position.Y, 0) + new Vector3(5, 0, 0)); // a player almost below it
        flock.Tick(Dt);
        Assert.Equal(FlockState.Scatter, flock.State);
        world.Threats.Clear();
        for (var t = 0f; t < kind.ScatterSeconds + 1; t += Dt) flock.Tick(Dt);
        Assert.Equal(FlockState.Cruise, flock.State); // calm again
        var s = flock.Serialize();
        Assert.Equal(FlockRules.TypeId, flock.TypeId);
        Assert.Equal(10, FlockRules.BirdsOf(s.Owner));
        Assert.Equal(0, FlockRules.SpeciesOf(s.Owner));
    }

    [Fact]
    public void PopulateFlyesTheFlocksFromTheirHabitatAndALackOfPointsIsLogged()
    {
        var animals = GameData.Load(DataDir).Animals;
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        FlockRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        var cfg = WildlifeFile.Parse("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":"crow","flocks":2,"birds_min":8,"birds_max":12},{"id":"gull","flocks":1}]}""").CheckAgainst(animals);
        var keeper = new WildlifeKeeper(cfg, animals, entities, 1);
        var log = new List<string>();
        keeper.Log = log.Add;
        keeper.Populate(k => k == "crow" ? Points((10, 10), (-40, 20)) : Array.Empty<Vector3>(), new Flat());
        Assert.Equal(2, keeper.Flocks.Count);
        Assert.Equal(2, entities.OfType(FlockRules.TypeId).Count());
        Assert.All(keeper.Flocks, f => Assert.InRange(f.Birds, 8, 12));
        Assert.Contains(log, l => l.Contains("gull"));
        keeper.Clear();
        Assert.Empty(keeper.Flocks);
    }

    [Theory]
    [InlineData("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":""}]}""", "no id")]
    [InlineData("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":"a"},{"id":"a"}]}""", "used twice")]
    [InlineData("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":"a","birds_min":1}]}""", "birds_min")]
    [InlineData("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":"a","altitude_min_m":9,"altitude_max_m":3}]}""", "altitude_min_m")]
    [InlineData("""{"version":1,"max_animals":0,"species":[],"flocks":[{"id":"a","boids":{"max_speed":1,"min_speed":5}}]}""", "boids")]
    public void BadFlockRulesAreRefused(string json, string why) =>
        Assert.Contains(why, Assert.Throws<DataFileException>(() => WildlifeFile.Parse(json)).Message);

    [Fact]
    public void BoidsFlowRoundAnAircraftFlownThroughThemAndNeverEnterIt()
    {
        var rules = new BoidRules();
        var goal = new Vector3(0, 30, 0);
        var swarm = new BoidSwarm(40, goal, rules, 9);
        for (var t = 0f; t < 20; t += 0.05f) swarm.Step(0.05f, goal); // settle into a flock
        const float radius = 8;
        var heli = new Vector3(-150, 30, 0);
        var velocity = new Vector3(40, 0, 0); // straight through the middle of the flock at 40 m/s
        var closest = float.MaxValue;
        var sides = new HashSet<string>();
        for (var t = 0f; t < 8; t += 0.02f)
        {
            heli += velocity * 0.02f;
            swarm.Step(0.02f, goal, false, new[] { new BoidObstacle(heli, velocity, radius) });
            for (var i = 0; i < swarm.Count; i++)
            {
                var d = Vector3.Distance(swarm.Positions[i], heli);
                closest = MathF.Min(closest, d);
                if (MathF.Abs(swarm.Positions[i].X - heli.X) < 12 && d < radius + 22) // close beside it (within the birds' avoid margin): which side of its path is the bird on?
                    sides.Add(MathF.Abs(swarm.Positions[i].Z - heli.Z) >= MathF.Abs(swarm.Positions[i].Y - heli.Y) ? (swarm.Positions[i].Z > heli.Z ? "left" : "right") : (swarm.Positions[i].Y > heli.Y ? "over" : "under"));
            }
        }
        Assert.True(closest >= radius - 0.01f, $"a bird was {closest} m from the aircraft's centre, inside its {radius} m");
        Assert.True(sides.Count >= 2, $"the flock parted round the aircraft: birds passed on {string.Join(", ", sides)}");
        for (var t = 0f; t < 40; t += 0.05f) swarm.Step(0.05f, goal); // behind it the flock closes up again
        Assert.All(swarm.Positions, p => Assert.True(Vector3.Distance(p, goal) < rules.LeashM * 3, $"{p} did not rejoin the flock"));
    }

    [Fact]
    public void ASwarmWithoutObstaclesBehavesAsBefore()
    {
        BoidSwarm Run(IReadOnlyList<BoidObstacle>? o)
        {
            var s = new BoidSwarm(10, Vector3.Zero, new BoidRules(), 2);
            for (var t = 0f; t < 10; t += 0.05f) s.Step(0.05f, new Vector3(50, 0, 0), false, o);
            return s;
        }
        Assert.Equal(Run(null).Positions, Run(Array.Empty<BoidObstacle>()).Positions);
        Assert.Equal(Run(null).Positions, Run(new[] { new BoidObstacle(new Vector3(9999, 0, 0), Vector3.Zero, 10) }).Positions); // far away: no effect
    }
}
