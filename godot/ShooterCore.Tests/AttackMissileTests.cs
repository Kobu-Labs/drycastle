using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The attack missile: its tuning in killstreaks.json, steering, flight, impact and expiry, and its killstreak rules.</summary>
public class AttackMissileTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static KillstreakFile Shipping => Shipped.Killstreaks;

    const ushort Owner = 7, Enemy = 8;

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

        public float WorldDistance(Vector3 from, Vector3 dir, float len, out object? hitCar)
        {
            hitCar = Wall <= len ? WallCar : null;
            return Wall;
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
            Damage = 100, EdgeDamage = 30, FullDamageRadius = 3, SplashRadius = 12, SelfDamageMultiplier = 0.5f, KnockbackSpeed = 6,
        };
        var w = new FakeWorld(NewManager());
        return (new AttackMissileEntity(Owner, t, w, Vector3.Zero, 0f, 0f), w);
    }

    // ---- killstreaks.json: the missile block ------------------------------------------------------------------------

    [Fact]
    public void ShippedMissileTuningIsLoadedAndSplashIsBiggerThanTheBazookas()
    {
        var t = Tuning;
        Assert.Equal(45f, t.SpeedMps);
        Assert.Equal(60f * MathF.PI / 180f, t.TurnRateRad, 1e-4f);
        Assert.Equal(20f, t.LifeSeconds);
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
            + "\"missile\":{\"speed_mps\":45,\"turn_rate_deg\":60,\"life_seconds\":20,\"spawn_height\":150,\"spawn_pitch_deg\":-70,\"damage\":1,\"edge_damage\":1,\"full_damage_radius\":1,\"splash_radius\":2,\"self_damage_multiplier\":1,\"knockback_speed\":1}}]}";
        var e = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
        Assert.Contains("only for the attack missile", e.Message);
    }

    [Theory]
    [InlineData("\"full_damage_radius\":12,\"splash_radius\":12", "must be less than")]
    [InlineData("\"spawn_pitch_deg\":-95", "between -89 and -1")]
    [InlineData("\"speed_mps\":0", "must be more than 0")]
    public void BadMissileTuningNamesTheField(string overrideFields, string expected)
    {
        var fields = new Dictionary<string, string>
        {
            ["speed_mps"] = "45", ["turn_rate_deg"] = "60", ["life_seconds"] = "20", ["spawn_height"] = "150", ["spawn_pitch_deg"] = "-70",
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
    public void PitchIsClampedShortOfStraightUp()
    {
        var (m, _) = Flying();
        m.Aim(0, MathF.PI / 2);
        for (var i = 0; i < 60; i++) m.Tick(0.1f);
        Assert.True(m.Pitch < MathF.PI / 2 - 0.01f);
        Assert.True(m.Pitch > 1.5f);
    }

    [Fact]
    public void ItFliesStraightAtConstantSpeedWhenNotSteered()
    {
        var (m, _) = Flying();
        m.Tick(1f);
        // yaw 0, pitch 0 faces -Z
        Assert.Equal(-45f, m.Position.Z, 1e-3f);
        Assert.Equal(0f, m.Position.X, 1e-4f);
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
        Assert.Equal(-(3f - 0.02f), b.Position.Z, 1e-3f); // the rocket's pull-back
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
        w.Players.Add(new HitTarget(5, new Vector3(0, 0, -10), Stance.Stand, 0));
        m.Tick(0.5f); // 22.5 m: the player is 10 m away
        Assert.Equal(WorldDespawnReason.Destroyed, m.Ended);
        var b = Assert.Single(w.Blasts);
        Assert.Equal((ushort)5, b.Victim);
        Assert.InRange(-b.Position.Z, 9f, 10f); // at the player's body, a little short of its centre
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
