using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.ShooterCore.Plantation;

/// <summary>
/// The outlines of the zones of the kinds the server asks "is a player inside?" of (zone_polygons.json, ADR 0022): today the plantation. The pipeline writes them because a pool of candidate
/// points (<see cref="ZonePoints"/>) cannot answer that. Scene-free. A map without the file, or a kind with no zone, has no polygons: <see cref="Contains"/> is false and the feature is simply off.
/// <para>
/// Axes: Godot ground axes, x east and z = -north, which is how the file already stores its rings. Point-in-polygon is 2D (the height is ignored): a zone is a piece of the ground plan.
/// </para>
/// </summary>
public sealed class ZonePolygons
{
    public const int SupportedVersion = 1;

    public static readonly ZonePolygons Empty = new(new Dictionary<string, Ring[]>());

    /// <summary>One closed outline (the last point joins the first) with its bounding box, for a quick reject.</summary>
    public sealed class Ring
    {
        public readonly Vector2[] Points;
        readonly float _minX, _minZ, _maxX, _maxZ;

        public Ring(IEnumerable<Vector2> points)
        {
            Points = points.ToArray();
            _minX = _minZ = float.MaxValue; _maxX = _maxZ = float.MinValue;
            foreach (var p in Points) { _minX = MathF.Min(_minX, p.X); _maxX = MathF.Max(_maxX, p.X); _minZ = MathF.Min(_minZ, p.Y); _maxZ = MathF.Max(_maxZ, p.Y); }
        }

        public bool Contains(float x, float z)
        {
            if (Points.Length < 3 || x < _minX || x > _maxX || z < _minZ || z > _maxZ) return false;
            return PointInPolygon(Points, x, z);
        }
    }

    readonly Dictionary<string, Ring[]> _rings;

    ZonePolygons(Dictionary<string, Ring[]> rings) => _rings = rings;

    /// <summary>The outlines of a zone kind, or none.</summary>
    public IReadOnlyList<Ring> Rings(string kind) => _rings.TryGetValue(kind, out var r) ? r : Array.Empty<Ring>();

    /// <summary>Whether the kind has any zone on this map.</summary>
    public bool Has(string kind) => Rings(kind).Count > 0;

    /// <summary>Whether the ground point (x east, z = -north) lies inside any zone of the kind.</summary>
    public bool Contains(string kind, float x, float z)
    {
        foreach (var ring in Rings(kind)) if (ring.Contains(x, z)) return true;
        return false;
    }

    /// <summary>
    /// Even-odd ray cast in the ground plane (x, z) over a closed outline given without the closing point, in either winding. A point exactly on an edge may count either way. Self-overlapping outlines
    /// count by parity, like the map's polygons.
    /// </summary>
    public static bool PointInPolygon(IReadOnlyList<Vector2> ring, float x, float z)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i]; var b = ring[j];
            if ((a.Y > z) == (b.Y > z)) continue;
            if (x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Parses the file contents; an empty string is the map without the file (no polygons).</summary>
    public static ZonePolygons FromJson(string? json) => string.IsNullOrEmpty(json) ? Empty : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no polygons.</summary>
    public static ZonePolygons FromJsonOrEmpty(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return Empty; }
    }

    /// <summary>
    /// Parses zone_polygons.json: <c>{"version": 1, "zones": [{"kind", "areas", "rings": [[[x, z], ...], ...]}]}</c> with the points in Godot ground axes already. Unknown fields are ignored; any
    /// version but <see cref="SupportedVersion"/> is refused.
    /// </summary>
    public static ZonePolygons Parse(string json, string name = "zone_polygons.json")
    {
        using (var doc = DataFile.Document(json, name))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DataFileException($"{name}: not an object");
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version < 1)
                throw new DataFileException($"{name}: missing or invalid version");
            if (version != SupportedVersion) throw new DataFileException($"{name}: version {version} is not the supported version {SupportedVersion}");
            if (!root.TryGetProperty("zones", out var zones) || zones.ValueKind != JsonValueKind.Array) throw new DataFileException($"{name}: missing zones array");
            var rings = new Dictionary<string, List<Ring>>();
            foreach (var z in zones.EnumerateArray())
            {
                var kind = z.ValueKind == JsonValueKind.Object && z.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                if (kind.Length == 0) throw new DataFileException($"{name}: an entry without kind");
                if (!z.TryGetProperty("rings", out var rs) || rs.ValueKind != JsonValueKind.Array) throw new DataFileException($"{name}: {kind} has no rings array");
                if (!rings.TryGetValue(kind, out var list)) rings[kind] = list = new List<Ring>();
                foreach (var r in rs.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Array) throw new DataFileException($"{name}: {kind} has a ring that is not an array");
                    var pts = new List<Vector2>(r.GetArrayLength());
                    foreach (var p in r.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2 || !Number(p[0], out var x) || !Number(p[1], out var pz))
                            throw new DataFileException($"{name}: {kind} has a point that is not [x, z]");
                        pts.Add(new Vector2(x, pz));
                    }
                    if (pts.Count >= 3) list.Add(new Ring(pts));
                }
            }
            return new ZonePolygons(rings.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }
    }

    static bool Number(JsonElement e, out float f)
    {
        f = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out f) && float.IsFinite(f);
    }
}
