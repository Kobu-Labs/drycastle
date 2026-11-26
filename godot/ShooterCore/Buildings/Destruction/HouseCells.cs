namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// Live state of one house: the grid and which of its solid cells are removed this round (a bitmask, one bit per cell index).
/// Every decision is integer, so the server and every client reach the same state from the same ops (D5).
/// </summary>
public sealed class HouseCells
{
    readonly ulong[] _removed;
    int _removedCount;

    public HouseCells(HouseGrid grid)
    {
        Grid = grid;
        _removed = new ulong[(grid.Count + 63) >> 6];
    }

    public HouseGrid Grid { get; }
    /// <summary>Ops applied this round.</summary>
    public ushort OpSeq { get; private set; }
    /// <summary>Original solid and not removed.</summary>
    public bool Solid(int i) => Grid.Cells.Span[i] != 0 && (_removed[i >> 6] & (1UL << (i & 63))) == 0;
    /// <summary>Whether any cell is removed.</summary>
    public bool Damaged => _removedCount > 0;

    /// <summary>The box of the removed cells (empty when none).</summary>
    public CellBox RemovedBox()
    {
        if (_removedCount == 0) return default;
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1;
        int nx = Grid.Nx, sxy = Grid.Nx * Grid.Ny;
        for (var w = 0; w < _removed.Length; w++)
        {
            var b = _removed[w];
            while (b != 0)
            {
                var i = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(b);
                b &= b - 1;
                int z = i / sxy, r = i - z * sxy, y = r / nx, x = r - y * nx;
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
                if (z < z0) z0 = z; if (z > z1) z1 = z;
            }
        }
        return new CellBox(x0, y0, z0, x1 + 1, y1 + 1, z1 + 1);
    }

    /// <summary>
    /// Carve (<see cref="Carve"/>), then <see cref="Support.Settle"/> (skipped when the op carved nothing: the state is unchanged), OpSeq++.
    /// The hash is taken after Settle.
    /// </summary>
    public CarveResult Apply(CarveOp op, DestructionConfig cfg)
    {
        var carved = Carve(op, cfg);
        int[] crumbled = [];
        IReadOnlyList<int[]> fallen = [];
        if (carved.Length > 0) (crumbled, fallen) = Support.Settle(this, cfg);
        OpSeq++;
        return new CarveResult(carved, crumbled, fallen, Hash());
    }

    /// <summary>
    /// The carve rule alone (no support pass, no OpSeq change): removes every cell that is solid, not foundation, of a material with resist r &gt; 0
    /// (1/16 units) and whose centre p (quarter-cells) has <c>d2(p, c) * r^2 &lt;= RadiusQ^2 * 256</c>. Returns the removed cells in ascending index order.
    /// </summary>
    internal int[] Carve(CarveOp op, DestructionConfig cfg)
    {
        var g = Grid;
        var pal = g.Palette.Span;
        Span<long> r2 = stackalloc long[64];
        var minResist = int.MaxValue;
        for (var k = 1; k < pal.Length; k++)
        {
            var m = pal[k].Material;
            var r = m == CellMaterial.Foundation ? 0 : cfg.Resist(m);
            r2[k] = (long)r * r;
            if (r > 0 && r < minResist) minResist = r;
        }
        if (minResist == int.MaxValue || op.RadiusQ == 0) return [];
        long rad = op.RadiusQ, lim = rad * rad * 256;
        // the farthest any material is carved, quarter-cells: R * 16 / min resist, rounded up
        var reach = (rad * 16 + minResist - 1) / minResist;
        int x0 = Lo(op.Cx, reach), x1 = Hi(op.Cx, reach, g.Nx);
        int y0 = Lo(op.Cy, reach), y1 = Hi(op.Cy, reach, g.Ny);
        int z0 = Lo(op.Cz, reach), z1 = Hi(op.Cz, reach, g.Nz);
        if (x0 > x1 || y0 > y1 || z0 > z1) return [];
        var cells = g.Cells.Span;
        var carved = new List<int>();
        for (var z = z0; z <= z1; z++)
        {
            long dz = 4L * z + 2 - op.Cz;
            for (var y = y0; y <= y1; y++)
            {
                long dy = 4L * y + 2 - op.Cy, dyz = dy * dy + dz * dz;
                var row = g.IndexOf(0, y, z);
                for (var x = x0; x <= x1; x++)
                {
                    var i = row + x;
                    var p = cells[i];
                    if (p == 0 || r2[p] == 0 || Removed(i)) continue;
                    long dx = 4L * x + 2 - op.Cx;
                    if ((dx * dx + dyz) * r2[p] > lim) continue;
                    _removed[i >> 6] |= 1UL << (i & 63);
                    _removedCount++;
                    carved.Add(i);
                }
            }
        }
        return carved.ToArray();
    }

    // cells whose centre 4x + 2 lies within [c - reach, c + reach], clamped to the grid
    static int Lo(long c, long reach) => (int)Math.Max(0, FloorDiv(c - reach - 2 + 3, 4));
    static int Hi(long c, long reach, int n) => (int)Math.Min(n - 1, FloorDiv(c + reach - 2, 4));
    static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>FNV-1a 32 over the removed bitmask as bytes: ceil(cells / 8) bytes, bit i in byte i / 8 at bit i % 8.</summary>
    public uint Hash()
    {
        var h = 2166136261u;
        var bytes = (Grid.Count + 7) >> 3;
        for (var b = 0; b < bytes; b++)
        {
            h ^= (byte)(_removed[b >> 3] >> ((b & 7) << 3));
            h *= 16777619u;
        }
        return h;
    }

    /// <summary>Every cell whole again, OpSeq 0 (round reset).</summary>
    public void Restore()
    {
        Array.Clear(_removed);
        _removedCount = 0;
        OpSeq = 0;
    }

    /// <summary>
    /// Varint runs (kept, removed, kept, ...) over the original solid cells in index order. The first run is kept cells (0 when the first solid cell is
    /// removed); the trailing kept run is left out, so an undamaged house encodes to no bytes.
    /// </summary>
    public byte[] EncodeRemoved()
    {
        var o = new List<byte>();
        var cells = Grid.Cells.Span;
        var cur = false;
        uint len = 0;
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] == 0) continue;
            var r = Removed(i);
            if (r == cur) { len++; continue; }
            Varint.Write(o, len);
            cur = r; len = 1;
        }
        if (cur) Varint.Write(o, len);
        return o.ToArray();
    }

    /// <summary>
    /// Sets the state to <paramref name="runs"/> (<see cref="EncodeRemoved"/>'s format; a cut-short list leaves the rest kept) and OpSeq to
    /// <paramref name="opSeq"/>. Runs past the house's solid cells or bad varints throw <see cref="InvalidDataException"/> and leave the state as it was.
    /// </summary>
    public void DecodeRemoved(ReadOnlySpan<byte> runs, ushort opSeq)
    {
        var cells = Grid.Cells.Span;
        var solids = 0;
        foreach (var c in cells) if (c != 0) solids++;
        var list = new List<uint>();
        long total = 0;
        for (var p = 0; p < runs.Length;)
        {
            var v = Varint.Read(runs, ref p);
            total += v;
            if (total > solids) throw new InvalidDataException($"house {Grid.Id}: removed runs cover {total} cells, it has {solids} solid");
            list.Add(v);
        }
        Array.Clear(_removed);
        _removedCount = 0;
        var i = 0;
        for (var k = 0; k < list.Count; k++)
            for (var n = list[k]; n > 0; i++)
            {
                if (cells[i] == 0) continue;
                if ((k & 1) == 1) { _removed[i >> 6] |= 1UL << (i & 63); _removedCount++; }
                n--;
            }
        OpSeq = opSeq;
    }

    /// <summary>Marks solid cell <paramref name="i"/> removed (no support pass, no OpSeq change). Air or already removed: nothing.</summary>
    internal void Remove(int i)
    {
        if (!Solid(i)) return;
        _removed[i >> 6] |= 1UL << (i & 63);
        _removedCount++;
    }

    /// <summary>Whether cell <paramref name="i"/> was solid and is removed.</summary>
    internal bool Removed(int i) => (_removed[i >> 6] & (1UL << (i & 63))) != 0;

    /// <summary>The removed bitmask, one bit per cell index (low bit first). Read-only view for the hash and the wire.</summary>
    internal ReadOnlySpan<ulong> RemovedBits => _removed;

    /// <summary>Sets OpSeq (the wire's decode and Apply).</summary>
    internal void SetOpSeq(ushort seq) => OpSeq = seq;
}
