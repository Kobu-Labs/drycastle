using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The gunship's rockets (killstreaks.json: speed 250 m/s, range 3000 m) run through the rocket sim with a per-launch speed and life.
/// The bazooka keeps its own 40 m/s and 3.75 s. These tests run <see cref="RocketFlight.Step"/> (the server's per-tick rule) on the
/// shipped numbers: the bug was that a gunship rocket flew at 40 m/s and burst about 150 m out, long before it reached the ground.
/// </summary>
public class RocketFlightTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static GunshipWeaponDef GunRockets => Shipped.Killstreaks.Gunship!.Weapons.Single(w => w.Id == "rockets");
    static WeaponDef Bazooka => Shipped.Weapons.Weapons.Single(w => w.Id == "bazooka");
    const float Dt = 1f / 30f;
    /// <summary>The gunship's muzzle: 120 m above the ground.</summary>
    const float GroundY = 0f, MuzzleHeight = 120f;

    /// <summary>The distance from <paramref name="from"/> along <paramref name="dir"/> to the ground plane (infinity if the ray does not go down to it).</summary>
    static float GroundDistance(Vector3 from, Vector3 dir) =>
        dir.Y < -1e-6f && from.Y > GroundY ? (from.Y - GroundY) / -dir.Y : float.PositiveInfinity;

    /// <summary>Runs a rocket tick by tick (as the server does) until it stops: the outcome, the distance flown and the time.</summary>
    static (RocketStep Last, float Flown, float Age) Fly(Vector3 origin, Vector3 dir, float speed, float life, IEnumerable<HitTarget> targets, ushort owner = 1)
    {
        var pos = origin; var age = 0f; var flown = 0f;
        while (true)
        {
            var step = RocketFlight.Step(pos, dir, age, speed, life, Dt, GroundDistance(pos, dir), targets, owner);
            flown += step.Distance;
            pos += dir * step.Distance;
            age += Dt;
            if (step.Outcome != RocketOutcome.Flying) return (step, flown, age);
        }
    }

    /// <summary>A ground point at slant range <paramref name="slant"/> from the muzzle, at the muzzle's bearing (+X).</summary>
    static (Vector3 Muzzle, Vector3 Target, Vector3 Dir) Shot(float slant)
    {
        var horizontal = MathF.Sqrt(slant * slant - MuzzleHeight * MuzzleHeight);
        var muzzle = new Vector3(0, GroundY + MuzzleHeight, 0);
        var target = new Vector3(horizontal, GroundY, 0);
        return (muzzle, target, Vector3.Normalize(target - muzzle));
    }

    [Fact]
    public void A_gunship_rocket_from_440_metres_reaches_and_detonates_on_the_ground_not_in_the_air()
    {
        var (muzzle, target, dir) = Shot(440f);
        Assert.Equal(440f, Vector3.Distance(muzzle, target), 0.01f);
        var r = GunRockets;
        var life = RocketFlight.LifeFor(r.RangeM, r.ProjectileSpeedMps);
        var (last, flown, age) = Fly(muzzle, dir, r.ProjectileSpeedMps, life, Array.Empty<HitTarget>());
        Assert.Equal(RocketOutcome.World, last.Outcome);
        Assert.Equal(440f, flown, r.ProjectileSpeedMps * Dt);  // it stopped at the ground within one tick of it
        Assert.True(age < life, "it hit the ground before its life ran out");
    }

    [Fact]
    public void A_gunship_rocket_hits_a_player_standing_at_440_metres()
    {
        var (muzzle, target, dir) = Shot(440f);
        var r = GunRockets;
        var player = new HitTarget(2, target, Stance.Stand, 0);
        var (last, _, _) = Fly(muzzle, dir, r.ProjectileSpeedMps, RocketFlight.LifeFor(r.RangeM, r.ProjectileSpeedMps), new[] { player });
        Assert.Equal(RocketOutcome.Player, last.Outcome);
        Assert.Equal(2, last.Victim);
    }

    [Fact]
    public void Had_the_gunship_rocket_kept_the_bazookas_40_mps_and_3_75_s_it_would_burst_about_150_metres_out()
    {
        // the bug, for the record: the same shot at the bazooka's flight expires in the air
        var (muzzle, _, dir) = Shot(440f);
        var (last, flown, _) = Fly(muzzle, dir, Bazooka.ProjectileSpeed, Bazooka.ProjectileLife, Array.Empty<HitTarget>());
        Assert.Equal(RocketOutcome.Expired, last.Outcome);
        Assert.Equal(150f, flown, 1.5f);
    }

    [Fact]
    public void The_bazookas_flight_is_unchanged_at_40_mps_for_3_75_s()
    {
        Assert.Equal(40f, Bazooka.ProjectileSpeed);
        Assert.Equal(3.75f, Bazooka.ProjectileLife);
        var (muzzle, _, dir) = Shot(440f);
        var (last, flown, age) = Fly(muzzle, dir, Bazooka.ProjectileSpeed, Bazooka.ProjectileLife, Array.Empty<HitTarget>());
        Assert.Equal(RocketOutcome.Expired, last.Outcome);
        Assert.Equal(150f, flown, 1.5f);
        Assert.Equal(3.75f, age, Dt);
    }

    [Fact]
    public void A_gunship_rocket_flies_its_whole_3000_metre_range_before_it_expires()
    {
        var r = GunRockets;
        Assert.Equal(250f, r.ProjectileSpeedMps);
        var life = RocketFlight.LifeFor(r.RangeM, r.ProjectileSpeedMps);
        Assert.True(life * r.ProjectileSpeedMps >= r.RangeM, "its life covers its range");
        var origin = new Vector3(0, 500, 0);
        var flat = Vector3.UnitX;                                 // no ground in the way: a sky shot
        var (last, flown, _) = Fly(origin, flat, r.ProjectileSpeedMps, life, Array.Empty<HitTarget>());
        Assert.Equal(RocketOutcome.Expired, last.Outcome);
        // it covers the range, and the margin is the most it flies beyond it
        Assert.InRange(flown, r.RangeM - r.ProjectileSpeedMps * Dt, r.RangeM + RocketFlight.LifeMarginS * r.ProjectileSpeedMps + r.ProjectileSpeedMps * Dt);
    }

    [Fact]
    public void The_life_is_the_range_over_the_speed_plus_the_margin()
    {
        Assert.Equal(12f + RocketFlight.LifeMarginS, RocketFlight.LifeFor(3000f, 250f), 1e-4f);
    }

    [Fact]
    public void A_rocket_with_no_per_launch_speed_uses_the_weapons_own_flight()
    {
        // the bazooka's own speed and life: the gunship's override is per launch, not a change to the weapon
        Assert.Equal(0f, Shipped.Killstreaks.Gunship!.Weapons.Single(w => w.Id == "cannon").ProjectileSpeedMps);
        Assert.Equal(0f, Shipped.Killstreaks.Ac130!.Weapons.Single(w => w.Kind == GunshipWeaponKind.Shell).ProjectileSpeedMps);
    }
}
