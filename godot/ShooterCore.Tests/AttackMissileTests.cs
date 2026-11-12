using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The attack missile: its tuning in killstreaks.json, steering, flight, impact and expiry, and its killstreak rules.</summary>
public class AttackMissileTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static KillstreakFile Shipping => Shipped.Killstreaks;

    const ushort Owner = 7, Enemy = 8;

    /// <summary>The fixture missile's own shallow limit (not the shipped one): the flying-fixture tests fly this dive.</summary>
    const float FixtureShallowRad = -10f * MathF.PI / 180f;

    /// <summary>A world with no walls and no players unless the test says so; records every detonation.</summary>
    sealed class FakeWorld : IMissileWorld
    {
        public FakeWorld(WorldEntityManager entities) => Entities = entities;
        public WorldEntityManager Entities { get; }
        public float Wall = float.PositiveInfinity;
        public object? WallCar;
        public List<HitTarget> Players = new();
        public float OwnerFacing;
        public List<MissileImpact> Blasts = new();

        /// <summary>Flat ground at this height (NaN for none); a ray going down meets it.</summary>
        public float GroundY = float.NaN;

        public float WorldDistance(Vector3 from, Vector3 dir, float len, out object? hitCar)
        {
            var ground = float.PositiveInfinity;
            if (!float.IsNaN(GroundY) && dir.Y < 0) ground = (from.Y - GroundY) / -dir.Y;
            var d = MathF.Min(Wall, ground);
            hitCar = d <= len && Wall <= ground ? WallCar : null;
            return d <= len ? d : float.PositiveInfinity; // the contract: infinity when nothing is within len
        }
        public IEnumerable<HitTarget> Targets() => Players;
        public float OwnerYaw(ushort owner) => OwnerFacing;
        public void Detonate(MissileImpact impact) => Blasts.Add(impact);
    }

    static WorldEntityManager NewManager()
    {
        var reg = new WorldEntityRegistry();
        AttackMissileRules.Register(reg);
        return new WorldEntityManager(reg);
    }

    static MissileTuning Tuning => Shipping.Find("attack_missile")!.Missile!;

    /// <summary>A missile at the origin flying straight along -Z, with the test's tuning and world.</summary>
    static (AttackMissileEntity M, FakeWorld W) Flying(float turnDeg = 60, float life = 20)
    {
        var t = new MissileTuning
        {
            SpeedMps = 45, TurnRateRad = turnDeg * MathF.PI / 180f, LifeSeconds = life, SpawnHeight = 150, SpawnPitchRad = -1.2f,
            ShallowPitchRad = FixtureShallowRad, SteepPitchRad = -80f * MathF.PI / 180f,
            Damage = 100, EdgeDamage = 30, FullDamageRadius = 3, SplashRadius = 12, SelfDamageMultiplier = 0.5f, KnockbackSpeed = 6,
        };
        var w = new FakeWorld(NewManager());
        // starts on the shallowest dive: the missile never flies level, so the tests fly the dive
        return (new AttackMissileEntity(Owner, t, w, Vector3.Zero, 0f, FixtureShallowRad), w);
    }

    // ---- killstreaks.json: the missile block ------------------------------------------------------------------------

    [Fact]
    public void ShippedMissileTuningIsLoadedAndSplashIsBiggerThanTheBazookas()
    {
        var t = Tuning;
        Assert.Equal(64f, t.SpeedMps);
        Assert.Equal(60f * MathF.PI / 180f, t.TurnRateRad, 1e-4f);
        Assert.Equal(60f, t.LifeSeconds); // a safety cap only
        Assert.Equal(450f, t.SpawnHeight);
        Assert.Equal(-45f * MathF.PI / 180f, t.SpawnPitchRad, 1e-4f);
        Assert.Equal(-32f * MathF.PI / 180f, t.ShallowPitchRad, 1e-4f);
        Assert.Equal(-80f * MathF.PI / 180f, t.SteepPitchRad, 1e-4f);
        Assert.Equal(12f, t.SplashRadius);
        Assert.True(t.SplashRadius > Shipped.Weapons.Find("bazooka")!.Splash!.Radius);
        Assert.Null(Shipping.Find("uav")!.Missile);
    }

    [Fact]
    public void TheAttackMissileNeedsAMissileBlock()
    {
        const string bare = "{\"id\":\"attack_missile\",\"name\":\"Missile\",\"kills_required\":5,\"unlock_level\":4,\"control\":\"pov\"}";
        var e = Assert.Throws<DataFileException>(() => KillstreakFile.Parse("{\"version\":1,\"streaks\":[" + bare + "]}"));
        Assert.Contains("\"missile\" block is required", e.Message);
    }

    [Fact]
    public void AMissileBlockOnAnotherStreakIsRefused()
    {
        var json = "{\"version\":1,\"streaks\":[{\"id\":\"uav\",\"name\":\"UAV\",\"kills_required\":4,\"unlock_level\":0,\"duration_seconds\":60,\"control\":\"none\","
            + "\"missile\":{\"speed_mps\":45,\"turn_rate_deg\":60,\"life_seconds\":20,\"spawn_height\":150,\"spawn_pitch_deg\":-45,\"pitch_min_deg\":-10,\"pitch_max_deg\":-80,\"damage\":1,\"edge_damage\":1,\"full_damage_radius\":1,\"splash_radius\":2,\"self_damage_multiplier\":1,\"knockback_speed\":1}}]}";
        var e = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
        Assert.Contains("only for the attack missile", e.Message);
    }

    [Theory]
    [InlineData("\"full_damage_radius\":12,\"splash_radius\":12", "must be less than")]
    [InlineData("\"spawn_pitch_deg\":-95", "\"missile.spawn_pitch_deg\" is required and must be between -89 and -1")]
    [InlineData("\"pitch_max_deg\":-95", "\"missile.pitch_max_deg\" is required and must be between -89 and -1")]
    [InlineData("\"pitch_min_deg\":-85", "\"missile.pitch_max_deg\" (the steepest) must be below")]
    [InlineData("\"spawn_pitch_deg\":-5", "must lie between")]
    [InlineData("\"speed_mps\":0", "must be more than 0")]
    public void BadMissileTuningNamesTheField(string overrideFields, string expected)
    {
        var fields = new Dictionary<string, string>
        {
            ["speed_mps"] = "64", ["turn_rate_deg"] = "60", ["life_seconds"] = "60", ["spawn_height"] = "450", ["spawn_pitch_deg"] = "-45",
            ["pitch_min_deg"] = "-10", ["pitch_max_deg"] = "-80",
            ["damage"] = "100", ["edge_damage"] = "30", ["full_damage_radius"] = "3", ["splash_radius"] = "12", ["self_damage_multiplier"] = "0.5", ["knockback_speed"] = "6",
        };
        foreach (var kv in overrideFields.Split(','))
        {
            var parts = kv.Split(':');
            fields[parts[0].Trim('"')] = parts[1];
        }
        var block = string.Join(",", fields.Select(kv => $"\"{kv.Key}\":{kv.Value}"));
        var json = "{\"version\":1,\"streaks\":[{\"id\":\"attack_missile\",\"name\":\"M\",\"kills_required\":5,\"unlock_level\":4,\"control\":\"pov\",\"missile\":{" + block + "}}]}";
        var e = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
        Assert.Contains(expected, e.Message);
    }

    // ---- registry and blast codec -----------------------------------------------------------------------------------

    [Fact]
    public void TheMissileHasItsOwnTypeIdWithNoHitPoints()
    {
        var reg = new WorldEntityRegistry();
        AttackMissileRules.Register(reg);
        AttackMissileRules.Register(reg); // idempotent
        var def = reg.Find(AttackMissileRules.TypeId);
        Assert.NotNull(def);
        Assert.Equal(10, def!.Id);
        Assert.False(def.HasHp);
        Assert.NotEqual(CorpseRules.TypeId, AttackMissileRules.TypeId);
    }

    [Fact]
    public void BlastPayloadRoundTrips()
    {
        var bytes = MissileCodec.EncodeBlast(300, 7, new Vector3(1.5f, -2f, 30.25f), 12f);
        Assert.Equal(MissileCodec.Size, bytes.Length);
        var d = MissileCodec.DecodeBlast(bytes);
        Assert.Equal(new MissileBlastInfo(300, 7, new Vector3(1.5f, -2f, 30.25f), 12f), d);
        Assert.Null(MissileCodec.DecodeBlast(bytes.AsSpan(0, 5)));
    }

    [Fact]
    public void TheSplashWeaponUsesTheMissileTuning()
    {
        var w = Tuning.AsWeapon();
        Assert.Equal(Tuning.Damage, w.Damage);
        Assert.Equal(Tuning.SplashRadius, w.Splash!.Radius);
        Assert.Equal(Tuning.FullDamageRadius, w.Splash.FullDamageRadius);
        // full damage at the centre, nothing past the radius
        Assert.Equal(Tuning.Damage, ProjectileMath.SplashDamage(w, 0f, false));
        Assert.Equal(0f, ProjectileMath.SplashDamage(w, Tuning.SplashRadius + 0.1f, false));
    }

    // ---- steering and flight ----------------------------------------------------------------------------------------

    [Fact]
    public void TheHeadingTurnsNoFasterThanTheTurnRate()
    {
        var (m, _) = Flying(turnDeg: 60);
        m.Aim(MathF.PI / 2, 0); // a right-angle turn is wanted
        m.Tick(0.1f);
        Assert.Equal(6f * MathF.PI / 180f, m.Yaw, 1e-3f); // 60 deg/s for 0.1 s
    }

    [Fact]
    public void TheHeadingReachesTheAimGivenEnoughTime()
    {
        var (m, _) = Flying(turnDeg: 60);
        m.Aim(MathF.PI / 2, 0);
        for (var i = 0; i < 30; i++) m.Tick(0.1f);
        Assert.Equal(MathF.PI / 2, m.Yaw, 1e-3f);
    }

    [Fact]
    public void TheHeadingTakesTheShortWayRound()
    {
        // facing 0.95 pi, wanting -0.95 pi: 0.1 pi across the +pi/-pi seam, 1.9 pi the long way round
        var w = new FakeWorld(NewManager());
        var m = new AttackMissileEntity(Owner, Tuning, w, Vector3.Zero, 0.95f * MathF.PI, 0f);
        m.Aim(-0.95f * MathF.PI, 0);
        m.Tick(0.1f);
        Assert.True(m.Yaw > 3.0f, "the missile should turn across the seam, not the long way");
    }

    [Fact]
    public void PitchNeverGoesLevelOrUp()
    {
        var (m, _) = Flying();
        m.Aim(0, 0.5f); // aiming up
        for (var i = 0; i < 60; i++) m.Tick(0.1f);
        Assert.Equal(FixtureShallowRad, m.Pitch, 1e-4f); // the shallowest dive, not level
        Assert.True(m.Pitch < 0);
    }

    [Fact]
    public void PitchNeverGoesStraightDown()
    {
        var (m, _) = Flying();
        m.Aim(0, -MathF.PI / 2); // straight down
        for (var i = 0; i < 60; i++) m.Tick(0.1f);
        Assert.Equal(Tuning.SteepPitchRad, m.Pitch, 1e-4f); // the steepest limit, short of vertical
        Assert.True(m.Pitch > -MathF.PI / 2 + 0.1f);
    }

    [Fact]
    public void PitchMovesAtTheTurnRateWithinTheClamp()
    {
        var (m, _) = Flying(turnDeg: 60); // starts on the shallowest dive, -10 deg
        m.Aim(0, -MathF.PI / 4);
        m.Tick(0.1f);
        Assert.Equal(FixtureShallowRad - 6f * MathF.PI / 180f, m.Pitch, 1e-3f);
    }

    [Fact]
    public void ASpawnPitchOutsideTheClampIsPulledInside()
    {
        var w = new FakeWorld(NewManager());
        var m = new AttackMissileEntity(Owner, Tuning, w, Vector3.Zero, 0f, 0f); // level, which is not allowed
        Assert.Equal(Tuning.ShallowPitchRad, m.Pitch, 1e-4f);
    }

    /// <summary>Flies a missile over flat ground from the shipped spawn height, holding one pitch; returns the time to impact.</summary>
    static float TimeToGround(float pitchRad)
    {
        var w = new FakeWorld(NewManager()) { GroundY = 0f };
        var m = new AttackMissileEntity(Owner, Tuning, w, new Vector3(0, Tuning.SpawnHeight, 0), 0f, pitchRad);
        m.Id = 1;
        m.Aim(0, pitchRad);
        const float dt = 0.01f;
        var t = 0f;
        while (m.Ended == null && t < 120f) { m.Tick(dt); t += dt; }
        Assert.Equal(WorldDespawnReason.Destroyed, m.Ended);
        Assert.Single(w.Blasts); // it hit the ground, it did not time out
        return t;
    }

    [Fact]
    public void TheShallowestDiveIsTheLongestFlightAndTheSteepestTheShortest()
    {
        var shallow = TimeToGround(Tuning.ShallowPitchRad);
        var steep = TimeToGround(Tuning.SteepPitchRad);
        Assert.True(shallow > steep * 1.5f, $"shallow {shallow} s vs steep {steep} s"); // 13.3 s vs 7.1 s at -32 and -80
    }

    [Fact]
    public void TheImpactTimeStaysInsideTheDiveBounds()
    {
        // height / (speed x sin of the pitch): the flight time at each limit, flat ground, no steering
        var expectShallow = Tuning.SpawnHeight / (Tuning.SpeedMps * MathF.Sin(-Tuning.ShallowPitchRad));
        var expectSteep = Tuning.SpawnHeight / (Tuning.SpeedMps * MathF.Sin(-Tuning.SteepPitchRad));
        Assert.InRange(TimeToGround(Tuning.ShallowPitchRad), expectShallow * 0.99f, expectShallow * 1.01f);
        Assert.InRange(TimeToGround(Tuning.SteepPitchRad), expectSteep * 0.99f, expectSteep * 1.01f);
        Assert.InRange(TimeToGround(Tuning.SpawnPitchRad), 9f, 11f); // the launch dive at -45 deg is about 9.9 s
        Assert.True(expectSteep > 0 && expectShallow < Tuning.LifeSeconds, "the safety cap never cuts a flight short");
    }

    [Fact]
    public void SteeringNeverLengthensTheFlightPastTheShallowestDive()
    {
        // whatever the owner aims at, the missile is never level: the ground comes within the shallow bound
        var bound = Tuning.SpawnHeight / (Tuning.SpeedMps * MathF.Sin(-Tuning.ShallowPitchRad)) * 1.01f;
        var w = new FakeWorld(NewManager()) { GroundY = 0f };
        var m = new AttackMissileEntity(Owner, Tuning, w, new Vector3(0, Tuning.SpawnHeight, 0), 0f, Tuning.SpawnPitchRad);
        m.Id = 1;
        m.Aim(MathF.PI, 0); // try to level out
        var t = 0f;
        while (m.Ended == null && t < 120f) { m.Tick(0.01f); t += 0.01f; }
        Assert.Equal(WorldDespawnReason.Destroyed, m.Ended);
        Assert.True(t <= bound, $"{t} s, bound {bound} s");
    }

    [Fact]
    public void ItFliesAlongItsDiveAtConstantSpeedWhenNotSteered()
    {
        var (m, _) = Flying();
        m.Tick(1f);
        // yaw 0 faces -Z; the pitch is the shallow dive, so 45 m along it
        var dir = HitMath.Forward(0, FixtureShallowRad);
        Assert.Equal(45f * dir.X, m.Position.X, 1e-3f);
        Assert.Equal(45f * dir.Y, m.Position.Y, 1e-3f);
        Assert.Equal(45f * dir.Z, m.Position.Z, 1e-3f);
        Assert.True(m.Position.Y < 0);
        Assert.Null(m.Ended);
    }

    [Fact]
    public void ItDetonatesOnAWorldHitJustInFrontOfTheSurface()
    {
        var (m, w) = Flying();
        w.Wall = 3f;
        m.Id = 42;
        m.Tick(0.1f); // 4.5 m per tick: the wall is 3 m away
        Assert.Equal(WorldDespawnReason.Destroyed, m.Ended);
        var b = Assert.Single(w.Blasts);
        Assert.Equal(42, b.Id);
        Assert.Equal(Owner, b.Owner);
        Assert.Null(b.Victim);
        Assert.Null(b.Car);
        Assert.Equal(3f - 0.02f, b.Position.Length(), 1e-3f); // the rocket's pull-back, along the dive
    }

    [Fact]
    public void ItReportsTheVehicleItHitsAsTheCar()
    {
        var (m, w) = Flying();
        var car = new object();
        w.Wall = 2f;
        w.WallCar = car;
        m.Tick(0.1f);
        Assert.Same(car, Assert.Single(w.Blasts).Car);
    }

    [Fact]
    public void ItDetonatesOnAPlayerAndNamesThemAsTheVictim()
    {
        var (m, w) = Flying();
        var ahead = HitMath.Forward(0, FixtureShallowRad) * 10f; // 10 m along the dive
        w.Players.Add(new HitTarget(5, ahead, Stance.Stand, 0));
        m.Tick(0.5f); // 22.5 m: the player is 10 m away
        Assert.Equal(WorldDespawnReason.Destroyed, m.Ended);
        var b = Assert.Single(w.Blasts);
        Assert.Equal((ushort)5, b.Victim);
        Assert.InRange(b.Position.Length(), 9f, 10f); // at the player's body, a little short of its centre
    }

    [Fact]
    public void TheOwnerIsNeverHitByTheirMissile()
    {
        var (m, w) = Flying();
        w.Players.Add(new HitTarget(Owner, new Vector3(0, 0, -5), Stance.Stand, 0));
        m.Tick(0.5f);
        Assert.Null(m.Ended);
        Assert.Empty(w.Blasts);
    }

    [Fact]
    public void ItExpiresWithoutABlastWhenItsLifeRunsOut()
    {
        var (m, w) = Flying(life: 20);
        for (var i = 0; i < 39; i++) m.Tick(0.5f); // 0.5 s steps are exact in binary: 39 steps is 19.5 s
        Assert.Null(m.Ended);
        m.Tick(0.5f);
        Assert.Equal(WorldDespawnReason.Expired, m.Ended);
        Assert.Empty(w.Blasts);
    }

    [Fact]
    public void ItHasNoHitPointsSoDamageDoesNotEndIt()
    {
        var (m, _) = Flying();
        m.OnDamage(500, Enemy);
        Assert.Null(m.Ended);
    }

    // ---- the killstreak with the real service and entity manager ----------------------------------------------------

    /// <summary>Owner with five kills on the missile slot, the streak activated: the harness the rules run in.</summary>
    sealed class Harness
    {
        public readonly List<(ushort Owner, EventKind Kind, byte[] Payload)> Sent = new();
        public readonly WorldEntityManager Entities = NewManager();
        public readonly FakeWorld World;
        public readonly KillstreakService Service;

        public Harness()
        {
            World = new FakeWorld(Entities) { OwnerFacing = 0.25f };
            var impl = Shipping.Streaks.Select(s => s.Id == AttackMissileStreak.StreakId
                ? (IKillstreak)new AttackMissileStreak(World, Tuning)
                : new LoggingKillstreak(s.Id));
            Service = new KillstreakService(Shipping, impl, (o, k, p) => Sent.Add((o, k, p)), _ => { });
            Service.Arm(Owner, new[] { AttackMissileStreak.StreakId, "", "" }, 0);
            for (var i = 0; i < 5; i++) Service.OnKill(Owner, Enemy, false);
        }
    }

    static NetInput Aim(float yaw) => NetInput.From(1, new MoveInput { Yaw = yaw }, 0f, 0);

    [Fact]
    public void ActivationSpawnsTheMissileAboveTheOwnerAndHandsOverTheControl()
    {
        var h = new Harness();
        Assert.True(h.Service.Activate(Owner, 0, new Vector3(3, 1, -4)));
        Assert.Equal(1, h.Entities.Count);
        var s = Assert.Single(h.Entities.Snapshot());
        Assert.Equal(AttackMissileRules.TypeId, s.Type);
        Assert.Equal(Owner, s.State.Owner);
        Assert.Equal(1 + Tuning.SpawnHeight, s.State.Position.Y, 0.05f);
        Assert.Equal(0.25f, s.State.YawRad, 0.01f); // the owner's facing
        Assert.NotNull(h.Service.ControlOf(Owner));
        Assert.Contains(h.Sent, e => e.Kind == EventKind.StreakUsed);
    }

    [Fact]
    public void TheOwnersInputSteersTheMissileAndOnlyThat()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 0, Vector3.Zero);
        Assert.True(h.Service.RouteInput(Owner, Aim(MathF.PI / 2)));
        h.Entities.Tick(1f); // the missile turns at 60 deg/s
        var yaw = h.Entities.Snapshot().Single().State.YawRad;
        Assert.Equal(0.25f + Tuning.TurnRateRad, yaw, 0.01f);
    }

    [Fact]
    public void AnImpactEndsTheStreakAndTheMissileCountsAsGone()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 0, Vector3.Zero);
        h.World.Wall = 1f;
        h.Entities.Tick(0.05f);
        Assert.Equal(0, h.Entities.Count);
        Assert.Single(h.World.Blasts);
        Assert.Null(h.Service.ControlOf(Owner));
        Assert.Contains(h.Sent, e => e.Kind == EventKind.StreakEnded);
    }

    [Fact]
    public void TheOwnersDeathRemovesTheMissileAndEndsTheControl()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 0, Vector3.Zero);
        h.Service.OnDeath(Owner);
        Assert.Equal(0, h.Entities.Count);
        Assert.Null(h.Service.ControlOf(Owner));
        Assert.Empty(h.World.Blasts); // death removes the missile; it does not detonate
    }

    [Fact]
    public void TheMissileIsRemovedAtTheRoundEndWithoutABlast()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 0, Vector3.Zero);
        h.Service.Reset();
        Assert.Equal(0, h.Entities.Count);
        Assert.Empty(h.World.Blasts);
    }

    [Fact]
    public void RouteInputOnlyReachesTheOwnersMissile()
    {
        var h = new Harness();
        h.Service.Activate(Owner, 0, Vector3.Zero);
        Assert.False(h.Service.RouteInput(Enemy, Aim(0))); // not the owner: nothing routed
    }
}
