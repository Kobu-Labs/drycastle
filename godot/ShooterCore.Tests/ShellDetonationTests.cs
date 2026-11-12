using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The 105 mm shell bursts when its tracer arrives (#162), not at the fire. The timing is pure (<see cref="ShellFlight"/>,
/// <see cref="TimedQueue{T}"/>); the server ticks the queue and runs the detonation. These tests run the same arithmetic on the shipped
/// tracer speed, and the detonation point is the fire-time point.
/// </summary>
public class ShellDetonationTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static GunshipWeaponDef Shell => Shipped.Killstreaks.Ac130!.Weapons.Single(w => w.Kind == GunshipWeaponKind.Shell);
    const double Tick = 1.0 / 30;

    [Fact]
    public void The_shell_detonates_at_its_tracers_arrival_within_one_tick()
    {
        var from = new Vector3(0, 120, 0);
        var to = new Vector3(2000, 0, 0);
        var expected = Vector3.Distance(from, to) / Shell.TracerSpeedMps!.Value;
        var timing = ShellFlight.Plan(from, to, Shell.TracerSpeedMps!.Value, now: 10.0, tickS: Tick);
        Assert.True(timing.Deferred);
        Assert.Equal(10.0 + expected, timing.DueAt, 1e-6);

        // the server ticks the queue: the shell is taken on the first tick at or after its due time
        var queue = new TimedQueue<string>();
        queue.Add(timing.DueAt, "shell");
        var taken = -1.0;
        for (var t = 10.0; t < 20.0 && taken < 0; t += Tick)
            if (queue.TakeDue(t).Count > 0) taken = t;
        Assert.True(taken >= timing.DueAt, "not before its arrival");
        Assert.True(taken - timing.DueAt <= Tick + 1e-9, "within one tick of it");
    }

    [Fact]
    public void Nothing_happens_before_the_arrival_so_the_damage_waits()
    {
        var queue = new TimedQueue<string>();
        queue.Add(12.0, "shell");
        Assert.Empty(queue.TakeDue(11.9));
        Assert.Equal(1, queue.Count);
        Assert.Equal(new[] { "shell" }, queue.TakeDue(12.0));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void A_shell_at_the_maximum_range_arrives_in_about_two_seconds()
    {
        var from = new Vector3(0, 120, 0);
        var to = from + Vector3.Normalize(new Vector3(1, -0.1f, 0)) * Shell.RangeM;
        var timing = ShellFlight.Plan(from, to, Shell.TracerSpeedMps!.Value, 0, Tick);
        Assert.InRange(timing.DueAt, 1.9, 2.1);
    }

    [Fact]
    public void A_short_flight_is_resolved_at_once()
    {
        var timing = ShellFlight.Plan(Vector3.Zero, new Vector3(5, 0, 0), Shell.TracerSpeedMps!.Value, 3.0, Tick);
        Assert.False(timing.Deferred);
        Assert.Equal(3.0, timing.DueAt);
    }

    [Fact]
    public void The_point_is_fixed_at_the_fire_so_a_shell_does_not_home_on_a_moving_target()
    {
        // the server queues the fire-time point and route; a target that moves away is not followed
        var fireAt = new Vector3(600, 0, 0);
        var queued = new { Point = fireAt, Due = ShellFlight.Plan(Vector3.Zero, fireAt, Shell.TracerSpeedMps!.Value, 0, Tick).DueAt };
        var targetLater = new Vector3(600, 0, 300);                 // the target has moved 300 m sideways
        Assert.Equal(new Vector3(600, 0, 0), queued.Point);         // the burst stays at the fire-time point
        Assert.NotEqual(targetLater, queued.Point);
        Assert.Equal(600f / Shell.TracerSpeedMps!.Value, queued.Due, 1e-5);
    }

    [Fact]
    public void A_round_reset_clears_the_shells_in_the_air()
    {
        var queue = new TimedQueue<string>();
        queue.Add(5.0, "a"); queue.Add(6.0, "b");
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.TakeDue(100));
    }

    [Fact]
    public void Due_items_come_out_in_due_order_and_ties_in_add_order()
    {
        var queue = new TimedQueue<string>();
        queue.Add(3.0, "c"); queue.Add(1.0, "a"); queue.Add(1.0, "b");
        Assert.Equal(new[] { "a", "b", "c" }, queue.TakeDue(3.0));
    }

    [Fact]
    public void Only_the_105_mm_shell_is_a_delayed_round_the_cannons_stay_instant()
    {
        var cannons = Shipped.Killstreaks.Gunship!.Weapons.Concat(Shipped.Killstreaks.Ac130!.Weapons).Where(w => w.Kind == GunshipWeaponKind.Cannon);
        Assert.All(cannons, c => Assert.NotEqual(GunshipWeaponKind.Shell, c.Kind));
        Assert.Equal("howitzer", Shell.Id);
    }
}
