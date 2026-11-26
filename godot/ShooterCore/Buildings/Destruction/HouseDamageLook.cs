namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// What a damaged house shows (client): near a hole the smooth mesh is hidden and the live cells are drawn as voxel blocks, so no surface of the smooth
/// mesh floats over air the grid never had (a roof's skin steps, eaves, trims) and a broken wall shows its thickness. <see cref="Near"/>: cells within
/// one cell (26 neighbours) of a removed cell; their live cells are meshed with every open face (<see cref="HouseMesher.Damaged"/>). <see cref="Hide"/>
/// (255 = the discard shader hides the smooth mesh there): <see cref="Near"/>, and original air within <see cref="OrphanReach"/> cells of a removed cell
/// with no live cell next to it (what was attached to blown-away cells). <see cref="Enclosed"/>: original air no face path reaches from the grid's edge
/// (the attic): the smooth mesh has no faces in it, so the live cells' faces toward it are drawn too once the house is damaged.
/// </summary>
public sealed class HouseDamageLook
{
    /// <summary>How far (cells, Chebyshev) original air with no live neighbour is hidden around a removed cell.</summary>
    public const int OrphanReach = 3;

    readonly byte[] _removed, _far, _live, _liveNear, _scratch;

    public HouseDamageLook(HouseGrid g)
    {
        Grid = g;
        var n = g.Count;
        Near = new byte[n]; Hide = new byte[n];
        _removed = new byte[n]; _far = new byte[n]; _live = new byte[n]; _liveNear = new byte[n]; _scratch = new byte[n];
        Enclosed = FindEnclosed(g, out var any);
        AnyEnclosed = any;
    }

    public HouseGrid Grid { get; }
    public byte[] Near { get; }
    public byte[] Hide { get; }
    public byte[] Enclosed { get; }
    public bool AnyEnclosed { get; }

    /// <summary>Recomputes <see cref="Near"/> and <see cref="Hide"/> from the cells' state.</summary>
    public void Update(HouseCells c)
    {
        var g = Grid;
        var pal = g.Cells.Span;
        var bits = c.RemovedBits;
        for (var i = 0; i < pal.Length; i++)
        {
            var gone = pal[i] != 0 && (bits[i >> 6] & (1UL << (i & 63))) != 0;
            _removed[i] = gone ? (byte)1 : (byte)0;
            _live[i] = pal[i] != 0 && !gone ? (byte)1 : (byte)0;
        }
        Dilate(_removed, Near, 1);
        Dilate(_removed, _far, OrphanReach);
        Dilate(_live, _liveNear, 1);
        for (var i = 0; i < pal.Length; i++)
            Hide[i] = Near[i] != 0 || (pal[i] == 0 && _far[i] != 0 && _liveNear[i] == 0) ? (byte)255 : (byte)0;
    }

    /// <summary>dst = src dilated by a (2r+1)^3 box (separable max along x, then y, then z).</summary>
    void Dilate(byte[] src, byte[] dst, int r)
    {
        var g = Grid;
        int nx = g.Nx, ny = g.Ny, nz = g.Nz, sxy = nx * ny;
        for (var z = 0; z < nz; z++)
            for (var y = 0; y < ny; y++) Line(src, dst, nx * (y + ny * z), 1, nx, r);
        for (var z = 0; z < nz; z++)
            for (var x = 0; x < nx; x++) Line(dst, _scratch, x + sxy * z, nx, ny, r);
        for (var y = 0; y < ny; y++)
            for (var x = 0; x < nx; x++) Line(_scratch, dst, x + nx * y, sxy, nz, r);
    }

    /// <summary>Max filter of radius r along one line of len cells, from start at stride step.</summary>
    static void Line(byte[] src, byte[] dst, int start, int step, int len, int r)
    {
        for (var k = 0; k < len; k++)
        {
            byte m = 0;
            for (int j = Math.Max(k - r, 0), b = Math.Min(k + r, len - 1); j <= b && m == 0; j++) m = src[start + j * step];
            dst[start + k * step] = m;
        }
    }

    /// <summary>Original air not 6-connected through air to a cell on the grid's edge.</summary>
    public static byte[] FindEnclosed(HouseGrid g, out bool any)
    {
        int nx = g.Nx, ny = g.Ny, nz = g.Nz, n = g.Count;
        var pal = g.Cells.ToArray();
        var seen = new bool[n];
        var stack = new Stack<int>();
        void Seed(int i) { if (pal[i] == 0 && !seen[i]) { seen[i] = true; stack.Push(i); } }
        for (var z = 0; z < nz; z++)
            for (var y = 0; y < ny; y++)
                for (var x = 0; x < nx; x++)
                    if (x == 0 || y == 0 || z == 0 || x == nx - 1 || y == ny - 1 || z == nz - 1) Seed(x + nx * (y + ny * z));
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            var (x, y, z) = g.Coords(i);
            if (x > 0) Seed(i - 1);
            if (x < nx - 1) Seed(i + 1);
            if (y > 0) Seed(i - nx);
            if (y < ny - 1) Seed(i + nx);
            if (z > 0) Seed(i - nx * ny);
            if (z < nz - 1) Seed(i + nx * ny);
        }
        var e = new byte[n];
        any = false;
        for (var i = 0; i < n; i++)
            if (pal[i] == 0 && !seen[i]) { e[i] = 1; any = true; }
        return e;
    }
}
