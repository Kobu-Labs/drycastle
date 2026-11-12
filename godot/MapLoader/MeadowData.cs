using System.IO.Compression;

namespace Suchohrad.MapLoader;

// Pure (no Godot types): compiled into ShooterCore.Tests by a link. The format and the generator are specified in
// pipeline/src/suchohrad_pipeline/meadow.py, which has the same hash and the same instance rules (a test pins both).

/// <summary>One generated piece of meadow clutter. Map metres, x east, y north (no height: the loader reads the ground); Rot is counter-clockwise seen from above.</summary>
public readonly record struct MeadowInstance(string Kind, float X, float Y, float Rot, float Scale);

/// <summary>A layer of the raster: which clutter kind it makes (and which other kind for <see cref="AltShare"/> of the instances), at most <see cref="MaxPerCell"/> per cell.</summary>
public sealed record MeadowLayer(string Name, string Kind, string AltKind, float AltShare, float MaxPerCell, float ScaleMin, float ScaleMax);

/// <summary>meadow.bin (manifest <c>files.meadow</c>): the density raster of the clutter the game generates near the player on fields and in forests.</summary>
public sealed class MeadowFile
{
    public const int SupportedFormat = 1;
    static readonly byte[] Magic = "MEAD"u8.ToArray();

    public float CellM { get; private init; }
    public float ChunkM { get; private init; }
    public int CellsPerChunk { get; private init; }
    /// <summary>An instance lies within this many metres of its cell's centre (the raster is nonzero only where that whole window is free).</summary>
    public float JitterM { get; private init; }
    /// <summary>A generated instance within this many metres times the tree's scale of a trunk is dropped.</summary>
    public float TrunkReachM { get; private init; }
    public IReadOnlyList<MeadowLayer> Layers { get; private init; } = Array.Empty<MeadowLayer>();
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
            var count = r.ReadByte();
            string Str() { var n = r.ReadByte(); return System.Text.Encoding.UTF8.GetString(r.ReadBytes(n)); }
            var layers = new List<MeadowLayer>();
            for (var i = 0; i < count; i++)
            {
                var name = Str(); var kind = Str(); var alt = Str();
                layers.Add(new MeadowLayer(name, kind, alt, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            }
            if (cellMm == 0 || cells == 0 || count == 0) throw new FormatException("meadow.bin: empty header");
            var file = new MeadowFile { CellM = cellMm / 1000f, ChunkM = chunkM, CellsPerChunk = cells, JitterM = jitter, TrunkReachM = reach, Layers = layers };
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

    /// <summary>Layer-major bytes of one chunk ("layers" x cells x cells, y from the south, x from the west), or null when the chunk has none.</summary>
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
            if (result.Length != Layers.Count * CellsPerChunk * CellsPerChunk) throw new FormatException($"meadow.bin: chunk {cx},{cy} has {result.Length} bytes");
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

    /// <summary>Appends the instances of one cell and layer (count = floor(e) plus one with probability frac(e), e = density / 255 * max).</summary>
    public static void Cell(MeadowFile f, int li, int gx, int gy, List<MeadowInstance> output)
    {
        var v = f.Density(li, gx, gy);
        if (v == 0) return;
        var layer = f.Layers[li];
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
            var kind = layer.AltKind.Length > 0 && Hash01(gx, gy, salt + 5) < layer.AltShare ? layer.AltKind : layer.Kind;
            output.Add(new MeadowInstance(kind, (float)x, (float)y, (float)rot, (float)scale));
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
        var r = ClutterSettings.RangeOf(layer.Kind);
        if (layer.AltKind.Length > 0) r = Math.Max(r, ClutterSettings.RangeOf(layer.AltKind));
        return r * scale;
    }

    /// <summary>Half the diagonal of a tile: added to the range, the distance being measured to the tile's centre.</summary>
    public static float TileAllowanceM(float cellM) => TileCells * cellM * 0.7072f;
}
