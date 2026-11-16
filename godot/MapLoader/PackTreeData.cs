using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The pack trees (#287): detailed models of the private nature pack
// near the player in place of the baked trees. Config: shooter/data/pack_trees.json. Streaming and drawing: MapLoader.PackTrees.cs.

/// <summary>A model that may stand in for a tree shape, its share among the models of that shape, and whether it is a bulk model (see the file's _note).</summary>
public sealed class PackTreeEntry
{
    public string Name { get; set; } = "";
    public float Weight { get; set; } = 1f;
    public bool Bulk { get; set; }
}

/// <summary>pack_trees.json: the near radius, the tiles and the models per tree shape (see the file's _note).</summary>
public sealed class PackTreeConfig : IVersioned
{
    public int Version { get; set; }
    public float NearM { get; set; } = 70f;
    public float FadeM { get; set; } = 14f;
    public float TileM { get; set; } = 48f;
    public float AheadM { get; set; } = 16f;
    public int MaxInFlight { get; set; } = 3;
    public double BudgetMs { get; set; } = 2.0;
    public float ScaleMin { get; set; } = 0.5f;
    public float ScaleMax { get; set; } = 4f;
    public float BulkShare { get; set; } = 0.85f;
    public bool Shadows { get; set; } = true;
    public Dictionary<string, List<PackTreeEntry>> Shapes { get; set; } = new();

    public static PackTreeConfig Parse(string json)
    {
        var c = DataFile.Parse<PackTreeConfig>(json, "pack_trees.json");
        if (!(c.NearM > 0)) throw new DataFileException("pack_trees.json: near_m must be above 0");
        if (c.FadeM < 0) throw new DataFileException("pack_trees.json: fade_m must not be negative");
        if (!(c.TileM > 0)) throw new DataFileException("pack_trees.json: tile_m must be above 0");
        if (c.AheadM < 0) throw new DataFileException("pack_trees.json: ahead_m must not be negative");
        if (c.MaxInFlight < 1) throw new DataFileException("pack_trees.json: max_in_flight must be at least 1");
        if (!(c.ScaleMin > 0) || c.ScaleMax < c.ScaleMin) throw new DataFileException("pack_trees.json: scale_min must be above 0 and scale_max at least scale_min");
        if (c.BulkShare < 0 || c.BulkShare > 1) throw new DataFileException("pack_trees.json: bulk_share must be within 0 and 1");
        foreach (var (shape, list) in c.Shapes)
            if (list.Any(e => e.Name.Length == 0 || e.Weight < 0)) throw new DataFileException($"pack_trees.json: shape '{shape}' has an entry without a name or with a negative weight");
        return c;
    }

    /// <summary>The entries of a tree shape (cone or box); empty for an unknown shape.</summary>
    public IReadOnlyList<PackTreeEntry> For(bool cone) => Shapes.TryGetValue(cone ? "cone" : "box", out var l) ? l : new List<PackTreeEntry>();
}

public static class PackTreeSettings
{
    /// <summary>Environment variable: 0, off, false or no = no pack trees; unset or anything else = on (when the nature pack is installed).</summary>
    public const string EnvVar = "DRYCASTLE_PACK_TREES";

    public static bool ParseEnabled(string? value)
        => string.IsNullOrWhiteSpace(value) || !(value.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no");

    /// <summary>Whether trees.json says the props meshes carry the base point of each tree in UV2 (<c>model.base_uv2</c>, see <see cref="PackTreeMath.NoBase"/>). False for a file without the flag.</summary>
    public static bool ReadBaseUv2(string treesJson)
    {
        if (string.IsNullOrWhiteSpace(treesJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(treesJson);
            return doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("base_uv2", out var b) && b.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>A model that may stand in for a tree: its id (index into the loader's models), native height, weight within its shape and bulk flag.</summary>
public readonly record struct PackCandidate(int ModelId, float HeightM, float Weight, bool Bulk);

/// <summary>The arithmetic of the pack trees: which model, how big, which tile, where the cut is.</summary>
public static class PackTreeMath
{
    const int ModelSalt = 3001;

    /// <summary>UV2 of a vertex that belongs to no tree (a fence): far outside any map. The shaders (tree_cut.gdshaderinc) skip the cut for it.</summary>
    public const float NoBase = 1.0e6f;

    /// <summary>A number in [0, 1) from a tree's position (rounded to centimetres): the same tree gets the same number on every client and every run.</summary>
    public static double Roll(float x, float y)
        => MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), ModelSalt);

    /// <summary>The index of the entry that <paramref name="roll"/> (in [0, 1)) falls on when the weights are laid end to end; -1 when no weight is above 0.</summary>
    public static int Pick(IReadOnlyList<float> weights, double roll)
    {
        double total = 0;
        foreach (var w in weights) total += Math.Max(w, 0f);
        if (total <= 0) return -1;
        var at = roll * total;
        var last = -1;
        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0) continue;
            last = i;
            at -= weights[i];
            if (at < 0) return i;
        }
        return last;
    }

    /// <summary>The height of the code-built tree above its foot (the top of its highest collision solid, <see cref="TreeColliders.Parts"/>).</summary>
    public static float CodeHeight(TreeInstance t, TreeModel m)
    {
        var top = float.MinValue;
        foreach (var p in TreeColliders.Parts(t, m)) top = MathF.Max(top, p.Z1);
        return top - t.Z;
    }

    /// <summary>The uniform scale that makes a model of <paramref name="modelHeightM"/> as tall as the code-built tree (1 for a model without height).</summary>
    public static float ScaleFor(TreeInstance t, TreeModel m, float modelHeightM)
        => modelHeightM > 1e-4f ? CodeHeight(t, m) / modelHeightM : 1f;

    /// <summary>
    /// The candidate for a tree of <paramref name="codeHeightM"/> (index into <paramref name="list"/>; -1 when nothing has a weight). A roll under
    /// <paramref name="bulkShare"/> picks from the bulk models, the rest from the occasional ones (all of one kind when the list has only that kind);
    /// within the kind the models whose scale (code height over native height) lies in [scaleMin, scaleMax] share the roll by weight, and when none does the
    /// one nearest to the span (in ratio) stands in. The same inputs give the same model on every client.
    /// </summary>
    public static int Choose(IReadOnlyList<PackCandidate> list, float codeHeightM, double roll, float scaleMin, float scaleMax, float bulkShare)
    {
        bool hasBulk = false, hasOcc = false;
        foreach (var c in list)
        {
            if (c.Weight <= 0) continue;
            if (c.Bulk) hasBulk = true; else hasOcc = true;
        }
        if (!hasBulk && !hasOcc) return -1;
        double share = hasBulk && hasOcc ? Math.Clamp(bulkShare, 0f, 1f) : hasBulk ? 1.0 : 0.0;
        var bulk = roll < share;
        var r = bulk ? (share > 0 ? roll / share : 0.0) : (share < 1 ? (roll - share) / (1 - share) : 0.0);

        var fit = new List<int>();
        var nearest = -1;
        var nearestOff = double.MaxValue;
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c.Weight <= 0 || c.Bulk != bulk) continue;
            var scale = c.HeightM > 1e-4f ? codeHeightM / c.HeightM : 1f;
            if (scale >= scaleMin && scale <= scaleMax) { fit.Add(i); continue; }
            var off = scale < scaleMin ? Math.Log(scaleMin / Math.Max(scale, 1e-6f)) : Math.Log(scale / scaleMax);
            if (off < nearestOff) { nearestOff = off; nearest = i; }
        }
        if (fit.Count == 0) return nearest;
        return fit[Pick(fit.Select(i => list[i].Weight).ToList(), r)];
    }

    /// <summary>One tree of a tile as a pack tree: the model's id, scale and turn; null when the shape has no model. Base in Godot axes.</summary>
    public static PackPlacement? Place(TreeInstance t, TreeModel m, IReadOnlyList<PackCandidate> cone, IReadOnlyList<PackCandidate> box, PackTreeConfig cfg)
    {
        var list = t.Cone ? cone : box;
        var h = CodeHeight(t, m);
        var pick = Choose(list, h, Roll(t.X, t.Y), cfg.ScaleMin, cfg.ScaleMax, cfg.BulkShare);
        if (pick < 0) return null;
        var model = list[pick];
        return new PackPlacement(new Vector3(t.X, t.Z, -t.Y), t.Rot, model.HeightM > 1e-4f ? h / model.HeightM : 1f, model.ModelId);
    }

    public static int TileOf(float v, float tileM) => (int)MathF.Floor(v / tileM);

    /// <summary>The trees by tile (map metres, x and y of the trunk).</summary>
    public static Dictionary<(int Tx, int Ty), List<int>> TileIndex(IReadOnlyList<TreeInstance> trees, float tileM)
    {
        var index = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < trees.Count; i++)
        {
            var key = (TileOf(trees[i].X, tileM), TileOf(trees[i].Y, tileM));
            if (!index.TryGetValue(key, out var l)) index[key] = l = new List<int>();
            l.Add(i);
        }
        return index;
    }

    /// <summary>The tiles whose box is within <paramref name="radius"/> of (fx, fy), with that distance (0 for the tile the point is in).</summary>
    public static IEnumerable<(int Tx, int Ty, float Dist)> TilesNear(float fx, float fy, float radius, float tileM)
    {
        var span = (int)MathF.Ceiling(radius / tileM) + 1;
        int cx = TileOf(fx, tileM), cy = TileOf(fy, tileM);
        for (var ty = cy - span; ty <= cy + span; ty++)
            for (var tx = cx - span; tx <= cx + span; tx++)
            {
                var d = DistToBox(fx, fy, tx * tileM, ty * tileM, (tx + 1) * tileM, (ty + 1) * tileM);
                if (d <= radius) yield return (tx, ty, d);
            }
    }

    /// <summary>Distance from (fx, fy) to the box (0 inside).</summary>
    public static float DistToBox(float fx, float fy, float x0, float y0, float x1, float y1)
    {
        var dx = MathF.Max(MathF.Max(x0 - fx, fx - x1), 0f);
        var dy = MathF.Max(MathF.Max(y0 - fy, fy - y1), 0f);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// How far a tree whose base is <paramref name="dist"/> from the focus is from being a pack tree: 0 inside radius - band, 1 beyond the radius,
    /// linear between. The pack tree is drawn with share 1 - this and the baked tree with this, dithered against the same noise, so exactly one of the
    /// two is drawn at every pixel. Mirrors <c>tree_far</c> in tree_cut.gdshaderinc.
    /// </summary>
    public static float FarShare(float dist, float radius, float band)
    {
        if (dist >= radius) return 1f;
        band = MathF.Max(band, 0.001f);
        return Math.Clamp((dist - (radius - band)) / band, 0f, 1f);
    }

    /// <summary>
    /// The radius of the cut: the near radius, but no further than the nearest tile that has pack trees to draw and is not built yet (distances from
    /// the focus to those tiles), so the baked trees are never hidden before the pack trees that replace them exist.
    /// </summary>
    public static float CutRadius(float nearM, IEnumerable<float> unbuiltTileDistances)
    {
        var r = nearM;
        foreach (var d in unbuiltTileDistances) r = MathF.Min(r, d);
        return r;
    }
}

/// <summary>One surface of a pack model: vertices in the model's frame (foot at the origin, Godot axes), every vertex with a normal and a UV, and the index of its material.</summary>
public sealed record PackSurface(int Material, Vector3[] Vertices, Vector3[] Normals, Vector2[] Uv, int[] Indices);

/// <summary>A pack model with its surfaces; <see cref="Triangles"/> is the sum over them.</summary>
public sealed class PackModelMesh
{
    public required string Name { get; init; }
    public required float HeightM { get; init; }
    public required IReadOnlyList<PackSurface> Surfaces { get; init; }
    int? _triangles;
    public int Triangles => _triangles ??= Surfaces.Sum(s => s.Indices.Length / 3);
}

/// <summary>A pack tree to draw: its base (Godot axes), turn about the up axis, uniform scale and the model's id.</summary>
public readonly record struct PackPlacement(Vector3 Base, float Rot, float Scale, int Model);

/// <summary>The triangles of every placement that wears one material, as one mesh: positions relative to the tile (centre x, z), UV2 = the tree's base (x, z) in the same frame.</summary>
public sealed class PackBucket
{
    public required int Material { get; init; }
    public readonly List<Vector3> V = new(), N = new();
    public readonly List<Vector2> Uv = new(), Uv2 = new();
    public readonly List<int> I = new();
}

public static class PackTileMesher
{
    /// <summary>
    /// A tile's trees as one bucket per material (ordered by material): each model's triangles scaled, turned by the tree's Rot and moved to its base, minus the tile
    /// centre, like the meadow's merged tiles. A tile then costs one draw per material, whatever models it holds.
    /// </summary>
    public static List<PackBucket> Build(IReadOnlyList<PackModelMesh> models, IReadOnlyList<PackPlacement> trees, Vector3 centre)
    {
        var byMaterial = new SortedDictionary<int, PackBucket>();
        foreach (var t in trees)
        {
            var (s, c) = MathF.SinCos(t.Rot);
            var origin = new Vector3(t.Base.X - centre.X, t.Base.Y, t.Base.Z - centre.Z);
            var baseUv = new Vector2(origin.X, origin.Z);
            foreach (var surface in models[t.Model].Surfaces)
            {
                if (!byMaterial.TryGetValue(surface.Material, out var b)) byMaterial[surface.Material] = b = new PackBucket { Material = surface.Material };
                var first = b.V.Count;
                for (var i = 0; i < surface.Vertices.Length; i++)
                {
                    b.V.Add(Turn(surface.Vertices[i], s, c) * t.Scale + origin);
                    b.N.Add(Turn(surface.Normals[i], s, c));
                    b.Uv.Add(surface.Uv[i]);
                    b.Uv2.Add(baseUv);
                }
                foreach (var idx in surface.Indices) b.I.Add(first + idx);
            }
        }
        return byMaterial.Values.ToList();
    }

    /// <summary>Rotation about +Y by the angle whose sine and cosine are given (Godot's <c>Basis.Rotated(Up, angle)</c>).</summary>
    static Vector3 Turn(Vector3 v, float s, float c) => new(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
}
