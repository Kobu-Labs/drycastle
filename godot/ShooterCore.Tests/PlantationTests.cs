using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The marijuana plantation (issue #364): the zone polygons, the hold, the high's state machine, the key priority, the status bit and the nausea.</summary>
public class PlantationTests
{
    const float Dt = 1f / 60f;

    // as the pipeline writes it: game axes [x, z] (x east, z = -north), one box 100..200 east and -300..-100 (north 100..300)
    const string Json = """
    {
      "version": 1,
      "axes": "rings: game axes [x, z]",
      "zones": [ { "kind": "plantation", "areas": ["Z-0001"], "rings": [ [[100, -300], [200, -300], [200, -100], [100, -100]] ] } ]
    }
    """;

    static readonly PlantationFile Data = PlantationFile.Default;

    // ---- the polygons ----

    [Fact]
    public void A_point_inside_a_plantation_polygon_counts_and_one_outside_does_not()
    {
        var z = ZonePolygons.Parse(Json);
        Assert.True(z.Has("plantation"));
        Assert.True(z.Contains("plantation", 150, -200));
        Assert.False(z.Contains("plantation", 99, -200));
        Assert.False(z.Contains("plantation", 150, -99));
        Assert.False(z.Contains("plantation", 250, -200));
        Assert.False(z.Contains("deer", 150, -200)); // another kind
    }

    [Fact]
    public void An_L_shaped_outline_leaves_its_notch_outside_in_either_winding()
    {
        var l = new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 10), new Vector2(10, 10), new Vector2(10, 20), new Vector2(0, 20) };
        foreach (var ring in new[] { l, l.Reverse().ToArray() })
        {
            Assert.True(ZonePolygons.PointInPolygon(ring, 5, 15));
            Assert.True(ZonePolygons.PointInPolygon(ring, 15, 5));
            Assert.False(ZonePolygons.PointInPolygon(ring, 15, 15)); // the notch
        }
    }

    [Fact]
    public void Several_rings_are_zones_of_their_own_and_a_ring_under_three_points_is_dropped()
    {
        var z = ZonePolygons.Parse("""{"version":1,"zones":[{"kind":"plantation","areas":[],"rings":[[[0,0],[10,0],[10,10],[0,10]],[[50,50],[60,50],[60,60],[50,60]],[[1,1],[2,2]]]}]}""");
        Assert.Equal(2, z.Rings("plantation").Count);
        Assert.True(z.Contains("plantation", 5, 5));
        Assert.True(z.Contains("plantation", 55, 55));
        Assert.False(z.Contains("plantation", 30, 30));
    }

    [Fact]
    public void A_map_without_the_file_or_a_zone_has_the_feature_off_with_no_error()
    {
        Assert.False(ZonePolygons.FromJson("").Has("plantation"));
        Assert.False(ZonePolygons.FromJson(null).Contains("plantation", 0, 0));
        Assert.False(ZonePolygons.FromJson("""{"version":1,"zones":[]}""").Has("plantation"));
        var errors = new List<string>();
        Assert.Same(ZonePolygons.Empty, ZonePolygons.FromJsonOrEmpty(null, errors.Add));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":2,"zones":[]}""")]
    [InlineData("""{"zones":[]}""")]
    [InlineData("""{"version":1,"zones":[{"kind":"plantation","rings":[[[0,"x"],[1,1],[2,2]]]}]}""")]
    public void A_broken_file_is_reported_and_counts_as_no_polygons(string json)
    {
        var errors = new List<string>();
        var z = ZonePolygons.FromJsonOrEmpty(json, errors.Add);
        Assert.False(z.Has("plantation"));
        Assert.Single(errors);
        Assert.Contains("zone_polygons.json", errors[0]);
    }

    // ---- the data ----

    [Fact]
    public void The_shipped_file_is_valid_and_has_the_decided_times()
    {
        var shipped = PlantationFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "plantation.json"));
        Assert.Equal(1.5, shipped.HoldSeconds);
        Assert.Equal(120, shipped.HighSeconds);
        Assert.Equal(PlantationFile.Default.View.Cigarette.Model, shipped.View.Cigarette.Model);
        Assert.NotEmpty(shipped.Nausea.Yaw);
        Assert.True(shipped.View.Cigarette.EmberEnergy > 2, "the ember blooms (ADR 0030: threshold 2)");
    }

    [Fact]
    public void A_bad_number_names_the_file_and_the_field()
    {
        var e = Assert.Throws<DataFileException>(() => PlantationFile.Parse("""{"version":1,"hold_seconds":0}"""));
        Assert.Contains("plantation.json", e.Message);
        Assert.Contains("hold_seconds", e.Message);
    }

    // ---- the high ----

    [Fact]
    public void A_completed_hold_makes_a_sober_player_high_and_the_clock_runs_out_at_120_s()
    {
        var h = new PlayerHigh();
        Assert.False(h.IsHigh);
        Assert.True(h.Complete(120f));
        Assert.True(h.IsHigh);
        for (var i = 0; i < 119; i++) Assert.False(h.Tick(1f));
        Assert.True(h.IsHigh);
        Assert.True(h.SecondsLeft > 0f);
        Assert.True(h.Tick(1f)); // the tick that runs it out
        Assert.False(h.IsHigh);
        Assert.Equal(0f, h.SecondsLeft);
        Assert.False(h.Tick(1f)); // sober: nothing to run out
    }

    [Fact]
    public void Another_completed_hold_while_high_starts_the_120_s_over()
    {
        var h = new PlayerHigh();
        h.Complete(120f);
        for (var i = 0; i < 100; i++) h.Tick(1f);
        Assert.InRange(h.SecondsLeft, 19f, 21f);
        Assert.False(h.Complete(120f)); // already high: no new status, the clock restarts
        Assert.InRange(h.SecondsLeft, 119f, 120f);
        Assert.True(h.IsHigh);
        for (var i = 0; i < 119; i++) Assert.False(h.Tick(1f));
        Assert.True(h.Tick(1f));
        Assert.False(h.IsHigh);
    }

    [Fact]
    public void A_death_clears_the_high_at_once_and_a_second_clear_does_nothing()
    {
        var h = new PlayerHigh();
        h.Complete(120f);
        Assert.True(h.Clear());
        Assert.False(h.IsHigh);
        Assert.False(h.Clear());
        Assert.True(h.Complete(120f)); // a respawned player can get high again from sober
    }

    [Fact]
    public void The_spec_declares_the_restart_and_every_move_the_machine_makes_is_legal()
    {
        Assert.True(PlayerHigh.Spec.Allows(HighState.Sober, HighState.High));
        Assert.True(PlayerHigh.Spec.Allows(HighState.High, HighState.Sober));
        Assert.Contains(("High", "High"), PlayerHigh.Spec.TransitionNames);
        StateMachineDiagnostics.Strict = true; // an undeclared transition would throw
        var h = new PlayerHigh();
        h.Complete(1f); h.Complete(1f); h.Tick(2f); h.Complete(1f); h.Clear();
    }

    // ---- the hold: the server's InteractHold with the plantation's rules ----

    static bool Hold(ref InteractHold hold, bool use, bool counts, int ticks, double seconds = 1.5)
    {
        var completed = false;
        for (var i = 0; i < ticks; i++) completed |= hold.Tick(use, counts, Dt, seconds);
        return completed;
    }

    [Fact]
    public void Holding_the_key_for_1_5_s_inside_completes_once_and_the_key_must_be_released_for_the_next()
    {
        var hold = new InteractHold();
        Assert.False(Hold(ref hold, true, true, 85)); // 1.42 s
        Assert.True(Hold(ref hold, true, true, 10)); // over 1.5 s
        Assert.False(Hold(ref hold, true, true, 600)); // still held: no second completion
        Assert.False(Hold(ref hold, false, true, 3));
        Assert.True(Hold(ref hold, true, true, 95)); // a new hold completes again
    }

    [Fact]
    public void Leaving_the_plantation_or_losing_the_key_to_something_else_starts_the_hold_over()
    {
        var hold = new InteractHold();
        Hold(ref hold, true, true, 60);
        Hold(ref hold, true, false, 1); // out of the plantation, or something else owns the key
        Assert.False(Hold(ref hold, true, true, 60)); // 1 s again: not enough
        Assert.True(Hold(ref hold, true, true, 40));
    }

    // ---- the key priority: the plantation is the lowest ----

    [Fact]
    public void The_hold_counts_only_alive_on_foot_inside_and_with_nothing_else_in_reach_of_the_key()
    {
        Assert.True(PlantationRules.HoldCounts(true, true, true, KeyCompetitor.None));
        Assert.False(PlantationRules.HoldCounts(false, true, true, KeyCompetitor.None)); // dead
        Assert.False(PlantationRules.HoldCounts(true, false, true, KeyCompetitor.None)); // in a seat, held, pinned
        Assert.False(PlantationRules.HoldCounts(true, true, false, KeyCompetitor.None)); // outside
        foreach (var other in Enum.GetValues<KeyCompetitor>().Where(k => k != KeyCompetitor.None))
            Assert.False(PlantationRules.HoldCounts(true, true, true, other), $"{other} wins the key");
        Assert.False(PlantationRules.HoldCounts(true, true, true, KeyCompetitor.Corpse | KeyCompetitor.Ladder));
    }

    [Fact]
    public void The_competitors_are_every_thing_that_can_own_the_key_on_foot()
    {
        foreach (var name in new[] { "Corpse", "GroundItem", "Crate", "Doors", "Ladder", "Vehicle", "MassSeat", "RozhlasSet", "Loot" })
            Assert.True(Enum.TryParse<KeyCompetitor>(name, out _), name);
    }

    [Fact]
    public void The_arbiter_gives_the_key_to_the_plantation_only_when_the_crate_the_item_and_the_corpse_do_not_want_it()
    {
        Assert.Equal(InteractOwner.Plantation, InteractArbiter.Owner(false, false, false, false, false, insidePlantation: true));
        Assert.Equal(InteractOwner.Crate, InteractArbiter.Owner(true, false, false, true, false, insidePlantation: true));
        Assert.Equal(InteractOwner.GroundItem, InteractArbiter.Owner(false, true, true, true, false, insidePlantation: true));
        Assert.Equal(InteractOwner.Corpse, InteractArbiter.Owner(false, false, false, true, false, insidePlantation: true));
        Assert.Equal(InteractOwner.None, InteractArbiter.Owner(false, false, false, false, false)); // outside: nobody
    }

    // ---- the replicated bit ----

    [Fact]
    public void High_is_bit_2_of_the_status_mask_travels_in_the_status_message_and_a_death_clears_it()
    {
        Assert.Equal(2, (int)StatusKind.High);
        var s = new PlayerStatuses();
        Assert.True(s.Grant(StatusKind.High));
        Assert.True(s.Grant(StatusKind.Blessing));
        Assert.Equal(0b110, s.Mask);
        Assert.True(StatusCodec.TryDecodeState(StatusCodec.EncodeState(7, s.Mask, false), out var id, out var mask, out _));
        Assert.Equal((7, (byte)0b110), (id, mask));
        var theirs = new PlayerStatuses();
        theirs.SetMask(mask);
        Assert.True(theirs.Has(StatusKind.High));
        Assert.True(s.Remove(StatusKind.High)); // the clock ran out: the blessing stays
        Assert.False(s.Has(StatusKind.High));
        Assert.True(s.Has(StatusKind.Blessing));
        s.Grant(StatusKind.High);
        s.OnDeath();
        Assert.False(s.Any);
    }

    // ---- the nausea ----

    static (double Yaw, double Pitch, float MaxRoll, float MaxFov) Run(Nausea n, float seconds, bool high, bool owned, bool reduced)
    {
        double yaw = 0, pitch = 0, maxYaw = 0, maxPitch = 0; float maxRoll = 0, maxFov = 0;
        for (var t = 0f; t < seconds; t += Dt)
        {
            var (dy, dp) = n.Step(Dt, high, owned, reduced);
            yaw += dy; pitch += dp;
            maxYaw = Math.Max(maxYaw, Math.Abs(yaw)); maxPitch = Math.Max(maxPitch, Math.Abs(pitch));
            maxRoll = MathF.Max(maxRoll, MathF.Abs(n.Visual.RollRad)); maxFov = MathF.Max(maxFov, MathF.Abs(n.Visual.FovDeg));
        }
        return (maxYaw, maxPitch, maxRoll, maxFov);
    }

    [Fact]
    public void The_nausea_moves_the_real_look_and_draws_a_roll_and_a_breathing_fov()
    {
        var (yaw, pitch, roll, fov) = Run(new Nausea(Data.Nausea), 30f, true, true, false);
        Assert.InRange(yaw * 180 / Math.PI, 1.0, 5.0); // degrees of real aim moved
        Assert.InRange(pitch * 180 / Math.PI, 0.5, 4.0);
        Assert.True(roll > 0.02f); // radians
        Assert.True(fov > 1f); // degrees
    }

    [Fact]
    public void Reduce_motion_leaves_a_much_weaker_version_and_the_nausea_fades_in_rather_than_jumping()
    {
        var full = Run(new Nausea(Data.Nausea), 30f, true, true, false);
        var weak = Run(new Nausea(Data.Nausea), 30f, true, true, true);
        Assert.True(weak.Yaw < full.Yaw * 0.3 && weak.Pitch < full.Pitch * 0.3 && weak.MaxRoll < full.MaxRoll * 0.3f && weak.MaxFov < full.MaxFov * 0.3f);
        var n = new Nausea(Data.Nausea);
        var (dy, _) = n.Step(Dt, true, true, false);
        Assert.True(Math.Abs(dy) < 1e-5); // the first frame moves next to nothing
    }

    [Fact]
    public void Nothing_moves_while_another_camera_owns_the_view_and_it_goes_on_where_it_stopped()
    {
        var n = new Nausea(Data.Nausea);
        Run(n, 5f, true, true, false);
        var (roll, fov) = n.Visual;
        var (dy, dp) = n.Step(Dt, true, false, false); // a cut-scene, a seat, the death view
        Assert.Equal((0f, 0f), (dy, dp));
        Assert.Equal((roll, fov), n.Visual);
        Assert.True(n.Active);
    }

    [Fact]
    public void When_the_high_ends_the_sway_fades_out_and_the_look_change_adds_up_to_what_was_added()
    {
        var n = new Nausea(Data.Nausea);
        double yaw = 0, pitch = 0;
        for (var t = 0f; t < 20f; t += Dt) { var (dy, dp) = n.Step(Dt, true, true, false); yaw += dy; pitch += dp; }
        Assert.True(n.Active);
        for (var t = 0f; t < 20f; t += Dt) { var (dy, dp) = n.Step(Dt, false, true, false); yaw += dy; pitch += dp; }
        Assert.False(n.Active);
        Assert.Equal(0.0, yaw, 6); // what the sway turned, it turned back: sober means the aim is only the player's
        Assert.Equal(0.0, pitch, 6);
        Assert.Equal((0f, 0f), n.Visual);
    }
}
