using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// A crop field (#328/#331): a ground patch (<c>GP-NNNN</c>) of a <c>crop_*</c> ground type. <see cref="Ring"/> is its outline in game metres
/// (X east, Y is the game's Z, south). One patch with several pieces gives several fields with the same <see cref="Id"/>.
/// </summary>
public sealed class CropField
{
    public CropField(string id, string type, IReadOnlyList<Vector2> ring)
    {
        Id = id; Type = type; Ring = ring;
        Area = MathF.Abs(Poly.SignedArea(ring));
        Centroid = Poly.Centroid(ring);
    }

    public string Id { get; }
    /// <summary>The ground type, e.g. <c>crop_grain</c>.</summary>
    public string Type { get; }
    public IReadOnlyList<Vector2> Ring { get; }
    public float Area { get; }
    public Vector2 Centroid { get; }

    /// <summary>Whether a ground patch type is a crop a harvester cuts (every crop type but the bare one).</summary>
    public static bool IsHarvestable(string type) => type.StartsWith("crop_", StringComparison.Ordinal) && type != "crop_bare";
}

/// <summary>The fields of a map and the choice of one for an event.</summary>
public static class CropFields
{
    /// <summary>The harvestable fields among <paramref name="patches"/> (id, ground type, ring), in the given order (client and server list them the same way: the index is the field's wire id).</summary>
    public static List<CropField> From(IEnumerable<(string Id, string Type, IReadOnlyList<Vector2> Ring)> patches)
    {
        var list = new List<CropField>();
        foreach (var (id, type, ring) in patches)
            if (CropField.IsHarvestable(type) && ring.Count >= 3) list.Add(new CropField(id, type, ring));
        return list;
    }

    /// <summary>
    /// The field for a new harvester: among the fields big enough (<paramref name="minAreaM2"/>) with a player within <paramref name="nearM"/> of their outline, a random one;
    /// with none near, the nearest to any player when <paramref name="orNearest"/> (an admin's command), else null. No players: a random big field when <paramref name="orNearest"/>.
    /// </summary>
    public static CropField? Pick(IReadOnlyList<CropField> fields, IReadOnlyList<Vector2> players, float nearM, float minAreaM2, Random rng, bool orNearest)
    {
        var big = fields.Where(f => f.Area >= minAreaM2).ToList();
        if (big.Count == 0) return null;
        float Dist(CropField f) => players.Count == 0 ? float.MaxValue : players.Min(p => Poly.DistanceToRing(f.Ring, p));
        var near = big.Where(f => Dist(f) <= nearM).ToList();
        if (near.Count > 0) return near[rng.Next(near.Count)];
        if (!orNearest) return null;
        if (players.Count == 0) return big[rng.Next(big.Count)];
        return big.OrderBy(Dist).First();
    }
}

/// <summary>Plain 2D polygon helpers (rings are not closed: the last vertex joins the first).</summary>
public static class Poly
{
    public static float SignedArea(IReadOnlyList<Vector2> r)
    {
        var a = 0f;
        for (var i = 0; i < r.Count; i++) { var p = r[i]; var q = r[(i + 1) % r.Count]; a += p.X * q.Y - q.X * p.Y; }
        return a / 2f;
    }

    public static Vector2 Centroid(IReadOnlyList<Vector2> r)
    {
        var a = SignedArea(r);
        if (MathF.Abs(a) < 1e-4f) { var s = Vector2.Zero; foreach (var p in r) s += p; return s / Math.Max(1, r.Count); }
        float cx = 0, cy = 0;
        for (var i = 0; i < r.Count; i++)
        {
            var p = r[i]; var q = r[(i + 1) % r.Count];
            var k = p.X * q.Y - q.X * p.Y;
            cx += (p.X + q.X) * k; cy += (p.Y + q.Y) * k;
        }
        return new Vector2(cx / (6 * a), cy / (6 * a));
    }

    public static bool Contains(IReadOnlyList<Vector2> r, Vector2 pt)
    {
        var inside = false;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            if ((r[i].Y > pt.Y) != (r[j].Y > pt.Y) && pt.X < (r[j].X - r[i].X) * (pt.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X) inside = !inside;
        return inside;
    }

    public static float DistanceToRing(IReadOnlyList<Vector2> r, Vector2 pt)
    {
        if (Contains(r, pt)) return 0;
        var best = float.MaxValue;
        for (var i = 0; i < r.Count; i++) best = MathF.Min(best, DistanceToSegment(pt, r[i], r[(i + 1) % r.Count]));
        return best;
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var l2 = ab.LengthSquared();
        var t = l2 < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
}
