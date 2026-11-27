using System.Numerics;
using System.Text.Json.Serialization;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>A link of a floor plan room: the gap in a partition from <see cref="P"/> to <see cref="Q"/> (map local, x east, y north) into room <see cref="To"/>; kind door, arch or open.</summary>
public sealed record PlanLink(string To, string Kind, Vector2 P, Vector2 Q);

/// <summary>One room of a floor plan (GLOSSARY "Floor plan", "Room type"): its outline (map local; the middle of its partitions, the inner face of the walls), its exterior doors and links.</summary>
public sealed record PlanRoom(string Id, string Kind, string Type, float AreaM2, Vector2[] Polygon, string[] Doors, string[] GarageDoors, PlanLink[] Links);

/// <summary>A door or garage door of a hollow house in its wall, map local: the opening runs from <see cref="A"/> to <see cref="B"/> at the middle of the wall.</summary>
public sealed record PlanDoor(string Id, HouseDoorKind Kind, Vector2 A, Vector2 B);

/// <summary>
/// A hollow house as interiors.json gives it (docs/interiors.md), as far as dressing it needs: the shell's outline and volume, the floor, its doors and its floor plan's rooms. Map local
/// axes (x east, y north, z up; Godot is x, z, -y). Godot-free. A house with no plan (under 8 or over 350 m² inside) has no rooms and is never dressed.
/// </summary>
public sealed record HouseFloorPlan(string Id, string Parcel, float FloorZ, float CeilingZ, Vector2[] Shell, float Z0, float Z1, PlanDoor[] Doors, PlanRoom[] Rooms)
{
    /// <summary>A map point at <paramref name="up"/> metres over the floor, in Godot axes.</summary>
    public Vector3 ToGodot(Vector2 local, float up) => new(local.X, FloorZ + up, -local.Y);

    /// <summary>Whether a body whose feet are at <paramref name="feet"/> (Godot) is inside the house's shelter volume.</summary>
    public bool Contains(Vector3 feet) => feet.Y >= Z0 && feet.Y <= Z1 && PlanGeometry.Inside(Shell, new Vector2(feet.X, -feet.Z));

    public PlanRoom? Room(string id) => Rooms.FirstOrDefault(r => r.Id == id);

    /// <summary>The hollow houses of an interiors.json text, in file order; none for an empty text.</summary>
    public static IReadOnlyList<HouseFloorPlan> Parse(string? json, string name = "interiors.json")
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<HouseFloorPlan>();
        var f = DataFile.Parse<InteriorPlanFile>(json, name);
        var list = new List<HouseFloorPlan>();
        foreach (var h in f.Houses)
        {
            var doors = h.Doors.Where(d => d.Pos.Length == 3 && d.Width > 0).Select(d =>
            {
                var yaw = d.Yaw * MathF.PI / 180f;
                var along = new Vector2(MathF.Cos(yaw), -MathF.Sin(yaw)); // across the opening: the compass yaw points out of the house
                var mid = new Vector2(d.Pos[0], d.Pos[1]);
                return new PlanDoor(d.Id, d.Kind, mid - along * (d.Width / 2), mid + along * (d.Width / 2));
            }).ToArray();
            var rooms = h.Rooms.Select(r => new PlanRoom(r.Id, r.Kind, r.Type, r.AreaM2, Poly(r.Polygon), r.Doors.ToArray(), r.GarageDoors.ToArray(),
                r.Links.Where(l => l.P.Length >= 2 && l.Q.Length >= 2).Select(l => new PlanLink(l.To, l.Kind, new Vector2(l.P[0], l.P[1]), new Vector2(l.Q[0], l.Q[1]))).ToArray()))
                .Where(r => r.Polygon.Length >= 3).ToArray();
            list.Add(new HouseFloorPlan(h.Id, h.Parcel, h.FloorZ, h.CeilingZ, Poly(h.Room.Polygon), h.Room.Z0, h.Room.Z1, doors, rooms));
        }
        return list;
    }

    static Vector2[] Poly(List<float[]> pts) => pts.Where(p => p.Length >= 2).Select(p => new Vector2(p[0], p[1])).ToArray();
}

/// <summary>interiors.json as the Trap house reads it (the doors' own reader is <see cref="HouseDoors"/>).</summary>
public sealed class InteriorPlanFile : IVersioned
{
    public int Version { get; set; }
    public List<InteriorPlanHouse> Houses { get; set; } = new();
}

public sealed class InteriorPlanHouse
{
    public string Id { get; set; } = "";
    public string Parcel { get; set; } = "";
    public float FloorZ { get; set; }
    public float CeilingZ { get; set; }
    public InteriorPlanVolume Room { get; set; } = new();
    public List<InteriorDoorDef> Doors { get; set; } = new();
    public List<InteriorPlanRoom> Rooms { get; set; } = new();
}

public sealed class InteriorPlanVolume
{
    public List<float[]> Polygon { get; set; } = new();
    public float Z0 { get; set; }
    public float Z1 { get; set; }
}

public sealed class InteriorPlanRoom
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    [JsonPropertyName("area_m2")] public float AreaM2 { get; set; }
    public List<float[]> Polygon { get; set; } = new();
    public List<string> Doors { get; set; } = new();
    public List<string> GarageDoors { get; set; } = new();
    public List<InteriorPlanLink> Links { get; set; } = new();
}

public sealed class InteriorPlanLink
{
    public string To { get; set; } = "";
    public string Kind { get; set; } = "";
    public float[] P { get; set; } = Array.Empty<float>();
    public float[] Q { get; set; } = Array.Empty<float>();
}

/// <summary>The plane geometry the Trap house planner needs: polygons, segments and oriented rectangles in map local metres.</summary>
public static class PlanGeometry
{
    /// <summary>Whether <paramref name="p"/> lies inside the polygon (even-odd).</summary>
    public static bool Inside(IReadOnlyList<Vector2> poly, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The signed area (positive: counter-clockwise).</summary>
    public static float SignedArea(IReadOnlyList<Vector2> poly)
    {
        var s = 0f;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) s += poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
        return s / 2;
    }

    public static float PointSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        var t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Whether segments a-b and c-d cross or touch.</summary>
    public static bool Cross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a), d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        const float eps = 1e-6f;
        return (MathF.Abs(d1) < eps && PointSegment(c, a, b) < 1e-4f) || (MathF.Abs(d2) < eps && PointSegment(d, a, b) < 1e-4f)
            || (MathF.Abs(d3) < eps && PointSegment(a, c, d) < 1e-4f) || (MathF.Abs(d4) < eps && PointSegment(b, c, d) < 1e-4f);
    }

    public static float SegmentSegment(Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
        Cross(a, b, c, d) ? 0 : MathF.Min(MathF.Min(PointSegment(a, c, d), PointSegment(b, c, d)), MathF.Min(PointSegment(c, a, b), PointSegment(d, a, b)));

    /// <summary>The distance between two polygons: 0 when they overlap or one holds the other.</summary>
    public static float PolygonPolygon(IReadOnlyList<Vector2> p, IReadOnlyList<Vector2> q)
    {
        if (Inside(q, p[0]) || Inside(p, q[0])) return 0;
        var best = float.MaxValue;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            for (int k = 0, l = q.Count - 1; k < q.Count; l = k++)
                best = MathF.Min(best, SegmentSegment(p[j], p[i], q[l], q[k]));
        return best;
    }

    /// <summary>The distance from a segment to a polygon's area: 0 when it touches or is inside.</summary>
    public static float SegmentPolygon(Vector2 a, Vector2 b, IReadOnlyList<Vector2> poly)
    {
        if (Inside(poly, a)) return 0;
        var best = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) best = MathF.Min(best, SegmentSegment(a, b, poly[j], poly[i]));
        return best;
    }

    /// <summary>The distance from <paramref name="p"/> to the polygon's outline.</summary>
    public static float ToOutline(IReadOnlyList<Vector2> poly, Vector2 p)
    {
        var best = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) best = MathF.Min(best, PointSegment(p, poly[j], poly[i]));
        return best;
    }

    /// <summary>The distance from a polygon's area to another polygon's outline (0 when they cross).</summary>
    public static float AreaToOutline(IReadOnlyList<Vector2> area, IReadOnlyList<Vector2> outline)
    {
        var best = float.MaxValue;
        for (int i = 0, j = area.Count - 1; i < area.Count; j = i++)
            for (int k = 0, l = outline.Count - 1; k < outline.Count; l = k++)
                best = MathF.Min(best, SegmentSegment(area[j], area[i], outline[l], outline[k]));
        return best;
    }

    /// <summary>Whether polygon <paramref name="inner"/> lies wholly inside <paramref name="outer"/> (its corners inside, no outline crossing).</summary>
    public static bool Within(IReadOnlyList<Vector2> inner, IReadOnlyList<Vector2> outer) =>
        inner.All(c => Inside(outer, c)) && AreaToOutline(inner, outer) > 1e-4f;

    /// <summary>The corners of a rectangle: centre, unit front, across (the full width) and depth (the full length front to back).</summary>
    public static Vector2[] Rect(Vector2 centre, Vector2 front, float across, float depth)
    {
        var right = new Vector2(front.Y, -front.X);
        var r = right * (across / 2); var f = front * (depth / 2);
        return new[] { centre - r - f, centre + r - f, centre + r + f, centre - r + f };
    }
}
