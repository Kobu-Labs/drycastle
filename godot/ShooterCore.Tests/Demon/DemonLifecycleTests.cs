using Xunit;
using Drycastle.ShooterCore.Demon;

namespace Drycastle.ShooterCore.Tests.Demon;

/// <summary>The demon's server-side stage machine: durations from the fight data, the same float countdown the hand-written <c>_dTime -= dt</c> had, and every failure path ending in None.</summary>
public class DemonLifecycleTests
{
    static DemonFightDef Fight => new() { TransformDelayS = 1f, TransformS = 7f, ClimbS = 1.5f, CorpseS = 8f };

    static int TicksUntilDue(DemonLifecycle l, float dt)
    {
        var n = 0;
        while (n < 100000) { n++; if (l.Tick(dt)) return n; }
        throw new Xunit.Sdk.XunitException("never due");
    }

    [Fact]
    public void Starts_with_no_demon_and_nothing_ticks()
    {
        var l = new DemonLifecycle(Fight);
        Assert.Equal(DemonStage.None, l.Stage);
        Assert.False(l.Active);
        Assert.False(l.Tick(100f));
    }

    [Fact]
    public void Runs_the_whole_script_with_the_configured_durations()
    {
        var l = new DemonLifecycle(Fight);
        Assert.True(l.Begin());
        Assert.Equal(DemonStage.Delay, l.Stage);
        Assert.Equal(1f, l.Left);
        Assert.False(l.Tick(0.5f));
        Assert.True(l.Tick(0.5f));

        l.BeginTransform();
        Assert.Equal(DemonStage.Transforming, l.Stage);
        Assert.Equal(7f, l.Left);
        Assert.True(l.Tick(7f));

        l.BeginClimb();
        Assert.Equal(DemonStage.Climbing, l.Stage);
        Assert.Equal(1.5f, l.Left);
        Assert.Equal(0f, l.ClimbProgress);
        l.Tick(0.75f);
        Assert.Equal(0.5f, l.ClimbProgress, 5);
        Assert.True(l.Tick(0.75f));
        Assert.Equal(1f, l.ClimbProgress);

        l.BeginFight();
        Assert.Equal(DemonStage.Fighting, l.Stage);
        Assert.False(l.Tick(1000f)); // the fight has no clock

        l.Die();
        Assert.Equal(DemonStage.Dying, l.Stage);
        Assert.Equal(8f, l.Left);
        Assert.True(l.Tick(8f));

        Assert.Equal(DemonStage.Dying, l.Clear());
        Assert.False(l.Active);
    }

    [Fact]
    public void Timing_matches_the_hand_written_countdown_tick_for_tick()
    {
        foreach (var (dur, dt) in new[] { (1f, 1f / 30f), (7f, 1f / 30f), (1.5f, 0.016f), (8f, 1f / 60f) })
        {
            var left = dur; var expected = 0;
            while (true) { expected++; left -= dt; if (left <= 0) break; }
            var l = new DemonLifecycle(new DemonFightDef { TransformDelayS = dur, TransformS = dur, ClimbS = dur, CorpseS = dur });
            l.Begin();
            Assert.Equal(expected, TicksUntilDue(l, dt));
        }
    }

    [Fact]
    public void Begin_is_refused_while_a_demon_is_here()
    {
        var l = new DemonLifecycle(Fight);
        l.Begin();
        l.Tick(0.4f);
        Assert.False(l.Begin());
        Assert.Equal(0.6f, l.Left, 5); // the clock was not restarted
    }

    [Theory]
    [InlineData(DemonStage.Delay)]
    [InlineData(DemonStage.Transforming)]
    [InlineData(DemonStage.Climbing)]
    [InlineData(DemonStage.Fighting)]
    [InlineData(DemonStage.Dying)]
    public void Clear_ends_every_stage_and_reports_where_it_was(DemonStage at)
    {
        var l = Reach(at);
        Assert.Equal(at, l.Clear());
        Assert.Equal(DemonStage.None, l.Stage);
        Assert.False(l.Tick(1f));
        Assert.Equal(DemonStage.None, l.Clear()); // nothing left to clear
        Assert.True(l.Begin());                   // and a new demon can start (a new round)
    }

    [Fact]
    public void A_failed_spawn_clears_from_Delay_and_the_stage_is_known_to_the_caller()
    {
        // DemonServer.Transform: the entity cap is reached while Delay -> Clear. The caller must still open the doors (ChurchDoorPolicyTests).
        var l = new DemonLifecycle(Fight);
        l.Begin();
        Assert.True(l.Tick(1f));
        Assert.Equal(DemonStage.Delay, l.Clear());
    }

    [Fact]
    public void WhyNot_names_missing_data()
    {
        Assert.NotNull(DemonLifecycle.WhyNot(new DemonFile()));
        var ok = new DemonFile();
        ok.Hits.Base = 10;
        Assert.Null(DemonLifecycle.WhyNot(ok));
    }

    [Fact]
    public void Follows_a_swapped_data_file()
    {
        var fight = Fight;
        var l = new DemonLifecycle(() => fight);
        fight = new DemonFightDef { TransformDelayS = 3f };
        l.Begin();
        Assert.Equal(3f, l.Left);
    }

    [Fact]
    public void Wire_states_of_the_scripted_stages()
    {
        Assert.Equal(DemonState.Transforming, DemonLifecycle.WireStateOf(DemonStage.Transforming));
        Assert.Equal(DemonState.Crawling, DemonLifecycle.WireStateOf(DemonStage.Climbing));
        Assert.Equal(DemonState.Dead, DemonLifecycle.WireStateOf(DemonStage.Dying));
    }

    [Fact]
    public void Every_brain_phase_has_a_wire_state_with_the_expected_names()
    {
        foreach (var phase in Enum.GetValues<DemonPhase>())
        {
            var state = DemonLifecycle.WireStateOf(phase);
            var expected = phase switch { DemonPhase.LeapWind => DemonState.Crouching, _ => Enum.Parse<DemonState>(phase.ToString()) };
            Assert.Equal(expected, state);
        }
    }

    [Fact]
    public void Wire_values_are_unchanged()
    {
        Assert.Equal(0, (byte)DemonState.Transforming);
        Assert.Equal(3, (byte)DemonState.Crawling);
        Assert.Equal(5, (byte)DemonState.Dead);
        Assert.Equal(12, (byte)DemonState.Pinning);
    }

    static DemonLifecycle Reach(DemonStage stage)
    {
        var l = new DemonLifecycle(Fight);
        l.Begin();
        if (stage == DemonStage.Delay) return l;
        l.BeginTransform();
        if (stage == DemonStage.Transforming) return l;
        l.BeginClimb();
        if (stage == DemonStage.Climbing) return l;
        l.BeginFight();
        if (stage == DemonStage.Fighting) return l;
        l.Die();
        return l;
    }
}
