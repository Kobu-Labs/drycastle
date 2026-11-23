using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// vehicle_outlines.json (issue #334): the top-view silhouette of each vehicle for the seat panel, a closed polygon of [x, z] points in vehicle-frame metres
/// (x right, z backward, so forward is -z), generated from the models by scripts/vehicles/make_vehicle_outlines.py. A vehicle without an entry is drawn as its hit box.
/// </summary>
public class VehicleOutlinesFile : IVersioned
{
    public int Version { get; set; }
    public Dictionary<string, List<float[]>> Outlines { get; set; } = new();

    public static VehicleOutlinesFile Load(string path) => Validate(DataFile.Load<VehicleOutlinesFile>(path));
    public static VehicleOutlinesFile Parse(string json) => Validate(DataFile.Parse<VehicleOutlinesFile>(json, "vehicle_outlines.json"));

    public static VehicleOutlinesFile Validate(VehicleOutlinesFile f)
    {
        foreach (var (id, o) in f.Outlines)
            if (o.Count < 3 || o.Any(p => p is null || p.Length != 2 || !float.IsFinite(p[0]) || !float.IsFinite(p[1])))
                throw new DataFileException($"vehicle_outlines.json: {id}: an outline is at least 3 points of [x, z]");
        return f;
    }

    /// <summary>The outline of a vehicle type, or null (no entry: the caller draws the hit box).</summary>
    public IReadOnlyList<float[]>? For(string id) => Outlines.TryGetValue(id, out var o) ? o : null;
}

/// <summary>Plain 2D geometry on an outline.</summary>
public static class OutlineGeometry
{
    /// <summary>Ray-casting point in polygon.</summary>
    public static bool Contains(IReadOnlyList<float[]> poly, float x, float z)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            float xi = poly[i][0], zi = poly[i][1], xj = poly[j][0], zj = poly[j][1];
            if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Distance from the point to the nearest edge of the polygon.</summary>
    public static float EdgeDistance(IReadOnlyList<float[]> poly, float x, float z)
    {
        var best = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            float ax = poly[j][0], az = poly[j][1], bx = poly[i][0], bz = poly[i][1];
            float dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            var t = l2 < 1e-9f ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1);
            float px = ax + t * dx - x, pz = az + t * dz - z;
            best = Math.Min(best, MathF.Sqrt(px * px + pz * pz));
        }
        return best;
    }
}
