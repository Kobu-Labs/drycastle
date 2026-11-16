using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The pack trees (#287): detailed models of the private nature pack
// near the player in place of the baked trees. Config: shooter/data/pack_trees.json. Streaming and drawing: MapLoader.PackTrees.cs.

/// <summary>A model that may stand in for a tree shape, its share among the models of that shape, and whether it is a bulk model (see the file's _note).</summary>
public sealed class PackTreeEntry
{
    /// <summary>The pack (folder under assets-private/synty/glb) the model is in; model names are unique per pack, so two packs may share one.</summary>
    public string Pack { get; set; } = PackTreeConfig.DefaultPack;
    public string Name { get; set; } = "";
    public float Weight { get; set; } = 1f;
    public bool Bulk { get; set; }
}

/// <summary>The models of one stand (trees.json <c>stand</c>): per shape, or <c>any</c> for both; a shape without models takes today's <c>shapes</c> list.</summary>
public sealed class PackStandConfig
{
    public List<PackTreeEntry> Cone { get; set; } = new();
    public List<PackTreeEntry> Box { get; set; } = new();
    /// <summary>Models for a shape this stand lists nothing for (a cone or box list of its own wins).</summary>
    public List<PackTreeEntry> Any { get; set; } = new();
    /// <summary>The stand's own bulk share (the file's bulk_share when absent): a birch stand wants its birches seen, a pine stand its cheap bulk pines.</summary>
    public float? BulkShare { get; set; }
    /// <summary>Whether an edge tree of this stand may take the <c>edge</c> models (a swamp keeps its dead trees to the edge).</summary>
    public bool Edge { get; set; } = true;

    public IReadOnlyList<PackTreeEntry> For(bool cone) => (cone ? Cone : Box) is { Count: > 0 } l ? l : Any;
}

/// <summary>pack_trees.json: the near radius, the tiles and the models per tree shape (see the file's _note).</summary>
public sealed class PackTreeConfig : IVersioned
{
    public const string DefaultPack = "nature";
    /// <summary>The stand whose models an edge tree may take (see <see cref="EdgeShare"/>).</summary>
    public const string EdgeStand = "edge";

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
    /// <summary>Share of edge trees (trees.json <c>edge</c>) that take the models of the <c>edge</c> stand (round, bushy broadleaf) instead of their stand's.</summary>
    public float EdgeShare { get; set; } = 0.5f;
    public bool Shadows { get; set; } = true;
    public Dictionary<string, List<PackTreeEntry>> Shapes { get; set; } = new();
    /// <summary>Models per stand (pine, birch, mixed, thicket, swamp, and edge); a stand not listed (or a shape it lists nothing for) takes <see cref="Shapes"/>.</summary>
    public Dictionary<string, PackStandConfig> Stands { get; set; } = new();
    public GroundObjectsConfig GroundObjects { get; set; } = new();
    public UndergrowthConfig Undergrowth { get; set; } = new();

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
        if (c.EdgeShare < 0 || c.EdgeShare > 1) throw new DataFileException("pack_trees.json: edge_share must be within 0 and 1");
        foreach (var (shape, list) in c.Shapes) CheckEntries($"shape '{shape}'", list);
        foreach (var (stand, sc) in c.Stands)
        {
            if (sc.BulkShare is < 0 or > 1) throw new DataFileException($"pack_trees.json: stand '{stand}' bulk_share must be within 0 and 1");
            CheckEntries($"stand '{stand}'", sc.Cone.Concat(sc.Box).Concat(sc.Any));
        }
        c.GroundObjects.Validate();
        c.Undergrowth.Validate();
        return c;
    }

    internal static void CheckEntries(string what, IEnumerable<PackTreeEntry> list)
    {
        if (list.Any(e => e.Name.Length == 0 || e.Pack.Length == 0 || e.Weight < 0)) throw new DataFileException($"pack_trees.json: {what} has an entry without a name or pack or with a negative weight");
    }

    /// <summary>Every pack the config names a model of (the nature pack always: it is the one a map needs).</summary>
    public IEnumerable<string> Packs()
    {
        var entries = Shapes.Values.SelectMany(l => l)
            .Concat(Stands.Values.SelectMany(s => s.Cone.Concat(s.Box).Concat(s.Any)))
            .Concat(GroundObjects.Kinds.Values.SelectMany(k => k.Models))
            .Concat(Undergrowth.Mixes.SelectMany(m => m.Kinds.SelectMany(k => k.Models)));
        return entries.Select(e => e.Pack).Append(DefaultPack).Distinct();
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

/// <summary>What trees.json says about a tree besides its geometry: its stand and whether it stands at the forest edge (the collision ignores both).</summary>
public readonly record struct PackTreeTag(string Stand, bool Edge)
{
    public const string Mixed = "mixed";
    public static readonly PackTreeTag Plain = new(Mixed, false);
}

public static class PackTreeTags
{
    /// <summary>
    /// The tag of every tree record of trees.json, in file order (<see cref="TreeColliders.Parse"/> makes one tree per record in the same order, so the two lists
    /// line up). A record without <c>stand</c> is "mixed", without <c>edge</c> not at the edge. Padded with plain tags to <paramref name="count"/> when the file
    /// holds fewer records (and empty when it cannot be read), so an index into the trees is always valid.
    /// </summary>
    public static PackTreeTag[] Parse(string treesJson, int count)
    {
        var tags = new PackTreeTag[count];
        Array.Fill(tags, PackTreeTag.Plain);
        if (count == 0 || string.IsNullOrWhiteSpace(treesJson)) return tags;
        try
        {
            using var doc = JsonDocument.Parse(treesJson);
            if (!doc.RootElement.TryGetProperty("trees", out var trees) || trees.ValueKind != JsonValueKind.Array) return tags;
            var i = 0;
            foreach (var t in trees.EnumerateArray())
            {
                if (i >= count) break;
                var stand = t.ValueKind == JsonValueKind.Object && t.TryGetProperty("stand", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } v ? v : PackTreeTag.Mixed;
                var edge = t.ValueKind == JsonValueKind.Object && t.TryGetProperty("edge", out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.Number && e.GetInt32() != 0);
                tags[i++] = new PackTreeTag(stand, edge);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        return tags;
    }
}

/// <summary>The models one stand draws, resolved: per shape, the bulk share, whether its edge trees may take the edge models.</summary>
public sealed record PackStandModels(PackCandidate[] Cone, PackCandidate[] Box, float BulkShare, bool Edge);

/// <summary>The resolved model lists of every stand (<see cref="PackTreeConfig.Stands"/>) beside today's per-shape lists, which anything unlisted falls back to.</summary>
public sealed class PackModelSets
{
    public required PackStandModels Default { get; init; }
    public IReadOnlyDictionary<string, PackStandModels> Stands { get; init; } = new Dictionary<string, PackStandModels>();

    /// <summary>The set of a tree: its stand's (the default one for an unknown stand), or the edge stand's for an edge tree whose <paramref name="edgeRoll"/> falls under <paramref name="edgeShare"/>.</summary>
    public PackStandModels For(PackTreeTag tag, float edgeShare, double edgeRoll)
    {
        var set = Stands.TryGetValue(string.IsNullOrEmpty(tag.Stand) ? PackTreeTag.Mixed : tag.Stand, out var s) ? s : Default;
        if (tag.Edge && set.Edge && edgeRoll < edgeShare && Stands.TryGetValue(PackTreeConfig.EdgeStand, out var e)) return e;
        return set;
    }

    /// <summary>
    /// Resolves the config's lists with <paramref name="resolve"/> (the model's id and height, null when the model cannot be loaded). A shape a list is empty for
    /// (nothing listed or nothing loadable) takes the default list of the shape, and the default's own empty shape the other shape's, so a tree always has a model
    /// as long as any has loaded.
    /// </summary>
    public static PackModelSets Build(PackTreeConfig cfg, Func<PackTreeEntry, PackCandidate?> resolve)
    {
        PackCandidate[] List(IEnumerable<PackTreeEntry> entries)
        {
            var l = new List<PackCandidate>();
            foreach (var e in entries)
                if (e.Weight > 0 && resolve(e) is { } c) l.Add(c with { Weight = e.Weight, Bulk = e.Bulk });
            return l.ToArray();
        }
        var cone = List(cfg.For(true));
        var box = List(cfg.For(false));
        var def = new PackStandModels(cone.Length > 0 ? cone : box, box.Length > 0 ? box : cone, cfg.BulkShare, true);
        var stands = new Dictionary<string, PackStandModels>();
        foreach (var (name, sc) in cfg.Stands)
        {
            var sCone = List(sc.For(true));
            var sBox = List(sc.For(false));
            if (sCone.Length == 0 && sBox.Length == 0) { sCone = def.Cone; sBox = def.Box; }
            else if (sCone.Length == 0) sCone = sBox; // a stand with one shape's list only
            else if (sBox.Length == 0) sBox = sCone;
            stands[name] = new PackStandModels(sCone, sBox, sc.BulkShare ?? cfg.BulkShare, sc.Edge);
        }
        return new PackModelSets { Default = def, Stands = stands };
    }
}

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
        => PlaceFrom(t, m, t.Cone ? cone : box, cfg.BulkShare, cfg);

    /// <summary>One tree as a pack tree, the models by the tree's stand and edge flag (<see cref="PackModelSets.For"/>).</summary>
    public static PackPlacement? Place(TreeInstance t, TreeModel m, PackTreeTag tag, PackModelSets sets, PackTreeConfig cfg)
    {
        var set = sets.For(tag, cfg.EdgeShare, EdgeRoll(t.X, t.Y));
        return PlaceFrom(t, m, t.Cone ? set.Cone : set.Box, set.BulkShare, cfg);
    }

    static PackPlacement? PlaceFrom(TreeInstance t, TreeModel m, IReadOnlyList<PackCandidate> list, float bulkShare, PackTreeConfig cfg)
    {
        var h = CodeHeight(t, m);
        var pick = Choose(list, h, Roll(t.X, t.Y), cfg.ScaleMin, cfg.ScaleMax, bulkShare);
        if (pick < 0) return null;
        var model = list[pick];
        return new PackPlacement(new Vector3(t.X, t.Z, -t.Y), t.Rot, model.HeightM > 1e-4f ? h / model.HeightM : 1f, model.ModelId);
    }

    const int EdgeSalt = 3002;

    /// <summary>A second number in [0, 1) from a tree's position, independent of <see cref="Roll"/>: whether an edge tree takes the edge models.</summary>
    public static double EdgeRoll(float x, float y)
        => MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), EdgeSalt);

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

    (Vector3 Min, Vector3 Max)? _bounds;
    /// <summary>The box of all vertices in the model's frame.</summary>
    public (Vector3 Min, Vector3 Max) Bounds => _bounds ??= ComputeBounds();

    (Vector3, Vector3) ComputeBounds()
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var s in Surfaces) foreach (var v in s.Vertices) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        return Surfaces.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (lo, hi);
    }
}

/// <summary>A pack model to draw (tree, ground object, undergrowth): its base (Godot axes), turn about the up axis, uniform scale, the model's id and a stretch of the model's Z axis on top of the scale (a log's length; 1 = none).</summary>
public readonly record struct PackPlacement(Vector3 Base, float Rot, float Scale, int Model, float Stretch = 1f);

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
                    b.V.Add(Turn(surface.Vertices[i] * new Vector3(t.Scale, t.Scale, t.Scale * t.Stretch), s, c) + origin);
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
