namespace Drycastle.ShooterCore;

/// <summary>Which panel of the customiser a parameter sits in.</summary>
public enum CamoGroup { Character, Suit, Foliage, FacePaint }

/// <summary>How the customiser edits a parameter: a 0..255 slider, an index into a camo.json list, or a colour.</summary>
public enum CamoParamKind { Byte, Choice, Colour }

/// <summary>
/// One tweakable field of a <see cref="CamoSpec"/>. Values travel as ints: a byte, a list index, or 0xRRGGBB for a colour,
/// so the customiser can build one slider or picker per parameter from <see cref="CamoEditor.Params"/> without a switch per field.
/// </summary>
public sealed record CamoParam(string Key, CamoGroup Group, CamoParamKind Kind, int Min, int Max, Func<CamoSpec, int> Get, Func<CamoSpec, int, CamoSpec> Set);

/// <summary>
/// The customiser's logic without any Godot types: the parameter list (built from camo.json, so it follows the data),
/// which controls are inert for a look, the random roll, presets applied to one part of the look, and the share code (base64 of the 33-byte CamoSpec).
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
            Choice("body", CamoGroup.Character, data.Bodies.Count, s => s.Body, (s, v) => s with { Body = v }),
            Choice("skin_tone", CamoGroup.Character, data.SkinTones.Count, s => s.SkinTone, (s, v) => s with { SkinTone = v }),
            Choice("headgear", CamoGroup.Character, data.Headgear.Count, s => s.Headgear, (s, v) => s with { Headgear = v }),

            Colour("suit_base", CamoGroup.Suit, s => s.SuitBase, (s, c) => s with { SuitBase = c }),
            Colour("suit_secondary", CamoGroup.Suit, s => s.SuitSecondary, (s, c) => s with { SuitSecondary = c }),

            Choice("suit_pattern", CamoGroup.Suit, data.SuitPatterns.Count, s => s.SuitPattern, (s, v) => s with { SuitPattern = v }),
            Byte("pattern_scale", CamoGroup.Suit, s => s.PatternScale, (s, v) => s with { PatternScale = v }),
            Byte("contrast", CamoGroup.Suit, s => s.Contrast, (s, v) => s with { Contrast = v }),

            Byte("tuft_layers", CamoGroup.Foliage, s => s.TuftLayers, WithTuftLayers, data.TuftMaxLayers),
            Choice("vegetation", CamoGroup.Foliage, data.Vegetation.Count, s => s.Vegetation, (s, v) => s with { Vegetation = v }),
            Byte("tuft_size", CamoGroup.Foliage, s => s.TuftSize, (s, v) => s with { TuftSize = v }),
            Byte("tuft_head", CamoGroup.Foliage, s => s.TuftHead, (s, v) => s with { TuftHead = v }),
            Byte("tuft_chest", CamoGroup.Foliage, s => s.TuftChest, (s, v) => s with { TuftChest = v }),
            Byte("tuft_arms", CamoGroup.Foliage, s => s.TuftArms, (s, v) => s with { TuftArms = v }),
            Byte("tuft_legs", CamoGroup.Foliage, s => s.TuftLegs, (s, v) => s with { TuftLegs = v }),
            Byte("tuft_colour_variation", CamoGroup.Foliage, s => s.TuftColourVariation, (s, v) => s with { TuftColourVariation = v }),
            Byte("tuft_sway", CamoGroup.Foliage, s => s.TuftSway, (s, v) => s with { TuftSway = v }),

            Byte("face_coverage", CamoGroup.FacePaint, s => s.FaceCoverage, (s, v) => s with { FaceCoverage = v }),
            Choice("face_pattern", CamoGroup.FacePaint, data.FacePatterns.Count, s => s.FacePattern, (s, v) => s with { FacePattern = v }),
            Colour("face_colour_a", CamoGroup.FacePaint, s => s.FaceColourA, (s, c) => s with { FaceColourA = c }),
            Colour("face_colour_b", CamoGroup.FacePaint, s => s.FaceColourB, (s, c) => s with { FaceColourB = c }),
            Colour("face_colour_c", CamoGroup.FacePaint, s => s.FaceColourC, (s, c) => s with { FaceColourC = c }),
        };
    }

    /// <summary>Density given to every body region when foliage is switched on and no region has any (see <see cref="WithTuftLayers"/>).</summary>
    public const byte StarterTuftDensity = 170;

    /// <summary>
    /// Sets the foliage thickness. Going from none to some while every region's density is 0 would draw nothing, so the
    /// regions then start at <see cref="StarterTuftDensity"/>: the first move of the control always shows foliage.
    /// </summary>
    public static CamoSpec WithTuftLayers(CamoSpec s, byte layers)
    {
        var next = s with { TuftLayers = layers };
        if (s.TuftLayers != 0 || layers == 0 || s.TuftHead + s.TuftChest + s.TuftArms + s.TuftLegs != 0) return next;
        return next with { TuftHead = StarterTuftDensity, TuftChest = StarterTuftDensity, TuftArms = StarterTuftDensity, TuftLegs = StarterTuftDensity };
    }

    /// <summary>
    /// Null when changing parameter <paramref name="key"/> changes how <paramref name="spec"/> is drawn; otherwise why it does not
    /// ("foliage_off", "face_off", "no_headgear"). The customiser disables such a control and shows the reason, so no control is a dead end.
    /// </summary>
    public static string? Inert(string key, CamoSpec spec, CamoFile data)
    {
        if (key == "headgear") return spec.Body < data.Bodies.Count && data.Bodies[spec.Body].Headgear.Count == 0 ? "no_headgear" : null;
        if (key == "vegetation" || (key.StartsWith("tuft_", StringComparison.Ordinal) && key != "tuft_layers")) return spec.TuftLayers == 0 ? "foliage_off" : null;
        if (key.StartsWith("face_", StringComparison.Ordinal) && key != "face_coverage") return spec.FaceCoverage == 0 ? "face_off" : null;
        return null;
    }

    /// <summary>
    /// A random look that passes <see cref="CamoSpec.Validate"/> and reads as camouflage: muted earth and leaf colours (never neon),
    /// a suit with two tones of one hue family, foliage on about half the rolls and face paint on about half.
    /// </summary>
    public static CamoSpec Randomize(CamoFile data, Random rng)
    {
        var hue = 25 + rng.NextDouble() * 110; // brown through olive to green
        var sat = 0.18 + rng.NextDouble() * 0.32;
        var val = 0.30 + rng.NextDouble() * 0.28;
        var baseColour = Hsv(hue, sat, val);
        var secondary = Hsv(hue + rng.NextDouble() * 30 - 15, sat * (0.8 + rng.NextDouble() * 0.4), val * (0.45 + rng.NextDouble() * 0.25));
        var foliage = data.TuftMaxLayers > 0 && rng.Next(2) == 0;
        var paint = rng.Next(2) == 0;
        byte Range(int lo, int hi) => (byte)rng.Next(lo, hi + 1);
        return new CamoSpec
        {
            Body = (byte)rng.Next(data.Bodies.Count), SkinTone = (byte)rng.Next(data.SkinTones.Count), Headgear = (byte)rng.Next(data.Headgear.Count),
            SuitBase = baseColour, SuitSecondary = secondary,
            SuitPattern = (byte)rng.Next(data.SuitPatterns.Count),
            PatternScale = Range(40, 220), Contrast = Range(90, 200),
            TuftLayers = foliage ? Range(1, data.TuftMaxLayers) : (byte)0,
            TuftSize = Range(60, 230),
            TuftHead = foliage ? Range(60, 255) : (byte)0, TuftChest = foliage ? Range(100, 255) : (byte)0,
            TuftArms = foliage ? Range(100, 255) : (byte)0, TuftLegs = foliage ? Range(100, 255) : (byte)0,
            TuftColourVariation = Range(40, 220), TuftSway = Range(40, 200),
            Vegetation = (byte)rng.Next(data.Vegetation.Count),
            FacePattern = (byte)rng.Next(data.FacePatterns.Count),
            FaceColourA = Hsv(hue, sat, 0.14 + rng.NextDouble() * 0.1), FaceColourB = Hsv(hue + 10, sat, 0.35 + rng.NextDouble() * 0.15),
            FaceColourC = Hsv(40, 0.25, 0.6 + rng.NextDouble() * 0.2),
            FaceCoverage = paint ? Range(90, 230) : (byte)0,
        };
    }

    /// <summary>An RGB colour from hue (degrees), saturation and value (0..1 each, clamped).</summary>
    static Rgb Hsv(double hue, double sat, double val)
    {
        sat = Math.Clamp(sat, 0, 1); val = Math.Clamp(val, 0, 1);
        var h = ((hue % 360) + 360) % 360 / 60.0;
        var c = val * sat;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        var m = val - c;
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Rgb(B(r + m), B(g + m), B(b + m));
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

    /// <summary>The share code: base64 of the encoded CamoSpec (44 characters).</summary>
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

    static int Pack(Rgb c) => (c.R << 16) | (c.G << 8) | c.B;
    static Rgb Unpack(int v) => new((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
}
