using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class BulletDropTests
{
    static WeaponDef Rifle(float velocity = 450, float range = 600, float zero = 100) => new()
    {
        Id = "t", Category = WeaponCategory.Sniper, Kind = WeaponKind.Hitscan, Slot = WeaponSlot.Primary, Range = range, MuzzleVelocity = velocity, ZeroDistance = zero,
    };

    [Fact]
    public void Only_a_single_bullet_hitscan_weapon_with_a_muzzle_velocity_drops()
    {
        Assert.True(BulletDrop.HasDrop(Rifle()));
        Assert.False(BulletDrop.HasDrop(Rifle(velocity: 0)));
        Assert.False(BulletDrop.HasDrop(new WeaponDef { Kind = WeaponKind.Projectile, MuzzleVelocity = 450 }));
        Assert.False(BulletDrop.HasDrop(new WeaponDef { Kind = WeaponKind.Hitscan, Pellets = 8, MuzzleVelocity = 450 }));
    }

    [Fact]
    public void The_bullet_is_on_the_line_of_sight_at_the_muzzle_and_at_the_zero_above_it_between_and_under_it_beyond()
    {
        var w = Rifle();
        Assert.Equal(0f, BulletDrop.Rise(w, 320, 0), 5);
        Assert.Equal(0f, BulletDrop.Rise(w, 320, 320), 5);
        Assert.True(BulletDrop.Rise(w, 320, 160) > 0);
        Assert.True(BulletDrop.Rise(w, 320, 400) < 0);
    }

    [Fact]
    public void The_fall_is_gravity_over_the_flight_time_at_the_muzzle_velocity()
    {
        var w = Rifle(velocity: 450);
        // unzeroed fall after 300 m: half g t squared with t = 300 / 450; against a 100 m zero the sight line is raised by the fall at 100 m, per 100 m
        var fall = (float x) => 0.5f * BulletDrop.Gravity * (x / 450f) * (x / 450f);
        Assert.Equal(fall(100) * 3 - fall(300), BulletDrop.Rise(w, 100, 300), 3);
        Assert.Equal(1.45f, -BulletDrop.Rise(w, 100, 300), 2);
        Assert.Equal(4.84f, BulletDrop.HoldoverMils(w, 100, 300), 1);
    }

    [Fact]
    public void A_dialled_distance_within_half_a_step_misses_by_centimetres()
    {
        var w = Rifle();
        Assert.True(MathF.Abs(BulletDrop.Rise(w, 320, 325)) < 0.05f);
        Assert.True(MathF.Abs(BulletDrop.Rise(w, 500, 505)) < 0.07f);
    }

    [Fact]
    public void The_rise_is_along_the_worlds_up_whatever_the_pitch()
    {
        var w = Rifle();
        var eye = new Vector3(3, 20, -4);
        var aim = Vector3.Normalize(new Vector3(1, -0.4f, 0.3f));
        var p = BulletDrop.Point(eye, aim, w, 200, 350);
        var onSight = eye + aim * 350;
        Assert.Equal(onSight.X, p.X, 3);
        Assert.Equal(onSight.Z, p.Z, 3);
        Assert.Equal(BulletDrop.Rise(w, 200, 350), p.Y - onSight.Y, 3);
        Assert.Equal(onSight, BulletDrop.Point(eye, aim, w, 350, 350));
    }

    [Fact]
    public void A_chord_of_one_segment_stays_within_five_millimetres_of_the_curve()
    {
        var w = Rifle(velocity: 420);
        var a = BulletDrop.Rise(w, 100, 300); var b = BulletDrop.Rise(w, 100, 300 + BulletDrop.SegmentM);
        var mid = BulletDrop.Rise(w, 100, 300 + BulletDrop.SegmentM / 2);
        Assert.True(MathF.Abs(mid - (a + b) / 2) < 0.005f);
    }

    [Fact]
    public void The_dial_moves_in_steps_between_the_smallest_zero_and_the_range()
    {
        var w = Rifle(range: 605);
        Assert.Equal(100, BulletDrop.DefaultZero(w));
        Assert.Equal(110, BulletDrop.Dial(w, 100, 1));
        Assert.Equal(BulletDrop.MinZeroM, BulletDrop.Dial(w, 60, -5));
        Assert.Equal(600, BulletDrop.Dial(w, 590, 3));
        Assert.Equal(320, BulletDrop.ClampZero(w, 323));
        Assert.Equal(BulletDrop.DefaultZeroM, BulletDrop.ClampZero(w, float.NaN));
        Assert.Equal(BulletDrop.DefaultZeroM, BulletDrop.DefaultZero(Rifle(zero: 0)));
    }

    [Fact]
    public void The_zero_message_round_trips_and_a_short_one_is_refused()
    {
        Assert.True(BulletDrop.TryDecodeZero(BulletDrop.EncodeZero(7, 320), out var index, out var zero));
        Assert.Equal(7, index);
        Assert.Equal(320, zero);
        Assert.False(BulletDrop.TryDecodeZero(new byte[2], out _, out _));
    }

    [Fact]
    public void A_dial_keeps_one_zero_per_weapon_and_a_death_resets_them()
    {
        var w = Rifle();
        var dial = new ZeroDial();
        Assert.Equal(100, dial.Of(3, w));
        dial.Set(3, w, 327);
        Assert.Equal(330, dial.Of(3, w));
        Assert.Equal(100, dial.Of(4, w));
        dial.Set(3, w, 5000);
        Assert.Equal(600, dial.Of(3, w));
        dial.Reset();
        Assert.Equal(100, dial.Of(3, w));
    }

    [Fact]
    public void Every_sniper_rifle_of_the_game_drops_and_no_other_weapon_does()
    {
        var weapons = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));
        foreach (var w in weapons.Weapons)
            Assert.Equal(w.Category == WeaponCategory.Sniper, BulletDrop.HasDrop(w));
    }
}
