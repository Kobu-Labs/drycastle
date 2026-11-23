using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Tests.Wildlife;

/// <summary>Pets with the Synty models: one-hit death without a corpse, sitting and barking, per-variant hit boxes, the clip name table.</summary>
public class PetBehaviourTests
{
    const float Dt = 0.05f;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    sealed class Flat : IAnimalWorld
    {
        public float? GroundY(float x, float z) => 0f;
        public Vector3? NearestThreat(Vector3 from, float radius) => null;
        public Vector3? PositionOf(ushort player) => null;
    }

    /// <summary>A pet species with the given extra fields (sit, bark, one_hit) and two looks, the second with its own hit box.</summary>
    static AnimalsFile Species(string extra = "") => AnimalsFile.Parse($$"""
        {"version":1,"idle_min_seconds":0.5,"idle_max_seconds":1,"eat_min_seconds":0.5,"eat_max_seconds":1,"eat_chance":0.5,"corpse_seconds":3,
        "animals":[{"id":"dog","name":"Dog","model":"a/beagle.glb","hp":35,"flees":false,"walk_speed":1.4,"run_speed":6,"flee_radius":5,"wander_radius":8,
          "hit_box":[0.2,0.5,0.7]{{extra}},"variants":[{"id":"saint","model":"a/saint.glb","hit_box":[0.4,0.95,1.25]},{"id":"plain","model":"a/plain.glb"}]}]}
        """);

    // ---- one hit ----

    [Fact]
    public void AOneHitPetDiesOfAnyDamageAndEndsAtOnceWithNoReactionAndNoCorpse()
    {
        var pet = new Animal(Species(",\"one_hit\":true"), 0, Vector3.Zero, 0f, new Flat(), 3);
        ushort killer = 0;
        pet.Died += (_, by) => killer = by;
        var toggle = pet.Serialize().State & AnimalRules.HitToggle;
        pet.OnDamage(0.5f, 9); // far below its 35 hp
        Assert.False(pet.Alive);
        Assert.Equal((ushort)9, killer);
        Assert.Equal(WorldDespawnReason.Destroyed, pet.Ended);
        Assert.Equal(toggle, pet.Serialize().State & AnimalRules.HitToggle); // no hit reaction to play
    }

    [Fact]
    public void ZeroDamageDoesNotKillAOneHitPet()
    {
        var pet = new Animal(Species(",\"one_hit\":true"), 0, Vector3.Zero, 0f, new Flat(), 3);
        pet.OnDamage(0, 9);
        Assert.True(pet.Alive);
        Assert.Null(pet.Ended);
    }

    [Fact]
    public void ASpeciesWithoutOneHitKeepsItsHitPointsReactionAndCorpse()
    {
        var pet = new Animal(Species(), 0, Vector3.Zero, 0f, new Flat(), 3);
        var toggle = pet.Serialize().State & AnimalRules.HitToggle;
        pet.OnDamage(5, 9);
        Assert.True(pet.Alive);
        Assert.Equal(30, pet.Hp);
        Assert.NotEqual(toggle, pet.Serialize().State & AnimalRules.HitToggle);
        pet.OnDamage(100, 9);
        Assert.Equal(AnimalState.Dead, pet.State);
        Assert.Null(pet.Ended); // lies for corpse_seconds
    }

    [Fact]
    public void TheKeeperDespawnsAOneHitPetAtOnceAsDestroyedAndTheShotCountsAsAKill()
    {
        var animals = Species(",\"one_hit\":true");
        var registry = new WorldEntityRegistry();
        AnimalRules.Register(registry);
        PoopRules.Register(registry);
        var entities = new WorldEntityManager(registry);
        var pets = PetsFile.Parse("""{"version":1,"group_chance":0,"max_pets":4,"species":[{"id":"dog","weight":1}],"pinned":[{"group":"G-1","species":"dog","count":2}]}""");
        var keeper = new PetKeeper(pets.CheckAgainst(animals), animals, entities, 1);
        var pen = new Pen(new IReadOnlyList<Vector2[]>[] { new[] { new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) } } });
        Assert.Equal(2, keeper.Populate(new Dictionary<string, Pen> { ["G-1"] = pen }, new Flat()));
        var targets = new List<HitTarget>();
        keeper.AddTargets(targets);
        var gone = new List<WorldDespawned>();
        entities.Despawned += gone.Add;
        Assert.Equal(DamageResult.Killed, keeper.Hit(targets[0].Id, 7, 1f));
        var d = Assert.Single(gone); // before any tick
        Assert.Equal(WorldDespawnReason.Destroyed, d.Reason);
        Assert.Equal(1, keeper.Count);
        Assert.Equal(1, keeper.AliveCount);
        targets.Clear();
        keeper.AddTargets(targets);
        Assert.Single(targets);
    }

    // ---- hit box per look ----

    [Fact]
    public void AVariantMayHaveItsOwnHitBoxAndOtherwiseTakesTheSpecies()
    {
        var dog = Species().Animals[0];
        Assert.Equal(new[] { 0.2f, 0.5f, 0.7f }, dog.HitBoxOf(0));
        Assert.Equal(new[] { 0.4f, 0.95f, 1.25f }, dog.HitBoxOf(1));
        Assert.Equal(new[] { 0.2f, 0.5f, 0.7f }, dog.HitBoxOf(2)); // no box of its own
        Assert.Equal(new[] { 0.2f, 0.5f, 0.7f }, dog.HitBoxOf(9)); // out of range: the species'
    }

    [Fact]
    public void TheHitTargetAndTheBodyBoxOfAPetUseItsVariantsBox()
    {
        var animals = Species();
        var big = new Animal(animals, 0, Vector3.Zero, 0f, new Flat(), 1, variant: 1);
        var small = new Animal(animals, 0, Vector3.Zero, 0f, new Flat(), 1, variant: 0);
        Assert.Equal(0.95f / PlayerHitBox.StandHeight, PetRules.TargetOf(1, big).Scale, 4);
        Assert.Equal(0.5f / PlayerHitBox.StandHeight, PetRules.TargetOf(1, small).Scale, 4);
        Assert.Equal(0.95f / 2, big.Bounds.Half.Y, 4);
        Assert.Equal(1.25f / 2, big.Bounds.Half.Z, 4);
    }

    [Theory]
    [InlineData("""{"id":"x","model":"a/x.glb","hit_box":[0.4,0.9]}""")]
    [InlineData("""{"id":"x","model":"a/x.glb","hit_box":[0.4,0,0.9]}""")]
    public void AVariantsBadHitBoxIsRefused(string variant) => Assert.Throws<DataFileException>(() => AnimalsFile.Parse($$"""
        {"version":1,"animals":[{"id":"dog","name":"Dog","model":"a/dog.glb","hp":40,"walk_speed":1,"run_speed":2,"flee_radius":5,"wander_radius":8,
          "hit_box":[0.3,0.6,0.8],"variants":[{{variant}}]}]}
        """));

    // ---- sit and bark ----

    const string SitsAndBarks = ",\"sit\":{\"chance\":0.4,\"min_seconds\":2,\"max_seconds\":4},\"bark\":{\"chance\":0.5,\"seconds\":1.5}";

    [Fact]
    public void ASittingBarkingSpeciesSitsForItsTimeThenStandsAndBarksOnceThenStands()
    {
        var pet = new Animal(Species(SitsAndBarks), 0, Vector3.Zero, 0f, new Flat(), 11);
        var spans = new List<(AnimalState State, float Seconds)>();
        var last = pet.State;
        var since = 0f;
        for (var t = 0f; t < 600; t += Dt)
        {
            pet.Tick(Dt);
            if (pet.State != last)
            {
                // the state it left, then how long it stayed: the transitions must be allowed (a Strict machine throws on the rest) and Sit/Bark only come from standing
                if (pet.State is AnimalState.Sit or AnimalState.Bark) Assert.Equal(AnimalState.Idle, last);
                if (last is AnimalState.Sit or AnimalState.Bark) Assert.Equal(AnimalState.Idle, pet.State);
                spans.Add((last, since));
                last = pet.State;
                since = 0;
            }
            since += Dt;
        }
        var sits = spans.Where(s => s.State == AnimalState.Sit).ToList();
        var barks = spans.Where(s => s.State == AnimalState.Bark).ToList();
        Assert.True(sits.Count > 5 && barks.Count > 5, $"{sits.Count} sits, {barks.Count} barks");
        Assert.All(sits, s => Assert.InRange(s.Seconds, 2 - Dt, 4 + Dt));
        Assert.All(barks, s => Assert.InRange(s.Seconds, 1.5f - Dt, 1.5f + Dt)); // one clip length
        Assert.Contains(spans, s => s.State == AnimalState.Walk); // it still does the rest
        Assert.Contains(spans, s => s.State == AnimalState.Eat);
    }

    [Fact]
    public void ASpeciesWithoutTheRulesNeverSitsOrBarks()
    {
        var pet = new Animal(Species(), 0, Vector3.Zero, 0f, new Flat(), 11);
        var seen = new HashSet<AnimalState>();
        for (var t = 0f; t < 600; t += Dt) { pet.Tick(Dt); seen.Add(pet.State); }
        Assert.DoesNotContain(AnimalState.Sit, seen);
        Assert.DoesNotContain(AnimalState.Bark, seen);
    }

    [Fact]
    public void SitAndBarkFitTheThreeBitStateOfTheWire()
    {
        Assert.All(Enum.GetValues<AnimalState>(), s => Assert.True((byte)s <= AnimalRules.StateMask));
        var pet = new Animal(Species(",\"sit\":{\"chance\":1,\"min_seconds\":2,\"max_seconds\":2}"), 0, Vector3.Zero, 0f, new Flat(), 11);
        while (pet.State != AnimalState.Sit) pet.Tick(Dt);
        Assert.Equal(AnimalState.Sit, AnimalRules.StateOf(pet.Serialize().State));
    }

    [Fact]
    public void ASittingPetDropsNoPoopUntilItStands()
    {
        var pets = PetsFile.Parse("""{"version":1,"poop_min_seconds":1,"poop_max_seconds":1}""");
        var poop = new PoopSchedule(pets, new Random(1));
        Assert.False(poop.Tick(5, true, AnimalState.Sit));
        Assert.False(poop.Tick(5, true, AnimalState.Bark));
        Assert.True(poop.Tick(Dt, true, AnimalState.Idle)); // due, and now it stands
    }

    [Fact]
    public void ASittingPetThatDiesOfOneHitIsGoneAtOnce()
    {
        var pet = new Animal(Species(",\"one_hit\":true,\"sit\":{\"chance\":1,\"min_seconds\":5,\"max_seconds\":5}"), 0, Vector3.Zero, 0f, new Flat(), 2);
        while (pet.State != AnimalState.Sit) pet.Tick(Dt);
        pet.OnDamage(1, 3);
        Assert.Equal(WorldDespawnReason.Destroyed, pet.Ended);
    }

    [Theory]
    [InlineData(",\"sit\":{\"chance\":1.5,\"min_seconds\":1,\"max_seconds\":2}")]
    [InlineData(",\"sit\":{\"chance\":0.5,\"min_seconds\":0,\"max_seconds\":2}")]
    [InlineData(",\"sit\":{\"chance\":0.5,\"min_seconds\":3,\"max_seconds\":2}")]
    [InlineData(",\"bark\":{\"chance\":0.5,\"seconds\":0}")]
    [InlineData(",\"bark\":{\"chance\":-0.1,\"seconds\":1}")]
    public void BadSitAndBarkRulesAreRefused(string extra) => Assert.Throws<DataFileException>(() => Species(extra));

    [Fact]
    public void ShippedDogsSitAndBarkAndCatsOnlySit()
    {
        var animals = GameData.Load(DataDir).Animals;
        Assert.NotNull(animals.Find("dog")!.Sit);
        Assert.NotNull(animals.Find("dog")!.Bark);
        Assert.NotNull(animals.Find("cat")!.Sit);
        Assert.Null(animals.Find("cat")!.Bark);
        foreach (var id in new[] { "hen", "rooster", "chick", "duck" }) Assert.True(animals.Find(id)!.OneHit && animals.Find(id)!.Sit == null && animals.Find(id)!.Bark == null, id);
        Assert.Null(animals.Find("chicken")); // the placeholder is gone
        Assert.All(new[] { "dog", "cat", "hen", "rooster", "chick", "duck" }, id => Assert.True(animals.Find(id)!.OneHit && !animals.Find(id)!.Flees, id));
        Assert.All(animals.Animals.Where(a => a.Flees && a.Model.StartsWith("animated-animals/")), a => Assert.False(a.OneHit, a.Id)); // the animated-animals wildlife keeps its hit points (they have Death and hit clips); the Synty forest animals die like pets
    }

    // ---- the clip table ----

    [Fact]
    public void ThePetPackAndTheAnimalPackBothResolveToTheClipsTheClientPlays()
    {
        var pets = AnimalClips.Resolve(new HashSet<string> { "Idle", "Walk", "Run", "Eat", "Sit_Start", "Sit_Idle", "Sit_Up", "Bark" }.Contains);
        Assert.Equal("Eat", pets[(int)AnimalClip.Eat]);
        Assert.Equal("Run", pets[(int)AnimalClip.Run]);
        Assert.Equal("Sit_Up", pets[(int)AnimalClip.SitUp]);
        Assert.Equal("Idle", pets[(int)AnimalClip.IdleAlt]); // no second idle: the plain one
        Assert.Null(pets[(int)AnimalClip.Death]);

        var wild = AnimalClips.Resolve(new HashSet<string> { "Idle", "Idle_2", "Eating", "Walk", "Gallop", "Death", "Idle_HitReact_Left", "Idle_HitReact_Right" }.Contains);
        Assert.Equal("Eating", wild[(int)AnimalClip.Eat]);
        Assert.Equal("Gallop", wild[(int)AnimalClip.Run]);
        Assert.Equal("Idle_2", wild[(int)AnimalClip.IdleAlt]);
        Assert.Equal("Idle_HitReact_Right", wild[(int)AnimalClip.HitRight]);
        Assert.Null(wild[(int)AnimalClip.SitStart]); // no sit-down: not played
        Assert.Equal("Idle", wild[(int)AnimalClip.SitIdle]);
        Assert.Equal("Idle", wild[(int)AnimalClip.Bark]);
    }

    [Fact]
    public void WhatASpeciesNeedsFollowsItsBehaviour()
    {
        var animals = Species(",\"one_hit\":true,\"sit\":{\"chance\":0.1,\"min_seconds\":1,\"max_seconds\":2},\"bark\":{\"chance\":0.1,\"seconds\":1}");
        var need = AnimalClips.Needed(animals.Animals[0]).ToList();
        Assert.Contains(AnimalClip.SitStart, need);
        Assert.Contains(AnimalClip.Bark, need);
        Assert.DoesNotContain(AnimalClip.Death, need); // dies at once: no death clip
        Assert.DoesNotContain(AnimalClip.HitLeft, need);
        var wild = AnimalClips.Needed(Species().Animals[0]).ToList();
        Assert.Contains(AnimalClip.Death, wild);
        Assert.DoesNotContain(AnimalClip.SitIdle, wild);
    }

    [Fact]
    public void EveryClipHasANameAndTheLoopingOnesAreTheStandingAndMovingClips()
    {
        foreach (var c in Enum.GetValues<AnimalClip>()) Assert.NotEmpty(AnimalClips.NamesOf(c));
        Assert.True(AnimalClips.Loops(AnimalClip.SitIdle));
        Assert.True(AnimalClips.Loops(AnimalClip.Walk));
        Assert.False(AnimalClips.Loops(AnimalClip.Bark));
        Assert.False(AnimalClips.Loops(AnimalClip.SitStart));
        Assert.False(AnimalClips.Loops(AnimalClip.Death));
    }
}
