using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Wildlife (#254): the animal NPC. Species data, hit points and death, the wire state, fleeing, wandering and the corpse's lifetime.</summary>
public class AnimalTests
{
    const float Dt = 0.05f;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly AnimalsFile Shipped = GameData.Load(DataDir).Animals;

    /// <summary>Models under this folder are in the private packs (assets-private/, the paid Synty packs): never in the repo, so a machine without them has no file and the client draws the hit box as a cube.</summary>
    const string PrivateFolder = "synty/";

    static bool InPrivatePack(string model) => model.StartsWith(PrivateFolder, StringComparison.Ordinal);

    /// <summary>The private packs folder, when this machine has it: DRYCASTLE_ASSETS_PRIVATE, else assets-private/ beside assets/ (the repo root). Null without the packs.</summary>
    static string? PrivateDir()
    {
        var env = Environment.GetEnvironmentVariable("DRYCASTLE_ASSETS_PRIVATE");
        if (!string.IsNullOrEmpty(env)) return Directory.Exists(env) ? env : null;
        var candidate = Path.Combine(Path.GetDirectoryName(AssetsDir())!, "assets-private");
        return Directory.Exists(Path.Combine(candidate, "synty")) ? candidate : null;
    }

    /// <summary>Every model of every species (the base and its variants) with the species it belongs to.</summary>
    static IEnumerable<(AnimalDef Species, string Model)> AllModels() =>
        Shipped.Animals.SelectMany(a => a.Variants.Select(v => v.Model).Prepend(a.Model).Select(m => (a, m)));

    static HashSet<string> ClipNames(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes.AsMemory(20, BitConverter.ToInt32(bytes, 12)));
        return doc.RootElement.GetProperty("animations").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToHashSet();
    }

    static void AssertHasTheClipsItPlays(AnimalDef species, string model, string path)
    {
        var have = ClipNames(path);
        var resolved = AnimalClips.Resolve(have.Contains);
        foreach (var clip in AnimalClips.Needed(species)) Assert.True(resolved[(int)clip] != null, $"{species.Id}: {model} has no clip for {clip} (has {string.Join(", ", have)})");
    }

    static string AssetsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "assets");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new InvalidOperationException("assets/manifest.json not found above the test output folder");
    }

    /// <summary>One small species table for the behaviour tests: a fast, skittish animal with 100 hp.</summary>
    static AnimalsFile Small(string rules = "") => AnimalsFile.Parse($$"""
        {"version":1,{{rules}}"animals":[{"id":"deer","name":"Deer","model":"animated-animals/deer.glb","scale":0.33,"hp":100,
        "walk_speed":1.5,"run_speed":12,"turn_rate_deg_per_s":360,"flee_radius":30,"wander_radius":20,"hit_box":[0.5,1.4,1.5]}]}
        """);

    sealed class FakeWorld : IAnimalWorld
    {
        public Func<float, float, bool> Blocked = (_, _) => false;
        public bool IsWatched = true;
        public bool Watched(Vector3 at) => IsWatched;
        public readonly List<Vector3> Threats = new();
        public readonly Dictionary<ushort, Vector3> Players = new();

        public float? GroundY(float x, float z) => Blocked(x, z) ? null : 0f;

        public Vector3? NearestThreat(Vector3 from, float radius)
        {
            Vector3? best = null;
            var bestD = radius;
            foreach (var t in Threats)
            {
                var d = MathF.Sqrt((t.X - from.X) * (t.X - from.X) + (t.Z - from.Z) * (t.Z - from.Z));
                if (d <= bestD) { best = t; bestD = d; }
            }
            return best;
        }

        public Vector3? PositionOf(ushort player) => Players.TryGetValue(player, out var p) ? p : null;
    }

    static Animal Make(IAnimalWorld w, AnimalsFile? file = null, int seed = 1, Vector3? at = null) =>
        new(file ?? Small(), 0, at ?? Vector3.Zero, 0f, w, seed);

    static void Run(Animal a, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt) a.Tick(Dt);
    }

    static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // ---- the data ----

    [Fact]
    public void ShippedSpeciesLoad()
    {
        Assert.Equal(28, Shipped.Animals.Count);
        Assert.NotNull(Shipped.Find("deer"));
        Assert.Equal(Shipped.IndexOf("deer"), Shipped.Animals.IndexOf(Shipped.Find("deer")!));
        Assert.Equal(-1, Shipped.IndexOf("dragon"));
        Assert.Null(Shipped.Find(null));
    }

    [Fact]
    public void EveryShippedPublicModelExistsAndHasTheClipsTheClientPlays()
    {
        foreach (var (a, model) in AllModels().Where(m => !InPrivatePack(m.Model)))
        {
            var path = Path.Combine(AssetsDir(), model);
            Assert.True(File.Exists(path), $"{a.Id}: {path} is missing");
            AssertHasTheClipsItPlays(a, model, path);
        }
    }

    [Fact]
    public void EveryShippedPrivateModelIsInThePackWhenThePackIsHereAndHasTheClipsTheClientPlays()
    {
        Assert.Contains(AllModels(), m => InPrivatePack(m.Model)); // the pets: the check below has something to look at
        if (PrivateDir() is not { } dir) return; // a machine without the paid packs: the client draws a box for these, nothing to check
        foreach (var (a, model) in AllModels().Where(m => InPrivatePack(m.Model)))
        {
            var path = Path.Combine(dir, model);
            Assert.True(File.Exists(path), $"{a.Id}: {path} is missing from the private pack (rebuild it: assets-private/synty/tools/build_pets.sh)");
            AssertHasTheClipsItPlays(a, model, path);
        }
    }

    [Fact]
    public void EveryShippedPetHitBoxIsTheMeasuredSizeOfItsModelWhenThePackIsHere()
    {
        if (PrivateDir() is not { } dir || !File.Exists(Path.Combine(dir, "synty/glb/pets/manifest.json"))) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "synty/glb/pets/manifest.json")));
        var measured = doc.RootElement.EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "animal")
            .ToDictionary(e => "synty/glb/pets/" + e.GetProperty("file").GetString(), e => e.GetProperty("size_m").EnumerateArray().Select(x => x.GetSingle()).ToArray());
        foreach (var a in Shipped.Animals.Where(a => InPrivatePack(a.Model)))
        {
            for (var variant = 0; variant < a.VariantCount; variant++)
            {
                var model = a.ModelOf(variant);
                Assert.True(measured.TryGetValue(model, out var size), $"{model} is not in the pack's manifest");
                var box = a.HitBoxOf(variant);
                for (var k = 0; k < 3; k++) Assert.InRange(box[k], size![k] - Math.Max(size[k] * 0.05f, 0.01f), size[k] + Math.Max(size[k] * 0.05f, 0.01f)); // written to the centimetre
            }
        }
    }

    [Fact]
    public void EveryShippedPublicModelIsInTheManifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetsDir(), "manifest.json")));
        var files = doc.RootElement.GetProperty("unverified_models").EnumerateArray().Concat(doc.RootElement.GetProperty("assets").EnumerateArray()).Select(m => m.GetProperty("file").GetString()).ToHashSet(); // the repo's own CC0 models (the Phoenix) are under assets
        foreach (var (_, model) in AllModels().Where(m => !InPrivatePack(m.Model))) Assert.Contains(model, files);
    }

    [Fact]
    public void ShippedAnimalsAreSizedSoTheyCanBeShot()
    {
        foreach (var a in Shipped.Animals)
        {
            Assert.True(a.Hp is > 0 and <= 255, a.Id);
            Assert.Equal(3, a.HitBox.Count);
            for (var v = 0; v < a.VariantCount; v++) Assert.True(a.HitBoxOf(v)[1] is > 0.05f and < 3f, $"{a.Id}: height {a.HitBoxOf(v)[1]} m");
            Assert.True(a.Flies || a.RunSpeed > a.WalkSpeed, a.Id); // a flyer has no gait: its numbers are placeholders
        }
    }

    [Theory]
    [InlineData("\"hp\":100", "\"hp\":300")]
    [InlineData("\"hp\":100", "\"hp\":0")]
    [InlineData("\"run_speed\":12", "\"run_speed\":1")]
    [InlineData("\"hit_box\":[0.5,1.4,1.5]", "\"hit_box\":[0.5,1.4]")]
    [InlineData("\"hit_box\":[0.5,1.4,1.5]", "\"hit_box\":[0.5,0,1.5]")]
    [InlineData("\"id\":\"deer\"", "\"id\":\"Deer\"")]
    [InlineData("\"model\":\"animated-animals/deer.glb\"", "\"model\":\"animated-animals/deer.png\"")]
    [InlineData("\"wander_radius\":20", "\"wander_radius\":1")]
    public void BadSpeciesAreRefused(string good, string bad)
    {
        const string json = """
            {"version":1,"animals":[{"id":"deer","name":"Deer","model":"animated-animals/deer.glb","scale":0.33,"hp":100,
            "walk_speed":1.5,"run_speed":12,"turn_rate_deg_per_s":360,"flee_radius":30,"wander_radius":20,"hit_box":[0.5,1.4,1.5]}]}
            """;
        Assert.Contains(good, json);
        Assert.Throws<DataFileException>(() => AnimalsFile.Parse(json.Replace(good, bad)));
    }

    [Fact]
    public void TwoSpeciesWithOneIdAreRefused()
    {
        const string one = """{"id":"deer","name":"Deer","model":"a.glb","scale":1,"hp":10,"walk_speed":1,"run_speed":2,"flee_radius":5,"wander_radius":5,"hit_box":[1,1,1]}""";
        Assert.Throws<DataFileException>(() => AnimalsFile.Parse($$"""{"version":1,"animals":[{{one}},{{one}}]}"""));
    }

    // ---- hit points and death ----

    [Fact]
    public void DamageTakesHitPointsAndAHitThatLeavesSomeKeepsItAlive()
    {
        var a = Make(new FakeWorld());
        a.OnDamage(30, 7);
        Assert.Equal(70, a.Hp);
        Assert.True(a.Alive);
        Assert.Equal(0, a.Killer);
        Assert.Null(a.Ended);
    }

    [Fact]
    public void TheLastHitKillsAndNamesTheKillerOnce()
    {
        var a = Make(new FakeWorld());
        var died = new List<(Animal, ushort)>();
        a.Died += (x, k) => died.Add((x, k));
        a.OnDamage(60, 7);
        a.OnDamage(60, 9);
        a.OnDamage(60, 5); // already dead: nothing
        Assert.False(a.Alive);
        Assert.Equal(AnimalState.Dead, a.State);
        Assert.Equal(0, a.Hp);
        Assert.Equal(9, a.Killer);
        Assert.Single(died);
        Assert.Equal((a, (ushort)9), died[0]);
    }

    [Fact]
    public void NoDamageOrNegativeDamageDoesNothing()
    {
        var a = Make(new FakeWorld());
        a.OnDamage(0, 1);
        a.OnDamage(-20, 1);
        a.OnDamage(float.NaN, 1);
        Assert.Equal(100, a.Hp);
    }

    [Fact]
    public void ADeadAnimalStaysWhereItFellAndTakesNoThreatsSeriously()
    {
        var w = new FakeWorld();
        var a = Make(w);
        a.OnDamage(500, 1);
        var at = a.Position;
        w.Threats.Add(new Vector3(2, 0, 0));
        Run(a, 5);
        Assert.Equal(at, a.Position);
        Assert.Equal(AnimalState.Dead, a.State);
    }

    [Fact]
    public void AKilledAnimalLiesForCorpseSecondsAndThenIsGone()
    {
        var a = Make(new FakeWorld(), Small("\"corpse_seconds\":10,"));
        a.OnDamage(500, 1);
        Run(a, 9);
        Assert.Null(a.Ended);
        Run(a, 1.5f);
        Assert.Equal(WorldDespawnReason.Expired, a.Ended);
    }

    // ---- the wire ----

    [Fact]
    public void TheStateCarriesSpeciesHpBehaviourAndHitBit()
    {
        var file = Shipped;
        var species = file.IndexOf("stag");
        var a = new Animal(file, species, new Vector3(10, 2, -5), 1f, new FakeWorld(), 3);
        var s = a.Serialize();
        Assert.Equal((ushort)species, s.Owner);
        Assert.Equal((byte)a.MaxHp, s.Hp);
        Assert.Equal(AnimalState.Idle, AnimalRules.StateOf(s.State));
        Assert.False(AnimalRules.HitToggled(s.State));
        Assert.Equal(0, a.Owner); // the owner field of the entity stays free: it belongs to no player

        a.OnDamage(10, 4);
        var hit = a.Serialize();
        Assert.True(AnimalRules.HitToggled(hit.State));
        Assert.Equal(AnimalState.Flee, AnimalRules.StateOf(hit.State));
        Assert.Equal((byte)(a.MaxHp - 10), hit.Hp);

        a.OnDamage(10, 4);
        Assert.False(AnimalRules.HitToggled(a.Serialize().State)); // flips with every hit, so a client sees each one

        a.OnDamage(1000, 4);
        var dead = a.Serialize();
        Assert.Equal(AnimalState.Dead, AnimalRules.StateOf(dead.State));
        Assert.Equal(0, dead.Hp);
    }

    [Fact]
    public void ALivingAnimalNeverShowsZeroHp()
    {
        var a = Make(new FakeWorld());
        a.OnDamage(99.9f, 1);
        Assert.True(a.Alive);
        Assert.Equal(1, a.Serialize().Hp);
    }

    [Fact]
    public void TheSpawnPayloadRoundTrips()
    {
        var a = new Animal(Shipped, Shipped.IndexOf("wolf"), new Vector3(100.5f, 3, -40.25f), 2f, new FakeWorld(), 1);
        a.OnDamage(5, 2);
        var s = a.Serialize();
        Assert.True(WorldCodec.TryDecodeSpawn(WorldCodec.EncodeSpawn(12, AnimalRules.TypeId, s), out var id, out var type, out var back));
        Assert.Equal((ushort)12, id);
        Assert.Equal(AnimalRules.TypeId, type);
        Assert.Equal(s, back);
    }

    [Fact]
    public void TheTypeRegistersOnceWithHitPointsOnTheWire()
    {
        var reg = new WorldEntityRegistry();
        AnimalRules.Register(reg);
        AnimalRules.Register(reg);
        var def = reg.Find(AnimalRules.TypeId);
        Assert.NotNull(def);
        Assert.True(def!.HasHp);
        Assert.Equal(50, AnimalRules.TypeId);
    }

    [Fact]
    public void TheManagerSpawnsDamagesKeepsTheCorpseAndRemovesIt()
    {
        var reg = new WorldEntityRegistry();
        AnimalRules.Register(reg);
        var world = new WorldEntityManager(reg);
        var gone = new List<WorldDespawned>();
        world.Despawned += gone.Add;
        var a = Make(new FakeWorld(), Small("\"corpse_seconds\":5,"));
        var id = world.Spawn(a);
        Assert.NotNull(id);

        world.Damage(id!.Value, 40, 3);
        Assert.Equal(1, world.Count);
        world.Damage(id.Value, 100, 3); // dies: it stays as a corpse, not despawned at once
        Assert.Equal(1, world.Count);
        Assert.Empty(gone);
        Assert.Equal(AnimalState.Dead, AnimalRules.StateOf(world.StateOf(id.Value)!.Value.State));

        for (var t = 0f; t < 6; t += Dt) world.Tick(Dt);
        Assert.Equal(0, world.Count);
        Assert.Equal(WorldDespawnReason.Expired, Assert.Single(gone).Reason);
    }

    [Fact]
    public void BoundsAreTheHitBoxStandingOnTheGround()
    {
        var a = Make(new FakeWorld(), at: new Vector3(4, 0, 6));
        var (centre, half, _) = a.Bounds;
        Assert.Equal(new Vector3(4, 0.7f, 6), centre);
        Assert.Equal(new Vector3(0.25f, 0.7f, 0.75f), half);
    }

    // ---- behaviour ----

    [Fact]
    public void UnwatchedItStandsStillAndGoesOnWhenAPlayerComesNear()
    {
        var w = new FakeWorld { IsWatched = false };
        var a = Make(w);
        Run(a, 30); // long enough to wander when watched
        Assert.Equal(AnimalState.Idle, a.State);
        Assert.Equal(Vector3.Zero, a.Position);
        w.IsWatched = true;
        w.Threats.Add(new Vector3(-10, 0, 0));
        Run(a, 2);
        Assert.Equal(AnimalState.Flee, a.State);
        Assert.True(a.Position.X > 10, $"it ran east, to x = {a.Position.X}");
    }

    [Fact]
    public void UnwatchedAThrownOneStillLandsAndGetsUp()
    {
        var w = new FakeWorld { IsWatched = false };
        var a = Make(w, Small("\"knockdown_seconds\":2,"));
        Assert.True(a.Throw(new Vector3(0, 5, -10), new Vector3(0, 0, 5)));
        Run(a, 1.5f);
        Assert.Equal(AnimalState.Down, a.State); // landed: the flight is not held up
        Run(a, 3);
        Assert.Equal(AnimalState.Flee, a.State); // and it got up; only then does it stand unseen
    }

    [Fact]
    public void AThreatInRangeMakesItRunAwayFromIt()
    {
        var w = new FakeWorld();
        var a = Make(w);
        w.Threats.Add(new Vector3(-10, 0, 0)); // west of it
        Run(a, 2);
        Assert.Equal(AnimalState.Flee, a.State);
        Assert.True(a.Position.X > 10, $"it ran east, to x = {a.Position.X}");
        Assert.True(MathF.Abs(a.Position.Z) < 5);
    }

    [Fact]
    public void ATreatOutOfRangeLeavesItBe()
    {
        var w = new FakeWorld();
        var a = Make(w);
        w.Threats.Add(new Vector3(-80, 0, 0));
        Run(a, 20);
        Assert.NotEqual(AnimalState.Flee, a.State);
    }

    [Fact]
    public void ItCalmsDownOnceTheThreatIsFarEnough()
    {
        var w = new FakeWorld();
        var a = Make(w, Small("\"calm_seconds\":2,"));
        w.Threats.Add(new Vector3(-10, 0, 0));
        Run(a, 1);
        Assert.Equal(AnimalState.Flee, a.State);
        w.Threats.Clear();
        Run(a, 1);
        Assert.Equal(AnimalState.Flee, a.State); // not yet: it keeps running for calm_seconds
        Run(a, 1.5f);
        Assert.NotEqual(AnimalState.Flee, a.State);
    }

    [Fact]
    public void AShotAnimalRunsFromTheShooterEvenWhenNothingIsInRange()
    {
        var w = new FakeWorld();
        w.Players[7] = new Vector3(0, 0, 100); // 100 m south: beyond flee_radius
        var a = Make(w, Small("\"alarm_seconds\":6,"));
        a.OnDamage(10, 7);
        Assert.Equal(AnimalState.Flee, a.State);
        Run(a, 3);
        Assert.True(a.Position.Z < -20, $"it ran north, away from the shooter, to z = {a.Position.Z}");
        Assert.Equal(AnimalState.Flee, a.State);
        Run(a, 6); // the alarm is over, then it calms down
        Assert.NotEqual(AnimalState.Flee, a.State);
    }

    [Fact]
    public void AShotAnimalWhoseShooterIsGoneBoltsTheWayItFaces()
    {
        var w = new FakeWorld();
        var a = Make(w); // yaw 0 faces -Z
        a.OnDamage(10, 99);
        Run(a, 2);
        Assert.True(a.Position.Z < -10);
    }

    [Fact]
    public void ItNeverStepsOntoGroundTheWorldRefuses()
    {
        var w = new FakeWorld { Blocked = (x, z) => x > 6 }; // a wall (or water) east of x = 6
        var a = Make(w);
        w.Threats.Add(new Vector3(-10, 0, 0)); // it wants to run east
        for (var t = 0f; t < 10; t += Dt)
        {
            a.Tick(Dt);
            Assert.True(a.Position.X <= 6.0001f, $"stepped onto x = {a.Position.X}");
        }
    }

    [Fact]
    public void AClimbOrDropOverTheStepLimitBlocks()
    {
        var w = new StepWorld();
        var a = Make(w, Small("\"max_step_m\":0.5,"));
        w.Threats.Add(new Vector3(-10, 0, 0));
        Run(a, 4);
        Assert.True(a.Position.X <= 3.0001f, $"went up the cliff at x = {a.Position.X}");
    }

    /// <summary>Flat ground with a 2 m high ledge from x = 3 on.</summary>
    sealed class StepWorld : IAnimalWorld
    {
        public readonly List<Vector3> Threats = new();
        public float? GroundY(float x, float z) => x > 3 ? 2f : 0f;
        public Vector3? NearestThreat(Vector3 from, float radius) => Threats.Count > 0 && Distance(Threats[0], from) <= radius ? Threats[0] : null;
        public Vector3? PositionOf(ushort player) => null;
    }

    [Fact]
    public void RunOverItIsThrownLiesAndGetsUpRunning()
    {
        var a = Make(new FakeWorld(), Small("\"knockdown_seconds\":2,"));
        Assert.True(a.Throw(new Vector3(0, 5, -10), new Vector3(0, 0, 5)));
        a.OnDamage(a.Def.Hp * Shipped.RunOverHpFraction, 7);
        Assert.Equal(AnimalState.Thrown, a.State); // the hit does not send it running: it is in the air
        Assert.Equal(50, a.Hp);
        a.Tick(Dt);
        Assert.True(a.Position.Y > 0);
        Run(a, 1f);
        Assert.Equal(AnimalState.Down, a.State);
        Assert.Equal(0, a.Position.Y);
        Assert.True(a.Position.Z < -3, $"thrown ahead of the car, z {a.Position.Z}");
        var landed = a.Position;
        Run(a, 1f);
        Assert.Equal(landed, a.Position); // lies still
        Run(a, 1.5f);
        Assert.Equal(AnimalState.Flee, a.State);
        Run(a, 1f);
        Assert.True(a.Position.Z < landed.Z - 3, "runs on, away from where the car was");
    }

    [Fact]
    public void TheAlarmFromAShotFreezesWhileItIsThrownAndDownAndRunsOutAfterGettingUp()
    {
        var a = Make(new FakeWorld(), Small("\"knockdown_seconds\":2,\"alarm_seconds\":3,\"calm_seconds\":0.5,"));
        a.Throw(new Vector3(0, 5, -10), new Vector3(0, 0, 5)); // alarms it: 3 s of running from the car
        Run(a, 1.2f);
        Assert.Equal(AnimalState.Down, a.State);
        Run(a, 0.5f);                                           // lying: the alarm does not tick
        Assert.Equal(AnimalState.Down, a.State);
        Run(a, 1.5f);
        Assert.Equal(AnimalState.Flee, a.State);                // up and running (the knockdown is over)
        Run(a, 2f);
        Assert.Equal(AnimalState.Flee, a.State);                // the alarm still had its full 3 s
        Run(a, 2.5f);
        Assert.NotEqual(AnimalState.Flee, a.State);             // ran out (+ calm_seconds): calmed down
    }

    [Fact]
    public void RunningOverOneHerdMemberDoesNotCutShortTheTumbleOfAnother()
    {
        var w = new FakeWorld();
        var a = Make(w); var b = Make(w, seed: 2);
        var herd = new Herd(Vector3.Zero, 5, 20, 10, 20, 1);
        herd.Add(a); herd.Add(b);
        a.Throw(new Vector3(0, 5, -10), Vector3.Zero);
        b.Throw(new Vector3(0, 5, -10), Vector3.Zero); // alarms the herd: a is in the air
        Assert.Equal(AnimalState.Thrown, a.State);
        Run(a, 1.2f);
        Assert.Equal(AnimalState.Down, a.State);
        b.Throw(new Vector3(0, 5, -10), Vector3.Zero);
        Assert.Equal(AnimalState.Down, a.State); // lying: not startled up either
    }

    [Fact]
    public void TheSecondRunOverKillsAndTheBodyKeepsTheArc()
    {
        var a = Make(new FakeWorld());
        var half = a.Def.Hp * Shipped.RunOverHpFraction;
        a.Throw(new Vector3(0, 5, -10), Vector3.Zero); a.OnDamage(half, 7);
        Run(a, 1.2f);
        Assert.Equal(AnimalState.Down, a.State);
        ushort killer = 0;
        a.Died += (_, by) => killer = by;
        a.Throw(new Vector3(0, 5, -10), Vector3.Zero); a.OnDamage(half, 7); // run over again while it lies
        Assert.Equal(AnimalState.Dead, a.State);
        Assert.Equal(7, killer);
        var at = a.Position;
        a.Tick(Dt);
        Assert.True(a.Position.Y > 0 && a.Position.Z < at.Z, "the body flies on");
        Run(a, 2f);
        Assert.Equal(0, a.Position.Y);
        var lies = a.Position;
        Run(a, 1f);
        Assert.Equal(lies, a.Position);
        Assert.False(a.Throw(new Vector3(0, 5, -10), Vector3.Zero)); // a dead one is not thrown again
    }

    [Fact]
    public void ThrownItRollsOnAlongTheGroundAndStopsWhereTheGroundEnds()
    {
        var a = Make(new FakeWorld());
        a.Throw(new Vector3(0, 5, -10), Vector3.Zero); a.OnDamage(1, 7);
        var touched = float.NaN;
        for (var i = 0; i < 200 && a.State == AnimalState.Thrown; i++)
        {
            a.Tick(Dt);
            if (float.IsNaN(touched) && a.Position.Y == 0 && i > 2) touched = a.Position.Z;
        }
        Assert.Equal(AnimalState.Down, a.State);
        Assert.True(a.Position.Z < touched - 1f, $"came down at z {touched}, lies at {a.Position.Z}: it kept part of its speed");
        Assert.Equal(0, a.Position.Y);

        var b = Make(new FakeWorld { Blocked = (_, z) => z < -6 }); // comes down at about z -5, a wall at -6
        b.Throw(new Vector3(0, 5, -10), Vector3.Zero); b.OnDamage(1, 7);
        Run(b, 3f);
        Assert.True(b.Position.Z >= -6, $"rolled through the wall to {b.Position.Z}");
    }

    [Fact]
    public void ThrownOverNoGroundItDropsWhereTheGroundEnds()
    {
        var a = Make(new FakeWorld { Blocked = (_, z) => z < -2 });
        a.Throw(new Vector3(0, 5, -10), Vector3.Zero); a.OnDamage(1, 7);
        Run(a, 2f);
        Assert.Equal(AnimalState.Down, a.State);
        Assert.True(a.Position.Z >= -2 && a.Position.Y == 0, $"landed at {a.Position}");
    }

    [Fact]
    public void ItWandersButStaysNearHome()
    {
        var a = Make(new FakeWorld(), Small("\"idle_min_seconds\":0.5,\"idle_max_seconds\":1,\"eat_chance\":0.2,"));
        var home = a.Home;
        var far = 0f;
        var moved = false;
        var states = new HashSet<AnimalState>();
        for (var t = 0f; t < 600; t += Dt)
        {
            a.Tick(Dt);
            far = MathF.Max(far, Distance(a.Position, home));
            moved |= Distance(a.Position, home) > 3;
            states.Add(a.State);
        }
        Assert.True(moved, "it never left the spot it spawned at");
        Assert.True(far <= 20.5f, $"it wandered {far} m from home (wander_radius 20)");
        Assert.Contains(AnimalState.Walk, states);
        Assert.Contains(AnimalState.Eat, states);
        Assert.Contains(AnimalState.Idle, states);
        Assert.DoesNotContain(AnimalState.Flee, states);
    }

    [Fact]
    public void AfterAFlightItWalksBackHome()
    {
        var w = new FakeWorld();
        var a = Make(w, Small("\"idle_min_seconds\":0.5,\"idle_max_seconds\":1,\"calm_seconds\":1,"));
        w.Threats.Add(new Vector3(-10, 0, 0));
        Run(a, 5);
        Assert.True(Distance(a.Position, a.Home) > 21f, "the flight took it out of its wander radius (20 m)");
        w.Threats.Clear();
        Run(a, 400);
        Assert.True(Distance(a.Position, a.Home) <= 21f, $"still {Distance(a.Position, a.Home)} m from home");
    }

    [Fact]
    public void ItFacesWhereItGoes()
    {
        var w = new FakeWorld();
        var a = Make(w);
        w.Threats.Add(new Vector3(0, 0, 10)); // south of it: it runs north, -Z, which is yaw 0
        Run(a, 2);
        Assert.True(MathF.Abs(TurretMath.AngleDiff(a.Yaw, 0)) < 0.2f, $"yaw {a.Yaw}");
    }

    [Fact]
    public void ASeedGivesTheSameLifeEveryTime()
    {
        static List<Vector3> Path(int seed)
        {
            var a = Make(new FakeWorld(), seed: seed);
            var path = new List<Vector3>();
            for (var i = 0; i < 4000; i++) { a.Tick(Dt); if (i % 100 == 0) path.Add(a.Position); }
            return path;
        }
        Assert.Equal(Path(5), Path(5));
        Assert.NotEqual(Path(5), Path(6));
    }

    [Fact]
    public void ItStandsStillWhenNoSpotAroundHasGround()
    {
        var w = new FakeWorld { Blocked = (x, z) => MathF.Abs(x) > 0.1f || MathF.Abs(z) > 0.1f }; // an island of one spot
        var a = Make(w, Small("\"eat_chance\":0,\"idle_min_seconds\":0.5,\"idle_max_seconds\":1,"));
        Run(a, 60);
        Assert.Equal(Vector3.Zero, a.Position);
        Assert.Equal(AnimalState.Idle, a.State);
    }

    // ---- vanishing (the Unicorn: startled, it turns immune and is teleported instead of running) ----

    static AnimalsFile Vanishing(string extra = "\"vanish_seconds\":2,\"vanish_min_m\":150,") => AnimalsFile.Parse($$"""
        {"version":1,"idle_min_seconds":100,"idle_max_seconds":200,"animals":[{"id":"deer","name":"Deer","model":"animated-animals/deer.glb","scale":0.33,"hp":100,{{extra}}
        "walk_speed":1.5,"run_speed":12,"turn_rate_deg_per_s":360,"flee_radius":30,"wander_radius":20,"hit_box":[0.5,1.4,1.5]}]}
        """);

    [Fact]
    public void AStartledVanishingAnimalVanishesInsteadOfRunning()
    {
        var w = new FakeWorld();
        var a = Make(w, Vanishing());
        Run(a, 1);
        w.Threats.Add(new Vector3(0, 0, 10));
        a.Tick(Dt);
        Assert.Equal(AnimalState.Vanish, a.State);
        Assert.True(a.Immune);
        var at = a.Position;
        Run(a, 1.5f);
        Assert.Equal((AnimalState.Vanish, at), (a.State, a.Position)); // it stands still the whole time
    }

    [Fact]
    public void EveryStartleOfAVanishingAnimalVanishes()
    {
        var shot = Make(new FakeWorld(), Vanishing());
        shot.OnDamage(10, 7);
        Assert.Equal(AnimalState.Vanish, shot.State);

        var w = new FakeWorld();
        var herd = new Herd(Vector3.Zero, 5, 20, 5, 10, 1);
        var (first, second) = (Make(w, Vanishing()), Make(w, Vanishing(), 2));
        herd.Add(first); herd.Add(second);
        herd.Alarm(first, new Vector3(0, 0, 5)); // the herd takes fright: the others startle
        Assert.Equal(AnimalState.Vanish, second.State);

        var run = Make(new FakeWorld(), Vanishing());
        Assert.True(run.Throw(new Vector3(0, 3, 4), new Vector3(0, 0, 5)));
        for (var i = 0; i < 160 && run.State != AnimalState.Vanish; i++) run.Tick(Dt); // flies, lies knocked down, gets up
        Assert.Equal(AnimalState.Vanish, run.State);
    }

    [Fact]
    public void ADamageDuringTheVanishIsAbsorbed()
    {
        var a = Make(new FakeWorld(), Vanishing());
        a.OnDamage(10, 7);
        Assert.Equal(AnimalState.Vanish, a.State);
        var before = a.Serialize();
        a.OnDamage(5000, 7);
        Assert.True(a.Alive);
        Assert.Equal(90f, a.Hp);
        Assert.Equal(before, a.Serialize()); // no hit toggle, no death
        Assert.False(a.Throw(new Vector3(0, 3, 4), Vector3.Zero));
        Assert.False(a.Carry());
        Assert.Equal(AnimalState.Vanish, a.State);
    }

    [Fact]
    public void AfterVanishSecondsItIsTeleportedAndStandsCalmForTheGrace()
    {
        var w = new FakeWorld();
        var a = Make(w, Vanishing());
        var seen = 0;
        a.Vanished += x => { seen++; Assert.Equal(AnimalState.Vanish, x.State); Assert.True(x.Teleport(new Vector3(200, 0, 0), 1f)); };
        a.OnDamage(10, 7);
        Run(a, 1.9f);
        Assert.Equal((0, AnimalState.Vanish), (seen, a.State));
        Run(a, 0.2f);
        Assert.Equal((1, AnimalState.Idle), (seen, a.State));
        Assert.True(Distance(a.Position, Vector3.Zero) >= 150);
        Assert.Equal(1f, a.Yaw);
        Assert.False(a.Teleport(Vector3.Zero, 0)); // only while vanishing
        Assert.Equal(200f, a.Position.X);

        // grace: a threat or a shot right after arriving startles nothing (the animal is not immune there either)
        w.Threats.Add(new Vector3(200, 0, 10));
        Run(a, 2.5f);
        Assert.Equal(AnimalState.Idle, a.State);
        a.OnDamage(10, 7);
        Assert.Equal(AnimalState.Idle, a.State);
        Assert.Equal(80f, a.Hp);
        Run(a, 1f); // 3 s of grace over
        Assert.Equal(AnimalState.Vanish, a.State);
    }

    [Fact]
    public void AVanishNobodyPlacesLeavesItWhereItWasAndStillCalm()
    {
        var w = new FakeWorld();
        var a = Make(w, Vanishing());
        a.OnDamage(10, 7);
        Run(a, 2.2f);
        Assert.Equal((AnimalState.Idle, Vector3.Zero), (a.State, a.Position));
        a.OnDamage(10, 7);
        Assert.Equal(AnimalState.Idle, a.State); // grace
    }

    [Fact]
    public void ASpeciesWithoutVanishSecondsFleesAsBefore()
    {
        var a = Make(new FakeWorld(), Vanishing(""));
        Assert.False(a.Vanishes);
        a.OnDamage(10, 7);
        Assert.Equal(AnimalState.Flee, a.State);
        Assert.False(a.Immune);
    }

    [Theory]
    [InlineData("\"vanish_seconds\":-1,")]
    [InlineData("\"vanish_seconds\":1,\"vanish_min_m\":-5,")]
    [InlineData("\"vanish_seconds\":1,\"vanish_grace_seconds\":-1,")]
    public void VanishFieldsAreValidated(string extra) => Assert.Throws<DataFileException>(() => Vanishing(extra));

    [Fact]
    public void TheShippedUnicornVanishesAndNoOtherSpecies()
    {
        var u = Shipped.Find("unicorn")!;
        Assert.True(u.VanishSeconds > 0 && u.VanishMinM > 0 && u.VanishGraceSeconds > 0);
        Assert.All(Shipped.Animals.Where(x => x.Id != "unicorn"), x => Assert.Equal(0f, x.VanishSeconds));
    }
}
