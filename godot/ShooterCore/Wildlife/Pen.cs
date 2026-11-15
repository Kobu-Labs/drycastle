using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>
/// The ground a pet may stand on: the union of the parcels of one property group. Each parcel is a polygon (an outer ring, then holes, even-odd);
/// points are Godot ground axes (x east, z). The unit is the property group, never a lone parcel. Godot-free.
/// </summary>
public sealed class Pen
{
    sealed class Part
    {
        public Vector2[][] Rings = Array.Empty<Vector2[]>();
        public float MinX, MinZ, MaxX, MaxZ, Area;
    }

    readonly Part[] _parts;
    readonly float _totalArea;

    /// <summary>One entry per parcel: its rings, outer ring first, then holes. A parcel whose outer ring has under 3 points (or no ring) is dropped whole; degenerate holes are dropped.</summary>
    public Pen(IEnumerable<IReadOnlyList<Vector2[]>> parcels)
    {
        var parts = new List<Part>();
        foreach (var rings in parcels)
        {
            if (rings.Count == 0 || rings[0].Length < 3) continue; // no usable outer ring: the parcel is out, a hole must never stand in for it
            var kept = rings.Where(r => r.Length >= 3).ToArray(); // kept[0] is the outer ring
            var p = new Part { Rings = kept, MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue };
            foreach (var v in kept[0])
            {
                p.MinX = MathF.Min(p.MinX, v.X); p.MaxX = MathF.Max(p.MaxX, v.X);
                p.MinZ = MathF.Min(p.MinZ, v.Y); p.MaxZ = MathF.Max(p.MaxZ, v.Y);
            }
            p.Area = MathF.Max(0, MathF.Abs(SignedArea(kept[0])) - kept.Skip(1).Sum(h => MathF.Abs(SignedArea(h))));
            parts.Add(p);
        }
        _parts = parts.ToArray();
        _totalArea = _parts.Sum(p => p.Area);
    }

    /// <summary>No parcel with a usable polygon.</summary>
    public bool IsEmpty => _parts.Length == 0 || !(_totalArea > 0);

    /// <summary>Square metres inside the pen (outer rings minus holes, parcels summed).</summary>
    public float AreaM2 => _totalArea;

    /// <summary>True when (x, z) is inside one of the parcels (even-odd over its rings, so a hole is outside).</summary>
    public bool Contains(float x, float z)
    {
        foreach (var p in _parts)
            if (Contains(p, x, z)) return true;
        return false;
    }

    /// <summary>A uniformly random point of the pen (parcels weighted by their area), or null when the pen is empty or no try landed inside.</summary>
    public Vector2? RandomPoint(Random rng, int attempts = 64)
    {
        if (IsEmpty) return null;
        for (var i = 0; i < attempts; i++)
        {
            var at = rng.NextDouble() * _totalArea;
            var p = _parts[^1];
            foreach (var q in _parts)
            {
                at -= q.Area;
                if (at < 0) { p = q; break; }
            }
            var x = p.MinX + (float)rng.NextDouble() * (p.MaxX - p.MinX);
            var z = p.MinZ + (float)rng.NextDouble() * (p.MaxZ - p.MinZ);
            if (Contains(p, x, z)) return new Vector2(x, z);
        }
        return null;
    }

    static bool Contains(Part part, float x, float z)
    {
        if (x < part.MinX || x > part.MaxX || z < part.MinZ || z > part.MaxZ) return false;
        var inside = false;
        foreach (var ring in part.Rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    static float SignedArea(Vector2[] ring)
    {
        var s = 0f;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++) s += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
        return s / 2;
    }
}

/// <summary>An <see cref="IAnimalWorld"/> that fences a pet into a <see cref="Pen"/>: no ground outside it, so the animal's own steps refuse to leave. Nothing frightens a pet.</summary>
public sealed class PennedWorld : IPennedWorld
{
    readonly IAnimalWorld _inner;
    readonly Pen _pen;

    public PennedWorld(IAnimalWorld inner, Pen pen) { _inner = inner; _pen = pen; }

    public float? GroundY(float x, float z) => _pen.Contains(x, z) ? _inner.GroundY(x, z) : null;

    public bool Inside(float x, float z) => _pen.Contains(x, z);

    /// <summary>The ground with no pen (what a pet carried out of it walks on).</summary>
    public IAnimalWorld Open => _inner;

    public Vector3? NearestThreat(Vector3 from, float radius) => null;

    public Vector3? PositionOf(ushort player) => _inner.PositionOf(player);
}

/// <summary>
/// Which parcels belong to which property group, read from <c>groups.json</c> of the built map (the pipeline's export writes it): <c>{"groups": {"G-0001": {"parcels": ["P-0026", ...]}}}</c>,
/// the shape of the <c>groups</c> section of an edits file.
/// </summary>
public static class PropertyGroups
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string json)
    {
        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("groups", out var g) || g.ValueKind != JsonValueKind.Object) return groups;
        foreach (var entry in g.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object || !entry.Value.TryGetProperty("parcels", out var ps) || ps.ValueKind != JsonValueKind.Array) continue;
            var ids = ps.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
            if (ids.Count > 0) groups[entry.Name] = ids;
        }
        return groups;
    }
}
