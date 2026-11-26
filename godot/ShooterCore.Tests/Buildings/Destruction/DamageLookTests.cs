using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class DamageLookTests
{
    static readonly (int X, int Y, int Z)[] Dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>A box house with a hole through the back wall (y = 14, away from the door) and the ceiling over it.</summary>
    static (HouseCells C, HouseDamageLook L) Holed()
    {
        var g = TestHouses.Box(20, 16, 20);
        var c = new HouseCells(g);
        for (var x = 8; x <= 11; x++)
            for (var z = 5; z <= 13; z++)
                for (var y = 12; y <= 14; y++)
                    if (c.Solid(g.IndexOf(x, y, z))) c.Remove(g.IndexOf(x, y, z));
        var l = new HouseDamageLook(g);
        l.Update(c);
        return (c, l);
    }

    [Fact]
    public void The_attic_is_enclosed_and_the_room_is_not()
    {
        var g = TestHouses.Box(20, 16, 20);
        var e = HouseDamageLook.FindEnclosed(g, out var any);
        Assert.True(any);
        Assert.Equal(0, e[g.IndexOf(10, 8, 5)]);   // the room (open through the door)
        Assert.Equal(1, e[g.IndexOf(10, 8, 14)]);  // over the ceiling slab, under the roof
        Assert.Equal(0, e[g.IndexOf(0, 0, 0)]);
    }

    [Fact]
    public void Near_cells_and_orphan_air_are_hidden_far_cells_are_not()
    {
        var (c, l) = Holed();
        var g = c.Grid;
        Assert.Equal(255, l.Hide[g.IndexOf(10, 14, 8)]);  // removed
        Assert.Equal(255, l.Hide[g.IndexOf(7, 14, 8)]);   // live wall next to the hole
        Assert.Equal(255, l.Hide[g.IndexOf(10, 15, 8)]);  // air just outside the hole
        Assert.Equal(0, l.Hide[g.IndexOf(3, 14, 8)]);     // the wall far off
        Assert.Equal(0, l.Hide[g.IndexOf(3, 15, 8)]);     // the air in front of it
        Assert.Equal(1, l.Near[g.IndexOf(7, 14, 8)]);
        Assert.Equal(0, l.Near[g.IndexOf(5, 14, 8)]);
    }

    [Fact]
    public void Damaged_faces_are_the_near_cells_open_faces_and_the_far_cells_attic_faces()
    {
        var (c, l) = Holed();
        var g = c.Grid;
        var quads = new List<Quad>();
        HouseMesher.Damaged(c, l.Near, l.Enclosed, l.AnyEnclosed, quads);
        var got = new HashSet<(int, int)>();
        foreach (var q in quads)
        {
            var dir = Array.IndexOf(Dirs, ((int)q.Normal.X, (int)q.Normal.Y, (int)q.Normal.Z));
            int w = (int)q.U.Length(), h = (int)q.V.Length();
            var eu = q.U / w; var ev = q.V / h;
            for (var i = 0; i < w; i++)
                for (var j = 0; j < h; j++)
                {
                    var p = q.Corner + eu * (i + 0.5f) + ev * (j + 0.5f) - q.Normal * 0.5f;
                    var cell = g.IndexOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
                    Assert.True(c.Solid(cell));
                    Assert.True(got.Add((cell, dir)), "overlapping quads");
                }
        }
        var want = new HashSet<(int, int)>();
        for (var i = 0; i < g.Count; i++)
        {
            if (!c.Solid(i)) continue;
            var (x, y, z) = g.Coords(i);
            for (var d = 0; d < 6; d++)
            {
                int ax = x + Dirs[d].X, ay = y + Dirs[d].Y, az = z + Dirs[d].Z;
                var inside = g.InBounds(ax, ay, az);
                var nb = inside ? g.IndexOf(ax, ay, az) : -1;
                var open = !inside || !c.Solid(nb);
                if (l.Near[i] != 0 ? open : inside && l.Enclosed[nb] != 0) want.Add((i, d));
            }
        }
        Assert.Equal(want.Count, got.Count);
        Assert.True(want.SetEquals(got));
    }
}
