using System.Numerics;
using System.Text.Json;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Physics layers of the world. Solid world (players, vehicles and bullets stop on it) is layer 1; vehicles are layer 2 (as
/// before). Shot-only cover (tree crowns, bushes) is layer 4: bullets, pellets, rockets and sight lines stop on it, while
/// players and cars move through it.
/// </summary>
public static class CollisionLayers
{
    public const uint World = 1;
    public const uint Vehicles = 2;
    public const uint ShotOnly = 4;

    /// <summary>Server rays for bullets, rockets, splash and turret sight: world, vehicles and shot-only cover.</summary>
    public const uint Bullets = World | Vehicles | ShotOnly;

    /// <summary>Client rays with no vehicle path (decals, pellet and own-shot tracers): world and shot-only cover.</summary>
    public const uint WorldShots = World | ShotOnly;
}

/// <summary>Whether a solid part is a trunk (wood, solid) or a crown (foliage, shot-only).</summary>
public enum PartRole : byte { Trunk = 0, Crown = 1 }

/// <summary>Box (rectangle in plan, yawed) or a four-sided pyramid with a square base.</summary>
public enum PartShape : byte { Box = 0, Pyramid = 1 }

/// <summary>
/// One collision solid of a prop, in map metres (x east, y north, z up). Box: centre (X, Y), vertical extent Z0..Z1, half
/// sizes along the yaw axes (HalfU along (cos, sin), HalfV along (-sin, cos)). Pyramid: base at Z0 with ring half-size
/// HalfU (= HalfV), apex at (X, Y, Z1). The same numbers build the Godot shapes, so the tests and the game agree.
/// </summary>
public readonly record struct SolidPart(PartRole Role, PartShape Shape, float X, float Y, float Z0, float Z1, float HalfU, float HalfV, float Yaw)
{
    /// <summary>Physics layer: trunks stop walking, crowns only shots.</summary>
    public uint Layer => Role == PartRole.Trunk ? CollisionLayers.World : CollisionLayers.ShotOnly;

    /// <summary>The surface group of a hit on this part (for <c>SurfaceClassifier</c>).</summary>
    public string Group => Role == PartRole.Trunk ? "wood" : "foliage";

    /// <summary>Corners (box, 8) or the base ring and the apex (pyramid, 5), map metres.</summary>
    public Vector3[] Points()
    {
        if (Shape == PartShape.Pyramid)
        {
            var ring = Ring();
            return [ring[0], ring[1], ring[2], ring[3], new Vector3(X, Y, Z1)];
        }
        var (ux, uy) = (MathF.Cos(Yaw), MathF.Sin(Yaw));
        var (vx, vy) = (-uy, ux);
        var pts = new Vector3[8];
        // the same axes as Planes(): u = (cos, sin), v = (-sin, cos)
        var k = 0;
        foreach (var z in new[] { Z0, Z1 })
            foreach (var (su, sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                pts[k++] = new Vector3(X + ux * HalfU * su + vx * HalfV * sv, Y + uy * HalfU * su + vy * HalfV * sv, z);
        return pts;
    }

    Vector3[] Ring()
    {
        var (c, s) = (MathF.Cos(Yaw), MathF.Sin(Yaw));
        var ring = new Vector3[4];
        var k = 0;
        foreach (var (dx, dy) in new[] { (-HalfU, -HalfU), (HalfU, -HalfU), (HalfU, HalfU), (-HalfU, HalfU) })
            ring[k++] = new Vector3(X + c * dx - s * dy, Y + s * dx + c * dy, Z0);
        return ring;
    }

    /// <summary>Outward planes (n, d) of the convex solid: a point p is inside when n·p ≤ d for all of them.</summary>
    public (Vector3 N, float D)[] Planes()
    {
        if (Shape == PartShape.Box)
        {
            var (ux, uy) = (MathF.Cos(Yaw), MathF.Sin(Yaw));
            var (vx, vy) = (-uy, ux);
            var cu = X * ux + Y * uy;
            var cv = X * vx + Y * vy;
            return
            [
                (new Vector3(ux, uy, 0), cu + HalfU),
                (new Vector3(-ux, -uy, 0), -cu + HalfU),
                (new Vector3(vx, vy, 0), cv + HalfV),
                (new Vector3(-vx, -vy, 0), -cv + HalfV),
                (new Vector3(0, 0, 1), Z1),
                (new Vector3(0, 0, -1), -Z0),
            ];
        }

        var ring = Ring();
        var apex = new Vector3(X, Y, Z1);
        var inside = new Vector3(X, Y, Z0 + (Z1 - Z0) * 0.1f); // on the axis, above the base: strictly inside
        var planes = new (Vector3 N, float D)[5];
        for (var k = 0; k < 4; k++)
        {
            var p = ring[k];
            var q = ring[(k + 1) % 4];
            var n = Vector3.Cross(q - p, apex - p);
            var d = Vector3.Dot(n, p);
            if (Vector3.Dot(n, inside) > d) { n = -n; d = -d; }
            planes[k] = (n, d);
        }
        planes[4] = (new Vector3(0, 0, -1), -Z0);
        return planes;
    }

    /// <summary>True when p is inside the solid, or within <paramref name="tolerance"/> metres outside it.</summary>
    public bool Contains(Vector3 p, float tolerance = 0f)
    {
        foreach (var (n, d) in Planes())
            if (Vector3.Dot(n, p) - d > tolerance * n.Length()) return false;
        return true;
    }

    /// <summary>Distance along <paramref name="dir"/> (in units of its length) to the first point of the solid within <paramref name="maxT"/>; 0 when the origin is inside; null when missed.</summary>
    public float? Ray(Vector3 origin, Vector3 dir, float maxT)
    {
        var tMin = 0f;
        var tMax = maxT;
        foreach (var (n, d) in Planes())
        {
            var denom = Vector3.Dot(n, dir);
            var dist = d - Vector3.Dot(n, origin);
            if (MathF.Abs(denom) < 1e-9f)
            {
                if (dist < 0) return null;
                continue;
            }
            var t = dist / denom;
            if (denom < 0) tMin = MathF.Max(tMin, t);   // entering this half-space
            else tMax = MathF.Min(tMax, t);             // leaving it
            if (tMin > tMax) return null;
        }
        return tMin;
    }
}

/// <summary>A tree from trees.json (map metres; Z is the ground height at the trunk foot, Cone the conifer crown).</summary>
public readonly record struct TreeInstance(float X, float Y, float Z, float Scale, float Rot, bool Cone, int ChunkX, int ChunkY);

/// <summary>The tree model numbers of the rules (trunk width and height, canopy edge, metres at scale 1).</summary>
public readonly record struct TreeModel(float TrunkWidthM, float TrunkHeightM, float CanopyM);

/// <summary>
/// Collision solids of the base map's trees, mirroring the drawn tree (pipeline <c>propmeshes.tree_mesh</c>): a trunk box
/// (solid) and either two stacked boxes (broadleaf) or two stacked pyramids (conifer) as the crown (shot-only).
/// </summary>
public static class TreeColliders
{
    /// <summary>Parses trees.json (format 1). Empty when the file has no trees.</summary>
    public static List<TreeInstance> Parse(string json, out TreeModel model)
    {
        model = new TreeModel(0.35f, 1.8f, 2.6f);
        var list = new List<TreeInstance>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("model", out var m))
            model = new TreeModel(F(m, "trunk_width_m", 0.35f), F(m, "trunk_height_m", 1.8f), F(m, "canopy_m", 2.6f));
        if (!root.TryGetProperty("trees", out var trees) || trees.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in trees.EnumerateArray())
        {
            var (cx, cy) = t.TryGetProperty("chunk", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2
                ? (c[0].GetInt32(), c[1].GetInt32()) : (0, 0);
            list.Add(new TreeInstance(
                F(t, "x", 0), F(t, "y", 0), F(t, "z", 0), F(t, "scale", 1), F(t, "rot", 0),
                t.TryGetProperty("shape", out var sh) && sh.GetString() == "cone",
                cx, cy));
        }
        return list;
    }

    static float F(JsonElement e, string name, float fallback) =>
        e.TryGetProperty(name, out var v) && v.TryGetSingle(out var f) ? f : fallback;

    /// <summary>The trunk and the crown solids of one tree, built with the same arithmetic as the drawn tree.</summary>
    public static List<SolidPart> Parts(TreeInstance t, TreeModel m)
    {
        var s = t.Scale;
        var trunkW = m.TrunkWidthM * s;
        var trunkH = m.TrunkHeightM * s;
        var canopy = m.CanopyM * s;
        var parts = new List<SolidPart>(3)
        {
            // the trunk box starts 0.2 m below the ground, as drawn
            new(PartRole.Trunk, PartShape.Box, t.X, t.Y, t.Z - 0.2f, t.Z + trunkH, trunkW / 2, trunkW / 2, t.Rot),
        };
        if (t.Cone)
        {
            parts.Add(new(PartRole.Crown, PartShape.Pyramid, t.X, t.Y, t.Z + trunkH * 0.5f, t.Z + trunkH + canopy * 0.7f, canopy * 0.55f, canopy * 0.55f, t.Rot));
            parts.Add(new(PartRole.Crown, PartShape.Pyramid, t.X, t.Y, t.Z + trunkH + canopy * 0.35f, t.Z + trunkH + canopy * 1.5f, canopy * 0.38f, canopy * 0.38f, t.Rot));
        }
        else
        {
            var lo = t.Z + trunkH - 0.3f * s;
            parts.Add(new(PartRole.Crown, PartShape.Box, t.X, t.Y, lo, lo + canopy, canopy / 2, canopy / 2, t.Rot));
            parts.Add(new(PartRole.Crown, PartShape.Box, t.X, t.Y, lo + canopy, lo + canopy * 1.6f, canopy * 0.3f, canopy * 0.3f, t.Rot));
        }
        return parts;
    }

    /// <summary>Surface group ("wood" on a trunk, "foliage" on a crown) of a map-metre point on a tree, or null when no tree is within <paramref name="tolerance"/>.</summary>
    public static string? GroupAt(IReadOnlyList<TreeInstance> trees, TreeModel m, Vector3 p, float tolerance = 0.05f)
    {
        foreach (var t in trees)
        {
            var reach = Reach(m, t);
            if (MathF.Abs(p.X - t.X) > reach || MathF.Abs(p.Y - t.Y) > reach) continue;
            foreach (var part in Parts(t, m))
                if (part.Contains(p, tolerance)) return part.Group;
        }
        return null;
    }

    /// <summary>No part of this tree lies further out than this, metres from its trunk.</summary>
    static float Reach(TreeModel m, TreeInstance t) => m.CanopyM * t.Scale * 1.6f + 0.5f;

    /// <summary>
    /// The trees bucketed into a uniform grid (ticket perf T3-003), so a surface lookup reads the few trees near a point
    /// instead of all 12,000. Same answer as <see cref="GroupAt"/>, including which tree wins where two overlap (the earlier one).
    /// Built once at load.
    /// </summary>
    public sealed class Index
    {
        /// <summary>Grid cell edge, metres. Larger than a tree's reach, so a tree touches at most a few cells.</summary>
        public const float CellM = 32f;

        readonly IReadOnlyList<TreeInstance> _trees;
        readonly TreeModel _model;
        readonly Dictionary<(int, int), List<int>> _cells = new();

        public Index(IReadOnlyList<TreeInstance> trees, TreeModel m)
        {
            _trees = trees;
            _model = m;
            for (var i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                var r = Reach(m, t);
                for (var cx = Cell(t.X - r); cx <= Cell(t.X + r); cx++)
                    for (var cy = Cell(t.Y - r); cy <= Cell(t.Y + r); cy++)
                    {
                        if (!_cells.TryGetValue((cx, cy), out var list)) _cells[(cx, cy)] = list = new List<int>();
                        list.Add(i);
                    }
            }
        }

        static int Cell(float v) => (int)MathF.Floor(v / CellM);

        /// <summary><see cref="TreeColliders.GroupAt"/>, looking only at the trees registered in the point's cell.</summary>
        public string? GroupAt(Vector3 p, float tolerance = 0.05f)
        {
            if (!_cells.TryGetValue((Cell(p.X), Cell(p.Y)), out var candidates)) return null;
            string? group = null;
            var best = int.MaxValue;
            foreach (var i in candidates)
            {
                if (i >= best) continue; // the earliest tree in the list wins, as in GroupAt
                var t = _trees[i];
                var reach = Reach(_model, t);
                if (MathF.Abs(p.X - t.X) > reach || MathF.Abs(p.Y - t.Y) > reach) continue;
                foreach (var part in Parts(t, _model))
                    if (part.Contains(p, tolerance)) { group = part.Group; best = i; break; }
            }
            return group;
        }
    }
}

/// <summary>
/// Which solids can share one Godot shape resource: the shape's size (box) or its base half-size and height (pyramid),
/// quantised to 1 mm. The position and yaw are the solid's own transform, so they do not take part. Trees are scaled by a
/// few hundred distinct values, so the trunks and crowns of the map share a few thousand shapes instead of one each.
/// </summary>
public static class SolidShapeKeys
{
    /// <summary>Kind 0 is a box (A = width, B = height, C = depth); kind 1 a pyramid (A = base half-size, B = height, C unused). Values in millimetres.</summary>
    public readonly record struct Key(byte Kind, long A, long B, long C);

    public static Key Of(SolidPart p)
    {
        var height = Quantise(p.Z1 - p.Z0);
        return p.Shape == PartShape.Pyramid
            ? new Key(1, Quantise(p.HalfU), height, 0)
            : new Key(0, Quantise(2 * p.HalfU), height, Quantise(2 * p.HalfV));
    }

    /// <summary>Metres to whole millimetres, rounded to nearest.</summary>
    public static long Quantise(float metres) => (long)MathF.Round(metres * 1000f);
}

/// <summary>
/// Collision solids of placed objects (objects.json), built from the asset's category and size (metres at scale 1, origin
/// at the base centre). Parts are in the object's local frame (X and Y zero, yaw zero); the node carries position and yaw.
/// </summary>
public static class ObjectColliders
{
    /// <summary>
    /// Flowers have none (walk and shoot through). Bushes stop shots only. Trees have a trunk (solid, lower third) and a crown
    /// box (shot-only). Every other category (car, slab, pot, wall, fence, radio tower, other) is one solid box.
    /// </summary>
    public static List<SolidPart> ForCategory(string category, float sx, float sy, float sz)
    {
        var hx = sx / 2;
        var hz = sz / 2;
        switch (category)
        {
            case "flower":
                return [];
            case "bush":
                return [new(PartRole.Crown, PartShape.Box, 0, 0, 0, sy, hx, hz, 0)];
            case "tree":
                return
                [
                    new(PartRole.Trunk, PartShape.Box, 0, 0, 0, sy * 0.35f, 0.15f, 0.15f, 0),
                    new(PartRole.Crown, PartShape.Box, 0, 0, sy * 0.35f, sy, hx, hz, 0),
                ];
            default:
                return [new(PartRole.Trunk, PartShape.Box, 0, 0, 0, sy, hx, hz, 0)];
        }
    }
}
