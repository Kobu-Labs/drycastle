using System.Numerics;
using Drycastle.ShooterCore.Minimap;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Minimap;

public class MapTrailsTests
{
    [Fact]
    public void A_vehicle_gets_a_point_only_every_min_step_and_breaking_starts_a_new_stretch()
    {
        var t = new MapTrails();
        t.Extend(1, new Vector2(0, 0));
        t.Extend(1, new Vector2(1, 0));   // too close
        t.Extend(1, new Vector2(5, 0));
        Assert.Single(t.Trails);
        Assert.Equal(2, t.Trails[0].Points.Count);
        t.Break(1);
        t.Extend(1, new Vector2(6, 0));
        Assert.Equal(2, t.Trails.Count);
        Assert.Equal(3, t.PointCount);
        Assert.Equal(new Vector2(5, 0), t.Trails[0].Max);
    }

    [Fact]
    public void Vehicles_have_their_own_stretches()
    {
        var t = new MapTrails();
        t.Extend(1, Vector2.Zero); t.Extend(2, new Vector2(100, 100));
        t.Extend(1, new Vector2(10, 0)); t.Extend(2, new Vector2(110, 100));
        Assert.Equal(2, t.Trails.Count);
        Assert.All(t.Trails, s => Assert.Equal(2, s.Points.Count));
    }

    [Fact]
    public void Past_the_point_cap_the_oldest_stretch_goes_first_and_an_open_one_is_not_extended_after_it_goes()
    {
        var t = new MapTrails();
        for (var v = 0; v < 3; v++)
            for (var i = 0; i < MapTrails.MaxPoints / 2; i++) t.Extend((ushort)v, new Vector2(i * 10, v * 1000));
        Assert.True(t.PointCount <= MapTrails.MaxPoints);
        Assert.Equal(2, t.Trails.Count); // vehicle 0's stretch was dropped
        t.Extend(0, new Vector2(0, 0)); // it starts afresh, no exception, no stale point
        Assert.Equal(1, t.Trails[^1].Points.Count);
    }

    [Fact]
    public void Server_cells_are_thinned_to_one_dot_per_block_and_capped()
    {
        var t = new MapTrails();
        t.AddCell(new Vector2(0.5f, 0.5f)); t.AddCell(new Vector2(1.5f, 1.5f)); t.AddCell(new Vector2(4.5f, 0.5f));
        Assert.Equal(2, t.Dots.Count);
        for (var i = 0; i < MapTrails.MaxDots * 2; i++) t.AddCell(new Vector2(100 + i * 5, 0));
        Assert.Equal(MapTrails.MaxDots, t.Dots.Count);
    }

    [Fact]
    public void Clear_forgets_everything_and_bumps_the_version()
    {
        var t = new MapTrails();
        t.Extend(1, Vector2.Zero); t.AddCell(Vector2.One);
        var v = t.Version;
        t.Clear();
        Assert.Empty(t.Trails); Assert.Empty(t.Dots); Assert.Equal(0, t.PointCount); Assert.NotEqual(v, t.Version);
        t.AddCell(Vector2.One); Assert.Single(t.Dots); // the blocks went too
    }
}
