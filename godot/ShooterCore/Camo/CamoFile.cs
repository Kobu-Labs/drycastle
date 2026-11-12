using System.Globalization;

namespace Drycastle.ShooterCore;

/// <summary>A named look in camo.json: its id, its display name and the resolved <see cref="CamoSpec"/>.</summary>
public sealed record CamoPreset(string Id, string Name, CamoSpec Spec);

/// <summary>
/// A character model in camo.json: the GLB (relative to the asset library) and which of its materials play which part.
/// <see cref="Suit"/> surfaces carry the camouflage pattern, <see cref="Gear"/> (vests, belts) takes a dark tint of the suit colour,
/// <see cref="Headgear"/> is the helmet (shown as modelled, covered in the suit pattern, or hidden, see <see cref="CamoSpec.Headgear"/>),
/// <see cref="Hair"/> keeps its modelled colour. <c>Skin</c> and <c>Face</c> always take the skin tone. Material names that are not
/// listed keep their modelled colour.
/// </summary>
public sealed record CamoBody(string Id, string Name, string Model, IReadOnlyList<string> Suit, IReadOnlyList<string> Gear, IReadOnlyList<string> Headgear, IReadOnlyList<string> Hair);

/// <summary>A skin tone in camo.json.</summary>
public sealed record SkinToneEntry(string Id, string Name, Rgb Colour);

/// <summary>
/// Concealment rules from camo.json: how much a stance hides a player (0..1) and how much moving gives back.
/// </summary>
public sealed record ConcealmentRules(float Stand, float Crouch, float Prone, float StillnessWeight, float FullSpeedMps)
{
    /// <summary>The base concealment of a stance, before movement is taken off.</summary>
    public float ForStance(Stance stance) => stance switch
    {
        Stance.Prone => Prone,
        Stance.Crouch => Crouch,
        _ => Stand,
    };
}

/// <summary>
/// camo.json (camouflage framework, Shooter v2 ticket #140). Lists the shader pattern names, face pattern names and
/// vegetation looks that a <see cref="CamoSpec"/> indexes into, the normalisation ranges for the 0..255 fields, the
/// suit and face presets for the customiser, and the concealment rules. Validated at load: a bad file stops the
/// server at start instead of rendering wrong.
/// </summary>
public sealed class CamoFile : IVersioned
{
    /// <summary>Newest camo.json version this code reads.</summary>
    public const int SupportedVersion = 1;

    public int Version { get; private init; }
    public IReadOnlyList<string> SuitPatterns { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> FacePatterns { get; private init; } = Array.Empty<string>();
    /// <summary>Tuft looks by name. A spec's <see cref="CamoSpec.Vegetation"/> indexes this list.</summary>
    public IReadOnlyList<string> Vegetation { get; private init; } = Array.Empty<string>();
    /// <summary>The character models a player can wear. A spec's <see cref="CamoSpec.Body"/> indexes this list.</summary>
    public IReadOnlyList<CamoBody> Bodies { get; private init; } = Array.Empty<CamoBody>();
    /// <summary>Skin tones. A spec's <see cref="CamoSpec.SkinTone"/> indexes this list.</summary>
    public IReadOnlyList<SkinToneEntry> SkinTones { get; private init; } = Array.Empty<SkinToneEntry>();
    /// <summary>Headgear looks by name, in the order the renderer knows: 0 as modelled, 1 covered in the suit pattern, 2 none. A spec's <see cref="CamoSpec.Headgear"/> indexes this list.</summary>
    public IReadOnlyList<string> Headgear { get; private init; } = Array.Empty<string>();
    public int TuftMaxLayers { get; private init; }
    /// <summary>Tuft length range in metres (255 maps to the max).</summary>
    public (float Min, float Max) TuftSizeM { get; private init; }
    /// <summary>Tuft tip sway range in metres (255 maps to the max).</summary>
    public (float Min, float Max) TuftSwayM { get; private init; }
    /// <summary>Pattern blobs per metre (255 maps to the max).</summary>
    public (float Min, float Max) PatternScalePerM { get; private init; }
    public IReadOnlyList<CamoPreset> SuitPresets { get; private set; } = Array.Empty<CamoPreset>();
    public IReadOnlyList<CamoPreset> FacePresets { get; private set; } = Array.Empty<CamoPreset>();
    public ConcealmentRules Concealment { get; private init; } = new(0.3f, 0.6f, 0.9f, 0.7f, 6f);
    /// <summary>The spotting range multiplier at full concealment (1 = no effect). Name tags and kill-cam use it.</summary>
    public float SpottingMinMultiplier { get; private init; } = 0.35f;

    Dictionary<string, CamoPreset>? _suitById, _faceById;
    public CamoPreset? SuitPreset(string id) => DataIndex.Find(ref _suitById, SuitPresets, p => p.Id, id);
    public CamoPreset? FacePreset(string id) => DataIndex.Find(ref _faceById, FacePresets, p => p.Id, id);

    public float TuftSizeOf(byte v) => Lerp(TuftSizeM, CamoSpec.Unit(v));
    public float TuftSwayOf(byte v) => Lerp(TuftSwayM, CamoSpec.Unit(v));
    public float PatternScaleOf(byte v) => Lerp(PatternScalePerM, CamoSpec.Unit(v));

    static float Lerp((float Min, float Max) r, float t) => r.Min + (r.Max - r.Min) * t;

    public static CamoFile Load(string path) => Resolve(DataFile.Load<CamoRaw>(path, SupportedVersion));

    public static CamoFile Parse(string json) => Resolve(DataFile.Parse<CamoRaw>(json, "camo.json", SupportedVersion));

    static CamoFile Resolve(CamoRaw raw)
    {
        const string f = "camo.json";
        var suit = Names(raw.SuitPatterns, f, "suit_patterns");
        var face = Names(raw.FacePatterns, f, "face_patterns");
        var veg = Names(raw.Vegetation, f, "vegetation");
        var headgear = Names(raw.Headgear, f, "headgear");
        var bodies = (raw.Bodies ?? new List<BodyRaw>()).Select(b => BuildBody(b, f)).ToList();
        if (bodies.Count == 0) throw new DataFileException($"{f}: no bodies");
        if (bodies.Count > byte.MaxValue + 1) throw new DataFileException($"{f}: bodies holds at most 256 entries");
        if (bodies.Select(b => b.Id).Distinct().Count() != bodies.Count) throw new DataFileException($"{f}: bodies needs unique ids");
        var tones = (raw.SkinTones ?? new List<SkinToneRaw>()).Select(s => new SkinToneEntry(s.Id ?? "", s.Name ?? s.Id ?? "", Hex(s.Colour, $"{f}: skin tone \"{s.Id}\"", "colour"))).ToList();
        if (tones.Count == 0) throw new DataFileException($"{f}: no skin_tones");
        if (tones.Count > byte.MaxValue + 1) throw new DataFileException($"{f}: skin_tones holds at most 256 entries");
        if (raw.TuftMaxLayers is < 0 or > 8) throw new DataFileException($"{f}: tuft_max_layers must be 0 to 8");
        var concealment = raw.Concealment ?? throw new DataFileException($"{f}: field \"concealment\" is required");
        foreach (var (name, v) in new[] { ("stand", concealment.Stand), ("crouch", concealment.Crouch), ("prone", concealment.Prone), ("stillness_weight", concealment.StillnessWeight) })
            if (v is < 0 or > 1) throw new DataFileException($"{f}: concealment.{name} must be 0 to 1");
        if (concealment.FullSpeedMps <= 0) throw new DataFileException($"{f}: concealment.full_speed_mps must be above 0");
        if (raw.SpottingMinMultiplier is <= 0 or > 1) throw new DataFileException($"{f}: spotting_min_multiplier must be above 0 and at most 1");

        var file = new CamoFile
        {
            Version = raw.Version,
            SuitPatterns = suit,
            FacePatterns = face,
            Vegetation = veg,
            Bodies = bodies,
            SkinTones = tones,
            Headgear = headgear,
            TuftMaxLayers = raw.TuftMaxLayers,
            TuftSizeM = ParseRange(raw.TuftSizeM, f, "tuft_size_m"),
            TuftSwayM = ParseRange(raw.TuftSwayM, f, "tuft_sway_m"),
            PatternScalePerM = ParseRange(raw.PatternScalePerM, f, "pattern_scale_per_m"),
            Concealment = new ConcealmentRules(concealment.Stand, concealment.Crouch, concealment.Prone, concealment.StillnessWeight, concealment.FullSpeedMps),
            SpottingMinMultiplier = raw.SpottingMinMultiplier,
        };
        file.SuitPresets = (raw.SuitPresets ?? new List<SuitPresetRaw>()).Select(p => BuildSuitPreset(file, p)).ToList();
        file.FacePresets = (raw.FacePresets ?? new List<FacePresetRaw>()).Select(p => BuildFacePreset(file, p)).ToList();
        if (file.SuitPresets.Count == 0) throw new DataFileException($"{f}: no suit_presets");
        if (file.FacePresets.Count == 0) throw new DataFileException($"{f}: no face_presets");
        CheckUniqueIds(file.SuitPresets, f, "suit_presets");
        CheckUniqueIds(file.FacePresets, f, "face_presets");
        return file;
    }

    static CamoBody BuildBody(BodyRaw b, string f)
    {
        var where = $"{f}: body \"{b.Id}\"";
        if (string.IsNullOrWhiteSpace(b.Id)) throw new DataFileException($"{f}: a body has no id");
        if (string.IsNullOrWhiteSpace(b.Model)) throw new DataFileException($"{where}: model is required");
        return new CamoBody(b.Id, b.Name ?? b.Id, b.Model, b.Suit ?? new List<string>(), b.Gear ?? new List<string>(), b.Headgear ?? new List<string>(), b.Hair ?? new List<string>());
    }

    static CamoPreset BuildSuitPreset(CamoFile file, SuitPresetRaw p)
    {
        var where = $"camo.json: suit preset \"{p.Id}\"";
        var idx = IndexOf(file.SuitPatterns, p.Pattern, where, "pattern");
        var veg = IndexOf(file.Vegetation, p.Vegetation, where, "vegetation");
        var density = p.TuftDensity ?? new List<int> { 0, 0, 0, 0 };
        if (density.Count != 4) throw new DataFileException($"{where}: tuft_density needs 4 values (head, chest, arms, legs)");
        var spec = new CamoSpec
        {
            SuitBase = Hex(p.Base, where, "base"), SuitSecondary = Hex(p.Secondary, where, "secondary"),
            SuitPattern = (byte)idx, PatternScale = ToByte(p.PatternScale, where, "pattern_scale"), Contrast = ToByte(p.Contrast, where, "contrast"),
            TuftLayers = ToByte(p.TuftLayers, where, "tuft_layers"), TuftSize = ToByte(p.TuftSize, where, "tuft_size"),
            TuftHead = ToByte(density[0], where, "tuft_density"), TuftChest = ToByte(density[1], where, "tuft_density"),
            TuftArms = ToByte(density[2], where, "tuft_density"), TuftLegs = ToByte(density[3], where, "tuft_density"),
            TuftColourVariation = ToByte(p.TuftColourVariation, where, "tuft_colour_variation"), TuftSway = ToByte(p.TuftSway, where, "tuft_sway"),
            Vegetation = (byte)veg,
        };
        Check(spec, file, where);
        return new CamoPreset(p.Id ?? "", p.Name ?? p.Id ?? "", spec);
    }

    static CamoPreset BuildFacePreset(CamoFile file, FacePresetRaw p)
    {
        var where = $"camo.json: face preset \"{p.Id}\"";
        var idx = IndexOf(file.FacePatterns, p.Pattern, where, "pattern");
        var colours = p.Colours ?? new List<string>();
        if (colours.Count != 3) throw new DataFileException($"{where}: colours needs 3 hex colours");
        var spec = new CamoSpec
        {
            FacePattern = (byte)idx,
            FaceColourA = Hex(colours[0], where, "colours"), FaceColourB = Hex(colours[1], where, "colours"), FaceColourC = Hex(colours[2], where, "colours"),
            FaceCoverage = ToByte(p.Coverage, where, "coverage"),
        };
        Check(spec, file, where);
        return new CamoPreset(p.Id ?? "", p.Name ?? p.Id ?? "", spec);
    }

    static void Check(CamoSpec spec, CamoFile file, string where)
    {
        if (spec.Validate(file) is { } err) throw new DataFileException($"{where}: {err}");
    }

    static void CheckUniqueIds(IEnumerable<CamoPreset> presets, string f, string list)
    {
        var seen = new HashSet<string>();
        foreach (var p in presets)
            if (p.Id == "" || !seen.Add(p.Id)) throw new DataFileException($"{f}: {list} needs unique non-empty ids (\"{p.Id}\")");
    }

    static IReadOnlyList<string> Names(List<string>? names, string f, string field)
    {
        if (names is null || names.Count == 0) throw new DataFileException($"{f}: {field} needs at least one name");
        if (names.Count > byte.MaxValue + 1) throw new DataFileException($"{f}: {field} holds at most 256 names");
        if (names.Any(string.IsNullOrWhiteSpace)) throw new DataFileException($"{f}: {field} has an empty name");
        return names.ToArray();
    }

    static (float, float) ParseRange(List<float>? r, string f, string field)
    {
        if (r is null || r.Count != 2) throw new DataFileException($"{f}: {field} needs [min, max]");
        if (r[0] < 0 || r[1] < r[0]) throw new DataFileException($"{f}: {field} must be 0 or more with min at most max");
        return (r[0], r[1]);
    }

    static int IndexOf(IReadOnlyList<string> names, string? name, string where, string field)
    {
        var i = names.ToList().IndexOf(name ?? "");
        if (i < 0) throw new DataFileException($"{where}: unknown {field} \"{name}\"");
        return i;
    }

    static byte ToByte(int v, string where, string field) =>
        v is >= 0 and <= 255 ? (byte)v : throw new DataFileException($"{where}: {field} must be 0 to 255");

    static Rgb Hex(string? s, string where, string field)
    {
        if (s is not { Length: 7 } || s[0] != '#' || !s[1..].All(Uri.IsHexDigit))
            throw new DataFileException($"{where}: {field} must be a #rrggbb colour");
        return new Rgb(
            byte.Parse(s.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    // Raw shape of camo.json, mapped to snake_case by DataFile.
    internal sealed class CamoRaw : IVersioned
    {
        public int Version { get; set; }
        public List<string>? SuitPatterns { get; set; }
        public List<string>? FacePatterns { get; set; }
        public List<string>? Vegetation { get; set; }
        public List<string>? Headgear { get; set; }
        public List<BodyRaw>? Bodies { get; set; }
        public List<SkinToneRaw>? SkinTones { get; set; }
        public int TuftMaxLayers { get; set; }
        public List<float>? TuftSizeM { get; set; }
        public List<float>? TuftSwayM { get; set; }
        public List<float>? PatternScalePerM { get; set; }
        public float SpottingMinMultiplier { get; set; } = 0.35f;
        public ConcealmentRaw? Concealment { get; set; }
        public List<SuitPresetRaw>? SuitPresets { get; set; }
        public List<FacePresetRaw>? FacePresets { get; set; }
    }

    internal sealed class BodyRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Model { get; set; }
        public List<string>? Suit { get; set; }
        public List<string>? Gear { get; set; }
        public List<string>? Headgear { get; set; }
        public List<string>? Hair { get; set; }
    }

    internal sealed class SkinToneRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Colour { get; set; }
    }

    internal sealed class ConcealmentRaw
    {
        public float Stand { get; set; }
        public float Crouch { get; set; }
        public float Prone { get; set; }
        public float StillnessWeight { get; set; }
        public float FullSpeedMps { get; set; }
    }

    internal sealed class SuitPresetRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Base { get; set; }
        public string? Secondary { get; set; }
        public string? Pattern { get; set; }
        public int PatternScale { get; set; } = 128;
        public int Contrast { get; set; } = 128;
        public int TuftLayers { get; set; }
        public int TuftSize { get; set; } = 128;
        public List<int>? TuftDensity { get; set; }
        public int TuftColourVariation { get; set; } = 96;
        public int TuftSway { get; set; } = 96;
        public string? Vegetation { get; set; }
    }

    internal sealed class FacePresetRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Pattern { get; set; }
        public List<string>? Colours { get; set; }
        public int Coverage { get; set; }
    }
}
