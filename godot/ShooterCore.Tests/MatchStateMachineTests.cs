using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Pins the match phase and player life machines and the order of the damage gates (a characterization: the outcomes are the ones the match had before they were made explicit).</summary>
public class MatchStateMachineTests
{
    sealed class Rig
    {
        public double T;
        public readonly MatchSim M;
        public readonly List<MatchEvent> Ev = new();
        public Rig(Action<MatchConfig>? tune = null)
        {
            var cfg = new MatchConfig();
            tune?.Invoke(cfg);
            M = new MatchSim(cfg, () => T, new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(200, 0, 0) }, new Random(1));
            M.Event += Ev.Add;
        }
        public void Advance(double s) { T += s; M.Update(); }
        public void Pass() => Advance(3.01); // spawn protection (3 s) is over
        public List<MatchPhase> Phases() => Ev.OfType<PhaseChanged>().Select(e => e.Phase).ToList();
    }

    // ---- the phase machine ----

    [Fact]
    public void A_new_match_waits_and_the_first_player_starts_the_round()
    {
        var r = new Rig();
        Assert.Equal(MatchPhase.Waiting, r.M.Phase);
        Assert.Equal(0f, r.M.TimeRemaining);
        r.M.AddPlayer(1, "a");
        Assert.Equal(new[] { MatchPhase.Running }, r.Phases());
        Assert.Equal(600f, r.M.TimeRemaining, 3);
    }

    [Fact]
    public void The_time_limit_ends_the_round_the_end_screen_time_restarts_it_all_on_the_absolute_clock()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a");
        r.T = 599.99; r.M.Update();
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        r.T = 600; r.M.Update(); // now >= start + limit, exactly
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(10f, r.M.TimeRemaining, 3);
        r.T = 609.99; r.M.Update();
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        r.T = 610; r.M.Update();
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        Assert.Equal(new[] { MatchPhase.Running, MatchPhase.Ending, MatchPhase.Running }, r.Phases());
        Assert.Equal(600f, r.M.TimeRemaining, 3);
    }

    [Fact]
    public void The_score_goal_ends_the_round_from_the_hit_that_reaches_it()
    {
        var r = new Rig(c => c.ScoreGoal = 1);
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(new ushort[] { 1 }, r.M.Winners);
    }

    [Fact]
    public void Dropping_below_the_minimum_players_waits_and_resets_scores_and_lives()
    {
        var r = new Rig(c => c.MinPlayers = 2);
        r.M.AddPlayer(1, "a");
        Assert.Equal(MatchPhase.Waiting, r.M.Phase); // one is not enough
        r.M.AddPlayer(2, "b");
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        r.M.RemovePlayer(2);
        Assert.Equal(MatchPhase.Waiting, r.M.Phase);
        Assert.Equal(0, r.M.Get(1)!.Kills);
        Assert.Equal(PlayerLife.Unspawned, r.M.Get(1)!.Life);
        Assert.Equal(EntityState.Dead, r.M.StateOf(1)); // not alive: nothing to protect
        Assert.Equal(0f, r.M.TimeRemaining);
        Assert.Equal(new[] { MatchPhase.Running, MatchPhase.Waiting }, r.Phases());
    }

    [Fact]
    public void Dropping_below_the_minimum_during_the_end_screen_waits_too()
    {
        var r = new Rig(c => { c.MinPlayers = 2; c.ScoreGoal = 1; });
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        r.M.RemovePlayer(1);
        Assert.Equal(MatchPhase.Waiting, r.M.Phase);
        Assert.Empty(r.M.Winners);
    }

    [Fact]
    public void A_player_leaving_a_waiting_match_changes_nothing()
    {
        var r = new Rig(c => c.MinPlayers = 2);
        r.M.AddPlayer(1, "a");
        r.Ev.Clear();
        r.M.RemovePlayer(1);
        Assert.Empty(r.Phases());
    }

    [Fact]
    public void An_admin_restart_starts_a_fresh_round_in_every_phase_even_from_running()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        r.Ev.Clear();
        r.Advance(5);
        r.M.Restart();
        Assert.Equal(new[] { MatchPhase.Running }, r.Phases()); // announced again although the phase is the same
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        Assert.Equal(0, r.M.Get(1)!.Kills);
        Assert.Equal(600f, r.M.TimeRemaining, 3);
        Assert.All(r.M.Players, p => Assert.Equal(PlayerLife.Alive, p.Life));
    }

    [Fact]
    public void A_restart_with_nobody_waits_and_a_restart_while_waiting_stays_waiting()
    {
        var r = new Rig();
        r.M.Restart();
        Assert.Equal(MatchPhase.Waiting, r.M.Phase);
        Assert.Equal(new[] { MatchPhase.Waiting }, r.Phases());
    }

    // ---- the player life machine ----

    [Fact]
    public void A_player_goes_unspawned_alive_dead_alive_and_back_to_unspawned_at_a_round_reset()
    {
        var r = new Rig(c => c.MinPlayers = 2);
        r.M.AddPlayer(1, "a");
        Assert.Equal(PlayerLife.Unspawned, r.M.Get(1)!.Life);
        r.M.AddPlayer(2, "b");
        Assert.Equal(PlayerLife.Alive, r.M.Get(1)!.Life);
        r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(PlayerLife.Dead, r.M.Get(2)!.Life);
        r.Advance(3.01);
        Assert.Equal(PlayerLife.Alive, r.M.Get(2)!.Life);
        r.M.ApplyDamage(2, 1, 0, 500); // still protected after the respawn
        Assert.Equal(PlayerLife.Alive, r.M.Get(2)!.Life);
        r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(PlayerLife.Dead, r.M.Get(2)!.Life);
        r.M.Restart();
        Assert.Equal(PlayerLife.Alive, r.M.Get(2)!.Life); // reset to unspawned, then spawned by the new round
    }

    // ---- the damage gates, in order ----

    static (Rig R, MatchPlayer Victim) TwoPlayers(Action<MatchConfig>? tune = null, bool protectedStill = false)
    {
        var r = new Rig(tune);
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b");
        if (!protectedStill) r.Pass();
        return (r, r.M.Get(2)!);
    }

    [Fact]
    public void Gate_phase_ignores_everything_outside_a_running_round_and_no_damage()
    {
        var r = new Rig();
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 2, 0, 50)); // waiting, nobody there
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 0)); // no damage
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, -5));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(99, 1, 0, 50)); // unknown victim
    }

    [Fact]
    public void Gate_phase_holds_even_for_the_void_while_the_end_screen_shows()
    {
        var r = new Rig(c => c.ScoreGoal = 1);
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 1, SpecialWeapons.Void, 9999, ignoreProtection: true));
    }

    [Fact]
    public void Gate_held_comes_before_godmode_friendly_fire_and_protection_and_ignore_protection_does_not_lift_it()
    {
        var (r, v) = TwoPlayers(protectedStill: true);
        r.M.SetHeld(2, true);
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50, ignoreProtection: true));
        Assert.Equal(100f, v.Health);
    }

    [Fact]
    public void Gate_godmode_stops_a_sandbox_player_until_they_take_damage_and_ignore_protection_does_not_lift_it()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b", sandbox: true); r.Pass();
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 9999, ignoreProtection: true));
        r.M.SetVulnerable(2, true);
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(2, 1, 0, 50));
    }

    [Fact]
    public void Gate_friendly_fire_stops_a_teammate_but_not_self_damage_and_ignore_protection_does_not_lift_it()
    {
        var r = new Rig(c => c.Mode = "tdm");
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.M.AddPlayer(3, "c"); r.M.AddPlayer(4, "d"); r.Pass();
        var mate = r.M.Players.First(p => p.Id != 1 && p.Team == r.M.Get(1)!.Team).Id;
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(mate, 1, 0, 50));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(mate, 1, 0, 9999, ignoreProtection: true));
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(1, 1, 0, 10)); // self damage
        var foe = r.M.Players.First(p => p.Team != r.M.Get(1)!.Team).Id;
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(foe, 1, 0, 10));
    }

    [Fact]
    public void Gate_spawn_protection_ends_with_the_time_and_is_skipped_by_ignore_protection()
    {
        var (r, _) = TwoPlayers(protectedStill: true);
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50));
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(2, 1, SpecialWeapons.Void, 10, ignoreProtection: true));
        r.Pass();
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(2, 1, 0, 10));
    }

    [Fact]
    public void Gate_invulnerable_after_a_blessing_save_is_skipped_by_ignore_protection_and_ends_with_the_time()
    {
        var (r, _) = TwoPlayers();
        var saves = 1;
        r.M.SaveFromDeath = _ => saves-- > 0;
        Assert.Equal(DamageResult.Saved, r.M.ApplyDamage(2, 1, 0, 500));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50)); // invulnerable
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(2, 1, SpecialWeapons.Void, 10, ignoreProtection: true));
        r.Advance(BlessingRules.InvulnerableS + 0.1);
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(2, 1, 0, 10));
    }

    [Fact]
    public void Save_from_death_is_asked_only_for_a_lethal_hit_and_never_for_the_void()
    {
        var (r, _) = TwoPlayers();
        var asked = 0;
        r.M.SaveFromDeath = _ => { asked++; return false; };
        r.M.ApplyDamage(2, 1, 0, 10);
        Assert.Equal(0, asked); // not lethal
        r.M.ApplyDamage(2, 1, SpecialWeapons.Void, 9999, ignoreProtection: true);
        Assert.Equal(0, asked); // falls out of the world are not saved
        Assert.Equal(PlayerLife.Dead, r.M.Get(2)!.Life);
    }

    [Fact]
    public void A_dead_player_is_ignored_before_any_other_gate_looks()
    {
        var (r, _) = TwoPlayers();
        r.M.ApplyDamage(2, 1, 0, 500);
        r.M.SetHeld(2, true);
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50));
        Assert.Equal(1, r.M.Get(2)!.Deaths);
    }
}
