using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Scud truck: its launcher machine, the missile's planned flight, the blast numbers, scud.json and the wire.</summary>
public class ScudTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly ScudFile Shipped = ScudFile.Load(Path.Combine(DataDir, "scud.json"));
    static ScudLauncherData T => Shipped.Launcher;

    const ushort Op = 7, Drv = 8, Other = 9;
    static readonly Vector3 Truck = new(100, 5, 100);
    static readonly Vector3 Far = new(100, 5, 600);   // 500 m away
    static readonly Vector3 Near = new(100, 5, 200);  // 100 m away: inside the minimum range

    /// <summary>Seat 0 is the driver, seat 1 the operator.</summary>
    static ScudCrew Crew(ushort driver, ushort op) => new(new ushort[] { driver, op }, 1);
    static ScudCrew Solo => Crew(0, Op);
    static ScudCrew Pair => Crew(Drv, Op);

    static ScudLauncher Launcher() => new(T);

    static void Run(ScudLauncher l, ScudCrew crew, float seconds, Vector3? truck = null)
    {
        for (var t = 0f; t < seconds; t += 0.1f) l.Tick(0.1f, crew, truck ?? Truck);
    }

    /// <summary>Open, lock and confirm as a solo operator: erecting.</summary>
    static ScudLauncher Erecting(ScudCrew? crew = null)
    {
        var c = crew ?? Solo;
        var l = Launcher();
        Assert.Equal(ScudAnswer.Ok, l.Open(c, Op));
        Assert.Equal(ScudAnswer.Ok, l.PlaceLock(c, Op, Far, Truck, true));
        Assert.Equal(ScudAnswer.Ok, l.Confirm(c, Op, 0));
        if (c.OccupiedMask != Solo.OccupiedMask) Assert.Equal(ScudAnswer.Ok, l.Confirm(c, Drv, 0));
        Assert.Equal(ScudLauncherState.Erecting, l.State);
        return l;
    }

    // ---- the machine ----

    [Fact]
    public void A_solo_player_locks_and_confirms_from_the_operator_seat_alone()
    {
        var l = Launcher();
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.Equal(ScudAnswer.Ok, l.Open(Solo, Op));
        Assert.Equal(ScudLauncherState.Targeting, l.State);
        Assert.Equal(ScudAnswer.Ok, l.PlaceLock(Solo, Op, Far, Truck, true));
        Assert.Equal(ScudLauncherState.Proposed, l.State); // the lock is the proposal: it does not launch by itself
        Assert.Equal(Far, l.Lock);
        Assert.Equal(ScudAnswer.Ok, l.Confirm(Solo, Op, 0));
        Assert.Equal(ScudLauncherState.Erecting, l.State);
    }

    [Fact]
    public void With_a_driver_aboard_both_seats_must_confirm()
    {
        var l = Launcher();
        l.Open(Pair, Op);
        l.PlaceLock(Pair, Op, Far, Truck, true);
        Assert.Equal(0b11, l.Need);
        l.Confirm(Pair, Op, 0);
        Assert.Equal(ScudLauncherState.Proposed, l.State);
        Assert.Equal(0b10, l.Confirmed);
        l.Confirm(Pair, Drv, 0);
        Assert.Equal(ScudLauncherState.Erecting, l.State);
    }

    [Fact]
    public void Only_the_operator_opens_the_screen_locks_and_aborts()
    {
        var l = Launcher();
        Assert.Equal(ScudAnswer.NotOperator, l.Open(Pair, Drv));
        Assert.Equal(ScudAnswer.NotAboard, l.Open(Pair, Other));
        l.Open(Pair, Op);
        Assert.Equal(ScudAnswer.NotOperator, l.PlaceLock(Pair, Drv, Far, Truck, true));
        Assert.Equal(ScudAnswer.NotOperator, l.Close(Pair, Drv));
        var e = Erecting(Pair);
        Assert.Equal(ScudAnswer.NotOperator, e.Abort(Pair, Drv));
    }

    [Fact]
    public void A_point_inside_the_minimum_range_or_off_the_map_is_refused()
    {
        var l = Launcher();
        l.Open(Solo, Op);
        Assert.Equal(ScudAnswer.TooClose, l.PlaceLock(Solo, Op, Near, Truck, true));
        Assert.Equal(ScudAnswer.OffMap, l.PlaceLock(Solo, Op, Far, Truck, false));
        Assert.Equal(ScudLauncherState.Targeting, l.State);
        Assert.Null(l.Lock);
        // exactly at the minimum range is allowed
        Assert.Equal(ScudAnswer.Ok, l.PlaceLock(Solo, Op, new Vector3(100, 5, 100 + (float)T.MinRangeM), Truck, true));
    }

    [Fact]
    public void A_moving_truck_does_not_start_erecting()
    {
        var l = Launcher();
        l.Open(Solo, Op);
        l.PlaceLock(Solo, Op, Far, Truck, true);
        Assert.Equal(ScudAnswer.Moving, l.Confirm(Solo, Op, 6f));
        Assert.Equal(ScudLauncherState.Proposed, l.State);
        Assert.Equal(0, l.Confirmed);
        Assert.Equal(ScudAnswer.Ok, l.Confirm(Solo, Op, 0.1f));
        Assert.Equal(ScudLauncherState.Erecting, l.State);
    }

    [Fact]
    public void Closing_the_screen_drops_the_lock()
    {
        var l = Launcher();
        l.Open(Solo, Op);
        Assert.Equal(ScudAnswer.Ok, l.Close(Solo, Op));
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        l.Open(Solo, Op);
        l.PlaceLock(Solo, Op, Far, Truck, true);
        Assert.Equal(ScudAnswer.Ok, l.Close(Solo, Op));
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.Null(l.Lock);
    }

    [Fact]
    public void The_operator_leaving_the_seat_closes_the_screen()
    {
        var l = Launcher();
        l.Open(Pair, Op);
        l.Tick(0.1f, Crew(Drv, 0), Truck);
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        l.Open(Pair, Op);
        l.Tick(0.1f, Crew(Op, Drv), Truck); // they swapped seats: someone else is the operator now
        Assert.Equal(ScudLauncherState.Stowed, l.State);
    }

    public static IEnumerable<object[]> Cancels()
    {
        yield return new object[] { "a passenger left", ScudCancel.CrewChanged };
        yield return new object[] { "a passenger sat down", ScudCancel.CrewChanged };
        yield return new object[] { "two swapped seats", ScudCancel.CrewChanged };
        yield return new object[] { "the truck moved", ScudCancel.Moved };
        yield return new object[] { "a confirmer withdrew", ScudCancel.Withdrawn };
        yield return new object[] { "the operator locked again", ScudCancel.Retarget };
        yield return new object[] { "nobody confirmed in time", ScudCancel.Timeout };
    }

    [Theory]
    [MemberData(nameof(Cancels))]
    public void A_proposal_is_cancelled_back_to_targeting_while_the_operator_is_at_the_screen(string what, ScudCancel why)
    {
        var l = Launcher();
        var dropped = new List<ScudCancel>();
        l.ProposalDropped += dropped.Add;
        var crew = what == "a passenger sat down" ? Solo : Pair;
        l.Open(crew, Op);
        l.PlaceLock(crew, Op, Far, Truck, true);
        if (what != "a passenger sat down") l.Confirm(crew, Op, 0); // a solo operator's confirmation would be the whole of it
        switch (what)
        {
            case "a passenger left": l.Tick(0.1f, Crew(0, Op), Truck); break;
            case "a passenger sat down": l.Tick(0.1f, Pair, Truck); break;
            case "two swapped seats": l.Tick(0.1f, new ScudCrew(new ushort[] { Op, Drv }, 1), Truck); break; // the operator is Drv now
            case "the truck moved": l.Tick(0.1f, crew, Truck + new Vector3(3, 0, 0)); break;
            case "a confirmer withdrew": Assert.Equal(ScudAnswer.Ok, l.Withdraw(crew, Op)); break;
            case "the operator locked again": Assert.Equal(ScudAnswer.Ok, l.PlaceLock(crew, Op, new Vector3(0, 5, 400), Truck, true)); break;
            case "nobody confirmed in time": Run(l, crew, (float)T.ProposalTimeoutSeconds + 1); break;
        }
        Assert.Equal(new[] { why }, dropped.ToArray());
        // a re-lock proposes the new point at once; every other cancel is back at the screen with no lock and no confirmations
        if (why == ScudCancel.Retarget)
        {
            Assert.Equal(ScudLauncherState.Proposed, l.State);
            Assert.Equal(0, l.Confirmed);
            Assert.Equal(new Vector3(0, 5, 400), l.Lock);
        }
        else if (what == "two swapped seats") Assert.Equal(ScudLauncherState.Stowed, l.State); // the operator left their seat: nobody has the screen
        else
        {
            Assert.Equal(ScudLauncherState.Targeting, l.State);
            Assert.Null(l.Lock);
            Assert.Equal(0, l.Confirmed);
        }
    }

    [Fact]
    public void A_proposal_whose_operator_left_goes_back_to_stowed()
    {
        var l = Launcher();
        l.Open(Pair, Op);
        l.PlaceLock(Pair, Op, Far, Truck, true);
        l.Tick(0.1f, Crew(Drv, 0), Truck);
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.Null(l.Lock);
        Assert.Equal(0, l.Operator);
    }

    [Fact]
    public void Withdrawing_without_having_confirmed_does_nothing()
    {
        var l = Launcher();
        l.Open(Pair, Op);
        l.PlaceLock(Pair, Op, Far, Truck, true);
        Assert.Equal(ScudAnswer.WrongState, l.Withdraw(Pair, Drv));
        Assert.Equal(ScudLauncherState.Proposed, l.State);
    }

    [Fact]
    public void Erecting_takes_its_time_immobilises_and_then_ignites()
    {
        var l = Erecting();
        Assert.True(l.Immobilised);
        Run(l, Solo, (float)T.ErectSeconds - 0.5f);
        Assert.Equal(ScudLauncherState.Erecting, l.State);
        Run(l, Solo, 0.7f);
        Assert.Equal(ScudLauncherState.Launching, l.State);
        Assert.Equal(MathF.PI / 2, l.RackAngle, 3);
    }

    [Fact]
    public void The_launch_comes_when_the_ignition_ends_and_carries_the_point_and_the_placer()
    {
        var l = Erecting();
        Run(l, Solo, (float)T.ErectSeconds + 0.2f);
        Assert.Equal(ScudLauncherState.Launching, l.State);
        ScudTick launch = default;
        for (var t = 0f; t < T.IgnitionSeconds + 0.5f && !launch.Launch; t += 0.1f) launch = l.Tick(0.1f, Solo, Truck);
        Assert.True(launch.Launch);
        Assert.Equal(Far, launch.Target);
        Assert.Equal(Op, launch.Placer);
        Assert.Equal(ScudLauncherState.Spent, l.State);
        // the launch is reported once
        Assert.False(l.Tick(0.1f, Solo, Truck).Launch);
    }

    [Fact]
    public void After_the_launch_the_rack_lowers_and_the_truck_is_empty_until_reloaded()
    {
        var l = Erecting();
        Run(l, Solo, (float)(T.ErectSeconds + T.IgnitionSeconds) + 0.5f);
        Assert.Equal(ScudLauncherState.Spent, l.State);
        Run(l, Solo, (float)T.SpentHoldSeconds + 0.3f);
        Assert.Equal(ScudLauncherState.Lowering, l.State);
        Assert.True(l.Immobilised);
        Run(l, Solo, (float)T.LowerSeconds + 0.3f);
        Assert.Equal(ScudLauncherState.Empty, l.State);
        Assert.False(l.Immobilised);
        Assert.Equal(0f, l.RackAngle);
        // empty: the screen is not available to fire
        Assert.Equal(ScudAnswer.WrongState, l.Open(Solo, Op));
        Assert.True(l.BeginReload());
        Assert.Equal(ScudLauncherState.Reloading, l.State);
        Assert.False(l.BeginReload()); // already loading
        Run(l, Solo, (float)T.ReloadSeconds + 0.3f);
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.Equal(ScudAnswer.Ok, l.Open(Solo, Op)); // ready to fire again
    }

    [Fact]
    public void The_operator_can_abort_the_erect_and_the_rocket_is_kept()
    {
        var l = Erecting();
        Run(l, Solo, 4f);
        var half = l.RackAngle;
        Assert.InRange(half, 0.1f, MathF.PI / 2 - 0.1f);
        Assert.Equal(ScudAnswer.Ok, l.Abort(Solo, Op));
        Assert.Equal(ScudLauncherState.Aborting, l.State);
        Assert.Null(l.Lock);
        Assert.Equal(half, l.RackFrom, 3);
        Assert.True(l.Immobilised);
        // it lowers at the lowering rate from where it got to, not for the whole lowering time
        Run(l, Solo, (float)T.LowerSeconds * half / (MathF.PI / 2) + 0.4f);
        Assert.Equal(ScudLauncherState.Stowed, l.State);
        Assert.Equal(0f, l.RackAngle);
        Assert.False(l.Immobilised);
        Assert.Equal(ScudAnswer.Ok, l.Open(Solo, Op)); // the rocket is still there
    }

    [Fact]
    public void The_ignition_cannot_be_aborted()
    {
        var l = Erecting();
        Run(l, Solo, (float)T.ErectSeconds + 0.3f);
        Assert.Equal(ScudLauncherState.Launching, l.State);
        Assert.Equal(ScudAnswer.WrongState, l.Abort(Solo, Op));
    }

    [Fact]
    public void Leaving_the_seats_does_not_stop_a_rack_that_is_rising()
    {
        var l = Erecting(Pair);
        Run(l, Crew(0, 0), (float)T.ErectSeconds + 0.3f);
        Assert.Equal(ScudLauncherState.Launching, l.State);
    }

    [Fact]
    public void An_admin_reload_is_instant_from_empty_or_loading_and_otherwise_refused()
    {
        var l = Launcher();
        Assert.False(l.ReloadNow()); // stowed already
        var e = Erecting();
        Assert.False(e.ReloadNow());
        Run(e, Solo, (float)(T.ErectSeconds + T.IgnitionSeconds + T.SpentHoldSeconds + T.LowerSeconds) + 1f);
        Assert.Equal(ScudLauncherState.Empty, e.State);
        Assert.True(e.ReloadNow());
        Assert.Equal(ScudLauncherState.Stowed, e.State);
        e.Tick(0.1f, Solo, Truck);
        Assert.Equal(ScudLauncherState.Stowed, e.State);
    }

    [Fact]
    public void The_spec_declares_every_edge_the_machine_takes()
    {
        // run every path of the tests above under the strict spec (an undeclared transition throws): one pass that touches every edge
        var edges = new HashSet<(ScudLauncherState, ScudLauncherState)>();
        var l = Launcher();
        l.Changed += (a, b) => edges.Add((a, b));
        var crew = Pair;
        void Full(bool abort)
        {
            l.Open(crew, Op); l.PlaceLock(crew, Op, Far, Truck, true);
            l.Withdraw(crew, Op); // nobody confirmed yet: nothing
            l.Confirm(crew, Op, 0); l.Withdraw(crew, Op); // Proposed to Targeting
            l.PlaceLock(crew, Op, Far, Truck, true); l.Close(crew, Op); // Proposed to Stowed
            l.Open(crew, Op); l.Close(crew, Op); // Targeting to Stowed
            l.Open(crew, Op); l.PlaceLock(crew, Op, Far, Truck, true); l.Confirm(crew, Op, 0); l.Confirm(crew, Drv, 0);
            if (abort) { Run(l, crew, 2); l.Abort(crew, Op); Run(l, crew, (float)T.LowerSeconds + 1); return; }
            Run(l, crew, (float)(T.ErectSeconds + T.IgnitionSeconds + T.SpentHoldSeconds + T.LowerSeconds) + 1);
        }
        Full(abort: true);
        Full(abort: false);
        l.BeginReload(); Run(l, crew, (float)T.ReloadSeconds + 1);
        Full(abort: false);
        l.BeginLoad(1, Truck, Truck); l.Tick(0.1f, crew, Truck, null); // Loading to Empty: the cart was lost
        l.BeginLoad(1, Truck, Truck);
        for (var t = 0f; t < T.ReloadSeconds + 1; t += 0.1f) l.Tick(0.1f, crew, Truck, new ScudCartSense(true, Truck)); // Loading to Reloading to Stowed
        Full(abort: false);
        l.ReloadNow();
        foreach (var (a, b) in edges) Assert.True(ScudLauncher.Spec.Allows(a, b), $"{a} -> {b} is not declared");
        // and the declared edges are all taken somewhere (the machine has no dead declaration)
        foreach (var (a, b) in ScudLauncher.Spec.TransitionNames.Select(t => (Enum.Parse<ScudLauncherState>(t.From), Enum.Parse<ScudLauncherState>(t.To)))) Assert.Contains((a, b), edges);
    }

    // ---- the rack ----

    [Fact]
    public void The_rack_angle_runs_from_flat_to_upright_smoothly_and_back()
    {
        float Angle(ScudLauncherState s, float t, float from = 0) => ScudRules.RackAngle(s, t, from, T);
        Assert.Equal(0f, Angle(ScudLauncherState.Stowed, 0));
        Assert.Equal(0f, Angle(ScudLauncherState.Erecting, 0));
        Assert.Equal(MathF.PI / 2, Angle(ScudLauncherState.Erecting, (float)T.ErectSeconds), 4);
        var last = -1f;
        for (var t = 0f; t <= T.ErectSeconds; t += 0.25f) { var a = Angle(ScudLauncherState.Erecting, t); Assert.True(a >= last); last = a; }
        Assert.Equal(MathF.PI / 2, Angle(ScudLauncherState.Launching, 1));
        Assert.Equal(MathF.PI / 2, Angle(ScudLauncherState.Spent, 1));
        Assert.Equal(MathF.PI / 2, Angle(ScudLauncherState.Lowering, 0), 4);
        Assert.Equal(0f, Angle(ScudLauncherState.Lowering, (float)T.LowerSeconds), 4);
        Assert.Equal(0.6f, Angle(ScudLauncherState.Aborting, 0, 0.6f), 4);
        Assert.Equal(0f, Angle(ScudLauncherState.Aborting, ScudRules.LowerSeconds(0.6f, T), 0.6f), 4);
        Assert.Equal(0f, Angle(ScudLauncherState.Empty, 3));
    }

    [Fact]
    public void The_rocket_shows_on_the_rack_until_the_launch_and_again_late_in_a_reload()
    {
        foreach (var s in new[] { ScudLauncherState.Stowed, ScudLauncherState.Targeting, ScudLauncherState.Proposed, ScudLauncherState.Erecting, ScudLauncherState.Launching, ScudLauncherState.Aborting })
            Assert.True(ScudRules.RocketOnRack(s, 0, T), s.ToString());
        foreach (var s in new[] { ScudLauncherState.Spent, ScudLauncherState.Lowering, ScudLauncherState.Empty })
            Assert.False(ScudRules.RocketOnRack(s, 5, T), s.ToString());
        // the clock LEFT decides in a reload: the admin's starts with all of it, one from a cart with the second half
        Assert.False(ScudRules.RocketOnRack(ScudLauncherState.Reloading, (float)T.ReloadSeconds, T));
        Assert.True(ScudRules.RocketOnRack(ScudLauncherState.Reloading, (float)T.ReloadSeconds * 0.4f, T));
        Assert.True(ScudRules.RocketOnRack(ScudLauncherState.Reloading, (float)T.ReloadSeconds * 0.5f, T));
        Assert.False(ScudRules.RocketOnRack(ScudLauncherState.Loading, (float)T.ReloadSeconds * 0.4f, T)); // still on the cart
    }

    [Fact]
    public void Only_the_states_with_the_rack_moving_or_a_reload_immobilise_the_truck()
    {
        foreach (var s in Enum.GetValues<ScudLauncherState>())
        {
            var want = s is ScudLauncherState.Erecting or ScudLauncherState.Launching or ScudLauncherState.Spent or ScudLauncherState.Lowering or ScudLauncherState.Aborting or ScudLauncherState.Reloading or ScudLauncherState.Loading;
            Assert.Equal(want, ScudRules.Immobilises(s));
        }
    }

    // ---- the flight ----

    static ScudFlightPlan Plan(Vector3 from, Vector3 to) => new(from, to, Shipped.Missile, (float)T.MinRangeM);

    [Theory]
    [InlineData(150f)]
    [InlineData(600f)]
    [InlineData(2000f)]
    [InlineData(4000f)]
    [InlineData(9000f)]
    public void The_missile_reaches_the_point_on_time_after_a_vertical_boost(float range)
    {
        var from = new Vector3(100, 20, 100);
        var to = new Vector3(100 + range * 0.8f, 5, 100 + range * 0.6f);
        var p = Plan(from, to);
        var m = Shipped.Missile;
        Assert.InRange(p.Seconds, m.FlightMinSeconds, m.FlightMaxSeconds);
        Assert.Equal(from, p.At(0));
        var end = p.At(p.Seconds);
        Assert.True(Vector3.Distance(end, to) < 0.01f, $"{end} vs {to}");
        // the boost: straight up, ending at the boost altitude
        var top = p.At((float)m.BoostSeconds);
        Assert.Equal(from.X, top.X, 2); Assert.Equal(from.Z, top.Z, 2);
        Assert.Equal(from.Y + (float)m.BoostAltitudeM, top.Y, 2);
        Assert.Equal(from.X, p.At((float)m.BoostSeconds * 0.5f).X, 2);
        // climbing in the boost
        var (_, pitch0) = p.HeadingAt(0.5f);
        Assert.True(pitch0 > 1.3f, $"pitch {pitch0}");
    }

    [Theory]
    [InlineData(150f)]
    [InlineData(2000f)]
    [InlineData(6000f)]
    public void The_apex_is_above_the_boost_and_the_missile_dives_onto_the_target(float range)
    {
        var from = new Vector3(0, 10, 0);
        var to = new Vector3(range, 0, 0);
        var p = Plan(from, to);
        var m = Shipped.Missile;
        Assert.True(p.Apex.Y > from.Y + (float)m.BoostAltitudeM, $"apex {p.Apex.Y}");
        Assert.InRange(p.ApexSeconds, (float)m.BoostSeconds, p.Seconds - 1f);
        // past the apex it only falls
        var last = p.Apex.Y;
        for (var t = p.ApexSeconds; t <= p.Seconds; t += 0.25f) { var y = p.At(t).Y; Assert.True(y <= last + 1e-3f, $"rises again at {t}"); last = y; }
        // it arrives nose down at about the dive angle, heading at the target
        var (yaw, pitch) = p.HeadingAt(p.Seconds - 0.06f);
        Assert.InRange(pitch, -(float)(m.DiveDeg + 6) * MathF.PI / 180, -(float)(m.DiveDeg - 6) * MathF.PI / 180);
        var fwd = HitMath.Forward(yaw, pitch);
        Assert.True(fwd.X > 0.1f, "points along +x, at the target");
        Assert.Equal(ScudPhase.Boost, p.PhaseAt(1));
        Assert.Equal(ScudPhase.Arc, p.PhaseAt((float)m.BoostSeconds + 0.1f));
        Assert.Equal(ScudPhase.Dive, p.PhaseAt(p.Seconds - 0.5f));
    }

    [Fact]
    public void The_flight_gets_longer_with_the_range_between_the_two_limits()
    {
        var near = Plan(Vector3.Zero, new Vector3(150, 0, 0)).Seconds;
        var mid = Plan(Vector3.Zero, new Vector3(2000, 0, 0)).Seconds;
        var far = Plan(Vector3.Zero, new Vector3(5000, 0, 0)).Seconds;
        Assert.Equal((float)Shipped.Missile.FlightMinSeconds, near, 3);
        Assert.Equal((float)Shipped.Missile.FlightMaxSeconds, far, 3);
        Assert.InRange(mid, near, far);
        Assert.True(near < mid && mid < far);
    }

    [Fact]
    public void The_path_has_no_jump_where_the_boost_hands_over_to_the_arc()
    {
        var p = Plan(Vector3.Zero, new Vector3(3000, 0, 0));
        var tb = p.BoostSeconds;
        var before = p.At(tb - 0.05f); var after = p.At(tb + 0.05f);
        var speed = Vector3.Distance(before, after) / 0.1f;
        var boostSpeed = 2f * (float)Shipped.Missile.BoostAltitudeM / tb;
        Assert.InRange(speed, boostSpeed * 0.9f, boostSpeed * 1.1f);
        // no frame of the path is faster than a plausible bound (no teleports)
        var last = p.At(0);
        for (var t = 0.1f; t <= p.Seconds; t += 0.1f) { var q = p.At(t); Assert.True(Vector3.Distance(last, q) < 150f, $"jump at {t}"); last = q; }
    }

    [Fact]
    public void The_missile_entity_follows_its_plan_and_impacts_once()
    {
        var plan = Plan(new Vector3(0, 0, 0), new Vector3(1000, 0, 0));
        var hits = new List<Vector3>();
        var e = new ScudMissileEntity(Op, plan, m => hits.Add(m.Position));
        Assert.Equal(ScudMissileRules.TypeId, e.TypeId);
        e.Tick(1f);
        Assert.Null(e.Ended);
        var s = e.Serialize();
        Assert.Equal(Op, s.Owner);
        Assert.Equal((byte)ScudPhase.Boost, s.State);
        Assert.True(s.Position.Y > 0);
        e.OnDamage(1_000_000f, 3); // it cannot be shot down
        Assert.Null(e.Ended);
        for (var t = 0f; t < plan.Seconds + 1 && e.Ended == null; t += 0.05f) e.Tick(0.05f);
        Assert.Equal(WorldDespawnReason.Destroyed, e.Ended);
        Assert.Single(hits);
        Assert.True(Vector3.Distance(hits[0], new Vector3(1000, 0, 0)) < 0.01f);
        e.Tick(1f);
        Assert.Single(hits);
    }

    [Fact]
    public void The_missile_entity_registers_as_a_world_entity_that_is_not_filtered_by_interest()
    {
        var reg = new WorldEntityRegistry();
        ScudMissileRules.Register(reg);
        ScudMissileRules.Register(reg); // idempotent on a shared registry
        var def = reg.Find(ScudMissileRules.TypeId);
        Assert.NotNull(def);
        Assert.False(def!.Interest);
        Assert.False(def.HasHp);
        var manager = new WorldEntityManager(reg);
        var plan = Plan(Vector3.Zero, new Vector3(1000, 0, 0));
        var id = manager.Spawn(new ScudMissileEntity(Op, plan, _ => { }));
        Assert.NotNull(id);
        for (var i = 0; i < 40 * 30; i++) manager.Tick(1f / 30f);
        Assert.Equal(0, manager.Count); // it despawned itself at the impact
    }

    // ---- the blast ----

    [Fact]
    public void Inside_the_lethal_radius_everybody_dies_and_beyond_it_the_damage_falls_off()
    {
        var b = Shipped.Blast;
        Assert.True(ScudBlastRules.PlayerHit(b, 0).Lethal);
        Assert.True(ScudBlastRules.PlayerHit(b, (float)b.LethalRadiusM).Lethal);
        var just = ScudBlastRules.PlayerHit(b, (float)b.LethalRadiusM + 0.5f);
        Assert.False(just.Lethal);
        Assert.InRange(just.Damage, b.OuterDamage - 2, b.OuterDamage);
        var last = float.MaxValue;
        for (var d = (float)b.LethalRadiusM + 1f; d < b.DamageRadiusM; d += 5f)
        {
            var hit = ScudBlastRules.PlayerHit(b, d);
            Assert.True(hit.Damage <= last && hit.Damage > 0, $"{d} m");
            last = hit.Damage;
        }
        Assert.InRange(ScudBlastRules.PlayerHit(b, (float)b.DamageRadiusM - 1f).Damage, b.EdgeDamage, b.EdgeDamage + 2);
        Assert.False(ScudBlastRules.PlayerHit(b, (float)b.DamageRadiusM).Hurts);
        Assert.False(ScudBlastRules.PlayerHit(b, 1000).Hurts);
    }

    [Fact]
    public void A_vehicle_in_the_destroy_radius_is_doomed_and_beyond_it_loses_hit_points_by_distance()
    {
        var b = Shipped.Blast;
        const float hp = 1500;
        Assert.Equal(hp + 1, ScudBlastRules.VehicleDamage(b, 0, hp));
        Assert.Equal(hp + 1, ScudBlastRules.VehicleDamage(b, (float)b.VehicleDestroyRadiusM, hp));
        var near = ScudBlastRules.VehicleDamage(b, (float)b.VehicleDestroyRadiusM + 1, hp);
        var far = ScudBlastRules.VehicleDamage(b, (float)b.VehicleDamageRadiusM - 1, hp);
        Assert.InRange(near, b.VehicleOuterDamage - 15, b.VehicleOuterDamage);
        Assert.InRange(far, b.VehicleEdgeDamage, b.VehicleEdgeDamage + 15);
        Assert.True(near > far);
        Assert.Equal(0f, ScudBlastRules.VehicleDamage(b, (float)b.VehicleDamageRadiusM, hp));
    }

    [Fact]
    public void The_shake_is_felt_across_the_map_and_falls_with_the_distance()
    {
        var b = Shipped.Blast;
        Assert.Equal((float)b.ShakeAmplitude, ScudBlastRules.Shake(b, 0), 4);
        Assert.True(ScudBlastRules.Shake(b, 1000) > 0.01f);
        Assert.True(ScudBlastRules.Shake(b, 1000) < ScudBlastRules.Shake(b, 200));
        Assert.Equal(0f, ScudBlastRules.Shake(b, (float)b.ShakeRangeM + 1));
        Assert.True(ScudBlastRules.ArrivalSeconds(b, 2600) > ScudBlastRules.ArrivalSeconds(b, 260));
    }

    [Fact]
    public void The_shipped_blast_is_the_biggest_in_the_game()
    {
        var b = Shipped.Blast;
        Assert.True(b.FxRadiusM > 40, "bigger than the MOAB's blast"); // NetClient.MoabBlastRadius is 40 m
        Assert.True(b.GlassRadiusM >= 250);
        Assert.True(b.LethalRadiusM >= 60);
        Assert.True(DustRules.HasFront((float)b.FxRadiusM));
    }

    // ---- the data ----

    [Fact]
    public void The_shipped_file_loads_and_equals_the_defaults()
    {
        var d = ScudFile.Default;
        Assert.Equal(d.Launcher.ErectSeconds, Shipped.Launcher.ErectSeconds);
        Assert.Equal(d.Launcher.MinRangeM, Shipped.Launcher.MinRangeM);
        Assert.Equal(d.Missile.BoostAltitudeM, Shipped.Missile.BoostAltitudeM);
        Assert.Equal(d.Blast.LethalRadiusM, Shipped.Blast.LethalRadiusM);
        Assert.Equal(150, Shipped.Launcher.MinRangeM);
        Assert.Equal(8, Shipped.Launcher.ErectSeconds);
    }

    [Fact]
    public void The_game_data_carries_scud_json_and_the_truck_is_a_launcher_that_never_returns()
    {
        var data = GameData.Load(DataDir);
        Assert.Equal(60, data.Scud.Blast.LethalRadiusM);
        var truck = data.Vehicles.Vehicles.Single(v => v.Id == "scud");
        Assert.NotNull(truck.Launcher);
        Assert.False(truck.Returns);
        Assert.Equal(0, truck.SpawnCount);
        Assert.Empty(truck.FixedSpots);
        Assert.Null(truck.Tow); // it cannot tow
        Assert.Equal(2, truck.Seats.Count);
        Assert.Equal("driver", truck.Seats[0].Role);
        Assert.Equal(1, truck.Launcher!.OperatorSeat);
        Assert.False(truck.SeatExposed(0)); Assert.False(truck.SeatExposed(1)); // both under the cab
        Assert.True(truck.Hp >= 1000);
        Assert.Equal("pack:synty-simple-military/scud", truck.Model);
        // every other type returns, as before
        Assert.All(data.Vehicles.Vehicles.Where(v => v.Id is not ("scud" or "rocket_cart")), v => Assert.True(v.Returns)); // the cart is the event's to replace
    }

    [Theory]
    [InlineData("""{"version":1,"launcher":{"erect_seconds":0}}""", "launcher.erect_seconds")]
    [InlineData("""{"version":1,"launcher":{"min_range_m":-1}}""", "launcher.min_range_m")]
    [InlineData("""{"version":1,"launcher":{"proposal_timeout_seconds":0}}""", "launcher.proposal_timeout_seconds")]
    [InlineData("""{"version":1,"targeting":{"zoom_max":1}}""", "targeting.zoom_min")]
    [InlineData("""{"version":1,"targeting":{"fov_deg":0}}""", "targeting.fov_deg")]
    [InlineData("""{"version":1,"targeting":{"zoom_step":1}}""", "targeting.zoom_step")]
    [InlineData("""{"version":1,"missile":{"boost_seconds":16}}""", "missile.flight_min_seconds")]
    [InlineData("""{"version":1,"missile":{"flight_max_seconds":10}}""", "missile.flight_min_seconds")]
    [InlineData("""{"version":1,"missile":{"dive_deg":95}}""", "missile.dive_deg")]
    [InlineData("""{"version":1,"missile":{"warn_seconds":15}}""", "missile.warn_seconds")]
    [InlineData("""{"version":1,"blast":{"damage_radius_m":50}}""", "blast.damage_radius_m")]
    [InlineData("""{"version":1,"blast":{"edge_damage":95}}""", "blast.edge_damage")]
    [InlineData("""{"version":1,"blast":{"vehicle_damage_radius_m":30}}""", "blast.vehicle_damage_radius_m")]
    [InlineData("""{"version":1,"blast":{"glass_radius_m":100}}""", "blast.glass_radius_m")]
    [InlineData("""{"version":1,"blast":{"shake_amplitude":2}}""", "blast.shake_amplitude")]
    [InlineData("""{"version":1,"blast":{"fx_radius_m":0}}""", "blast.fx_radius_m")]
    [InlineData("""{"version":0}""", "version")]
    [InlineData("""{"version":9}""", "version")]
    [InlineData("""not json""", "invalid JSON")]
    public void A_bad_field_is_refused_with_the_file_and_the_field_named(string json, string named)
    {
        var e = Assert.Throws<DataFileException>(() => ScudFile.Parse(json));
        Assert.Contains("scud.json", e.Message);
        Assert.Contains(named, e.Message);
    }

    [Fact]
    public void A_file_with_only_a_version_takes_the_defaults()
    {
        var f = ScudFile.Parse("""{"version":1}""");
        Assert.Equal(60, f.Blast.LethalRadiusM);
        var partial = ScudFile.Parse("""{"version":1,"blast":{"lethal_radius_m":70,"damage_radius_m":200}}""");
        Assert.Equal(70, partial.Blast.LethalRadiusM);
        Assert.Equal(150, partial.Launcher.MinRangeM);
    }

    [Fact]
    public void A_launcher_block_on_a_bad_seat_or_a_gun_truck_is_refused()
    {
        var f = GameData.Load(DataDir).Vehicles;
        var truck = f.Vehicles.Single(v => v.Id == "scud");
        truck.Launcher!.OperatorSeat = 0;
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        truck.Launcher.OperatorSeat = 2;
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        truck.Launcher.OperatorSeat = 1;
        truck.Launcher.Muzzle = new float[] { 0, 1 };
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
    }

    // ---- the wire ----

    [Fact]
    public void Commands_round_trip_and_reject_garbage()
    {
        foreach (var cmd in Enum.GetValues<ScudCmd>())
        {
            var m = new ScudCommandMsg(cmd, 123.5f, -456.25f, 2.5f);
            Assert.Equal(m, ScudCodec.DecodeCommand(ScudCodec.Encode(m)));
        }
        Assert.Null(ScudCodec.DecodeCommand(new byte[5]));
        var bad = ScudCodec.Encode(new ScudCommandMsg(ScudCmd.Lock, 1, 2));
        bad[0] = 99;
        Assert.Null(ScudCodec.DecodeCommand(bad));
        var nan = ScudCodec.Encode(new ScudCommandMsg(ScudCmd.Lock, float.NaN, 2));
        Assert.Null(ScudCodec.DecodeCommand(nan));
    }

    [Fact]
    public void The_state_round_trips_with_and_without_a_lock_and_a_countdown()
    {
        var a = new ScudStateMsg(0x4003, ScudLauncherState.Erecting, 3.25f, 4.75f, 0f, Op, new Vector3(10.5f, 3f, -20.25f), 0b10, 0b11);
        Assert.Equal(a, ScudCodec.DecodeState(ScudCodec.Encode(a)));
        var b = new ScudStateMsg(0x4003, ScudLauncherState.Stowed, 0f, float.PositiveInfinity, 0f, 0, null, 0, 0);
        var back = ScudCodec.DecodeState(ScudCodec.Encode(b))!;
        Assert.Equal(b, back);
        Assert.True(float.IsPositiveInfinity(back.Left));
        // the rack angle an abort lowers from survives to a byte of 90 degrees
        var c = new ScudStateMsg(1, ScudLauncherState.Aborting, 0, 2, 0.8f, Op, null, 0, 0);
        Assert.Equal(0.8f, ScudCodec.DecodeState(ScudCodec.Encode(c))!.RackFrom, 2);
        foreach (var s in Enum.GetValues<ScudLauncherState>())
            Assert.Equal(s, ScudCodec.DecodeState(ScudCodec.Encode(new ScudStateMsg(1, s, 0, 1, 0, 0, null, 0, 0)))!.State);
        Assert.Null(ScudCodec.DecodeState(new byte[ScudCodec.StateBytes - 1]));
        var junk = ScudCodec.Encode(a); junk[2] = 200;
        Assert.Null(ScudCodec.DecodeState(junk));
    }

    [Fact]
    public void The_view_the_launch_the_impact_and_the_notice_round_trip()
    {
        var v = new ScudViewMsg(0x4001, 1234.5f, -77.5f, 3.2f);
        Assert.Equal(v, ScudCodec.DecodeView(ScudCodec.Encode(v)));
        var l = new ScudLaunchMsg(44, 0x4001, Op, new Vector3(1, 2, 3), new Vector3(400, 5, -600), 17.5f);
        Assert.Equal(l, ScudCodec.DecodeLaunch(ScudCodec.Encode(l)));
        Assert.Null(ScudCodec.DecodeLaunch(ScudCodec.Encode(l with { Seconds = 0 })));
        var i = new ScudImpactMsg(44, Op, new Vector3(400, 5, -600));
        Assert.Equal(i, ScudCodec.DecodeImpact(ScudCodec.Encode(i)));
        foreach (var n in Enum.GetValues<ScudNoticeCode>()) Assert.Equal(n, ScudCodec.DecodeNotice(ScudCodec.Encode(n)));
        Assert.Null(ScudCodec.DecodeNotice(new byte[] { 0 }));
        Assert.Null(ScudCodec.DecodeNotice(new byte[] { 99 }));
        Assert.Null(ScudCodec.DecodeNotice(new byte[2]));
    }

    [Fact]
    public void Every_refusal_and_cancel_has_a_notice_and_an_ok_has_none()
    {
        Assert.Null(ScudCodec.NoticeOf(ScudAnswer.Ok));
        foreach (var a in Enum.GetValues<ScudAnswer>().Where(a => a != ScudAnswer.Ok)) Assert.NotNull(ScudCodec.NoticeOf(a));
        foreach (var c in Enum.GetValues<ScudCancel>()) Assert.True(Enum.IsDefined(ScudCodec.NoticeOf(c)));
    }

    [Fact]
    public void The_protocol_is_71_and_the_events_are_where_the_docs_say()
    {
        Assert.Equal(73, Wire.ProtocolVersion);
        Assert.Equal(80, (int)EventKind.ScudCommand);
        Assert.Equal(85, (int)EventKind.ScudNotice);
        Assert.Equal(237, SpecialWeapons.Scud);
        // no other event kind shares a number with the Scud's
        var kinds = Enum.GetValues<EventKind>().Select(k => (int)k).ToList();
        for (var k = 80; k <= 85; k++) Assert.Equal(1, kinds.Count(x => x == k));
    }

    [Fact]
    public void The_launcher_machine_is_in_the_state_machine_catalog()
    {
        Assert.Contains(StateMachineCatalog.All, s => s.Name == "Scud.Launcher");
        Assert.Empty(ScudLauncher.Spec.Unreachable);
    }

    // ---- the client's screen ----

    [Fact]
    public void The_operator_opens_the_screen_waits_for_the_server_and_it_closes_when_the_launcher_goes_to_a_dead_state()
    {
        var sc = new ScudScreen();
        Assert.Equal(ScudScreenAction.None, sc.Update(0, true, true, ScudLauncherState.Stowed, toggle: false));
        Assert.Equal(ScudScreenAction.SendOpen, sc.Update(1, true, true, ScudLauncherState.Stowed, toggle: true));
        Assert.Equal(ScudScreenMode.Operating, sc.Mode);
        Assert.True(sc.Pending);
        // the server has not answered yet: the screen waits
        sc.Update(1.5, true, true, ScudLauncherState.Stowed, false);
        Assert.Equal(ScudScreenMode.Operating, sc.Mode);
        // it answers: Targeting, the wait is over
        sc.Update(1.6, true, true, ScudLauncherState.Targeting, false);
        Assert.False(sc.Pending);
        Assert.Equal(ScudScreenMode.Operating, sc.Mode);
        // the proposal, the erect and the ignition keep it up
        foreach (var s in new[] { ScudLauncherState.Proposed, ScudLauncherState.Erecting, ScudLauncherState.Launching })
        {
            sc.Update(2, true, true, s, false);
            Assert.Equal(ScudScreenMode.Operating, sc.Mode);
        }
        // the missile left: the screen closes
        sc.Update(3, true, true, ScudLauncherState.Spent, false);
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
    }

    [Fact]
    public void An_open_the_server_never_answers_gives_up_and_a_cancel_closes_the_screen()
    {
        var sc = new ScudScreen();
        sc.Update(10, true, true, ScudLauncherState.Stowed, toggle: true);
        sc.Update(10 + ScudScreen.AnswerGiveUpSeconds + 0.1, true, true, ScudLauncherState.Stowed, false);
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
        // a cancel that sends the launcher back to Stowed while the screen is up closes it
        sc.Update(20, true, true, ScudLauncherState.Stowed, toggle: true);
        sc.Update(20.2, true, true, ScudLauncherState.Targeting, false);
        sc.Update(20.4, true, true, ScudLauncherState.Stowed, false);
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
    }

    [Fact]
    public void The_screen_key_and_back_close_the_screen_and_tell_the_server_while_it_matters()
    {
        var sc = new ScudScreen();
        sc.Update(0, true, true, ScudLauncherState.Stowed, toggle: true);
        sc.Update(0.1, true, true, ScudLauncherState.Targeting, false);
        Assert.Equal(ScudScreenAction.SendClose, sc.Update(1, true, true, ScudLauncherState.Targeting, toggle: false, back: true));
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
        // back with no screen up does nothing (it is the pause menu's)
        Assert.Equal(ScudScreenAction.None, sc.Update(2, true, true, ScudLauncherState.Stowed, toggle: false, back: true));
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
        // the screen key during the erect brings the screen back (no server message) and closes it again
        Assert.Equal(ScudScreenAction.None, sc.Update(3, true, true, ScudLauncherState.Erecting, toggle: true));
        Assert.Equal(ScudScreenMode.Operating, sc.Mode); // the operator gets the screen back to abort from
        Assert.Equal(ScudScreenAction.None, sc.Update(4, true, true, ScudLauncherState.Erecting, toggle: true));
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
    }

    [Fact]
    public void Another_seat_watches_the_feed_only_while_a_screen_is_live_and_leaving_the_truck_closes_it()
    {
        var sc = new ScudScreen();
        // nothing to watch while the operator has no screen up
        Assert.Equal(ScudScreenAction.None, sc.Update(0, true, false, ScudLauncherState.Stowed, toggle: true));
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
        Assert.Equal(ScudScreenAction.None, sc.Update(1, true, false, ScudLauncherState.Proposed, toggle: true));
        Assert.Equal(ScudScreenMode.Watching, sc.Mode);
        sc.Update(2, true, false, ScudLauncherState.Erecting, false);
        Assert.Equal(ScudScreenMode.Watching, sc.Mode);
        sc.Update(3, true, false, ScudLauncherState.Targeting, false); // the proposal was dropped, the operator is aiming again: still live
        Assert.Equal(ScudScreenMode.Watching, sc.Mode);
        sc.Update(4, false, false, ScudLauncherState.Targeting, false);
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
        // the operator closing their screen ends the feed
        sc.Update(5, true, false, ScudLauncherState.Targeting, toggle: true);
        sc.Update(6, true, false, ScudLauncherState.Stowed, false);
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
    }

    [Fact]
    public void A_screen_is_closed_when_the_player_changes_seats_and_the_machine_is_in_the_catalog()
    {
        var sc = new ScudScreen();
        sc.Update(0, true, true, ScudLauncherState.Stowed, toggle: true);
        sc.Update(0.1, true, true, ScudLauncherState.Targeting, false);
        sc.Update(0.2, true, false, ScudLauncherState.Targeting, false); // no longer in the operator seat
        Assert.Equal(ScudScreenMode.Closed, sc.Mode);
        Assert.Contains(StateMachineCatalog.All, s => s.Name == "Scud.Screen");
        Assert.Empty(ScudScreen.Spec.Unreachable);
    }
}
