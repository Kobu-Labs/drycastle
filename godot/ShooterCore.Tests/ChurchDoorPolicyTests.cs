using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The church doors' single decider. The first group pins what the five scattered deciders did (Mass phase reaction, demon clear / kill / door keeper, the
/// admin, the close-when-empty latch); the last group is the dead end that is fixed: a void Mass whose demon never came left the doors shut for good.
/// </summary>
public class ChurchDoorPolicyTests
{
    const float Grace = 3f;

    [Fact]
    public void Closed_by_default()
    {
        var p = new ChurchDoorPolicy();
        Assert.False(p.IsOpen);
        Assert.False(p.CloseWhenEmpty);
    }

    [Fact]
    public void Gathering_opens_and_cancels_a_pending_close()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassOver);
        Assert.True(p.CloseWhenEmpty);
        Assert.True(p.Apply(DoorReason.MassGathering));
        Assert.True(p.IsOpen);
        Assert.False(p.CloseWhenEmpty);
        Assert.False(p.Apply(DoorReason.MassGathering)); // already open: no change to apply
    }

    [Fact]
    public void Void_slams_the_doors_shut_and_leaves_the_latch_alone()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        Assert.True(p.Apply(DoorReason.MassVoid));
        Assert.False(p.IsOpen);
        Assert.False(p.CloseWhenEmpty);
    }

    [Fact]
    public void Mass_over_only_arms_the_deferred_close_it_does_not_touch_the_doors()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        Assert.False(p.Apply(DoorReason.MassOver));
        Assert.True(p.IsOpen);
        Assert.True(p.CloseWhenEmpty);
    }

    [Fact]
    public void Deferred_close_waits_for_an_empty_church_then_fires_once()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        p.Apply(DoorReason.MassOver);
        Assert.False(p.TickDeferredClose(() => 2));
        Assert.True(p.IsOpen);
        Assert.True(p.TickDeferredClose(() => 0));
        Assert.False(p.IsOpen);
        Assert.False(p.CloseWhenEmpty);
        Assert.False(p.TickDeferredClose(() => 0));
    }

    [Fact]
    public void Deferred_close_does_not_count_heads_unless_one_is_pending()
    {
        var p = new ChurchDoorPolicy();
        var asked = 0;
        p.TickDeferredClose(() => { asked++; return 0; });
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Deferred_close_clears_the_latch_even_when_the_doors_were_already_shut()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassOver);
        Assert.False(p.TickDeferredClose(() => 0)); // nothing to move...
        Assert.False(p.CloseWhenEmpty);              // ...but it is spent: a later open stays open
    }

    [Fact]
    public void Demon_starting_cancels_the_deferred_close()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassOver);
        p.Apply(DoorReason.DemonStarting);
        Assert.False(p.CloseWhenEmpty);
    }

    [Fact]
    public void Demon_killed_opens_and_arms_the_close_so_the_church_shuts_once_everyone_left()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassVoid);
        Assert.True(p.Apply(DoorReason.DemonKilled));
        Assert.True(p.IsOpen);
        Assert.True(p.CloseWhenEmpty);
        Assert.False(p.TickDeferredClose(() => 1));
        Assert.True(p.TickDeferredClose(() => 0));
        Assert.False(p.IsOpen);
    }

    [Fact]
    public void Demon_cleared_opens_without_arming_the_close()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassVoid);
        Assert.True(p.Apply(DoorReason.DemonCleared));
        Assert.True(p.IsOpen);
        Assert.False(p.CloseWhenEmpty);
    }

    [Fact]
    public void Admin_sets_the_doors_once_and_a_pending_close_still_fires()
    {
        var p = new ChurchDoorPolicy();
        Assert.True(p.SetByAdmin(true));
        Assert.False(p.SetByAdmin(true));
        p.Apply(DoorReason.MassOver);
        p.SetByAdmin(true);
        Assert.True(p.CloseWhenEmpty);
        Assert.True(p.TickDeferredClose(() => 0));
        Assert.False(p.IsOpen);
        Assert.True(p.SetByAdmin(true));
        Assert.True(p.SetByAdmin(false));
    }

    // ---- the demon fight's door keeper ----

    [Fact]
    public void Fight_shuts_the_doors_after_the_grace_while_someone_is_inside()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        p.FightBegins();
        var dt = 1f / 30f;
        var ticks = 0;
        // the hand-written original: _dDoorT += dt; if (_dDoorT >= grace) close
        var t = 0f; var expected = 0;
        while (true) { expected++; t += dt; if (t >= Grace) break; }
        while (p.IsOpen && ticks < 1000) { ticks++; p.TickFight(dt, 1, Grace); }
        Assert.False(p.IsOpen);
        Assert.Equal(expected, ticks);
    }

    [Fact]
    public void Fight_does_not_count_grace_while_the_doors_are_shut_and_resets_it()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        p.FightBegins();
        p.TickFight(2f, 1, Grace);          // 2 s of grace used
        Assert.True(p.SetByAdmin(false));
        p.TickFight(1f, 1, Grace);          // shut: the clock resets, nothing happens
        p.SetByAdmin(true);
        Assert.False(p.TickFight(2f, 1, Grace)); // a fresh 2 s, not 3
        Assert.True(p.TickFight(1f, 1, Grace));
        Assert.False(p.IsOpen);
    }

    [Fact]
    public void Fight_opens_the_doors_when_nobody_is_inside_and_resets_the_grace()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassVoid);
        p.FightBegins();
        Assert.True(p.TickFight(0.05f, 0, Grace));
        Assert.True(p.IsOpen);
        Assert.False(p.TickFight(0.05f, 0, Grace));
        p.TickFight(2.9f, 1, Grace);
        p.TickFight(0.5f, 0, Grace);        // the church empties: the clock is gone
        Assert.False(p.TickFight(2.9f, 1, Grace));
        Assert.True(p.IsOpen);
    }

    [Fact]
    public void Fight_begins_resets_the_grace_clock()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        p.TickFight(2.5f, 1, Grace);
        p.FightBegins();
        Assert.False(p.TickFight(0.9f, 1, Grace));
        Assert.True(p.IsOpen);
    }

    // ---- regression: Void without a demon was a dead end ----

    [Fact]
    public void A_demon_that_cannot_start_leaves_the_doors_open()
    {
        // Mass phase Void shut the doors; DemonBegin used to return silently (no data, no anchors) and nothing opened them again.
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassGathering);
        p.Apply(DoorReason.MassVoid);
        Assert.False(p.IsOpen);
        Assert.True(p.Apply(DoorReason.DemonFailed));
        Assert.True(p.IsOpen);
    }

    [Fact]
    public void The_doors_open_when_the_event_ends_during_the_demons_delay()
    {
        // DemonClear used to skip the doors when the stage was Delay (also a failed spawn): the doors Void shut stayed shut for good.
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassVoid);
        Assert.True(p.Apply(DoorReason.DemonCleared));
        Assert.True(p.Apply(DoorReason.MassOver) == false);
        Assert.True(p.IsOpen);                       // open until the church is empty,
        Assert.True(p.TickDeferredClose(() => 0));   // then shut as after any event
        Assert.False(p.IsOpen);
    }

    [Fact]
    public void Failure_after_the_fight_began_also_ends_open_and_concludable()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassVoid);
        p.FightBegins();
        p.Apply(DoorReason.DemonStarting);
        p.TickFight(1f, 1, Grace);   // shut already: stays shut
        Assert.False(p.IsOpen);
        Assert.True(p.Apply(DoorReason.DemonFailed));
        Assert.True(p.IsOpen);
    }

    [Fact]
    public void Mass_over_while_already_shut_arms_the_close_then_the_empty_check_fires_it_without_moving_the_doors()
    {
        var p = new ChurchDoorPolicy();
        Assert.False(p.Apply(DoorReason.MassOver));
        Assert.False(p.IsOpen);
        Assert.True(p.CloseWhenEmpty);                     // ShutUntilEmpty
        Assert.False(p.TickDeferredClose(() => 1));
        Assert.False(p.TickDeferredClose(() => 0));        // fires (latch cleared) but the doors were shut: no change
        Assert.False(p.CloseWhenEmpty);
        Assert.False(p.TickDeferredClose(() => 0));
    }

    [Fact]
    public void An_admin_toggle_keeps_the_pending_close()
    {
        var p = new ChurchDoorPolicy();
        p.Apply(DoorReason.MassOver);                      // ShutUntilEmpty
        Assert.True(p.SetByAdmin(true));
        Assert.True(p.CloseWhenEmpty);
        Assert.True(p.SetByAdmin(false));
        Assert.True(p.CloseWhenEmpty);
    }

    [Fact]
    public void Every_reason_from_every_state_is_a_declared_transition()
    {
        // Strict: an undeclared edge throws. Reach each of the four states by a path, then apply every reason.
        var reach = new (DoorState State, DoorReason[] Path)[]
        {
            (DoorState.Shut, []),
            (DoorState.Open, [DoorReason.MassGathering]),
            (DoorState.OpenUntilEmpty, [DoorReason.MassGathering, DoorReason.MassOver]),
            (DoorState.ShutUntilEmpty, [DoorReason.MassOver]),
        };
        foreach (var (state, path) in reach)
            foreach (var reason in Enum.GetValues<DoorReason>())
            {
                var p = new ChurchDoorPolicy();
                foreach (var r in path) p.Apply(r);
                Assert.Equal(state is DoorState.Open or DoorState.OpenUntilEmpty, p.IsOpen);
                p.Apply(reason);
                p.SetByAdmin(true); p.SetByAdmin(false);
                p.TickFight(1f, 0, Grace); p.TickFight(1f, 1, Grace);
            }
    }
}
