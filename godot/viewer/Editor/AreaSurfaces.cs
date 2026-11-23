using Drycastle.MapLoader;
using Godot;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>How many triangles a mesh may take (the ground map and the Zones overlay each have one); past the maximum the rest is left out.</summary>
public sealed class TriangleBudget
{
    public int Count;
    public bool Truncated;
    public int Max = AreaSurfaces.MaxTriangles;
}

/// <summary>The areas of one ground type (or zone kind): outer rings with their holes, for the point test and the mesh, and the materials of the mesh built from them.</summary>
public sealed class AreaSet
{
    public string Name = "";
    public int Areas, Drawn;
    public float AreaM2;
    public readonly List<Vector2[]> Outers = new();                 // for the point test: one entry per outer ring
    public readonly List<List<Vector2[]>> Holes = new();            // the holes of each outer
    public StandardMaterial3D? Fill, Edge;
}

/// <summary>Area meshes draped on the terrain: what the ground map and the Zones overlay draw (fill, and optionally an outline, per type).</summary>
public static class AreaSurfaces
{
    public const float Lift = 1.0f;         // above the terrain; the 12 m triangles do not follow every bump, so it floats a little
    public const int MaxTriangles = 260000;
    public const float StripeTileM = 24f;   // one texture tile on the ground: bands 4 m wide, 8.5 m apart (period)
    const float MaxEdge = 12f;
    const float EdgeWidth = 1.2f;

    /// <summary>Sort the rings of one area into outers and holes by how many other rings enclose them (even depth: outer; odd: a hole of the smallest outer around it).</summary>
    public static void Split(List<Vector2[]> rings, AreaSet t, out float area)
    {
        area = 0f;
        var outers = new List<(Vector2[] Ring, float Area)>();
        var holes = new List<Vector2[]>();
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            var depth = rings.Count(o => !ReferenceEquals(o, ring) && o.Length >= 3 && PointInRing(o, ring[0]));
            if (depth % 2 == 0) outers.Add((ring, Mathf.Abs(ParcelInfo.RingArea(ring)))); else holes.Add(ring);
        }
        foreach (var (ring, a) in outers) { t.Outers.Add(ring); t.Holes.Add(new List<Vector2[]>()); area += a; }
        foreach (var h in holes)
        {
            var best = -1; var bestArea = float.MaxValue;
            for (var i = t.Outers.Count - outers.Count; i < t.Outers.Count; i++)
            {
                var a = Mathf.Abs(ParcelInfo.RingArea(t.Outers[i]));
                if (a < bestArea && PointInRing(t.Outers[i], h[0])) { best = i; bestArea = a; }
            }
            if (best >= 0) { t.Holes[best].Add(h); area -= Mathf.Abs(ParcelInfo.RingArea(h)); }
        }
    }

    public static bool PointInRing(Vector2[] ring, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X) inside = !inside;
        return inside;
    }

    public static bool Hit(AreaSet t, Vector2 p)
    {
        for (var i = 0; i < t.Outers.Count; i++)
            if (PointInRing(t.Outers[i], p) && !t.Holes[i].Any(h => PointInRing(h, p))) return true;
        return false;
    }

    public static StandardMaterial3D Material(int priority) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        RenderPriority = priority,
    };

    /// <summary>One fill surface and one edge surface for the areas of <paramref name="t"/> (its materials are stored on it). Polygons with holes are cut open along a thin slit (Clipper) so they triangulate.</summary>
    public static void Build(Drycastle.MapLoader.MapLoader map, ArrayMesh mesh, AreaSet t, Color colour, float fillAlpha, int priority, TriangleBudget budget, Texture2D? stripes = null, bool edges = true)
    {
        var b = new Builder(map, budget);
        for (var i = 0; i < t.Outers.Count; i++)
        {
            foreach (var piece in b.Cut(t.Outers[i], t.Holes[i]))
            {
                var tri = Geometry2D.TriangulatePolygon(piece);
                for (var k = 0; k + 2 < tri.Length; k += 3) b.Triangle(piece[tri[k]], piece[tri[k + 1]], piece[tri[k + 2]], 0);
            }
            if (!edges) continue;
            b.Ribbon(t.Outers[i]);
            foreach (var h in t.Holes[i]) b.Ribbon(h);
        }

        if (b.Fill.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = b.Fill.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            t.Fill = Material(priority);
            t.Fill.AlbedoColor = new Color(colour, fillAlpha);
            if (stripes != null) // projected on the ground from above, in world metres: the stripes run on across zones and meet at the borders
            {
                t.Fill.AlbedoTexture = stripes;
                t.Fill.Uv1Triplanar = true;
                t.Fill.Uv1WorldTriplanar = true;
                t.Fill.Uv1Scale = Vector3.One / StripeTileM;
            }
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, t.Fill);
        }
        if (b.Edge.Count > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = b.Edge.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = b.EdgeIdx.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            t.Edge = Material(priority + 1);
            t.Edge.AlbedoColor = new Color(colour, 0.95f);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, t.Edge);
        }
    }

    /// <summary>The vertices of one mesh while it is built: triangles (cut to at most 12 m edges, within the budget) and outline ribbons.</summary>
    sealed class Builder
    {
        readonly Drycastle.MapLoader.MapLoader _map;
        readonly TriangleBudget _budget;
        public readonly List<Vector3> Fill = new();
        public readonly List<Vector3> Edge = new();
        public readonly List<int> EdgeIdx = new();

        public Builder(Drycastle.MapLoader.MapLoader map, TriangleBudget budget)
        {
            _map = map;
            _budget = budget;
        }

        Vector3 Up(Vector2 p, float extra = 0f) => new(p.X, _map.GroundHeightOrMean(p.X, p.Y) + Lift + extra, p.Y);

        /// <summary>The outer ring cut into pieces without holes: each hole gets a slit from its right-most corner to the east, merged with it, cut from the pieces.</summary>
        public List<Vector2[]> Cut(Vector2[] outer, List<Vector2[]> holes)
        {
            var pieces = new List<Vector2[]> { outer };
            foreach (var hole in holes)
            {
                var far = hole[0]; foreach (var v in hole) if (v.X > far.X) far = v;
                var reach = 1e5f;
                var slit = new[] { far + new Vector2(-0.05f, -0.05f), far + new Vector2(reach, -0.05f), far + new Vector2(reach, 0.05f), far + new Vector2(-0.05f, 0.05f) };
                var merged = Geometry2D.MergePolygons(hole, slit);
                var cutter = merged.Count > 0 ? merged[0] : hole;
                var next = new List<Vector2[]>();
                foreach (var p in pieces)
                    foreach (var q in Geometry2D.ClipPolygons(p, cutter))
                        if (!Geometry2D.IsPolygonClockwise(q)) next.Add(q); // a clockwise piece would be a hole: the slit reaches the outside, so none is left
                pieces = next.Count > 0 ? next : pieces;
            }
            return pieces;
        }

        public void Triangle(Vector2 a, Vector2 b, Vector2 c, int depth)
        {
            var ab = a.DistanceTo(b); var bc = b.DistanceTo(c); var ca = c.DistanceTo(a);
            var longest = Mathf.Max(ab, Mathf.Max(bc, ca));
            if (longest > MaxEdge && depth < 14)
            {
                if (_budget.Count >= _budget.Max) { _budget.Truncated = true; return; }
                if (ab >= bc && ab >= ca) { var m = (a + b) / 2; Triangle(a, m, c, depth + 1); Triangle(m, b, c, depth + 1); }
                else if (bc >= ca) { var m = (b + c) / 2; Triangle(a, b, m, depth + 1); Triangle(a, m, c, depth + 1); }
                else { var m = (c + a) / 2; Triangle(a, b, m, depth + 1); Triangle(m, b, c, depth + 1); }
                return;
            }
            if (_budget.Count >= _budget.Max) { _budget.Truncated = true; return; }
            Fill.Add(Up(a)); Fill.Add(Up(b)); Fill.Add(Up(c));
            _budget.Count++;
        }

        public void Ribbon(Vector2[] ring)
        {
            for (var i = 0; i < ring.Length; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                var dir = (b - a).Normalized();
                if (!float.IsFinite(dir.X)) continue;
                var side = new Vector2(-dir.Y, dir.X) * (EdgeWidth / 2f);
                var n = Mathf.Max(1, (int)Mathf.Ceil(a.DistanceTo(b) / 8f));
                for (var k = 0; k < n; k++)
                {
                    var p0 = a.Lerp(b, (float)k / n); var p1 = a.Lerp(b, (float)(k + 1) / n);
                    var at = Edge.Count;
                    Edge.Add(Up(p0 + side, 0.02f)); Edge.Add(Up(p0 - side, 0.02f)); Edge.Add(Up(p1 + side, 0.02f)); Edge.Add(Up(p1 - side, 0.02f));
                    EdgeIdx.AddRange(new[] { at, at + 1, at + 2, at + 1, at + 3, at + 2 });
                }
            }
        }
    }
}
