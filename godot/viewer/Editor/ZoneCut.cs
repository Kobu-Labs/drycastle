using Drycastle.MapLoader.Core;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor;

/// <summary>A cut of the file (edits.json `zone_cuts`): an area taken out of the rule zone of a kind. Internal: never listed as a zone.</summary>
internal sealed record ZoneCut(string Id, string Kind, List<(double x, double y)> Ring)
{
    /// <summary>A cut is grown by zones.CUT_BUFFER_M (0.5 m) in the export; an area whose outline lies inside a cut, within that, is the cut one (also when a rebuild moved it a little).</summary>
    const double CutTolM = 0.55;

    /// <summary>The cuts of a document.</summary>
    public static List<ZoneCut> ListOf(Obj disk)
    {
        var list = new List<ZoneCut>();
        if (disk.GetValueOrDefault("zone_cuts") is Obj co)
            foreach (var (id, e) in co)
                if (e is Obj r && r.GetValueOrDefault("kind") is string kind && ShapeDoc.ParsePolygon(r.GetValueOrDefault("polygon")) is { Count: >= 3 } ring)
                    list.Add(new ZoneCut(id, kind, ring));
        return list;
    }

    /// <summary>The cut takes this area out (every corner of the area lies inside it or within the tolerance of its edge).</summary>
    public bool Covers(RulePart p)
    {
        foreach (var v in ObjectEdits.Despiked(p.Abs)) // cuts are recorded despiked: the spike tips of the outline stick out of them
        {
            if (ShapeDoc.PointInRing(Ring, v.x, v.y)) continue;
            var near = false;
            for (var i = 0; i < Ring.Count && !near; i++) near = SegmentDistance(Ring[i], Ring[(i + 1) % Ring.Count], v) <= CutTolM;
            if (!near) return false;
        }
        return true;
    }

    static double SegmentDistance((double x, double y) a, (double x, double y) b, (double x, double y) p)
    {
        double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / len2, 0, 1);
        return Math.Sqrt(Math.Pow(p.x - (a.x + t * dx), 2) + Math.Pow(p.y - (a.y + t * dy), 2));
    }
}

/// <summary>One outline of a ground area, absolute: the part of a rule zone before any kind or cut applies.</summary>
internal sealed record RulePart(string Area, string Part, string GroundKind, List<(double x, double y)> Abs, List<List<(double x, double y)>> HoleAbs)
{
    /// <summary>The outlines of the last build's ground areas (parcels.json `ground_areas`), absolute; a multi-part area is `A-0012.1`, `A-0012.2`, ... (as the MCP server names them).</summary>
    public static List<RulePart> Of(Drycastle.MapLoader.MapLoader map)
    {
        var parts = new List<RulePart>();
        static Vector2[] Open(Vector2[] r) => r.Length > 1 && r[0] == r[^1] ? r[..^1] : r;
        List<(double x, double y)>? Clean(Vector2[] ring)
        {
            var abs = new List<(double x, double y)>();
            foreach (var v in ring) { var p = map.ToAbsolute(v); if (abs.Count == 0 || abs[^1] != p) abs.Add(p); }
            if (abs.Count > 1 && abs[0] == abs[^1]) abs.RemoveAt(abs.Count - 1);
            return abs.Count >= 3 ? abs : null;
        }
        foreach (var a in map.GroundAreas)
        {
            var rings = a.Rings.Select(Open).Where(r => r.Length >= 3).ToList();
            var outers = new List<Vector2[]>(); var holes = new List<Vector2[]>();
            foreach (var ring in rings)
                (rings.Count(o => !ReferenceEquals(o, ring) && AreaSurfaces.PointInRing(o, ring[0])) % 2 == 0 ? outers : holes).Add(ring);
            var of = holes.GroupBy(h => outers.Where(o => AreaSurfaces.PointInRing(o, h[0])).OrderBy(o => Mathf.Abs(ParcelInfo.RingArea(o))).FirstOrDefault())
                .Where(g => g.Key != null).ToDictionary(g => g.Key!, g => g.ToList());
            var i = 0;
            foreach (var o in outers)
            {
                i++;
                if (Clean(o) is not { } abs) continue;
                var h = of.GetValueOrDefault(o)?.Select(Clean).Where(r => r != null).Select(r => r!).ToList() ?? new List<List<(double x, double y)>>();
                parts.Add(new RulePart(a.Id, outers.Count == 1 ? a.Id : $"{a.Id}.{i}", a.Kind, abs, h));
            }
        }
        return parts;
    }
}
