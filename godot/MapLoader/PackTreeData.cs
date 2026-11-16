using System.Numerics;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The pack-tree prototype (#287): detailed models of the private nature pack
// near the player in place of the baked trees. Config: shooter/data/pack_trees.json. Streaming and drawing: MapLoader.PackTrees.cs.

/// <summary>A model that may stand in for a tree shape, and its share among the models of that shape.</summary>
public sealed class PackTreeEntry
{
    public string Name { get; set; } = "";
    public float Weight { get; set; } = 1f;
}

/// <summary>pack_trees.json: the near ring, the tiles and the models per tree shape (see the file's _note).</summary>
public sealed class PackTreeConfig : IVersioned
{
    public int Version { get; set; }
    public float NearM { get; set; } = 70f;
    public float FadeM { get; set; } = 14f;
    public float TileM { get; set; } = 32f;
    public float ReleaseM { get; set; } = 120f;
    public float RescanM { get; set; } = 3f;
    public int MaxBuildsPerCall { get; set; } = 2;
    public double BudgetMs { get; set; } = 3.0;
    public bool Shadows { get; set; } = true;
    public Dictionary<string, List<PackTreeEntry>> Shapes { get; set; } = new();

    public static PackTreeConfig Parse(string json)
    {
        var c = DataFile.Parse<PackTreeConfig>(json, "pack_trees.json");
        if (!(c.NearM > 0)) throw new DataFileException("pack_trees.json: near_m must be above 0");
        if (c.FadeM < 0) throw new DataFileException("pack_trees.json: fade_m must not be negative");
        if (!(c.TileM > 0)) throw new DataFileException("pack_trees.json: tile_m must be above 0");
        if (c.ReleaseM < 0 || c.RescanM < 0) throw new DataFileException("pack_trees.json: release_m and rescan_m must not be negative");
        if (c.MaxBuildsPerCall < 1) throw new DataFileException("pack_trees.json: max_builds_per_call must be at least 1");
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
}

/// <summary>The arithmetic of the pack trees: which model, how big, which tile.</summary>
public static class PackTreeMath
{
    const int ModelSalt = 3001;

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

    /// <summary>The tiles whose centre is within <paramref name="radius"/> of (fx, fy), with that distance.</summary>
    public static IEnumerable<(int Tx, int Ty, float Dist)> TilesWithin(float fx, float fy, float radius, float tileM)
    {
        var span = (int)MathF.Ceiling(radius / tileM) + 1;
        int cx = TileOf(fx, tileM), cy = TileOf(fy, tileM);
        for (var ty = cy - span; ty <= cy + span; ty++)
            for (var tx = cx - span; tx <= cx + span; tx++)
            {
                var dx = (tx + 0.5f) * tileM - fx;
                var dy = (ty + 0.5f) * tileM - fy;
                var d = MathF.Sqrt(dx * dx + dy * dy);
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
}

/// <summary>
/// Which tree a baked triangle belongs to. The chunk meshes hold fences and trees in one mesh per props node with nothing to tell them apart,
/// but a tree's triangles lie exactly on the faces of its collision solids (the colliders mirror the drawn tree), so a triangle with all three
/// corners on one face of one tree's solid is that tree's. A fence triangle would have to lie on a tree's face by chance. Map metres.
/// </summary>
public sealed class TreeTriangleOwner
{
    const float CellM = 16f, Tol = 0.02f;

    readonly IReadOnlyList<TreeInstance> _trees;
    readonly TreeModel _model;
    readonly Dictionary<(int, int), List<int>> _cells = new();
    readonly (Vector3 N, float D)[][][] _planes; // per tree (built when first asked), per part: unit normals
    int _last = -1;

    public TreeTriangleOwner(IReadOnlyList<TreeInstance> trees, TreeModel model)
    {
        _trees = trees;
        _model = model;
        _planes = new (Vector3, float)[trees.Count][][];
        for (var i = 0; i < trees.Count; i++)
        {
            var t = trees[i];
            var r = model.CanopyM * t.Scale * 1.6f + 0.5f; // as TreeColliders: nothing of the tree lies further out
            for (var cx = Cell(t.X - r); cx <= Cell(t.X + r); cx++)
                for (var cy = Cell(t.Y - r); cy <= Cell(t.Y + r); cy++)
                {
                    if (!_cells.TryGetValue((cx, cy), out var list)) _cells[(cx, cy)] = list = new List<int>();
                    list.Add(i);
                }
        }
    }

    static int Cell(float v) => (int)MathF.Floor(v / CellM);

    /// <summary>The index (into the tree list) of the tree the triangle belongs to, or -1 (a fence, or anything else).</summary>
    public int Find(Vector3 a, Vector3 b, Vector3 c)
    {
        if (_last >= 0 && On(_last, a, b, c)) return _last; // a tree's triangles are consecutive
        var m = (a + b + c) / 3f;
        if (!_cells.TryGetValue((Cell(m.X), Cell(m.Y)), out var candidates)) return -1;
        foreach (var i in candidates)
            if (i != _last && On(i, a, b, c)) return _last = i;
        return -1;
    }

    bool On(int i, Vector3 a, Vector3 b, Vector3 c)
    {
        var parts = _planes[i] ??= Planes(i);
        foreach (var planes in parts)
            if (OnFace(planes, a, b, c)) return true;
        return false;
    }

    (Vector3 N, float D)[][] Planes(int i)
    {
        var parts = TreeColliders.Parts(_trees[i], _model);
        var result = new (Vector3, float)[parts.Count][];
        for (var k = 0; k < parts.Count; k++)
            result[k] = parts[k].Planes().Select(p => { var len = p.N.Length(); return (p.N / len, p.D / len); }).ToArray();
        return result;
    }

    /// <summary>All three corners within the tolerance of the solid, and on one of its faces.</summary>
    static bool OnFace((Vector3 N, float D)[] planes, Vector3 a, Vector3 b, Vector3 c)
    {
        foreach (var (n, d) in planes)
            if (MathF.Abs(Vector3.Dot(n, a) - d) <= Tol && MathF.Abs(Vector3.Dot(n, b) - d) <= Tol && MathF.Abs(Vector3.Dot(n, c) - d) <= Tol)
            {
                foreach (var (n2, d2) in planes)
                    if (Vector3.Dot(n2, a) - d2 > Tol || Vector3.Dot(n2, b) - d2 > Tol || Vector3.Dot(n2, c) - d2 > Tol) return false;
                return true;
            }
        return false;
    }
}
