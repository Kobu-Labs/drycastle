using System.Text.Json;

namespace Drycastle.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link (with ClutterData.cs), so parsing, variant choice and placeholders can be tested.

/// <summary>What the game draws one crop kind with (crops.json <c>kinds</c>): a plant of the field's crop or a sparse artifact.</summary>
/// <param name="Asset">An asset id of assets/manifest.json (e.g. the generated rapeseed plant); wins over <paramref name="Synty"/>.</param>
/// <param name="Synty">Model names of the private Synty POLYGON Farm pack (<c>pack:synty-farm/&lt;name&gt;</c>); several = variants picked per instance.</param>
public sealed record CropKind(string Role, string? Asset, IReadOnlyList<string> Synty)
{
    public bool IsPlant => Role == "plant";
}

/// <summary>The instances of one kind in one chunk. Rows as in clutter.json without a tint: x east, y north, z ground height, rot, scale.</summary>
public sealed record CropGroup(int ChunkX, int ChunkY, string Kind, IReadOnlyList<ClutterInstance> Instances);

/// <summary>
/// crops.json (map manifest <c>files.crops</c>; only maps with a crop patch): {"format_version":1, "axes":"...", "chunk_size_m":250,
/// "kinds":{"crop_wheat":{"role":"plant","synty":["SM_Prop_Plant_Wheat_Optimised_01"]}},
/// "chunks":{"3_-1":{"crop_wheat":[[x,y,z,rot,scale],...]}}}. Kinds not listed under "kinds" are skipped.
/// </summary>
public sealed class CropDoc
{
    public const int SupportedFormat = 1;

    public int FormatVersion { get; init; }
    public IReadOnlyDictionary<string, CropKind> Kinds { get; init; } = new Dictionary<string, CropKind>();
    public IReadOnlyList<CropGroup> Groups { get; init; } = Array.Empty<CropGroup>();
    public int InstanceCount => Groups.Sum(g => g.Instances.Count);

    /// <summary>Parses the file; throws <see cref="FormatException"/> on a format version it does not know or a malformed entry.</summary>
    public static CropDoc Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("format_version", out var v) && v.TryGetInt32(out var iv) ? iv : 0;
        if (version != SupportedFormat) throw new FormatException($"crops.json format_version {version}, expected {SupportedFormat}");
        var kinds = new Dictionary<string, CropKind>();
        if (root.TryGetProperty("kinds", out var ks))
            foreach (var k in ks.EnumerateObject())
            {
                var role = k.Value.TryGetProperty("role", out var r) && r.GetString() == "plant" ? "plant" : "artifact";
                var asset = k.Value.TryGetProperty("asset", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s ? s : null;
                var synty = new List<string>();
                if (k.Value.TryGetProperty("synty", out var sy) && sy.ValueKind == JsonValueKind.Array)
                    foreach (var n in sy.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name) synty.Add(name);
                kinds[k.Name] = new CropKind(role, asset, synty);
            }
        var groups = new List<CropGroup>();
        if (root.TryGetProperty("chunks", out var chunks))
            foreach (var c in chunks.EnumerateObject())
            {
                var parts = c.Name.Split('_');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var ix) || !int.TryParse(parts[1], out var iy))
                    throw new FormatException($"crops.json: bad chunk key '{c.Name}'");
                foreach (var kind in c.Value.EnumerateObject())
                {
                    if (!kinds.ContainsKey(kind.Name)) continue;
                    var list = new List<ClutterInstance>(kind.Value.GetArrayLength());
                    foreach (var e in kind.Value.EnumerateArray())
                    {
                        if (e.GetArrayLength() < 5) throw new FormatException($"crops.json: chunk {c.Name} {kind.Name}: entry needs x,y,z,rot,scale");
                        list.Add(new ClutterInstance(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle(), e[4].GetSingle()));
                    }
                    if (list.Count > 0) groups.Add(new CropGroup(ix, iy, kind.Name, list));
                }
            }
        return new CropDoc { FormatVersion = version, Kinds = kinds, Groups = groups };
    }
}

/// <summary>Distances and per-instance choices of crop drawing; one place for the numbers.</summary>
public static class CropSettings
{
    /// <summary>The asset id of a Farm pack model (the private manifest written by scripts/private_pack_glb.gd).</summary>
    public static string SyntyAsset(string model) => "pack:synty-farm/" + model;

    /// <summary>Distance (metres) at which a plant is gone, before the cell allowance and the clutter scale: a field is read from further than a tuft.</summary>
    public const float PlantRangeM = 110f;
    /// <summary>The same for an artifact (bale, tractor, scarecrow): few and big.</summary>
    public const float ArtifactRangeM = 220f;

    public static float RangeOf(CropKind kind) => kind.IsPlant ? PlantRangeM : ArtifactRangeM;

    /// <summary>Which of <paramref name="count"/> variants an instance wears: a hash of its position, so it is the same on every client and every load.</summary>
    public static int Variant(ClutterInstance i, int count)
    {
        if (count <= 1) return 0;
        unchecked
        {
            var h = (uint)((int)MathF.Round(i.X * 8f) * 73856093) ^ (uint)((int)MathF.Round(i.Y * 8f) * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (int)(h % (uint)count);
        }
    }
}

/// <summary>The stand-in for a kind whose model is not there (a player without the paid pack): a flat-coloured cone or box.</summary>
public readonly record struct CropPlaceholder(bool Cone, float Width, float Height, string Colour)
{
    /// <summary>By kind name, else by role (plants a green cone, artifacts a brown box).</summary>
    public static CropPlaceholder For(string kind, CropKind spec) => Table.TryGetValue(kind, out var p) ? p : spec.IsPlant ? new(true, 0.4f, 0.7f, "#6f9a3c") : new(false, 1.0f, 1.0f, "#8a6a48");

    static readonly IReadOnlyDictionary<string, CropPlaceholder> Table = new Dictionary<string, CropPlaceholder>
    {
        ["crop_wheat"] = new(true, 0.35f, 0.9f, "#d2b455"),
        ["crop_corn"] = new(true, 0.45f, 2.0f, "#4e7d34"),
        ["crop_potato"] = new(true, 0.55f, 0.45f, "#4f7f38"),
        ["crop_rapeseed"] = new(true, 0.45f, 0.8f, "#c4cc3c"),
        ["art_hay_bale_round"] = new(false, 1.6f, 1.4f, "#c9a748"),
        ["art_hay_bale_square"] = new(false, 0.9f, 0.5f, "#c9a748"),
        ["art_hay_pile"] = new(true, 2.4f, 1.8f, "#bf9f45"),
        ["art_baler"] = new(false, 2.4f, 1.8f, "#b24a2c"),
        ["art_scarecrow"] = new(true, 0.5f, 1.9f, "#7a5a34"),
        ["art_tractor"] = new(false, 2.2f, 2.4f, "#3f7a3a"),
        ["art_potato_box"] = new(false, 0.9f, 0.6f, "#9a7a4a"),
        ["art_sign_potatoes"] = new(false, 0.8f, 1.4f, "#d9d2b8"),
        ["art_grain_bag"] = new(false, 0.5f, 0.8f, "#cdbf94"),
        ["art_sprinkler"] = new(false, 0.4f, 0.7f, "#8d9aa3"),
        ["art_tyre"] = new(false, 0.9f, 0.3f, "#2b2b2b"),
        ["art_plough"] = new(false, 1.6f, 1.2f, "#8c4a2e"),
        ["art_wood_stack"] = new(false, 1.8f, 1.2f, "#8a6338"),
    };
}
