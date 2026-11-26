using System.Diagnostics;
using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;
using Xunit.Abstractions;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class FractureTests(ITestOutputHelper log)
{
    static readonly FractureConfig Cfg = new();

    static int[] Roof(HouseGrid g) => Enumerable.Range(0, g.Count).Where(i => g.Material(i) == CellMaterial.Roof).ToArray();

    static bool Connected(HouseGrid g, int[] piece)
    {
        var set = piece.ToHashSet();
        var seen = new HashSet<int> { piece[0] };
        var q = new Queue<int>(seen);
        while (q.Count > 0)
        {
            var (x, y, z) = g.Coords(q.Dequeue());
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
            {
                if (!g.InBounds(x + dx, y + dy, z + dz)) continue;
                var j = g.IndexOf(x + dx, y + dy, z + dz);
                if (set.Contains(j) && seen.Add(j)) q.Enqueue(j);
            }
        }
        return seen.Count == piece.Length;
    }

    [Fact]
    public void Pieces_cover_every_cell_once_are_connected_and_dust_is_last()
    {
        var g = TestHouses.Box(24, 18, 20);
        var cells = Roof(g).Reverse().ToArray(); // any order in
        var parts = Fracture.Split(g, cells, new Vector3(12, 9, 18), 7, Cfg);
        Assert.Equal(cells.Order(), parts.SelectMany(p => p).Order());
        var pieces = parts.Take(parts.Count - 1).ToList();
        Assert.True(pieces.Count >= cells.Length / Cfg.CellsPerPiece / 2, $"{pieces.Count} pieces of {cells.Length} cells");
        Assert.All(pieces, p => Assert.True(p.Length >= Cfg.DustBelowCells && Connected(g, p)));
        Assert.All(parts, p => Assert.Equal(p.Order(), p));
    }

    [Fact]
    public void Split_is_deterministic_and_the_seed_matters()
    {
        var g = TestHouses.Box(24, 18, 20);
        var cells = Roof(g);
        var a = Fracture.Split(g, cells, new Vector3(5, 3, 15), 42, Cfg);
        var b = Fracture.Split(g, (int[])cells.Clone(), new Vector3(5, 3, 15), 42, Cfg);
        Assert.Equal(a, b);
        var c = Fracture.Split(g, cells, new Vector3(5, 3, 15), 43, Cfg);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Small_component_is_one_piece_or_dust()
    {
        var g = TestHouses.Box(10, 10, 10, storey: 3);
        var cells = Roof(g).Take(30).ToArray();
        Assert.Equal(30, cells.Length);
        var parts = Fracture.Split(g, cells, Vector3.Zero, 1, Cfg);
        Assert.Equal(cells.Order(), parts.SelectMany(p => p).Order());
        var tiny = Fracture.Split(g, cells.Take(3).ToArray(), Vector3.Zero, 1, Cfg);
        Assert.Single(tiny); // only the dust group
        Assert.Equal(3, tiny[^1].Length);
        Assert.Single(Fracture.Split(g, [], Vector3.Zero, 1, Cfg));
    }

    [Fact]
    public void Seeds_pack_closer_near_the_impact()
    {
        var g = TestHouses.Box(40, 32, 28);
        var cells = Enumerable.Range(0, g.Count).Where(i => g.Material(i) == CellMaterial.Wall && g.Coords(i).Y == 1).ToArray();
        var impact = new Vector3(2, 1.5f, 6);
        double Mean(IEnumerable<int[]> ps) => ps.Average(p => p.Length);
        var parts = Fracture.Split(g, cells, impact, 5, Cfg with { DustBelowCells = 0 });
        var pieces = parts.Take(parts.Count - 1).ToList();
        var near = pieces.Where(p => p.Average(i => g.Coords(i).X) < 20).ToList();
        var far = pieces.Where(p => p.Average(i => g.Coords(i).X) >= 20).ToList();
        Assert.True(Mean(near) < Mean(far), $"near {Mean(near):F1} far {Mean(far):F1}");
    }

    [Fact]
    public void Benchmark_40x32x28()
    {
        var g = TestHouses.Box(40, 32, 28);
        var roof = Roof(g);
        var house = Enumerable.Range(0, g.Count).Where(i => g.Original(i) && g.Material(i) != CellMaterial.Foundation).ToArray();
        double Best(int[] cells)
        {
            Fracture.Split(g, cells, new Vector3(20, 16, 20), 1, Cfg);
            var best = double.MaxValue;
            for (var r = 0; r < 10; r++)
            {
                var sw = Stopwatch.StartNew();
                Fracture.Split(g, cells, new Vector3(20, 16, 20), (uint)r, Cfg);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }
        log.WriteLine($"Split roof {Best(roof):F3} ms ({roof.Length} cells), whole house {Best(house):F3} ms ({house.Length} cells)");
    }
}
