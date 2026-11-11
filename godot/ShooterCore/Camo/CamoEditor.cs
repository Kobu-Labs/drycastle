namespace Suchohrad.ShooterCore;

/// <summary>Which panel of the customiser a parameter sits in.</summary>
public enum CamoGroup { SuitColours, Pattern, Foliage, FacePaint }

/// <summary>How the customiser edits a parameter: a 0..255 slider, an index into a camo.json list, or a colour.</summary>
public enum CamoParamKind { Byte, Choice, Colour }

/// <summary>
/// One tweakable field of a <see cref="CamoSpec"/>. Values travel as ints: a byte, a list index, or 0xRRGGBB for a colour,
/// so the customiser can build one slider or picker per parameter from <see cref="CamoEditor.Params"/> without a switch per field.
/// </summary>
public sealed record CamoParam(string Key, CamoGroup Group, CamoParamKind Kind, int Min, int Max, Func<CamoSpec, int> Get, Func<CamoSpec, int, CamoSpec> Set);

/// <summary>
/// The customiser's logic without any Godot types: the parameter list (built from camo.json, so it follows the data),
/// the random roll, presets applied to one part of the look, and the share code (base64 of the 30-byte CamoSpec).
/// </summary>
public static class CamoEditor
{
    /// <summary>Every tweakable parameter, grouped in the order the customiser shows them.</summary>
    public static IReadOnlyList<CamoParam> Params(CamoFile data)
    {
        static CamoParam Byte(string key, CamoGroup g, Func<CamoSpec, byte> get, Func<CamoSpec, byte, CamoSpec> set, int max = 255) =>
            new(key, g, CamoParamKind.Byte, 0, max, s => get(s), (s, v) => set(s, (byte)Math.Clamp(v, 0, max)));
        static CamoParam Choice(string key, CamoGroup g, int count, Func<CamoSpec, byte> get, Func<CamoSpec, byte, CamoSpec> set) =>
            new(key, g, CamoParamKind.Choice, 0, Math.Max(0, count - 1), s => get(s), (s, v) => set(s, (byte)Math.Clamp(v, 0, Math.Max(0, count - 1))));
        static CamoParam Colour(string key, CamoGroup g, Func<CamoSpec, Rgb> get, Func<CamoSpec, Rgb, CamoSpec> set) =>
            new(key, g, CamoParamKind.Colour, 0, 0xFFFFFF, s => Pack(get(s)), (s, v) => set(s, Unpack(v)));

        return new List<CamoParam>
        {
            Colour("suit_base", CamoGroup.SuitColours, s => s.SuitBase, (s, c) => s with { SuitBase = c }),
            Colour("suit_secondary", CamoGroup.SuitColours, s => s.SuitSecondary, (s, c) => s with { SuitSecondary = c }),

            Choice("suit_pattern", CamoGroup.Pattern, data.SuitPatterns.Count, s => s.SuitPattern, (s, v) => s with { SuitPattern = v }),
            Byte("pattern_scale", CamoGroup.Pattern, s => s.PatternScale, (s, v) => s with { PatternScale = v }),
            Byte("contrast", CamoGroup.Pattern, s => s.Contrast, (s, v) => s with { Contrast = v }),

            Byte("tuft_layers", CamoGroup.Foliage, s => s.TuftLayers, (s, v) => s with { TuftLayers = v }, data.TuftMaxLayers),
            Choice("vegetation", CamoGroup.Foliage, data.Vegetation.Count, s => s.Vegetation, (s, v) => s with { Vegetation = v }),
            Byte("tuft_size", CamoGroup.Foliage, s => s.TuftSize, (s, v) => s with { TuftSize = v }),
            Byte("tuft_head", CamoGroup.Foliage, s => s.TuftHead, (s, v) => s with { TuftHead = v }),
            Byte("tuft_chest", CamoGroup.Foliage, s => s.TuftChest, (s, v) => s with { TuftChest = v }),
            Byte("tuft_arms", CamoGroup.Foliage, s => s.TuftArms, (s, v) => s with { TuftArms = v }),
            Byte("tuft_legs", CamoGroup.Foliage, s => s.TuftLegs, (s, v) => s with { TuftLegs = v }),
            Byte("tuft_colour_variation", CamoGroup.Foliage, s => s.TuftColourVariation, (s, v) => s with { TuftColourVariation = v }),
            Byte("tuft_sway", CamoGroup.Foliage, s => s.TuftSway, (s, v) => s with { TuftSway = v }),

            Choice("face_pattern", CamoGroup.FacePaint, data.FacePatterns.Count, s => s.FacePattern, (s, v) => s with { FacePattern = v }),
            Colour("face_colour_a", CamoGroup.FacePaint, s => s.FaceColourA, (s, c) => s with { FaceColourA = c }),
            Colour("face_colour_b", CamoGroup.FacePaint, s => s.FaceColourB, (s, c) => s with { FaceColourB = c }),
            Colour("face_colour_c", CamoGroup.FacePaint, s => s.FaceColourC, (s, c) => s with { FaceColourC = c }),
            Byte("face_coverage", CamoGroup.FacePaint, s => s.FaceCoverage, (s, v) => s with { FaceCoverage = v }),
        };
    }

    /// <summary>A uniformly random look that passes <see cref="CamoSpec.Validate"/>.</summary>
    public static CamoSpec Randomize(CamoFile data, Random rng)
    {
        var spec = new CamoSpec
        {
            SuitBase = RandomRgb(rng), SuitSecondary = RandomRgb(rng),
            SuitPattern = (byte)rng.Next(data.SuitPatterns.Count),
            PatternScale = RandomByte(rng), Contrast = RandomByte(rng),
            TuftLayers = (byte)rng.Next(data.TuftMaxLayers + 1),
            TuftSize = RandomByte(rng),
            TuftHead = RandomByte(rng), TuftChest = RandomByte(rng), TuftArms = RandomByte(rng), TuftLegs = RandomByte(rng),
            TuftColourVariation = RandomByte(rng), TuftSway = RandomByte(rng),
            Vegetation = (byte)rng.Next(data.Vegetation.Count),
            FacePattern = (byte)rng.Next(data.FacePatterns.Count),
            FaceColourA = RandomRgb(rng), FaceColourB = RandomRgb(rng), FaceColourC = RandomRgb(rng),
            FaceCoverage = RandomByte(rng),
        };
        return spec;
    }

    /// <summary>Replaces the suit part of <paramref name="draft"/> (colours, pattern, foliage) with a suit preset's.</summary>
    public static CamoSpec WithSuitPreset(CamoSpec draft, CamoSpec preset) => draft with
    {
        SuitBase = preset.SuitBase, SuitSecondary = preset.SuitSecondary, SuitPattern = preset.SuitPattern,
        PatternScale = preset.PatternScale, Contrast = preset.Contrast, TuftLayers = preset.TuftLayers, TuftSize = preset.TuftSize,
        TuftHead = preset.TuftHead, TuftChest = preset.TuftChest, TuftArms = preset.TuftArms, TuftLegs = preset.TuftLegs,
        TuftColourVariation = preset.TuftColourVariation, TuftSway = preset.TuftSway, Vegetation = preset.Vegetation,
    };

    /// <summary>Replaces the face paint part of <paramref name="draft"/> with a face preset's.</summary>
    public static CamoSpec WithFacePreset(CamoSpec draft, CamoSpec preset) => draft with
    {
        FacePattern = preset.FacePattern, FaceColourA = preset.FaceColourA, FaceColourB = preset.FaceColourB,
        FaceColourC = preset.FaceColourC, FaceCoverage = preset.FaceCoverage,
    };

    /// <summary>The share code: base64 of the encoded CamoSpec (40 characters).</summary>
    public static string ShareCode(CamoSpec spec) => Convert.ToBase64String(spec.Encode());

    /// <summary>
    /// Reads a share code. Null with a reason the customiser can show when the text is not base64, the wrong length or
    /// version, or the look does not pass camo.json validation. Whitespace around the code is ignored.
    /// </summary>
    public static CamoSpec? ParseShareCode(string code, CamoFile data, out string? reason)
    {
        reason = null;
        var text = code.Trim();
        if (text.Length == 0) { reason = "paste a share code first"; return null; }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { reason = "that is not a share code"; return null; }
        var spec = CamoSpec.TryDecode(bytes);
        if (spec is null) { reason = "that share code is from a different version"; return null; }
        if (spec.Validate(data) is { } err) { reason = err; return null; }
        return spec;
    }

    static byte RandomByte(Random rng) => (byte)rng.Next(256);
    static Rgb RandomRgb(Random rng) => new(RandomByte(rng), RandomByte(rng), RandomByte(rng));
    static int Pack(Rgb c) => (c.R << 16) | (c.G << 8) | c.B;
    static Rgb Unpack(int v) => new((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
}
