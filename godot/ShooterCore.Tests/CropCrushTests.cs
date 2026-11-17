using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class CropCrushTests
{
    static readonly CropCrushConfig Cfg = new();
    static Vector2[] Rect(float x0, float y0, float x1, float y1) => new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

    [Fact]
    public void The_grid_is_the_fields_bounding_box_in_metre_cells_and_a_huge_field_is_not_tracked()
    {
        var g = CrushGrid.For(Rect(10.5f, -20.2f, 50.5f, 9.8f), Cfg)!;
        Assert.Equal(10, g.OriginX); Assert.Equal(-21, g.OriginZ);
        Assert.Equal(41, g.Width); Assert.Equal(31, g.Height);
        Assert.Equal(new Vector2(10.5f, -20.5f), g.CentreOf(0));
        Assert.Null(CrushGrid.For(Rect(0, 0, 2000, 2000), Cfg));
        Assert.NotNull(CrushGrid.For(Rect(0, 0, 684, 400), Cfg)); // GP-0084 at H8 is ~273k cells
        Assert.Null(CrushGrid.For(new[] { Vector2.Zero, Vector2.One }, Cfg));
    }

    [Fact]
    public void A_strip_along_a_path_crushes_the_cells_in_its_width_and_driving_it_again_adds_nothing()
    {
        var g = CrushGrid.For(Rect(0, 0, 100, 40), Cfg)!;
        var half = CropCrushRules.HalfStrip(Cfg, 1.1f); // a car 2.2 m wide
        var first = g.Mark(new Vector2(10, 20), new Vector2(40, 20), half, 0);
        Assert.NotEmpty(first);
        Assert.Equal(first.OrderBy(i => i).ToList(), first); // ascending
        // about 30 m long and 2 cells wide
        Assert.InRange(first.Count, 55, 75);
        Assert.All(first, i => Assert.InRange(MathF.Abs(g.CentreOf(i).Y - 20), 0, half + 1e-3f));
        Assert.Empty(g.Mark(new Vector2(10, 20), new Vector2(40, 20), half, g.Count)); // the same ground again
        Assert.Empty(g.Mark(new Vector2(40, 20), new Vector2(10, 20.2f), half, g.Count)); // and back
        var more = g.Mark(new Vector2(40, 20), new Vector2(60, 20), half, g.Count); // only the new stretch
        Assert.InRange(more.Count, 35, 45);
        Assert.Equal(first.Count + more.Count, g.Count);
    }

    [Fact]
    public void Only_a_moving_vehicle_crushes_and_a_driverless_one_only_when_it_rolls_properly()
    {
        Assert.False(CropCrushRules.Crushes(Cfg, 0.2f, true));
        Assert.True(CropCrushRules.Crushes(Cfg, 1f, true));
        Assert.True(CropCrushRules.Crushes(Cfg, -3f, true)); // reversing too
        Assert.False(CropCrushRules.Crushes(Cfg, 1f, false));
        Assert.True(CropCrushRules.Crushes(Cfg, 5f, false));
    }

    [Fact]
    public void The_total_and_the_per_field_size_are_bounded()
    {
        var cfg = new CropCrushConfig { MaxCellsTotal = 50 };
        var g = CrushGrid.For(Rect(0, 0, 100, 100), cfg)!;
        var cells = g.Mark(new Vector2(0, 50), new Vector2(100, 50), 2, 0);
        Assert.Equal(50, cells.Count); // stops at the cap
        Assert.Empty(g.Mark(new Vector2(0, 80), new Vector2(100, 80), 2, 50));
        Assert.False(g.Set(7, 50));
    }

    [Fact]
    public void A_delta_and_a_whole_mask_round_trip_in_small_messages()
    {
        var g = CrushGrid.For(Rect(0, 0, 300, 200), Cfg)!;
        var half = 1.0f;
        var sent = new List<int>();
        for (var x = 0; x < 290; x += 2) sent.AddRange(g.Mark(new Vector2(x, 30), new Vector2(x + 2, 30), half, g.Count));
        for (var x = 290; x > 0; x -= 2) sent.AddRange(g.Mark(new Vector2(x, 90), new Vector2(x - 2, 90), half, g.Count));
        // the deltas of a drive, in messages under the cap
        var msgs = CrushCodec.PackDelta(7, sent.OrderBy(i => i).ToList(), Cfg.MaxMessageBytes);
        Assert.All(msgs, m => Assert.True(m.Length <= Cfg.MaxMessageBytes));
        var back = msgs.SelectMany(m => CrushCodec.Cells(CrushCodec.Decode(m)!.Value)).OrderBy(i => i).ToList();
        Assert.Equal(sent.OrderBy(i => i).ToList(), back);
        Assert.All(msgs, m => Assert.Equal((ushort)7, CrushCodec.Decode(m)!.Value.Field));
        // a late joiner gets the whole mask as runs: far fewer bytes than the cells' indices
        var runMsgs = CrushCodec.PackRuns(7, g.Runs(), Cfg.MaxMessageBytes);
        Assert.All(runMsgs, m => Assert.True(m.Length <= Cfg.MaxMessageBytes));
        var again = CrushGrid.For(Rect(0, 0, 300, 200), Cfg)!;
        foreach (var m in runMsgs) foreach (var c in CrushCodec.Cells(CrushCodec.Decode(m)!.Value)) again.Set(c, again.Count);
        Assert.Equal(g.Count, again.Count);
        for (var i = 0; i < g.CellCount; i += 7) Assert.Equal(g.IsSet(i), again.IsSet(i));
        Assert.True(runMsgs.Sum(m => m.Length) < msgs.Sum(m => m.Length) + 400);
        Assert.Null(CrushCodec.Decode(new byte[2]));
    }

    [Fact]
    public void A_mask_larger_than_the_message_cap_still_fits_in_a_bounded_number_of_messages()
    {
        var g = CrushGrid.For(Rect(0, 0, 250, 250), Cfg)!; // 62500 cells, the worst the cap lets in: alternating cells
        for (var i = 0; i < g.CellCount; i += 2) g.Set(i, 0);
        var msgs = CrushCodec.PackRuns(1, g.Runs(), Cfg.MaxMessageBytes);
        Assert.True(msgs.Count < 1000, $"{msgs.Count} messages");
        var total = msgs.Sum(m => m.Length);
        Assert.True(total < 70000, $"{total} bytes"); // about one byte per cell at worst, 62500 cells
        var back = CrushGrid.For(Rect(0, 0, 250, 250), Cfg)!;
        foreach (var m in msgs) foreach (var c in CrushCodec.Cells(CrushCodec.Decode(m)!.Value)) back.Set(c, 0);
        Assert.Equal(g.Count, back.Count);
    }

    // ---- CropCrushMask: what the server and a client both run (#353) ----

    static CropCrushMask MaskOf(CropCrushConfig? cfg = null) => new(cfg ?? Cfg, new[] { new CropField("GP-1", "crop_grain", Rect(0, 0, 200, 100)) });

    /// <summary>A 4.3 m long, 2.2 m wide car at (x, z) heading east (yaw -90 degrees: forward is +x).</summary>
    static CrushBody East(float x, float z = 50, float speed = 10, bool driven = true, bool ground = true) => new(new Vector2(x, z), -MathF.PI / 2, 1.1f, 2.15f, speed, driven, ground);

    [Fact]
    public void The_strip_is_stamped_from_the_leading_end_not_the_centre()
    {
        var mask = MaskOf(); var fresh = new List<int>();
        for (var x = 20f; x <= 40f; x += 0.1f) mask.Step(1, East(x), fresh);
        var grid = mask.GridOf(0)!;
        var maxX = fresh.Max(i => grid.CentreOf(i).X);
        // the car's centre reached 40 (and its sampler's last segment ends at or before it): the nose is 2.15 m further, the strip's rounded end stops at the bumper
        Assert.InRange(maxX, 40f, 42.4f);
        Assert.True(maxX > 40f);
        Assert.Equal(fresh.Count, grid.Count);
        Assert.Equal(fresh.Count, mask.Total);
        var lead = CropCrushRules.LeadM(Cfg, 1.1f, 2.15f);
        Assert.Equal(2.15f - 1.1f * 0.95f, lead, 3);
    }

    [Fact]
    public void Reversing_leads_with_the_rear_and_turning_round_starts_the_path_afresh()
    {
        var mask = MaskOf(); var fresh = new List<int>();
        for (var x = 100f; x >= 60f; x -= 0.1f) mask.Step(1, East(x), fresh); // yaw east, moving west: backing up
        var grid = mask.GridOf(0)!;
        var minX = fresh.Min(i => grid.CentreOf(i).X);
        Assert.True(minX < 62f, $"{minX}"); // the rear is the leading end: it went at least about to the centre's x minus the lead
        Assert.InRange(fresh.Max(i => grid.CentreOf(i).X), 0f, 100f); // nothing ahead of the start
    }

    [Fact]
    public void Standing_slow_or_off_the_ground_stamps_nothing_and_a_driverless_one_needs_speed()
    {
        var mask = MaskOf(); var fresh = new List<int>();
        for (var x = 20f; x <= 40f; x += 0.1f)
        {
            Assert.Equal(-1, mask.Step(1, East(x, speed: 0.2f), fresh));
            Assert.Equal(-1, mask.Step(2, East(x, speed: 10, ground: false), fresh)); // flying, afloat or wrecked
            Assert.Equal(-1, mask.Step(3, East(x, speed: 1f, driven: false), fresh));
        }
        Assert.Empty(fresh); Assert.Equal(0, mask.Total);
    }

    [Fact]
    public void Off_the_field_nothing_is_stamped_and_the_path_starts_afresh_on_entering()
    {
        var mask = MaskOf(); var fresh = new List<int>();
        for (var x = -30f; x <= -1f; x += 0.1f) Assert.Equal(-1, mask.Step(1, East(x), fresh));
        Assert.Empty(fresh);
        for (var x = 0f; x <= 30f; x += 0.1f) mask.Step(1, East(x), fresh);
        Assert.NotEmpty(fresh);
    }

    [Fact]
    public void The_samples_are_spaced_by_the_crush_config_not_the_harvesters()
    {
        var wide = MaskOf(new CropCrushConfig { SampleM = 10 }); var tight = MaskOf(new CropCrushConfig { SampleM = 0.5f });
        var a = new List<int>(); var b = new List<int>();
        for (var x = 20f; x <= 28f; x += 0.1f) { wide.Step(1, East(x), a); tight.Step(1, East(x), b); }
        // 8 m of travel: a sample every 10 m has stamped nothing yet, one every 0.5 m has
        Assert.Empty(a); Assert.NotEmpty(b);
        Assert.Equal(2f, new CropCrushConfig().SampleM);
    }

    [Fact]
    public void The_client_stamps_what_it_drew_and_the_servers_cells_add_only_what_is_new()
    {
        var client = MaskOf(); var server = MaskOf();
        var mine = new List<int>(); var theirs = new List<int>();
        // the client draws the car ahead of the server's: its stamp reaches further
        for (var x = 20f; x <= 45f; x += 0.1f) client.Step(1, East(x), mine);
        for (var x = 20f; x <= 40f; x += 0.1f) server.Step(1, East(x), theirs);
        var before = client.Total;
        var added = theirs.Count(i => client.Set(0, i));
        Assert.Equal(before + added, client.Total);
        Assert.InRange(added, 0, 4); // what the client had not drawn itself (an edge cell at most)
        Assert.All(theirs, i => Assert.True(client.GridOf(0)!.IsSet(i)));
        Assert.False(client.Set(0, theirs[0])); // again: nothing
        Assert.False(client.Set(5, 0)); // no such field
    }

    [Fact]
    public void A_new_round_clears_the_masks_and_the_total_and_every_path()
    {
        var mask = MaskOf(); var fresh = new List<int>();
        for (var x = 20f; x <= 40f; x += 0.1f) mask.Step(1, East(x), fresh);
        Assert.True(mask.Total > 0);
        mask.Clear();
        Assert.Equal(0, mask.Total); Assert.Empty(mask.Grids);
        fresh.Clear();
        Assert.Equal(-1, mask.Step(1, East(40.1f), fresh)); // the path starts afresh: the first sample only marks the start
        Assert.Empty(fresh);
        for (var x = 40.2f; x <= 60f; x += 0.1f) mask.Step(1, East(x), fresh);
        Assert.NotEmpty(fresh);
    }

    [Fact]
    public void The_total_cap_holds_over_stamping_and_the_servers_cells()
    {
        var mask = MaskOf(new CropCrushConfig { MaxCellsTotal = 30 }); var fresh = new List<int>();
        for (var x = 20f; x <= 120f; x += 0.1f) mask.Step(1, East(x), fresh);
        Assert.Equal(30, mask.Total);
        Assert.Equal(30, fresh.Count);
        Assert.False(mask.Set(0, 5)); // full
    }

    [Fact]
    public void A_flush_every_tenth_of_a_second_of_a_fast_car_stays_far_under_the_message_cap()
    {
        Assert.Equal(0.1f, Cfg.FlushS);
        var mask = MaskOf(); var fresh = new List<int>();
        var worst = 0;
        // 40 m/s (a fast car), 60 Hz, one flush per 0.1 s: the bytes of each flush
        var cells = new List<int>(); var t = 0f; var next = 0.1f;
        for (var x = 10f; x < 190f; x += 40f / 60f, t += 1f / 60f)
        {
            fresh.Clear(); mask.Step(1, East(x), fresh); cells.AddRange(fresh);
            if (t < next) continue;
            next = t + Cfg.FlushS; cells.Sort();
            worst = Math.Max(worst, CrushCodec.PackDelta(0, cells, Cfg.MaxMessageBytes).Sum(m => m.Length));
            cells.Clear();
        }
        Assert.InRange(worst, 1, 120);
    }
}
