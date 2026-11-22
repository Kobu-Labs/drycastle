using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Companions (#488): companions.json, the keeper (who has one in the world, and when), and the rules of the two kinds, heel and wing.</summary>
public class CompanionTests
{
    const float Dt = 1f / 30f;

    public CompanionTests() => StateMachineDiagnostics.Strict = true;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Flat ground at height 0 everywhere, or only where <see cref="MaxX"/> allows; a wall across the way at <see cref="WallX"/>.</summary>
    sealed class Flat : IAnimalWorld
    {
        public float MaxX = float.PositiveInfinity, WallX = float.PositiveInfinity;
        public float? GroundY(float x, float z) => x <= MaxX ? 0f : null;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
        public float SolidAhead(Vector3 from, Vector3 dir, float maxM, float height) =>
            dir.X > 0 && from.X < WallX && (WallX - from.X) / dir.X <= maxM ? (WallX - from.X) / dir.X : float.PositiveInfinity;
    }

    static readonly AnimalsFile Animals = AnimalsFile.Parse("""
        {"version":1,"animals":[
          {"id":"dog","name":"Dog","model":"a/dog.glb","hp":35,"flees":false,"walk_speed":1.5,"run_speed":6,"turn_rate_deg_per_s":300,"flee_radius":5,"wander_radius":8,"hit_box":[0.2,0.5,0.7],
           "variants":[{"id":"collie","model":"a/collie.glb"}]},
          {"id":"bird","name":"Bird","model":"a/bird.glb","hp":5,"flies":true,"walk_speed":1,"run_speed":1,"turn_rate_deg_per_s":360,"flee_radius":1,"wander_radius":3,"hit_box":[0.6,0.3,0.5]}]}
        """);

    static CompanionsFile File(string dogRules = "{}", string birdRules = "{}") => CompanionsFile.Parse($$"""
        {"version":1,"companions":[
          {"id":"dog","name":"Dog","kind":"heel","species":"dog","asset":"collie","rules":{{dogRules}}},
          {"id":"bird","name":"Bird","kind":"wing","species":"bird","rules":{{birdRules}}}]}
        """).CheckAgainst(Animals);

    static T Make<T>(CompanionsFile file, string id, IAnimalWorld world, CompanionOwner at) where T : Companion
    {
        var index = file.IndexOf(id);
        var def = file.Companions[index];
        var species = Animals.IndexOf(def.Species);
        return (T)CompanionKinds.Find(def.Kind)!.Create(new CompanionSpawn(def, index, Animals.Animals[species], species, def.VariantIn(Animals.Animals[species]), 7, at, world, 11));
    }

    static void Run(Companion c, float seconds, Func<float, CompanionOwner>? owner = null)
    {
        for (var t = 0f; t < seconds; t += Dt)
        {
            if (owner != null) c.Lead(owner(t));
            c.Tick(Dt);
        }
    }

    static float Flat2(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    static readonly CompanionOwner Origin = new(Vector3.Zero, 0f);

    // ---- companions.json ----

    [Fact]
    public void TheShippedFileLoadsAndEveryEntryNamesAKindAndASpecies()
    {
        var data = GameData.Load(DataDir);
        Assert.NotEmpty(data.Companions.Companions);
        foreach (var c in data.Companions.Companions)
        {
            Assert.NotNull(CompanionKinds.Find(c.Kind));
            Assert.NotNull(data.Animals.Find(c.Species));
        }
    }

    [Fact]
    public void AnEntryIsRefusedForAnUnknownKindSpeciesAssetOrABadRuleOfItsKind()
    {
        Assert.Contains("kind", Assert.Throws<DataFileException>(() => CompanionsFile.Parse("""{"version":1,"companions":[{"id":"x","kind":"swim","species":"dog"}]}""")).Message);
        Assert.Contains("twice", Assert.Throws<DataFileException>(() => CompanionsFile.Parse("""{"version":1,"companions":[{"id":"x","kind":"heel","species":"dog"},{"id":"x","kind":"heel","species":"dog"}]}""")).Message);
        Assert.Contains("stop_m", Assert.Throws<DataFileException>(() => File(dogRules: """{"stop_m":5,"follow_m":3}""")).Message); // the heel kind reads its own rules
        Assert.Contains("fly_speed", Assert.Throws<DataFileException>(() => File(birdRules: """{"fly_speed":0}""")).Message);
        Assert.Contains("reward_tier", Assert.Throws<DataFileException>(() => CompanionsFile.Parse("""{"version":1,"companions":[{"id":"x","kind":"heel","species":"dog","reward_tier":"platinum"}]}""")).Message);
        var cat = CompanionsFile.Parse("""{"version":1,"companions":[{"id":"x","kind":"heel","species":"cat"}]}""");
        Assert.Contains("animals.json", Assert.Throws<DataFileException>(() => cat.CheckAgainst(Animals)).Message);
        var poodle = CompanionsFile.Parse("""{"version":1,"companions":[{"id":"x","kind":"heel","species":"dog","asset":"poodle"}]}""");
        Assert.Contains("variant", Assert.Throws<DataFileException>(() => poodle.CheckAgainst(Animals)).Message);
    }

    [Fact]
    public void ACompanionWithARewardTierIsAHuntRewardOfThatTier()
    {
        var file = CompanionsFile.Parse("""{"version":1,"companions":[{"id":"dog","name":"Dog","kind":"heel","species":"dog","reward_tier":"gold"},{"id":"cat","name":"Cat","kind":"heel","species":"dog"}]}""");
        var rewards = HuntRewards.For(new WeaponsFile(), new ItemsFile(), file);
        var r = Assert.Single(rewards);
        Assert.Equal(new HuntReward(HuntTier.Gold, CompanionRules.UnlockKind, "dog", "Dog"), r);
    }

    // ---- the world entity ----

    [Fact]
    public void ItCarriesItsEntryAndPoseOnTheWireAndNothingHurtsIt()
    {
        var file = File();
        var dog = Make<HeelCompanion>(file, "dog", new Flat(), Origin);
        Assert.Equal(CompanionRules.TypeId, dog.TypeId);
        Assert.Equal((ushort)7, dog.Owner);
        Assert.Equal(1, dog.Variant); // the collie
        var s = dog.Serialize();
        Assert.Equal((ushort)file.IndexOf("dog"), s.Owner);
        Assert.Equal(CompanionPose.Idle, CompanionRules.PoseOf(s.State));
        dog.OnDamage(1000, 3);
        Assert.Null(dog.Ended);
        dog.Dismiss();
        Assert.Equal(WorldDespawnReason.Destroyed, dog.Ended);
    }

    [Fact]
    public void ItAppearsBesideItsOwnerOnGroundThatFits()
    {
        var dog = Make<HeelCompanion>(File(), "dog", new Flat(), new CompanionOwner(new Vector3(10, 0, 10), 1f));
        Assert.Equal(CompanionRules.BesideM, Flat2(dog.Position, new Vector3(10, 0, 10)), 2);
        var nowhere = Make<HeelCompanion>(File(), "dog", new Flat { MaxX = -100 }, new CompanionOwner(new Vector3(10, 3, 10), 0f));
        Assert.Equal(new Vector3(10, 3, 10), nowhere.Position); // no ground round the owner: where the owner is
    }

    // ---- heel ----

    [Fact]
    public void AHeelCompanionStaysByAStandingOwnerAndSitsAfterAWhile()
    {
        var dog = Make<HeelCompanion>(File("""{"sit_after_min_seconds":2,"sit_after_max_seconds":3}"""), "dog", new Flat(), Origin);
        var at = dog.Position;
        Run(dog, 1.5f);
        Assert.Equal(HeelState.Stay, dog.State);
        Assert.Equal(CompanionPose.Idle, dog.Pose);
        Run(dog, 2f);
        Assert.Equal(HeelState.Sit, dog.State);
        Assert.Equal(CompanionPose.Sit, dog.Pose);
        Assert.Equal(at, dog.Position);
        var never = Make<HeelCompanion>(File("""{"sit_after_min_seconds":0,"sit_after_max_seconds":0}"""), "dog", new Flat(), Origin);
        Run(never, 60f);
        Assert.Equal(HeelState.Stay, never.State);
    }

    [Fact]
    public void AHeelCompanionWalksAfterItsOwnerAndStopsAtTheirHeel()
    {
        var dog = Make<HeelCompanion>(File("""{"sit_after_min_seconds":0,"sit_after_max_seconds":0}"""), "dog", new Flat(), Origin);
        var owner = new CompanionOwner(new Vector3(4.5f, 0, 0), 0f);
        dog.Lead(owner);
        Run(dog, 0.5f);
        Assert.Equal(HeelState.Follow, dog.State);
        Assert.Equal(CompanionPose.Walk, dog.Pose);
        Run(dog, 10f);
        Assert.Equal(HeelState.Stay, dog.State);
        Assert.InRange(Flat2(dog.Position, owner.Position), 0.5f, 1.8f);
    }

    [Fact]
    public void AHeelCompanionRunsWhenItsOwnerGetsAwayAndKeepsUpWithARunningOwner()
    {
        var dog = Make<HeelCompanion>(File("""{"run_speed":8}"""), "dog", new Flat(), Origin);
        var ran = false;
        // the owner runs east at 6 m/s for 20 s
        for (var t = 0f; t < 20f; t += Dt)
        {
            dog.Lead(new CompanionOwner(new Vector3(6 * t, 0, 0), 0f));
            dog.Tick(Dt);
            ran |= dog.State == HeelState.Run && dog.Pose == CompanionPose.Run;
        }
        Assert.True(ran);
        Assert.InRange(Flat2(dog.Position, new Vector3(120, 0, 0)), 0f, 9f); // never left behind, never recalled
    }

    [Fact]
    public void AHeelCompanionTooFarBehindIsPutBesideItsOwner()
    {
        var dog = Make<HeelCompanion>(File(), "dog", new Flat(), Origin);
        var far = new CompanionOwner(new Vector3(200, 0, 50), 0f);
        dog.Lead(far);
        Run(dog, 0.5f);
        Assert.Equal(CompanionRules.BesideM, Flat2(dog.Position, far.Position), 2);
        Assert.Equal(HeelState.Stay, dog.State);
    }

    [Theory]
    [InlineData(true)]  // a wall across the way
    [InlineData(false)] // no ground between it and its owner
    public void AHeelCompanionThatCannotGetThereIsPutBesideItsOwnerAfterBeingStuck(bool wall)
    {
        var world = wall ? new Flat { WallX = 3 } : new Flat();
        var dog = Make<HeelCompanion>(File("""{"stuck_seconds":1.5}"""), "dog", world, Origin);
        if (!wall) world.MaxX = 3; // the ground ends there; its owner is beyond (on a bridge the terrain does not know)
        var owner = new CompanionOwner(new Vector3(12, 0, 0), 0f);
        dog.Lead(owner);
        Run(dog, 1.5f); // it runs to the obstacle in well under a second
        Assert.True(dog.Position.X < 3.1f, $"it stopped at the obstacle, x {dog.Position.X}"); // not yet stuck for long enough: still on this side
        Run(dog, 3f);
        // put beside its owner: on ground there when there is some, else exactly where the owner is
        Assert.InRange(Flat2(dog.Position, owner.Position), 0f, CompanionRules.BesideM + 0.01f);
    }

    // ---- wing ----

    [Fact]
    public void AWingCompanionHoversAtItsPerchOverItsOwner()
    {
        var bird = Make<WingCompanion>(File(birdRules: """{"circle_every_min_seconds":0,"circle_every_max_seconds":0}"""), "bird", new Flat(), Origin);
        Run(bird, 5f);
        Assert.Equal(WingState.Hover, bird.State);
        Assert.Equal(CompanionPose.Hover, bird.Pose);
        // the owner faces -Z: behind is +Z, left is -X
        Assert.Equal(new Vector3(-0.8f, 2.2f, 1.2f), bird.Position);
    }

    [Fact]
    public void AWingCompanionFliesAfterAMovingOwnerAndHoversAgainWhenTheyStop()
    {
        var bird = Make<WingCompanion>(File(birdRules: """{"circle_every_min_seconds":0,"circle_every_max_seconds":0}"""), "bird", new Flat(), Origin);
        var flew = false;
        for (var t = 0f; t < 10f; t += Dt)
        {
            bird.Lead(new CompanionOwner(new Vector3(0, 0, -6 * t), 0f));
            bird.Tick(Dt);
            flew |= bird.State == WingState.Trail && bird.Pose == CompanionPose.Fly;
        }
        Assert.True(flew);
        var stopped = new CompanionOwner(new Vector3(0, 0, -60), 0f);
        Run(bird, 4f, _ => stopped);
        Assert.Equal(WingState.Hover, bird.State);
        Assert.InRange(Vector3.Distance(bird.Position, new Vector3(-0.8f, 2.2f, -58.8f)), 0f, 0.05f);
    }

    [Fact]
    public void AWingCompanionCirclesItsOwnerNowAndThenAndComesBackToItsPerch()
    {
        var bird = Make<WingCompanion>(File(birdRules: """{"circle_every_min_seconds":2,"circle_every_max_seconds":2,"circle_radius_m":3,"circle_speed":6,"circle_turns":1}"""), "bird", new Flat(), Origin);
        Run(bird, 1.9f);
        Assert.Equal(WingState.Hover, bird.State);
        Run(bird, 0.3f);
        Assert.Equal(WingState.Circle, bird.State);
        var widest = 0f;
        var swept = 0f;
        var last = MathF.Atan2(bird.Position.Z, bird.Position.X);
        for (var t = 0f; t < 4f && bird.State == WingState.Circle; t += Dt) // one turn of 3 m at 6 m/s is pi seconds
        {
            bird.Tick(Dt);
            widest = MathF.Max(widest, Flat2(bird.Position, Vector3.Zero));
            var a = MathF.Atan2(bird.Position.Z, bird.Position.X);
            swept += MathF.Abs(TurretMath.AngleDiff(last, a));
            last = a;
            Assert.Equal(2.2f, bird.Position.Y, 3);
        }
        Assert.Equal(3f, widest, 2);
        Assert.InRange(swept, MathF.Tau * 0.9f, MathF.Tau * 1.05f);
        Assert.Equal(WingState.Trail, bird.State); // the turn is over: back to its perch
        Run(bird, 1.5f);
        Assert.Equal(WingState.Hover, bird.State);
        Assert.InRange(Vector3.Distance(bird.Position, new Vector3(-0.8f, 2.2f, 1.2f)), 0f, 0.2f);
    }

    [Fact]
    public void AWingCompanionKeepsItsClearanceOverRisingGroundAndIsPutAtItsPerchWhenFarBehind()
    {
        var bird = Make<WingCompanion>(File(birdRules: """{"circle_every_min_seconds":0,"circle_every_max_seconds":0,"clearance_m":3}"""), "bird", new Flat(), Origin);
        Assert.Equal(3f, bird.Position.Y, 3); // the perch is 2.2 m up, the clearance says 3
        var far = new CompanionOwner(new Vector3(500, 0, 0), 0f);
        bird.Lead(far);
        bird.Tick(Dt);
        Assert.InRange(Flat2(bird.Position, far.Position), 0f, 2f);
        Assert.Equal(WingState.Hover, bird.State);
    }

    // ---- the keeper ----

    static (CompanionKeeper Keeper, WorldEntityManager Entities, List<WorldDespawned> Gone) Keeper()
    {
        var registry = new WorldEntityRegistry();
        CompanionRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        var gone = new List<WorldDespawned>();
        entities.Despawned += gone.Add;
        return (new CompanionKeeper(File(), Animals, entities, 5), entities, gone);
    }

    [Fact]
    public void ACompanionIsInTheWorldOnlyWhileItsOwnerIsPresent()
    {
        var (keeper, entities, gone) = Keeper();
        var world = new Flat();
        CompanionOwner? owner = Origin;
        Assert.True(keeper.Assign(7, "dog"));
        Assert.False(keeper.Assign(7, "dragon"));
        Assert.Equal("dog", keeper.Assigned(7));
        Assert.Null(keeper.Of(7)); // not before the keeper's tick
        keeper.Update(world, _ => owner);
        var dog = keeper.Of(7);
        Assert.IsType<HeelCompanion>(dog);
        Assert.Equal(1, entities.Count);

        owner = new CompanionOwner(new Vector3(8, 0, 0), 0f); // the owner walks off: the Companion is told
        keeper.Update(world, _ => owner);
        for (var i = 0; i < 300; i++) { entities.Tick(Dt); keeper.Update(world, _ => owner); }
        Assert.InRange(Flat2(dog!.Position, owner.Value.Position), 0f, 2f);

        owner = null; // dead, or in a seat
        keeper.Update(world, _ => owner);
        Assert.Null(keeper.Of(7));
        Assert.Equal(0, entities.Count);
        Assert.Equal(WorldDespawnReason.Destroyed, Assert.Single(gone).Reason); // a puff on the clients

        owner = new CompanionOwner(new Vector3(50, 0, 50), 0f); // alive again somewhere else: a new one beside them
        keeper.Update(world, _ => owner);
        Assert.NotSame(dog, keeper.Of(7));
        Assert.InRange(Flat2(keeper.Of(7)!.Position, owner.Value.Position), 0f, CompanionRules.BesideM + 0.01f);
    }

    [Fact]
    public void AnotherCompanionReplacesTheOldOneAndNoneTakesItAway()
    {
        var (keeper, entities, _) = Keeper();
        var world = new Flat();
        keeper.Assign(7, "dog");
        keeper.Update(world, _ => Origin);
        keeper.Assign(7, "bird");
        Assert.Equal(0, entities.Count); // the dog went at once
        keeper.Update(world, _ => Origin);
        Assert.IsType<WingCompanion>(keeper.Of(7));
        keeper.Assign(7, null);
        Assert.Null(keeper.Assigned(7));
        Assert.Equal(0, entities.Count);
        keeper.Update(world, _ => Origin);
        Assert.Equal(0, entities.Count);
    }

    [Fact]
    public void ANewRoundAndALeavingPlayerLeaveNoCompanionBehind()
    {
        var (keeper, entities, _) = Keeper();
        var world = new Flat();
        keeper.Assign(7, "dog");
        keeper.Assign(8, "bird");
        keeper.Update(world, _ => Origin);
        Assert.Equal(2, keeper.Count);
        entities.Clear(); // the match clears the entities for the new round
        keeper.Clear();
        Assert.Equal(0, keeper.Count);
        keeper.Update(world, _ => Origin); // both players keep theirs
        Assert.Equal(2, entities.Count);
        keeper.Forget(8);
        Assert.Equal(1, entities.Count);
        Assert.Null(keeper.Assigned(8));
        Assert.NotNull(keeper.Of(7));
    }
}
