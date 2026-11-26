using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// The hidden voxel grid of one hollow house, as read from house_voxels.bin (Contract 1). Immutable. Cells are 0.25 m in the house's own frame:
/// grid +x points along <see cref="Yaw"/> (ccw from east), +z is up, cell (0,0,0)'s min corner is <see cref="Origin"/> (map local, z up).
/// </summary>
public sealed class HouseGrid
{
    /// <summary>The house's wire id: its position in the file (houses sorted by id).</summary>
    public ushort Index { get; }
    /// <summary>The building id, as interiors.json "id".</summary>
    public string Id { get; }
    public Vector3 Origin { get; }
    public float Yaw { get; }
    public float Cell { get; }
    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }
    /// <summary>Entry 0 is air.</summary>
    public ReadOnlyMemory<(CellMaterial Material, byte R, byte G, byte B)> Palette { get; }
    /// <summary>Palette index per cell, 0 = air, over i = x + nx*(y + ny*z).</summary>
    public ReadOnlyMemory<byte> Cells { get; }

    readonly float _cos, _sin;

    public HouseGrid(ushort index, string id, Vector3 origin, float yaw, float cell, int nx, int ny, int nz,
        ReadOnlyMemory<(CellMaterial Material, byte R, byte G, byte B)> palette, ReadOnlyMemory<byte> cells)
    {
        if (nx <= 0 || ny <= 0 || nz <= 0 || nx > ushort.MaxValue || ny > ushort.MaxValue || nz > ushort.MaxValue) throw new ArgumentException($"house {id}: bad size {nx}x{ny}x{nz}");
        if (!(cell > 0)) throw new ArgumentException($"house {id}: cell must be positive");
        if (palette.Length is 0 or > 64 || palette.Span[0].Material != CellMaterial.Air) throw new ArgumentException($"house {id}: palette of 1..64 entries, entry 0 air");
        if (cells.Length != (long)nx * ny * nz) throw new ArgumentException($"house {id}: {cells.Length} cells, expected {(long)nx * ny * nz}");
        foreach (var c in cells.Span) if (c >= palette.Length) throw new ArgumentException($"house {id}: palette index {c} out of range");
        Index = index; Id = id; Origin = origin; Yaw = yaw; Cell = cell; Nx = nx; Ny = ny; Nz = nz; Palette = palette; Cells = cells;
        _cos = MathF.Cos(yaw); _sin = MathF.Sin(yaw);
    }

    public int Count => Nx * Ny * Nz;

    public int IndexOf(int x, int y, int z) => x + Nx * (y + Ny * z);

    public bool InBounds(int x, int y, int z) => (uint)x < (uint)Nx && (uint)y < (uint)Ny && (uint)z < (uint)Nz;

    /// <summary>The cell coordinates of index <paramref name="i"/>.</summary>
    public (int X, int Y, int Z) Coords(int i) => (i % Nx, i / Nx % Ny, i / (Nx * Ny));

    /// <summary>The material cell <paramref name="i"/> was built of (before any carve).</summary>
    public CellMaterial Material(int i) => Palette.Span[Cells.Span[i]].Material;

    /// <summary>Whether cell <paramref name="i"/> was solid in the file (any carve aside).</summary>
    public bool Original(int i) => Cells.Span[i] != 0;

    /// <summary>Map local (x east, y north, z up, metres) to grid units (cells, float; a cell's centre is at its index + 0.5).</summary>
    public Vector3 ToGrid(Vector3 map)
    {
        var d = map - Origin;
        return new Vector3(d.X * _cos + d.Y * _sin, -d.X * _sin + d.Y * _cos, d.Z) / Cell;
    }

    /// <summary>Grid units back to map local metres (<see cref="ToGrid"/>'s inverse).</summary>
    public Vector3 ToMap(Vector3 grid)
    {
        var g = grid * Cell;
        return Origin + new Vector3(g.X * _cos - g.Y * _sin, g.X * _sin + g.Y * _cos, g.Z);
    }
}

/// <summary>A box of grid cells, [X0, X1) x [Y0, Y1) x [Z0, Z1). The default is empty.</summary>
public readonly record struct CellBox(int X0, int Y0, int Z0, int X1, int Y1, int Z1)
{
    public static CellBox All(HouseGrid g) => new(0, 0, 0, g.Nx, g.Ny, g.Nz);
    public bool Empty => X0 >= X1 || Y0 >= Y1 || Z0 >= Z1;
    /// <summary>Grown by <paramref name="n"/> cells on every side, clipped to the grid.</summary>
    public CellBox Grow(int n, HouseGrid g) => Empty ? this : new(Math.Max(X0 - n, 0), Math.Max(Y0 - n, 0), Math.Max(Z0 - n, 0), Math.Min(X1 + n, g.Nx), Math.Min(Y1 + n, g.Ny), Math.Min(Z1 + n, g.Nz));
    public CellBox Union(CellBox o) => Empty ? o : o.Empty ? this : new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Min(Z0, o.Z0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1), Math.Max(Z1, o.Z1));
    /// <summary>The box of the cells where <paramref name="set"/> is not 0 (empty when none).</summary>
    public static CellBox Of(HouseGrid g, ReadOnlySpan<byte> set)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1;
        for (var i = 0; i < set.Length; i++)
        {
            if (set[i] == 0) continue;
            var (x, y, z) = g.Coords(i);
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
        }
        return x1 < 0 ? default : new(x0, y0, z0, x1 + 1, y1 + 1, z1 + 1);
    }
}
