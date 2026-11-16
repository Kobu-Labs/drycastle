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
        Assert.Null(CrushGrid.For(Rect(0, 0, 1000, 1000), Cfg));
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
}
