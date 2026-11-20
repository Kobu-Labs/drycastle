using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The grenade (#346): weapons.json numbers, the flight rule the server runs, the fuse and the hotbar.</summary>
public class GrenadeTests
{
    static readonly WeaponsFile File = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
    static WeaponDef Grenade => File.Weapons.Single(w => w.Category == WeaponCategory.Grenade);
    const float Dt = 1f / 30f;

    /// <summary>A flat floor at y = 0 with an upward normal.</summary>
    static GrenadeStop Floor(Vector3 from, Vector3 dir, float len)
    {
        if (dir.Y >= -1e-6f || from.Y <= 0) return new GrenadeStop(float.PositiveInfinity, Vector3.UnitY);
        var d = from.Y / -dir.Y;
        return d <= len ? new GrenadeStop(d, Vector3.UnitY) : new GrenadeStop(float.PositiveInfinity, Vector3.UnitY);
    }

    [Fact]
    public void Shipped_grenade_is_a_special_slot_projectile_with_a_blast_smaller_than_a_wave()
    {
        var w = Grenade;
        Assert.Equal(WeaponKind.Projectile, w.Kind);
        Assert.Equal(WeaponSlot.Special, w.Slot);
        Assert.NotNull(w.Grenade);
        Assert.NotNull(w.Splash);
        Assert.True(w.Splash!.Radius < DustRules.FrontMinRadius); // the cloud only, no Blast wave
        Assert.True(w.Splash.Radius < 8f);                         // RocketFx.SimplifyMaxRadius: the light blast path
        Assert.Null(w.Lock);
    }

    [Fact]
    public void Hotbar_ends_with_the_grenade()
    {
        var bar = LoadoutHotbar.Build(File, null);
        Assert.Equal(4, bar.Length);
        Assert.Equal(WeaponCategory.Melee, File.Weapons[bar[2]].Category);
        Assert.Equal(WeaponCategory.Grenade, File.Weapons[bar[3]].Category);
    }

    [Fact]
    public void A_throw_uses_a_round_and_the_next_waits_for_the_cooldown_whatever_is_selected()
    {
        var weapons = new PlayerWeapons(File, hotbar: LoadoutHotbar.Build(File, null));
        var gi = File.Weapons.IndexOf(Grenade);
        var selected = weapons.Slot;
        Assert.NotEqual(gi + 1, selected);
        var shot = weapons.TryThrow(1.0);
        Assert.NotNull(shot);
        Assert.Equal(gi, shot!.Value.WeaponIndex);
        Assert.Equal(selected, weapons.Slot); // the weapon in hand stays selected
        Assert.Equal(1, weapons.Mag(gi + 1));
        Assert.Equal(Grenade.ReserveAmmo - 1, weapons.Reserve(gi + 1));
        Assert.Null(weapons.TryThrow(1.0 + Dt));
        Assert.NotNull(weapons.TryThrow(1.0 + Grenade.ReloadTime + Dt));
    }

    [Fact]
    public void The_trigger_does_not_throw_a_selected_grenade()
    {
        var weapons = new PlayerWeapons(File, hotbar: LoadoutHotbar.Build(File, null));
        var slot = File.Weapons.IndexOf(Grenade) + 1;
        weapons.Update(1.0, new WeaponInput(false, false, slot, false));
        Assert.Equal(slot, weapons.Slot);
        Assert.Null(weapons.Update(1.0 + Dt, new WeaponInput(true, false, 0, false)));
        Assert.Equal(Grenade.MagazineSize, weapons.Mag(slot));
    }

    [Fact]
    public void Last_round_gone_means_no_throw()
    {
        var weapons = new PlayerWeapons(File, hotbar: LoadoutHotbar.Build(File, null));
        var t = 1.0; var n = 0;
        while (weapons.TryThrow(t) != null) { n++; t += 5; }
        Assert.Equal(Grenade.MagazineSize + Grenade.ReserveAmmo, n);
        Assert.False(weapons.HasGrenade);
    }

    [Fact]
    public void Holding_the_key_aims_and_the_release_throws()
    {
        var k = new GrenadeThrower();
        Assert.Equal(GrenadeThrowStep.None, k.Tick(true, false, true));
        Assert.True(k.Aiming);
        Assert.Equal(GrenadeThrowStep.None, k.Tick(true, false, true));
        Assert.Equal(GrenadeThrowStep.Throw, k.Tick(false, false, true));
        Assert.False(k.Aiming);
        Assert.Equal(GrenadeThrowStep.None, k.Tick(false, false, true));
    }

    [Fact]
    public void The_aim_key_at_the_release_puts_the_grenade_away()
    {
        var k = new GrenadeThrower();
        k.Tick(true, false, true);
        Assert.Equal(GrenadeThrowStep.Cancel, k.Tick(false, true, true));
        Assert.False(k.Aiming);
    }

    [Fact]
    public void Losing_the_right_to_aim_cancels_and_a_key_held_into_it_does_not_start_an_aim()
    {
        var k = new GrenadeThrower();
        k.Tick(true, false, true);
        Assert.Equal(GrenadeThrowStep.Cancel, k.Tick(true, false, false)); // got into a vehicle with G held
        Assert.Equal(GrenadeThrowStep.None, k.Tick(true, false, true));    // still held: no fresh press, no aim
        Assert.False(k.Aiming);
        k.Tick(false, false, true);
        k.Tick(true, false, true);
        Assert.True(k.Aiming);
    }

    [Fact]
    public void A_press_where_g_belongs_to_something_else_does_not_aim()
    {
        var k = new GrenadeThrower();
        Assert.Equal(GrenadeThrowStep.None, k.Tick(true, false, GrenadeThrower.CanAim(true, true, false, true, scudScreen: true)));
        Assert.False(k.Aiming);
        Assert.False(GrenadeThrower.CanAim(onFoot: false, true, false, true, false)); // seated (a Scud seat opens its screen with G)
        Assert.False(GrenadeThrower.CanAim(true, true, false, hasRound: false, false));
        Assert.False(GrenadeThrower.CanAim(true, handsFree: false, false, true, false));
        Assert.False(GrenadeThrower.CanAim(true, true, onLadder: true, true, false));
        Assert.True(GrenadeThrower.CanAim(true, true, false, true, false));
    }

    [Fact]
    public void The_aim_arc_is_the_flight_the_server_flies_and_reaches_further_than_it_used_to()
    {
        var g = Grenade.Grenade!;
        Assert.True(Grenade.ProjectileSpeed > 17f);
        var from = new Vector3(0, 1.6f, 0);
        var dir = Vector3.Normalize(new Vector3(1, 0.3f, 0));
        var vel = dir * Grenade.ProjectileSpeed;
        var arc = GrenadeArc.Fly(g, from, vel, Floor);
        var pos = from; var v = vel;
        for (var k = 0; k < arc.Points.Count - 1; k++)
        {
            var s = GrenadeFlight.Step(g, pos, v, Dt, Floor);
            pos = s.Pos; v = s.Vel;
            Assert.Equal(arc.Points[k + 1], pos);
        }
        Assert.NotEmpty(arc.Bounces);
        Assert.True(arc.Bounces[0].X > 17f); // the first landing is further than the old 17 m/s throw could reach on the level
    }

    [Fact]
    public void Falls_under_gravity_with_no_floor()
    {
        var g = Grenade.Grenade!;
        var s = GrenadeFlight.Step(g, new Vector3(0, 100, 0), new Vector3(10, 0, 0), Dt, (_, _, _) => new GrenadeStop(float.PositiveInfinity, Vector3.UnitY));
        Assert.Equal(10f, s.Vel.X, 3);
        Assert.Equal(-g.Gravity * Dt, s.Vel.Y, 3);
        Assert.Equal(10f * Dt, s.Pos.X, 3);
        Assert.False(s.Bounced);
    }

    [Fact]
    public void Thrown_forward_it_bounces_then_comes_to_rest_and_the_fuse_runs_out_at_its_time()
    {
        var g = Grenade.Grenade!;
        var pos = new Vector3(0, 1.6f, 0);
        var vel = new Vector3(Grenade.ProjectileSpeed, 0, 0);
        var fuse = new Countdown(); fuse.Start(g.FuseSeconds);
        var bounces = 0; var ticks = 0; var rested = false;
        while (true)
        {
            ticks++;
            var s = GrenadeFlight.Step(g, pos, vel, Dt, Floor);
            pos = s.Pos; vel = s.Vel;
            if (s.Bounced) bounces++;
            if (s.AtRest) rested = true;
            Assert.True(pos.Y >= -0.001f, "never through the floor");
            if (fuse.Tick(Dt)) break;
        }
        Assert.True(bounces >= 2);
        Assert.True(rested);
        Assert.InRange(ticks * Dt, g.FuseSeconds, g.FuseSeconds + Dt + 1e-3f);
        Assert.True(pos.X > 5f && pos.X < 40f); // it went somewhere, and rolled to a stop
    }

    [Fact]
    public void A_wall_sends_it_back()
    {
        var g = Grenade.Grenade!;
        GrenadeStop Wall(Vector3 from, Vector3 dir, float len) => dir.X > 0 && from.X < 5f && 5f - from.X <= len ? new GrenadeStop(5f - from.X, -Vector3.UnitX) : new GrenadeStop(float.PositiveInfinity, Vector3.Zero);
        var pos = new Vector3(4.8f, 50, 0); var vel = new Vector3(17, 0, 0);
        var s = GrenadeFlight.Step(g, pos, vel, Dt, Wall);
        Assert.True(s.Bounced);
        Assert.True(s.Vel.X < 0);
        Assert.True(MathF.Abs(s.Vel.X) < 17 * g.Bounce + 1e-3f);
        Assert.True(s.Pos.X < 5f);
    }

    [Fact]
    public void Standing_on_or_within_a_metre_of_the_grenade_kills_the_thrower_too()
    {
        var w = Grenade;
        foreach (var d in new[] { 0f, 0.5f, 1f })
        {
            Assert.True(ProjectileMath.SplashDamage(w, d, false) >= MoveConsts.MaxHealth, $"others at {d} m");
            Assert.True(ProjectileMath.SplashDamage(w, d, true) >= MoveConsts.MaxHealth, $"the thrower at {d} m");
        }
        Assert.Equal(0, ProjectileMath.SplashDamage(w, w.Splash!.Radius + 0.1f, false));
        Assert.True(ProjectileMath.SplashDamage(w, 3f, false) < MoveConsts.MaxHealth); // the kill zone is tight: further out it wounds
    }

    [Fact]
    public void A_grenade_comes_to_rest_a_body_radius_above_the_floor()
    {
        var g = Grenade.Grenade!;
        var s = GrenadeFlight.Step(g, new Vector3(0, 0.2f, 0), new Vector3(0, -9f, 0), Dt, Floor);
        Assert.True(s.Bounced);
        Assert.True(s.Pos.Y >= GrenadeFlight.Skin - 1e-4f, $"stopped at {s.Pos.Y}");
    }
}
