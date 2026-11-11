using System.Buffers.Binary;

namespace Suchohrad.ShooterCore;

/// <summary>An RGB colour, one byte per channel.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// A player's camouflage: the suit (colours, pattern, foliage tufts) and the face paint. Every field is a plain number,
/// colour or index into <c>camo.json</c>, so a new look is data only. Stored in the profile (ADR 0003) and sent to every
/// client in an <see cref="EventKind.Appearance"/> event, so the codec is fixed-size and small (<see cref="EncodedSize"/> bytes).
/// Byte-valued ranges are normalised to 0..1 by the renderer (see <see cref="Unit"/>).
/// </summary>
public sealed record CamoSpec
{
    /// <summary>Codec version, the first byte of every encoded spec. Bump when the layout changes.</summary>
    public const byte CodecVersion = 1;
    /// <summary>Fixed encoded length in bytes (well under the 64-byte budget of the camo ticket).</summary>
    public const int EncodedSize = 30;

    // Suit
    public Rgb SuitBase { get; init; } = new(0x4b, 0x5a, 0x32);
    public Rgb SuitSecondary { get; init; } = new(0x2e, 0x3a, 0x1f);
    /// <summary>Index into <c>suit_patterns</c> in camo.json.</summary>
    public byte SuitPattern { get; init; }
    /// <summary>Pattern blob size, 0..255 normalised over the data file's scale range.</summary>
    public byte PatternScale { get; init; } = 128;
    /// <summary>Contrast between the two suit colours, 0..255.</summary>
    public byte Contrast { get; init; } = 128;
    /// <summary>Number of foliage tuft layers, 0..<c>tuft_max_layers</c>. Zero means a plain suit.</summary>
    public byte TuftLayers { get; init; }
    /// <summary>Tuft length, 0..255 normalised over <c>tuft_size_m</c>.</summary>
    public byte TuftSize { get; init; } = 128;
    /// <summary>Tuft density per body region, 0..255 each.</summary>
    public byte TuftHead { get; init; }
    public byte TuftChest { get; init; }
    public byte TuftArms { get; init; }
    public byte TuftLegs { get; init; }
    /// <summary>How much each tuft's colour varies towards the secondary colour, 0..255.</summary>
    public byte TuftColourVariation { get; init; } = 96;
    /// <summary>Tuft sway amount, 0..255 normalised over <c>tuft_sway_m</c>.</summary>
    public byte TuftSway { get; init; } = 96;
    /// <summary>Index into <c>vegetation</c> in camo.json, the tuft look.</summary>
    public byte Vegetation { get; init; }

    // Face paint
    /// <summary>Index into <c>face_patterns</c> in camo.json.</summary>
    public byte FacePattern { get; init; }
    public Rgb FaceColourA { get; init; } = new(0x1d, 0x2a, 0x14);
    public Rgb FaceColourB { get; init; } = new(0x5b, 0x6b, 0x3a);
    public Rgb FaceColourC { get; init; } = new(0xc8, 0xb9, 0x8a);
    /// <summary>How much of the face the paint covers, 0..255. Zero means no face paint.</summary>
    public byte FaceCoverage { get; init; }

    /// <summary>The look a new player gets: a plain olive suit and no face paint.</summary>
    public static CamoSpec Default { get; } = new();

    /// <summary>Maps a 0..255 field to 0..1.</summary>
    public static float Unit(byte v) => v / 255f;

    /// <summary>Encodes to exactly <see cref="EncodedSize"/> bytes.</summary>
    public byte[] Encode()
    {
        var b = new byte[EncodedSize];
        var at = 0;
        b[at++] = CodecVersion;
        Put(b, ref at, SuitBase); Put(b, ref at, SuitSecondary);
        b[at++] = SuitPattern; b[at++] = PatternScale; b[at++] = Contrast; b[at++] = TuftLayers; b[at++] = TuftSize;
        b[at++] = TuftHead; b[at++] = TuftChest; b[at++] = TuftArms; b[at++] = TuftLegs;
        b[at++] = TuftColourVariation; b[at++] = TuftSway; b[at++] = Vegetation;
        b[at++] = FacePattern;
        Put(b, ref at, FaceColourA); Put(b, ref at, FaceColourB); Put(b, ref at, FaceColourC);
        b[at++] = FaceCoverage;
        return b;
    }

    /// <summary>Decodes an encoded spec. Null when the length or the codec version is wrong. Indices are not checked here; see <see cref="Validate"/>.</summary>
    public static CamoSpec? TryDecode(ReadOnlySpan<byte> d)
    {
        if (d.Length != EncodedSize || d[0] != CodecVersion) return null;
        var at = 1;
        var spec = new CamoSpec
        {
            SuitBase = Get(d, ref at), SuitSecondary = Get(d, ref at),
            SuitPattern = d[at++], PatternScale = d[at++], Contrast = d[at++], TuftLayers = d[at++], TuftSize = d[at++],
            TuftHead = d[at++], TuftChest = d[at++], TuftArms = d[at++], TuftLegs = d[at++],
            TuftColourVariation = d[at++], TuftSway = d[at++], Vegetation = d[at++],
            FacePattern = d[at++],
            FaceColourA = Get(d, ref at), FaceColourB = Get(d, ref at), FaceColourC = Get(d, ref at),
            FaceCoverage = d[at++],
        };
        return spec;
    }

    /// <summary>
    /// Checks the indices and limits against the data file. Null when valid, otherwise a reason the client can show.
    /// Every colour and 0..255 field is valid by construction, so only the indices and the layer count can fail.
    /// </summary>
    public string? Validate(CamoFile data)
    {
        if (SuitPattern >= data.SuitPatterns.Count) return $"suit pattern {SuitPattern} is not in camo.json";
        if (FacePattern >= data.FacePatterns.Count) return $"face pattern {FacePattern} is not in camo.json";
        if (Vegetation >= data.Vegetation.Count) return $"vegetation {Vegetation} is not in camo.json";
        if (TuftLayers > data.TuftMaxLayers) return $"tuft layers {TuftLayers} exceed the maximum {data.TuftMaxLayers}";
        return null;
    }

    static void Put(byte[] b, ref int at, Rgb c) { b[at++] = c.R; b[at++] = c.G; b[at++] = c.B; }
    static Rgb Get(ReadOnlySpan<byte> d, ref int at) { var c = new Rgb(d[at], d[at + 1], d[at + 2]); at += 3; return c; }
}

/// <summary>
/// The appearance event payload: a player id and that player's <see cref="CamoSpec"/>. Little-endian u16 id, then the spec.
/// </summary>
public static class AppearanceCodec
{
    public const int PayloadSize = 2 + CamoSpec.EncodedSize;

    public static byte[] Encode(ushort playerId, CamoSpec spec)
    {
        var b = new byte[PayloadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(b, playerId);
        spec.Encode().CopyTo(b, 2);
        return b;
    }

    /// <summary>Null when the payload is the wrong size or the spec does not decode.</summary>
    public static (ushort PlayerId, CamoSpec Spec)? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != PayloadSize) return null;
        var spec = CamoSpec.TryDecode(d[2..]);
        return spec is null ? null : (BinaryPrimitives.ReadUInt16LittleEndian(d), spec);
    }
}
