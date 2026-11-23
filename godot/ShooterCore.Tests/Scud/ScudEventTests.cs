using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Scud;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Scud;

/// <summary>The Scud event: the search area's maths, the event's lifecycle, the bunkers' cart slots and their places, the intel entry and its area on the wire, and scud.json's event block.</summary>
public class ScudEventTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly ScudFile Shipped = ScudFile.Load(Path.Combine(DataDir, "scud.json"));
    static ScudEventData E => Shipped.Event;

    // ---- the search area ----

    [Theory]
    [InlineData(1, 900f)]
    [InlineData(2, 1500f)]
    [InlineData(3, 3200f)]
    [InlineData(4, 4400f)]
    public void The_truck_is_always_inside_its_area_and_never_on_its_edge_or_at_its_centre(int seed, float extent)
    {
        var rng = new Random(seed);
        var radius = ScudSearch.RadiusM(E.SearchRadiusCells, extent);
        for (var i = 0; i < 20000; i++)
        {
            var truck = new Vector2(rng.Next(-2000, 2000), rng.Next(-2000, 2000));
            var area = ScudSearch.Make(truck, radius, rng);
            var d = Vector2.Distance(truck, area.Centre);
            Assert.True(area.Contains(truck));
            Assert.True(d <= radius * ScudSearch.MaxOffsetShare + 0.01f, $"{d} of {radius}");
            Assert.Equal(radius, area.Radius);
        }
    }

    [Fact]
    public void The_truck_stands_uniformly_in_the_circle_so_the_centre_says_nothing()
    {
        var rng = new Random(77);
        const int n = 40000; const float r = 1000f;
        var inner = 0; var sum = Vector2.Zero; var quadrants = new int[4]; var atCentre = 0;
        var truck = new Vector2(500, -300);
        for (var i = 0; i < n; i++)
        {
            var a = ScudSearch.Make(truck, r, rng);
            var off = truck - a.Centre; // where the truck is seen from the centre
            sum += off;
            if (off.Length() < r * ScudSearch.MaxOffsetShare * 0.5f) inner++; // half the radius is a quarter of the area
            quadrants[(off.X >= 0 ? 1 : 0) + (off.Y >= 0 ? 2 : 0)]++;
            if (off.Length() < 1f) atCentre++;
        }
        Assert.InRange(inner / (double)n, 0.235, 0.265);
        foreach (var q in quadrants) Assert.InRange(q / (double)n, 0.235, 0.265);
        Assert.True((sum / n).Length() < r * 0.02f, "the average offset is the centre");
        Assert.True(atCentre < n / 500, "the centre is not where the truck is");
    }

    [Fact]
    public void The_radius_is_nine_cells_of_the_sheets_own_grid_and_the_centre_has_a_grid_reference()
    {
        Assert.Equal(9, E.SearchRadiusCells);
        foreach (var extent in new[] { 800f, 2400f, 3900f })
        {
            var cell = TacticalMapMath.CellSize(extent);
            var radius = ScudSearch.RadiusM(9, extent);
            Assert.Equal(9 * cell, radius);
            Assert.Equal(9, ScudSearch.Cells(radius, extent));
        }
        // the reference of a centre is its cell on the sheet: column letters from the west, rows from the south
        var origin = new Vector2(-1000, -1200); var side = 2400f; var south = origin.Y + side;
        var cell2 = TacticalMapMath.CellSize(side);
        var centre = new Vector2(origin.X + 3.5f * cell2, south - 6.5f * cell2); // the middle of D7
        Assert.Equal("D7", TacticalMapMath.ReferenceOf(centre, origin.X, south, side));
    }

    [Fact]
    public void The_sheet_is_the_longer_side_plus_the_padding_centred_on_the_box()
    {
        var (origin, side) = TacticalMapMath.Sheet(-300, -100, 700, 400);
        Assert.Equal(1000f + 2 * TacticalMapMath.SheetPad, side);
        Assert.Equal(new Vector2(-300 + 500 - side / 2, -100 + 250 - side / 2), origin);
        var (o2, s2) = TacticalMapMath.Sheet(0, 0, 200, 900);
        Assert.Equal(900f + 2 * TacticalMapMath.SheetPad, s2);
        Assert.Equal(100f - s2 / 2, o2.X, 3);
    }

    // ---- the intel entry and the wire ----

    static readonly SearchArea Area = new(new Vector2(-312.5f, 840.25f), 1800f);

    [Fact]
    public void The_entry_is_top_secret_with_the_area_as_its_place_and_fits_the_boards_limits()
    {
        var e = ScudEventRun.Intel(10, Area);
        Assert.Equal("scud", e.Kind); Assert.Equal(ScudEventRun.IntelId, e.Id);
        Assert.Equal("Top secret weapon located", e.Name);
        Assert.True(e.IsArea);
        Assert.Equal(Area.Centre, e.Where); Assert.Equal(Area.Radius, e.Radius);
        Assert.False(e.Test);
        Assert.True(e.Name.Length <= IntelCodec.MaxName && e.Description.Length <= IntelCodec.MaxDescription);
        Assert.InRange(e.Risks.Count, 1, IntelCodec.MaxRisks);
        Assert.All(e.Risks, r => Assert.True(r.Length <= IntelCodec.MaxRisk, r));
        Assert.DoesNotContain(e.Risks.Concat(new[] { e.Description, e.Name }), t => t.Contains("-312") || t.Contains("840")); // the text says nothing of where
        Assert.True(e.StartsAt <= 10); // under way at once
        Assert.Null(e.EndsAt); // it leaves the board when the event ends, not by a clock
    }

    [Fact]
    public void An_area_crosses_the_wire_with_its_centre_and_radius_and_a_point_is_still_a_point()
    {
        var area = ScudEventRun.Intel(100, Area);
        var point = new IntelEvent("p", "creature", "Sighting", "Something large.", new[] { "Big" }, 130, new Vector2(10, 20));
        var none = new IntelEvent("n", "generic", "Village wide", "Everywhere.", Array.Empty<string>(), 90);
        var bytes = IntelCodec.Encode(new[] { area, point, none }, 100, "scud");
        var back = IntelCodec.Decode(bytes, 500)!;
        Assert.Equal(3, back.Events.Count);
        var a = back.Events.Single(x => x.Id == "scud");
        Assert.True(a.IsArea);
        Assert.Equal(Area.Centre, a.Where); Assert.Equal(Area.Radius, a.Radius);
        Assert.Equal(area.Name, a.Name); Assert.Equal(area.Risks, a.Risks);
        Assert.Equal(500, a.StartsAt, 3);
        var p = back.Events.Single(x => x.Id == "p");
        Assert.False(p.IsArea); Assert.Null(p.Radius); Assert.Equal(new Vector2(10, 20), p.Where);
        Assert.Null(back.Events.Single(x => x.Id == "n").Where);
        Assert.Equal("scud", back.Announced?.Id);
        // a point event costs what it always did: no radius bytes
        var onlyPoint = IntelCodec.Encode(new[] { point }, 0);
        var asArea = IntelCodec.Encode(new[] { point with { Radius = 5f } }, 0);
        Assert.Equal(onlyPoint.Length + 4, asArea.Length);
        Assert.False(IntelCodec.Decode(onlyPoint, 0)!.Events[0].IsArea);
    }

    [Fact]
    public void An_area_without_a_centre_or_with_a_bad_radius_is_refused()
    {
        var good = IntelCodec.Encode(new[] { ScudEventRun.Intel(0, Area) }, 0);
        Assert.NotNull(IntelCodec.Decode(good, 0));
        Assert.Null(IntelCodec.Decode(good.Take(good.Length - 12).ToArray(), 0)); // cut short
        // the flags byte of the only event: find it by encoding a variant, then clear the "has a place" bit so an area has no centre
        var noPlace = IntelCodec.Encode(new[] { new IntelEvent("x", "scud", "N", "D", Array.Empty<string>(), 0, Radius: 50f) }, 0);
        Assert.False(IntelCodec.Decode(noPlace, 0)!.Events[0].IsArea); // not encoded as an area: no centre
        foreach (var bad in new[] { 0f, -5f, float.NaN, IntelCodec.MaxRadius + 1 })
        {
            var b = IntelCodec.Encode(new[] { new IntelEvent("x", "scud", "N", "D", Array.Empty<string>(), 0, new Vector2(1, 2), Radius: 100f) }, 0);
            // the radius is the 4 bytes after the centre: id, kind, name, description, flags, time, x, z, radius, then the risk count
            var at = b.Length - 1 - 4; // risk count is the last byte (no risks)
            BitConverter.GetBytes(bad).CopyTo(b, at);
            Assert.Null(IntelCodec.Decode(b, 0));
        }
    }

    [Fact]
    public void The_board_holds_an_area_entry_like_any_other_and_loses_it_on_withdraw()
    {
        var board = new IntelBoard();
        board.Put(ScudEventRun.Intel(0, Area));
        Assert.Single(board.Events);
        Assert.True(board.Events[0].IsArea);
        Assert.True(board.Remove(ScudEventRun.IntelId));
        Assert.True(board.IsEmpty);
        Assert.False(board.Remove(ScudEventRun.IntelId));
    }

    // ---- the lifecycle ----

    [Fact]
    public void The_first_boarder_finds_the_truck_and_the_event_is_found_for_good_until_the_truck_dies()
    {
        var run = new ScudEventRun();
        Assert.Equal(ScudEventPhase.Idle, run.Phase);
        Assert.False(run.TruckOut);
        Assert.True(run.Begin(Area, 1200f));
        Assert.Equal(ScudEventPhase.Searching, run.Phase);
        Assert.True(run.TruckOut);
        Assert.False(run.Begin(Area, 1200f)); // one at a time
        Assert.Equal(ScudEventOutcome.None, run.Tick(1f, 0, true));
        Assert.Equal(1199f, run.Left, 1);
        Assert.Equal(ScudEventOutcome.Found, run.Tick(1f, 42, true));
        Assert.Equal(ScudEventPhase.Found, run.Phase);
        Assert.Equal((ushort)42, run.Finder);
        Assert.True(run.TruckOut); // the truck is still out: no second event yet
        Assert.False(run.Begin(Area, 1200f));
        // found: nothing times out, a second boarder changes nothing, only the truck's end matters
        Assert.Equal(ScudEventOutcome.None, run.Tick(100000f, 43, true));
        Assert.Equal((ushort)42, run.Finder);
        Assert.Equal(ScudEventOutcome.TruckLost, run.Tick(1f, 0, false));
        Assert.Equal(ScudEventPhase.Over, run.Phase);
        Assert.False(run.TruckOut);
        Assert.Equal(ScudEventOutcome.None, run.Tick(1f, 0, false));
        Assert.True(run.Begin(Area, 60f)); // a new event may begin
        Assert.Equal(0, run.Finder);
    }

    [Fact]
    public void Nobody_boarding_in_the_time_despawns_it_and_a_destroyed_truck_ends_it_unfound()
    {
        var run = new ScudEventRun();
        run.Begin(Area, (float)(E.DespawnMinutes * 60));
        var outcome = ScudEventOutcome.None; var seconds = 0f;
        while (outcome == ScudEventOutcome.None && seconds < 4000) { outcome = run.Tick(1f, 0, true); seconds++; }
        Assert.Equal(ScudEventOutcome.TimedOut, outcome);
        Assert.Equal(E.DespawnMinutes * 60, seconds, 0);
        Assert.Equal(ScudEventPhase.Over, run.Phase);

        run.Begin(Area, 600f);
        Assert.Equal(ScudEventOutcome.TruckLost, run.Tick(1f, 0, false));
        Assert.Equal(ScudEventPhase.Over, run.Phase);
        // the truck lost and a boarder in the same tick: lost wins (a destroyed truck has nobody)
        run.Begin(Area, 600f);
        Assert.Equal(ScudEventOutcome.TruckLost, run.Tick(1f, 9, false));
    }

    [Fact]
    public void An_admin_wins_it_for_the_caller_and_a_cancel_or_a_round_ends_it_with_nobody_paid()
    {
        var run = new ScudEventRun();
        Assert.False(run.Win(5)); Assert.False(run.Despawn());
        run.Begin(Area, 600f);
        Assert.True(run.Win(5));
        Assert.Equal(ScudEventPhase.Found, run.Phase); Assert.Equal((ushort)5, run.Finder);
        Assert.False(run.Win(6)); // already found
        Assert.True(run.Despawn()); // a round ends a found event too
        Assert.Equal(ScudEventPhase.Over, run.Phase);
        run.Begin(Area, 600f);
        Assert.True(run.Despawn());
        Assert.Equal(0, run.Finder);
        Assert.False(run.Despawn());
    }

    [Fact]
    public void Every_edge_the_event_machine_declares_is_taken_somewhere()
    {
        var edges = new HashSet<(string, string)>();
        var run = new ScudEventRun();
        // run it through every path under the strict spec (an undeclared transition throws)
        run.Begin(Area, 5f); run.Tick(1f, 1, true); run.Tick(1f, 0, false); // Idle > Searching > Found > Over
        run.Begin(Area, 5f); run.Tick(10f, 0, true); // Over > Searching > Over (timeout)
        run.Begin(Area, 5f); run.Win(2); run.Despawn();
        foreach (var t in ScudEventRun.Spec.TransitionNames) edges.Add(t);
        Assert.Contains(("Idle", "Searching"), edges); Assert.Contains(("Searching", "Found"), edges); Assert.Contains(("Searching", "Over"), edges);
        Assert.Contains(("Found", "Over"), edges); Assert.Contains(("Over", "Searching"), edges);
    }

    [Fact]
    public void The_event_is_a_startable_kind_that_takes_the_one_event_slot_and_frees_it_when_won()
    {
        Assert.True(EventBook.IsStartable(EventBook.Scud));
        var book = new EventBook();
        Assert.NotNull(book.Start(EventBook.Scud, 0));
        foreach (var kind in EventBook.Startable) Assert.NotNull(book.WhyNotStart(kind)); // one at a time
        Assert.Null(book.Start(EventBook.Bigfoot, 1));
        Assert.Null(book.End(EventBook.Bigfoot, EventPhase.Won, 2)); // never another kind's
        var run = book.End(EventBook.Scud, EventPhase.Won, 3)!;
        Assert.Equal(EventPhase.Won, run.Phase);
        Assert.Null(book.WhyNotStart(EventBook.Thieves)); // the slot is free while the truck still lives
        Assert.NotNull(book.Start(EventBook.Bigfoot, 4));
    }

    // ---- the carts at the bunkers ----

    [Fact]
    public void A_slot_restocks_a_new_cart_after_the_cooldown_once_its_cart_is_emptied_or_gone()
    {
        var cooldown = (float)E.CartCooldownSeconds;
        var slot = new ScudCartSlot();
        Assert.Equal(CartSlotState.Closed, slot.State);
        Assert.Equal(CartSlotAction.None, slot.Tick(1f, true, cooldown)); // closed: nothing
        slot.Open();
        Assert.Equal(CartSlotState.Stocked, slot.State);
        for (var i = 0; i < 100; i++) Assert.Equal(CartSlotAction.None, slot.Tick(60f, true, cooldown)); // a loaded cart, wherever it is towed, is the slot's
        Assert.Equal(CartSlotState.Stocked, slot.State);
        Assert.Equal(CartSlotAction.None, slot.Tick(0.1f, false, cooldown)); // emptied (or destroyed): the cooldown starts
        Assert.Equal(CartSlotState.Restocking, slot.State);
        Assert.Equal(cooldown, slot.Left, 1);
        Assert.Equal(CartSlotAction.None, slot.Tick(cooldown - 1f, true, cooldown)); // the cooldown does not look at the old cart again
        Assert.Equal(CartSlotAction.Place, slot.Tick(1.5f, false, cooldown));
        // the bunker was blocked: wait the retry and ask again
        slot.Placed(false);
        Assert.Equal(CartSlotState.Restocking, slot.State);
        Assert.Equal(CartSlotAction.None, slot.Tick(ScudCartSlot.RetrySeconds - 1f, false, cooldown));
        Assert.Equal(CartSlotAction.Place, slot.Tick(1.5f, false, cooldown));
        slot.Placed(true);
        Assert.Equal(CartSlotState.Stocked, slot.State);
        Assert.Equal(CartSlotAction.None, slot.Tick(1f, true, cooldown));
    }

    [Fact]
    public void A_slot_that_could_not_stock_its_first_cart_tries_again_and_the_trucks_end_closes_it_everywhere()
    {
        var slot = new ScudCartSlot();
        slot.Open(placed: false);
        Assert.Equal(CartSlotState.Restocking, slot.State);
        Assert.Equal(CartSlotAction.Place, slot.Tick(ScudCartSlot.RetrySeconds + 0.1f, false, 600f));
        slot.Close();
        Assert.Equal(CartSlotState.Closed, slot.State);
        Assert.Equal(CartSlotAction.None, slot.Tick(10000f, false, 600f));
        slot.Placed(true); // a late answer to a closed slot changes nothing
        Assert.Equal(CartSlotState.Closed, slot.State);
        slot.Open(); slot.Tick(1f, false, 600f); slot.Close();
        Assert.Equal(CartSlotState.Closed, slot.State); // closed from Restocking too
        Assert.Contains(("Stocked", "Closed"), ScudCartSlot.Spec.TransitionNames);
    }

    [Fact]
    public void The_cart_spots_stand_outside_the_bunker_with_the_drawbar_pointing_away_from_it_doorway_first()
    {
        var centre = new Vector2(-405, -113); const float radius = 5f, standoff = 3f, half = 6.3f;
        var door = 0.7f; // radians, a direction in ground x, z
        var spots = ScudCartSpots.Candidates(centre, radius, door, standoff, half);
        Assert.True(spots.Count >= 36);
        // the first is straight out of the doorway, its rear end standoff beyond the bunker's footprint
        var first = spots[0];
        var u = new Vector2(MathF.Cos(door), MathF.Sin(door));
        Assert.Equal(radius + standoff + half, Vector2.Distance(first.Origin, centre), 3);
        Assert.True(Vector2.Dot(Vector2.Normalize(first.Origin - centre), u) > 0.9999f);
        foreach (var s in spots)
        {
            var out_ = Vector2.Normalize(s.Origin - centre);
            var forward = new Vector2(-MathF.Sin(s.Yaw), -MathF.Cos(s.Yaw)); // the drawbar's way (-Z) in ground x, z
            Assert.True(Vector2.Dot(forward, out_) > 0.9999f, "the drawbar points away from the bunker");
            Assert.True(Vector2.Distance(s.Origin, centre) - half >= radius + standoff - 1e-3f, "the whole cart is outside the footprint");
        }
        Assert.Equal(spots.Count, spots.Select(s => (MathF.Round(s.Origin.X, 2), MathF.Round(s.Origin.Y, 2))).Distinct().Count());
        // later spots are fanned and further out; every direction is offered at the end
        Assert.True(spots.Skip(7).Take(7).All(s => Vector2.Distance(s.Origin, centre) > Vector2.Distance(first.Origin, centre)));
        var all = ScudCartSpots.Candidates(centre, radius, null, standoff, half);
        Assert.Equal(36, all.Count);
        Assert.Equal(12, all.Select(s => MathF.Round(MathF.Atan2((s.Origin - centre).Y, (s.Origin - centre).X), 2)).Distinct().Count());
    }

    // ---- scud.json ----

    [Fact]
    public void The_shipped_event_numbers_are_the_designed_ones()
    {
        Assert.Equal(9, E.SearchRadiusCells);
        Assert.Equal(20, E.DespawnMinutes);
        Assert.Equal(600, E.CartCooldownSeconds);
        Assert.Equal(400, E.XpReward);
        Assert.True(E.SpawnWeight > 0);
        Assert.True(E.MinPlayerDistanceM >= 100);
        Assert.True(E.CartStandoffM >= 0);
        Assert.True(E.SpotClearanceM > 0);
    }

    [Theory]
    [InlineData("""{"version":1,"event":{"search_radius_cells":0}}""", "event.search_radius_cells")]
    [InlineData("""{"version":1,"event":{"search_radius_cells":41}}""", "event.search_radius_cells")]
    [InlineData("""{"version":1,"event":{"despawn_minutes":0}}""", "event.despawn_minutes")]
    [InlineData("""{"version":1,"event":{"xp_reward":-1}}""", "event.xp_reward")]
    [InlineData("""{"version":1,"event":{"spawn_weight":-1}}""", "event.spawn_weight")]
    [InlineData("""{"version":1,"event":{"cart_cooldown_seconds":0}}""", "event.cart_cooldown_seconds")]
    [InlineData("""{"version":1,"event":{"min_player_distance_m":-5}}""", "event.min_player_distance_m")]
    [InlineData("""{"version":1,"event":{"cart_standoff_m":-1}}""", "event.cart_standoff_m")]
    [InlineData("""{"version":1,"event":{"spot_clearance_m":0}}""", "event.spot_clearance_m")]
    public void A_bad_event_field_is_refused_with_the_file_and_the_field_named(string json, string named)
    {
        var e = Assert.Throws<DataFileException>(() => ScudFile.Parse(json));
        Assert.Contains("scud.json", e.Message);
        Assert.Contains(named, e.Message);
    }

    [Fact]
    public void The_found_notice_crosses_the_wire_and_the_zone_kind_is_in_the_pipelines_rules()
    {
        Assert.Equal(ScudNoticeCode.EventFound, ScudCodec.DecodeNotice(ScudCodec.Encode(ScudNoticeCode.EventFound)));
        Assert.Null(ScudCodec.DecodeNotice(new byte[] { (byte)ScudNoticeCode.EventFound + 1 }));
        // the zone kind is what the server asks PickEventSpawn for (EventBook.Scud), and the pipeline's rules section carries it
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "pipeline", "config", "rules.yaml"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var rules = File.ReadAllText(Path.Combine(dir!, "pipeline", "config", "rules.yaml"));
        Assert.Contains($"\n  {EventBook.Scud}:\n    m2_per_point:", rules);
        var zones = File.ReadAllText(Path.Combine(dir, "pipeline", "src", "drycastle_pipeline", "edits", "zones.py"));
        Assert.Contains($"SCUD = \"{EventBook.Scud}\"", zones);
    }
}
