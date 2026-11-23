using Xunit;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Tests.Fsm;

public class TimingTests
{
    [Fact]
    public void Countdown_fires_once_when_it_runs_out_and_is_ready_after()
    {
        var c = new Countdown();
        Assert.True(c.Ready);
        Assert.False(c.Tick(1f));
        c.Start(1f);
        Assert.True(c.Running);
        Assert.False(c.Tick(0.4f));
        Assert.True(c.Tick(0.6f));
        Assert.False(c.Tick(0.1f));
        Assert.True(c.Ready);
        Assert.Equal(0f, c.Left);
    }

    [Fact]
    public void Countdown_start_if_ready_does_not_extend_a_running_one()
    {
        var c = new Countdown();
        Assert.True(c.StartIfReady(2f));
        c.Tick(0.5f);
        Assert.False(c.StartIfReady(2f));
        Assert.Equal(1.5f, c.Left, 5);
    }

    [Fact]
    public void Countdown_start_with_zero_or_less_is_idle()
    {
        var c = new Countdown();
        c.Start(0f);
        Assert.True(c.Ready);
        c.Start(-1f);
        Assert.True(c.Ready);
    }

    [Fact]
    public void Change_reports_the_first_value_and_then_only_differences()
    {
        var ch = new Change<byte>();
        Assert.True(ch.Update(0, out var before));
        Assert.Equal(0, before);
        Assert.False(ch.Update(0));
        Assert.True(ch.Update(3, out before));
        Assert.Equal(0, before);
        ch.Reset();
        Assert.True(ch.Update(3));
    }

    [Fact]
    public void Edge_pressed_and_released_and_the_must_release_first_latch()
    {
        var e = new Edge();
        Assert.False(e.Pressed(false));
        Assert.True(e.Pressed(true));
        Assert.False(e.Pressed(true));
        Assert.True(e.Released(false));
        Assert.False(e.Released(false));

        var latched = new Edge(startHeld: true);
        Assert.False(latched.Pressed(true));
        latched.Pressed(false);
        Assert.True(latched.Pressed(true));
    }

    [Fact]
    public void Ramp_moves_by_at_most_the_step_and_lands_on_the_target()
    {
        Assert.Equal(0.25f, Ramp.MoveToward(0f, 1f, 0.25f));
        Assert.Equal(0.75f, Ramp.MoveToward(1f, 0f, 0.25f));
        Assert.Equal(1f, Ramp.MoveToward(0.9f, 1f, 0.25f));
        Assert.Equal(1f, Ramp.MoveToward(1f, 1f, 0.25f));
    }

    [Fact]
    public void PendingAnswer_dims_when_slow_and_gives_up_once()
    {
        var p = new PendingAnswer();
        Assert.False(p.Dimmed(5, 0.12));
        p.Begin(10);
        Assert.False(p.Dimmed(10.05, 0.12));
        Assert.True(p.Dimmed(10.2, 0.12));
        Assert.False(p.GiveUp(10.3, 0.6));
        Assert.True(p.GiveUp(10.7, 0.6));
        Assert.False(p.GiveUp(10.8, 0.6));
        Assert.False(p.Active);
    }
}
