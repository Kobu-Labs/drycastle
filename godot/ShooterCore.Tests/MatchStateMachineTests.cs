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

    [Fact]
    public void The_match_phase_spec_lists_what_can_follow_what()
    {
        var s = MatchSim.PhaseSpec;
        Assert.True(s.Allows(MatchPhase.Waiting, MatchPhase.Running));
        Assert.True(s.Allows(MatchPhase.Running, MatchPhase.Ending));
        Assert.True(s.Allows(MatchPhase.Ending, MatchPhase.Running));
        Assert.True(s.Allows(MatchPhase.Running, MatchPhase.Waiting));
        Assert.True(s.Allows(MatchPhase.Ending, MatchPhase.Waiting));
        Assert.False(s.Allows(MatchPhase.Waiting, MatchPhase.Ending)); // a round has to run before it can end
    }

    sealed class Pod : IWorldEntity
    {
        public byte TypeId => 9;
        public ushort Owner => 1;
        public WorldDespawnReason? Ended => null;
        public void Tick(float dt) { }
        public void OnDamage(float amount, ushort attacker) { }
        public WorldState Serialize() => WorldState.Make(Vector3.Zero, 0, 0, 1, 1, 0);
    }

    [Fact]
    public void Dropping_to_waiting_clears_the_world_entities_like_a_round_start_does()
    {
        var types = new WorldEntityRegistry();
        types.Register(new WorldEntityDef(9, "pod", HasHp: false));
        var t = 0.0;
        var m = new MatchSim(new MatchConfig { MinPlayers = 2 }, () => t, new[] { Vector3.Zero }, new Random(1), types);
        m.AddPlayer(1, "a"); m.AddPlayer(2, "b");
        m.Entities.Spawn(new Pod());
        Assert.Equal(1, m.Entities.Count);
        m.RemovePlayer(2);
        Assert.Equal(MatchPhase.Waiting, m.Phase);
        Assert.Equal(0, m.Entities.Count);
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

    [Fact]
    public void The_life_spec_lists_what_can_follow_what()
    {
        var s = MatchPlayer.LifeSpec;
        Assert.True(s.Allows(PlayerLife.Unspawned, PlayerLife.Alive));
        Assert.True(s.Allows(PlayerLife.Alive, PlayerLife.Dead));
        Assert.True(s.Allows(PlayerLife.Dead, PlayerLife.Alive));
        Assert.True(s.Allows(PlayerLife.Alive, PlayerLife.Unspawned));
        Assert.True(s.Allows(PlayerLife.Dead, PlayerLife.Unspawned));
        Assert.False(s.Allows(PlayerLife.Unspawned, PlayerLife.Dead));
    }

    // ---- the client's view of the phase ----

    static byte[] State(MatchPhase p, float left, params (ushort Id, int Kills, int Deaths)[] rows) =>
        MatchCodec.Encode(new MatchStateMsg(p, left, Array.Empty<ushort>(), rows.Select(r => new ScoreRow(r.Id, "p" + r.Id, r.Kills, r.Deaths, 10)).ToList()));

    [Fact]
    public void The_client_reports_each_phase_change_once_and_the_first_state_has_no_earlier_phase()
    {
        var c = new ClientMatchState();
        var seen = new List<(MatchPhase?, MatchPhase)>();
        c.PhaseChanged += (from, to) => seen.Add((from, to));
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 500), 0);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 499), 1); // the once-a-second resend
        c.Apply(EventKind.MatchState, State(MatchPhase.Ending, 10), 2);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 600), 12);
        Assert.Equal(new (MatchPhase?, MatchPhase)[] { (null, MatchPhase.Running), (MatchPhase.Running, MatchPhase.Ending), (MatchPhase.Ending, MatchPhase.Running) }, seen);
    }

    [Fact]
    public void An_admin_restart_of_a_running_round_clears_the_feed_when_a_score_falls()
    {
        var c = new ClientMatchState();
        var restarts = 0;
        c.RoundRestarted += () => restarts++;
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 590, (1, 0, 0), (2, 0, 0)), 0);
        c.Apply(EventKind.Kill, MatchCodec.Encode(new KillMsg(1, 2, 0)), 1);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 589, (1, 1, 0), (2, 0, 1)), 1.1);
        Assert.Equal(0, restarts);
        Assert.Single(c.KillFeed);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 588, (1, 1, 0), (2, 0, 1)), 2); // the periodic resend
        Assert.Equal(0, restarts);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 586, (1, 0, 0), (2, 0, 0)), 4); // scores back to zero, time barely moved: the restart came at once
        Assert.Equal(1, restarts);
        Assert.Equal(1, c.Restarts);
        Assert.Empty(c.KillFeed);
    }

    [Fact]
    public void An_admin_restart_of_a_running_round_is_also_told_by_the_time_jumping_back_up()
    {
        var c = new ClientMatchState();
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 300), 0);
        c.Apply(EventKind.Kill, MatchCodec.Encode(new KillMsg(1, 1, 0)), 1); // a suicide: nobody's score moved
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 299, (1, 0, 0)), 1.5);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 600), 10); // restarted: back to the limit, not the 290 s it ran down to
        Assert.Equal(1, c.Restarts);
        Assert.Empty(c.KillFeed);
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 598), 12.5); // a little behind the clock: jitter, not a restart
        Assert.Equal(1, c.Restarts);
    }

    [Fact]
    public void Joining_a_running_match_and_the_end_screen_do_not_look_like_restarts()
    {
        var c = new ClientMatchState();
        c.Apply(EventKind.MatchState, State(MatchPhase.Running, 120, (1, 5, 2)), 0); // the first state of a mid-round joiner
        Assert.Equal(0, c.Restarts);
        c.Apply(EventKind.MatchState, State(MatchPhase.Ending, 10, (1, 25, 2)), 5);
        Assert.Equal(0, c.Restarts);
        Assert.Equal(1, c.RoundsEnded);
    }

    // ---- the gate ladder on its own (pure) ----

    [Fact]
    public void The_ladder_names_the_first_gate_that_applies_in_the_documented_order()
    {
        // everything blocks: peel the gates off one by one, each next to answer is the next in Order
        var f = new DamageFacts(RoundRunning: false, Amount: 0, VictimAlive: false, Held: true, Godmode: true, FriendlyFireBlocked: true, SpawnProtected: true, Invulnerable: true, IgnoreProtection: false);
        var seen = new List<DamageGate>();
        seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { RoundRunning = true, Amount = 5 }; seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { VictimAlive = true }; seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { Held = false }; seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { Godmode = false }; seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { FriendlyFireBlocked = false }; seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { SpawnProtected = false }; seen.Add(DamageGates.FirstBlock(f)!.Value);
        f = f with { Invulnerable = false };
        Assert.Null(DamageGates.FirstBlock(f));
        Assert.Equal(DamageGates.Order, seen);
        Assert.Equal(Enum.GetValues<DamageGate>(), DamageGates.Order); // every gate is in the list, in declaration order
    }

    [Fact]
    public void Ignore_protection_lifts_only_spawn_protection_and_invulnerability()
    {
        var all = new DamageFacts(true, 5, true, Held: true, Godmode: true, FriendlyFireBlocked: true, SpawnProtected: true, Invulnerable: true, IgnoreProtection: true);
        Assert.Equal(DamageGate.Held, DamageGates.FirstBlock(all));
        Assert.Equal(DamageGate.Godmode, DamageGates.FirstBlock(all with { Held = false }));
        Assert.Equal(DamageGate.FriendlyFire, DamageGates.FirstBlock(all with { Held = false, Godmode = false }));
        Assert.Null(DamageGates.FirstBlock(all with { Held = false, Godmode = false, FriendlyFireBlocked = false }));
    }
}
