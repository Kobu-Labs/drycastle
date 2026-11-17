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
    public void A_throw_uses_a_round_and_the_next_waits_for_the_reload()
    {
        var weapons = new PlayerWeapons(File, hotbar: LoadoutHotbar.Build(File, null));
        var slot = File.Weapons.IndexOf(Grenade) + 1;
        weapons.Update(1.0, new WeaponInput(false, false, slot, false));
        Assert.Equal(slot, weapons.Slot);
        var shot = weapons.Update(1.0 + Dt, new WeaponInput(true, false, 0, false));
        Assert.NotNull(shot);
        Assert.Equal(0, weapons.Mag(slot));
        Assert.Equal(Grenade.ReserveAmmo, weapons.Reserve(slot));
        Assert.Null(weapons.Update(1.0 + 2 * Dt, new WeaponInput(false, false, 0, false)));
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
    public void Splash_follows_the_weapons_numbers_and_halves_on_the_thrower()
    {
        var w = Grenade;
        Assert.Equal(w.Damage, ProjectileMath.SplashDamage(w, 0.5f, false));
        Assert.Equal(MathF.Round(w.Damage * w.Splash!.SelfDamageMultiplier), ProjectileMath.SplashDamage(w, 0.5f, true));
        Assert.Equal(0, ProjectileMath.SplashDamage(w, w.Splash.Radius + 0.1f, false));
    }
}
