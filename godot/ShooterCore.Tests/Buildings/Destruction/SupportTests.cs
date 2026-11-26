using System.Diagnostics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;
using Xunit.Abstractions;
using HouseSupport = Drycastle.ShooterCore.Buildings.Destruction.Support;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

/// <summary>Support.Settle (D6): detach, load, crumble. Houses are shaped with HouseCells.Remove, not a carve.</summary>
public class SupportTests(ITestOutputHelper output)
{
    // TestHouses.Box(40, 32, 28): margin 1, foundation z 0, floor z 1, walls z 2..11, ceiling z 12, gable roof above
    const int Nx = 40, Ny = 32, Nz = 28, Ceil = 12;
    static readonly HouseGrid G = TestHouses.Box(Nx, Ny, Nz);
    static readonly DestructionConfig Cfg = DestructionConfig.Default;

    static HouseCells Shaped(Func<int, int, int, CellMaterial, bool> remove)
    {
        var c = new HouseCells(G);
        for (var i = 0; i < G.Count; i++)
        {
            if (!c.Solid(i)) continue;
            var (x, y, z) = G.Coords(i);
            if (remove(x, y, z, G.Material(i))) c.Remove(i);
        }
        return c;
    }

    static IEnumerable<int> Cells(HouseCells c, CellMaterial m) => Enumerable.Range(0, G.Count).Where(i => c.Solid(i) && G.Material(i) == m);
    static int[] Live(HouseCells c) => Enumerable.Range(0, G.Count).Where(c.Solid).ToArray();

    [Fact]
    public void An_intact_house_is_stable()
    {
        var c = new HouseCells(G);
        var (crumbled, fallen) = HouseSupport.Settle(c, Cfg);
        Assert.Empty(crumbled);
        Assert.Empty(fallen);
        Assert.False(c.Damaged);
    }

    [Fact]
    public void Cutting_every_wall_under_the_roof_drops_the_roof_as_one_part()
    {
        var c = Shaped((_, _, z, m) => m == CellMaterial.Wall && z < Ceil);
        var roof = Cells(c, CellMaterial.Roof).ToArray();
        var ceiling = Cells(c, CellMaterial.Ceiling).ToArray();
        var (crumbled, fallen) = HouseSupport.Settle(c, Cfg);
        Assert.Empty(crumbled);
        var part = Assert.Single(fallen);
        Assert.Subset(part.ToHashSet(), roof.Concat(ceiling).ToHashSet());
        Assert.True(part.SequenceEqual(part.Order()));
        Assert.Empty(Cells(c, CellMaterial.Roof));
        Assert.Empty(Cells(c, CellMaterial.Ceiling));
        Assert.NotEmpty(Cells(c, CellMaterial.Floor));    // the floor stays
    }

    [Fact]
    public void A_roof_on_a_one_cell_column_crumbles_the_neck_and_falls()
    {
        // every wall under the ceiling gone but the corner column (1, 1)
        var c = Shaped((x, y, z, m) => m == CellMaterial.Wall && z < Ceil && !(x == 1 && y == 1));
        var column = Enumerable.Range(2, Ceil - 2).Select(z => G.IndexOf(1, 1, z)).ToArray();
        var (crumbled, fallen) = HouseSupport.Settle(c, Cfg);
        Assert.NotEmpty(crumbled.Intersect(column));
        Assert.NotEmpty(fallen);
        Assert.Empty(Cells(c, CellMaterial.Roof));
        Assert.Empty(Cells(c, CellMaterial.Ceiling));
        Assert.Equal((Nx - 2) * (Ny - 2), Cells(c, CellMaterial.Floor).Count());
        output.WriteLine($"crumbled {crumbled.Length} ({crumbled.Count(i => G.Material(i) == CellMaterial.Wall)} wall), fallen {string.Join(", ", fallen.Select(f => f.Length))}");
    }

    [Fact]
    public void A_house_that_lost_its_front_and_one_side_wall_stands()
    {
        var c = Shaped((x, y, z, m) => m == CellMaterial.Wall && z < Ceil && (y == 1 || x == 1));
        var (crumbled, fallen) = HouseSupport.Settle(c, Cfg);
        Assert.Empty(crumbled);
        Assert.Empty(fallen);
    }

    [Fact]
    public void A_panel_cut_out_of_a_wall_falls_as_one_part()
    {
        // a frame cut around x 4..12, z 4..8 of the front wall (y = 1, the door is at x 18..21): the panel inside is loose
        bool Frame(int x, int z) => (x is 3 or 13 && z is >= 3 and <= 9) || (z is 3 or 9 && x is >= 3 and <= 13);
        var c = Shaped((x, y, z, _) => y == 1 && Frame(x, z));
        var panel = (from z in Enumerable.Range(4, 5) from x in Enumerable.Range(4, 9) select G.IndexOf(x, 1, z)).Order().ToArray();
        var (crumbled, fallen) = HouseSupport.Settle(c, Cfg);
        Assert.Empty(crumbled);
        Assert.Equal(panel, Assert.Single(fallen));
        Assert.All(panel, i => Assert.False(c.Solid(i)));
    }

    [Fact]
    public void The_span_rule_crumbles_a_ceiling_too_far_from_a_wall()
    {
        var cfg = DestructionConfig.Parse("""{ "version": 1, "support": { "max_passes": 4, "span_cells": 6 } }""");
        var c = new HouseCells(G);
        var (crumbled, _) = HouseSupport.Settle(c, cfg);
        Assert.Contains(G.IndexOf(Nx / 2, Ny / 2, Ceil), crumbled);
        Assert.DoesNotContain(G.IndexOf(4, 1, Ceil), crumbled);              // 3 sideways from the corner
        Assert.DoesNotContain(crumbled, i => G.Material(i) == CellMaterial.Wall && G.Coords(i).Z < Ceil);
    }

    [Fact]
    public void Settle_is_deterministic()
    {
        HouseCells Run(out (int[] C, IReadOnlyList<int[]> F) r)
        {
            var c = Shaped((x, y, z, m) => m == CellMaterial.Wall && z < Ceil && !(x == 1 && y == 1));
            r = HouseSupport.Settle(c, Cfg);
            return c;
        }
        var a = Run(out var ra);
        HouseSupport.Settle(Shaped((_, y, z, _) => y == 1 && z == 5), Cfg);   // other work between, through the same buffers
        var b = Run(out var rb);
        Assert.Equal(ra.C, rb.C);
        Assert.Equal(ra.F.Count, rb.F.Count);
        for (var k = 0; k < ra.F.Count; k++) Assert.Equal(ra.F[k], rb.F[k]);
        Assert.Equal(Live(a), Live(b));
    }

    [Fact]
    public void Benchmark_an_rpg_hole_and_settle_on_40x32x28()
    {
        // an RPG-sized hole (r = 2 m = 8 cells) in the front wall, then Settle; the carve is WP-A's, so the sphere is removed by hand
        var c = new HouseCells(G);
        var sphere = Enumerable.Range(0, G.Count).Where(i =>
        {
            var (x, y, z) = G.Coords(i);
            int dx = x - Nx / 2, dy = y - 1, dz = z - 6;
            return G.Material(i) != CellMaterial.Foundation && dx * dx + dy * dy + dz * dz <= 64;
        }).ToArray();
        var times = new List<double>();
        for (var k = 0; k < 60; k++)
        {
            c.Restore();
            foreach (var i in sphere) c.Remove(i);
            var t = Stopwatch.GetTimestamp();
            HouseSupport.Settle(c, Cfg);
            times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        times.Sort();
        var median = times[times.Count / 2];
        output.WriteLine($"Settle 40x32x28, rpg hole: median {median:F3} ms, min {times[0]:F3} ms");
        Assert.True(median < 10, $"Settle took {median:F3} ms (budget: 1 ms with the carve, in Release)");
    }
}
