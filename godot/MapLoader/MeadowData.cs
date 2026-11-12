using System.IO.Compression;

namespace Suchohrad.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The format and the generator are specified in
// pipeline/src/suchohrad_pipeline/meadow.py, which has the same hash and the same instance rules (a test pins both).

/// <summary>One generated piece of meadow clutter. Map metres, x east, y north (no height: the loader reads the ground); Rot is counter-clockwise seen from above.</summary>
public readonly record struct MeadowInstance(string Kind, float X, float Y, float Rot, float Scale, byte Tint = ClutterTint.Neutral);

/// <summary>One kind of a mix and its share (the shares of a mix add up to 1).</summary>
public readonly record struct MixEntry(string Kind, float Share);

/// <summary>
/// A layer of the raster: at most <see cref="MaxPerCell"/> instances per cell, scaled in ScaleMin..ScaleMax. <see cref="Mixes"/>[m] is what the layer
/// makes in a cell of mix m (the high nibble of the cell's zone byte); an id past the list uses mix 0.
/// </summary>
public sealed record MeadowLayer(string Name, float MaxPerCell, float ScaleMin, float ScaleMax, IReadOnlyList<IReadOnlyList<MixEntry>> Mixes)
{
    /// <summary>Every kind the layer can make.</summary>
    public IEnumerable<string> Kinds => Mixes.SelectMany(m => m.Select(e => e.Kind)).Distinct();

    public IReadOnlyList<MixEntry> Mix(int id) => Mixes[id < Mixes.Count ? id : 0];
}

/// <summary>
/// meadow.bin (manifest <c>files.meadow</c>, format 3): the density raster of the clutter the game generates near the player on fields and in forests: a
/// density plane per layer and a zone plane (high nibble: the species mix of the patch, low nibble: dryness, 8 = neutral).
/// </summary>
public sealed class MeadowFile
{
    public const int SupportedFormat = 3;
    static readonly byte[] Magic = "MEAD"u8.ToArray();

    public float CellM { get; private init; }
    public float ChunkM { get; private init; }
    public int CellsPerChunk { get; private init; }
    /// <summary>An instance lies within this many metres of its cell's centre (the raster is nonzero only where that whole window is free).</summary>
    public float JitterM { get; private init; }
    /// <summary>A generated instance within this many metres times the tree's scale of a trunk is dropped.</summary>
    public float TrunkReachM { get; private init; }
    public IReadOnlyList<MeadowLayer> Layers { get; private init; } = Array.Empty<MeadowLayer>();
    /// <summary>The kinds whose instances carry a tint byte.</summary>
    public IReadOnlySet<string> TintKinds { get; private init; } = new HashSet<string>();
    public int ChunkCount => _entries.Count;

    byte[] _data = Array.Empty<byte>();
    int _base;
    readonly Dictionary<(int, int), (int Offset, int Length)> _entries = new();
    readonly Dictionary<(int, int), byte[]?> _decoded = new();

    /// <summary>Parses the header and the chunk directory; the chunks are inflated when first asked for. Throws <see cref="FormatException"/> on a bad file.</summary>
    public static MeadowFile Parse(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new FormatException("meadow.bin: bad magic");
            var version = r.ReadUInt16();
            if (version != SupportedFormat) throw new FormatException($"meadow.bin format {version}, expected {SupportedFormat}");
            var cellMm = r.ReadUInt16();
            var cells = r.ReadUInt16();
            var chunkM = r.ReadSingle();
            var jitter = r.ReadSingle();
            var reach = r.ReadSingle();
            var kindCount = r.ReadByte();
            string Str() { var n = r.ReadByte(); return System.Text.Encoding.UTF8.GetString(r.ReadBytes(n)); }
            var kinds = new List<string>(); var tint = new HashSet<string>();
            for (var i = 0; i < kindCount; i++)
            {
                kinds.Add(Str());
                if ((r.ReadByte() & 1) != 0) tint.Add(kinds[^1]);
            }
            var count = r.ReadByte();
            var layers = new List<MeadowLayer>();
            for (var i = 0; i < count; i++)
            {
                var name = Str(); var max = r.ReadSingle(); var lo = r.ReadSingle(); var hi = r.ReadSingle();
                var mixCount = r.ReadByte();
                var mixes = new List<IReadOnlyList<MixEntry>>();
                for (var m = 0; m < mixCount; m++)
                {
                    var entries = new List<MixEntry>();
                    for (var e = r.ReadByte(); e > 0; e--)
                    {
                        var idx = r.ReadByte();
                        if (idx >= kinds.Count) throw new FormatException($"meadow.bin: kind index {idx}");
                        entries.Add(new MixEntry(kinds[idx], r.ReadSingle()));
                    }
                    if (entries.Count == 0) throw new FormatException("meadow.bin: empty mix");
                    mixes.Add(entries);
                }
                if (mixes.Count == 0) throw new FormatException("meadow.bin: layer without mixes");
                layers.Add(new MeadowLayer(name, max, lo, hi, mixes));
            }
            if (cellMm == 0 || cells == 0 || count == 0) throw new FormatException("meadow.bin: empty header");
            var file = new MeadowFile { CellM = cellMm / 1000f, ChunkM = chunkM, CellsPerChunk = cells, JitterM = jitter, TrunkReachM = reach, Layers = layers, TintKinds = tint };
            var chunks = r.ReadUInt32();
            var dir = new List<(int, int, int, int)>();
            for (var i = 0; i < chunks; i++) dir.Add((r.ReadInt32(), r.ReadInt32(), (int)r.ReadUInt32(), (int)r.ReadUInt32()));
            file._base = (int)ms.Position;
            file._data = data;
            foreach (var (ix, iy, off, len) in dir)
            {
                if (file._base + (long)off + len > data.Length) throw new FormatException($"meadow.bin: chunk {ix},{iy} runs past the end");
                file._entries[(ix, iy)] = (off, len);
            }
            return file;
        }
        catch (EndOfStreamException) { throw new FormatException("meadow.bin: truncated"); }
    }

    /// <summary>Layer-major bytes of one chunk (the layers and then the zone plane, each cells x cells, y from the south, x from the west), or null when the chunk has none.</summary>
    byte[]? Chunk(int cx, int cy)
    {
        if (_decoded.TryGetValue((cx, cy), out var cached)) return cached;
        byte[]? result = null;
        if (_entries.TryGetValue((cx, cy), out var e))
        {
            using var z = new ZLibStream(new MemoryStream(_data, _base + e.Offset, e.Length), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            result = ms.ToArray();
            if (result.Length != (Layers.Count + 1) * CellsPerChunk * CellsPerChunk) throw new FormatException($"meadow.bin: chunk {cx},{cy} has {result.Length} bytes");
        }
        _decoded[(cx, cy)] = result;
        return result;
    }

    /// <summary>The density byte of a layer in a global cell (floor of metres / cell size); 0 where there is nothing.</summary>
    public byte Density(int layer, int gx, int gy)
    {
        var n = CellsPerChunk;
        var cx = FloorDiv(gx, n); var cy = FloorDiv(gy, n);
        var chunk = Chunk(cx, cy);
        return chunk == null ? (byte)0 : chunk[(layer * n + (gy - cy * n)) * n + (gx - cx * n)];
    }

    /// <summary>The zone byte of a global cell: the high nibble is the species mix, the low nibble the dryness (8 = neutral); 0 where the chunk has none.</summary>
    public byte Zone(int gx, int gy) => Density(Layers.Count, gx, gy);

    internal static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
}

/// <summary>The generator: the raster plus a hash of the cell makes the same instances on every machine.</summary>
public static class MeadowGen
{
    /// <summary>The most instances one cell and layer make, whatever the layer's maximum says.</summary>
    public const int MaxPerCellHard = 49;

    /// <summary>64-bit mix of two integers and a salt, as a number in [0, 1) (the same as <c>meadow.hash01</c> in the pipeline).</summary>
    public static double Hash01(int ix, int iy, int salt)
    {
        unchecked
        {
            var x = (ulong)(long)ix * 0x9E3779B97F4A7C15UL;
            x ^= (ulong)(long)iy * 0xC2B2AE3D27D4EB4FUL;
            x ^= (ulong)salt * 0x165667B19E3779F9UL;
            x ^= 0x243F6A8885A308D3UL;
            x ^= x >> 33;
            x *= 0xFF51AFD7ED558CCDUL;
            x ^= x >> 33;
            x *= 0xC4CEB9FE1A85EC53UL;
            x ^= x >> 33;
            return (x >> 11) * (1.0 / 9007199254740992.0);
        }
    }

    // The tone field of the tint (pipeline meadow.py: TONE_*): a value noise over the ground in 2 m cells plus a small per-instance jitter.
    const double ToneLattice = 12.0, ToneAmp = 100.0, ToneJitter = 14.0;
    const int ToneSalt = 2000;

    /// <summary>Smooth noise in [0, 1) at (gx, gy) in cells (floats), bilinear between lattice corners of <paramref name="lattice"/> cells; the same as <c>meadow.value_noise</c>.</summary>
    public static double ValueNoise(double gx, double gy, double lattice, int salt)
    {
        double fx = gx / lattice, fy = gy / lattice;
        double x0 = Math.Floor(fx), y0 = Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        int ix = (int)x0, iy = (int)y0;
        double a = Hash01(ix, iy, salt), b = Hash01(ix + 1, iy, salt), c = Hash01(ix, iy + 1, salt), d = Hash01(ix + 1, iy + 1, salt);
        return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
    }

    /// <summary>The tint byte of an instance: the tone noise at the cell centre, a per-instance jitter hash (salt 0x10000 + layer * 256 + j) and a bias in tint units.</summary>
    public const double DryStep = 10.0;

    public static byte Tint(int gx, int gy, int li, int j, double bias = 0)
    {
        var noise = ValueNoise(gx + 0.5, gy + 0.5, ToneLattice, ToneSalt);
        var jitter = Hash01(gx, gy, 0x10000 + li * 256 + j);
        var t = Math.Floor(ClutterTint.Neutral + (noise - 0.5) * 2 * ToneAmp + (jitter - 0.5) * 2 * ToneJitter + bias + 0.5);
        return (byte)Math.Clamp(t, 0, 255);
    }

    /// <summary>Appends the instances of one cell and layer (count = floor(e) plus one with probability frac(e), e = density / 255 * max).</summary>
    public static void Cell(MeadowFile f, int li, int gx, int gy, List<MeadowInstance> output)
    {
        var v = f.Density(li, gx, gy);
        if (v == 0) return;
        var layer = f.Layers[li];
        var zone = f.Zone(gx, gy);
        var mix = layer.Mix(zone >> 4);
        var bias = ((zone & 15) - 8) * DryStep;
        var e = v / 255.0 * layer.MaxPerCell;
        var whole = Math.Floor(e);
        var count = (int)whole + (Hash01(gx, gy, li * 256) < e - whole ? 1 : 0);
        count = Math.Min(count, MaxPerCellHard);
        for (var j = 0; j < count; j++)
        {
            var salt = li * 256 + 5 * j;
            var x = (gx + 0.5) * f.CellM + (Hash01(gx, gy, salt + 1) * 2 - 1) * f.JitterM;
            var y = (gy + 0.5) * f.CellM + (Hash01(gx, gy, salt + 2) * 2 - 1) * f.JitterM;
            var rot = Hash01(gx, gy, salt + 3) * (2 * Math.PI);
            var scale = layer.ScaleMin + Hash01(gx, gy, salt + 4) * (layer.ScaleMax - layer.ScaleMin);
            var pick = Hash01(gx, gy, salt + 5);
            var kind = mix[^1].Kind;
            var acc = 0.0;
            foreach (var entry in mix)
            {
                acc += entry.Share;
                if (pick < acc) { kind = entry.Kind; break; }
            }
            output.Add(new MeadowInstance(kind, (float)x, (float)y, (float)rot, (float)scale, f.TintKinds.Contains(kind) ? Tint(gx, gy, li, j, bias) : ClutterTint.Neutral));
        }
    }

    /// <summary>Appends the instances of a layer in the square of <paramref name="tileCells"/> cells whose south-west cell is (tx * tileCells, ty * tileCells).</summary>
    public static void Tile(MeadowFile f, int li, int tx, int ty, int tileCells, List<MeadowInstance> output)
    {
        for (var gy = ty * tileCells; gy < (ty + 1) * tileCells; gy++)
            for (var gx = tx * tileCells; gx < (tx + 1) * tileCells; gx++)
                Cell(f, li, gx, gy, output);
    }
}

/// <summary>The streaming numbers of the meadow: how far and how much, in one place.</summary>
public static class MeadowSettings
{
    /// <summary>A tile is this many cells wide (24 m at 2 m cells); each tile and layer is one MultiMesh per kind.</summary>
    public const int TileCells = 12;
    /// <summary>A tile is built when its centre is this much closer than the point where it would start to show (so it is there before it fades in).</summary>
    public const float BuildAheadM = 12f;
    /// <summary>A tile is freed when it is this much further than the point where it stops showing (hysteresis against building it again at once).</summary>
    public const float KeepBehindM = 30f;
    /// <summary>
    /// A kind with at least this many instances in a meadow tile and layer gets its own MultiMesh node; fewer (the strays of a mix, the minor species of a patch) are
    /// merged with the tile's other small groups into one mesh with the tint baked into its vertex colours, so one more species does not mean one more draw call.
    /// </summary>
    public const int MultiMeshMin = 48;

    public static bool UsesMultiMesh(int instances) => instances >= MultiMeshMin;

    /// <summary>Tiles are re-chosen when the focus has moved this far (metres) since the last time.</summary>
    public const float RescanM = 3f;
    /// <summary>Tile builds started per call at most, and the time one call may spend (milliseconds); a build is never split.</summary>
    public const int MaxBuildsPerCall = 6;
    public const double BudgetMs = 2.0;

    /// <summary>Instances alive at once per layer; nearest tiles come first, so the cap trims the far edge.</summary>
    public static int CapOf(string layer) => layer switch
    {
        "tuft" => 9000,
        "fern" => 3500,
        "flower" => 2500,
        "pebble" => 5000,
        "litter" => 6000,
        _ => 3000,
    };

    /// <summary>Whether a layer may still add instances: <paramref name="live"/> alive of its cap (scaled down with the clutter setting, not below a quarter).</summary>
    public static bool HasRoom(string layer, float scale, int live) => live < (int)(CapOf(layer) * Math.Max(scale, 0.25f));

    /// <summary>The distance a layer reaches (its kinds' clutter range times the setting).</summary>
    public static float RangeOf(MeadowLayer layer, float scale)
    {
        var r = 0f;
        foreach (var kind in layer.Kinds) r = Math.Max(r, ClutterSettings.RangeOf(kind));
        return r * scale;
    }

    /// <summary>Half the diagonal of a tile: added to the range, the distance being measured to the tile's centre.</summary>
    public static float TileAllowanceM(float cellM) => TileCells * cellM * 0.7072f;
}
