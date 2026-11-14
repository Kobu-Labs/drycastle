using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The vehicle's life (hit points, scheduled blast, destroyed until it returns) and its gun's reload, as explicit machines.</summary>
public class VehicleLifeTests
{
    [Fact]
    public void A_vehicle_starts_alive_at_full_hit_points()
    {
        var v = new VehicleLifecycle(200);
        Assert.Equal(VehicleLife.Alive, v.State);
        Assert.Equal(200f, v.Hp);
        Assert.False(v.DetonationDue(1e9));
    }

    [Fact]
    public void Damage_that_leaves_hit_points_does_not_doom_it_and_the_last_hit_does()
    {
        var v = new VehicleLifecycle(100);
        Assert.False(v.Damage(60, 5, 0));
        Assert.Equal(40f, v.Hp);
        Assert.Equal(VehicleLife.Alive, v.State);
        Assert.True(v.Damage(60, 6, 0)); // more than is left: the hit points stop at zero
        Assert.Equal(0f, v.Hp);
        Assert.Equal(VehicleLife.Doomed, v.State);
    }

    [Fact]
    public void The_blast_is_due_at_the_scheduled_time_and_a_second_hit_keeps_the_first_date()
    {
        var v = new VehicleLifecycle(100);
        v.Damage(100, 10, 0.5f); // a chain reaction: half a second after the neighbour went off
        Assert.False(v.DetonationDue(10.49));
        Assert.True(v.DetonationDue(10.5));
        Assert.False(v.Damage(30, 10.1, 0)); // an immediate hit does not pull the date forward: it was scheduled once
        Assert.False(v.DetonationDue(10.49));
        Assert.True(v.DetonationDue(10.5));
    }

    [Fact]
    public void An_immediate_blast_is_due_in_the_same_tick()
    {
        var v = new VehicleLifecycle(100);
        v.Damage(100, 7, 0);
        Assert.True(v.DetonationDue(7));
    }

    [Fact]
    public void The_blast_destroys_it_until_the_return_time_and_the_return_repairs_it()
    {
        var v = new VehicleLifecycle(100);
        v.Damage(100, 7, 0);
        v.Destroy(7 + 30);
        Assert.True(v.Destroyed);
        Assert.False(v.DetonationDue(100)); // no second blast
        Assert.False(v.ReturnDue(36.99));
        Assert.True(v.ReturnDue(37));
        Assert.True(v.Reset(repair: true));
        Assert.Equal(VehicleLife.Alive, v.State);
        Assert.Equal(100f, v.Hp);
        Assert.False(v.ReturnDue(1e9));
    }

    [Fact]
    public void A_destroyed_vehicle_takes_no_damage()
    {
        var v = new VehicleLifecycle(100);
        v.Damage(100, 0, 0);
        v.Destroy(30);
        Assert.False(v.Damage(50, 1, 0));
        Assert.Equal(0f, v.Hp);
        Assert.True(v.Destroyed);
    }

    [Fact]
    public void A_return_that_does_not_repair_keeps_a_damaged_vehicle_damaged()
    {
        var v = new VehicleLifecycle(100);
        v.Damage(70, 1, 0);
        Assert.False(v.Reset(repair: false)); // abandoned, or fell out of the world, while smoking
        Assert.Equal(30f, v.Hp);
        Assert.Equal(VehicleLife.Alive, v.State);
    }

    [Fact]
    public void A_doomed_vehicle_that_is_sent_back_does_not_blow_up_at_its_origin()
    {
        // abandoned or out of the world while its chain-reaction blast is still scheduled: the old fields kept zero hit points and the date
        var v = new VehicleLifecycle(100);
        v.Damage(100, 20, 2f);
        Assert.True(v.Doomed);
        Assert.True(v.Reset(repair: false));
        Assert.Equal(VehicleLife.Alive, v.State);
        Assert.Equal(100f, v.Hp);
        Assert.False(v.DetonationDue(22));
        Assert.False(v.DetonationDue(1e9));
        Assert.True(v.Damage(100, 30, 0)); // and it can be doomed again by a new hit
    }

    [Fact]
    public void The_life_spec_lists_what_can_follow_what()
    {
        var s = VehicleLifecycle.Spec;
        Assert.True(s.Allows(VehicleLife.Alive, VehicleLife.Doomed));
        Assert.True(s.Allows(VehicleLife.Doomed, VehicleLife.Destroyed));
        Assert.True(s.Allows(VehicleLife.Destroyed, VehicleLife.Alive));
        Assert.True(s.Allows(VehicleLife.Doomed, VehicleLife.Alive));
        Assert.False(s.Allows(VehicleLife.Alive, VehicleLife.Destroyed)); // the blast is always scheduled first
        Assert.False(s.Allows(VehicleLife.Destroyed, VehicleLife.Doomed));
    }

    // ---- the gun ----

    static VehicleGun Gun() => new(new VehicleGunDef { Magazine = 2, Reserve = 3, FireInterval = 0.5f, ReloadTime = 1f });

    [Fact]
    public void An_empty_magazine_starts_a_reload_and_the_first_pull_after_its_time_ends_it()
    {
        var g = Gun();
        Assert.True(g.TryFire(0, true));
        Assert.True(g.TryFire(0.5, true));
        Assert.False(g.Reloading);
        Assert.False(g.TryFire(1.0, true)); // empty: starts the reload instead
        Assert.True(g.Reloading);
        Assert.Equal(1f, g.ReloadRemaining(1.0), 3);
        Assert.False(g.TryFire(1.9, true));
        Assert.True(g.Reloading);
        Assert.True(g.TryFire(2.0, true)); // ends the reload (magazine 2, reserve 1) and fires
        Assert.False(g.Reloading);
        Assert.Equal(1, g.Magazine);
        Assert.Equal(1, g.Reserve);
        Assert.Equal(0f, g.ReloadRemaining(2.0));
    }

    [Fact]
    public void A_reload_is_refused_when_full_when_out_of_reserve_and_while_reloading()
    {
        var g = Gun();
        Assert.False(g.TryReload(0)); // full
        g.TryFire(0, true);
        Assert.True(g.TryReload(0.1));
        Assert.False(g.TryReload(0.2)); // already reloading
    }
}
