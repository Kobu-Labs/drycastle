using System.Text.Json;

namespace Suchohrad.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link, so the parsing and the grouping can be tested.

/// <summary>One placed piece of ground clutter. Map metres, x east, y north, z up; Rot is counter-clockwise seen from above.</summary>
/// <remarks>Tint is the optional sixth number of an entry: 128 (<see cref="ClutterTint.Neutral"/>) = the mesh's own colours, lower = fresher, higher = drier.</remarks>
public readonly record struct ClutterInstance(float X, float Y, float Z, float Rot, float Scale, byte Tint = ClutterTint.Neutral);

/// <summary>The instances of one kind in one chunk.</summary>
public sealed record ClutterGroup(int ChunkX, int ChunkY, string Kind, IReadOnlyList<ClutterInstance> Instances);

/// <summary>
/// clutter.json (map manifest <c>files.clutter</c>): {"format_version":1, "chunk_size_m":250, "kinds":{"tuft":{"mesh":"tuft"}},
/// "chunks":{"3_-1":{"tuft":[[x,y,z,rot,scale(,tint)],...]}}}. Kinds not listed under "kinds" are skipped.
/// </summary>
public sealed class ClutterDoc
{
    public const int SupportedFormat = 1;

    public int FormatVersion { get; init; }
    /// <summary>Kind -> name of its mesh node in clutter_meshes.glb.</summary>
    public IReadOnlyDictionary<string, string> KindMesh { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ClutterGroup> Groups { get; init; } = Array.Empty<ClutterGroup>();
    public int InstanceCount => Groups.Sum(g => g.Instances.Count);

    /// <summary>Parses the file; throws <see cref="FormatException"/> on a format version it does not know or a malformed entry.</summary>
    public static ClutterDoc Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("format_version", out var v) && v.TryGetInt32(out var iv) ? iv : 0;
        if (version != SupportedFormat) throw new FormatException($"clutter.json format_version {version}, expected {SupportedFormat}");
        var kinds = new Dictionary<string, string>();
        if (root.TryGetProperty("kinds", out var ks))
            foreach (var k in ks.EnumerateObject())
                kinds[k.Name] = k.Value.TryGetProperty("mesh", out var m) && m.GetString() is { Length: > 0 } s ? s : k.Name;
        var groups = new List<ClutterGroup>();
        if (root.TryGetProperty("chunks", out var chunks))
            foreach (var c in chunks.EnumerateObject())
            {
                var parts = c.Name.Split('_');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var ix) || !int.TryParse(parts[1], out var iy))
                    throw new FormatException($"clutter.json: bad chunk key '{c.Name}'");
                foreach (var kind in c.Value.EnumerateObject())
                {
                    if (!kinds.ContainsKey(kind.Name)) continue;
                    var list = new List<ClutterInstance>(kind.Value.GetArrayLength());
                    foreach (var e in kind.Value.EnumerateArray())
                    {
                        if (e.GetArrayLength() < 5) throw new FormatException($"clutter.json: chunk {c.Name} {kind.Name}: entry needs x,y,z,rot,scale");
                        var tint = e.GetArrayLength() > 5 && e[5].TryGetInt32(out var tv) ? (byte)Math.Clamp(tv, 0, 255) : ClutterTint.Neutral;
                        list.Add(new ClutterInstance(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle(), e[4].GetSingle(), tint));
                    }
                    if (list.Count > 0) groups.Add(new ClutterGroup(ix, iy, kind.Name, list));
                }
            }
        return new ClutterDoc { FormatVersion = version, KindMesh = kinds, Groups = groups };
    }
}

/// <summary>
/// The colour factor of a tint byte (the pipeline's <c>detail_meshes.tint_rgb</c>): the instance colour of a MultiMesh, which multiplies the
/// vertex colours. Fresh at 0, white at 128, dry at 255, linear between. A product cannot add yellow to a green, only take green and blue
/// away, so "dry" reads as olive; real straw is its own kind.
/// </summary>
public static class ClutterTint
{
    public const byte Neutral = 128;
    static readonly (float R, float G, float B) Fresh = (0.80f, 0.97f, 0.78f), Dry = (1.0f, 0.80f, 0.55f);

    public static (float R, float G, float B) Rgb(byte t)
    {
        var (end, w) = t < Neutral ? (Fresh, (Neutral - t) / 128f) : (Dry, (t - Neutral) / 127f);
        return (1f + (end.R - 1f) * w, 1f + (end.G - 1f) * w, 1f + (end.B - 1f) * w);
    }
}

/// <summary>Distances and the on/off setting of clutter drawing; one place for the numbers.</summary>
public static class ClutterSettings
{
    /// <summary>Environment variable: 0 or off = no clutter, 1 or unset = default distances, another number scales the distances (0.5 = half).</summary>
    public const string EnvVar = "SUCHOHRAD_CLUTTER";
    /// <summary>Instances are grouped into cells this wide (metres), one MultiMesh per cell and kind, because a visibility range
    /// is measured to the node as a whole: one chunk-sized MultiMesh would vanish while the player stands inside it.</summary>
    public const float CellM = 48f;
    /// <summary>Distance (metres) from the camera at which a kind is gone, before the cell allowance.</summary>
    public const float DefaultRangeM = 70f;
    /// <summary>The fade zone before the range end (metres).</summary>
    public const float FadeMarginM = 8f;
    /// <summary>Half the diagonal of a cell: added to the range, the distance being measured to the cell centre.</summary>
    public static readonly float CellAllowanceM = CellM * 0.7072f;

    /// <summary>Per kind overrides of <see cref="DefaultRangeM"/>: small things vanish sooner.</summary>
    public static readonly IReadOnlyDictionary<string, float> KindRangeM = new Dictionary<string, float>
    {
        ["pebble"] = 40f,
        ["leaf"] = 40f,
        ["stick"] = 40f,
        ["flower"] = 55f,
        ["rock"] = 80f,
    };

    public static float RangeOf(string kind) => KindRangeM.TryGetValue(kind, out var r) ? r : DefaultRangeM;

    /// <summary>Parses the environment value: null or empty = 1; "off"/"false"/"0" = 0; a number &gt;= 0 is clamped to 4; junk = 1.</summary>
    public static float ParseScale(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 1f;
        var v = value.Trim();
        if (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0f;
        return float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)
            ? Math.Clamp(f, 0f, 4f) : 1f;
    }

    /// <summary>
    /// Splits a group into cells of <see cref="CellM"/> metres (cell index from the map position), in a stable order.
    /// </summary>
    public static IEnumerable<IReadOnlyList<ClutterInstance>> Cells(IReadOnlyList<ClutterInstance> instances)
    {
        var cells = new SortedDictionary<(int, int), List<ClutterInstance>>();
        foreach (var i in instances)
        {
            var key = ((int)MathF.Floor(i.X / CellM), (int)MathF.Floor(i.Y / CellM));
            if (!cells.TryGetValue(key, out var l)) cells[key] = l = new();
            l.Add(i);
        }
        return cells.Values;
    }
}
