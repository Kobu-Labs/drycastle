using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The forest ground of the pack trees (#287): ground objects (fallen logs are drawn only, #359; stumps,
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

/// <summary>
/// Fallen trees (#287): how a log record (4-9 m, 0.3-0.6 m thick, a size box that is not solid, #359) gets a model. An upright model (<see cref="PackTreeEntry.Lay"/>: a dead tree) is laid down
/// along the log, base at the thick end, scaled so its height is the log's length; a log model is used as it is, or several end to end, stretched at most <see cref="StretchMax"/>.
/// </summary>
public sealed class FallenConfig
{
    /// <summary>Off: every log takes a log model stretched to its length (the first #287 slice), <c>lay</c> entries are ignored.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Most any axis of a model is stretched against the others (a log model's length against its girth, a laid tree's length against its girth).</summary>
    public float StretchMax { get; set; } = 1.25f;
    /// <summary>The span of the uniform scale that makes a laid model's height the log's length; a model outside it is not used for that log.</summary>
    public float ScaleMin { get; set; } = 0.5f;
    public float ScaleMax { get; set; } = 2.6f;
    /// <summary>How deep a laid trunk sinks into the ground, as a share of its diameter (a log on a slope is half buried; the underside is never seen).</summary>
    public float Sink { get; set; } = 0.12f;
    /// <summary>Most log models laid end to end for one record (a 9 m log is three 3 m ones).</summary>
    public int MaxPieces { get; set; } = 3;
    /// <summary>Weight of the log models when they take two or more pieces, against one piece (a laid dead tree is the nicer sight on a long record).</summary>
    public float PieceWeight { get; set; } = 0.5f;

    public void Validate()
    {
        if (StretchMax < 1f || !(ScaleMin > 0) || ScaleMax < ScaleMin || Sink < 0 || Sink > 0.4f || MaxPieces < 1 || MaxPieces > 6 || PieceWeight < 0)
            throw new DataFileException("pack_trees.json: ground_objects.fallen needs stretch_max of 1 or more, scale_min above 0 and not above scale_max, sink within 0 and 0.4, max_pieces within 1 and 6, piece_weight of 0 or more");
    }
}

public sealed class GroundObjectsConfig
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, GroundKindConfig> Kinds { get; set; } = new();
    public FallenConfig Fallen { get; set; } = new();

    public void Validate()
    {
        Fallen.Validate();
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
    /// <summary>A kind big enough to be seen appearing (saplings, shrubs, bushes): it is drawn out to <see cref="UndergrowthConfig.FarM"/> instead of the near radius.</summary>
    public bool Far { get; set; }
    public List<PackTreeEntry> Models { get; set; } = new();
}

/// <summary>The kinds of undergrowth that grow on the cells of one meadow mix (the high nibble of meadow.bin's zone byte: rules.yaml detail.meadow.zones, 5 = forest, 9 = swamp).</summary>
public sealed class UndergrowthMixConfig
{
    public int Mix { get; set; }
    public string Name { get; set; } = "";
    public List<UndergrowthKindConfig> Kinds { get; set; } = new();
}

public sealed class UndergrowthConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Undergrowth has no baked twin, so it fades out on its own, shorter radius (own fade band inside it) and is built only for tiles within <see cref="NearM"/> + <see cref="AheadM"/>
    /// of the focus, as its own mesh (about 48k triangles a forest tile, more than the trees of it).
    /// </summary>
    public float NearM { get; set; } = 60f;
    public float FadeM { get; set; } = 12f;
    /// <summary>Tiles are built this far past <see cref="NearM"/> and freed twice as far.</summary>
    public float AheadM { get; set; } = 16f;
    /// <summary>The radius of the kinds marked <c>far</c> (own mesh per tile, own fade band <see cref="FarFadeM"/>); at or under <see cref="NearM"/> = no far kinds, all on the near radius.</summary>
    public float FarM { get; set; }
    public float FarFadeM { get; set; } = 24f;
    /// <summary>Share of the fade band over which one piece grows out of the ground (the rest of the band spreads the pieces' starts).</summary>
    public float Grow { get; set; } = 0.35f;
    /// <summary>How deep (m) a piece starts under the ground while it grows.</summary>
    public float SinkM { get; set; } = 0.5f;
    /// <summary>Per meadow mix: its kinds. A mix no cell of the map has costs nothing; a cell of a mix not listed grows nothing.</summary>
    public List<UndergrowthMixConfig> Mixes { get; set; } = new();
    /// <summary>Most triangles of undergrowth in one tile; over it the scatter is thinned.</summary>
    public int TrisPerTile { get; set; } = 60000;
    /// <summary>Lattice of the clump noise (m).</summary>
    public float ClumpM { get; set; } = 6f;
    /// <summary>A tree's trunk raises the density within this distance (m).</summary>
    public float FootM { get; set; } = 4f;
    /// <summary>Nothing grows within this distance (m, times the tree's scale) of a trunk.</summary>
    public float TrunkClearM { get; set; } = 0.4f;

    public void Validate()
    {
        if (TrisPerTile < 0) throw new DataFileException("pack_trees.json: undergrowth.tris_per_tile must not be negative");
        if (!(NearM > 0) || FadeM < 0 || AheadM < 0) throw new DataFileException("pack_trees.json: undergrowth near_m must be above 0, fade_m and ahead_m not negative");
        if (FarM < 0 || FarFadeM < 0 || !(Grow > 0) || Grow > 1 || SinkM < 0) throw new DataFileException("pack_trees.json: undergrowth far_m, far_fade_m and sink_m must not be negative, grow within 0 and 1");
        if (!(ClumpM > 0) || FootM < 0 || TrunkClearM < 0) throw new DataFileException("pack_trees.json: undergrowth clump_m must be above 0, foot_m and trunk_clear_m not negative");
        var seen = new HashSet<int>();
        foreach (var mix in Mixes)
        {
            // the zone byte's high nibble: 0..15
            if (mix.Mix is < 0 or > 15 || !seen.Add(mix.Mix)) throw new DataFileException($"pack_trees.json: undergrowth mix '{mix.Name}' needs a mix id within 0 and 15 that no other mix has");
            foreach (var k in mix.Kinds)
            {
                if (k.Name.Length == 0 || k.Per100M2 < 0 || !(k.ScaleMin > 0) || k.ScaleMax < k.ScaleMin || k.Clump < 0 || k.Clump > 1 || k.Foot < 0)
                    throw new DataFileException($"pack_trees.json: undergrowth kind '{k.Name}' has a bad number (per_100m2, scale_min/max, clump 0..1, foot)");
                PackTreeConfig.CheckEntries($"undergrowth kind '{k.Name}'", k.Models);
            }
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
    /// <summary>Kinds whose baked mesh carries no base point (<c>model.baked_only</c>): never cut, so they need no pack model.</summary>
    public IReadOnlySet<string> BakedOnly { get; init; } = new HashSet<string>();
    /// <summary>The model numbers the pipeline drew the objects and the collision solids from (the collider's arithmetic gives the box a log's trunk must lie in).</summary>
    public GroundModel Model { get; init; } = new();

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
            var bakedOnly = new HashSet<string>();
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                baseUv2 = m.TryGetProperty("base_uv2", out var b) && b.ValueKind == JsonValueKind.True;
                if (m.TryGetProperty("baked_only", out var bo) && bo.ValueKind == JsonValueKind.Array)
                    foreach (var e in bo.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) bakedOnly.Add(e.GetString()!);
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
            GroundModel model;
            try { GroundObjectColliders.Parse(json, out model); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { model = new GroundModel(); }
            return new GroundObjectFile { BaseUv2 = baseUv2, Objects = list, RadiusM = radii, BakedOnly = bakedOnly, Model = model };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>A pack model that may stand in for a ground object of one kind (id into the loader's models).</summary>
public readonly record struct GroundCandidate(int ModelId, float Weight, bool Lay = false);

/// <summary>The size box of a log (not solid), in metres: half its width across the log and the height of its top over the object's ground point (the trunk drawn must lie inside both).</summary>
public readonly record struct LogBox(float HalfWidth, float Top);

/// <summary>An upright model laid down on a log: the scale of its cross-section, how much its length is stretched against that, and how high its axis rides over the ground.</summary>
public readonly record struct LaidFit(float Cross, float Stretch, float Lift);

public static class GroundObjectMath
{
    const int ModelSalt = 3101, PieceSalt = 3102, RollSalt = 3103;

    /// <summary>The candidate for an object, by a hash of its position and the weights (-1 when the kind has none).</summary>
    public static int Choose(IReadOnlyList<GroundCandidate> list, float x, float y)
    {
        var roll = MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), ModelSalt);
        return PackTreeMath.Pick(list.Select(c => c.Weight).ToList(), roll);
    }

    /// <summary>
    /// The pack model as one object of the map, Godot axes. The cross-section is scaled so the model's horizontal radius is <paramref name="radiusM"/> times
    /// the object's scale (the code-built size, which the collider follows); a log with a length is stretched along the model's Z (its long axis) to exactly that
    /// length, so the model never passes the collider, and moved so that the middle of its length (not its origin) is the object's point.
    /// </summary>
    public static PackPlacement Place(PackGroundObject o, PackModelMesh model, int modelId, float radiusM, float yawDeg, bool stretchToLen)
    {
        var (s, ez) = LogSize(model, radiusM, o.Scale, stretchToLen);
        var stretch = 1f;
        if (stretchToLen && o.Len > 0 && ez > 1e-4f && s > 1e-6f) stretch = o.Len / (ez * s);
        var yaw = o.Rot + yawDeg * MathF.PI / 180f;
        var origin = new Vector3(o.X, o.Z, -o.Y);
        if (stretchToLen)
        {
            var (lo, hi) = model.Bounds;
            var (sin, cos) = MathF.SinCos(yaw);
            origin -= new Vector3(sin, 0, cos) * ((lo.Z + hi.Z) * 0.5f * s * stretch); // the model's Z axis points along sin, cos after the turn
        }
        return new PackPlacement(origin, yaw, s, modelId, stretch, PackKind.Object);
    }

    /// <summary>Scale of a model for the code-built radius at the object's scale, and the model's native extent along its Z (a log's length).</summary>
    static (float Scale, float ZExtent) LogSize(PackModelMesh model, float radiusM, float scale, bool log)
    {
        var (lo, hi) = model.Bounds;
        float ex = hi.X - lo.X, ey = hi.Y - lo.Y, ez = hi.Z - lo.Z;
        // a log's cross-section is X/Y (its length is Z); the others are as wide as the wider horizontal side
        var modelRadius = log ? MathF.Max(ex, ey) * 0.5f : MathF.Max(ex, ez) * 0.5f;
        return (modelRadius > 1e-4f ? radiusM * scale / modelRadius : scale, ez);
    }

    /// <summary>The size box of a log record, from the collider's own arithmetic (<see cref="GroundObjectColliders.LogBox"/>), so the drawn trunk and the collider-sized box agree.</summary>
    public static LogBox BoxOf(PackGroundObject o, GroundModel model)
    {
        var part = GroundObjectColliders.LogBox(new GroundObject(GroundKind.Log, o.X, o.Y, o.Z, o.Rot, o.Scale, o.Len, 0, 0), model);
        return new LogBox(part.HalfV, part.Z1 - o.Z);
    }

    /// <summary>
    /// Whether (and how) an upright <paramref name="model"/> lies down as a log of <paramref name="len"/> metres: a uniform scale that makes its height the length (inside
    /// <see cref="FallenConfig.ScaleMin"/>..<see cref="FallenConfig.ScaleMax"/>, else null), its trunk then no wider than the collider box (thinned across by up to
    /// <see cref="FallenConfig.StretchMax"/> against the length, else null), and the axis height that rests it on the ground with <see cref="FallenConfig.Sink"/> of its diameter below it.
    /// </summary>
    public static LaidFit? FitLaid(PackModelMesh model, float len, LogBox box, FallenConfig f)
    {
        var height = model.HeightM;
        if (height <= 1e-3f || len <= 0) return null;
        var along = len / height;
        if (along < f.ScaleMin || along > f.ScaleMax) return null;
        var d = 2f * MathF.Max(model.TrunkRadiusM, 0.01f); // trunk diameter per unit scale
        var room = MathF.Min(2f * box.HalfWidth, box.Top / (1f - f.Sink)); // widest trunk that is inside the box across and over the ground
        var cross = along;
        if (d * cross > room) cross = MathF.Max(room / d, along / f.StretchMax);
        if (d * cross > room * 1.001f) return null;
        return new LaidFit(cross, along / cross, d * cross * (0.5f - f.Sink));
    }

    /// <summary>How much a log model of native length <paramref name="modelLen"/> (already scaled to the girth) is stretched to cover <paramref name="seg"/> metres.</summary>
    public static float PieceStretch(float modelLen, float seg) => modelLen > 1e-4f ? seg / modelLen : 1f;

    /// <summary>
    /// The pack model(s) of one log record, Godot axes: whole fallen trees (an upright <see cref="GroundCandidate.Lay"/> model laid along the log, base at the thick end -u, the
    /// middle of its height at the log's midpoint) where one fits the record's length and collider box, else log models stretched at most <see cref="FallenConfig.StretchMax"/>,
    /// on a long record several end to end (the fewest that fit). All candidates that fit share the choice by weight and a hash of the position; when none fits, the log model
    /// nearest to its length is stretched to it all the same (the baked log must not vanish without a model), or the first laid model clamped if the kind has no log model.
    /// </summary>
    public static List<PackPlacement> PlaceLog(PackGroundObject o, IReadOnlyList<GroundCandidate> cands, IReadOnlyList<PackModelMesh> models, float radiusM, float yawDeg, FallenConfig f, LogBox box)
    {
        var logs = cands.Where(c => !c.Lay && c.Weight > 0).ToList();
        var lays = f.Enabled ? cands.Where(c => c.Lay && c.Weight > 0).ToList() : new List<GroundCandidate>();
        if (logs.Count == 0 && lays.Count == 0) return new List<PackPlacement>();
        if (o.Len <= 0 || !f.Enabled)
        {
            // the first slice: one model stretched to the length
            var pool = logs.Count > 0 ? logs : cands.Where(c => c.Weight > 0).ToList();
            var id = pool[PackTreeMath.Pick(pool.Select(c => c.Weight).ToList(), MeadowGen.Hash01((int)MathF.Round(o.X * 100f), (int)MathF.Round(o.Y * 100f), ModelSalt))].ModelId;
            return [Place(o, models[id], id, radiusM, yawDeg, true)];
        }

        float LogLen(GroundCandidate c) { var (s, ez) = LogSize(models[c.ModelId], radiusM, o.Scale, true); return s * ez; }
        // the fewest pieces for which some log model stays within the stretch limit
        int pieces = 0; var fitting = new List<GroundCandidate>();
        for (var n = 1; n <= f.MaxPieces && fitting.Count == 0; n++)
        {
            var seg = o.Len / n;
            fitting = logs.Where(c => { var st = PieceStretch(LogLen(c), seg); return st <= f.StretchMax && st >= 1f / f.StretchMax; }).ToList();
            pieces = n;
        }

        var options = new List<(GroundCandidate Cand, LaidFit? Fit)>();
        var weights = new List<float>();
        foreach (var c in lays)
            if (FitLaid(models[c.ModelId], o.Len, box, f) is { } fit) { options.Add((c, fit)); weights.Add(c.Weight); }
        var groupWeight = fitting.Sum(c => c.Weight) * (pieces > 1 ? f.PieceWeight : 1f);
        if (fitting.Count > 0 && groupWeight > 0) { options.Add((default, null)); weights.Add(groupWeight); }

        if (options.Count == 0) return Fallback(o, logs, lays, models, radiusM, yawDeg, f, LogLen);
        var roll = MeadowGen.Hash01((int)MathF.Round(o.X * 100f), (int)MathF.Round(o.Y * 100f), ModelSalt);
        var pick = options[PackTreeMath.Pick(weights, roll)];
        if (pick.Fit is { } laid) return [Laid(o, models[pick.Cand.ModelId], pick.Cand.ModelId, laid)];
        return Pieces(o, fitting, models, radiusM, yawDeg, pieces);
    }

    /// <summary>An upright model laid along the log: the base (thick end) toward -u, the middle of its height on the object's point, the axis lifted so it rests on the ground.</summary>
    static PackPlacement Laid(PackGroundObject o, PackModelMesh model, int id, LaidFit fit)
    {
        var (lo, hi) = model.Bounds;
        var along = fit.Cross * fit.Stretch; // scale along the trunk
        var mid = (lo.Y + hi.Y) * 0.5f * along;
        float ux = MathF.Cos(o.Rot), uy = MathF.Sin(o.Rot);
        var roll = (float)(MeadowGen.Hash01((int)MathF.Round(o.X * 100f), (int)MathF.Round(o.Y * 100f), RollSalt) * 2 * Math.PI); // which way the branches point
        // the tip about X sends model +Y to +Z, the turn of rot + 90 degrees sends +Z to u: the base lies at -u and the top at +u
        return new PackPlacement(new Vector3(o.X - ux * mid, o.Z + fit.Lift, -(o.Y - uy * mid)), o.Rot + MathF.PI / 2, fit.Cross, id, fit.Stretch, PackKind.Object, default, MathF.PI / 2, roll);
    }

    /// <summary>Log models end to end along the record, each covering <c>len / n</c>, the model of each piece chosen by a hash of the piece's own position.</summary>
    static List<PackPlacement> Pieces(PackGroundObject o, List<GroundCandidate> fitting, IReadOnlyList<PackModelMesh> models, float radiusM, float yawDeg, int n)
    {
        var result = new List<PackPlacement>(n);
        var seg = o.Len / n;
        float ux = MathF.Cos(o.Rot), uy = MathF.Sin(o.Rot);
        for (var k = 0; k < n; k++)
        {
            var t = -o.Len / 2 + seg * (k + 0.5f);
            var piece = o with { X = o.X + ux * t, Y = o.Y + uy * t, Len = seg };
            var roll = MeadowGen.Hash01((int)MathF.Round(piece.X * 100f), (int)MathF.Round(piece.Y * 100f), PieceSalt);
            var id = fitting[PackTreeMath.Pick(fitting.Select(c => c.Weight).ToList(), roll)].ModelId;
            result.Add(Place(piece, models[id], id, radiusM, yawDeg, true));
        }
        return result;
    }

    static List<PackPlacement> Fallback(PackGroundObject o, List<GroundCandidate> logs, List<GroundCandidate> lays, IReadOnlyList<PackModelMesh> models, float radiusM, float yawDeg, FallenConfig f, Func<GroundCandidate, float> logLen)
    {
        if (logs.Count == 0)
        {
            // no log model at all: the first upright model laid down as far as the scale span goes, its girth thinned within the stretch limit
            var c = lays[0];
            var m = models[c.ModelId];
            var along = Math.Clamp(o.Len / MathF.Max(m.HeightM, 1e-3f), f.ScaleMin, f.ScaleMax);
            var fit = new LaidFit(along, 1f, 2f * MathF.Max(m.TrunkRadiusM, 0.01f) * along * (0.5f - f.Sink));
            return [Laid(o, m, c.ModelId, fit)];
        }
        GroundCandidate best = logs[0]; var bestPieces = 1; var bestOff = float.MaxValue;
        foreach (var c in logs)
            for (var n = 1; n <= f.MaxPieces; n++)
            {
                var off = MathF.Abs(MathF.Log(PieceStretch(logLen(c), o.Len / n)));
                if (off < bestOff) { bestOff = off; best = c; bestPieces = n; }
            }
        return Pieces(o, [best], models, radiusM, yawDeg, bestPieces);
    }
}

// ---- undergrowth ----

/// <summary>A trunk in map metres, for clearing and for the density at tree feet.</summary>
public readonly record struct TreeFoot(float X, float Y, float Scale);

/// <summary>One kind of undergrowth resolved against the loaded models.</summary>
public sealed record UndergrowthKind(float PerM2, float ScaleMin, float ScaleMax, float Clump, float Foot, int[] ModelIds, float[] Weights, int[] Tris, bool Far = false);

/// <summary>The kinds that grow on the cells of one meadow mix in a tile; <see cref="SaltBase"/> keeps the hash of its pieces apart from another mix's (0 for the first, so a one-mix tile is as it was).</summary>
public sealed record UndergrowthSet(ForestMask Cells, IReadOnlyList<UndergrowthKind> Kinds, int SaltBase);

/// <summary>The cells of one meadow mix in a tile: global cell indices (floor of metres over cell size), a bool per cell of the box starting at (Gx0, Gy0).</summary>
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
        => Tile(cfg, [new UndergrowthSet(forest, kinds, 0)], x0, y0, x1, y1, cellM, trees, groundZ, trisBudget, out triangles);

    /// <summary>
    /// <see cref="Tile(UndergrowthConfig, IReadOnlyList{UndergrowthKind}, float, float, float, float, float, ForestMask, IReadOnlyList{TreeFoot}, Func{float, float, float}, int, out int)"/>
    /// for several meadow mixes at once: each set grows its kinds on its own cells (a cell is in one mix, so two sets never share one), and the triangle budget is
    /// the tile's, shared by all of them.
    /// </summary>
    public static List<PackPlacement> Tile(UndergrowthConfig cfg, IReadOnlyList<UndergrowthSet> sets, float x0, float y0, float x1, float y1, float cellM,
        IReadOnlyList<TreeFoot> trees, Func<float, float, float> groundZ, int trisBudget, out int triangles)
    {
        var all = new List<(PackPlacement P, int Tris, double Prio)>();
        int gx0 = (int)MathF.Floor(x0 / cellM), gx1 = (int)MathF.Ceiling(x1 / cellM), gy0 = (int)MathF.Floor(y0 / cellM), gy1 = (int)MathF.Ceiling(y1 / cellM);
        var lattice = cfg.ClumpM / cellM;
        foreach (var set in sets)
        {
            var forest = set.Cells; var kinds = set.Kinds;
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
                        var kb = Salt + set.SaltBase + ki * 100;
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
                            all.Add((new PackPlacement(new Vector3(x, z, -y), rot, scale, k.ModelIds[pick], Kind: k.Far ? PackKind.UndergrowthFar : PackKind.Undergrowth), k.Tris[pick], MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), Salt + 7)));
                        }
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
