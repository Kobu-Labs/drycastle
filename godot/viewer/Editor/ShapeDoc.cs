using Drycastle.Viewer.Edits;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The polygons the two shape tools share: ground PATCHES in the Ground tool (edits.json `ground_patches`,
/// {GP-NNNN: {type, polygon, water_y?}}) and ZONES in the Zones tool (`zones`, {Z-NNNN: {kind, polygon}}), both in absolute
/// S-JTSK metres (GLOSSARY "Ground patch", "Zone"; ADR 0022). Their data and file format; the editing gestures are <see cref="Tools.ShapeEditor"/>.
/// </summary>
public enum ShapeKind { Patch, Zone }

/// <summary>One drawn polygon: its ID and corners in absolute metres.</summary>
public record ShapeInfo(string Id, List<(double x, double y)> Points)
{
    /// <summary>What the user reads for this shape (its ID; a zone that comes from a rule has the ground area's).</summary>
    public virtual string Label => Id;
    public double AreaM2
    {
        get
        {
            double a = 0;
            for (var i = 0; i < Points.Count; i++) { var p = Points[i]; var q = Points[(i + 1) % Points.Count]; a += p.x * q.y - q.x * p.y; }
            return Math.Abs(a) / 2;
        }
    }
}

/// <summary>A ground patch: exactly one ground type, plus the height of the water surface when the type is water.</summary>
public sealed record PatchInfo(string Id, string Type, List<(double x, double y)> Points, double? WaterY) : ShapeInfo(Id, Points);

/// <summary>
/// A zone: exactly one zone kind of the catalog. Drawn ones are `zones.Z-NNNN`; one that comes from a rule (a ground area of
/// the kind's where_ground types, minus the cut ones) has <see cref="AreaPart"/> (its ground area, `A-0012`, `A-0012.2` for the
/// second outline of a multi-part one) and an ID of area and kind, since one area is a zone of several kinds. Both behave the
/// same to the user (Tools/ZonesTool.cs "detach" makes an edit of a rule-derived one an ordinary zone plus a cut).
/// Points are the outline in absolute metres; Holes are not selectable, they only keep clicks and the overlay honest.
/// </summary>
public sealed record ZoneInfo(string Id, string Kind, List<(double x, double y)> Points, string? AreaPart = null, List<List<(double x, double y)>>? Holes = null) : ShapeInfo(Id, Points)
{
    public bool FromRule => AreaPart != null;
    public override string Label => AreaPart ?? Id;
    readonly (double MinX, double MinY, double MaxX, double MaxY) _box = Points.Count == 0 ? default : (Points.Min(p => p.x), Points.Min(p => p.y), Points.Max(p => p.x), Points.Max(p => p.y));

    public bool Contains(double x, double y)
        => x >= _box.MinX && x <= _box.MaxX && y >= _box.MinY && y <= _box.MaxY && ShapeDoc.PointInRing(Points, x, y) && (Holes == null || !Holes.Any(h => ShapeDoc.PointInRing(h, x, y)));
}

/// <summary>Reading and writing the shapes of edits.json: keys, IDs, polygons.</summary>
public static class ShapeDoc
{
    public static string KeyOf(ShapeKind k) => k == ShapeKind.Patch ? "ground_patches" : "zones";
    static string PrefixOf(ShapeKind k) => k == ShapeKind.Patch ? "GP-" : "Z-";
    public static string NounOf(ShapeKind k) => k == ShapeKind.Patch ? "patch" : "zone";

    public static Obj? ShapesOf(Obj root, ShapeKind k) => root.GetValueOrDefault(KeyOf(k)) as Obj;

    public static List<(double x, double y)>? ParsePolygon(object? polygon)
    {
        if (polygon is not List<object?> pts) return null;
        var ring = new List<(double x, double y)>();
        foreach (var p in pts)
        {
            if (p is List<object?> { Count: 2 } xy && ObjectEdits.Number(xy[0]) is double x && ObjectEdits.Number(xy[1]) is double y) ring.Add((x, y));
            else return null;
        }
        if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    public static List<object?> PolygonValue(IEnumerable<(double x, double y)> ring)
        => ring.Select(p => (object?)new List<object?> { ObjectEdits.N3(p.x), ObjectEdits.N3(p.y) }).ToList();

    /// <summary>Next free ID: one above the highest in use (edits/mcp/ids.py next_id).</summary>
    public static string NextShapeId(Obj root, ShapeKind k) => NextId(root, KeyOf(k), PrefixOf(k));

    public static string NextId(Obj root, string key, string prefix)
    {
        var top = 0;
        if (root.GetValueOrDefault(key) is Obj so)
            foreach (var id in so.Keys)
                if (id.StartsWith(prefix) && int.TryParse(id.AsSpan(prefix.Length), out var n)) top = Math.Max(top, n);
        return $"{prefix}{top + 1:0000}";
    }

    /// <summary>Numeric part of GP-NNNN / Z-NNNN (GP-10000 outranks GP-9999): the higher number wins where patches overlap.</summary>
    public static long ShapeNumber(string id) => id.IndexOf('-') is var i and >= 0 && long.TryParse(id.AsSpan(i + 1), out var n) ? n : -1;

    public static bool PointInRing(List<(double x, double y)> ring, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].y > y) != (ring[j].y > y) && x < (ring[j].x - ring[i].x) * (y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x) inside = !inside;
        return inside;
    }

    public static string RingMessage(int code) => code switch
    {
        ObjectEdits.RingTooFew => "needs at least 3 corners",
        ObjectEdits.RingRepeated => "has two identical neighbouring corners",
        ObjectEdits.RingNotSimple => "crosses or touches itself, or has no area",
        _ => "",
    };
}
