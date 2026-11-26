using System.Buffers;
using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// Faces and box covers of a house's cells (docs/house-destruction.md Contract 3, WP-C). Everything is in grid units (one cell = 1).
/// Faces are merged greedily per slice by palette index and normal. Each <see cref="Quad"/> spans Corner .. Corner + U + V with
/// U x V pointing along its outward Normal. Scratch volumes come from <see cref="ArrayPool{T}.Shared"/>, so a warm caller allocates
/// nothing beyond its own lists.
/// </summary>
public static class HouseMesher
{
    // Scratch volume codes: 0 = not solid and never was (original air, or not in the piece), 1..63 = solid (palette index),
    // Cut | palette = was solid, now removed (not solid).
    const byte Cut = 0x80;
    /// <summary>Solid for the neighbour test, but its own faces are not meshed (a palette index is under 64).</summary>
    const byte NoMesh = 0x40;

    /// <summary>Every face of a live cell whose neighbour is not solid (air, removed, outside the grid): the collider's faces. Greedy, no allocation beyond <paramref name="into"/>.</summary>
    public static void Surface(HouseCells c, List<Quad> into) => Surface(c, CellBox.All(c.Grid), into);

    /// <summary>
    /// <see cref="Surface(HouseCells, List{Quad})"/> of the live cells in <paramref name="region"/> only (a collider chunk). Faces between two live cells are never made,
    /// so the faces of regions that tile the grid add up to the whole house's.
    /// </summary>
    public static void Surface(HouseCells c, CellBox region, List<Quad> into)
    {
        var g = c.Grid;
        if (region.Empty) return;
        var vol = ArrayPool<byte>.Shared.Rent(g.Count);
        try
        {
            var f = region.Grow(1, g);
            Fill(c, vol, f.X0, f.Y0, f.Z0, f.X1, f.Y1, f.Z1);
            Greedy(vol, g.Nx, g.Ny, g.Nz, region.X0, region.Y0, region.Z0, region.X1, region.Y1, region.Z1, Vector3.Zero, into);
        }
        finally { ArrayPool<byte>.Shared.Return(vol); }
    }

    /// <summary>
    /// The voxel faces of a damaged house (<see cref="HouseDamageLook"/>): every open face (toward air, a removed cell or the grid's edge) of the live
    /// cells near a hole (<see cref="HouseDamageLook.Near"/>, within <see cref="HouseDamageLook.NearBox"/>), and the faces of the other live cells toward the
    /// attic's air (<see cref="HouseDamageLook.Enclosed"/>, within <see cref="HouseDamageLook.EnclosedBox"/>). Greedy, grid units. Only those boxes are read.
    /// </summary>
    public static void Damaged(HouseCells c, HouseDamageLook look, List<Quad> into)
    {
        var g = c.Grid;
        if (!c.Damaged) return;
        var vol = ArrayPool<byte>.Shared.Rent(g.Count);
        try
        {
            var pal = g.Cells.Span;
            var bits = c.RemovedBits;
            var near = look.Near;
            var r = look.NearBox;
            if (!r.Empty)
            {
                var f = r.Grow(1, g);
                for (var z = f.Z0; z < f.Z1; z++)
                for (var y = f.Y0; y < f.Y1; y++)
                for (int i = g.IndexOf(f.X0, y, z), end = i + f.X1 - f.X0; i < end; i++)
                {
                    var p = pal[i];
                    var live = p != 0 && (bits[i >> 6] & (1UL << (i & 63))) == 0;
                    vol[i] = live ? (near[i] != 0 ? p : NoMesh) : (byte)0;
                }
                Greedy(vol, g.Nx, g.Ny, g.Nz, r.X0, r.Y0, r.Z0, r.X1, r.Y1, r.Z1, Vector3.Zero, into);
            }
            if (!look.AnyEnclosed) return;
            var enclosed = look.Enclosed;
            var e = look.EnclosedBox.Grow(1, g); // the live cells next to the attic's air
            var fe = e.Grow(1, g);
            for (var z = fe.Z0; z < fe.Z1; z++)
            for (var y = fe.Y0; y < fe.Y1; y++)
            for (int i = g.IndexOf(fe.X0, y, z), end = i + fe.X1 - fe.X0; i < end; i++)
            {
                var p = pal[i];
                var live = p != 0 && (bits[i >> 6] & (1UL << (i & 63))) == 0;
                vol[i] = live ? (near[i] != 0 ? NoMesh : p) : p == 0 && enclosed[i] != 0 ? (byte)0 : NoMesh;
            }
            Greedy(vol, g.Nx, g.Ny, g.Nz, e.X0, e.Y0, e.Z0, e.X1, e.Y1, e.Z1, Vector3.Zero, into, edgeFaces: false);
        }
        finally { ArrayPool<byte>.Shared.Return(vol); }
    }

    /// <summary>A debris piece: every face of <paramref name="cells"/> not shared with another cell of the piece, greedy, in grid units.</summary>
    public static void Surface(HouseGrid g, ReadOnlySpan<int> cells, List<Quad> into)
    {
        if (cells.IsEmpty) return;
        var (x0, y0, z0, sx, sy, sz) = Bounds(g, cells);
        var n = sx * sy * sz;
        var vol = ArrayPool<byte>.Shared.Rent(n);
        try
        {
            Array.Clear(vol, 0, n);
            var pal = g.Cells.Span;
            foreach (var i in cells)
            {
                var (x, y, z) = g.Coords(i);
                vol[x - x0 + sx * (y - y0 + sy * (z - z0))] = pal[i];
            }
            Greedy(vol, sx, sy, sz, 0, 0, 0, sx, sy, sz, new Vector3(x0, y0, z0), into);
        }
        finally { ArrayPool<byte>.Shared.Return(vol); }
    }

    /// <summary>
    /// Greedy box cover of the live cells, for colliders (grid units, Min inclusive corner, Max exclusive corner). The boxes do not overlap
    /// and together cover exactly the solid cells. Each box grows along x, then y, then z from the lowest uncovered cell.
    /// </summary>
    public static void Boxes(HouseCells c, List<(Vector3 Min, Vector3 Max)> into)
    {
        var g = c.Grid;
        int nx = g.Nx, ny = g.Ny, nz = g.Nz, n = g.Count, sxy = nx * ny;
        var vol = ArrayPool<byte>.Shared.Rent(n);
        try
        {
            var pal = g.Cells.Span;
            var bits = c.RemovedBits;
            for (var i = 0; i < n; i++) vol[i] = pal[i] != 0 && (bits[i >> 6] & (1UL << (i & 63))) == 0 ? (byte)1 : (byte)0;
            for (var z = 0; z < nz; z++)
                for (var y = 0; y < ny; y++)
                {
                    var row = nx * (y + ny * z);
                    for (var x = 0; x < nx; x++)
                    {
                        if (vol[row + x] == 0) continue;
                        var start = row + x;
                        var w = 1;
                        while (x + w < nx && vol[start + w] != 0) w++;
                        var h = 1;
                        while (y + h < ny && Full(vol, start + h * nx, w)) h++;
                        var d = 1;
                        while (z + d < nz && Plate(vol, start + d * sxy, w, h, nx)) d++;
                        for (var k = 0; k < d; k++)
                            for (var j = 0; j < h; j++)
                                Array.Clear(vol, start + k * sxy + j * nx, w);
                        into.Add((new Vector3(x, y, z), new Vector3(x + w, y + h, z + d)));
                        x += w - 1;
                    }
                }
        }
        finally { ArrayPool<byte>.Shared.Return(vol); }
    }

    static bool Full(byte[] vol, int at, int w)
    {
        for (var i = 0; i < w; i++) if (vol[at + i] == 0) return false;
        return true;
    }

    static bool Plate(byte[] vol, int at, int w, int h, int nx)
    {
        for (var j = 0; j < h; j++) if (!Full(vol, at + j * nx, w)) return false;
        return true;
    }

    /// <summary>Over the cells [x0, x1) x [y0, y1) x [z0, z1): vol[i] = palette index of a solid cell, Cut | palette of a removed one, 0 of original air.</summary>
    static void Fill(HouseCells c, byte[] vol, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var g = c.Grid;
        var pal = g.Cells.Span;
        var bits = c.RemovedBits;
        for (var z = z0; z < z1; z++)
            for (var y = y0; y < y1; y++)
            {
                var row = g.Nx * (y + g.Ny * z);
                for (int i = row + x0, end = row + x1; i < end; i++)
                {
                    var p = pal[i];
                    vol[i] = p != 0 && (bits[i >> 6] & (1UL << (i & 63))) != 0 ? (byte)(p | Cut) : p;
                }
            }
    }

    static (int X0, int Y0, int Z0, int Sx, int Sy, int Sz) Bounds(HouseGrid g, ReadOnlySpan<int> cells)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
        foreach (var i in cells)
        {
            var (x, y, z) = g.Coords(i);
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
            if (z < z0) z0 = z; if (z > z1) z1 = z;
        }
        return (x0, y0, z0, x1 - x0 + 1, y1 - y0 + 1, z1 - z0 + 1);
    }

    static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    /// <summary>
    /// Greedy faces of a scratch volume (codes as in <see cref="Fill"/>): a face wherever the neighbour is not solid (outside the volume counts as not
    /// solid; with edgeFaces off, no faces there). Only the cells of the region [lo, hi) are meshed; the cells next to it that are read must be filled too.
    /// </summary>
    static void Greedy(byte[] vol, int nx, int ny, int nz, int lx, int ly, int lz, int hx, int hy, int hz, Vector3 offset, List<Quad> into, bool edgeFaces = true)
    {
        Span<int> size = [nx, ny, nz];
        Span<int> stride = [1, nx, nx * ny];
        Span<int> lo = [lx, ly, lz];
        Span<int> ext = [hx - lx, hy - ly, hz - lz];
        var maskLen = Math.Max(ext[0] * ext[1], Math.Max(ext[1] * ext[2], ext[2] * ext[0]));
        var mask = ArrayPool<byte>.Shared.Rent(maskLen);
        try
        {
            for (var a = 0; a < 3; a++)
            {
                int u = (a + 1) % 3, v = (a + 2) % 3;
                int na = size[a], nu = ext[u], nv = ext[v], sa = stride[a], su = stride[u], sv = stride[v];
                var origin = lo[u] * su + lo[v] * sv;
                for (int k = lo[a], kEnd = lo[a] + ext[a]; k < kEnd; k++)
                    for (var sign = 1; sign >= -1; sign -= 2)
                    {
                        var hasNb = sign > 0 ? k + 1 < na : k > 0;
                        var nbOff = sign * sa;
                        var any = false;
                        for (var iv = 0; iv < nv; iv++)
                        {
                            var at = origin + k * sa + iv * sv;
                            var m = iv * nu;
                            for (var iu = 0; iu < nu; iu++, at += su)
                            {
                                var p = vol[at];
                                byte f = 0;
                                if (p is > 0 and < NoMesh)
                                {
                                    if (!hasNb) { if (edgeFaces) f = p; }
                                    else { var q = vol[at + nbOff]; if (q == 0 || q >= Cut) f = p; }
                                }
                                mask[m + iu] = f;
                                any |= f != 0;
                            }
                        }
                        if (any) Emit(mask, nu, nv, a, u, v, sign > 0 ? k + 1 : k, sign, offset + Axes[u] * lo[u] + Axes[v] * lo[v], into);
                    }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(mask); }
    }

    /// <summary>Greedy rectangles of one slice's mask (rows along u, then grown along v), cleared as they are taken.</summary>
    static void Emit(byte[] mask, int nu, int nv, int a, int u, int v, int plane, int sign, Vector3 offset, List<Quad> into)
    {
        var eu = Axes[u]; var ev = Axes[v]; var normal = Axes[a] * sign;
        for (var iv = 0; iv < nv; iv++)
            for (var iu = 0; iu < nu; )
            {
                var p = mask[iu + iv * nu];
                if (p == 0) { iu++; continue; }
                var w = 1;
                while (iu + w < nu && mask[iu + w + iv * nu] == p) w++;
                var h = 1;
                while (iv + h < nv && Same(mask, iu + (iv + h) * nu, w, p)) h++;
                for (var j = 0; j < h; j++) Array.Clear(mask, iu + (iv + j) * nu, w);
                var corner = offset + Axes[a] * plane + eu * iu + ev * iv;
                Vector3 du = eu * w, dv = ev * h;
                into.Add(sign > 0 ? new Quad(corner, du, dv, normal, p) : new Quad(corner, dv, du, normal, p));
                iu += w;
            }
    }

    static bool Same(byte[] mask, int at, int w, byte p)
    {
        for (var i = 0; i < w; i++) if (mask[at + i] != p) return false;
        return true;
    }
}
