using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>One carve: a sphere, centre and radius in quarter-cells, in the house's grid frame (Contract 3 and 4). Cell (x, y, z)'s centre is at quarter-cell 4x + 2.</summary>
public readonly record struct CarveOp(short Cx, short Cy, short Cz, ushort RadiusQ)
{
    /// <summary>
    /// The op of a blast at <paramref name="map"/> (map local, z up) of <paramref name="radiusM"/> metres: the centre rounded to the nearest quarter-cell
    /// in the grid frame, the radius rounded to quarter-cells. Null when the sphere misses the grid's box, the radius rounds to 0, or the centre is out of
    /// the i16 range (thousands of cells away, so it misses anyway). Floats are used only here; everything after it is integer.
    /// </summary>
    public static CarveOp? From(HouseGrid g, Vector3 map, float radiusM)
    {
        if (!(radiusM > 0) || !float.IsFinite(map.X) || !float.IsFinite(map.Y) || !float.IsFinite(map.Z)) return null;
        var p = g.ToGrid(map) * 4f;
        var rq = MathF.Round(radiusM / g.Cell * 4f);
        if (!(rq >= 1)) return null;
        var r = (long)MathF.Min(rq, ushort.MaxValue);
        if (!Fits(p.X) || !Fits(p.Y) || !Fits(p.Z)) return null;
        long cx = (long)MathF.Round(p.X), cy = (long)MathF.Round(p.Y), cz = (long)MathF.Round(p.Z);
        long dx = Gap(cx, 4L * g.Nx), dy = Gap(cy, 4L * g.Ny), dz = Gap(cz, 4L * g.Nz);
        if (dx * dx + dy * dy + dz * dz > r * r) return null;
        return new CarveOp((short)cx, (short)cy, (short)cz, (ushort)r);
    }

    static bool Fits(float v) => MathF.Round(v) is >= short.MinValue and <= short.MaxValue;

    /// <summary>Distance from <paramref name="c"/> to the span [0, <paramref name="max"/>].</summary>
    static long Gap(long c, long max) => c < 0 ? -c : c > max ? c - max : 0;
}
