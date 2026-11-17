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
        }
        // every lane is cut once: the lanes (along Y) are the field's, one step apart
        var ys = plan.Passes.Select(p => p.Start.Y).OrderBy(y => y).ToList();
        for (var i = 1; i < ys.Count; i++) Assert.Equal(step, ys[i] - ys[i - 1], 2);
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
    public void The_driver_follows_the_skip_lane_and_bulb_turns_with_the_machines_real_handling()
    {
        var plan = FieldPlanner.Plan(Rect(0, 0, 160, 60), Cfg)!;
        var brain = new HarvesterBrain(plan, Cfg);
        var seconds = Drive(brain, plan, out _, 4000);
        Assert.Equal(HarvesterPhase.Gone, brain.Phase);
        Assert.True(seconds < 3000, $"took {seconds:0} s");
        Assert.Equal(plan.Passes.Count - 1, brain.Progress.Pass);
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
            if (m.Phase == MachineCorpsePhase.Drawn && t > Cfg.DrawSeconds * 0.5f && t < Cfg.DrawSeconds * 0.6f) Assert.InRange(m.DrawFraction, 0.4f, 0.7f); // about half way in
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
        Assert.True(float.IsPositiveInfinity(Cfg.BaleLifetimeS)); // no lifetime
    }

    [Fact]
    public void A_bale_corpse_never_expires_is_no_corpse_for_the_cap_and_keeps_lying_after_its_items_are_taken()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types);
        GroundItemRules.Register(types);
        var world = new WorldEntityManager(types);
        var book = new CorpseBook(world);
        var bale = book.Add(new Corpse(HarvesterRules.BaleOwner, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(3) }, Cfg.BaleLifetimeS))!.Value;
        for (var i = 0; i < CorpseRules.MaxCorpses * 3; i++) book.Add(new Corpse(1, new Vector3(i, 0, 0), 0, 0, 0)); // a busy fight
        for (var i = 0; i < 100; i++) world.Tick(60f); // 100 minutes
        Assert.NotNull(book.Get(bale));
        Assert.True(book.CorpseCount <= CorpseRules.MaxCorpses + 1);
        // the safety cap drops the oldest bale only
        var more = Enumerable.Range(0, CorpseBook.MaxBales).Select(i => book.Add(new Corpse(HarvesterRules.BaleOwner, new Vector3(i, 0, 5), 0, 0, 0, null, Cfg.BaleLifetimeS))!.Value).ToList();
        Assert.Null(book.Get(bale));
        Assert.NotNull(book.Get(more[^1]));
    }

    /// <summary>The smallest radius of any three consecutive turn waypoints of the plan (the circle through them).</summary>
    static float TightestTurn(HarvestPlan plan)
    {
        var tight = float.MaxValue;
        for (var i = 1; i + 1 < plan.Legs.Count; i++)
        {
            if (plan.Legs[i].Phase != HarvesterPhase.Turn || plan.Legs[i - 1].Phase == HarvesterPhase.Cut && plan.Legs[i + 1].Phase != HarvesterPhase.Turn) continue;
            var p = plan.Legs[i - 1].Target; var q = plan.Legs[i].Target; var r = plan.Legs[i + 1].Target;
            var a = Vector2.Distance(p, q); var b = Vector2.Distance(q, r); var c = Vector2.Distance(p, r);
            var area2 = MathF.Abs((q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X));
            if (area2 < 1e-3f) continue; // a straight run
            tight = MathF.Min(tight, a * b * c / (2f * area2));
        }
        return tight;
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(200, 100)]
    [InlineData(150, 33)]
    [InlineData(300, 160)]
    public void A_real_machine_turns_on_arcs_no_tighter_than_its_radius_and_still_cuts_every_lane(float w, float h)
    {
        Assert.Equal(7f, Cfg.TurnRadiusM);
        var ring = Rect(0, 0, w, h);
        var plan = FieldPlanner.Plan(ring, Cfg)!;
        var step = Cfg.HeaderWidthM - Cfg.PassOverlapM;
        var lanes = (int)MathF.Floor((h - 2 * Cfg.EdgeMarginM - Cfg.HeaderWidthM / 2f + 1e-3f) / step) + 1;
        Assert.Equal(lanes, plan.Passes.Count); // none is left out
        Assert.Equal(lanes, plan.Passes.Select(p => (int)MathF.Round((p.Start.Y - Cfg.EdgeMarginM - Cfg.HeaderWidthM / 2f) / step)).Distinct().Count());
        Assert.True(TightestTurn(plan) >= Cfg.TurnRadiusM * 0.95f, $"tightest turn {TightestTurn(plan):F2} m");
        // the lanes are skipped: the first turn spans the lanes a half circle needs
        var skip = FieldPlanner.SkipLanes(Cfg.TurnRadiusM, step);
        Assert.Equal(4, skip);
        if (lanes > 2 * skip) Assert.True(MathF.Abs(plan.Passes[1].Start.Y - plan.Passes[0].Start.Y) >= skip * step - 0.01f);
        for (var i = 1; i < plan.Passes.Count; i++) Assert.True(Vector2.Dot(plan.Passes[i].Dir, plan.Passes[i - 1].Dir) < -0.99f);
    }

    [Fact]
    public void A_bulb_turn_between_neighbouring_lanes_is_three_arcs_that_end_on_the_next_lane_heading_back()
    {
        var a = new HarvestPass(new Vector2(0, 0), new Vector2(100, 0));
        var b = new HarvestPass(new Vector2(100, 4.5f), new Vector2(0, 4.5f));
        var legs = new List<HarvestLeg>();
        typeof(FieldPlanner).GetMethod("AddTurn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { legs, a, b, 0, 9f, Cfg });
        var plan = new HarvestPlan(new[] { a, b }, Vector2.Zero, 0, legs);
        Assert.True(TightestTurn(plan) >= Cfg.TurnRadiusM * 0.95f, $"{TightestTurn(plan):F2}");
        Assert.Equal(b.Start, legs[^1].Target);
        var end = FieldPlanner.BulbEnd(0.9f, 7f);
        Assert.True(end.Y > 0);
        Assert.Equal(4.5f, FieldPlanner.BulbEnd(Bisect(4.5f), 7f).Y, 2);
    }

    static float Bisect(float dy)
    {
        float lo = 0, hi = MathF.PI;
        for (var i = 0; i < 50; i++) { var m = (lo + hi) / 2f; if (FieldPlanner.BulbEnd(m, 7f).Y > dy) lo = m; else hi = m; }
        return (lo + hi) / 2f;
    }

    // ---- the reel and the real path ----

    [Fact]
    public void Only_the_reel_box_catches_and_it_turns_with_the_machine()
    {
        var reel = Cfg.Reel;
        var o = new Vector3(100, 5, 50);
        // yaw 0 faces -Z: the reel is 3.66 m ahead (z smaller)
        Assert.True(reel.Contains(o, 0, new Vector3(100, 5.5f, 50 - reel.CentreForwardM)));
        Assert.True(reel.Contains(o, 0, new Vector3(100 + 2.4f, 5.5f, 50 - reel.CentreForwardM + 0.5f)));
        Assert.False(reel.Contains(o, 0, new Vector3(100 + 3f, 5.5f, 50 - reel.CentreForwardM))); // beside the header
        Assert.False(reel.Contains(o, 0, new Vector3(100, 5.5f, 50 - 1))); // the body
        Assert.False(reel.Contains(o, 0, new Vector3(100, 5.5f, 50 + 2))); // behind
        Assert.False(reel.Contains(o, 0, new Vector3(100, 5 + 4, 50 - reel.CentreForwardM))); // overhead
        // turned a quarter to the left (yaw +90 deg faces -X): the same box lies west
        var yaw = MathF.PI / 2;
        Assert.True(reel.Contains(o, yaw, new Vector3(100 - reel.CentreForwardM, 5.5f, 50 + 2f)));
        Assert.False(reel.Contains(o, yaw, new Vector3(100, 5.5f, 50 - reel.CentreForwardM)));
        var c = reel.Centre(new Vector2(100, 50), yaw);
        Assert.Equal(100 - reel.CentreForwardM, c.X, 3); Assert.Equal(50f, c.Y, 3);
    }

    [Fact]
    public void The_sampler_gives_a_segment_every_two_metres_only_while_it_cuts()
    {
        var s = new SwathSampler(Cfg);
        Assert.False(s.Step(0, new Vector2(0, 0), true, out _, out _)); // the first point only starts the path
        Assert.False(s.Step(0, new Vector2(1, 0), true, out _, out _));
        Assert.True(s.Step(0, new Vector2(2.1f, 0), true, out var a, out var b));
        Assert.Equal(new SwathPt(0, 0), a); Assert.Equal(SwathPt.From(new Vector2(2.1f, 0)), b);
        Assert.False(s.Step(0, new Vector2(3, 0), true, out _, out _));
        Assert.False(s.Step(0, new Vector2(10, 0), false, out _, out _)); // lifted (stopped or backing): the path is over
        Assert.False(s.Step(0, new Vector2(12, 0), true, out _, out _)); // and starts afresh
        Assert.True(s.Step(0, new Vector2(14.5f, 0), true, out a, out _));
        Assert.Equal(SwathPt.From(new Vector2(12, 0)), a);
        Assert.False(s.Step(1, new Vector2(16.5f, 0), true, out _, out _)); // another field: afresh
        Assert.False(s.Step(-1, new Vector2(18.5f, 0), true, out _, out _)); // off the crop
        Assert.False(s.Step(1, new Vector2(500, 0), true, out _, out _)); // a jump is no drive
    }

    static (SwathPt, SwathPt) Seg(float x0, float z0, float x1, float z1) => (SwathPt.From(new Vector2(x0, z0)), SwathPt.From(new Vector2(x1, z1)));

    [Fact]
    public void Ground_cut_before_is_not_cut_again_and_does_not_count()
    {
        var f = new FieldSwath(Cfg, dropsBales: true);
        var total = 0;
        for (var x = 0f; x < 40; x += 2) { var (a, b) = Seg(x, 0, x + 2, 0); Assert.True(f.TryAdd(a, b, total)); total += 2; }
        var metres = f.Metres;
        Assert.Equal(40f, metres, 1);
        for (var x = 0f; x < 40; x += 2) { var (a, b) = Seg(x, 0.5f, x + 2, 0.5f); Assert.False(f.TryAdd(a, b, total)); } // the same lane again
        for (var x = 40f; x > 0; x -= 2) { var (a, b) = Seg(x, -0.5f, x - 2, -0.5f); Assert.False(f.TryAdd(a, b, total)); } // and back
        Assert.Equal(metres, f.Metres);
        var (c, d) = Seg(0, 4.5f, 2, 4.5f); // the next lane over: new ground
        Assert.True(f.TryAdd(c, d, total));
        Assert.Equal(2, f.Strokes.Count); // a stretch not joined to the last one starts a new stroke
    }

    [Fact]
    public void A_bale_drops_every_sixty_metres_of_new_cut_where_the_unloader_was_up_to_the_cap()
    {
        var f = new FieldSwath(Cfg, dropsBales: true);
        var total = 0;
        for (var i = 0; i < 29; i++) { var (a, b) = Seg(2 * i, 0, 2 * i + 2, 0); f.TryAdd(a, b, total); total += 2; } // 58 m
        Assert.Empty(f.Bales);
        { var (a, b) = Seg(58, 0, 60, 0); f.TryAdd(a, b, total); }
        Assert.Single(f.Bales); // 60 m, the header at x=60 heading east
        Assert.Equal(60f - Cfg.DroppedBaleBehindM, f.Bales[0].Pos.X, 1);
        Assert.Equal(-Cfg.DroppedBaleLeftM, f.Bales[0].Pos.Y, 1); // left of east is north (-z)
        // a long drive: capped
        var g = new FieldSwath(new HarvesterConfig { MaxSwathPoints = 100000, MaxSwathPointsTotal = 1000000 }, true);
        for (var i = 0; i < 2000; i++) { var (a, b) = Seg(2 * i, 0, 2 * i + 2, 0); g.TryAdd(a, b, 0); }
        Assert.Equal(24, g.Bales.Count);
        Assert.Empty(new FieldSwath(Cfg, dropsBales: false).Bales); // corn and potatoes drop none
    }

    [Fact]
    public void A_client_replaying_the_kept_strokes_derives_the_same_bales_even_after_a_fade()
    {
        var server = new FieldSwath(Cfg, true);
        var rng = new Random(5);
        float x = 0, z = 0, yaw = 0; var total = 0;
        for (var i = 0; i < 400; i++)
        {
            yaw += (float)(rng.NextDouble() - 0.5) * 0.5f;
            var nx = x + MathF.Cos(yaw) * 2.1f; var nz = z + MathF.Sin(yaw) * 2.1f;
            if (server.TryAdd(SwathPt.From(new Vector2(x, z)), SwathPt.From(new Vector2(nx, nz)), total)) total += 2;
            x = nx; z = nz;
        }
        Assert.NotEmpty(server.Bales);
        // the server sends each kept stroke packed; the client decodes and adds the segments
        var client = new FieldSwath(Cfg, true);
        foreach (var stroke in server.Strokes)
            foreach (var bytes in SwathCodec.Pack(3, 4.8f, 0, false, stroke))
            {
                var m = SwathCodec.Decode(bytes)!.Value;
                for (var i = 1; i < m.Points.Count; i++) client.Add(m.Points[i - 1], m.Points[i]);
            }
        Assert.Equal(server.Bales.Count, client.Bales.Count);
        for (var i = 0; i < server.Bales.Count; i++) Assert.Equal(server.Bales[i], client.Bales[i]);
        Assert.Equal(server.Metres, client.Metres, 2);
        server.FadeBales();
        Assert.Empty(server.Bales); Assert.Equal(0f, server.Metres);
        Assert.Equal(server.Strokes.Count, server.FirstCounted); // a late joiner is told the earlier strokes bring no bales
    }

    [Fact]
    public void The_history_is_bounded_and_packs_into_small_messages()
    {
        var cfg = new HarvesterConfig { MaxSwathPoints = 50 };
        var f = new FieldSwath(cfg, false);
        var kept = 0;
        for (var i = 0; i < 100; i++) if (f.TryAdd(SwathPt.From(new Vector2(2 * i, 0)), SwathPt.From(new Vector2(2 * i + 2, 0)), 0)) kept++;
        Assert.True(f.PointCount <= 50 && kept < 100);
        Assert.False(new FieldSwath(Cfg, false).TryAdd(new SwathPt(0, 0), new SwathPt(8, 0), Cfg.MaxSwathPointsTotal)); // all fields together are bounded too
        // a long stroke goes out in messages of at most 100 points; a jump inside it breaks it
        var stroke = Enumerable.Range(0, 250).Select(i => new SwathPt(i * 8, 0)).Append(new SwathPt(100000, 0)).Append(new SwathPt(100008, 0)).ToList();
        var msgs = SwathCodec.Pack(2, 4.8f, 7, false, stroke);
        Assert.All(msgs, m => Assert.True(m.Length <= SwathCodec.HeaderBytes + 2 * (SwathCodec.MaxPoints - 1)));
        var decoded = msgs.Select(m => SwathCodec.Decode(m)!.Value).ToList();
        Assert.Equal(250, decoded.Sum(m => m.Points.Count - 1)); // every segment but the jump, once
        Assert.Equal((ushort)7, decoded[0].AgeS); Assert.Equal(4.8f, decoded[0].WidthM, 1);
        Assert.Null(SwathCodec.Decode(new byte[5]));
    }

    [Fact]
    public void A_stretch_for_a_joiner_says_it_is_a_replay_and_a_live_one_does_not()
    {
        var stroke = new[] { new SwathPt(0, 0), new SwathPt(8, 0) };
        Assert.False(SwathCodec.Decode(SwathCodec.Pack(1, 4.8f, 0, false, stroke)[0])!.Value.Replay);
        var replay = SwathCodec.Decode(SwathCodec.Pack(1, 4.8f, 0, true, stroke, replay: true)[0])!.Value;
        Assert.True(replay.Replay); Assert.True(replay.NoBales); // the two flag bits are independent
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
        var out2 = new HarvesterCodec.FxMsg(HarvesterCodec.FxKind.BaleOut, new Vector3(1.5f, 2f, 3f), 41, 7); // the victim rides along (#351)
        Assert.Equal(out2, HarvesterCodec.DecodeFx(HarvesterCodec.EncodeFx(out2))!.Value);
        Assert.Equal((ushort)7, HarvesterCodec.DecodeFx(HarvesterCodec.EncodeFx(out2))!.Value.Victim);
        Assert.Null(HarvesterCodec.DecodeFx(new byte[15])); // the old, shorter message is refused
        Assert.True(HarvesterCodec.IsStaticBale(fb.Id));
        Assert.Equal(300, HarvesterCodec.StaticBaleIndex(fb.Id));
        Assert.False(HarvesterCodec.IsStaticBale(77));
        var dropped = HarvesterCodec.DroppedBaleId(23);
        Assert.True(HarvesterCodec.IsDroppedBale(dropped) && HarvesterCodec.IsWorldBale(dropped) && !HarvesterCodec.IsStaticBale(dropped));
        Assert.Equal(23, HarvesterCodec.DroppedBaleIndex(dropped));
        Assert.True(HarvesterCodec.IsWorldBale(HarvesterCodec.StaticBaleId(5)) && !HarvesterCodec.IsDroppedBale(HarvesterCodec.StaticBaleId(5)));
        Assert.False(HarvesterCodec.IsWorldBale(77));
        Assert.Null(HarvesterCodec.DecodeFx(new byte[HarvesterCodec.FxBytes].Select((_, i) => i == 0 ? (byte)99 : (byte)0).ToArray()));
    }

    // ---- the catch: the orbit and the throw (#351) ----

    [Fact]
    public void The_drawn_in_body_orbits_the_reel_axis_the_way_the_reel_turns()
    {
        const float r = 0.95f;
        Assert.Equal(new Vector3(0, r, 0), HarvesterCatch.OrbitOffset(0, r));                      // straight above the axis
        var front = HarvesterCatch.OrbitOffset(MathF.PI / 2, r);
        Assert.Equal(0f, front.Y, 4); Assert.Equal(-r, front.Z, 4);                                 // 90 degrees on: in front (the machine faces -z), the top moved forward
        Assert.Equal(-r, HarvesterCatch.OrbitOffset(MathF.PI, r).Y, 4);                             // then below
        Assert.All(Enumerable.Range(0, 40), i => Assert.Equal(r, HarvesterCatch.OrbitOffset(i * 0.37f, r).Length(), 4)); // always a radius away
        // 3.6 s at the reel's 3.4 rad/s is about two turns, not the one and a quarter of the old 1.2 s draw
        Assert.True(HarvesterCatch.OrbitAngle(1.8f, 3.4f, Cfg.DrawSeconds) - 1.8f > 2 * MathF.Tau * 0.95f);
        Assert.Equal(3.6f, Cfg.DrawSeconds);
        Assert.Equal(1f, Cfg.ChewSeconds); // the bale pops out a second after the body went in
        // the reel's axis is where the model has it: 3.66 m ahead, 0.81 m up
        Assert.Equal(3.66f, Cfg.HeaderForwardM); Assert.Equal(0.81f, Cfg.ReelAxisUpM);
    }

    [Fact]
    public void The_bale_is_thrown_back_left_and_up_on_top_of_the_machines_velocity_from_the_unloader()
    {
        var fwd = new Vector3(0, 0, -1); // facing -z: right is +x
        var v = HarvesterCatch.BaleVelocity(fwd, new Vector3(0, 0, -3), Cfg);
        Assert.Equal(-3f + Cfg.BaleThrowBackMps, v.Z, 3);   // backwards (+z) against the machine's own 3 m/s forward
        Assert.Equal(-Cfg.BaleThrowLeftMps, v.X, 3);        // to the left (-x)
        Assert.Equal(Cfg.BaleThrowUpMps, v.Y, 3);
        var at = HarvesterCatch.UnloaderAt(new Vector3(10, 0, 20), fwd, 4.3f, Cfg);
        Assert.Equal(10 - Cfg.DroppedBaleLeftM, at.X, 3); Assert.Equal(20 + 4.3f + 1.6f, at.Z, 3); Assert.Equal(Cfg.UnloaderHeightM, at.Y, 3);
    }

    [Fact]
    public void A_thrown_bale_flies_lands_rolls_with_friction_and_stops_where_its_hay_bale_ends()
    {
        var cfg = Cfg;
        var bale = new Corpse(HarvesterRules.BaleOwner, new Vector3(0, 1.6f, 0), 0, 0, 0, null, cfg.BaleLifetimeS);
        var hay = new HayBale(bale.Position, cfg);
        bale.Launch(HarvesterCatch.BaleVelocity(new Vector3(0, 0, -1), Vector3.Zero, cfg), rolls: true);
        Assert.True(bale.Airborne);
        Assert.Equal(CorpseRules.FlightPitch, bale.Serialize().PitchRad, 1);
        var sawRoll = false; var lastZ = bale.Position.Z; var steps = 0;
        for (; steps < 600 && (bale.Airborne || bale.Rolling); steps++)
        {
            bale.Fly(1f / 30, Flat);
            if (bale.Rolling)
            {
                sawRoll = true;
                Assert.Equal(CorpseRules.RollPitch, bale.Serialize().PitchRad, 2); // replicated as rolling
                Assert.True(bale.Position.Z >= lastZ - 1e-4f);                      // it keeps going backwards (+z), never back
                Assert.InRange(bale.Position.Y, -0.01f, 0.01f);                     // on the ground
            }
            lastZ = bale.Position.Z;
            hay.MoveTo(bale.Position);
        }
        Assert.True(sawRoll); Assert.True(steps < 600);
        Assert.False(bale.Airborne); Assert.False(bale.Rolling);
        Assert.Equal(0f, bale.Serialize().PitchRad);
        Assert.Equal(Vector3.Zero, bale.Velocity);
        Assert.Equal(bale.Position, hay.Position);
        // it flew about 0.7 s at 7 m/s back (about 5 m), then rolled v^2 / (2 * friction) more (about 4 m): well past where it landed
        Assert.InRange(bale.Position.Z, 8f, 12f);
    }

    [Fact]
    public void A_rolling_bale_stops_dead_against_a_wall_and_flies_on_when_the_ground_ends()
    {
        // a wall 3 m behind the landing: stops at it, never through
        float? WallAt(Vector3 from, Vector3 dir, float len) => dir.Z > 0.5f && 6f - from.Z is var d && d >= 0 && d <= len ? d : Flat(from, dir, len);
        var b = new ThrownBody(new Vector3(0, 0.3f, 0));
        b.Launch(new Vector3(0, 1, 8), rolls: true);
        for (var i = 0; i < 600 && (b.Airborne || b.Rolling); i++) b.Fly(1f / 30, WallAt);
        Assert.False(b.Rolling);
        Assert.True(b.Position.Z <= 6f, $"{b.Position.Z:F2}");
        // a ledge at z = 5: the ground is gone beyond it, the rolling body drops
        float? Ledge(Vector3 from, Vector3 dir, float len) => dir.Y < -0.5f ? (from.Z > 5f ? null : Flat(from, dir, len)) : Flat(from, dir, len);
        var c = new ThrownBody(new Vector3(0, 0.3f, 0));
        c.Launch(new Vector3(0, 1, 12), rolls: true);
        var dropped = false;
        for (var i = 0; i < 300 && (c.Airborne || c.Rolling); i++)
        {
            var was = c.Rolling;
            c.Fly(1f / 30, Ledge);
            if (was && c.Airborne) dropped = true;
            if (dropped) break;
        }
        Assert.True(dropped);
    }

    [Fact]
    public void Without_rolls_a_thrown_body_still_stops_dead_on_landing()
    {
        var b = new ThrownBody(new Vector3(0, 0.3f, 0));
        b.Launch(new Vector3(0, 1, 8));
        for (var i = 0; i < 300 && b.Airborne; i++) b.Fly(1f / 30, Flat);
        Assert.False(b.Rolling); Assert.False(b.Airborne);
    }

    /// <summary>The ground at y = 0 (a ray cast down from above, or sideways: nothing).</summary>
    static float? Flat(Vector3 from, Vector3 dir, float len)
    {
        if (dir.Y > -0.5f) return null;
        var d = from.Y / -dir.Y;
        return d >= 0 && d <= len ? d : null;
    }

    // ---- the vehicle ----

    [Fact]
    public void The_harvester_is_a_slow_open_cab_machine_with_its_own_run_over()
    {
        var def = Harvester;
        Assert.False(def.RunOver); // NetServer.Harvester.cs runs it over at field speed
        Assert.True(def.Seats[0].Exposed && def.Seats[0].Role == "driver"); // a shot reaches the driver
        Assert.True(def.TurnRadius <= Cfg.TurnRadiusM);
        Assert.StartsWith("pack:synty-vehicles/harvester", def.Model); // standardized (#325)
        Assert.True(def.TopSpeed > Cfg.TransitSpeedMps);
        Assert.Equal(0f, def.ModelYawDeg); // the standardizer turned the prefab (Unity forward is +Z) to forward -Z
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
