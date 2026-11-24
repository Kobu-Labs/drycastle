using System.Text.Json;

namespace Drycastle.MapLoader.Vegetation;

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
    /// <summary>The side of a chunk (<c>chunk_size_m</c>; 250 when the file does not say).</summary>
    public float ChunkSizeM { get; init; } = 250f;
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
        var chunkM = root.TryGetProperty("chunk_size_m", out var cs) && cs.TryGetSingle(out var csm) && csm > 0f ? csm : 250f;
        return new CropDoc { FormatVersion = version, ChunkSizeM = chunkM, Kinds = kinds, Groups = groups };
    }
}

/// <summary>Distances and per-instance choices of crop drawing; one place for the numbers.</summary>
public static class CropSettings
{
    /// <summary>The asset id of a Farm pack model (the private manifest written by scripts/packs/private_pack_glb.gd).</summary>
    public static string SyntyAsset(string model) => "pack:synty-farm/" + model;

    /// <summary>Distance (metres) at which a plant is gone, before the cell allowance and the clutter scale: a field is read from further than a tuft.</summary>
    public const float PlantRangeM = 110f;
    /// <summary>The band inside <see cref="PlantRangeM"/> over which the plants thin out one by one, so a field has no edge where they end (crop_plant.gdshader).</summary>
    public const float PlantFadeM = 50f;
    /// <summary>The share of the band one plant takes to grow from nothing to whole.</summary>
    public const float PlantGrow = 0.3f;

    /// <summary>
    /// How much of a plant stands, 0 (gone) to 1 (whole), <paramref name="distance"/> metres from the camera: whole inside <c>radius - band</c>, gone at the radius,
    /// in between each plant starts at a distance of its own (<paramref name="threshold"/> in [0, 1), a hash of where it stands). Mirrors crop_plant.gdshader.
    /// </summary>
    public static float Grow(float distance, float threshold, float radius, float band, float grow = PlantGrow)
    {
        band = MathF.Max(band, 0.001f);
        var far = distance >= radius ? 1f : Math.Clamp((distance - (radius - band)) / band, 0f, 1f);
        var span = MathF.Max(grow, 0.001f);
        var g = Math.Clamp((span + (1f - span) * threshold - far) / span, 0f, 1f);
        return g * g * (3f - 2f * g);
    }

    /// <summary>The far look of a field is a sheet over it with a picture of the crop (MapLoader.CropSheet.cs). Side of a sheet cell (m): the planted ground is covered in cells this wide.</summary>
    public const float SheetCellM = 4f;
    /// <summary>Side of the ground one picture covers (m), then it repeats.</summary>
    public const float SheetTileM = 12f;
    /// <summary>Side of the picture (px).</summary>
    public const int SheetPx = 512;
    /// <summary>The sheet floats this share of the plants' height above the ground, at most <see cref="SheetLiftMaxM"/>.</summary>
    public const float SheetLift = 0.6f;
    public const float SheetLiftMaxM = 0.5f;

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

/// <summary>
/// Where a chunk's plants of one kind stand, in cells of <see cref="CropSettings.SheetCellM"/>: what the far sheet of the field is cut from. Pure, so it is tested.
/// </summary>
public sealed class CropSheetGrid
{
    public float CellM { get; }
    /// <summary>Cell (floor of map x, y over the cell side) -> how many plants stand in it and the sum of their ground heights.</summary>
    public Dictionary<(int X, int Y), (int Count, float ZSum)> Cells { get; } = new();

    public CropSheetGrid(IReadOnlyList<ClutterInstance> plants, float cellM = CropSettings.SheetCellM)
    {
        CellM = cellM;
        foreach (var i in plants)
        {
            var key = ((int)MathF.Floor(i.X / cellM), (int)MathF.Floor(i.Y / cellM));
            Cells[key] = Cells.TryGetValue(key, out var c) ? (c.Count + 1, c.ZSum + i.Z) : (1, i.Z);
        }
    }

    /// <summary>The count of a cell that is full of plants: the upper quartile of the cells' counts (edges and tramlines hold fewer). 0 when there is none.</summary>
    public static float Full(IEnumerable<int> counts)
    {
        var sorted = counts.OrderBy(c => c).ToList();
        return sorted.Count == 0 ? 0f : sorted[Math.Min(sorted.Count - 1, sorted.Count * 3 / 4)];
    }

    /// <summary>
    /// The grid corner at (x, y) (the low corner of cell (x, y)): the ground height there (the mean of the plants in the four cells around it) and how full
    /// of plants the ground is (the mean of the four cells' counts over <paramref name="full"/>, each at most 1; an empty cell counts 0, so the field's edge feathers).
    /// </summary>
    public (float Z, float Share) Corner(int x, int y, float full)
    {
        var n = 0; var z = 0f; var share = 0f;
        for (var dx = -1; dx <= 0; dx++)
            for (var dy = -1; dy <= 0; dy++)
                if (Cells.TryGetValue((x + dx, y + dy), out var c))
                {
                    n += c.Count; z += c.ZSum;
                    share += MathF.Min(1f, c.Count / MathF.Max(full, 1f));
                }
        return (n > 0 ? z / n : 0f, share / 4f);
    }
}
