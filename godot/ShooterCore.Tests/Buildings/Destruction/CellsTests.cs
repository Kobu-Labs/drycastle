using System.Diagnostics;
using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;
using Xunit.Abstractions;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class CellsTests(ITestOutputHelper output)
{
    static readonly DestructionConfig Cfg = DestructionConfig.Default;

    static CarveOp At(HouseGrid g, int x, int y, int z, ushort radiusQ) => new((short)(4 * x + 2), (short)(4 * y + 2), (short)(4 * z + 2), radiusQ);

    [Fact]
    public void From_quantises_to_quarter_cells_in_the_grid_frame()
    {
        var g = TestHouses.Box(20, 16, 24, origin: new Vector3(10, 20, 3), yaw: MathF.PI / 2);
        // yaw 90: grid +x is north, grid +y is west. Centre of cell (4, 2, 6) in map metres:
        var map = g.ToMap(new Vector3(4.5f, 2.5f, 6.5f));
        var op = CarveOp.From(g, map, 2f);
        Assert.Equal(new CarveOp(18, 10, 26, 32), op);
        // rounding to the nearest quarter-cell
        Assert.Equal((short)19, CarveOp.From(g, g.ToMap(new Vector3(4.70f, 2.5f, 6.5f)), 1f)!.Value.Cx);
    }

    [Fact]
    public void From_is_null_when_the_sphere_misses_the_grid()
    {
        var g = TestHouses.Box(20, 16, 24);   // 5 x 4 x 6 m from the origin
        Assert.Null(CarveOp.From(g, new Vector3(-3, 2, 2), 2f));
        Assert.NotNull(CarveOp.From(g, new Vector3(-1.9f, 2, 2), 2f));
        Assert.Null(CarveOp.From(g, new Vector3(2, 2, 2), 0f));
        Assert.Null(CarveOp.From(g, new Vector3(9000, 2, 2), 2f));
        Assert.Null(CarveOp.From(g, new Vector3(float.NaN, 2, 2), 2f));
    }

    [Fact]
    public void Carve_removes_the_cells_inside_the_sphere()
    {
        var c = new HouseCells(TestHouses.Box(20, 16, 24));
        var g = c.Grid;
        // radius one cell, centred on a wall cell: the cell and its 4 wall neighbours (the others are air)
        var carved = c.Carve(At(g, 1, 10, 5, 4), Cfg);
        int[] want = [g.IndexOf(1, 10, 4), g.IndexOf(1, 9, 5), g.IndexOf(1, 10, 5), g.IndexOf(1, 11, 5), g.IndexOf(1, 10, 6)];
        Assert.Equal(want, carved);
        Assert.All(want, i => Assert.False(c.Solid(i)));
        Assert.True(c.Damaged);
        Assert.Empty(c.Carve(At(g, 1, 10, 5, 4), Cfg));    // already gone

        // a bigger op agrees with the rule checked cell by cell
        var d = new HouseCells(g);
        var op = new CarveOp(30, 9, 41, 37);
        var got = d.Carve(op, Cfg).ToHashSet();
        for (var i = 0; i < g.Count; i++)
        {
            var (x, y, z) = g.Coords(i);
            var m = g.Material(i);
            long r = m is CellMaterial.Air or CellMaterial.Foundation ? 0 : Cfg.Resist(m);
            long dx = 4 * x + 2 - op.Cx, dy = 4 * y + 2 - op.Cy, dz = 4 * z + 2 - op.Cz;
            var inside = r > 0 && (dx * dx + dy * dy + dz * dz) * r * r <= (long)op.RadiusQ * op.RadiusQ * 256;
            Assert.Equal(inside, got.Contains(i));
        }
        Assert.NotEmpty(got);
    }

    [Fact]
    public void Foundation_is_never_carved()
    {
        var c = new HouseCells(TestHouses.Box(20, 16, 24));
        var g = c.Grid;
        var carved = c.Carve(At(g, 10, 8, 0, 4 * 20), Cfg);
        Assert.Contains(g.IndexOf(10, 8, 1), carved);   // floor slab goes
        for (var i = 0; i < g.Count; i++)
            if (g.Material(i) == CellMaterial.Foundation) Assert.True(c.Solid(i));
    }

    [Fact]
    public void Resist_scales_the_radius()
    {
        // centred on the y = 1 wall at the lintel row (z = 11, whole wall); radius 4 cells: count the row's carved cells
        var g = TestHouses.Box(40, 16, 24);
        var op = At(g, 20, 1, 11, 4 * 4);
        int Reach(DestructionConfig cfg)
        {
            var c = new HouseCells(g);
            c.Carve(op, cfg);
            var n = 0;
            for (var x = 0; x < g.Nx; x++) if (g.Original(g.IndexOf(x, 1, 11)) && !c.Solid(g.IndexOf(x, 1, 11))) n++;
            return n;
        }
        Assert.Equal(9, Reach(Cfg));                    // resist 1.0: |dx| <= 4
        Assert.Equal(5, Reach(WithWallResist("2.0")));  // resist 2.0: |dx| <= 2
        Assert.Equal(17, Reach(WithWallResist("0.5"))); // resist 0.5: |dx| <= 8
        Assert.Equal(0, Reach(WithWallResist("0")));    // resist 0: never
    }

    static DestructionConfig WithWallResist(string resist) => DestructionConfig.Parse(
        """{"version":1,"materials":{"foundation":{"resist":0},"floor":{"resist":1.2},"wall":{"resist":""" + resist +
        """},"partition":{"resist":0.6},"ceiling":{"resist":0.9},"roof":{"resist":0.5}}}""");

    [Fact]
    public void Removed_runs_round_trip_and_hash_follows_the_state()
    {
        var g = TestHouses.Box(24, 20, 28);
        var a = new HouseCells(g);
        var whole = a.Hash();
        Assert.Empty(a.EncodeRemoved());
        a.Carve(At(g, 1, 5, 5, 12), Cfg);
        a.Carve(At(g, 12, 10, 14, 30), Cfg);
        a.Remove(g.IndexOf(1, 1, 0));               // the very first solid cell: first kept run is 0
        Assert.NotEqual(whole, a.Hash());
        var runs = a.EncodeRemoved();
        Assert.Equal(0, runs[0]);

        var b = new HouseCells(g);
        b.DecodeRemoved(runs, 7);
        Assert.Equal(7, b.OpSeq);
        Assert.Equal(a.Hash(), b.Hash());
        for (var i = 0; i < g.Count; i++) Assert.Equal(a.Solid(i), b.Solid(i));
        Assert.Equal(runs, b.EncodeRemoved());

        // decoding replaces the state; empty runs = whole
        b.DecodeRemoved([], 0);
        Assert.False(b.Damaged);
        Assert.Equal(whole, b.Hash());
        // runs past the solid cells are refused and leave the state alone
        b.DecodeRemoved(runs, 3);
        Assert.Throws<InvalidDataException>(() => b.DecodeRemoved([0x80, 0x80, 0x80, 0x01], 9));
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(3, b.OpSeq);

        a.Restore();
        Assert.Equal(whole, a.Hash());
        Assert.Equal(0, a.OpSeq);
    }

    [Fact]
    public void Hash_is_fnv1a_over_the_bitmask_bytes()
    {
        var g = TestHouses.Box(9, 7, 6);   // 378 cells: 48 bytes, the last one partial
        var c = new HouseCells(g);
        c.Remove(g.IndexOf(1, 3, 3));
        var bytes = new byte[(g.Count + 7) / 8];
        var i0 = g.IndexOf(1, 3, 3);
        bytes[i0 >> 3] |= (byte)(1 << (i0 & 7));
        var h = 2166136261u;
        foreach (var b in bytes) { h ^= b; h *= 16777619u; }
        Assert.Equal(h, c.Hash());
    }

    [Fact]
    public void Carving_is_deterministic()
    {
        var g = TestHouses.Box(40, 32, 28, yaw: 0.7f, origin: new Vector3(100, -50, 2));
        CarveOp[] ops = [.. Enumerable.Range(0, 12).Select(k => CarveOp.From(g, g.ToMap(new Vector3(3 + k * 3.1f, 1 + k * 2.3f % 30, 3 + k * 1.7f % 20)), 0.6f + k * 0.3f)!.Value)];
        var a = new HouseCells(g); var b = new HouseCells(g);
        foreach (var op in ops) Assert.Equal(a.Carve(op, Cfg), b.Carve(op, Cfg));
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.EncodeRemoved(), b.EncodeRemoved());
        // the same blast in map space gives the same op on every machine
        Assert.Equal(CarveOp.From(g, new Vector3(103.3f, -48.1f, 3.9f), 2f), CarveOp.From(g, new Vector3(103.3f, -48.1f, 3.9f), 2f));
    }

    [Fact]
    public void Apply_counts_ops_and_skips_settle_when_nothing_is_carved()
    {
        var c = new HouseCells(TestHouses.Box(20, 16, 24));
        var r = c.Apply(At(c.Grid, 10, 8, 5, 4), Cfg);   // the middle of the room: air only
        Assert.Empty(r.Carved);
        Assert.Equal(1, c.OpSeq);
        Assert.Equal(c.Hash(), r.Hash);
    }

    [Fact]
    public void Rpg_op_on_a_40x32x28_house_is_fast()
    {
        var g = TestHouses.Box(40, 32, 28);
        var op = CarveOp.From(g, g.ToMap(new Vector3(1.5f, 16, 6)), 2f)!.Value;   // on the x = 1 wall
        var c = new HouseCells(g);
        for (var k = 0; k < 20; k++) { c.Restore(); c.Carve(op, Cfg); c.Hash(); }   // warm up
        const int n = 200;
        var sw = Stopwatch.StartNew();
        for (var k = 0; k < n; k++) { c.Restore(); c.Carve(op, Cfg); c.Hash(); }
        var carveMs = sw.Elapsed.TotalMilliseconds / n;
        output.WriteLine($"carve + hash, r = 2 m, 40x32x28: {carveMs:F4} ms ({CountFirst(g, op)} cells carved)");
        Assert.True(carveMs < Budget.Ms(0.5), $"carve + hash took {carveMs:F3} ms (budget {Budget.Ms(0.5)} ms)");

        // with Settle, once WP-B has landed it (the stub throws until then)
        try
        {
            c.Restore(); c.Apply(op, Cfg);
            sw.Restart();
            for (var k = 0; k < n; k++) { c.Restore(); c.Apply(op, Cfg); }
            var applyMs = sw.Elapsed.TotalMilliseconds / n;
            output.WriteLine($"Apply (carve + Settle + hash): {applyMs:F4} ms");
            Assert.True(applyMs < Budget.Ms(1), $"Apply took {applyMs:F3} ms (budget {Budget.Ms(1)} ms)");
        }
        catch (NotImplementedException) { output.WriteLine("Support.Settle not implemented yet: Apply not timed"); }
    }

    static int CountFirst(HouseGrid g, CarveOp op) => new HouseCells(g).Carve(op, Cfg).Length;
}
