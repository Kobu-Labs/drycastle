using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>
/// Physics layers of the world. Solid world (players, vehicles and bullets stop on it) is layer 1; vehicles are layer 2 (as
/// before). Shot-only cover (tree crowns, bushes) is layer 4: bullets, pellets, rockets and sight lines stop on it, while
/// players and cars move through it. Layer 8 is the box of a vehicle that on-foot players are stopped by: a body of its own
/// beside the vehicle's (see <see cref="VehicleSolid"/>). Layer 16 is the player and bot bodies (<see cref="Players"/>): no world ray queries it, so none
/// has to exclude them by hand.
/// </summary>
public static class CollisionLayers
{
    public const uint World = 1;
    public const uint Vehicles = 2;
    public const uint ShotOnly = 4;

    /// <summary>
    /// The solid box of a vehicle for on-foot movement (#217): a static body with the vehicle's hit volume, on the server and on every client.
    /// It is not the vehicle's own body (layer <see cref="Vehicles"/>), because that one has a collision exception with every player so that it
    /// can drive over them, and an exception works both ways: the player's move would pass through it too. Rays never query this layer.
    /// </summary>
    public const uint VehicleSolid = 8;

    /// <summary>
    /// The bodies of players and bots, server and client. Off the world layer so that a ray for the ground, a wall or a shot never meets one, and an
    /// on-foot body does not stop on another (<see cref="CollisionRules"/>: player x other player = Pass). A mover that must stop on players asks for it
    /// by its rule row (<see cref="CollisionRules.BodyMask"/>).
    /// </summary>
    public const uint Players = 16;

    /// <summary>
    /// The Holy Mass demon's body: one static body (a capsule, the player's scaled as the demon is) that the server and every client move to the demon's pose. The demon is no obstacle
    /// for a shot (it is hit by its Hit shape), no ray meets it; the movers whose row Collides with it (<see cref="CollisionRules.BodyMask"/>, <see cref="CollisionRules.SweepMask"/>) stop on it.
    /// </summary>
    public const uint Demon = 32;

    /// <summary>What an on-foot player's movement collides with: the world, the vehicles' solid boxes (#217) and the demon's body. Not while seated, which is the world only.</summary>
    public const uint OnFoot = World | VehicleSolid | Demon;

    /// <summary>Server rays for bullets, rockets, splash and turret sight: world, vehicles and shot-only cover.</summary>
    public const uint Bullets = World | Vehicles | ShotOnly;

    /// <summary>Client rays with no vehicle path (decals, pellet and own-shot tracers): world and shot-only cover.</summary>
    public const uint WorldShots = World | ShotOnly;
}

/// <summary>Whether a solid part is a trunk (wood, solid) or a crown (foliage, shot-only).</summary>
public enum PartRole : byte { Trunk = 0, Crown = 1 }

/// <summary>Box (rectangle in plan, yawed), a four-sided pyramid with a square base, or a four-sided taper (frustum) with square rings.</summary>
public enum PartShape : byte { Box = 0, Pyramid = 1, Frustum = 2 }

/// <summary>
/// One collision solid of a prop, in map metres (x east, y north, z up). Box: centre (X, Y), vertical extent Z0..Z1, half
/// sizes along the yaw axes (HalfU along (cos, sin), HalfV along (-sin, cos)). Pyramid: base at Z0 with ring half-size
/// HalfU (= HalfV), apex at (X, Y, Z1). Frustum: square ring of half-size HalfU (= HalfV) at Z0, of half-size HalfTop at Z1.
/// The same numbers build the Godot shapes, so the tests and the game agree.
/// </summary>
public readonly record struct SolidPart(PartRole Role, PartShape Shape, float X, float Y, float Z0, float Z1, float HalfU, float HalfV, float Yaw, float HalfTop = 0)
{
    /// <summary>Physics layer: trunks stop walking, crowns only shots.</summary>
    public uint Layer => Role == PartRole.Trunk ? CollisionLayers.World : CollisionLayers.ShotOnly;

    /// <summary>The surface group of a hit on this part (for <c>SurfaceClassifier</c>).</summary>
    public string Group => Role == PartRole.Trunk ? "wood" : "foliage";

    /// <summary>Corners (box, 8), the base ring and the apex (pyramid, 5) or the base and top rings (frustum, 8), map metres.</summary>
    public Vector3[] Points()
    {
        if (Shape == PartShape.Pyramid)
        {
            var ring = Ring(HalfU, Z0);
            return [ring[0], ring[1], ring[2], ring[3], new Vector3(X, Y, Z1)];
        }
        if (Shape == PartShape.Frustum)
        {
            var (lo, hi) = (Ring(HalfU, Z0), Ring(HalfTop, Z1));
            return [lo[0], lo[1], lo[2], lo[3], hi[0], hi[1], hi[2], hi[3]];
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

    /// <summary>The square ring of half-size <paramref name="half"/> at height <paramref name="z"/>, in the solid's yawed frame.</summary>
    Vector3[] Ring(float half, float z)
    {
        var (c, s) = (MathF.Cos(Yaw), MathF.Sin(Yaw));
        var ring = new Vector3[4];
        var k = 0;
        foreach (var (dx, dy) in new[] { (-half, -half), (half, -half), (half, half), (-half, half) })
            ring[k++] = new Vector3(X + c * dx - s * dy, Y + s * dx + c * dy, z);
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

        var ring = Ring(HalfU, Z0);
        var inside = new Vector3(X, Y, Z0 + (Z1 - Z0) * 0.1f); // on the axis, above the base: strictly inside
        if (Shape == PartShape.Frustum)
        {
            var top = Ring(HalfTop, Z1);
            var sides = new (Vector3 N, float D)[6];
            for (var k = 0; k < 4; k++)
                sides[k] = Side(ring[k], ring[(k + 1) % 4], top[k], inside); // a side face is planar: a base edge and the top corner above its start
            sides[4] = (new Vector3(0, 0, 1), Z1);
            sides[5] = (new Vector3(0, 0, -1), -Z0);
            return sides;
        }
        var apex = new Vector3(X, Y, Z1);
        var planes = new (Vector3 N, float D)[5];
        for (var k = 0; k < 4; k++)
            planes[k] = Side(ring[k], ring[(k + 1) % 4], apex, inside);
        planes[4] = (new Vector3(0, 0, -1), -Z0);
        return planes;
    }

    /// <summary>The outward plane through p, q (a base edge) and r (a point above it), oriented away from <paramref name="inside"/>.</summary>
    static (Vector3 N, float D) Side(Vector3 p, Vector3 q, Vector3 r, Vector3 inside)
    {
        var n = Vector3.Cross(q - p, r - p);
        var d = Vector3.Dot(n, p);
        return Vector3.Dot(n, inside) > d ? (-n, -d) : (n, d);
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

/// <summary>
/// A tree from trees.json (map metres; Z is the ground height at the trunk foot, Cone the conifer crown). A forest tree
/// belongs to a <c>Stand</c> (pine, birch, mixed, thicket, swamp; a map without stands: mixed) and is an <c>Edge</c> tree
/// within the outer fringe of the forest. Both only pick its look: the size is already in Scale, so the solids ignore them.
/// </summary>
public readonly record struct TreeInstance(float X, float Y, float Z, float Scale, float Rot, bool Cone, int ChunkX, int ChunkY, bool Forest = false, string Stand = "mixed", bool Edge = false);

/// <summary>One crown tier of a forest tree, metres at scale 1 above the ground: <c>Lo</c> is the base (pyramid) or bottom (box), <c>Hi</c> the apex or top.</summary>
public readonly record struct CrownTier(float Lo, float Half, float Hi);

/// <summary>
/// The forest tree numbers of the model block (rules <c>props.trees.forest</c>, metres at scale 1): a trunk frustum
/// (<c>TrunkWidthM</c> at its foot, <c>TrunkTopRatio</c> of that at <c>TrunkHeightM</c>) and the crown tiers of a conifer
/// (stacked pyramids) or a broadleaf (stacked boxes).
/// </summary>
public sealed record ForestModel(float TrunkWidthM, float TrunkHeightM, float TrunkTopRatio, IReadOnlyList<CrownTier> Cone, IReadOnlyList<CrownTier> Box);

/// <summary>The tree model numbers of the rules (trunk width and height, canopy edge, metres at scale 1; Forest only in maps 0.11.0+).</summary>
public readonly record struct TreeModel(float TrunkWidthM, float TrunkHeightM, float CanopyM, ForestModel? Forest = null);

/// <summary>
/// Collision solids of the base map's trees, mirroring the drawn tree (pipeline <c>propmeshes.tree_mesh</c>): a trunk box
/// (solid) and either two stacked boxes (broadleaf) or two stacked pyramids (conifer) as the crown (shot-only). A forest tree
/// (record flag, and the model's forest block) has a tapered trunk and tiers from the forest block instead.
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
            model = new TreeModel(F(m, "trunk_width_m", 0.35f), F(m, "trunk_height_m", 1.8f), F(m, "canopy_m", 2.6f), ParseForest(m));
        if (!root.TryGetProperty("trees", out var trees) || trees.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in trees.EnumerateArray())
        {
            var (cx, cy) = t.TryGetProperty("chunk", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2
                ? (c[0].GetInt32(), c[1].GetInt32()) : (0, 0);
            list.Add(new TreeInstance(
                F(t, "x", 0), F(t, "y", 0), F(t, "z", 0), F(t, "scale", 1), F(t, "rot", 0),
                t.TryGetProperty("shape", out var sh) && sh.GetString() == "cone",
                cx, cy,
                t.TryGetProperty("forest", out var fo) && (fo.ValueKind == JsonValueKind.True || fo.ValueKind == JsonValueKind.Number && fo.GetInt32() != 0),
                t.TryGetProperty("stand", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "mixed",
                t.TryGetProperty("edge", out var ed) && ed.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    /// <summary>The forest block of the model, or null when the map has none (a map before 0.11.0) or it is incomplete: forest trees are then built as plain ones.</summary>
    static ForestModel? ParseForest(JsonElement model)
    {
        if (!model.TryGetProperty("forest", out var f) || f.ValueKind != JsonValueKind.Object) return null;
        var cone = Tiers(f, "cone_tiers");
        var box = Tiers(f, "box_tiers");
        if (cone == null || box == null || !f.TryGetProperty("trunk_width_m", out _) || !f.TryGetProperty("trunk_height_m", out _) || !f.TryGetProperty("trunk_top_ratio", out _)) return null;
        return new ForestModel(F(f, "trunk_width_m", 0), F(f, "trunk_height_m", 0), F(f, "trunk_top_ratio", 1), cone, box);
    }

    static List<CrownTier>? Tiers(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<CrownTier>();
        foreach (var t in arr.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 3) return null;
            list.Add(new CrownTier(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle()));
        }
        return list;
    }

    static float F(JsonElement e, string name, float fallback) =>
        e.TryGetProperty(name, out var v) && v.TryGetSingle(out var f) ? f : fallback;

    /// <summary>The trunk and the crown solids of one tree, built with the same arithmetic as the drawn tree.</summary>
    public static List<SolidPart> Parts(TreeInstance t, TreeModel m)
    {
        var s = t.Scale;
        if (t.Forest && m.Forest is { } f) return ForestParts(t, f);
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

    /// <summary>The solids of a forest tree: a trunk frustum from 0.2 m below the ground, then a pyramid or box per crown tier, as drawn by <c>propmeshes.forest_tree_mesh</c>.</summary>
    static List<SolidPart> ForestParts(TreeInstance t, ForestModel f)
    {
        var s = t.Scale;
        var half = f.TrunkWidthM * s / 2;
        var parts = new List<SolidPart>(1 + (t.Cone ? f.Cone.Count : f.Box.Count))
        {
            new(PartRole.Trunk, PartShape.Frustum, t.X, t.Y, t.Z - 0.2f, t.Z + f.TrunkHeightM * s, half, half, t.Rot, half * f.TrunkTopRatio),
        };
        foreach (var tier in t.Cone ? f.Cone : f.Box)
            parts.Add(new(PartRole.Crown, t.Cone ? PartShape.Pyramid : PartShape.Box, t.X, t.Y, t.Z + tier.Lo * s, t.Z + tier.Hi * s, tier.Half * s, tier.Half * s, t.Rot));
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
    /// <summary>Kind 0 is a box (A = width, B = height, C = depth); kind 1 a pyramid (A = base half-size, B = height, C unused); kind 2 a frustum (A = base half-size, B = height, C = top half-size). Values in millimetres.</summary>
    public readonly record struct Key(byte Kind, long A, long B, long C);

    public static Key Of(SolidPart p)
    {
        var height = Quantise(p.Z1 - p.Z0);
        return p.Shape switch
        {
            PartShape.Pyramid => new Key(1, Quantise(p.HalfU), height, 0),
            PartShape.Frustum => new Key(2, Quantise(p.HalfU), height, Quantise(p.HalfTop)),
            _ => new Key(0, Quantise(2 * p.HalfU), height, Quantise(2 * p.HalfV)),
        };
    }

    /// <summary>Metres to whole millimetres, rounded to nearest.</summary>
    public static long Quantise(float metres) => (long)MathF.Round(metres * 1000f);
}

/// <summary>
/// Collision solids of placed objects (objects.json), built from the asset's category and size (metres at scale 1, origin
/// at the base centre), or from the boxes the asset lists itself (<see cref="ParseSolids"/>), which win. Parts are in the
/// object's local frame (yaw zero; X and Y zero for the category rule); the node carries position and yaw.
/// </summary>
public static class ObjectColliders
{
    /// <summary>Assets that stop nothing whatever their category: the satellite dish (category "other" so the editor palette shows it, but a fitting on a wall).</summary>
    public static bool IsPassThrough(string asset) => asset == DishSource.AssetId;

    /// <summary>An object stops nothing when its asset is a pass-through one or it was placed with <c>solid: false</c> (dressing over a climbable prim).</summary>
    public static bool IsPassThrough(string asset, bool solid) => !solid || IsPassThrough(asset);

    /// <summary>
    /// Flowers and house connectors have none (walk and shoot through). Bushes stop shots only. Trees have a trunk (solid, lower third) and a crown
    /// box (shot-only). A pole (utility pole) is its column alone: a thin solid box at the origin, as high as the pole, so the
    /// lamp arm, the crossarm and a cabinet beside it (which make the size wide) stop nothing. Every other category (car, slab,
    /// pot, wall, fence, radio tower, other) is one solid box.
    /// </summary>
    /// <summary>Half the side of a utility pole's solid column, metres.</summary>
    public const float PoleHalf = 0.2f;

    public static List<SolidPart> ForCategory(string category, float sx, float sy, float sz)
    {
        var hx = sx / 2;
        var hz = sz / 2;
        switch (category)
        {
            case "flower":
            case "connector": // a house connector: a small fitting high on a wall or roof
                return [];
            case "bush":
                return [new(PartRole.Crown, PartShape.Box, 0, 0, 0, sy, hx, hz, 0)];
            case "tree":
                return
                [
                    new(PartRole.Trunk, PartShape.Box, 0, 0, 0, sy * 0.35f, 0.15f, 0.15f, 0),
                    new(PartRole.Crown, PartShape.Box, 0, 0, sy * 0.35f, sy, hx, hz, 0),
                ];
            case "pole":
                return [new(PartRole.Trunk, PartShape.Box, 0, 0, 0, sy, PoleHalf, PoleHalf, 0)];
            default:
                return [new(PartRole.Trunk, PartShape.Box, 0, 0, 0, sy, hx, hz, 0)];
        }
    }

    /// <summary>
    /// The solid boxes an asset lists itself: the "solids" of its manifest entry, each {"min": [x, y, z], "max": [x, y, z],
    /// "surface": "wall"} in the asset's own frame (pipeline axes, z up, metres at scale 1). An asset that has them collides
    /// with these and not with the box of its category: a flood wall piece is a ledge to walk on beside a wall, a gate is
    /// two piers with a way through. Empty when the entry has none; an entry that is not a proper box is skipped.
    /// </summary>
    public static List<AssetSolid> ParseSolids(JsonElement asset)
    {
        var solids = new List<AssetSolid>();
        if (!asset.TryGetProperty("solids", out var arr) || arr.ValueKind != JsonValueKind.Array) return solids;
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || !Corner(s, "min", out var lo) || !Corner(s, "max", out var hi)) continue;
            if (hi.X <= lo.X || hi.Y <= lo.Y || hi.Z <= lo.Z) continue;
            var surface = s.TryGetProperty("surface", out var su) && su.ValueKind == JsonValueKind.String ? su.GetString()! : "wall";
            solids.Add(new AssetSolid(
                new SolidPart(PartRole.Trunk, PartShape.Box, (lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, lo.Z, hi.Z, (hi.X - lo.X) / 2, (hi.Y - lo.Y) / 2, 0),
                surface));
        }
        return solids;
    }

    static bool Corner(JsonElement solid, string name, out Vector3 corner)
    {
        corner = default;
        if (!solid.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) return false;
        foreach (var v in p.EnumerateArray())
            if (v.ValueKind != JsonValueKind.Number) return false;
        corner = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
        return true;
    }
}

/// <summary>A solid box of an asset (<see cref="ObjectColliders.ParseSolids"/>) and the surface group of a hit on it ("wall", "metal").</summary>
public readonly record struct AssetSolid(SolidPart Part, string Surface);
