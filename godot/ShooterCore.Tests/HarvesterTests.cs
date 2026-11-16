using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The harvester event (#331): fields, pass planning, the AI driver, the cooldown, the bales, the corpse in the machine, the burn credit, the wire and the chat command.</summary>
public class HarvesterTests
{
    const float Dt = 1f / Wire.TickRate;
    static readonly HarvesterConfig Cfg = new();
    static VehicleDef Harvester => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Vehicles.Single(v => v.Id == HarvesterRules.VehicleId);

    static Vector2[] Rect(float x0, float y0, float x1, float y1) => new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

    // ---- fields ----

    [Fact]
    public void Only_crop_patches_but_the_bare_one_are_fields()
    {
        var ring = Rect(0, 0, 10, 10);
        var fields = CropFields.From(new (string, string, IReadOnlyList<Vector2>)[]
        {
            ("GP-0001", "paint_1f7d0c", ring), ("GP-0051", "crop_grain", ring), ("GP-0052", "crop_bare", ring), ("GP-0053", "crop_corn", ring), ("GP-0054", "crop_potato", new[] { Vector2.Zero, Vector2.One }),
        });
        Assert.Equal(new[] { "GP-0051", "GP-0053" }, fields.Select(f => f.Id));
        Assert.Equal(100f, fields[0].Area, 3);
        Assert.Equal(new Vector2(5, 5), fields[0].Centroid);
    }

    [Fact]
    public void A_field_near_a_player_is_picked_and_far_ones_only_when_asked()
    {
        var near = new CropField("GP-1", "crop_grain", Rect(0, 0, 100, 60));
        var far = new CropField("GP-2", "crop_grain", Rect(2000, 0, 2100, 60));
        var tiny = new CropField("GP-3", "crop_grain", Rect(120, 0, 125, 5));
        var fields = new[] { near, far, tiny };
        var players = new[] { new Vector2(150, 30) };
        for (var s = 0; s < 20; s++) Assert.Same(near, CropFields.Pick(fields, players, 350, 1500, new Random(s), false)); // the tiny one is no job, the far one is not near
        Assert.Null(CropFields.Pick(new[] { far }, players, 350, 1500, new Random(1), orNearest: false));
        Assert.Same(far, CropFields.Pick(new[] { far }, players, 350, 1500, new Random(1), orNearest: true)); // an admin's command takes the nearest
        Assert.Null(CropFields.Pick(new[] { tiny }, players, 350, 1500, new Random(1), orNearest: true));
    }

    // ---- pass planning ----

    [Fact]
    public void A_rectangle_is_cut_in_parallel_passes_driven_to_and_fro_inside_the_margin()
    {
        var ring = Rect(0, 0, 120, 40);
        var plan = FieldPlanner.Plan(ring, Cfg)!;
        Assert.NotNull(plan);
        var step = Cfg.HeaderWidthM - Cfg.PassOverlapM;
        var expected = (int)MathF.Floor((40 - 2 * Cfg.EdgeMarginM - Cfg.HeaderWidthM) / step) + 1;
        Assert.Equal(expected, plan.Passes.Count);
        for (var i = 0; i < plan.Passes.Count; i++)
        {
            var p = plan.Passes[i];
            Assert.True(Poly.Contains(ring, p.Start) && Poly.Contains(ring, p.End));
            Assert.True(p.Length > 100f); // 120 m less the two margins
            if (i > 0) Assert.True(Vector2.Dot(p.Dir, plan.Passes[i - 1].Dir) < -0.99f, "the next pass runs the other way");
            if (i > 0) Assert.Equal(step, MathF.Abs(plan.Passes[i].Start.Y - plan.Passes[i - 1].Start.Y), 2); // along X: Y is the lateral
        }
        Assert.True(plan.TotalCutM > 100f * plan.Passes.Count);
    }

    [Fact]
    public void The_drive_ends_with_the_way_out_along_the_edge_past_where_it_came_in()
    {
        var ring = Rect(0, 0, 120, 40);
        var plan = FieldPlanner.Plan(ring, Cfg)!;
        Assert.Equal(HarvesterPhase.Approach, plan.Legs[0].Phase);
        Assert.Equal(plan.Passes[0].Start, plan.Legs[0].Target);
        Assert.Equal(HarvesterPhase.Leave, plan.Legs[^1].Phase);
        Assert.False(Poly.Contains(ring, plan.Legs[^1].Target)); // the last point is outside the outline
        Assert.True(Poly.Contains(ring, plan.Entry));
        var phases = plan.Legs.Select(l => l.Phase).ToList();
        Assert.Equal(plan.Passes.Count, phases.Count(p => p == HarvesterPhase.Cut));
        Assert.True(phases.Where((p, i) => p == HarvesterPhase.Turn && i > 0 && phases[i - 1] == HarvesterPhase.Cut).Count() == plan.Passes.Count - 1);
        // phases only ever follow the declared machine
        var fsm = new StateMachine<HarvesterPhase>(HarvesterBrain.Spec);
        foreach (var leg in plan.Legs) fsm.Enter(leg.Phase);
    }

    [Fact]
    public void A_sliver_has_no_pass()
    {
        Assert.Null(FieldPlanner.Plan(Rect(0, 0, 100, 5), Cfg));
        Assert.Null(FieldPlanner.Plan(new[] { Vector2.Zero, Vector2.UnitX }, Cfg));
    }

    [Fact]
    public void A_concave_field_gets_a_pass_for_each_stretch_of_a_line()
    {
        // a "U": two arms 30 m wide joined at the bottom (low Y): lines through the arms cross it in two stretches
        var u = new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 80), new Vector2(70, 80), new Vector2(70, 30), new Vector2(30, 30), new Vector2(30, 80), new Vector2(0, 80) };
        var plan = FieldPlanner.Plan(u, Cfg)!;
        Assert.All(plan.Passes, p => Assert.True(Poly.Contains(u, p.Start) && Poly.Contains(u, p.End)));
        Assert.All(plan.Passes, p => Assert.False(Poly.Contains(u, (p.Start + p.End) / 2) == false)); // no pass cuts across the notch
    }

    // ---- the AI driver ----

    /// <summary>Drives the brain with the shipped harvester's car model until it finishes; returns the seconds it took.</summary>
    static float Drive(HarvesterBrain brain, HarvestPlan plan, out List<Vector2> cut, float limit = 1800)
    {
        var def = Harvester;
        var pos = plan.Entry; var s = new CarState { Yaw = plan.EntryYaw };
        cut = new List<Vector2>();
        var t = 0f;
        while (!brain.Finished && t < limit)
        {
            var cmd = brain.Tick(Dt, pos, s.Yaw, s.Speed);
            CarModel.Step(ref s, def, cmd.Throttle, cmd.Steer, false, Dt);
            pos += FieldPlanner.Forward(s.Yaw) * s.Speed * Dt;
            if (cmd.Cutting) cut.Add(pos + FieldPlanner.Forward(s.Yaw) * Cfg.HeaderForwardM);
            t += Dt;
        }
        return t;
    }

    [Fact]
    public void The_driver_cuts_every_pass_and_leaves_the_field()
    {
        var plan = FieldPlanner.Plan(Rect(0, 0, 90, 30), Cfg)!;
        var brain = new HarvesterBrain(plan, Cfg);
        var seconds = Drive(brain, plan, out var cut);
        Assert.Equal(HarvesterPhase.Gone, brain.Phase);
        Assert.True(seconds < 900, $"took {seconds:0} s");
        Assert.Equal(plan.Passes.Count - 1, brain.Progress.Pass);
        Assert.Equal(plan.Passes[^1].Length, brain.Progress.Along, 1);
        // the header covered the whole field's width: every pass line was driven near its centre
        foreach (var p in plan.Passes)
            Assert.Contains(cut, c => Poly.DistanceToSegment(c, p.Start, p.End) < 1.5f && Vector2.Distance(c, p.End) < 12f);
    }

    [Fact]
    public void A_killed_driver_stops_the_brain_from_any_phase()
    {
        var plan = FieldPlanner.Plan(Rect(0, 0, 90, 30), Cfg)!;
        var brain = new HarvesterBrain(plan, Cfg);
        for (var i = 0; i < 400; i++) brain.Tick(Dt, plan.Entry, plan.EntryYaw, 0);
        brain.Stop();
        Assert.Equal(HarvesterPhase.Stopped, brain.Phase);
        var cmd = brain.Tick(Dt, plan.Entry, plan.EntryYaw, 3);
        Assert.Equal(0f, cmd.Throttle);
        Assert.False(cmd.Cutting);
    }

    [Fact]
    public void A_leg_that_cannot_be_reached_is_given_up_after_the_timeout()
    {
        var plan = FieldPlanner.Plan(Rect(0, 0, 90, 30), Cfg)!;
        var brain = new HarvesterBrain(plan, Cfg);
        var legs = 0;
        for (var t = 0f; t < Cfg.LegTimeoutS * 3 && !brain.Finished; t += 1f) { brain.Tick(1f, new Vector2(-5000, -5000), 0, 0); legs = brain.LegIndex; }
        Assert.True(legs >= 2, "a machine that never gets anywhere still moves on to the next leg");
    }

    [Fact]
    public void The_pilot_steers_toward_the_target_and_slows_for_a_sharp_turn()
    {
        var (_, left) = HarvesterPilot.Drive(Vector2.Zero, 0, 3, new Vector2(-10, -10), 4); // facing -Z (yaw 0): a target at -X is to the left; steer +1 is right
        var (_, right) = HarvesterPilot.Drive(Vector2.Zero, 0, 3, new Vector2(10, -10), 4);
        Assert.True(left < 0 && right > 0);
        var (straight, none) = HarvesterPilot.Drive(Vector2.Zero, 0, 0, new Vector2(0, -50), 4);
        Assert.True(straight > 0.9f && MathF.Abs(none) < 1e-3f);
        var (slow, _) = HarvesterPilot.Drive(Vector2.Zero, 0, 4, new Vector2(0, 50), 4); // behind it: it does not floor it
        Assert.True(slow < straight);
    }

    // ---- the swath progress (all that is synced) ----

    [Fact]
    public void Progress_only_grows_and_replays_as_cuts_along_the_passes()
    {
        var passes = new[] { new HarvestPass(new Vector2(0, 0), new Vector2(100, 0)), new HarvestPass(new Vector2(100, 4.5f), new Vector2(0, 4.5f)) };
        var p = HarvestProgress.None;
        p = HarvestProgress.Advance(p, passes, 0, new Vector2(30, 0.5f));
        Assert.Equal(new HarvestProgress(0, 30), p);
        Assert.Equal(p, HarvestProgress.Advance(p, passes, 0, new Vector2(10, 0))); // never back
        p = HarvestProgress.Advance(p, passes, 1, new Vector2(70, 4.5f));
        Assert.Equal(new HarvestProgress(1, 30), p);
        Assert.Equal(p, HarvestProgress.Advance(p, passes, 0, new Vector2(90, 0))); // an earlier pass is done
        var cuts = HarvestProgress.Cuts(passes, HarvestProgress.None, p);
        Assert.Equal(2, cuts.Count);
        Assert.Equal((new Vector2(0, 0), new Vector2(100, 0)), cuts[0]); // the whole first pass
        Assert.Equal(new Vector2(100, 4.5f), cuts[1].A);
        Assert.Equal(70f, cuts[1].B.X, 3);
        var since = HarvestProgress.Cuts(passes, new HarvestProgress(0, 30), new HarvestProgress(0, 50));
        Assert.Single(since);
        Assert.Equal(30f, since[0].A.X, 3); Assert.Equal(50f, since[0].B.X, 3);
        Assert.Empty(HarvestProgress.Cuts(passes, p, p));
    }

    // ---- when it comes ----

    [Fact]
    public void No_harvester_for_twelve_minutes_after_one_is_done_but_a_command_ignores_that()
    {
        var d = new HarvesterDirector(new HarvesterConfig { InitialDelayS = 0, AutoChance = 1f, AutoRollS = 1 }, new Random(1));
        Assert.Equal(12 * 60f, Cfg.CooldownS);
        Assert.True(d.Tick(1, true, false)); // ready, certain roll: due
        d.Started();
        Assert.True(d.Active);
        Assert.NotNull(d.WhyNotForced()); // one at a time, also for the command
        Assert.False(d.Tick(1000, true, false)); // never while one is out
        d.Finished();
        Assert.False(d.Active);
        Assert.Equal(720f, d.CooldownLeft, 3);
        Assert.Null(d.WhyNotForced()); // the command does not wait for the cooldown
        for (var t = 0; t < 719; t++) Assert.False(d.Tick(1, true, false));
        var due = false;
        for (var t = 0; t < 5 && !due; t++) due = d.Tick(1, true, false);
        Assert.True(due);
    }

    [Fact]
    public void The_automatic_spawn_waits_for_a_human_and_for_other_events()
    {
        var d = new HarvesterDirector(new HarvesterConfig { InitialDelayS = 0, AutoChance = 1f, AutoRollS = 1 }, new Random(1));
        Assert.False(d.Tick(1, humansPlaying: false, otherEventRunning: false));
        Assert.False(d.Tick(1, humansPlaying: true, otherEventRunning: true));
        Assert.True(d.Tick(1, humansPlaying: true, otherEventRunning: false));
    }

    [Fact]
    public void A_rare_roll_spawns_a_few_times_an_hour_not_every_minute()
    {
        var d = new HarvesterDirector(new HarvesterConfig { InitialDelayS = 0 }, new Random(7));
        var spawns = 0;
        for (var minute = 0; minute < 600; minute++) // ten hours of rolls once a minute, with the cooldown after each
        {
            if (!d.Tick(60, true, false)) continue;
            spawns++; d.Started(); d.Finished();
        }
        Assert.InRange(spawns, 5, 45);
    }

    [Fact]
    public void A_new_round_starts_the_delay_again()
    {
        var d = new HarvesterDirector(Cfg, new Random(1));
        d.Started(); d.Reset();
        Assert.False(d.Active);
        Assert.Equal(Cfg.InitialDelayS, d.CooldownLeft, 3);
    }

    [Fact]
    public void The_harvester_is_a_startable_kind_of_the_one_at_a_time_event_book()
    {
        Assert.True(EventBook.IsStartable(EventBook.Harvester));
        var book = new EventBook();
        Assert.NotNull(book.Start(EventBook.Harvester, 0));
        Assert.NotNull(book.WhyNotStart(EventBook.Bigfoot));
        Assert.Null(book.Start(EventBook.Harvester, 1));
    }

    // ---- the command ----

    [Fact]
    public void The_event_command_takes_harvester_and_describes_it()
    {
        var ctx = new ChatContext(true, new[] { "car" });
        var parse = ChatCommands.Parse("/event harvester", ctx);
        Assert.True(parse.Ok, parse.Error);
        Assert.Equal(new[] { "harvester" }, parse.Args);
        var cmd = ChatCommands.Find("event")!;
        var choice = ChatCommands.Choices(cmd, 0, ctx).Single(c => c.Value == "harvester");
        Assert.Contains("cooldown", choice.Detail);
        Assert.False(ChatCommands.Parse("/event harvester", new ChatContext(false, Array.Empty<string>())).Ok); // admins only
        Assert.Contains(ChatCommands.Hint("/event har", ctx).Suggestions, s => s.Insert == "harvester");
    }

    [Fact]
    public void The_spawn_message_says_the_field_and_where_in_map_metres()
    {
        var text = HarvesterRules.SpawnedText("GP-0053", new Vector3(-120.4f, 3.6f, 88.4f));
        Assert.Contains("GP-0053", text);
        Assert.Contains("x=-120", text);
        Assert.Contains("y=4", text);
        Assert.Contains("z=88", text);
        var refused = HarvesterRules.RefusedRunning("GP-0053", new Vector3(1, 2, 3));
        Assert.Contains("GP-0053", refused);
        Assert.Contains("already", refused);
    }

    // ---- bales ----

    [Fact]
    public void A_shot_lights_a_bale_it_burns_a_few_seconds_and_turns_to_ash()
    {
        var bale = new HayBale(new Vector3(10, 0, 10), Cfg);
        Assert.Equal(BaleState.Intact, bale.State);
        Assert.True(bale.HitBy(new Vector3(0, 1.2f, 10), Vector3.UnitX, 20)); // a shot straight along its middle
        Assert.False(bale.HitBy(new Vector3(0, 1.2f, 14), Vector3.UnitX, 20)); // passes 4 m wide of it
        Assert.False(bale.HitBy(new Vector3(0, 1.2f, 10), Vector3.UnitX, 5)); // stops short of it
        Assert.True(bale.Ignite());
        Assert.False(bale.Ignite()); // already burning
        Assert.True(bale.Burning);
        var seconds = 0f;
        while (bale.Burning && seconds < 60) { Assert.False(seconds > 0 && bale.State == BaleState.Ash); bale.Tick(Dt); seconds += Dt; }
        Assert.Equal(BaleState.Ash, bale.State);
        Assert.InRange(seconds, Cfg.BaleBurnSeconds - 0.1f, Cfg.BaleBurnSeconds + 0.1f);
        Assert.False(bale.Ignite()); // ash does not burn again
    }

    [Fact]
    public void A_burning_bale_scorches_only_the_players_beside_it()
    {
        var bale = new HayBale(new Vector3(0, 0, 0), Cfg);
        var players = new (ushort, Vector3)[] { (1, new Vector3(1, 0, 1)), (2, new Vector3(6, 0, 0)), (3, new Vector3(0, 5, 0)) };
        Assert.Empty(bale.Scorched(players)); // whole: no fire
        bale.Ignite();
        Assert.Equal(new ushort[] { 1 }, bale.Scorched(players).ToArray());
        for (var i = 0; i < 400; i++) bale.Tick(Dt);
        Assert.Empty(bale.Scorched(players)); // ash
    }

    [Fact]
    public void A_burning_bale_burns_its_victims_credited_to_nobody()
    {
        Assert.Equal((ushort)7, BurnCredit.Attacker(BurnSet.HazardSource, 7, true)); // the victim's own damage: a death, no kill for anyone
        Assert.Equal((ushort)7, BurnCredit.Attacker(BurnSet.NoSource, 7, true));
        Assert.Equal((ushort)7, BurnCredit.Attacker(12, 7, false)); // the igniter has left
        Assert.Equal((ushort)12, BurnCredit.Attacker(12, 7, true)); // the devil's curse is still its owner's
        var burns = new BurnSet();
        burns.Ignite(7, BurnSet.HazardSource, Cfg.BaleBurn);
        var ticks = new List<BurnTick>();
        burns.Step(1f, ticks);
        Assert.Single(ticks);
        Assert.Equal(BurnSet.HazardSource, ticks[0].Source);
        burns.Ignite(7, 3, Cfg.BaleBurn); // a later igniter takes the credit over
        ticks.Clear(); burns.Step(1f, ticks);
        Assert.Equal((ushort)3, ticks[0].Source);
    }

    // ---- the corpse in the machine ----

    [Fact]
    public void A_victim_is_drawn_in_then_chewed_then_baled()
    {
        var m = new MachineCorpse(5, Cfg);
        Assert.Equal(MachineCorpsePhase.Drawn, m.Phase);
        Assert.Equal(0f, m.DrawFraction, 2);
        var entered = new List<MachineCorpsePhase>();
        for (var t = 0f; t < 20 && !m.Done; t += Dt)
        {
            if (m.Phase == MachineCorpsePhase.Drawn && t > Cfg.DrawSeconds / 2) Assert.InRange(m.DrawFraction, 0.4f, 0.8f);
            if (m.Tick(Dt) is { } p) entered.Add(p);
        }
        Assert.Equal(new[] { MachineCorpsePhase.Inside, MachineCorpsePhase.Baled }, entered);
        Assert.Equal(1f, m.DrawFraction);
        Assert.Null(m.Tick(Dt)); // done: nothing more
    }

    [Fact]
    public void A_drawn_in_corpse_cannot_be_looted_and_keeps_its_items_for_the_bale()
    {
        var corpse = new Corpse(9, new Vector3(1, 0, 1), 0, 0, 0, new[] { new ItemStack(3) });
        corpse.Attach(new Vector3(1, 0, 1)); // drawn in: seated, moved by the host
        Assert.False(corpse.CanLoot);
        corpse.Follow(new Vector3(4, 1, 4));
        Assert.Equal(new Vector3(4, 1, 4), corpse.Position);
        var bale = new Corpse(HarvesterRules.BaleOwner, new Vector3(5, 0, 5), 0, 0, 0, corpse.Items, Cfg.BaleLifetimeS);
        Assert.True(HarvesterRules.IsBale(bale.Owner));
        Assert.True(bale.CanLoot);
        Assert.Equal(corpse.Items.Count, bale.Items.Count);
        Assert.Equal(CorpseRules.LifetimeSeconds, Cfg.BaleLifetimeS); // despawns like a corpse
    }

    // ---- the wire ----

    [Fact]
    public void The_state_and_effect_messages_round_trip()
    {
        var s = new HarvesterCodec.StateMsg(HarvesterPhase.Cut, 1012, 7, 12, 63.4f, true, true, 4.8f, 3.5f, 0.3f);
        var back = HarvesterCodec.DecodeState(HarvesterCodec.EncodeState(s))!.Value;
        Assert.Equal(s.Phase, back.Phase); Assert.Equal(s.Car, back.Car); Assert.Equal(s.Field, back.Field); Assert.Equal(s.Pass, back.Pass);
        Assert.Equal(63.4f, back.Along, 1);
        Assert.True(back.Cutting && back.Driven);
        Assert.Equal(4.8f, back.HeaderM, 1); Assert.Equal(3.5f, back.MarginM, 1); Assert.Equal(0.3f, back.OverlapM, 1);
        Assert.Null(HarvesterCodec.DecodeState(new byte[3]));
        var fx = new HarvesterCodec.FxMsg(HarvesterCodec.FxKind.BaleLit, new Vector3(12.5f, 3f, -40.25f), HarvesterCodec.StaticBaleId(300));
        var fb = HarvesterCodec.DecodeFx(HarvesterCodec.EncodeFx(fx))!.Value;
        Assert.Equal(fx, fb);
        Assert.True(HarvesterCodec.IsStaticBale(fb.Id));
        Assert.Equal(300, HarvesterCodec.StaticBaleIndex(fb.Id));
        Assert.False(HarvesterCodec.IsStaticBale(77));
        Assert.Null(HarvesterCodec.DecodeFx(new byte[15].Select((_, i) => i == 0 ? (byte)99 : (byte)0).ToArray()));
    }

    // ---- the vehicle ----

    [Fact]
    public void The_harvester_is_a_slow_open_cab_machine_with_its_own_run_over()
    {
        var def = Harvester;
        Assert.False(def.RunOver); // NetServer.Harvester.cs runs it over at field speed
        Assert.True(def.Seats[0].Exposed && def.Seats[0].Role == "driver"); // a shot reaches the driver
        Assert.True(def.TurnRadius <= Cfg.TurnRadiusM);
        Assert.StartsWith("pack:synty-farm/", def.Model);
        Assert.True(def.TopSpeed > Cfg.TransitSpeedMps);
        Assert.Equal(180f, def.ModelYawDeg); // Unity's forward is +Z
    }

    [Fact]
    public void Every_harvester_machine_is_in_the_catalog()
    {
        var names = StateMachineCatalog.All.Select(s => s.Name).ToList();
        Assert.Contains("Harvester.Ai", names);
        Assert.Contains("Hay.Bale", names);
        Assert.Contains("Harvester.Corpse", names);
    }
}
