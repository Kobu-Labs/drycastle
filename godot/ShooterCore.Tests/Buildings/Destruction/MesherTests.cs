using System.Diagnostics;
using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;
using Xunit.Abstractions;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class MesherTests(ITestOutputHelper log)
{
    static readonly (CellMaterial, byte, byte, byte)[] Pal = [(CellMaterial.Air, 0, 0, 0), (CellMaterial.Wall, 200, 200, 200), (CellMaterial.Roof, 150, 50, 40)];

    static HouseGrid Grid(int nx, int ny, int nz, Func<int, int, int, byte> at)
    {
        var cells = new byte[nx * ny * nz];
        for (var z = 0; z < nz; z++) for (var y = 0; y < ny; y++) for (var x = 0; x < nx; x++) cells[x + nx * (y + ny * z)] = at(x, y, z);
        return new HouseGrid(0, "t", Vector3.Zero, 0, 0.25f, nx, ny, nz, Pal, cells);
    }

    static readonly (int X, int Y, int Z)[] Dirs = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>The unit faces (cell, direction) the quads cover; fails on overlap, a bad winding or a palette that differs from the cell's.</summary>
    static HashSet<(int Cell, int Dir)> Rasterise(HouseGrid g, List<Quad> quads)
    {
        var set = new HashSet<(int, int)>();
        foreach (var q in quads)
        {
            Assert.Equal(q.Normal, Vector3.Normalize(Vector3.Cross(q.U, q.V)));
            var dir = Array.IndexOf(Dirs, ((int)q.Normal.X, (int)q.Normal.Y, (int)q.Normal.Z));
            Assert.True(dir >= 0);
            int w = (int)q.U.Length(), h = (int)q.V.Length();
            Vector3 eu = q.U / w, ev = q.V / h;
            for (var i = 0; i < w; i++)
                for (var j = 0; j < h; j++)
                {
                    var p = q.Corner + eu * (i + 0.5f) + ev * (j + 0.5f) - q.Normal * 0.5f;
                    var cell = g.IndexOf((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));
                    Assert.Equal(g.Cells.Span[cell], q.Palette);
                    Assert.True(set.Add((cell, dir)), "overlapping quads");
                }
        }
        return set;
    }

    /// <summary>Brute force: the faces of solid cells (by isSolid) whose neighbour passes nbOk (null = outside the grid).</summary>
    static HashSet<(int, int)> Expected(HouseGrid g, Func<int, bool> isSolid, Func<int?, bool> nbOk)
    {
        var set = new HashSet<(int, int)>();
        for (var i = 0; i < g.Count; i++)
        {
            if (!isSolid(i)) continue;
            var (x, y, z) = g.Coords(i);
            for (var d = 0; d < 6; d++)
            {
                int ax = x + Dirs[d].X, ay = y + Dirs[d].Y, az = z + Dirs[d].Z;
                if (nbOk(g.InBounds(ax, ay, az) ? g.IndexOf(ax, ay, az) : null)) set.Add((i, d));
            }
        }
        return set;
    }

    static HouseCells Carved(HouseGrid g, Vector3 centre, float r)
    {
        var c = new HouseCells(g);
        for (var i = 0; i < g.Count; i++)
        {
            var (x, y, z) = g.Coords(i);
            if (g.Material(i) != CellMaterial.Foundation && Vector3.Distance(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), centre) <= r) c.Remove(i);
        }
        return c;
    }

    [Fact]
    public void One_cell_has_six_faces()
    {
        var c = new HouseCells(Grid(3, 3, 3, (x, y, z) => (byte)(x == 1 && y == 1 && z == 1 ? 1 : 0)));
        var q = new List<Quad>();
        HouseMesher.Surface(c, q);
        Assert.Equal(6, q.Count);
        Assert.Equal(6, q.Select(f => f.Normal).Distinct().Count());
    }

    [Fact]
    public void Faces_merge_by_palette()
    {
        var q = new List<Quad>();
        HouseMesher.Surface(new HouseCells(Grid(4, 3, 3, (x, y, z) => (byte)(y == 1 && z == 1 && x is 1 or 2 ? 1 : 0))), q);
        Assert.Equal(6, q.Count);
        q.Clear();
        HouseMesher.Surface(new HouseCells(Grid(4, 3, 3, (x, y, z) => (byte)(y == 1 && z == 1 && x is 1 or 2 ? x : 0))), q);
        Assert.Equal(10, q.Count);
    }

    [Fact]
    public void Surfaces_of_a_carved_house_match_brute_force()
    {
        var g = TestHouses.Box(20, 16, 18);
        var c = Carved(g, new Vector3(10, 1.5f, 6), 4);
        var far = Carved(g, new Vector3(19, 15, 12), 3); // a second hole at the grid's corner: the cut region spans both and is clipped
        for (var i = 0; i < g.Count; i++) if (!far.Solid(i)) c.Remove(i);
        Assert.True(c.Damaged);
        var q = new List<Quad>();
        HouseMesher.Surface(c, q);
        var whole = Expected(g, c.Solid, n => n is not { } j || !c.Solid(j));
        Assert.Equal(whole, Rasterise(g, q));
        // collider columns: the regions that tile the grid give the same faces, none twice
        q.Clear();
        for (var y0 = 0; y0 < g.Ny; y0 += 7)
            for (var x0 = 0; x0 < g.Nx; x0 += 6)
                HouseMesher.Surface(c, new CellBox(x0, y0, 0, Math.Min(x0 + 6, g.Nx), Math.Min(y0 + 7, g.Ny), g.Nz), q);
        Assert.Equal(whole, Rasterise(g, q));
    }

    [Fact]
    public void Piece_surface_matches_brute_force()
    {
        var g = TestHouses.Box(20, 16, 18);
        var piece = Enumerable.Range(0, g.Count).Where(i => g.Material(i) == CellMaterial.Roof && g.Coords(i).X < 12).ToArray();
        var set = piece.ToHashSet();
        var q = new List<Quad>();
        HouseMesher.Surface(g, piece, q);
        Assert.Equal(Expected(g, set.Contains, n => n is not { } j || !set.Contains(j)), Rasterise(g, q));
    }

    [Fact]
    public void Boxes_cover_exactly_the_solid_cells()
    {
        var g = TestHouses.Box(20, 16, 18);
        var c = Carved(g, new Vector3(3, 8, 9), 5);
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        HouseMesher.Boxes(c, boxes);
        var cover = new int[g.Count];
        foreach (var (min, max) in boxes)
            for (var z = (int)min.Z; z < (int)max.Z; z++) for (var y = (int)min.Y; y < (int)max.Y; y++) for (var x = (int)min.X; x < (int)max.X; x++)
                cover[g.IndexOf(x, y, z)]++;
        for (var i = 0; i < g.Count; i++) Assert.Equal(c.Solid(i) ? 1 : 0, cover[i]);
        Assert.True(boxes.Count < Enumerable.Range(0, g.Count).Count(c.Solid) / 4, $"{boxes.Count} boxes is barely merged");
    }

    static double BestMs(Action a, int runs = 30)
    {
        a();
        var best = double.MaxValue;
        for (var r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void Benchmark_40x32x28()
    {
        var g = TestHouses.Box(40, 32, 28);
        var c = Carved(g, new Vector3(20, 1.5f, 8), 8); // an RPG (2 m) on the front wall
        var q = new List<Quad>(4096);
        var b = new List<(Vector3, Vector3)>(4096);
        var all = BestMs(() => { q.Clear(); HouseMesher.Surface(c, q); });
        var allN = q.Count;
        var box = BestMs(() => { b.Clear(); HouseMesher.Boxes(c, b); });
        var roof = Enumerable.Range(0, g.Count).Where(i => g.Material(i) == CellMaterial.Roof).ToArray();
        var piece = BestMs(() => { q.Clear(); HouseMesher.Surface(g, roof, q); });
        var before = GC.GetAllocatedBytesForCurrentThread();
        q.Clear(); HouseMesher.Surface(c, q); b.Clear(); HouseMesher.Boxes(c, b);
        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"AllFaces {all:F3} ms ({allN} quads), Boxes {box:F3} ms ({b.Count} boxes), roof piece {piece:F3} ms ({roof.Length} cells), warm alloc {alloc} B");
        Assert.True(box < Budget.Ms(2), $"Boxes {box:F3} ms");
        Assert.Equal(0, alloc);
    }
}
