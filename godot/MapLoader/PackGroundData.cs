using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The forest ground of the pack trees (#287): solid ground objects (logs, stumps,
// boulders, root plates of ground_objects.json) as pack models in place of the baked ones, and client-only undergrowth. Both ride the tiles of
// MapLoader.PackTrees.cs, so they merge into the same per-material draws as the trees. Config: the sections ground_objects and undergrowth of pack_trees.json.

// ---- config ----

/// <summary>One kind of ground object (log, stump, boulder, rootplate): its pack models, the code-built radius at scale 1 and the turn that lines the model up with the code-built one.</summary>
public sealed class GroundKindConfig
{
    public List<PackTreeEntry> Models { get; set; } = new();
    /// <summary>Radius of the code-built object at scale 1 (m); the pack model is scaled to this times the object's scale. ground_objects.json may state its own (model.kinds.KIND.radius_m), which wins.</summary>
    public float RadiusM { get; set; } = 0.5f;
    /// <summary>Degrees added to the object's rot: the pack log lies along its Z, the code-built one along the axis ground_objects.json says.</summary>
    public float YawDeg { get; set; }
}

public sealed class GroundObjectsConfig
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, GroundKindConfig> Kinds { get; set; } = new();

    public void Validate()
    {
        foreach (var (kind, k) in Kinds)
        {
            if (!(k.RadiusM > 0)) throw new DataFileException($"pack_trees.json: ground_objects kind '{kind}' needs radius_m above 0");
            if (k.Models.Any(e => e.Name.Length == 0 || e.Weight < 0)) throw new DataFileException($"pack_trees.json: ground_objects kind '{kind}' has an entry without a name or with a negative weight");
        }
    }
}

/// <summary>One kind of undergrowth: how many per 100 m2 of forest floor, how big, how clumped, how much denser at tree feet, and which models.</summary>
public sealed class UndergrowthKindConfig
{
    public string Name { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("per_100m2")] // the snake-case policy would make it per100_m2
    public float Per100M2 { get; set; }
    public float ScaleMin { get; set; } = 1f;
    public float ScaleMax { get; set; } = 1f;
    /// <summary>0 = even, 1 = patches with bare gaps between thick clumps (mean stays near 1).</summary>
    public float Clump { get; set; }
    /// <summary>Density factor right at a trunk (falls to 1 at foot_m).</summary>
    public float Foot { get; set; } = 1f;
    public List<PackTreeEntry> Models { get; set; } = new();
}

public sealed class UndergrowthConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>The mix id (high nibble of meadow.bin's zone byte) of forest ground: rules.yaml detail.meadow.zones.forest.</summary>
    public int ForestMix { get; set; } = 5;
    /// <summary>Most triangles of undergrowth in one tile; over it the scatter is thinned.</summary>
    public int TrisPerTile { get; set; } = 60000;
    /// <summary>Lattice of the clump noise (m).</summary>
    public float ClumpM { get; set; } = 6f;
    /// <summary>A tree's trunk raises the density within this distance (m).</summary>
    public float FootM { get; set; } = 4f;
    /// <summary>Nothing grows within this distance (m, times the tree's scale) of a trunk.</summary>
    public float TrunkClearM { get; set; } = 0.4f;
    public List<UndergrowthKindConfig> Kinds { get; set; } = new();

    public void Validate()
    {
        if (TrisPerTile < 0) throw new DataFileException("pack_trees.json: undergrowth.tris_per_tile must not be negative");
        if (!(ClumpM > 0) || FootM < 0 || TrunkClearM < 0) throw new DataFileException("pack_trees.json: undergrowth clump_m must be above 0, foot_m and trunk_clear_m not negative");
        foreach (var k in Kinds)
        {
            if (k.Name.Length == 0 || k.Per100M2 < 0 || !(k.ScaleMin > 0) || k.ScaleMax < k.ScaleMin || k.Clump < 0 || k.Clump > 1 || k.Foot < 0)
                throw new DataFileException($"pack_trees.json: undergrowth kind '{k.Name}' has a bad number (per_100m2, scale_min/max, clump 0..1, foot)");
            if (k.Models.Any(e => e.Name.Length == 0 || e.Weight < 0)) throw new DataFileException($"pack_trees.json: undergrowth kind '{k.Name}' has an entry without a name or with a negative weight");
        }
    }
}

// ---- ground_objects.json ----

/// <summary>One solid ground object of the map (map metres, z up; Rot as in trees.json; Len for logs).</summary>
public readonly record struct PackGroundObject(string Kind, float X, float Y, float Z, float Rot, float Scale, float Len);

/// <summary>ground_objects.json (format 1): the objects, and the sizes it states for its kinds (<c>model.kinds.KIND.radius_m</c>, optional).</summary>
public sealed class GroundObjectFile
{
    public bool BaseUv2 { get; init; }
    public IReadOnlyList<PackGroundObject> Objects { get; init; } = Array.Empty<PackGroundObject>();
    public IReadOnlyDictionary<string, float> RadiusM { get; init; } = new Dictionary<string, float>();

    /// <summary>Parses the file; null for a file that is not JSON or is not format 1 (the caller warns). A malformed object is skipped.</summary>
    public static GroundObjectFile? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format_version", out var fv) || fv.ValueKind != JsonValueKind.Number || fv.GetInt32() != 1) return null;
            var baseUv2 = false;
            var radii = new Dictionary<string, float>();
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                baseUv2 = m.TryGetProperty("base_uv2", out var b) && b.ValueKind == JsonValueKind.True;
                if (m.TryGetProperty("kinds", out var ks) && ks.ValueKind == JsonValueKind.Object)
                    foreach (var k in ks.EnumerateObject())
                        if (k.Value.ValueKind == JsonValueKind.Object && k.Value.TryGetProperty("radius_m", out var r) && r.ValueKind == JsonValueKind.Number && r.GetSingle() > 0)
                            radii[k.Name] = r.GetSingle();
            }
            var list = new List<PackGroundObject>();
            if (root.TryGetProperty("objects", out var objs) && objs.ValueKind == JsonValueKind.Array)
                foreach (var o in objs.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) continue;
                    float N(string key, float dflt) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : dflt;
                    if (!o.TryGetProperty("x", out _) || !o.TryGetProperty("y", out _)) continue;
                    list.Add(new PackGroundObject(kind.GetString()!, N("x", 0), N("y", 0), N("z", 0), N("rot", 0), N("scale", 1f), N("len", 0)));
                }
            return new GroundObjectFile { BaseUv2 = baseUv2, Objects = list, RadiusM = radii };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>A pack model that may stand in for a ground object of one kind (id into the loader's models).</summary>
public readonly record struct GroundCandidate(int ModelId, float Weight);

public static class GroundObjectMath
{
    const int ModelSalt = 3101;

    /// <summary>The candidate for an object, by a hash of its position and the weights (-1 when the kind has none).</summary>
    public static int Choose(IReadOnlyList<GroundCandidate> list, float x, float y)
    {
        var roll = MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), ModelSalt);
        return PackTreeMath.Pick(list.Select(c => c.Weight).ToList(), roll);
    }

    /// <summary>
    /// The pack model as one object of the map, Godot axes. The cross-section is scaled so the model's horizontal radius is <paramref name="radiusM"/> times
    /// the object's scale (the code-built size, which the collider follows); a log with a length is stretched along the model's Z (its long axis) to exactly that
    /// length, so the model never passes the collider.
    /// </summary>
    public static PackPlacement Place(PackGroundObject o, PackModelMesh model, int modelId, float radiusM, float yawDeg, bool stretchToLen)
    {
        var (lo, hi) = model.Bounds;
        float ex = hi.X - lo.X, ey = hi.Y - lo.Y, ez = hi.Z - lo.Z;
        // a log's cross-section is X/Y (its length is Z); the others are as wide as the wider horizontal side
        var modelRadius = stretchToLen ? MathF.Max(ex, ey) * 0.5f : MathF.Max(ex, ez) * 0.5f;
        var s = modelRadius > 1e-4f ? radiusM * o.Scale / modelRadius : o.Scale;
        var stretch = 1f;
        if (stretchToLen && o.Len > 0 && ez > 1e-4f && s > 1e-6f) stretch = o.Len / (ez * s);
        return new PackPlacement(new Vector3(o.X, o.Z, -o.Y), o.Rot + yawDeg * MathF.PI / 180f, s, modelId, stretch);
    }
}

// ---- undergrowth ----

/// <summary>A trunk in map metres, for clearing and for the density at tree feet.</summary>
public readonly record struct TreeFoot(float X, float Y, float Scale);

/// <summary>One kind of undergrowth resolved against the loaded models.</summary>
public sealed record UndergrowthKind(float PerM2, float ScaleMin, float ScaleMax, float Clump, float Foot, int[] ModelIds, float[] Weights, int[] Tris);

/// <summary>The forest cells of a tile: global cell indices (floor of metres over cell size), a bool per cell of the box starting at (Gx0, Gy0).</summary>
public sealed class ForestMask
{
    public required int Gx0 { get; init; }
    public required int Gy0 { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    public required bool[] Cells { get; init; }
    public bool Contains(int gx, int gy) { int x = gx - Gx0, y = gy - Gy0; return x >= 0 && y >= 0 && x < W && y < H && Cells[y * W + x]; }
}

public static class UndergrowthGen
{
    const int Salt = 9000, MaxPerCell = 12;

    /// <summary>The clump factor for a noise value in [0, 1): 1 at the middle, 0 in gaps, up to 1 + 2 * clump in the thick of a clump.</summary>
    public static float ClumpFactor(double noise, float clump) => MathF.Max(0f, 1f + 2f * clump * (float)(2 * noise - 1));

    /// <summary>The density factor at distance <paramref name="dist"/> from the nearest trunk: <paramref name="foot"/> at the trunk, 1 from <paramref name="footM"/> on.</summary>
    public static float FootFactor(float dist, float footM, float foot)
    {
        if (footM <= 0 || dist >= footM) return 1f;
        var t = 1f - dist / footM;
        return 1f + (foot - 1f) * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The undergrowth of the tile with map-metre box [x0, x1) x [y0, y1), Godot axes. Every forest cell (cellM metres, aligned to the global grid so tiles agree)
    /// makes, per kind, floor(e) plus one with probability frac(e) instances, e = density x clump x foot, each from a hash of the cell: the same on every client
    /// and every run, whatever the tile. <paramref name="groundZ"/> gives the ground at a map position (NaN: none, nothing grows there). Over
    /// <paramref name="trisBudget"/> triangles the scatter is thinned by a hash of each position (every kind and place thins alike, the same ones go first).
    /// </summary>
    public static List<PackPlacement> Tile(UndergrowthConfig cfg, IReadOnlyList<UndergrowthKind> kinds, float x0, float y0, float x1, float y1, float cellM,
        ForestMask forest, IReadOnlyList<TreeFoot> trees, Func<float, float, float> groundZ, int trisBudget, out int triangles)
    {
        var all = new List<(PackPlacement P, int Tris, double Prio)>();
        int gx0 = (int)MathF.Floor(x0 / cellM), gx1 = (int)MathF.Ceiling(x1 / cellM), gy0 = (int)MathF.Floor(y0 / cellM), gy1 = (int)MathF.Ceiling(y1 / cellM);
        var lattice = cfg.ClumpM / cellM;
        for (var gy = gy0; gy < gy1; gy++)
            for (var gx = gx0; gx < gx1; gx++)
            {
                if (!forest.Contains(gx, gy)) continue;
                var cx = (gx + 0.5f) * cellM; var cy = (gy + 0.5f) * cellM;
                var d = float.MaxValue;
                foreach (var t in trees) d = MathF.Min(d, MathF.Sqrt((t.X - cx) * (t.X - cx) + (t.Y - cy) * (t.Y - cy)));
                for (var ki = 0; ki < kinds.Count; ki++)
                {
                    var k = kinds[ki];
                    if (k.PerM2 <= 0 || k.ModelIds.Length == 0) continue;
                    var kb = Salt + ki * 100;
                    var noise = k.Clump > 0 ? MeadowGen.ValueNoise(gx + 0.5, gy + 0.5, lattice, kb + 99) : 0.5;
                    var e = k.PerM2 * cellM * cellM * ClumpFactor(noise, k.Clump) * FootFactor(d, cfg.FootM, k.Foot);
                    var whole = MathF.Floor(e);
                    var count = Math.Min(MaxPerCell, (int)whole + (MeadowGen.Hash01(gx, gy, kb) < e - whole ? 1 : 0));
                    for (var j = 0; j < count; j++)
                    {
                        var s = kb + 1 + 6 * j;
                        var x = (gx + (float)MeadowGen.Hash01(gx, gy, s)) * cellM;
                        var y = (gy + (float)MeadowGen.Hash01(gx, gy, s + 1)) * cellM;
                        if (x < x0 || x >= x1 || y < y0 || y >= y1) continue;
                        var rot = (float)(MeadowGen.Hash01(gx, gy, s + 2) * 2 * Math.PI);
                        var scale = k.ScaleMin + (float)MeadowGen.Hash01(gx, gy, s + 3) * (k.ScaleMax - k.ScaleMin);
                        var pick = PackTreeMath.Pick(k.Weights, MeadowGen.Hash01(gx, gy, s + 4));
                        if (pick < 0) continue;
                        var clear = false;
                        foreach (var t in trees)
                        {
                            var r = cfg.TrunkClearM * t.Scale;
                            if ((t.X - x) * (t.X - x) + (t.Y - y) * (t.Y - y) < r * r) { clear = true; break; }
                        }
                        if (clear) continue;
                        var z = groundZ(x, y);
                        if (float.IsNaN(z)) continue;
                        all.Add((new PackPlacement(new Vector3(x, z, -y), rot, scale, k.ModelIds[pick]), k.Tris[pick], MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), Salt + 7)));
                    }
                }
            }
        long total = 0;
        foreach (var a in all) total += a.Tris;
        if (total > trisBudget)
        {
            all.Sort((a, b) => a.Prio.CompareTo(b.Prio));
            var kept = new List<(PackPlacement P, int Tris, double Prio)>();
            long sum = 0;
            foreach (var a in all) { if (sum + a.Tris > trisBudget) break; sum += a.Tris; kept.Add(a); }
            all = kept; total = sum;
        }
        triangles = (int)total;
        return all.Select(a => a.P).ToList();
    }
}
