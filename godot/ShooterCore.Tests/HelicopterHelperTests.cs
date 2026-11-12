using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The helicopter helper: the gunner's pick, warning and burst timing, the data, and the streak through the real service.</summary>
public class HelicopterHelperTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    const ushort Owner = 1;
    static readonly Vector3 Muzzle = new(0, 45, 0);

    static HelicopterHelperTuning Tuning(float roll = 0) => new()
    {
        CycleMinS = 3, CycleMaxS = 6, WarningS = 1, Rounds = 6, RoundIntervalS = 0.25f, Damage = 25, Accuracy = 0.8f,
        MissRadiusM = 2, SightRangeM = 400, WeaponId = "m82", ExposureStand = 1, ExposureCrouch = 0.7f, ExposureProne = 0.4f,
    };

    static HelperCandidate Enemy(ushort id, float distance, Stance stance = Stance.Stand) =>
        new(id, Muzzle + new Vector3(0, -40, -distance), stance);

    static Func<double> Const(double v) => () => v;

    static readonly Func<Vector3, Vector3, bool> AllVisible = (_, _) => true;

    /// <summary>Runs the gunner for <paramref name="seconds"/> in small steps and returns what it did.</summary>
    static List<HelperAction> Run(HelicopterGunner g, double seconds, IReadOnlyList<HelperCandidate> enemies, Func<Vector3, Vector3, bool>? clear = null, float step = 0.05f)
    {
        var all = new List<HelperAction>();
        var buf = new List<HelperAction>();
        var visible = clear ?? AllVisible;
        for (var t = 0.0; t < seconds - 1e-6; t += step)
        {
            buf.Clear();
            g.Tick(step, Muzzle, enemies, visible, buf);
            all.AddRange(buf);
        }
        return all;
    }

    [Fact]
    public void Gunner_WaitsOutTheCycleThenWarnsThenFiresTheBurst()
    {
        var g = new HelicopterGunner(Tuning(), Const(0));   // roll 0: the shortest cycle, every round hits
        var enemies = new[] { Enemy(2, 20) };

        Assert.Empty(Run(g, 2.9, enemies));

        var warn = Run(g, 0.3, enemies).Single();
        var w = Assert.IsType<HelperWarnAction>(warn);
        Assert.Equal((ushort)2, w.Target);
        Assert.Equal(1f, w.Seconds);

        // the burst waits for the warning to run out (it began at 3.0 s, so the first round is at 4.0 s)
        Assert.Empty(Run(g, 0.7, enemies));
        var rounds = Run(g, 0.2, enemies).OfType<HelperRoundAction>().ToList();
        Assert.Single(rounds);
        Assert.True(rounds[0].Hit);
        Assert.Equal(25f, rounds[0].Damage);
        Assert.Equal(Muzzle, rounds[0].From);
    }

    [Fact]
    public void Gunner_FiresSixRoundsAtTheConfiguredInterval()
    {
        var g = new HelicopterGunner(Tuning(), Const(0));
        var enemies = new[] { Enemy(2, 20) };
        var all = Run(g, 6, enemies);

        var rounds = all.OfType<HelperRoundAction>().ToList();
        Assert.Equal(6, rounds.Count);
        Assert.Equal(6 * 25f, rounds.Sum(r => r.Damage)); // four hits of 25 kill a standing player (100 hp)
    }

    [Fact]
    public void Gunner_PrefersTheMostExposedNearTarget()
    {
        // prone at 10 m scores 10 / 0.4 = 25; standing at 20 m scores 20: the standing one is preferred
        var g = new HelicopterGunner(Tuning(), Const(0));
        var enemies = new[] { Enemy(1, 10, Stance.Prone), Enemy(2, 20, Stance.Stand) };
        var warn = Run(g, 3.1, enemies).OfType<HelperWarnAction>().Single();
        Assert.Equal((ushort)2, warn.Target);
    }

    [Fact]
    public void Gunner_SkipsHiddenAndOutOfRangeEnemies()
    {
        var g = new HelicopterGunner(Tuning(), Const(0));
        var enemies = new[] { Enemy(1, 5), Enemy(2, 500), Enemy(3, 30) };
        Func<Vector3, Vector3, bool> clear = (_, to) => to != enemies[0].Aim; // enemy 1 is behind a wall
        var warn = Run(g, 3.1, enemies, clear).OfType<HelperWarnAction>().Single();
        Assert.Equal((ushort)3, warn.Target);
    }

    [Fact]
    public void Gunner_StopsTheBurstWhenTheTargetGoesOutOfSight()
    {
        var g = new HelicopterGunner(Tuning(), Const(0));
        var enemies = new[] { Enemy(2, 20) };
        var seen = true;
        Func<Vector3, Vector3, bool> clear = (_, _) => seen;

        Run(g, 3.1, enemies, clear); // warned, warning under way
        Run(g, 1.05, enemies, clear); // the first round or two go off
        seen = false;
        Assert.Empty(Run(g, 2, enemies, clear).OfType<HelperRoundAction>());
        Assert.Null(g.Target);
    }

    [Fact]
    public void Gunner_CancelsTheWarningWhenTheTargetIsHiddenAtTheEnd()
    {
        var g = new HelicopterGunner(Tuning(), Const(0));
        var enemies = new[] { Enemy(2, 20) };
        var seen = true;
        Func<Vector3, Vector3, bool> clear = (_, _) => seen;

        Run(g, 3.1, enemies, clear);
        seen = false;
        Assert.Empty(Run(g, 1.2, enemies, clear));
        Assert.Null(g.Target);
    }

    [Fact]
    public void Gunner_MissesLandNearTheAim()
    {
        // roll 0.99: the hit roll misses (0.99 > 0.8) and the miss point is near the aim
        var g = new HelicopterGunner(Tuning(), Const(0.99));
        var enemies = new[] { Enemy(2, 20) };
        // a 0.99 roll also sets a long cycle (about 6 s), so the burst is seen only after nine seconds
        var rounds = Run(g, 9, enemies).OfType<HelperRoundAction>().ToList();

        Assert.NotEmpty(rounds);
        var aim = enemies[0].Aim;
        foreach (var r in rounds)
        {
            Assert.False(r.Hit);
            Assert.True(Vector3.Distance(r.End, aim) <= 2f + 1e-3f, "a miss lands within the miss radius of the aim");
        }
    }

    [Fact]
    public void Gunner_NoTargetMeansNoWarningAndAFreshCycle()
    {
        var g = new HelicopterGunner(Tuning(), Const(0));
        Assert.Empty(Run(g, 10, Array.Empty<HelperCandidate>()));
    }

    [Fact]
    public void Warning_CodecRoundTripsAndRejectsShortPayloads()
    {
        var w = new HelperWarning(7, 1234, 1f);
        var back = HelperWarningCodec.Decode(HelperWarningCodec.Encode(w));
        Assert.Equal(w, back);
        Assert.Null(HelperWarningCodec.Decode(new byte[HelperWarningCodec.Size - 1]));
    }

    [Fact]
    public void Shipped_HelperHasItsDurationAndTuning()
    {
        var def = Shipped.Killstreaks.Find(HelicopterHelperStreak.StreakId)!;
        Assert.Equal(60.0, def.DurationSeconds);
        Assert.NotNull(def.Helper);
        Assert.Equal(6, def.Helper!.Rounds);
        Assert.Equal("m82", def.Helper.WeaponId);
        Assert.Null(Shipped.Killstreaks.Find("turret")!.Helper);
    }

    [Fact]
    public void Parse_RequiresAHelperBlockAndOnlyForTheHelper()
    {
        const string noBlock = """{ "version": 1, "streaks": [ { "id": "helicopter_helper", "name": "H", "kills_required": 15, "unlock_level": 11, "control": "none" } ] }""";
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(noBlock));
        Assert.Contains("helper", ex.Message);

        const string onTurret = """{ "version": 1, "streaks": [ { "id": "turret", "name": "T", "kills_required": 6, "unlock_level": 5, "control": "none", "helper": { } } ] }""";
        Assert.Throws<DataFileException>(() => KillstreakFile.Parse(onTurret));
    }

    [Fact]
    public void Parse_NamesTheFieldOfABadValue()
    {
        const string badAccuracy = """
        { "version": 1, "streaks": [ { "id": "helicopter_helper", "name": "H", "kills_required": 15, "unlock_level": 11, "control": "none",
          "helper": { "cycle_min_s": 3, "cycle_max_s": 6, "warning_s": 1, "rounds": 6, "round_interval_s": 0.25, "damage": 25,
                      "accuracy": 1.5, "miss_radius_m": 2, "sight_range_m": 400, "weapon_id": "m82",
                      "exposure_stand": 1, "exposure_crouch": 0.7, "exposure_prone": 0.4 } } ] }
        """;
        var ex = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(badAccuracy));
        Assert.Contains("accuracy", ex.Message);
    }

    /// <summary>The server's side of the helper, faked: a helicopter that is there until removed, and a list of what it did.</summary>
    sealed class FakeWorld : IHelperWorld
    {
        public const ushort Heli = 77;
        public List<HelperCandidate> Targets = new();
        public readonly List<ushort> Spawned = new();
        public readonly List<ushort> Despawned = new();
        public readonly List<(ushort Heli, ushort Target, float Seconds)> Warnings = new();
        public readonly List<(ushort Target, bool Hit, float Damage)> Rounds = new();
        bool _alive = true;

        public ushort? SpawnHelicopter()
        {
            Spawned.Add(Heli);
            _alive = true;
            return Heli;
        }

        public void Despawn(ushort entityId)
        {
            Despawned.Add(entityId);
            _alive = false;
        }

        public Vector3? Helicopter(ushort entityId) => _alive && entityId == Heli ? Muzzle : null;
        public IReadOnlyList<HelperCandidate> Enemies(ushort owner) => Targets;
        public bool Clear(Vector3 from, Vector3 to) => true;
        public void Warn(ushort helicopter, ushort target, float seconds) => Warnings.Add((helicopter, target, seconds));
        public void Round(ushort owner, ushort target, Vector3 from, Vector3 end, bool hit, float damage) => Rounds.Add((target, hit, damage));
    }

    [Fact]
    public void Streak_FliesOverTheMatchThroughTheServiceAndEndsWithItsOwner()
    {
        var world = new FakeWorld { Targets = new List<HelperCandidate> { Enemy(2, 20) } };
        var streak = new HelicopterHelperStreak(Shipped.Killstreaks.Find(HelicopterHelperStreak.StreakId)!.Helper!, world, Const(0));
        var log = new List<string>();
        var service = new KillstreakService(Shipped.Killstreaks,
            Shipped.Killstreaks.Streaks.Select(s => s.Id == HelicopterHelperStreak.StreakId ? (IKillstreak)streak : new LoggingKillstreak(s.Id)),
            (_, _, _) => { }, log.Add);

        service.Arm(Owner, new[] { HelicopterHelperStreak.StreakId, "", "" }, 0);
        for (var i = 0; i < 15; i++) service.OnKill(Owner, (ushort)(100 + i), false);
        Assert.True(service.Activate(Owner, 0, Vector3.Zero));

        Assert.Equal(new[] { FakeWorld.Heli }, world.Spawned);
        Assert.Equal(1, streak.Flying);
        Assert.Equal(1, service.ActiveCount(Owner, HelicopterHelperStreak.StreakId));

        for (var t = 0.0; t < 7; t += 0.05) { service.Tick(t); streak.Tick(0.05f); }
        Assert.Single(world.Warnings);
        Assert.Equal((FakeWorld.Heli, (ushort)2, 1f), world.Warnings[0]);
        Assert.Equal(6, world.Rounds.Count);

        // the owner dies: the helicopter goes and the streak ends
        service.OnDeath(Owner);
        Assert.Equal(new[] { FakeWorld.Heli }, world.Despawned);
        Assert.Equal(0, streak.Flying);
    }

    [Fact]
    public void Streak_ExpiresWithItsDurationAndRemovesTheHelicopter()
    {
        var world = new FakeWorld();
        var streak = new HelicopterHelperStreak(Shipped.Killstreaks.Find(HelicopterHelperStreak.StreakId)!.Helper!, world, Const(0));
        var service = new KillstreakService(Shipped.Killstreaks,
            Shipped.Killstreaks.Streaks.Select(s => s.Id == HelicopterHelperStreak.StreakId ? (IKillstreak)streak : new LoggingKillstreak(s.Id)),
            (_, _, _) => { }, _ => { });

        service.Arm(Owner, new[] { HelicopterHelperStreak.StreakId, "", "" }, 0);
        for (var i = 0; i < 15; i++) service.OnKill(Owner, (ushort)(100 + i), false);
        service.Activate(Owner, 0, Vector3.Zero);

        service.Tick(59.0);
        Assert.Empty(world.Despawned);
        service.Tick(60.0);
        Assert.Equal(new[] { FakeWorld.Heli }, world.Despawned);
        Assert.Equal(0, service.ActiveCount(Owner, HelicopterHelperStreak.StreakId));
    }
}
