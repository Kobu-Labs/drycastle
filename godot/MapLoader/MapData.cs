using Godot;

namespace Drycastle.MapLoader;

/// <summary>A parcel of the base map (from parcels.json).</summary>
public sealed class ParcelInfo
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string CadastralRef { get; init; } = "";
    /// <summary>Chunk index (ix, iy).</summary>
    public Vector2I Chunk { get; init; }
    public IReadOnlyList<string> BuildingIds { get; init; } = Array.Empty<string>();
    /// <summary>Anchor in map metres: x east, y north.</summary>
    public Vector2 AnchorMap { get; init; }
    /// <summary>Anchor in Godot axes (x east, z = -north); y is 0 until placed on terrain.</summary>
    public Vector3 AnchorXZ => new(AnchorMap.X, 0f, -AnchorMap.Y);
    /// <summary>True when the chunk glTF has parcel_ID_visual / parcel_ID_collision nodes for it.</summary>
    public bool HasModel { get; internal set; }

    /// <summary>
    /// Polygon rings (outer ring first, then holes) in Godot ground axes: Vector2(x east, z = -north).
    /// From parcels.json `polygon` (map metres, x east, y north).
    /// </summary>
    public IReadOnlyList<Vector2[]> Rings { get; init; } = Array.Empty<Vector2[]>();
    /// <summary>Bounding box of the polygon in Godot ground axes (Position = min x/z).</summary>
    public Rect2 Bounds { get; init; }
    /// <summary>Area in square metres (outer ring minus holes).</summary>
    public float AreaM2 { get; init; }
    /// <summary>Property group the parcel belongs to (G-NNNN), filled from the edits file; null when none.</summary>
    public string? Group { get; set; }

    /// <summary>Point in polygon (even-odd over all rings) for a ground point (x, z) in Godot axes.</summary>
    public bool Contains(Vector2 p)
    {
        if (!Bounds.HasPoint(p)) return false;
        var inside = false;
        foreach (var ring in Rings)
        {
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
        }
        return inside;
    }

    internal static float RingArea(Vector2[] ring)
    {
        double s = 0;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            s += (double)ring[j].X * ring[i].Y - (double)ring[i].X * ring[j].Y;
        return (float)Math.Abs(s / 2);
    }
}

/// <summary>
/// Coarse uniform grid over parcel bounding boxes: a point lookup tests only the parcels whose box
/// touches the point's cell, so picking among 1000+ parcels stays instant.
/// </summary>
public sealed class ParcelIndex
{
    const float Cell = 64f;
    readonly Dictionary<long, List<ParcelInfo>> _cells = new();

    static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    public ParcelIndex(IEnumerable<ParcelInfo> parcels)
    {
        foreach (var p in parcels)
        {
            if (p.Rings.Count == 0) continue;
            var b = p.Bounds;
            int x0 = (int)Mathf.Floor(b.Position.X / Cell), x1 = (int)Mathf.Floor(b.End.X / Cell);
            int z0 = (int)Mathf.Floor(b.Position.Y / Cell), z1 = (int)Mathf.Floor(b.End.Y / Cell);
            for (var x = x0; x <= x1; x++)
                for (var z = z0; z <= z1; z++)
                {
                    var k = Key(x, z);
                    if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new();
                    l.Add(p);
                }
        }
    }

    /// <summary>The parcel containing the ground point (x, z); null when none. The smallest wins if several overlap.</summary>
    public ParcelInfo? At(float x, float z)
    {
        if (!_cells.TryGetValue(Key((int)Mathf.Floor(x / Cell), (int)Mathf.Floor(z / Cell)), out var l)) return null;
        ParcelInfo? best = null;
        var p = new Vector2(x, z);
        foreach (var c in l)
            if (c.Contains(p) && (best == null || c.AreaM2 < best.AreaM2)) best = c;
        return best;
    }
}

/// <summary>A spawn candidate (from spawns.json).</summary>
public sealed class SpawnPoint
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Parcel { get; init; } = "";
    public string Highway { get; init; } = "";
    public Vector2I Chunk { get; init; }
    /// <summary>Position in Godot axes: x east, y elevation, z = -north.</summary>
    public Vector3 Position { get; init; }
}

/// <summary>Counters filled while loading.</summary>
public sealed class LoadStats
{
    public int ChunksLoaded;
    public int CollidersBuilt;
    public int Triangles;
    /// <summary>Ground clutter instances built (0 on a headless load or a map without clutter).</summary>
    public int ClutterInstances;
    public double LoadSeconds;
}

/// <summary>Rule values from the manifest `edit_defaults`: what the pipeline uses when an edit leaves a value unset.</summary>
public sealed class EditDefaults
{
    public double FenceHeight { get; init; }
    public double FenceThickness { get; init; }
    public string FenceColour { get; init; } = "";
    public double FenceProbability { get; init; }
    public IReadOnlyList<string> FenceKinds { get; init; } = Array.Empty<string>();
    /// <summary>Trees per 1000 m2 by parcel kind (kinds not listed get none).</summary>
    public IReadOnlyDictionary<string, double> TreeDensity { get; init; } = new Dictionary<string, double>();

    internal static EditDefaults Read(System.Text.Json.JsonElement e)
    {
        var f = e.GetProperty("fence");
        var t = e.GetProperty("trees");
        return new EditDefaults
        {
            FenceHeight = f.GetProperty("height_m").GetDouble(),
            FenceThickness = f.GetProperty("thickness_m").GetDouble(),
            FenceColour = f.GetProperty("colour").GetString() ?? "",
            FenceProbability = f.GetProperty("probability").GetDouble(),
            FenceKinds = f.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToList(),
            TreeDensity = t.GetProperty("density_per_1000m2").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble()),
        };
    }
}
