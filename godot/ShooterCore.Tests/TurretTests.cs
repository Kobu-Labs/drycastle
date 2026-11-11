using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The sentry turret: placement rules, aiming and targeting, damage and lifetime, and the killstreak's placement flow.</summary>
public class TurretTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static TurretDef Def => Shipped.Turret.Def;

    static readonly Vector3 Feet = Vector3.Zero;
    static readonly Vector3 Eye = new(0, 1.6f, 0);

    /// <summary>A flat world at y = 0 that the aim ray hits when it points down, and nothing else.</summary>
    internal sealed class FlatWorld : ITurretWorld
    {
        public (Vector3 Feet, Vector3 Eye)? Owner = (Feet, Eye);
        public IReadOnlyList<TurretCandidate> Foes = Array.Empty<TurretCandidate>();
        public readonly List<(ushort Target, float Damage)> Shots = new();

        public (Vector3 Feet, Vector3 Eye)? Pose(ushort owner) => Owner;

        public GroundHit? WorldRay(Vector3 from, Vector3 dir, float maxDistance)
        {
            if (dir.Y >= -1e-6f) return null;
            var t = -from.Y / dir.Y;
            if (t <= 0 || t > maxDistance) return null;
            return new GroundHit(from + dir * t, Vector3.UnitY);
        }

        public IReadOnlyList<TurretCandidate> Enemies(ushort owner) => Foes;

        public void Fire(ushort owner, ushort target, float damage) => Shots.Add((target, damage));
    }

    /// <summary>Aim straight down at the flat ground about 2.9 m away (inside the placement range).</summary>
    const float ValidPitch = -0.5f;

    // ---- placement

    [Fact]
    public void FlatGroundWithinRangeIsValid()
    {
        var hit = new GroundHit(new Vector3(2.9f, 0, 0), Vector3.UnitY);
        Assert.Equal(PlacementCheck.Valid, TurretMath.Evaluate(Feet, hit, Def).Check);
    }

    [Fact]
    public void NoGroundUnderTheAimIsNotValid()
    {
        var (check, point) = TurretMath.Evaluate(Feet, null, Def);
        Assert.Equal(PlacementCheck.NoGround, check);
        Assert.Equal(Feet, point);
    }

    [Fact]
    public void SteepGroundOrAWallIsNotValid()
    {
        var wall = new GroundHit(new Vector3(1, 1, 0), new Vector3(1, 0, 0));
        Assert.Equal(PlacementCheck.TooSteep, TurretMath.Evaluate(Feet, wall, Def).Check);
        var slope = new GroundHit(new Vector3(1, 0, 0), Vector3.Normalize(new Vector3(0.5f, 1f, 0))); // about 27 degrees
        Assert.Equal(PlacementCheck.TooSteep, TurretMath.Evaluate(Feet, slope, Def).Check);
    }

    [Fact]
    public void GroundBeyondThePlacementRangeIsOutOfRange()
    {
        var far = new GroundHit(new Vector3(3.2f, 0, 0), Vector3.UnitY);
        Assert.Equal(PlacementCheck.OutOfRange, TurretMath.Evaluate(Feet, far, Def).Check);
        // the range is measured on the ground: 2.5 m out and 0.8 m down is still in range
        var near = new GroundHit(new Vector3(2.5f, -0.8f, 0), Vector3.UnitY);
        Assert.Equal(PlacementCheck.Valid, TurretMath.Evaluate(Feet, near, Def).Check);
    }

    [Fact]
    public void GroundFarAboveOrBelowTheFeetIsUneven()
    {
        var ledge = new GroundHit(new Vector3(1, Def.PlacementStepM + 0.2f, 0), Vector3.UnitY);
        Assert.Equal(PlacementCheck.Uneven, TurretMath.Evaluate(Feet, ledge, Def).Check);
    }

    // ---- aim and targeting

    [Fact]
    public void YawTurnsTheShortWayRound()
    {
        Assert.Equal(0f, TurretMath.AngleDiff(0f, 0f), 5);
        Assert.Equal(-0.2f, TurretMath.AngleDiff(0.1f, -0.1f), 5);
        // across the wrap: from just under pi to just over -pi is a small step
        var d = TurretMath.AngleDiff(MathF.PI - 0.05f, -MathF.PI + 0.05f);
        Assert.Equal(0.1f, d, 4);
        Assert.Equal(0.3f, TurretMath.StepAngle(0f, 1f, 0.3f), 5);
        Assert.Equal(1f, TurretMath.StepAngle(0f, 1f, 5f), 5);
    }

    [Fact]
    public void YawPointsAtATargetInTheGodotConvention()
    {
        // yaw 0 faces -Z; a target straight ahead in -Z needs yaw 0
        Assert.Equal(0f, TurretMath.YawTo(Vector3.Zero, new Vector3(0, 0, -5)), 5);
        // a target on +X is to the right of -Z, so yaw is negative (positive turns left)
        Assert.True(TurretMath.YawTo(Vector3.Zero, new Vector3(5, 0, 0)) < 0);
        // and the turret's forward matches the server's shared forward vector
        var f = HitMath.Forward(TurretMath.YawTo(Vector3.Zero, new Vector3(3, 0, -4)), 0);
        Assert.True(Vector3.Dot(Vector3.Normalize(f), Vector3.Normalize(new Vector3(3, 0, -4))) > 0.999f);
    }

    [Fact]
    public void PickTargetChoosesTheNearestVisibleEnemyInSightRange()
    {
        var muzzle = new Vector3(0, 1, 0);
        var near = new TurretCandidate(7, new Vector3(4, 1, 0));
        var far = new TurretCandidate(3, new Vector3(10, 1, 0));
        var pick = TurretMath.PickTarget(muzzle, new[] { far, near }, 40, (_, _) => true);
        Assert.Equal((ushort?)7, pick);
    }

    [Fact]
    public void PickTargetSkipsEnemiesBehindWallsAndBeyondSight()
    {
        var muzzle = Vector3.Zero;
        var hidden = new TurretCandidate(1, new Vector3(2, 0, 0));
        var open = new TurretCandidate(2, new Vector3(9, 0, 0));
        var beyond = new TurretCandidate(3, new Vector3(50, 0, 0));
        Assert.Equal((ushort?)2, TurretMath.PickTarget(muzzle, new[] { hidden, open, beyond }, 40, (_, to) => to.X > 5));
        Assert.Null(TurretMath.PickTarget(muzzle, new[] { beyond }, 40, (_, _) => true));
    }

    [Fact]
    public void PickTargetTieKeepsTheLowerId()
    {
        var a = new TurretCandidate(9, new Vector3(3, 0, 0));
        var b = new TurretCandidate(4, new Vector3(0, 0, 3));
        Assert.Equal((ushort?)4, TurretMath.PickTarget(Vector3.Zero, new[] { a, b }, 40, (_, _) => true));
    }

    // ---- configuration

    [Fact]
    public void ShippedTurretFileLoadsWithTheDocumentedNumbers()
    {
        Assert.Equal(500f, Def.Hp);
        Assert.Equal(90f, Def.LifetimeSeconds);
        Assert.Equal(3f, Def.PlacementRangeM);
        Assert.Equal(3, Def.KnifeHitsToDestroy);
        // a third of the hp, rounded up, so three knife hits always destroy it
        Assert.Equal(167f, Shipped.Turret.KnifeDamage);
        Assert.True(Shipped.Turret.KnifeDamage * Def.KnifeHitsToDestroy >= Def.Hp);
    }

    [Fact]
    public void TurretFileRejectsAMissingOrBadField()
    {
        var good = ReadShippedJson();
        Assert.Throws<DataFileException>(() => TurretFile.Parse(good.Replace("\"hp\": 500,", "")));
        Assert.Throws<DataFileException>(() => TurretFile.Parse(good.Replace("\"knife_hits_to_destroy\": 3", "\"knife_hits_to_destroy\": 0")));
        Assert.Throws<DataFileException>(() => TurretFile.Parse(good.Replace("\"placement_probe_m\": 6", "\"placement_probe_m\": 2")));
        Assert.Throws<DataFileException>(() => TurretFile.Parse(good.Replace("\"placement_slope_deg\": 12", "\"placement_slope_deg\": 120")));
    }

    static string ReadShippedJson() => File.ReadAllText(Path.Combine(DataDir, "turret.json"));

    // ---- the entity

    [Fact]
    public void GhostFollowsTheAimAndIsValidOnlyOnGoodGround()
    {
        var world = new FlatWorld();
        var ghost = new TurretEntity(Def, 1, world);
        ghost.Track(Feet, Eye, 0f, ValidPitch);
        Assert.True(ghost.CanPlace);
        Assert.True(TurretRules.IsGhostValid(ghost.Serialize().State));

        ghost.Track(Feet, Eye, 0f, -0.2f); // the ray misses the ground within the probe
        Assert.False(ghost.CanPlace);
        Assert.False(TurretRules.IsGhostValid(ghost.Serialize().State));
        Assert.True(TurretRules.IsGhost(ghost.Serialize().State));
    }

    [Fact]
    public void PlaceArmsOnlyAValidGhost()
    {
        var world = new FlatWorld();
        var ghost = new TurretEntity(Def, 1, world);
        ghost.Track(Feet, Eye, 0f, -0.2f);
        Assert.False(ghost.Place());
        Assert.Equal(TurretEntity.Stage.Placing, ghost.Phase);

        ghost.Track(Feet, Eye, 0f, ValidPitch);
        Assert.True(ghost.Place());
        Assert.True(TurretRules.IsArmed(ghost.Serialize().State));
        Assert.False(ghost.Place()); // already armed
    }

    [Fact]
    public void ArmedTurretFiresAtAnEnemyOnlyAfterTheAcquireDelay()
    {
        var world = new FlatWorld { Foes = new[] { new TurretCandidate(5, new Vector3(0, 1, -6)) } };
        var t = new TurretEntity(Def, 1, world);
        t.Track(Feet, Eye, 0f, ValidPitch);
        t.Place();

        var dt = 0.05f;
        var elapsed = 0f;
        while (world.Shots.Count == 0 && elapsed < 5f) { t.Tick(dt); elapsed += dt; }
        Assert.NotEmpty(world.Shots);
        Assert.Equal((ushort)5, world.Shots[0].Target);
        Assert.Equal(Def.DamagePerShot, world.Shots[0].Damage);
        Assert.True(elapsed >= Def.AcquireDelayS - 0.001f, $"fired after {elapsed:F2} s, before the {Def.AcquireDelayS} s acquire delay");
    }

    [Fact]
    public void ArmedTurretDoesNotFireAtNothing()
    {
        var world = new FlatWorld();
        var t = new TurretEntity(Def, 1, world);
        t.Track(Feet, Eye, 0f, ValidPitch);
        t.Place();
        for (var i = 0; i < 100; i++) t.Tick(0.05f);
        Assert.Empty(world.Shots);
        Assert.Null(t.Target);
    }

    [Fact]
    public void ThreeKnifeHitsDestroyTheTurretAndTwoDoNot()
    {
        var t = ArmedAt(new FlatWorld());
        var knife = Shipped.Turret.KnifeDamage;
        t.OnDamage(knife, 2); t.OnDamage(knife, 2);
        Assert.Null(t.Ended);
        t.OnDamage(knife, 2);
        Assert.Equal(WorldDespawnReason.Destroyed, t.Ended);
    }

    [Fact]
    public void ALifetimeEndsTheTurretAsExpired()
    {
        var t = ArmedAt(new FlatWorld());
        for (var i = 0; i < (int)(Def.LifetimeSeconds / 0.5f) - 1; i++) t.Tick(0.5f);
        Assert.Null(t.Ended);
        t.Tick(0.5f);
        Assert.Equal(WorldDespawnReason.Expired, t.Ended);
    }

    [Fact]
    public void HitPointsReachTheWireAsAByteOfTheMaximum()
    {
        var t = ArmedAt(new FlatWorld());
        Assert.Equal((byte)255, t.Serialize().Hp);
        t.OnDamage(Def.Hp / 2, 2);
        Assert.InRange((int)t.Serialize().Hp, 126, 129);
    }

    [Fact]
    public void ArmedFlashIsOnlyForTheFramesAfterAShot()
    {
        var world = new FlatWorld { Foes = new[] { new TurretCandidate(5, new Vector3(0, 1, -6)) } };
        var t = new TurretEntity(Def, 1, world);
        t.Track(Feet, Eye, 0f, ValidPitch);
        t.Place();
        while (world.Shots.Count == 0) t.Tick(0.05f);
        world.Foes = Array.Empty<TurretCandidate>(); // no second shot while the flash fades
        Assert.True(TurretRules.IsFlashing(t.Serialize().State));
        for (var i = 0; i < 10; i++) t.Tick(0.05f);
        Assert.False(TurretRules.IsFlashing(t.Serialize().State));
    }

    static TurretEntity ArmedAt(FlatWorld world)
    {
        var t = new TurretEntity(Def, 1, world);
        t.Track(Feet, Eye, 0f, ValidPitch);
        Assert.True(t.Place());
        return t;
    }
}
