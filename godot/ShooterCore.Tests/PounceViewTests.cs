using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The client's pounce view machine. The first group pins what the old nullable-stage + booleans did; the last is the hazard that is closed.</summary>
public class PounceViewTests
{
    const ushort Me = 7, Other = 9;

    static DemonPounceMsg Wind(ushort v) => new(v, PounceStage.Wind);
    static DemonPounceMsg Pin(ushort v, float yaw = 1.5f) => new(v, PounceStage.Pinned, yaw);
    static DemonPounceMsg Released(ushort v) => new(v, PounceStage.Released);
    static DemonPounceMsg Aborted(ushort v) => new(v, PounceStage.Aborted);

    [Fact]
    public void Starts_with_nobody_marked()
    {
        var v = new PounceView();
        Assert.Equal(PounceViewStage.None, v.Stage);
        Assert.False(v.Marked);
        Assert.False(v.PinnedMe(Me));
    }

    [Fact]
    public void Full_story_of_my_pounce_wind_pin_release_rise()
    {
        var v = new PounceView();
        Assert.Equal(PounceViewEffect.None, v.OnMessage(Wind(Me), Me));
        Assert.True(v.MarkedMe(Me));
        Assert.False(v.PinnedMe(Me));
        Assert.Equal(PounceViewEffect.PinnedMine, v.OnMessage(Pin(Me, 2f), Me));
        Assert.True(v.PinnedMe(Me));
        Assert.Equal(2f, v.Yaw);
        Assert.Equal(PounceViewEffect.RiseAfterRelease, v.OnMessage(Released(Me), Me));
        Assert.True(v.Rising);
        Assert.False(v.Marked);
        v.RiseDone();
        Assert.Equal(PounceViewStage.None, v.Stage);
    }

    [Fact]
    public void Abort_after_a_pin_rises_without_the_fade()
    {
        var v = new PounceView();
        v.OnMessage(Wind(Me), Me); v.OnMessage(Pin(Me), Me);
        Assert.Equal(PounceViewEffect.RiseAfterAbort, v.OnMessage(Aborted(Me), Me));
        Assert.True(v.Rising);
    }

    [Fact]
    public void Abort_from_the_wind_just_ends_it_no_rise()
    {
        var v = new PounceView();
        v.OnMessage(Wind(Me), Me);
        Assert.Equal(PounceViewEffect.None, v.OnMessage(Aborted(Me), Me));
        Assert.Equal(PounceViewStage.None, v.Stage);
    }

    [Fact]
    public void Someone_elses_pounce_has_no_local_effects_and_no_rise()
    {
        var v = new PounceView();
        Assert.Equal(PounceViewEffect.None, v.OnMessage(Wind(Other), Me));
        Assert.Equal(PounceViewEffect.None, v.OnMessage(Pin(Other), Me));
        Assert.True(v.Pinned);
        Assert.False(v.PinnedMe(Me));
        Assert.Equal(Other, v.LieVictim);
        Assert.Equal(PounceViewEffect.None, v.OnMessage(Released(Other), Me));
        Assert.Equal(PounceViewStage.None, v.Stage);
    }

    [Fact]
    public void A_joiner_can_start_pinned_or_in_the_wind()
    {
        var a = new PounceView();
        Assert.Equal(PounceViewEffect.PinnedMine, a.OnMessage(Pin(Me), Me));
        var b = new PounceView();
        b.OnMessage(Wind(Other), Me);
        Assert.Equal(PounceViewStage.Wind, b.Stage);
    }

    [Fact]
    public void The_end_of_someone_else_is_ignored()
    {
        var v = new PounceView();
        v.OnMessage(Wind(Me), Me); v.OnMessage(Pin(Me), Me);
        Assert.Equal(PounceViewEffect.None, v.OnMessage(Released(Other), Me));
        Assert.True(v.PinnedMe(Me));
        var idle = new PounceView();
        Assert.Equal(PounceViewEffect.None, idle.OnMessage(Released(Me), Me));
        Assert.Equal(PounceViewStage.None, idle.Stage);
    }

    [Fact]
    public void Reset_cuts_everything_including_the_rise()
    {
        var v = new PounceView();
        v.OnMessage(Pin(Me), Me);
        v.OnMessage(Released(Me), Me);
        Assert.True(v.Rising);
        v.Reset();
        Assert.Equal(PounceViewStage.None, v.Stage);
        v.OnMessage(Pin(Me), Me);
        v.Reset();
        Assert.False(v.Pinned);
    }

    [Fact]
    public void A_new_pin_while_rising_takes_over_from_the_rise()
    {
        var v = new PounceView();
        v.OnMessage(Pin(Me), Me); v.OnMessage(Released(Me), Me);
        Assert.Equal(PounceViewEffect.PinnedMine, v.OnMessage(Pin(Me), Me));
        Assert.False(v.Rising);
        Assert.True(v.PinnedMe(Me));
    }

    [Fact]
    public void RiseDone_outside_a_rise_does_nothing()
    {
        var v = new PounceView();
        v.OnMessage(Pin(Me), Me);
        v.RiseDone();
        Assert.True(v.Pinned);
    }

    // ---- hazard: a Wind for another victim used to overwrite my pin without ending its view ----

    [Fact]
    public void A_wind_for_another_victim_while_I_am_pinned_ends_my_pin_explicitly()
    {
        var v = new PounceView();
        v.OnMessage(Pin(Me), Me);
        Assert.Equal(PounceViewEffect.PinSuperseded, v.OnMessage(Wind(Other), Me));
        Assert.Equal(PounceViewStage.Wind, v.Stage);
        Assert.False(v.PinnedMe(Me));
        Assert.Equal(Other, v.Victim);
    }

    [Fact]
    public void A_pin_of_someone_else_while_I_am_pinned_also_ends_mine()
    {
        var v = new PounceView();
        v.OnMessage(Pin(Me), Me);
        Assert.Equal(PounceViewEffect.PinSuperseded, v.OnMessage(Pin(Other), Me));
        Assert.Equal(Other, v.LieVictim);
    }

    [Fact]
    public void A_wind_during_the_rise_ends_the_rise()
    {
        var v = new PounceView();
        v.OnMessage(Pin(Me), Me); v.OnMessage(Released(Me), Me);
        v.OnMessage(Wind(Me), Me);
        Assert.False(v.Rising);
        Assert.True(v.MarkedMe(Me));
    }
}
