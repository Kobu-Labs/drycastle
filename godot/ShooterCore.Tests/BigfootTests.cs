using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Bigfoot event (#264): the lifecycle, the data, the hit count and phases, the hit volume, the arcs, the brain on a fake world, the trail and the codecs.</summary>
public class BigfootTests
{
    const float Dt = 1f / 30f;

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly BigfootFile Shipped = GameData.Load(DataDir).Bigfoot;

    static string AssetsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "assets");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new InvalidOperationException("assets/manifest.json not found above the test output folder");
    }

    /// <summary>A world of flat ground at height 0 with what a test puts in it, recording what the Bigfoot does.</summary>
    sealed class FakeWorld : IBigfootWorld
    {
        public Func<float, float, bool> NoGround = (_, _) => false;
        public Func<Vector3, Vector3, bool> Wall = (_, _) => false;
        public readonly List<BigfootPlayer> PlayerList = new();
        public readonly List<BigfootVehicle> VehicleList = new();
        public readonly List<(Vector3 From, Vector3 Velocity)> Trunks = new();
        public readonly List<(ushort Player, float Damage, BigfootHurt How)> Hurts = new();
        public readonly List<(ushort Player, Vector3 Velocity)> Knocks = new();
        public readonly List<(ushort Player, Vector3 At)> Holds = new();
        public readonly List<(ushort Player, Vector3 Velocity, float Grace)> Launches = new();
        public readonly List<(ushort Car, Vector3 Landing, float Seconds)> VehicleThrows = new();
        public readonly List<(BigfootFxKind Kind, Vector3 At, ushort Id)> Fxs = new();

        public IReadOnlyList<BigfootPlayer> Players => PlayerList;
        public IReadOnlyList<BigfootVehicle> Vehicles => VehicleList;
        public float? GroundY(float x, float z, float nearY) => NoGround(x, z) ? null : 0f;
        public bool Blocked(Vector3 from, Vector3 to, float radius) => Wall(from, to);
        public void SpawnTrunk(Vector3 from, Vector3 velocity) => Trunks.Add((from, velocity));
        public void Hurt(ushort player, float damage, BigfootHurt how) => Hurts.Add((player, damage, how));
        public void Knock(ushort player, Vector3 velocity) => Knocks.Add((player, velocity));
        public void Hold(ushort player, Vector3 at) => Holds.Add((player, at));
        public void Launch(ushort player, Vector3 velocity, float graceSeconds) => Launches.Add((player, velocity, graceSeconds));
        public readonly List<(ushort Car, Vector3 Hold, float Seconds)> VehicleLifts = new();
        public void LiftVehicle(ushort vehicle, Vector3 hold, float seconds) { VehicleLifts.Add((vehicle, hold, seconds)); VehicleList.RemoveAll(v => v.Id == vehicle); } // the server lists no vehicle in its hands
        public void ThrowVehicle(ushort vehicle, Vector3 landing, float seconds, float spinRadPerS) => VehicleThrows.Add((vehicle, landing, seconds));
        public void Fx(BigfootFxKind kind, Vector3 at, ushort id = 0, byte extra = 0) => Fxs.Add((kind, at, id));

        public FakeWorld Player(ushort id, float x, float z, bool onFloor = true, bool inVehicle = false) { PlayerList.Add(new BigfootPlayer(id, new Vector3(x, 0, z), onFloor, inVehicle)); return this; }
        public void Remove(ushort id) => PlayerList.RemoveAll(p => p.Id == id);
    }

    static Bigfoot Make(FakeWorld w, BigfootFile? data = null, int participants = 0) => new(data ?? Shipped, w, Vector3.Zero, 0f, seed: 7, participants);

    /// <summary>Runs the brain for <paramref name="seconds"/> of game time; the world's players do not move.</summary>
    static void Run(Bigfoot b, float seconds, Action<float>? each = null)
    {
        for (var t = 0f; t < seconds; t += Dt) { b.Tick(Dt); each?.Invoke(t); }
    }

    /// <summary>Hits it until the given fraction of its count is taken (never the last hit).</summary>
    static void HitUntil(Bigfoot b, float fraction, ushort attacker = 1)
    {
        while (b.TakenFraction < fraction && b.Alive && b.HitsTaken + 1 < b.HitsNeeded) b.Hit(attacker, 1);
    }

    // ---- the data ----

    [Fact]
    public void TheShippedFileLoadsAndFitsTheRules()
    {
        Assert.Equal(2f, Shipped.HitScale);
        Assert.Equal(2 * MoveConsts.CapsuleHeight, Shipped.HeightM, 3);
        Assert.True(Shipped.DamageMin >= 2 && Shipped.DamageMax <= 5, "all of its damage is 2 to 5");
        Assert.True(Shipped.Hits.SecondPhaseAt < Shipped.Hits.ThirdPhaseAt && Shipped.Hits.ThirdPhaseAt < Shipped.Hits.EnrageAt);
        Assert.True(Shipped.Reward.CorpseSeconds > CorpseRules.LifetimeSeconds, "the reward corpse lasts longer than a player's");
        Assert.True(Shipped.Reward.TrailLengthM / Shipped.Reward.TrailSpacingM <= BigfootTrail.MaxPrints);
    }

    [Fact]
    public void EveryKeyOfTheShippedFileIsOneTheCodeReads()
    {
        // unknown keys are skipped by the loader, so a typo in the file would silently leave a default in force
        var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "bigfoot.json")), opts);
        void Check(JsonElement obj, Type type, string path)
        {
            var props = type.GetProperties().ToDictionary(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name), p => p);
            foreach (var key in obj.EnumerateObject())
            {
                if (path == "" && key.Name == "version") continue;
                Assert.True(props.ContainsKey(key.Name), $"{path}{key.Name} is not read by {type.Name}");
                if (key.Value.ValueKind == JsonValueKind.Object) Check(key.Value, props[key.Name].PropertyType, path + key.Name + ".");
            }
        }
        Check(doc.RootElement, typeof(BigfootFile), "");
    }

    [Fact]
    public void TheModelAndTheRewardItemExist()
    {
        Assert.True(File.Exists(Path.Combine(AssetsDir(), Shipped.Model)), Shipped.Model);
        var items = GameData.Load(DataDir).Items;
        var head = items.Find(Shipped.Reward.HeadItem);
        Assert.NotNull(head);
        Assert.True(head!.HeadshotImmune);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AssetsDir(), "manifest.json")));
        var listed = doc.RootElement.GetProperty("unverified_models").EnumerateArray().Select(m => m.GetProperty("file").GetString()).ToHashSet();
        Assert.Contains(Shipped.Model, listed);
    }

    [Fact]
    public void TheModelHasTheClipsTheClientPlays()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AssetsDir(), Shipped.Model));
        using var doc = JsonDocument.Parse(bytes.AsMemory(20, BitConverter.ToInt32(bytes, 12)));
        var clips = doc.RootElement.GetProperty("animations").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
        foreach (var plain in BigfootRules.Clips)
            Assert.Contains(clips, c => c == plain || c.EndsWith("|" + plain));
    }

    [Theory]
    [InlineData("\"second_phase_at\": 0.30", "\"second_phase_at\": 0.70")]
    [InlineData("\"enrage_at\": 0.85", "\"enrage_at\": 1.0")]
    [InlineData("\"damage_min\": 2", "\"damage_min\": 0")]
    [InlineData("\"hit_scale\": 2", "\"hit_scale\": 0")]
    [InlineData("\"trail_spacing_m\": 1.9", "\"trail_spacing_m\": 0.1")]
    [InlineData("\"model\": \"quaternius-giant/giant.glb\"", "\"model\": \"../giant.glb\"")]
    [InlineData("\"head_item\": \"bigfoot_head\"", "\"head_item\": \"\"")]
    public void BadFilesAreRefused(string good, string bad)
    {
        var json = File.ReadAllText(Path.Combine(DataDir, "bigfoot.json"));
        Assert.Contains(good, json);
        BigfootFile.Parse(json); // the shipped file is fine
        Assert.Throws<DataFileException>(() => BigfootFile.Parse(json.Replace(good, bad)));
    }

    [Fact]
    public void AnEmptyFileTakesTheDefaults()
    {
        var f = BigfootFile.Parse("{\"version\":1}");
        Assert.Equal(2, f.DamageMin);
        Assert.Equal(5, f.DamageMax);
        Assert.Equal(300f, f.Hits.PerPlayer);
    }

    // ---- the hit count, the phases and the weights ----

    [Fact]
    public void TheHitCountScalesWithThePlayersAndNeverDropsBelowOne()
    {
        var h = Shipped.Hits;
        Assert.Equal((int)h.PerPlayer, BigfootRules.HitsNeeded(h, 0));
        Assert.Equal((int)h.PerPlayer, BigfootRules.HitsNeeded(h, 1));
        Assert.Equal((int)h.PerPlayer * 4, BigfootRules.HitsNeeded(h, 4));
    }

    [Fact]
    public void PhasesFollowTheFractionTaken()
    {
        var h = Shipped.Hits;
        Assert.Equal(0, BigfootRules.PhaseAt(h, 0f));
        Assert.Equal(0, BigfootRules.PhaseAt(h, h.SecondPhaseAt - 0.01f));
        Assert.Equal(1, BigfootRules.PhaseAt(h, h.SecondPhaseAt));
        Assert.Equal(2, BigfootRules.PhaseAt(h, h.ThirdPhaseAt));
        Assert.False(BigfootRules.EnrageAt(h, h.EnrageAt - 0.01f));
        Assert.True(BigfootRules.EnrageAt(h, h.EnrageAt));
    }

    [Fact]
    public void WhatAHitCountsDependsOnWhatMadeIt()
    {
        var h = Shipped.Hits;
        bool Proj(byte w) => w == 2; // weapon 2 is the bazooka in this test
        Assert.Equal(HitSource.Bullet, BigfootRules.SourceOf(0, 5, Proj));
        Assert.Equal(HitSource.Explosive, BigfootRules.SourceOf(2, 5, Proj));
        Assert.Equal(HitSource.Explosive, BigfootRules.SourceOf(SpecialWeapons.Missile, 5, Proj));
        Assert.Equal(HitSource.Explosive, BigfootRules.SourceOf(SpecialWeapons.Moab, 5, Proj));
        Assert.Equal(HitSource.VehicleWeapon, BigfootRules.SourceOf(SpecialWeapons.VehicleGun, 5, Proj));
        Assert.Equal(HitSource.VehicleWeapon, BigfootRules.SourceOf(SpecialWeapons.Flak, 5, Proj));
        Assert.Equal(HitSource.Bullet, BigfootRules.SourceOf(SpecialWeapons.Turret, 5, Proj));
        Assert.Equal(HitSource.None, BigfootRules.SourceOf(SpecialWeapons.Vehicle, 5, Proj)); // ramming does nothing
        Assert.Equal(HitSource.None, BigfootRules.SourceOf(SpecialWeapons.Fall, 5, Proj));
        Assert.Equal(1f, BigfootRules.Weight(h, HitSource.Bullet));
        Assert.Equal(h.ExplosiveWeight, BigfootRules.Weight(h, HitSource.Explosive));
        Assert.Equal(h.VehicleWeaponWeight, BigfootRules.Weight(h, HitSource.VehicleWeapon));
        Assert.Equal(0f, BigfootRules.Weight(h, HitSource.None));
    }

    [Fact]
    public void TheStateByteCarriesTheActTheHitBitThePhaseAndTheEnrage()
    {
        foreach (var act in Enum.GetValues<BigfootAct>())
            foreach (var toggle in new[] { false, true })
                foreach (var enraged in new[] { false, true })
                    for (var phase = 0; phase <= 2; phase++)
                    {
                        var b = BigfootRules.Pack(act, toggle, phase, enraged);
                        Assert.Equal(act, BigfootRules.ActOf(b));
                        Assert.Equal(toggle, BigfootRules.HitToggled(b));
                        Assert.Equal(phase, BigfootRules.PhaseOf(b));
                        Assert.Equal(enraged, BigfootRules.IsEnraged(b));
                    }
    }

    [Fact]
    public void ItsIdsAreClearOfTheOtherRanges()
    {
        Assert.True(BigfootRules.TargetId >= TargetDummies.FirstId + 64);
        Assert.NotEqual(BigfootRules.TypeId, CorpseRules.TypeId);
        var registry = new WorldEntityRegistry();
        BigfootRules.Register(registry);
        BigfootRules.Register(registry); // idempotent
        Assert.True(registry.Find(BigfootRules.TypeId)!.HasHp);
        Assert.False(registry.Find(BigfootRules.TrunkTypeId)!.HasHp);
    }

    // ---- the lifecycle ----

    [Fact]
    public void OneEventRunsAtATimeAndEndsWonOrDespawned()
    {
        var book = new EventBook();
        Assert.Null(book.Start("fireworks", 0));
        var run = book.Start(EventBook.Bigfoot, 5)!;
        Assert.True(run.Active);
        Assert.Null(book.Start(EventBook.Bigfoot, 6)); // one at a time
        Assert.Same(run, book.Current);
        Assert.Same(run, book.End(EventPhase.Won, 90));
        Assert.Equal(EventPhase.Won, run.Phase);
        Assert.Equal(90, run.EndedAt);
        Assert.Null(book.Current);
        Assert.Same(run, book.Last);
        Assert.Null(book.End(EventPhase.Won, 91)); // nothing runs
        Assert.False(run.Win(92)); // and a finished run stays finished
        var next = book.Start(EventBook.Bigfoot, 100)!;
        Assert.Same(next, book.End(EventPhase.Despawned, 101));
        Assert.Equal(EventPhase.Despawned, next.Phase);
        Assert.Null(book.End(EventPhase.Active, 102));
    }

    // ---- the hit volume ----

    static HitTarget Giant(Vector3 feet) => new(BigfootRules.TargetId, feet, Stance.Stand, 0, HeadGuarded: true, Scale: 2f);
    static HitTarget Person(Vector3 feet) => new(1, feet, Stance.Stand, 0);

    [Fact]
    public void TheHitVolumeIsThePlayersCapsuleAtTwiceTheSize()
    {
        var feet = new Vector3(0, 0, -10);
        var (a, b) = HitMath.Axis(Giant(feet));
        var (pa, pb) = HitMath.Axis(Person(feet));
        Assert.Equal(pa.Y * 2, a.Y, 3);
        Assert.Equal(pb.Y * 2, b.Y, 3);
        Assert.Equal(PlayerHitBox.Radius * 2, HitMath.BodyRadius(Giant(feet)), 4);
        Assert.Equal(PlayerHitBox.Radius, HitMath.BodyRadius(Person(feet)), 4);
    }

    [Fact]
    public void AShotOverAPlayersHeadStillHitsTheGiantAndIsNeverAHeadshot()
    {
        var feet = new Vector3(0, 0, -10);
        var origin = new Vector3(0, 3.0f, 0); // above a player's head, within the giant's body
        var dir = new Vector3(0, 0, -1);
        Assert.Null(HitMath.Raycast(origin, dir, 100, float.PositiveInfinity, new[] { Person(feet) }, 99));
        var hit = HitMath.Raycast(origin, dir, 100, float.PositiveInfinity, new[] { Giant(feet) }, 99);
        Assert.NotNull(hit);
        Assert.Equal(BigfootRules.TargetId, hit!.Value.Id);
        Assert.False(hit.Value.Head, "a headshot on the giant is a body hit");
        var wide = HitMath.Raycast(new Vector3(1.0f, 1.0f, 0), dir, 100, float.PositiveInfinity, new[] { Giant(feet) }, 99);
        Assert.NotNull(wide); // 1.0 m to the side is inside its 1.12 m radius, outside a player's 0.56 m
        Assert.Null(HitMath.Raycast(new Vector3(1.0f, 1.0f, 0), dir, 100, float.PositiveInfinity, new[] { Person(feet) }, 99));
    }

    [Fact]
    public void ABlastDistanceIsMeasuredToTheGrownBody()
    {
        var feet = new Vector3(0, 0, 0);
        Assert.Equal(0f, HitMath.DistanceToBody(new Vector3(1.0f, 1f, 0), Giant(feet)), 3);
        Assert.Equal(1.0f - PlayerHitBox.Radius, HitMath.DistanceToBody(new Vector3(1.0f, 1f, 0), Person(feet)), 3);
    }

    // ---- scripted arcs ----

    [Fact]
    public void AnArcLandsWhereItWasAimed()
    {
        var from = new Vector3(3, 2, -4);
        var to = new Vector3(40, 0, 25);
        var v = Ballistics.LaunchVelocity(from, to, 1.8f);
        var at = Ballistics.PositionAt(from, v, 1.8f);
        Assert.Equal(to.X, at.X, 3); Assert.Equal(to.Y, at.Y, 3); Assert.Equal(to.Z, at.Z, 3);
    }

    [Fact]
    public void AThrownVehicleFliesTheArcAndEndsExactlyAtTheLanding()
    {
        var t = new VehicleThrow(new Vector3(0, 0, 0), 0.5f, new Vector3(30, 1, -20), 2.4f, 2f);
        Assert.False(t.Done);
        var apex = 0f;
        for (var i = 0; i < 200 && !t.Done; i++) apex = MathF.Max(apex, t.Step(Dt).Y);
        Assert.True(t.Done);
        Assert.True(apex > 5f, $"it flew {apex} m high");
        Assert.Equal(new Vector3(30, 1, -20), t.Position);
        Assert.Equal(0f, t.VerticalSpeed);
        Assert.Equal(0.5f + 2f * 2.4f, t.Yaw, 3);
        Assert.Equal(new Vector3(30, 1, -20), t.Step(Dt)); // stepping on does nothing
    }

    // ---- the brain ----

    [Fact]
    public void ItChasesTheNearestPlayerOverTheGround()
    {
        var w = new FakeWorld().Player(1, 0, -80).Player(2, 200, 0);
        var b = Make(w);
        Run(b, 1.5f);
        Assert.True(b.Position.Z < -10, $"it walked towards player 1, to {b.Position}");
        Assert.True(MathF.Abs(b.Position.X) < 5);
        Assert.Equal(1, b.Target);
        Assert.Equal(0f, b.Position.Y);
        Assert.True(b.Act is BigfootAct.Run or BigfootAct.Walk);
    }

    [Fact]
    public void ItGivesUpAPlayerBeyondItsReach()
    {
        var w = new FakeWorld().Player(1, 0, -(Shipped.Movement.GiveUpM + 20));
        var b = Make(w);
        Run(b, 3);
        Assert.Equal(0, b.Target);
        Assert.Equal(Vector3.Zero, b.Position);
        Assert.Equal(BigfootAct.Idle, b.Act);
    }

    [Fact]
    public void ThePhasesAlsoBeginByTheClockWithNobodyShooting()
    {
        var w = new FakeWorld().Player(1, 0, -30);
        var b = Make(w);
        Run(b, Shipped.Hits.SecondPhaseAfterSeconds - 1);
        Assert.Equal(0, b.Phase);
        Run(b, 2);
        Assert.Equal(1, b.Phase);
        Run(b, Shipped.Hits.ThirdPhaseAfterSeconds - Shipped.Hits.SecondPhaseAfterSeconds);
        Assert.Equal(2, b.Phase);
        Run(b, Shipped.Hits.EnrageAfterSeconds - Shipped.Hits.ThirdPhaseAfterSeconds);
        Assert.True(b.Enraged);
    }

    [Fact]
    public void ItKeepsATargetUntilItDiesThenTakesAnother()
    {
        var w = new FakeWorld().Player(1, 0, -140).Player(2, 135, 0); // all beyond the throwing range: it only runs
        var b = Make(w);
        b.Tick(Dt);
        var first = b.Target;
        Assert.Equal(2, first);
        w.PlayerList.Insert(0, new BigfootPlayer(3, new Vector3(0, 0, 120), true, false)); // a nearer one arrives: not before the retarget time
        Run(b, 1.5f);
        Assert.Equal(first, b.Target);
        w.Remove(first);
        b.Tick(Dt);
        Assert.NotEqual(first, b.Target);
        Assert.NotEqual(0, b.Target);
    }

    [Fact]
    public void ABuildingStopsItAndItGoesRound()
    {
        // a wall across x = 10 from z = -30 to z = 30; the player stands behind it
        var w = new FakeWorld().Player(1, 40, 0);
        w.Wall = (from, to) => (from.X < 10 && to.X >= 10 - 0.001f || from.X >= 10 && to.X < 10) && MathF.Abs(to.Z) < 30;
        var b = Make(w);
        var crossed = false;
        Run(b, 60, _ => { if (MathF.Abs(b.Position.Z) < 30 && b.Position.X > 10.01f) crossed = true; });
        Assert.False(crossed, "it never walked through the wall");
        Assert.True(b.Position.X > 9.5f || MathF.Abs(b.Position.Z) > 20f, $"it pressed on and tried to get past, at {b.Position}");
    }

    [Fact]
    public void AStepTheGroundRefusesStopsIt()
    {
        var w = new FakeWorld().Player(1, 40, 0);
        w.NoGround = (x, _) => x > 5; // the map ends at x = 5
        var b = Make(w);
        Run(b, 30);
        Assert.True(b.Position.X <= 5.01f, $"it stayed on the ground, at {b.Position}");
    }

    [Fact]
    public void ItThrowsLogsAtAPlayerInRangeAndAimsAhead()
    {
        var w = new FakeWorld().Player(1, 0, -30);
        var b = Make(w);
        Run(b, 6);
        Assert.NotEmpty(w.Trunks);
        var (from, v) = w.Trunks[0];
        Assert.True(from.Y > 2f, "it throws from its hand, high up");
        // the log lands near the player's chest (the player stood still: no lead)
        var chest = MoveConsts.CapsuleHeight * 0.6f;
        var lands = Ballistics.PositionAt(from, v, FlightSeconds(from, v, chest)); // where the log passes the player's chest
        Assert.True(Vector2.Distance(new Vector2(lands.X, lands.Z), new Vector2(0, -30)) < 1.5f, $"it passes the chest at {lands}");
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.TrunkThrow);
    }

    /// <summary>The time a log launched from <paramref name="from"/> with <paramref name="v"/> comes down through height <paramref name="y"/>.</summary>
    static float FlightSeconds(Vector3 from, Vector3 v, float y) => (v.Y + MathF.Sqrt(v.Y * v.Y + 2 * Ballistics.Gravity * (from.Y - y))) / Ballistics.Gravity;

    [Fact]
    public void TheStompComesInPhaseTwoAndOnlyWhenSomeoneIsNear()
    {
        var w = new FakeWorld().Player(1, 0, -6);
        var b = Make(w);
        Run(b, 8);
        Assert.DoesNotContain(w.Fxs, f => f.Kind == BigfootFxKind.Shockwave); // phase one has no stomp
        Assert.Equal(0, b.Phase);
        HitUntil(b, Shipped.Hits.SecondPhaseAt);
        Assert.Equal(1, b.Phase);
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.Roar);
        Run(b, 8);
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.StompWindup); // the telegraph comes first
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.Shockwave);
        var windup = w.Fxs.FindIndex(f => f.Kind == BigfootFxKind.StompWindup);
        Assert.True(windup < w.Fxs.FindIndex(f => f.Kind == BigfootFxKind.Shockwave));
    }

    [Fact]
    public void TheShockwaveKnocksBackGroundedPlayersOnceAndAJumpAvoidsIt()
    {
        var w = new FakeWorld().Player(1, 0, -8).Player(2, 6, -8, onFloor: false); // 2 is in the air when the ring passes
        var b = Make(w);
        HitUntil(b, Shipped.Hits.SecondPhaseAt);
        Run(b, 12);
        Assert.Contains(w.Knocks, k => k.Player == 1);
        Assert.DoesNotContain(w.Knocks, k => k.Player == 2);
        var knock = w.Knocks.First(k => k.Player == 1);
        Assert.True(knock.Velocity.Y > 0, "it lifts as well as pushes");
        Assert.All(w.Hurts.Where(h => h.How == BigfootHurt.Shockwave), h => Assert.InRange(h.Damage, 2f, 5f));
        Assert.DoesNotContain(w.Hurts, h => h.Player == 2 && h.How == BigfootHurt.Shockwave);
    }

    [Fact]
    public void ARingCrossesAPlayerOnlyOncePerStomp()
    {
        var w = new FakeWorld().Player(1, 0, -12);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.SecondPhaseAt);
        Run(b, 12);
        var stomps = w.Fxs.Count(f => f.Kind == BigfootFxKind.Shockwave);
        Assert.True(stomps >= 1);
        Assert.True(w.Knocks.Count(k => k.Player == 1) <= stomps, "never more than one knock per stomp");
    }

    [Fact]
    public void ItThrowsAVehicleInContactInPhaseTwoToWhereThereIsGround()
    {
        var w = new FakeWorld().Player(1, 0, -12);
        w.VehicleList.Add(new BigfootVehicle(0x4001, new Vector3(2, 0, 1)));
        var b = Make(w);
        Run(b, 0.3f);
        Assert.Empty(w.VehicleThrows); // not in phase one
        HitUntil(b, Shipped.Hits.SecondPhaseAt);
        Run(b, 3);
        Assert.Single(w.VehicleThrows);
        var (car, landing, seconds) = w.VehicleThrows[0];
        Assert.Equal(0x4001, car);
        var d = Vector2.Distance(new Vector2(landing.X, landing.Z), new Vector2(2, 1));
        Assert.InRange(d, Shipped.VehicleThrow.MinDistanceM - 0.1f, Shipped.VehicleThrow.MaxDistanceM + 0.1f);
        Assert.Equal(Shipped.VehicleThrow.FlightSeconds, seconds);
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.VehicleThrow && f.Id == 0x4001);
    }

    [Fact]
    public void TheVehicleIsLiftedOverItsHeadForTheWindupAndThrownFromThere()
    {
        var w = new FakeWorld().Player(1, 0, -12);
        w.VehicleList.Add(new BigfootVehicle(0x4001, new Vector3(2, 0, 1)));
        var b = Make(w);
        HitUntil(b, Shipped.Hits.SecondPhaseAt);
        Run(b, Shipped.VehicleThrow.WindupSeconds * 0.5f);
        var lift = Assert.Single(w.VehicleLifts); // taken at the start of the wind-up: it cannot drive away
        Assert.Equal(0x4001, lift.Car);
        Assert.Equal(b.Position.Y + Shipped.VehicleThrow.HoldHeightM, lift.Hold.Y, 3);
        Assert.True(lift.Hold.Y > Shipped.HeightM);
        Assert.Empty(w.VehicleThrows);
        Assert.Equal(BigfootAct.VehicleWindup, b.Act);
        Run(b, Shipped.VehicleThrow.WindupSeconds);
        Assert.Single(w.VehicleThrows);
    }

    [Fact]
    public void ALongVehicleIsInContactAtItsEndAndAHelicopterOnlyWithinReach()
    {
        // a bus: its middle is 8 m away, its end at 2 m
        var w = new FakeWorld().Player(1, 0, -12);
        w.VehicleList.Add(new BigfootVehicle(0x4002, new Vector3(8, 0, 0), HalfLength: 6.03f));
        var b = Make(w);
        HitUntil(b, Shipped.Hits.SecondPhaseAt);
        Run(b, 0.5f);
        Assert.Equal(0x4002, Assert.Single(w.VehicleLifts).Car);
        // a helicopter overhead, out of reach, is left alone; one that hovers low is taken
        var sky = new FakeWorld().Player(1, 0, -12);
        sky.VehicleList.Add(new BigfootVehicle(0x4003, new Vector3(1, 30, 0), HalfLength: 4f));
        var b2 = Make(sky);
        HitUntil(b2, Shipped.Hits.SecondPhaseAt);
        Run(b2, 0.5f);
        Assert.Empty(sky.VehicleLifts);
        sky.VehicleList[0] = new BigfootVehicle(0x4003, new Vector3(1, 3, 0), HalfLength: 4f);
        Run(b2, 0.5f);
        Assert.Single(sky.VehicleLifts);
    }

    [Fact]
    public void ALiftedVehicleRisesHoldsAndFliesFromOverhead()
    {
        var t = new VehicleThrow(new Vector3(2, 0, 1), 0.5f, new Vector3(0, 4.2f, 0), 1f);
        Assert.Equal(new Vector3(2, 0, 1), t.Position);
        t.Step(0.5f);
        Assert.InRange(t.Position.Y, 1.5f, 2.7f);
        t.Step(1f);
        Assert.Equal(new Vector3(0, 4.2f, 0), t.Position); // held
        Assert.False(t.Done);
        t.Release(new Vector3(30, 0, 0), 2f, 1f);
        t.Step(1f);
        Assert.True(t.Position.Y > 4.2f && t.Position.X is > 10 and < 20);
        t.Step(1f);
        Assert.True(t.Done);
        Assert.Equal(new Vector3(30, 0, 0), t.Position);
        // one nobody throws comes down where it stood
        var dropped = new VehicleThrow(new Vector3(2, 0, 1), 0f, new Vector3(0, 4.2f, 0), 1f);
        for (var i = 0; i < 60 && !dropped.Done; i++) dropped.Step(0.1f);
        Assert.True(dropped.Done);
        Assert.Equal(new Vector3(2, 0, 1), dropped.Position);
    }

    [Fact]
    public void ItGrabsFlailsAndLaunchesAtAnotherPlayerInPhaseThree()
    {
        var w = new FakeWorld().Player(1, 0, -3).Player(2, 60, 0);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        Assert.Equal(2, b.Phase);
        Run(b, 3);
        // the grab: the player is held at its hand every tick until the launch
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.Grab && f.Id == 1);
        Assert.NotEmpty(w.Holds);
        Assert.All(w.Holds, h => Assert.Equal(1, h.Player));
        Assert.True(w.Holds[0].At.Y > 2f);
        Run(b, Shipped.Grab.HoldSeconds + 1);
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.Launch && f.Id == 1);
        var launch = w.Launches.First(l => l.Velocity != Vector3.Zero);
        Assert.Equal(1, launch.Player);
        Assert.Equal(Shipped.Grab.LaunchGraceSeconds, launch.Grace);
        // aimed at player 2: from the hand, it lands on them in the flight time
        var hand = w.Holds[^1].At;
        var lands = Ballistics.PositionAt(hand, launch.Velocity, Shipped.Grab.LaunchFlightSeconds);
        Assert.True(Vector2.Distance(new Vector2(lands.X, lands.Z), new Vector2(60, 0)) < 1f, $"it lands at {lands}");
        Assert.Equal(0, b.HeldPlayer);
    }

    [Fact]
    public void WithNobodyElseTheLaunchGoesInARandomDirection()
    {
        var w = new FakeWorld().Player(1, 0, -3);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        Run(b, 3 + Shipped.Grab.HoldSeconds);
        var launch = w.Launches.FirstOrDefault(l => l.Velocity != Vector3.Zero);
        Assert.NotEqual(default, launch);
        Assert.True(launch.Velocity.Y > 0);
        var flat = new Vector2(launch.Velocity.X, launch.Velocity.Z).Length() * Shipped.Grab.LaunchFlightSeconds;
        Assert.InRange(flat, Shipped.Grab.MinLaunchDistanceM, 80f); // thrown far
    }

    [Fact]
    public void TheHeldPlayerIsReplicatedAsTheOwnerAndLetGoWhenTheyDie()
    {
        var w = new FakeWorld().Player(1, 0, -3);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        Run(b, 3);
        Assert.Equal(1, b.HeldPlayer);
        Assert.Equal(1, b.Serialize().Owner);
        Assert.Equal(BigfootAct.Hold, BigfootRules.ActOf(b.Serialize().State));
        w.Remove(1);
        b.Tick(Dt);
        Assert.Equal(0, b.HeldPlayer);
        Assert.Equal(0, b.Serialize().Owner);
    }

    [Fact]
    public void ALaunchedPlayerHurtsWhomTheyHitAndTheLandingCostsTwoToFive()
    {
        var w = new FakeWorld().Player(1, 0, -3).Player(2, 30, 0);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        for (var i = 0; i < 600 && !w.Launches.Any(l => l.Velocity != Vector3.Zero); i++) b.Tick(Dt);
        Assert.Contains(w.Launches, l => l.Velocity != Vector3.Zero);
        // player 1 flies over player 2 and lands: feet next to them, then on the floor
        w.PlayerList.Clear();
        w.PlayerList.Add(new BigfootPlayer(2, new Vector3(30, 0, 0), true, false));
        w.PlayerList.Add(new BigfootPlayer(1, new Vector3(30.5f, 1, 0), false, false));
        Run(b, 0.5f);
        w.PlayerList[1] = new BigfootPlayer(1, new Vector3(30.5f, 0, 0), true, false);
        Run(b, 0.2f);
        Assert.Contains(w.Hurts, h => h.Player == 2 && h.How == BigfootHurt.LaunchHit);
        Assert.Contains(w.Hurts, h => h.Player == 1 && h.How == BigfootHurt.Landing);
        Assert.All(w.Hurts.Where(h => h.How is BigfootHurt.LaunchHit or BigfootHurt.Landing), h => Assert.InRange(h.Damage, 2f, 5f));
        Assert.Equal(1, w.Hurts.Count(h => h.Player == 2 && h.How == BigfootHurt.LaunchHit));
    }

    [Fact]
    public void ItGrabsNoOneInAVehicle()
    {
        var w = new FakeWorld().Player(1, 0, -3, inVehicle: true);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        Run(b, 6);
        Assert.Empty(w.Holds);
    }

    [Fact]
    public void EnragedItIsFasterAndItsCooldownsAreShorter()
    {
        var far = new FakeWorld().Player(1, 0, -140);
        var calm = Make(far);
        Run(calm, 4);
        var calmWalked = -calm.Position.Z;
        var far2 = new FakeWorld().Player(1, 0, -140);
        var angry = Make(far2);
        HitUntil(angry, Shipped.Hits.EnrageAt);
        Assert.True(angry.Enraged);
        var start = angry.Position.Z;
        Run(angry, 4);
        Assert.True(start - angry.Position.Z > calmWalked * 1.15f, $"enraged it walked {start - angry.Position.Z} m, calm {calmWalked} m");
        Assert.True(BigfootRules.IsEnraged(angry.Serialize().State));
    }

    [Fact]
    public void ItDiesAtTheHitCountTheKillerAndTheHittersAreCredited()
    {
        var w = new FakeWorld().Player(1, 0, -20).Player(2, 10, -10).Player(3, 400, 0);
        var b = Make(w, participants: 2);
        var died = 0;
        b.Died += _ => died++;
        Run(b, 0.2f);
        Assert.Equal(BigfootRules.HitsNeeded(Shipped.Hits, 2), b.HitsNeeded);
        for (var i = 0; i < 5; i++) b.Hit(3, 1); // player 3 hit it from afar: enough hits to count
        while (b.HitsTaken + 1 < b.HitsNeeded) b.Hit(1, 1);
        Assert.False(b.Hit(9, 0), "a hit that weighs nothing does nothing");
        Assert.True(b.Hit(2, 1), "the last hit kills");
        Assert.Equal(1, died);
        Assert.False(b.Alive);
        Assert.Equal(2, b.Killer);
        Assert.Contains((ushort)1, b.Credited);
        Assert.Contains((ushort)2, b.Credited);
        Assert.Contains((ushort)3, b.Credited);
        Assert.DoesNotContain((ushort)9, b.Credited);
        Assert.False(b.Hit(1, 1), "a dead one takes nothing");
        Assert.Equal(BigfootAct.Dead, b.Act);
        Assert.Equal(0, b.Serialize().Hp);
    }

    [Fact]
    public void ABigWeightFinishesItFaster()
    {
        var w = new FakeWorld().Player(1, 0, -20);
        var b = Make(w);
        var rockets = 0;
        while (b.Alive) { b.Hit(1, Shipped.Hits.ExplosiveWeight); rockets++; }
        Assert.Equal((int)MathF.Ceiling(b.HitsNeeded / Shipped.Hits.ExplosiveWeight), rockets);
    }

    [Fact]
    public void TheBodyLiesOutTheDeathClipThenIsGone()
    {
        var w = new FakeWorld().Player(1, 0, -20);
        var b = Make(w);
        b.Kill();
        Assert.Equal(0, b.Killer);
        Assert.Null(b.Ended);
        Run(b, Shipped.Reward.DeathSeconds - 1);
        Assert.Null(b.Ended);
        Run(b, 2);
        Assert.Equal(WorldDespawnReason.Destroyed, b.Ended);
    }

    [Fact]
    public void ARunLossIsAWipeItHealsToFullAndWaitsWhereItStands()
    {
        var w = new FakeWorld().Player(1, 0, -20);
        var b = Make(w);
        Run(b, 3);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        Assert.Equal(2, b.Phase);
        var at = b.Position;
        w.Remove(1); // the only participant dies
        b.Tick(Dt);
        Assert.True(b.Waiting);
        Assert.Equal(0f, b.HitsTaken);
        Assert.Equal(0, b.Phase);
        Assert.Contains(w.Fxs, f => f.Kind == BigfootFxKind.Heal);
        Assert.Equal(100, b.Serialize().Hp);
        // it waits: nobody near, so it does not move, and a far player does not wake it
        w.Player(2, 0, -500);
        Run(b, 5);
        Assert.Equal(at, b.Position);
        Assert.Equal(BigfootAct.Idle, b.Act);
        Assert.True(b.Waiting);
        // somebody comes within the wake radius: the fight is on again
        w.Remove(2);
        w.Player(3, at.X + 30, at.Z);
        Run(b, 1);
        Assert.False(b.Waiting);
        Assert.NotEqual(at, b.Position);
    }

    [Fact]
    public void PlayersWhoJustWalkAwayAreNotAWipe()
    {
        var w = new FakeWorld().Player(1, 0, -20);
        var b = Make(w);
        Run(b, 1);
        b.Hit(1, 1);
        w.PlayerList.Clear();
        w.Player(1, 0, -300); // alive, but far: out of the participant radius
        Run(b, 3);
        Assert.False(b.Waiting);
        Assert.Equal(1f, b.HitsTaken);
    }

    [Fact]
    public void WithNoLivingPlayerNearForTheDespawnTimeItDespawnsWithNoReward()
    {
        var w = new FakeWorld().Player(1, 0, -Shipped.Wipe.DespawnRadiusM - 50);
        w.NoGround = (_, _) => true; // it cannot walk: the player stays out of its reach
        var b = Make(w);
        Run(b, Shipped.Wipe.DespawnMinutes * 60 - 5);
        Assert.Null(b.Ended);
        Run(b, 10);
        Assert.Equal(WorldDespawnReason.Expired, b.Ended);
        Assert.Empty(b.Credited);
    }

    [Fact]
    public void ANearPlayerKeepsItFromDespawning()
    {
        var w = new FakeWorld().Player(1, 0, -100);
        var b = Make(w);
        Run(b, Shipped.Wipe.DespawnMinutes * 60 + 10, _ => { });
        Assert.Null(b.Ended); // one is always within the radius: it walked to them, and they stood there
    }

    [Fact]
    public void TheCountOnlyGoesUpAsPlayersComeAndNeverDownAsTheyLeave()
    {
        var w = new FakeWorld().Player(1, 0, -20);
        var b = Make(w);
        b.Tick(Dt);
        var one = b.HitsNeeded;
        w.Player(2, 5, -20).Player(3, 10, -20);
        b.Tick(Dt);
        Assert.Equal(BigfootRules.HitsNeeded(Shipped.Hits, 3), b.HitsNeeded);
        w.Remove(2); w.Remove(3);
        b.Hit(1, 1);
        Run(b, 1);
        Assert.Equal(BigfootRules.HitsNeeded(Shipped.Hits, 3), b.HitsNeeded);
        Assert.True(b.HitsNeeded > one);
    }

    [Fact]
    public void ARemovedOneLetsItsHeldPlayerGo()
    {
        var w = new FakeWorld().Player(1, 0, -3);
        var b = Make(w);
        HitUntil(b, Shipped.Hits.ThirdPhaseAt);
        Run(b, 3);
        Assert.Equal(1, b.HeldPlayer);
        b.Remove(WorldDespawnReason.Removed);
        Assert.Equal(0, b.HeldPlayer);
        Assert.Contains(w.Launches, l => l.Player == 1 && l.Velocity == Vector3.Zero);
        Assert.Equal(WorldDespawnReason.Removed, b.Ended);
    }

    // ---- the logs ----

    [Fact]
    public void ALogHurtsThePlayerItHitsAndEndsThere()
    {
        var w = new FakeWorld().Player(1, 20, 0);
        var from = new Vector3(0, 2, 0);
        var v = Ballistics.LaunchVelocity(from, new Vector3(20, 1, 0), 1f);
        var log = new TrunkEntity(Shipped, w, from, v, 3);
        for (var i = 0; i < 60 && log.Ended == null; i++) log.Tick(Dt);
        Assert.Equal(WorldDespawnReason.Destroyed, log.Ended);
        var hurt = Assert.Single(w.Hurts);
        Assert.Equal(1, hurt.Player);
        Assert.Equal(BigfootHurt.Trunk, hurt.How);
        Assert.InRange(hurt.Damage, 2f, 5f);
    }

    [Fact]
    public void ALogThatMissesLandsAndExpires()
    {
        var w = new FakeWorld().Player(1, 100, 100);
        var v = Ballistics.LaunchVelocity(new Vector3(0, 2, 0), new Vector3(20, 0, 0), 1f);
        var log = new TrunkEntity(Shipped, w, new Vector3(0, 2, 0), v, 3);
        for (var i = 0; i < 90 && log.Ended == null; i++) log.Tick(Dt);
        Assert.Equal(WorldDespawnReason.Expired, log.Ended);
        Assert.Empty(w.Hurts);
        Assert.Equal(0f, log.Position.Y);
    }

    [Fact]
    public void ALogDoesNotHurtSomeoneInAVehicle()
    {
        var w = new FakeWorld().Player(1, 20, 0, inVehicle: true);
        var v = Ballistics.LaunchVelocity(new Vector3(0, 2, 0), new Vector3(20, 1, 0), 1f);
        var log = new TrunkEntity(Shipped, w, new Vector3(0, 2, 0), v, 3);
        for (var i = 0; i < 60 && log.Ended == null; i++) log.Tick(Dt);
        Assert.Empty(w.Hurts);
    }

    // ---- the reward trail ----

    [Fact]
    public void TheTrailRunsFromTheFallOverGroundWithEvenSteps()
    {
        var start = new Vector3(100, 5, -40);
        var prints = BigfootTrail.Plan(start, 120, 1.9f, 8, (x, z) => 5f, new Random(3), out var end);
        Assert.Equal(63, prints.Count);
        Assert.Equal(prints[^1].Position, end);
        Assert.All(prints, p => Assert.Equal(5f, p.Position.Y));
        Assert.InRange(Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(end.X, end.Z)), 90f, 125f);
        for (var i = 1; i < prints.Count; i++)
            Assert.InRange(Vector3.Distance(prints[i].Position, prints[i - 1].Position), 0.8f, 3.2f); // a stride, alternating sides
    }

    [Fact]
    public void ATrailTakesTheHeadingWithGroundAndStopsWhereItRunsOut()
    {
        var start = Vector3.Zero;
        // ground only on the east side, up to 50 m
        var prints = BigfootTrail.Plan(start, 120, 2f, 200, (x, z) => x > -1 && x < 50 ? 0f : null, new Random(5), out var end);
        Assert.NotEmpty(prints);
        Assert.All(prints, p => Assert.True(p.Position.X > -1 && p.Position.X < 50));
        Assert.Equal(prints[^1].Position, end);
        // no ground anywhere: no trail, the corpse lies at the start
        Assert.Empty(BigfootTrail.Plan(start, 120, 2f, 10, (_, _) => null, new Random(5), out var none));
        Assert.Equal(start, none);
    }

    [Fact]
    public void TheTrailAndTheEffectsSurviveTheWire()
    {
        var prints = BigfootTrail.Plan(new Vector3(10, 2, 10), 40, 2f, 4, (_, _) => 2f, new Random(1), out _);
        var bytes = BigfootTrail.Encode(prints);
        Assert.Equal(1 + prints.Count * BigfootTrail.PrintBytes, bytes.Length);
        Assert.True(BigfootTrail.TryDecode(bytes, out var back));
        Assert.Equal(prints.Count, back.Count);
        for (var i = 0; i < prints.Count; i++)
        {
            Assert.True(Vector3.Distance(prints[i].Position, back[i].Position) < 0.02f);
            Assert.True(MathF.Abs(MathF.Atan2(MathF.Sin(prints[i].Yaw - back[i].Yaw), MathF.Cos(prints[i].Yaw - back[i].Yaw))) < 0.001f);
        }
        Assert.True(BigfootTrail.TryDecode(BigfootTrail.Encode(Array.Empty<Footprint>()), out var cleared));
        Assert.Empty(cleared);
        Assert.False(BigfootTrail.TryDecode(new byte[] { 3, 0, 0 }, out _));
        Assert.False(BigfootTrail.TryDecode(Array.Empty<byte>(), out _));

        var fx = new BigfootFxMsg(BigfootFxKind.VehicleThrow, new Vector3(-12.5f, 3.25f, 80f), 0x4002, 24);
        var got = BigfootFxCodec.Decode(BigfootFxCodec.Encode(fx));
        Assert.NotNull(got);
        Assert.Equal(fx.Kind, got!.Value.Kind);
        Assert.True(Vector3.Distance(fx.Position, got.Value.Position) < 0.02f);
        Assert.Equal(fx.Id, got.Value.Id);
        Assert.Equal(fx.Extra, got.Value.Extra);
        Assert.Null(BigfootFxCodec.Decode(new byte[BigfootFxCodec.Bytes - 1]));
        var bad = BigfootFxCodec.Encode(fx); bad[0] = 200;
        Assert.Null(BigfootFxCodec.Decode(bad));
    }

    // ---- the corpse, the chat, the wire ----

    [Fact]
    public void ACorpseCanLastLongerThanAPlayers()
    {
        var player = new Corpse(1, Vector3.Zero, 0, 0, 0);
        var giant = new Corpse(BigfootRules.TargetId, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(0) }, lifetime: 600);
        for (var t = 0f; t < CorpseRules.LifetimeSeconds + 1; t += 1f) { player.Tick(1f); giant.Tick(1f); }
        Assert.Equal(WorldDespawnReason.Expired, player.Ended);
        Assert.Null(giant.Ended);
        Assert.Single(giant.Items);
        for (var t = 0f; t < 500; t += 1f) giant.Tick(1f);
        Assert.Equal(WorldDespawnReason.Expired, giant.Ended);
    }

    [Fact]
    public void TheEventCommandKnowsTheBigfootAndTheCurrentEvent()
    {
        var admin = new ChatContext(true, Array.Empty<string>());
        Assert.Equal(new[] { "bigfoot" }, ChatCommands.Parse("/event BIGFOOT", admin).Args);
        Assert.Equal(new[] { "current", "end" }, ChatCommands.Parse("/event current end", admin).Args);
        Assert.Equal(new[] { "current", "cancel" }, ChatCommands.Parse("/event current cancel", admin).Args);
        Assert.True(ChatCommands.Parse("/event current", admin).Ok); // the server says what it needs
        Assert.False(ChatCommands.Parse("/event current explode", admin).Ok);
        Assert.Contains(ChatCommands.Hint("/event ", admin).Suggestions, s => s.Insert == "bigfoot");
        Assert.Contains(ChatCommands.Hint("/event current ", admin).Suggestions, s => s.Insert == "end");
        Assert.Contains("admin", ChatCommands.Parse("/event bigfoot", new ChatContext(false, Array.Empty<string>())).Error);
    }

    [Fact]
    public void TheWireKnowsTheBigfootsKindsAndTheXpReason()
    {
        Assert.True(Wire.ProtocolVersion >= 32);
        Assert.Equal(210, (int)EventKind.BigfootFx);
        Assert.Equal(211, (int)EventKind.BigfootTrail);
        Assert.False(Wire.IsEffect(EventKind.BigfootFx), "an effect that names a vehicle is reliable: the driver must stop predicting");
        Assert.Equal(300, GameData.Load(DataDir).Levels.Awards.EventWin);
        Assert.True(Enum.IsDefined(XpReason.EventWin));
        var lines = new[] { new XpLine(XpReason.EventWin, 300) };
        var back = ProgressionEvents.DecodeXpSummary(ProgressionEvents.EncodeXpSummary(new XpSummaryInfo(500, 3, lines)));
        Assert.Equal(XpReason.EventWin, back!.Lines[0].Reason);
    }

    [Fact]
    public void TheShotsReachTheGiantThroughTheTargetListLikeAnyPlayer()
    {
        // the server adds the Bigfoot to the live targets; a hit on it names its target id
        var w = new FakeWorld().Player(1, 0, -20);
        var b = Make(w);
        Assert.True(b.HitVolume.HeadGuarded);
        Assert.Equal(Shipped.HitScale, b.HitVolume.Scale);
        Assert.Equal(BigfootRules.TargetId, b.HitVolume.Id);
        var at = new HitTarget(BigfootRules.TargetId, new Vector3(0, 0, -30), Stance.Stand, 0, true, 2f);
        var ahead = HitMath.Raycast(new Vector3(0, 2, 0), new Vector3(0, 0, -1), 200, float.PositiveInfinity, new[] { Person(new Vector3(5, 0, -10)), at }, 99);
        Assert.Equal(BigfootRules.TargetId, ahead!.Value.Id);
    }
}
