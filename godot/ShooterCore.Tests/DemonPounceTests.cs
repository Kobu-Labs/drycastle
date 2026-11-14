using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The demon's pounce: the choice and the cooldown, the homing landing, the pin, the health it leaves, the immunity while pinned, and that every way out lets the victim go.</summary>
public class DemonPounceTests
{
    static DemonFile Full => DemonFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "holy_mass_demon.json"));

    /// <summary>The shipped numbers with everything but the pounce off (roar, flame, fireballs out of reach, no leap by chance) and the pounce sure and at once.</summary>
    static DemonFile Def
    {
        get
        {
            var d = Full;
            d.Roar.DurationS = 0; d.Flame.DurationS = 0; d.Leap.Chance = 0;
            d.Fireball.MaxRangeM = 0.1f; // never in reach: no fireball gets in the way
            d.Pounce.Chance = 1; d.Pounce.FirstAfterS = 0;
            return d;
        }
    }

    static AnchorGraph Graph => AnchorGraph.FromJson(DemonTests.ChurchJson);

    // the demon starts on anchor 5, on the east wall at (10, 3, 0): a victim at (2, 0, 0) is about 8 m away
    static DemonPlayer P(ushort id, float x, float z, bool grounded = true, bool seated = false, bool alive = true) => new(id, new Vector3(x, 0, z), alive, default, grounded, seated);

    static DemonBrain Brain(DemonFile? d = null, int seed = 1, Func<Vector3, Vector3, bool>? sight = null) => new(d ?? Def, Graph, new Random(seed), sight, 5);

    const float Dt = 1f / 30;

    static List<DemonEvent> Run(DemonBrain b, Func<IReadOnlyList<DemonPlayer>> players, float seconds, Func<DemonBrain, bool>? stop = null)
    {
        var ev = new List<DemonEvent>();
        for (var i = 0; i < (int)(seconds * 30); i++)
        {
            b.Step(Dt, players(), ev);
            if (stop != null && stop(b)) break;
        }
        return ev;
    }

    static List<DemonEvent> Run(DemonBrain b, IReadOnlyList<DemonPlayer> players, float seconds, Func<DemonBrain, bool>? stop = null) => Run(b, () => players, seconds, stop);

    static IEnumerable<DemonPounced> Stages(List<DemonEvent> ev) => ev.OfType<DemonPounced>();

    /// <summary>A brain that has gone through to the pin of player 1 standing at (2, 0, 0).</summary>
    static (DemonBrain Brain, List<DemonEvent> Events) Pinned(DemonFile? d = null)
    {
        var b = Brain(d);
        var ev = Run(b, new[] { P(1, 2, 0) }, 20, x => x.Phase == DemonPhase.Pinning);
        Assert.Equal(DemonPhase.Pinning, b.Phase);
        return (b, ev);
    }

    // ---- data ----

    [Fact]
    public void The_shipped_pounce_is_on_and_leaves_ten_hp()
    {
        var p = Full.Pounce;
        Assert.True(p.PinS > 0 && p.WindS > 0 && p.RangeM > 0);
        Assert.Equal(10, p.LeaveHp);
        Assert.Equal(5f, p.PinS);
    }

    [Fact]
    public void A_pounce_with_a_bad_leave_hp_is_refused_and_zero_pin_turns_it_off()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "holy_mass_demon.json"));
        Assert.Throws<DataFileException>(() => DemonFile.Parse(json.Replace("\"leave_hp\": 10", "\"leave_hp\": 0")));
        var off = DemonFile.Parse(json.Replace("\"pin_s\": 5.0", "\"pin_s\": 0").Replace("\"leave_hp\": 10", "\"leave_hp\": 0"));
        Assert.Equal(0, off.Pounce.PinS);
    }

    [Fact]
    public void The_pounce_event_round_trips()
    {
        var m = new DemonPounceMsg(513, PounceStage.Pinned, -1.25f);
        Assert.Equal(m, DemonCodec.DecodePounce(DemonCodec.EncodePounce(m)));
        Assert.Null(DemonCodec.DecodePounce(new byte[] { 1, 0, 9, 0, 0, 0, 0 })); // a stage that does not exist
    }

    // ---- the choice and the cooldown ----

    [Fact]
    public void It_marks_a_grounded_player_in_range_and_sight_and_crouches()
    {
        var b = Brain();
        var ev = Run(b, new[] { P(1, 2, 0) }, 10, x => x.Phase == DemonPhase.PounceWind);
        Assert.Equal(DemonPhase.PounceWind, b.Phase);
        Assert.Equal((ushort)1, b.PounceVictim);
        var s = Assert.Single(Stages(ev));
        Assert.Equal((ushort)1, s.Victim);
        Assert.Equal(PounceStage.Wind, s.Stage);
    }

    [Fact]
    public void It_does_not_pounce_on_a_jumper_a_sitter_the_dead_a_far_one_or_one_out_of_sight()
    {
        foreach (var p in new[] { P(1, 2, 0, grounded: false), P(1, 2, 0, seated: true), P(1, 2, 0, alive: false) })
        {
            var b = Brain();
            var ev = Run(b, new[] { p }, 15);
            Assert.Empty(Stages(ev));
        }
        var d = Def; d.Pounce.RangeM = 3; // the walls are all further off than this
        var far = Brain(d);
        Assert.Empty(Stages(Run(far, new[] { P(1, 2, 0) }, 15)));
        var blind = Brain(sight: (_, _) => false);
        Run(blind, new[] { P(1, 2, 0) }, 15);
        Assert.Null(blind.PounceVictim);
    }

    [Fact]
    public void It_waits_for_the_first_after_time()
    {
        var d = Def; d.Pounce.FirstAfterS = 12; d.Pounce.RangeM = 60; // in reach wherever it crawls to
        var b = Brain(d);
        Run(b, new[] { P(1, 2, 0) }, 11.5f);
        Assert.Null(b.PounceVictim);
        Run(b, new[] { P(1, 2, 0) }, 40, x => x.PounceVictim != null);
        Assert.Equal((ushort)1, b.PounceVictim);
    }

    [Fact]
    public void A_pin_of_zero_seconds_is_no_pounce()
    {
        var d = Def; d.Pounce.PinS = 0;
        var b = Brain(d);
        var ev = Run(b, new[] { P(1, 2, 0) }, 30);
        Assert.Empty(Stages(ev));
    }

    [Fact]
    public void The_cooldown_holds_the_next_pounce_back_and_it_is_not_the_same_victim_twice_when_another_qualifies()
    {
        var d = Def; d.Pounce.CooldownS = 30;
        var b = Brain(d);
        var both = new[] { P(1, 2, 0), P(2, 2, 3) };
        var ev = Run(b, both, 40, x => x.Phase == DemonPhase.Pinning);
        var first = Stages(ev).First().Victim;
        var after = Run(b, both, 15, x => x.PounceVictim == null && x.Phase is DemonPhase.Leaping or DemonPhase.Dwelling);
        Run(b, both, 28.5f); // 30 s counts from the release; not yet over
        Assert.DoesNotContain(Stages(after), s => s.Stage == PounceStage.Wind);
        var later = Run(b, both, 40, x => x.PounceVictim != null);
        var second = Stages(later).First(s => s.Stage == PounceStage.Wind);
        Assert.NotEqual(first, second.Victim);
    }

    // ---- flight and landing ----

    [Fact]
    public void The_arc_homes_in_on_where_the_victim_is_when_it_lands()
    {
        var b = Brain();
        var x = 2f; var z = 0f;
        IReadOnlyList<DemonPlayer> Now() => new[] { P(1, x, z) };
        var ev = new List<DemonEvent>();
        var landed = false;
        for (var i = 0; i < 600 && !landed; i++)
        {
            if (b.Phase == DemonPhase.Pouncing) { x += 4f * Dt; z += 3f * Dt; } // the victim runs, it does not help
            b.Step(Dt, Now(), ev);
            landed = b.Phase == DemonPhase.Pinning;
        }
        Assert.True(landed);
        var pinned = Stages(ev).Single(s => s.Stage == PounceStage.Pinned);
        Assert.Equal(x, pinned.Feet.X, 0.05f);
        Assert.Equal(z, pinned.Feet.Z, 0.05f);
        Assert.Equal(0f, pinned.Feet.Y, 0.001f); // the floor
        Assert.True(x > 2.5f); // it did run
    }

    [Fact]
    public void The_victim_lies_head_away_from_where_the_demon_came_from_and_the_demon_stands_on_the_floor_over_them()
    {
        var (b, ev) = Pinned();
        var pinned = Stages(ev).Single(s => s.Stage == PounceStage.Pinned);
        // it came from the east wall: the head is to the west (-x), and the yaw says so (head towards (sin yaw, cos yaw))
        Assert.True(b.PinDirection.X < -0.9f);
        Assert.Equal(b.PinDirection.X, MathF.Sin(pinned.Yaw), 1e-4f);
        Assert.Equal(b.PinDirection.Z, MathF.Cos(pinned.Yaw), 1e-4f);
        Run(b, new[] { P(1, 2, 0) }, 1); // settled
        Assert.Equal(Vector3.UnitY, b.Pose.Normal);
        Assert.Equal(0f, b.Pose.Position.Y, 0.001f);
        Assert.True(Vector3.Dot(b.Pose.Forward, b.PinDirection) > 0.99f); // its body along theirs, facing the way their head lies
        // its root is behind the head end by the head's reach
        var headEnd = b.PinFeet + b.PinDirection * DemonBrain.LyingHalfM;
        Assert.Equal(Full.Pounce.HeadForwardM * Full.Scale, Vector3.Distance(b.Pose.Position, headEnd), 0.05f);
    }

    // ---- the pin ----

    [Fact]
    public void The_pin_lasts_the_pin_seconds_then_it_releases_and_leaps_away_to_a_wall_or_the_ceiling()
    {
        var (b, ev) = Pinned();
        var held = 0f;
        var rel = new List<DemonEvent>();
        for (var i = 0; i < 400 && Stages(rel).All(s => s.Stage != PounceStage.Released); i++) { b.Step(Dt, new[] { P(1, 2, 0) }, rel); held += Dt; }
        Assert.Equal(Full.Pounce.PinS, held, 0.1f);
        Assert.Null(b.PounceVictim);
        Assert.Contains(b.Phase, new[] { DemonPhase.LeapWind, DemonPhase.Leaping });
        Run(b, new[] { P(1, 2, 0) }, 10, x => x.Phase is DemonPhase.Dwelling);
        Assert.Equal(DemonPhase.Dwelling, b.Phase);
        Assert.True(Vector3.Dot(b.Pose.Normal, Vector3.UnitY) < 0.5f); // on a wall or the ceiling, not the floor
        Assert.True(b.Pose.Position.Y > 1f);
        Assert.DoesNotContain(Stages(rel), s => s.Stage == PounceStage.Aborted);
    }

    // ---- health and immunity (the match) ----

    static (MatchSim M, Action<double> Advance) Match()
    {
        double t = 0;
        var m = new MatchSim(new MatchConfig(), () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        m.AddPlayer(1, "a"); m.AddPlayer(2, "b");
        t += 10; m.Update(); // spawn protection is over
        return (m, s => { t += s; m.Update(); });
    }

    [Theory]
    [InlineData(100f)]
    [InlineData(54f)]
    [InlineData(3f)]
    public void The_victim_is_left_at_exactly_ten_hp_from_above_and_from_below(float before)
    {
        var (m, _) = Match();
        m.Get(1)!.Health = before;
        Assert.True(m.SetHealth(1, Full.Pounce.LeaveHp));
        Assert.Equal(10f, m.Get(1)!.Health);
    }

    [Fact]
    public void A_held_player_takes_no_damage_from_anything_and_cannot_be_healed_and_a_released_one_can_be_hurt_again()
    {
        var (m, _) = Match();
        Assert.True(m.SetHeld(1, true));
        Assert.Equal(DamageResult.Ignored, m.ApplyDamage(1, 2, 0, 500f));
        Assert.Equal(DamageResult.Ignored, m.ApplyDamage(1, 1, SpecialWeapons.Burn, 50f));
        Assert.Equal(DamageResult.Ignored, m.ApplyDamage(1, 1, SpecialWeapons.Void, 9999f, ignoreProtection: true));
        Assert.Equal(100f, m.Get(1)!.Health);
        Assert.Equal(PlayerLife.Alive, m.Get(1)!.Life);
        m.Get(1)!.Health = 40;
        Assert.False(m.Heal(1));
        Assert.Equal(40f, m.Get(1)!.Health);
        m.SetHeld(1, false);
        Assert.Equal(DamageResult.Damaged, m.ApplyDamage(1, 2, 0, 5f));
        Assert.Equal(35f, m.Get(1)!.Health);
    }

    // ---- every way out lets the victim go ----

    static void AssertReleased(DemonBrain b, List<DemonEvent> ev, ushort victim)
    {
        Assert.Null(b.PounceVictim);
        Assert.Single(Stages(ev), s => s.Victim == victim && s.Stage is PounceStage.Aborted or PounceStage.Released);
        Assert.Equal(new List<ushort> { victim }, PounceRules.Stuck(new ushort[] { victim }, b, _ => true)); // nothing holds them any more
    }

    [Fact]
    public void The_victim_leaving_during_the_wind_aborts_with_no_pin()
    {
        var b = Brain();
        Run(b, new[] { P(1, 2, 0) }, 10, x => x.Phase == DemonPhase.PounceWind);
        var ev = Run(b, Array.Empty<DemonPlayer>(), 1);
        AssertReleased(b, ev, 1);
        Assert.DoesNotContain(Stages(ev), s => s.Stage == PounceStage.Pinned);
        Assert.NotEqual(DemonPhase.PounceWind, b.Phase);
    }

    [Fact]
    public void The_victim_dying_before_the_landing_aborts_it_finishes_the_leap_without_a_pin_and_leaps_away()
    {
        var b = Brain();
        Run(b, new[] { P(1, 2, 0) }, 10, x => x.Phase == DemonPhase.Pouncing);
        var ev = Run(b, new[] { P(1, 2, 0, alive: false), P(2, 5, 5) }, 6, x => x.Phase is DemonPhase.Dwelling);
        AssertReleased(b, ev, 1);
        Assert.DoesNotContain(Stages(ev), s => s.Stage == PounceStage.Pinned);
        Assert.Equal(DemonPhase.Dwelling, b.Phase);
        Assert.True(b.Pose.Position.Y > 1f); // it did not stay down there
    }

    [Fact]
    public void The_victim_disconnecting_during_the_pin_aborts_and_the_demon_goes()
    {
        var (b, _) = Pinned();
        var ev = Run(b, new[] { P(2, 5, 5) }, 0.5f);
        AssertReleased(b, ev, 1);
        Assert.Contains(b.Phase, new[] { DemonPhase.LeapWind, DemonPhase.Leaping });
        Assert.DoesNotContain(Stages(ev), s => s.Stage == PounceStage.Released); // no release: their health is not touched
    }

    [Fact]
    public void The_demon_dying_during_the_pin_aborts()
    {
        var (b, _) = Pinned();
        var ev = new List<DemonEvent>();
        for (var i = 0; i < 200 && b.Phase != DemonPhase.Dead; i++) b.RegisterHit(2, DemonHitKind.Explosive, ev);
        Assert.Equal(DemonPhase.Dead, b.Phase);
        AssertReleased(b, ev, 1);
        Assert.Contains(ev, e => e is DemonDied);
    }

    [Fact]
    public void Everyone_dying_during_the_wind_aborts_and_it_waits()
    {
        var b = Brain();
        Run(b, new[] { P(1, 2, 0) }, 10, x => x.Phase == DemonPhase.PounceWind);
        var ev = Run(b, new[] { P(1, 2, 0, alive: false) }, 0.2f);
        AssertReleased(b, ev, 1);
        Assert.Contains(ev, e => e is DemonWiped);
        Assert.Equal(DemonPhase.Waiting, b.Phase);
    }

    [Fact]
    public void The_hosts_sweep_lets_go_of_anyone_nothing_holds_whatever_ended_the_event()
    {
        var (b, _) = Pinned();
        var held = new ushort[] { 1, 7 };
        // the demon pins 1; 7 is held by nothing (a leftover)
        Assert.Equal(new List<ushort> { 7 }, PounceRules.Stuck(held, b, _ => true));
        Assert.Equal(new List<ushort> { 1, 7 }, PounceRules.Stuck(held, null, _ => true)); // no brain: the event ended or the demon was removed
        Assert.Equal(new List<ushort> { 1, 7 }, PounceRules.Stuck(held, b, id => id != 1 && id != 7)); // they left or died
        var ev = new List<DemonEvent>();
        for (var i = 0; i < 200 && b.Phase != DemonPhase.Dead; i++) b.RegisterHit(2, DemonHitKind.Explosive, ev);
        Assert.Equal(new List<ushort> { 1, 7 }, PounceRules.Stuck(held, b, _ => true)); // a dead demon holds nobody
    }
}
