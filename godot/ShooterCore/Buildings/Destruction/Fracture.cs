using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// Voronoi split of a fallen component into pieces (docs/house-destruction.md Contract 3 "Fracture", WP-C). Integer maths, deterministic:
/// the same grid, cells, impact and seed give the same pieces on every machine.
/// </summary>
public static class Fracture
{
    /// <summary>
    /// Splits <paramref name="cells"/> (a component's cell indices, any order, no duplicates) into pieces. Seeds: cells / cells_per_piece (at
    /// least 1), drawn by a xorshift32 seeded with FNV-1a of (house index, <paramref name="seed"/>, the component's lowest cell); pass the op's
    /// OpSeq (or a seed derived from it) as <paramref name="seed"/>. Each seed starts on a random cell of the component and is pulled toward
    /// <paramref name="impactGrid"/> (grid units) by up to impact_bias of the way, so seeds pack closer near the impact. Every cell goes to its
    /// nearest seed (squared distance in quarter cells, ties by seed index); a piece that ends up disconnected (6-neighbour) is split into its
    /// components. Returns the pieces, each sorted by cell index, ordered by their lowest cell, then always one last group (possibly empty): the
    /// dust, every piece under dust_below_cells merged, sorted. Every input cell is in exactly one group.
    /// </summary>
    public static IReadOnlyList<int[]> Split(HouseGrid g, int[] cells, Vector3 impactGrid, uint seed, FractureConfig cfg)
    {
        var n = cells.Length;
        var result = new List<int[]>();
        if (n == 0) { result.Add([]); return result; }
        var sorted = (int[])cells.Clone();
        Array.Sort(sorted);

        // cell centres in quarter cells, and the component's bounds
        var cx = new int[n]; var cy = new int[n]; var cz = new int[n];
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
        for (var k = 0; k < n; k++)
        {
            var (x, y, z) = g.Coords(sorted[k]);
            cx[k] = x * 4 + 2; cy[k] = y * 4 + 2; cz[k] = z * 4 + 2;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
        }

        // seeds
        var rng = Mix(g.Index, seed, sorted[0]);
        var seeds = Math.Max(1, n / Math.Max(1, cfg.CellsPerPiece));
        long ix = Quarter(impactGrid.X), iy = Quarter(impactGrid.Y), iz = Quarter(impactGrid.Z);
        long biasQ = (long)MathF.Round(Math.Clamp(cfg.ImpactBias, 0f, 1f) * 256);
        var sx = new long[seeds]; var sy = new long[seeds]; var sz = new long[seeds];
        for (var s = 0; s < seeds; s++)
        {
            var k = (int)(Next(ref rng) % (uint)n);
            long pull = biasQ * (Next(ref rng) & 255); // 0 .. 65280 of 65536
            sx[s] = cx[k] + (ix - cx[k]) * pull / 65536;
            sy[s] = cy[k] + (iy - cy[k]) * pull / 65536;
            sz[s] = cz[k] + (iz - cz[k]) * pull / 65536;
        }

        // nearest seed, into a local label volume (0 = not in the component, else seed + 1)
        int bx = x1 - x0 + 1, by = y1 - y0 + 1, bz = z1 - z0 + 1;
        var lab = new int[bx * by * bz];
        var local = new int[n];
        // exact nearest seed, pruned along the component's longest axis: seeds sorted on it, scanned outward from the cell until the
        // axis distance alone is over the best (equal still scans: a tie goes to the lower seed index)
        var (ca, sa) = bx >= by && bx >= bz ? (cx, sx) : by >= bz ? (cy, sy) : (cz, sz);
        var order = new int[seeds];
        var key = new long[seeds];
        for (var s = 0; s < seeds; s++) { order[s] = s; key[s] = sa[s]; }
        Array.Sort(key, order); // keys equal: order among them does not matter, ties are settled by index below
        for (var k = 0; k < n; k++)
        {
            long best = long.MaxValue; var bi = int.MaxValue;
            long ck = ca[k];
            int hi = LowerBound(key, ck), lo = hi - 1;
            for (; hi < seeds; hi++)
            {
                var da = key[hi] - ck;
                if (da * da > best) break;
                Try(order[hi]);
            }
            for (; lo >= 0; lo--)
            {
                var da = ck - key[lo];
                if (da * da > best) break;
                Try(order[lo]);
            }

            void Try(int s)
            {
                long dx = cx[k] - sx[s], dy = cy[k] - sy[s], dz = cz[k] - sz[s];
                var d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best || (d2 == best && s < bi)) { best = d2; bi = s; }
            }
            int lx = (cx[k] - 2) / 4 - x0, ly = (cy[k] - 2) / 4 - y0, lz = (cz[k] - 2) / 4 - z0;
            local[k] = lx + bx * (ly + by * lz);
            lab[local[k]] = bi + 1;
        }

        // connected pieces per seed, discovered in cell order (so ordered by lowest cell); visited cells get their label negated
        var queue = new int[n];
        var dust = new List<int>();
        var sxy = bx * by;
        for (var k = 0; k < n; k++)
        {
            var start = local[k];
            var l = lab[start];
            if (l <= 0) continue;
            int head = 0, tail = 0;
            queue[tail++] = start; lab[start] = -l;
            while (head < tail)
            {
                var q = queue[head++];
                int qx = q % bx, qy = q / bx % by, qz = q / sxy;
                if (qx > 0) Visit(q - 1);
                if (qx + 1 < bx) Visit(q + 1);
                if (qy > 0) Visit(q - bx);
                if (qy + 1 < by) Visit(q + bx);
                if (qz > 0) Visit(q - sxy);
                if (qz + 1 < bz) Visit(q + sxy);
            }
            var piece = new int[tail];
            for (var j = 0; j < tail; j++)
            {
                var q = queue[j];
                piece[j] = g.IndexOf(x0 + q % bx, y0 + q / bx % by, z0 + q / sxy);
            }
            Array.Sort(piece);
            if (piece.Length < cfg.DustBelowCells) dust.AddRange(piece);
            else result.Add(piece);

            void Visit(int o)
            {
                if (lab[o] != l) return;
                lab[o] = -l;
                queue[tail++] = o;
            }
        }
        var d = dust.ToArray();
        Array.Sort(d);
        result.Add(d);
        return result;
    }

    /// <summary>The first index whose key is not below <paramref name="v"/>.</summary>
    static int LowerBound(long[] keys, long v)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (keys[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    static long Quarter(float v) => (long)MathF.Round(Math.Clamp(v, -1e6f, 1e6f) * 4);

    /// <summary>FNV-1a 32 over the house index, the seed and the lowest cell (little-endian bytes); never 0 (xorshift's dead state).</summary>
    static uint Mix(ushort house, uint seed, int lowest)
    {
        var h = 2166136261u;
        void Add(uint v, int bytes)
        {
            for (var i = 0; i < bytes; i++) { h ^= (v >> (8 * i)) & 0xFF; h *= 16777619u; }
        }
        Add(house, 2); Add(seed, 4); Add((uint)lowest, 4);
        return h == 0 ? 0x9E3779B9u : h;
    }

    static uint Next(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return s;
    }
}
