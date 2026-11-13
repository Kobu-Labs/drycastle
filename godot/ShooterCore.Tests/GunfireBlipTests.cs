using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Gunfire blips: which shots blip, the radius, the per-shooter rate limit and the lifetime.</summary>
public class GunfireBlipTests
{
    const ushort Me = 1, Foe = 2;
    static bool Q(ushort shooter = Foe, bool player = true, bool friends = false, int weapon = 0, WeaponKind kind = WeaponKind.Hitscan) =>
        GunfireBlipRules.Qualifies(shooter, Me, player, friends, weapon, 5, kind);

    [Fact]
    public void An_enemy_gun_shot_qualifies() => Assert.True(Q());

    [Fact]
    public void Own_shots_teammates_knives_and_streak_weapons_do_not()
    {
        Assert.False(Q(shooter: Me));
        Assert.False(Q(friends: true));
        Assert.False(Q(kind: WeaponKind.Melee));
        Assert.False(Q(weapon: SpecialWeapons.Turret)); // past the weapon table: turret, aircraft, vehicle guns
        Assert.False(Q(weapon: SpecialWeapons.VehicleGun));
        Assert.False(Q(player: false)); // the helper helicopter and unknown ids
    }

    [Fact]
    public void Data_file_defaults_are_150_m_1_5_s_and_a_quarter_second()
    {
        var m = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Match;
        Assert.Equal(150f, m.GunfireBlipRadius);
        Assert.Equal(1.5f, m.GunfireBlipSeconds);
        Assert.Equal(0.25f, m.GunfireBlipInterval);
    }

    [Fact]
    public void Only_shots_inside_the_radius_blip()
    {
        var b = new GunfireBlips(150, 1.5f, 0.25f);
        Assert.True(b.Add(Foe, new Vector2(149, 0), Vector2.Zero, 0));
        Assert.False(b.Add(3, new Vector2(151, 0), Vector2.Zero, 0));
    }

    [Fact]
    public void A_shooter_blips_once_per_interval()
    {
        var b = new GunfireBlips(150, 1.5f, 0.25f);
        var live = new List<GunfireBlip>();
        Assert.True(b.Add(Foe, new Vector2(10, 0), Vector2.Zero, 0));
        Assert.False(b.Add(Foe, new Vector2(10, 0), Vector2.Zero, 0.1));
        Assert.True(b.Add(3, new Vector2(10, 0), Vector2.Zero, 0.1)); // another shooter is not limited
        Assert.True(b.Add(Foe, new Vector2(10, 0), Vector2.Zero, 0.25));
        b.Collect(0.3, live);
        Assert.Equal(3, live.Count);
    }

    [Fact]
    public void A_blip_ages_then_expires_at_its_lifetime()
    {
        var b = new GunfireBlips(150, 1.5f, 0.25f);
        var live = new List<GunfireBlip>();
        b.Add(Foe, new Vector2(10, 5), Vector2.Zero, 10);
        b.Collect(10.75, live);
        Assert.Equal(0.5f, Assert.Single(live).Age01, 3);
        Assert.Equal(new Vector2(10, 5), live[0].Position);
        b.Collect(11.5, live);
        Assert.Empty(live);
    }

    [Fact]
    public void Reduce_motion_keeps_full_opacity_and_a_fixed_size()
    {
        var blip = new GunfireBlip(Vector2.Zero, 0.8f);
        Assert.Equal(1f, blip.Alpha(true));
        Assert.Equal(0.5f, blip.Growth(true));
        Assert.Equal(0.2f, blip.Alpha(false), 3);
    }
}
