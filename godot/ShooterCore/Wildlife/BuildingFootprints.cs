namespace Drycastle.ShooterCore.Wildlife;

/// <summary>
/// Where the buildings are, for <see cref="IAnimalWorld.NearestBuildingM"/>: polygons in ground axes (x, z), even-odd over a polygon's rings (outer first, then holes).
/// The server fills it with the parcels that hold a building (the parcel outline is at least as far out as the building, so "m from the outline" is never closer than m to the building).
/// </summary>
public sealed class BuildingFootprints
{
    sealed record Poly(float MinX, float MinZ, float MaxX, float MaxZ, (float X, float Z)[][] Rings);

    readonly List<Poly> _polys = new();

    public int Count => _polys.Count;

    public void Add(IReadOnlyList<(float X, float Z)[]> rings)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var ring in rings)
            foreach (var (x, z) in ring) { minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x); minZ = MathF.Min(minZ, z); maxZ = MathF.Max(maxZ, z); }
        if (minX <= maxX) _polys.Add(new Poly(minX, minZ, maxX, maxZ, rings.ToArray()));
    }

    /// <summary>Flat distance to the nearest polygon (0 inside one); infinity when none is within <paramref name="maxM"/>.</summary>
    public float Nearest(float x, float z, float maxM)
    {
        var best = float.PositiveInfinity;
        foreach (var p in _polys)
        {
            var reach = MathF.Min(maxM, best);
            if (x < p.MinX - reach || x > p.MaxX + reach || z < p.MinZ - reach || z > p.MaxZ + reach) continue;
            var inside = false;
            var edge = float.PositiveInfinity;
            foreach (var ring in p.Rings)
            {
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    var (ax, az) = ring[i]; var (bx, bz) = ring[j];
                    if ((az > z) != (bz > z) && x < (bx - ax) * (z - az) / (bz - az) + ax) inside = !inside;
                    edge = MathF.Min(edge, SegmentDistance(x, z, ax, az, bx, bz));
                }
            }
            if (inside) return 0;
            if (edge < best && edge <= maxM) best = edge;
        }
        return best;
    }

    static float SegmentDistance(float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        var len2 = dx * dx + dz * dz;
        var t = len2 > 0 ? Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len2, 0f, 1f) : 0f;
        float cx = ax + t * dx - px, cz = az + t * dz - pz;
        return MathF.Sqrt(cx * cx + cz * cz);
    }
}
