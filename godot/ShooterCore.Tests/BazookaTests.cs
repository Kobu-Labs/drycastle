using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class BazookaTests
{
    static readonly WeaponsFile File = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
    static WeaponDef Bz => File.Find("bazooka")!;
    const double Dt = 1.0 / 30;

    [Theory]
    [InlineData(0f, 100f)]
    [InlineData(1.5f, 100f)]
    [InlineData(3f, 73f)]       // 100 - 80 * 1.5 / 4.5
    [InlineData(4.5f, 47f)]     // 100 - 80 * 3 / 4.5
    [InlineData(5.99f, 20f)]
    [InlineData(6f, 0f)]
    [InlineData(10f, 0f)]
    public void SplashFalloffIsLinear(float d, float expected) => Assert.Equal(expected, ProjectileMath.SplashDamage(Bz, d, false), 0.6f);

    [Fact]
    public void SelfDamageIsHalf()
    {
        Assert.Equal(50f, ProjectileMath.SplashDamage(Bz, 0.5f, true));
        Assert.Equal(37f, ProjectileMath.SplashDamage(Bz, 3f, true), 0.6f);
    }

    [Fact]
    public void CapsuleDistance()
    {
        var feet = new Vector3(0, 0, 0);
        Assert.Equal(0f, ProjectileMath.DistanceToCapsule(new Vector3(0, 1, 0), feet, 1.8f, 0.3f));
        Assert.Equal(2.7f, ProjectileMath.DistanceToCapsule(new Vector3(3, 1, 0), feet, 1.8f, 0.3f), 1e-3f);
        Assert.Equal(1.7f, ProjectileMath.DistanceToCapsule(new Vector3(0, 3.5f, 0), feet, 1.8f, 0.3f), 1e-3f);
        Assert.Equal(0f, ProjectileMath.DistanceToCapsule(new Vector3(0, 0, 0), feet, 1.8f, 0.3f));
    }

    [Fact]
    public void SweepHitsPlayerBeforeWall()
    {
        var t = new[] { new HitTarget(2, new Vector3(0, 0, -10), Stance.Stand, 0) };
        var r = ProjectileMath.Sweep(new Vector3(0, 1, 0), -Vector3.UnitZ, 20, 15, t, 1);
        Assert.Equal(ImpactKind.Player, r.Kind); Assert.Equal(2, r.Victim); Assert.Equal(10f - PlayerHitBox.Radius, r.Distance, 0.05f);
    }

    [Fact]
    public void SweepStopsAtWallAndSkipsPlayersBehindIt()
    {
        var t = new[] { new HitTarget(2, new Vector3(0, 0, -10), Stance.Stand, 0) };
        var r = ProjectileMath.Sweep(new Vector3(0, 1, 0), -Vector3.UnitZ, 20, 5, t, 1);
        Assert.Equal(ImpactKind.World, r.Kind); Assert.Equal(5f, r.Distance);
    }

    [Fact]
    public void SweepIgnoresOwnerAndShortSegments()
    {
        var t = new[] { new HitTarget(1, new Vector3(0, 0, -2), Stance.Stand, 0), new HitTarget(2, new Vector3(0, 0, -10), Stance.Stand, 0) };
        Assert.Equal(ImpactKind.None, ProjectileMath.Sweep(new Vector3(0, 1, 0), -Vector3.UnitZ, 1.33f, float.PositiveInfinity, t, 1).Kind);
        var r = ProjectileMath.Sweep(new Vector3(0, 1, -8), -Vector3.UnitZ, 1.8f, float.PositiveInfinity, t, 1);
        Assert.Equal(2, r.Victim);
    }

    [Fact]
    public void KnockbackPointsAwayAndScales()
    {
        var k = ProjectileMath.Knockback(Bz, new Vector3(0, 0, 0), new Vector3(3, 0.9f, 0), 1f);
        Assert.True(k.X > 0 && k.Y > 0);
        Assert.Equal(Bz.Splash!.KnockbackSpeed, k.Length(), 1e-3f);
        Assert.Equal(Bz.Splash.KnockbackSpeed * 0.2f, ProjectileMath.Knockback(Bz, Vector3.Zero, Vector3.UnitX, 0.2f).Length(), 1e-3f);
        Assert.True(ProjectileMath.Knockback(Bz, Vector3.Zero, new Vector3(0, 0.3f, 0), 1f).Y > 4f); // from below: up
    }

    [Fact]
    public void BazookaAmmoAndTimers()
    {
        var w = new PlayerWeapons(File); double t = 10; w.Reset(t);
        FireOutcome? Tick(bool fire = false, bool reload = false, int slot = 3) { t += Dt; return w.Update(t, new WeaponInput(fire, reload, slot, false)); }
        for (var i = 0; i < 30; i++) Tick();
        Assert.Equal(3, w.Slot); Assert.Equal((1, 4), (w.Mag(3), w.Reserve(3)));
        Assert.NotNull(Tick(true));
        Assert.Equal(0, w.Mag(3));
        for (var i = 0; i < 31; i++) Tick(); Assert.Null(Tick(true)); // empty: the pull starts the reload
        Assert.True(w.Reloading);
        var start = t;
        while (w.Reloading) Tick();
        Assert.InRange(t - start, 3.4, 3.6);
        Assert.Equal((1, 3), (w.Mag(3), w.Reserve(3)));
        w.RefillReserves(); Assert.Equal(4, w.Reserve(3));
        Assert.Equal(0.85f, w.SpeedFor(3));
    }

    [Fact]
    public void LaunchAndExplosionCodecRoundTrip()
    {
        var l = new LaunchMsg(0x8001, 3, 2, new Vector3(1, 2, 3), new Vector3(0, 0, -40));
        Assert.Equal(l, WeaponCodec.DecodeLaunch(WeaponCodec.Encode(l)));
        var e = new ExplosionMsg(0x8001, 3, 2, new Vector3(4, 5, 6), 8f);
        Assert.Equal(e, WeaponCodec.DecodeExplosion(WeaponCodec.Encode(e)));
    }
}
