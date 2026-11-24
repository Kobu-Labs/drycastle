using System.Numerics;
using System.Text.Json;
using Drycastle.MapLoader.Vegetation;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.MapLoader.Trees;

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
    /// <summary>Ground object log kind only: an upright model (a dead tree) that is laid down along the log instead of a model that already lies (see <see cref="FallenConfig"/>).</summary>
    public bool Lay { get; set; }
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
    public float NearM { get; set; } = 150f;
    public float FadeM { get; set; } = 30f;
    public float TileM { get; set; } = 48f;
    public float AheadM { get; set; } = 32f;
    public int MaxInFlight { get; set; } = 4;
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
    public PackColourConfig Colour { get; set; } = new();
    /// <summary>The far trees as cards of the pack models (#362).</summary>
    public PackCardConfig Cards { get; set; } = new();
    /// <summary>Tiles are built around the stretch from the focus to where it will be in this many seconds at its present speed, so a car does not outrun them (0 = around the focus only).</summary>
    public float LeadS { get; set; } = 2f;
    /// <summary>The most that stretch may be (m).</summary>
    public float LeadMaxM { get; set; } = 100f;

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
        c.Colour.Validate();
        c.Cards.Validate();
        if (c.LeadS < 0 || c.LeadMaxM < 0) throw new DataFileException("pack_trees.json: lead_s and lead_max_m must not be negative");
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

/// <summary>A clearly different tree colour: a multiplier on the foliage texels (1 = unchanged, each channel 0..2) and its share among the accents.</summary>
public sealed class PackColourAccent
{
    public string Name { get; set; } = "";
    public float[] Tint { get; set; } = { 1f, 1f, 1f };
    public float Weight { get; set; } = 1f;
}

/// <summary>
/// Colour of the pack trees (the file's <c>colour</c> section): the atlas shows every tree of a model in the same few greens, the baked trees had several. The mesher
/// writes a tint per tree into the vertex colour (hash of its position, <see cref="PackColourMath.Tint"/>) and the shader applies it to foliage texels only, with
/// <see cref="Saturation"/> and <see cref="Gain"/> on top. Trunks, rocks and bark are not touched (the shader weighs by how green a texel is).
/// </summary>
public sealed class PackColourConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Saturation of foliage (1 = the atlas as it is).</summary>
    public float Saturation { get; set; } = 1.25f;
    /// <summary>Brightness of foliage (1 = as it is).</summary>
    public float Gain { get; set; } = 1.05f;
    /// <summary>Most a tree's green moves toward yellow-green (+) or blue-green (-): red goes up and blue down by this share, or the reverse.</summary>
    public float HueShift { get; set; } = 0.2f;
    /// <summary>Most a tree's foliage is lighter or darker than the atlas (share).</summary>
    public float Value { get; set; } = 0.15f;
    /// <summary>Share of trees that take one of the <see cref="Accents"/> instead of the hue shift (a position hash).</summary>
    public float AccentShare { get; set; } = 0.15f;
    /// <summary>How much of all this the undergrowth gets (0 = none, 1 = as the trees): ferns and shrubs vary, but gently.</summary>
    public float UndergrowthScale { get; set; } = 0.5f;
    /// <summary>A texel is foliage by how much greener than red and blue it is (g - max(r, b)) / g: none below <see cref="LeafLo"/>, fully above <see cref="LeafHi"/>.</summary>
    public float LeafLo { get; set; } = 0.01f;
    public float LeafHi { get; set; } = 0.08f;
    public List<PackColourAccent> Accents { get; set; } = new();
    /// <summary>Foliage takes the colour of the baked tree it stands in for (trees.json <c>colour</c>), so a tree is the same green on both sides of the swap; the
    /// atlas only shades it. This is the colour of a tree whose record has none (#rrggbb).</summary>
    public string TreeColour { get; set; } = "#2f6a3a";
    /// <summary>The greens undergrowth takes (it has no baked twin), by a position hash.</summary>
    public List<string> UndergrowthColours { get; set; } = new() { "#3b7a3a" };
    /// <summary>The atlas' foliage brightness (linear luma) that shows the tree colour as it is; lighter and darker texels shade it within <see cref="ShadeMin"/>..<see cref="ShadeMax"/>.</summary>
    public float LeafRef { get; set; } = 0.2f;
    public float ShadeMin { get; set; } = 0.75f;
    public float ShadeMax { get; set; } = 1.25f;

    public void Validate()
    {
        if (!(Saturation >= 0) || !(Gain > 0)) throw new DataFileException("pack_trees.json: colour saturation must not be negative and gain above 0");
        if (HueShift < 0 || HueShift > 0.9f || Value < 0 || Value > 0.9f) throw new DataFileException("pack_trees.json: colour hue_shift and value must be within 0 and 0.9");
        if (AccentShare < 0 || AccentShare > 1 || UndergrowthScale < 0 || UndergrowthScale > 1) throw new DataFileException("pack_trees.json: colour accent_share and undergrowth_scale must be within 0 and 1");
        if (LeafLo < 0 || !(LeafHi > LeafLo)) throw new DataFileException("pack_trees.json: colour leaf_hi must be above leaf_lo, which is not negative");
        if (!(LeafRef > 0) || !(ShadeMin > 0) || ShadeMax < ShadeMin) throw new DataFileException("pack_trees.json: colour leaf_ref and shade_min must be above 0, shade_max not below shade_min");
        if (!PackColourMath.TryHex(TreeColour, out _) || UndergrowthColours.Count == 0 || UndergrowthColours.Any(c => !PackColourMath.TryHex(c, out _)))
            throw new DataFileException("pack_trees.json: colour tree_colour and undergrowth_colours must be #rrggbb (at least one undergrowth colour)");
        foreach (var a in Accents)
            if (a.Tint.Length != 3 || a.Tint.Any(v => !(v >= 0 && v <= 2)) || a.Weight < 0)
                throw new DataFileException($"pack_trees.json: colour accent '{a.Name}' needs tint [r, g, b] within 0 and 2 and a weight of 0 or more");
    }
}

/// <summary>The per-tree tint (#287): the same position gives the same colour on every client and every run.</summary>
public static class PackColourMath
{
    const int Salt = 3003;

    /// <summary>
    /// The multiplier on the foliage texels of the thing standing at map position (x, y). A hue roll moves it toward yellow-green or blue-green (red and blue
    /// opposite), a value roll lightens or darkens it, and a share of positions (<see cref="PackColourConfig.AccentShare"/>) take a weighted accent instead of the hue
    /// shift. <paramref name="amount"/> (1 = trees, <see cref="PackColourConfig.UndergrowthScale"/> = undergrowth) scales every departure from 1, accents' share included.
    /// Each channel stays within 0..2 (it is stored halved in an 8-bit vertex colour).
    /// </summary>
    public static Vector3 Tint(float x, float y, PackColourConfig c, float amount = 1f)
    {
        if (!c.Enabled || amount <= 0) return Vector3.One;
        int ix = (int)MathF.Round(x * 100f), iy = (int)MathF.Round(y * 100f);
        var hue = (float)(2 * MeadowGen.Hash01(ix, iy, Salt) - 1) * c.HueShift;
        var value = 1f + (float)(2 * MeadowGen.Hash01(ix, iy, Salt + 1) - 1) * c.Value;
        var tint = new Vector3(1f + hue, 1f, 1f - hue);
        if (MeadowGen.Hash01(ix, iy, Salt + 2) < c.AccentShare * amount && c.Accents.Count > 0)
        {
            var pick = PackTreeMath.Pick(c.Accents.Select(a => a.Weight).ToList(), MeadowGen.Hash01(ix, iy, Salt + 3));
            if (pick >= 0) tint = new Vector3(c.Accents[pick].Tint[0], c.Accents[pick].Tint[1], c.Accents[pick].Tint[2]);
        }
        tint = Vector3.Lerp(Vector3.One, tint, amount) * (1f + (value - 1f) * amount);
        return Vector3.Clamp(tint, Vector3.Zero, new Vector3(2f));
    }

    /// <summary>#rrggbb as channels 0..1 (the bytes the pipeline bakes into the far tree's vertex colour, so the two match exactly).</summary>
    public static bool TryHex(string? hex, out Vector3 rgb)
    {
        rgb = default;
        if (hex is not { Length: 7 } || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        rgb = new Vector3((v >> 16 & 255) / 255f, (v >> 8 & 255) / 255f, (v & 255) / 255f);
        return true;
    }

    /// <summary>The foliage colour of the tree at (x, y): the baked tree's colour (the config's when the record has none) times the small per-tree variation.</summary>
    public static Vector3 Foliage(Vector3 baked, float x, float y, PackColourConfig c)
    {
        if (!c.Enabled) return Vector3.One;
        if (baked == default) TryHex(c.TreeColour, out baked);
        return Vector3.Clamp(baked * Tint(x, y, c), Vector3.Zero, new Vector3(2f));
    }

    /// <summary>A scattered piece of undergrowth (Godot axes) marked as such, for the shader's own cut radius, in one of the undergrowth greens, varied gently.</summary>
    public static PackPlacement Undergrowth(PackPlacement p, PackColourConfig c)
    {
        float x = p.Base.X, y = -p.Base.Z;
        if (!c.Enabled) return p with { Kind = p.Kind == PackKind.UndergrowthFar ? PackKind.UndergrowthFar : PackKind.Undergrowth, Tint = Vector3.One };
        var pick = (int)(MeadowGen.Hash01((int)MathF.Round(x * 100f), (int)MathF.Round(y * 100f), Salt + 9) * c.UndergrowthColours.Count);
        TryHex(c.UndergrowthColours[Math.Min(pick, c.UndergrowthColours.Count - 1)], out var green);
        return p with { Kind = p.Kind == PackKind.UndergrowthFar ? PackKind.UndergrowthFar : PackKind.Undergrowth, Tint = Vector3.Clamp(green * Tint(x, y, c, c.UndergrowthScale), Vector3.Zero, new Vector3(2f)) };
    }
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
public readonly record struct PackTreeTag(string Stand, bool Edge, Vector3 Colour = default)
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
                var colour = default(Vector3);
                if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("colour", out var c) && c.ValueKind == JsonValueKind.String) PackColourMath.TryHex(c.GetString(), out colour);
                tags[i++] = new PackTreeTag(stand, edge, colour);
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

/// <summary>
/// Which pack model each tree's solids follow (#375): the choice the client draws by (<see cref="PackTreeMath.ModelOf"/>), made from tree_limbs.json's models and
/// heights instead of the loaded pack, so the server and a client without the pack make it too.
/// </summary>
public static class PackTreeLimbs
{
    /// <summary>Sets <see cref="TreeInstance.Model"/> of every tree (file order, as <paramref name="treesJson"/>) and returns the tree model carrying the limb table.</summary>
    public static TreeModel Dress(List<TreeInstance> trees, TreeModel model, string treesJson, PackTreeConfig cfg, IReadOnlyList<LimbModel> limbs)
    {
        var ids = new Dictionary<(string, string), int>();
        for (var i = 0; i < limbs.Count; i++) ids[(limbs[i].Pack, limbs[i].Name)] = i;
        var sets = PackModelSets.Build(cfg, e => ids.TryGetValue((e.Pack, e.Name), out var i) ? new PackCandidate(i, limbs[i].HeightM, e.Weight, e.Bulk) : null);
        var tags = PackTreeTags.Parse(treesJson, trees.Count);
        for (var i = 0; i < trees.Count; i++)
            trees[i] = trees[i] with { Model = PackTreeMath.ModelOf(trees[i], model, tags[i], sets, cfg) };
        return model with { Limbs = limbs };
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

    /// <summary>The height of the code-built tree above its foot (the top of its highest collision solid, <see cref="TreeColliders.CodeParts"/>).</summary>
    public static float CodeHeight(TreeInstance t, TreeModel m) => TreeColliders.CodeHeight(t, m);

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
        return PlaceFrom(t, m, t.Cone ? set.Cone : set.Box, set.BulkShare, cfg, tag.Colour);
    }

    /// <summary>The model a tree is drawn as (the candidate's id), by the same choice as <see cref="Place(TreeInstance, TreeModel, PackTreeTag, PackModelSets, PackTreeConfig)"/>; -1 when its shape has none.</summary>
    public static int ModelOf(TreeInstance t, TreeModel m, PackTreeTag tag, PackModelSets sets, PackTreeConfig cfg)
    {
        var set = sets.For(tag, cfg.EdgeShare, EdgeRoll(t.X, t.Y));
        var list = t.Cone ? set.Cone : set.Box;
        var pick = Choose(list, CodeHeight(t, m), Roll(t.X, t.Y), cfg.ScaleMin, cfg.ScaleMax, set.BulkShare);
        return pick < 0 ? -1 : list[pick].ModelId;
    }

    static PackPlacement? PlaceFrom(TreeInstance t, TreeModel m, IReadOnlyList<PackCandidate> list, float bulkShare, PackTreeConfig cfg, Vector3 baked = default)
    {
        var h = CodeHeight(t, m);
        var pick = Choose(list, h, Roll(t.X, t.Y), cfg.ScaleMin, cfg.ScaleMax, bulkShare);
        if (pick < 0) return null;
        var model = list[pick];
        return new PackPlacement(new Vector3(t.X, t.Z, -t.Y), t.Rot, model.HeightM > 1e-4f ? h / model.HeightM : 1f, model.ModelId, Tint: PackColourMath.Foliage(baked, t.X, t.Y, cfg.Colour));
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
    /// linear between. It is compared with the tree's own <see cref="Threshold"/> (<see cref="IsBaked"/>), so over the band the trees swap whole, one by one.
    /// Mirrors <c>tree_far</c> in tree_cut.gdshaderinc.
    /// </summary>
    public static float FarShare(float dist, float radius, float band)
    {
        if (dist >= radius) return 1f;
        band = MathF.Max(band, 0.001f);
        return Math.Clamp((dist - (radius - band)) / band, 0f, 1f);
    }

    /// <summary>
    /// The tree's own threshold in [0, 1): a hash of its world base point on a 1 m grid shifted off the centimetre lattice (interleaved-gradient noise of the cell).
    /// Mirrors <c>tree_threshold</c> in tree_cut.gdshaderinc: the baked mesh and the pack tile both derive it from the same world base, so they agree.
    /// </summary>
    public static float Threshold(float worldX, float worldZ)
    {
        static float Fract(float v) => v - MathF.Floor(v);
        var cx = MathF.Floor(worldX + 0.4137f);
        var cz = MathF.Floor(worldZ + 0.2719f);
        return Fract(52.9829189f * Fract(cx * 0.06711056f + cz * 0.00583715f));
    }

    /// <summary>
    /// Whether the baked tree stands (true) or the pack tree (false): the whole tree switches when its far share passes its threshold, so exactly one of the two is
    /// drawn, never a mix. Far 0 is always pack, far 1 always baked. Mirrors <c>tree_is_baked</c> in tree_cut.gdshaderinc.
    /// </summary>
    public static bool IsBaked(float far, float worldX, float worldZ) => far > Threshold(worldX, worldZ);

    /// <summary>
    /// How much of an undergrowth piece stands, 0 (not there) to 1 (whole): it has no baked twin to swap with, so it grows out of the ground. It starts when the far share
    /// falls under the piece's threshold and is whole once the share has fallen <paramref name="grow"/> further; thresholds are squeezed into [grow, 1) so every piece
    /// is whole at far share 0. Mirrors <c>tree_grow</c> in tree_cut.gdshaderinc.
    /// </summary>
    public static float Grow(float far, float threshold, float grow)
    {
        grow = MathF.Max(grow, 0.001f);
        var g = Math.Clamp((grow + (1f - grow) * threshold - far) / grow, 0f, 1f);
        return g * g * (3f - 2f * g);
    }

    /// <summary>
    /// Where the focus will be in <paramref name="leadS"/> seconds at <paramref name="velocity"/> (m/s on the ground), no further than <paramref name="maxM"/>:
    /// tiles are built around the stretch from the focus to there.
    /// </summary>
    public static Vector2 Lead(Vector2 focus, Vector2 velocity, float leadS, float maxM)
    {
        var off = velocity * leadS;
        var len = off.Length();
        if (!(len > 1e-3f) || maxM <= 0) return focus;
        return focus + off * (MathF.Min(len, maxM) / len);
    }

    /// <summary>
    /// The focus' speed on the ground, smoothed: follows the step from <paramref name="last"/> to <paramref name="now"/> over <paramref name="dt"/> with a half-second
    /// lag. A jump faster than <paramref name="maxSpeed"/> is a teleport (a spawn, a camera cut), not a speed: it resets to 0.
    /// </summary>
    public static Vector2 Velocity(Vector2 smoothed, Vector2 last, Vector2 now, float dt, float maxSpeed = 150f)
    {
        if (!(dt > 1e-5f)) return smoothed;
        var v = (now - last) / dt;
        if (v.Length() > maxSpeed) return Vector2.Zero;
        return Vector2.Lerp(smoothed, v, 1f - MathF.Exp(-dt * 4f));
    }

    /// <summary>Distance from the stretch (ax, ay)-(bx, by) to the box, sampled every half box side (0 when a sample is inside).</summary>
    public static float DistToBoxAlong(float ax, float ay, float bx, float by, float x0, float y0, float x1, float y1)
    {
        var len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        var steps = Math.Clamp((int)MathF.Ceiling(len / MathF.Max(0.5f * MathF.Min(x1 - x0, y1 - y0), 1f)), 1, 64);
        var best = DistToBox(ax, ay, x0, y0, x1, y1);
        for (var i = 1; i <= steps && len > 0; i++)
        {
            var t = i / (float)steps;
            best = MathF.Min(best, DistToBox(ax + (bx - ax) * t, ay + (by - ay) * t, x0, y0, x1, y1));
        }
        return best;
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

    readonly Lazy<float> _trunkRadius;
    public PackModelMesh() => _trunkRadius = new Lazy<float>(ComputeTrunkRadius);

    /// <summary>
    /// The trunk's radius of an upright model (m at scale 1): the 30th percentile of the distance from the trunk's axis (the median position) of the vertices between 10% and 50%
    /// of the height, above the root flare and below the crown. A dead tree's branch vertices lie far from the axis and a median would take them for trunk; a low percentile reads
    /// the trunk itself. A log lying on the ground needs it for how high its axis rides and whether it fits the collider box.
    /// </summary>
    public float TrunkRadiusM => _trunkRadius.Value;

    float ComputeTrunkRadius()
    {
        var (lo, hi) = Bounds;
        float h = hi.Y - lo.Y, y0 = lo.Y + 0.1f * h, y1 = lo.Y + 0.5f * h;
        if (h <= 1e-4f) return 0f;
        var pts = new List<Vector2>();
        foreach (var s in Surfaces) foreach (var v in s.Vertices) if (v.Y >= y0 && v.Y <= y1) pts.Add(new Vector2(v.X, v.Z));
        if (pts.Count == 0) return 0f;
        static float Percentile(List<float> l, float q) { l.Sort(); return l[(int)(q * (l.Count - 1))]; }
        var axis = new Vector2(Percentile(pts.Select(p => p.X).ToList(), 0.5f), Percentile(pts.Select(p => p.Y).ToList(), 0.5f));
        return Percentile(pts.Select(p => Vector2.Distance(p, axis)).ToList(), 0.3f);
    }

    (Vector3, Vector3) ComputeBounds()
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var s in Surfaces) foreach (var v in s.Vertices) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        return Surfaces.Count == 0 ? (Vector3.Zero, Vector3.Zero) : (lo, hi);
    }
}

/// <summary>What a placement is: decides how the shader cuts it (tree_cut, or undergrowth's own radius) and whether it takes the tint (ground objects do not). Stored in the vertex colour's alpha.</summary>
public enum PackKind : byte
{
    Tree = 0, Object = 1, Undergrowth = 2,
    /// <summary>Undergrowth of a kind marked <c>far</c> (saplings, shrubs, bushes): big enough to be seen appearing, so it has a longer radius of its own.</summary>
    UndergrowthFar = 3,
}

/// <summary>
/// A pack model to draw (tree, ground object, undergrowth): its base (Godot axes), turn about the up axis, uniform scale, the model's id and a stretch on top of the scale
/// (along the model's Z, a log's length; along its Y when it is laid down; 1 = none). <paramref name="Tint"/> is the foliage multiplier (default = none, see
/// <see cref="PackColourMath"/>). A <paramref name="Pitch"/> above 0 tips the model about its X axis before the turn (a quarter turn lays an upright model down with its top toward
/// the turn's heading), after <paramref name="Roll"/> turned it about its own upright axis (which way its branches point).
/// </summary>
public readonly record struct PackPlacement(Vector3 Base, float Rot, float Scale, int Model, float Stretch = 1f, PackKind Kind = PackKind.Tree, Vector3 Tint = default, float Pitch = 0f, float Roll = 0f);

/// <summary>The triangles of every placement that wears one material, as one mesh: positions relative to the tile (centre x, z), UV2 = the tree's base (x, z) in the same frame.</summary>
public sealed class PackBucket
{
    public int Material { get; set; }
    public readonly List<Vector3> V = new(), N = new();
    public readonly List<Vector2> Uv = new(), Uv2 = new();
    /// <summary>Vertex colour: rgb = the tint halved (8 bits cannot hold above 1), a = the placement's <see cref="PackKind"/> (tree 1, object 0.5, undergrowth 0), read by pack_tree.gdshader.</summary>
    public readonly List<Vector4> Col = new();
    public readonly List<int> I = new();

    // A tile's lists are megabytes and live for a few frames (built on a worker, handed to the renderer, dropped): at a 220 m radius that was over a gigabyte of
    // garbage on the large object heap. So the lists are sized once to what the tile needs and go back to a small pool when the renderer has them.
    const int PoolMax = 24;
    static readonly List<PackBucket> Pool = new(); // under its own lock; few entries

    /// <summary>An empty bucket with room for this many vertices and indices: the smallest pooled one that is big enough (tiles differ a hundredfold), else a new one.</summary>
    public static PackBucket Rent(int material, int vertices, int indices)
    {
        PackBucket? b = null;
        lock (Pool)
        {
            var best = -1;
            for (var i = 0; i < Pool.Count; i++)
                if (Pool[i].V.Capacity >= vertices && Pool[i].I.Capacity >= indices && (best < 0 || Pool[i].V.Capacity < Pool[best].V.Capacity)) best = i;
            if (best >= 0) { b = Pool[best]; Pool.RemoveAt(best); }
        }
        b ??= new PackBucket();
        b.Material = material;
        b.V.EnsureCapacity(vertices); b.N.EnsureCapacity(vertices); b.Uv.EnsureCapacity(vertices); b.Uv2.EnsureCapacity(vertices); b.Col.EnsureCapacity(vertices);
        b.I.EnsureCapacity(indices);
        return b;
    }

    /// <summary>Gives the bucket back once nothing reads it any more (its lists are emptied and reused by a later tile). A full pool drops its smallest.</summary>
    public void Return()
    {
        V.Clear(); N.Clear(); Uv.Clear(); Uv2.Clear(); Col.Clear(); I.Clear();
        lock (Pool)
        {
            Pool.Add(this);
            if (Pool.Count <= PoolMax) return;
            var least = 0;
            for (var i = 1; i < Pool.Count; i++) if (Pool[i].V.Capacity < Pool[least].V.Capacity) least = i;
            Pool.RemoveAt(least);
        }
    }
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
        var need = new Dictionary<int, (int V, int I)>(); // the size of each bucket, so its lists are allocated once
        foreach (var t in trees)
            foreach (var surface in models[t.Model].Surfaces)
            {
                need.TryGetValue(surface.Material, out var n);
                need[surface.Material] = (n.V + surface.Vertices.Length, n.I + surface.Indices.Length);
            }
        foreach (var t in trees)
        {
            var pose = new PackPose(t, centre);
            var baseUv = pose.BaseUv;
            var tint = t.Tint == default ? Vector3.One : t.Tint;
            var col = new Vector4(tint * 0.5f, KindAlpha(t.Kind, card: false));
            foreach (var surface in models[t.Model].Surfaces)
            {
                if (!byMaterial.TryGetValue(surface.Material, out var b)) byMaterial[surface.Material] = b = PackBucket.Rent(surface.Material, need[surface.Material].V, need[surface.Material].I);
                var first = b.V.Count;
                for (var i = 0; i < surface.Vertices.Length; i++)
                {
                    b.V.Add(pose.Point(surface.Vertices[i]));
                    b.N.Add(pose.Turned(surface.Normals[i]));
                    b.Uv.Add(surface.Uv[i]);
                    b.Uv2.Add(baseUv);
                    b.Col.Add(col);
                }
                foreach (var idx in surface.Indices) b.I.Add(first + idx);
            }
        }
        return byMaterial.Values.ToList();
    }

    /// <summary>
    /// What a piece is, as the vertex colour's alpha (8 bits; pack_tree.gdshader reads it back with a margin): tree 1, object 0.5, undergrowth 0, far undergrowth 0.25;
    /// a card of a tree 0.875, of an object 0.375.
    /// </summary>
    public static float KindAlpha(PackKind kind, bool card) => kind switch
    {
        PackKind.Tree => card ? 0.875f : 1f,
        PackKind.Object => card ? 0.375f : 0.5f,
        PackKind.UndergrowthFar => 0.25f,
        _ => 0f,
    };

    /// <summary>Rotation about +X (Y toward Z for a positive angle): a quarter turn lays an upright model down, its top toward +Z and its +Z side down.</summary>
    internal static Vector3 Tip(Vector3 v, float s, float c) => new(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);

    /// <summary>Rotation about +Y by the angle whose sine and cosine are given (Godot's <c>Basis.Rotated(Up, angle)</c>).</summary>
    internal static Vector3 Turn(Vector3 v, float s, float c) => new(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
}

/// <summary>How a placement's model gets into its tile: scaled (and stretched), laid down when it has a pitch, turned by its Rot, moved to its base minus the tile centre.</summary>
public readonly struct PackPose
{
    readonly float _s, _c, _sp, _cp, _sr, _cr;
    readonly bool _laid;
    readonly Vector3 _size, _origin;

    public PackPose(PackPlacement t, Vector3 centre)
    {
        (_s, _c) = MathF.SinCos(t.Rot);
        (_sp, _cp) = MathF.SinCos(t.Pitch);
        (_sr, _cr) = MathF.SinCos(t.Roll);
        _origin = new Vector3(t.Base.X - centre.X, t.Base.Y, t.Base.Z - centre.Z);
        _laid = t.Pitch != 0f;
        _size = _laid ? new Vector3(t.Scale, t.Scale * t.Stretch, t.Scale) : new Vector3(t.Scale, t.Scale, t.Scale * t.Stretch);
    }

    /// <summary>The base in the tile's frame (x, z): UV2 of every vertex of the piece.</summary>
    public Vector2 BaseUv => new(_origin.X, _origin.Z);

    /// <summary>A direction of the model in the tile (turned, not scaled or moved).</summary>
    public Vector3 Turned(Vector3 v)
    {
        if (_laid) v = PackTileMesher.Tip(PackTileMesher.Turn(v, _sr, _cr), _sp, _cp);
        return PackTileMesher.Turn(v, _s, _c);
    }

    /// <summary>A point of the model in the tile.</summary>
    public Vector3 Point(Vector3 v) => Turned(v * _size) + _origin;
}
