namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// Structure (D6, docs/house-destruction.md Contract 3 "Support"): detach, then load, repeat until stable. Integer maths only, the same on every
/// machine. Scratch buffers are per thread and reused; only the returned arrays are allocated.
/// </summary>
public static class Support
{
    const int Unreached = int.MaxValue;
    const int Taken = -1;                    // dist of a cell gathered into a fallen component

    [ThreadStatic] static byte[]? _live;     // palette index of each live cell, 0 = air or removed
    [ThreadStatic] static int[]? _dist;      // steps (6-neighbour) to the foundation, Unreached = not connected
    [ThreadStatic] static int[]? _queue;     // BFS order, nondecreasing distance
    [ThreadStatic] static long[]? _load;
    [ThreadStatic] static int[]? _side;      // span rule: sideways steps from the nearest vertical support
    [ThreadStatic] static int[]? _next;
    [ThreadStatic] static List<int>? _hits;

    /// <summary>
    /// Removes the cells that crumble under load and the components with no path to the foundation; returns them. Each pass: detach (flood fill
    /// from the foundation; every other solid component is Fallen), load (from the largest distance to the smallest, each cell hands its weight and
    /// what it received to its neighbours one step closer, split evenly, the remainder to the lowest index), crumble (load over strength, or the
    /// span rule). Repeats until a pass crumbles nothing, at most support.max_passes times; a pass limit hit still ends with a detach, so nothing floats.
    /// Crumbled is sorted by cell index; Fallen components are in pass order, then by lowest cell, each sorted.
    /// </summary>
    public static (int[] Crumbled, IReadOnlyList<int[]> Fallen) Settle(HouseCells c, DestructionConfig cfg)
    {
        var g = c.Grid;
        var n = g.Count;
        var live = Grow(ref _live, n);
        var dist = Grow(ref _dist, n);
        var queue = Grow(ref _queue, n);
        var load = Grow(ref _load, n);
        var hits = _hits ??= new List<int>();

        // per palette entry: weight, strength, span; foundation is the root and never crumbles
        var pal = g.Palette.Span;
        Span<int> weight = stackalloc int[pal.Length], strength = stackalloc int[pal.Length], span = stackalloc int[pal.Length];
        Span<bool> root = stackalloc bool[pal.Length];
        var maxSpan = 0;
        for (var p = 1; p < pal.Length; p++)
        {
            var m = pal[p].Material;
            if (m == CellMaterial.Foundation) { root[p] = true; continue; }
            if (m == CellMaterial.Air) continue;
            weight[p] = cfg.Weight(m);
            strength[p] = cfg.Strength(m);
            span[p] = cfg.Span(m);
            maxSpan = Math.Max(maxSpan, span[p]);
        }

        var cells = g.Cells.Span;
        var removed = c.RemovedBits;
        var solid = 0;
        for (var i = 0; i < n; i++)
        {
            var p = (removed[i >> 6] & (1UL << (i & 63))) != 0 ? (byte)0 : cells[i];
            live[i] = p;
            if (p != 0) solid++;
        }

        var dims = new Dims(g.Nx, g.Ny, g.Nz);
        List<int[]>? fallen = null;
        List<int>? crumbled = null;
        var pending = true;     // cells went since the last detach
        for (var pass = 0; pass < cfg.Support.MaxPasses && pending; pass++)
        {
            var reached = Detach(c, dims, live, root, dist, queue, ref solid, ref fallen);
            Load(dims, live, weight, dist, queue, reached, load);
            hits.Clear();
            for (var q = 0; q < reached; q++)
            {
                var i = queue[q];
                var s = strength[live[i]];
                if (s > 0 && load[i] > s) hits.Add(i);
            }
            if (maxSpan > 0) SpanRule(dims, live, span, maxSpan, dist, queue, reached, hits);
            hits.Sort();
            var w = 0;
            for (var k = 0; k < hits.Count; k++)
            {
                var i = hits[k];
                if (k > 0 && i == hits[k - 1]) continue;     // hit by both rules
                c.Remove(i);
                live[i] = 0;
                solid--;
                (crumbled ??= new List<int>()).Add(i);
                w++;
            }
            pending = w > 0;
        }
        if (pending) Detach(c, dims, live, root, dist, queue, ref solid, ref fallen);

        var cr = crumbled is null ? Array.Empty<int>() : crumbled.ToArray();
        Array.Sort(cr);
        return (cr, (IReadOnlyList<int[]>?)fallen ?? Array.Empty<int[]>());
    }

    readonly struct Dims(int nx, int ny, int nz)
    {
        public readonly int Nx = nx, Ny = ny, Nz = nz, Nxy = nx * ny;

        /// <summary>Bit d set: neighbour d of cell i is in the grid. Neighbour order is ascending index: -z, -y, -x, +x, +y, +z.</summary>
        public int Mask(int i, out int dz)
        {
            int x = i % Nx, y = i / Nx % Ny, z = i / Nxy;
            dz = z;
            var m = 0;
            if (z > 0) m |= 1;
            if (y > 0) m |= 2;
            if (x > 0) m |= 4;
            if (x < Nx - 1) m |= 8;
            if (y < Ny - 1) m |= 16;
            if (z < Nz - 1) m |= 32;
            return m;
        }

        public int Offset(int d) => d switch { 0 => -Nxy, 1 => -Nx, 2 => -1, 3 => 1, 4 => Nx, _ => Nxy };
    }

    /// <summary>
    /// BFS from every live foundation cell: fills dist and queue (queue[0..reached) in nondecreasing distance). Every live cell it misses is
    /// removed, as 6-neighbour components in order of their lowest cell. Returns the count reached.
    /// </summary>
    static int Detach(HouseCells c, Dims dm, byte[] live, ReadOnlySpan<bool> root, int[] dist, int[] queue, ref int solid, ref List<int[]>? fallen)
    {
        var n = dm.Nxy * dm.Nz;
        Span<int> off = stackalloc int[6];
        for (var d = 0; d < 6; d++) off[d] = dm.Offset(d);
        var tail = 0;
        for (var i = 0; i < n; i++)
        {
            var p = live[i];
            if (p != 0 && root[p]) { dist[i] = 0; queue[tail++] = i; }
            else dist[i] = Unreached;
        }
        var head = 0;
        while (head < tail)
        {
            var i = queue[head++];
            var nd = dist[i] + 1;
            var mask = dm.Mask(i, out _);
            for (var d = 0; d < 6; d++)
            {
                if ((mask & (1 << d)) == 0) continue;
                var k = i + off[d];
                if (dist[k] == Unreached && live[k] != 0) { dist[k] = nd; queue[tail++] = k; }
            }
        }
        var reached = tail;
        if (reached == solid) return reached;

        // the rest falls: one component per flood fill, started at its lowest cell
        for (var i = 0; i < n && solid > reached; i++)
        {
            if (dist[i] != Unreached || live[i] == 0) continue;
            dist[i] = Taken;
            int h = reached, t = reached;
            queue[t++] = i;
            while (h < t)
            {
                var j = queue[h++];
                var mask = dm.Mask(j, out _);
                for (var d = 0; d < 6; d++)
                {
                    if ((mask & (1 << d)) == 0) continue;
                    var k = j + off[d];
                    if (dist[k] == Unreached && live[k] != 0) { dist[k] = Taken; queue[t++] = k; }
                }
            }
            var comp = queue.AsSpan(reached, t - reached).ToArray();
            Array.Sort(comp);
            foreach (var k in comp) { c.Remove(k); live[k] = 0; }
            solid -= comp.Length;
            (fallen ??= new List<int[]>()).Add(comp);
        }
        return reached;
    }

    /// <summary>
    /// The load pass, from the largest distance to the smallest. Cells of one distance never hand load to each other, so their order (the spec's
    /// tie-break by index) cannot change the result; the BFS order is walked backwards as is.
    /// </summary>
    static void Load(Dims dm, byte[] live, ReadOnlySpan<int> weight, int[] dist, int[] queue, int reached, long[] load)
    {
        Span<int> off = stackalloc int[6];
        for (var d = 0; d < 6; d++) off[d] = dm.Offset(d);
        Span<int> to = stackalloc int[6];
        for (var q = 0; q < reached; q++) load[queue[q]] = 0;
        for (var q = reached - 1; q >= 0; q--)
        {
            var i = queue[q];
            var di = dist[i];
            if (di == 0) continue;                    // foundation: the ground takes it
            var total = load[i] + weight[live[i]];
            var mask = dm.Mask(i, out _);
            var k = 0;
            for (var d = 0; d < 6; d++)               // ascending index, so to[0] is the lowest
                if ((mask & (1 << d)) != 0 && dist[i + off[d]] == di - 1) to[k++] = i + off[d];
            var share = total / k;                    // k >= 1: the BFS parent is one step closer
            for (var j = 0; j < k; j++) load[to[j]] += share;
            load[to[0]] += total - share * k;
        }
    }

    /// <summary>
    /// The span rule. A cell is a vertical support when a live column runs straight down from it to the foundation. Sideways steps from the
    /// nearest support cost 1, up and down steps 0; a cell whose material has a span under that count crumbles (added to hits).
    /// </summary>
    static void SpanRule(Dims dm, byte[] live, ReadOnlySpan<int> span, int maxSpan, int[] dist, int[] queue, int reached, List<int> hits)
    {
        var n = dm.Nxy * dm.Nz;
        var side = Grow(ref _side, n);
        var next = Grow(ref _next, n);
        Span<int> off = stackalloc int[6];
        for (var d = 0; d < 6; d++) off[d] = dm.Offset(d);
        for (var q = 0; q < reached; q++) side[queue[q]] = Unreached;
        // roots in index order (z ascending), so the cell below is decided first; then queue is free for the level queue
        var cur = queue;
        var curLen = 0;
        for (var i = 0; i < n; i++)
        {
            if (dist[i] == Unreached || dist[i] < 0) continue;
            if (dist[i] == 0 || (i >= dm.Nxy && dist[i - dm.Nxy] >= 0 && dist[i - dm.Nxy] != Unreached && side[i - dm.Nxy] == 0))
                side[i] = 0;
        }
        for (var i = 0; i < n; i++) if (dist[i] >= 0 && dist[i] != Unreached && side[i] == 0) cur[curLen++] = i;
        for (var level = 0; curLen > 0 && level <= maxSpan; level++)
        {
            var nextLen = 0;
            for (var h = 0; h < curLen; h++)
            {
                var i = cur[h];
                if (side[i] != level) continue;
                var mask = dm.Mask(i, out _);
                for (var d = 0; d < 6; d++)
                {
                    if ((mask & (1 << d)) == 0) continue;
                    var k = i + off[d];
                    if (dist[k] < 0 || dist[k] == Unreached) continue;
                    var sideways = d is >= 1 and <= 4;
                    var nd = sideways ? level + 1 : level;
                    if (nd >= side[k]) continue;
                    side[k] = nd;
                    if (sideways) next[nextLen++] = k; else cur[curLen++] = k;
                }
            }
            (cur, next) = (next, cur);
            curLen = nextLen;
        }
        for (var i = 0; i < n; i++)
        {
            if (dist[i] <= 0 || dist[i] == Unreached) continue;
            var s = span[live[i]];
            if (s > 0 && side[i] > s) hits.Add(i);
        }
    }

    static T[] Grow<T>(ref T[]? buf, int n) => buf is { } b && b.Length >= n ? b : buf = new T[n];
}
