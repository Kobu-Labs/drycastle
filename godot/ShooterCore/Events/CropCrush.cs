using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Every number of the vehicles' crop damage (#328): a ground vehicle driving through a crop field crushes the plants in its path. Cosmetic: no collision, no slowdown.</summary>
public sealed class CropCrushConfig
{
    /// <summary>Metres between samples of a vehicle's path (the segments the footprint is stamped along).</summary>
    public float SampleM { get; set; } = 2;
    /// <summary>A vehicle crushes while it moves faster than <see cref="MinSpeedMps"/> with somebody at the wheel, or faster than <see cref="FreeSpeedMps"/> without.</summary>
    public float MinSpeedMps { get; set; } = 0.6f;
    public float FreeSpeedMps { get; set; } = 2f;
    /// <summary>The crushed strip's width, as a share of the vehicle's body width (vehicles.json has no wheel positions: the tracks are the body's).</summary>
    public float WidthShare { get; set; } = 0.95f;
    /// <summary>Side of a mask cell, metres.</summary>
    public float CellM { get; set; } = 1;
    /// <summary>A field's mask has at most this many cells (its bounding box); a bigger field is not tracked (the village's largest, GP-0084 at ~273k, is). All fields together keep at most <see cref="MaxCellsTotal"/> crushed cells.</summary>
    public int MaxCellsPerField { get; set; } = 1 << 20; // 1 MB of bools: the village's largest field is ~273k cells
    public int MaxCellsTotal { get; set; } = 120000;
    /// <summary>The most bytes of one crush message (a late joiner's history comes in these).</summary>
    public int MaxMessageBytes { get; set; } = 200;
    /// <summary>The server sends the cells that are new this often, seconds (a client already stamped what it drew, see <see cref="CropCrushMask"/>; this is for the others, joiners and missed cells).</summary>
    public float FlushS { get; set; } = 0.1f;
    /// <summary>A vehicle's direction of travel (which end leads) is decided after it moved this far, metres, so jitter at a standstill does not flip it.</summary>
    public float DirectionM { get; set; } = 0.25f;
}

/// <summary>
/// What vehicles crushed on one crop field (#328): a bit per 1 m cell of the field's bounding box, so a field costs at most a few KB, whatever is driven over it and however often.
/// Ground driven over before adds nothing (<see cref="Mark"/> returns only the cells that are new, which is all that is sent). Nothing is per plant: a client lays the plants in those cells flat.
/// </summary>
public sealed class CrushGrid
{
    readonly CropCrushConfig _cfg;
    readonly bool[] _bits;

    CrushGrid(CropCrushConfig cfg, int ox, int oz, int w, int h)
    {
        _cfg = cfg; OriginX = ox; OriginZ = oz; Width = w; Height = h; _bits = new bool[w * h];
    }

    /// <summary>The grid of a field's ring (game x, z), or null when its bounding box has more than <see cref="CropCrushConfig.MaxCellsPerField"/> cells. Server and clients build it the same way.</summary>
    public static CrushGrid? For(IReadOnlyList<Vector2> ring, CropCrushConfig cfg)
    {
        if (ring.Count < 3) return null;
        var c = cfg.CellM;
        var ox = (int)MathF.Floor(ring.Min(p => p.X) / c); var oz = (int)MathF.Floor(ring.Min(p => p.Y) / c);
        var w = (int)MathF.Ceiling(ring.Max(p => p.X) / c) - ox; var h = (int)MathF.Ceiling(ring.Max(p => p.Y) / c) - oz;
        if (w <= 0 || h <= 0 || (long)w * h > cfg.MaxCellsPerField) return null;
        return new CrushGrid(cfg, ox, oz, w, h);
    }

    public int OriginX { get; }
    public int OriginZ { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellCount => _bits.Length;
    public int Count { get; private set; }

    public bool IsSet(int index) => index >= 0 && index < _bits.Length && _bits[index];

    /// <summary>The cell (in cell units, so metres / <see cref="CropCrushConfig.CellM"/>) a grid index is.</summary>
    public (int X, int Z) CellOf(int index) => (OriginX + index % Width, OriginZ + index / Width);

    /// <summary>Centre of a cell in game metres.</summary>
    public Vector2 CentreOf(int index)
    {
        var (x, z) = CellOf(index);
        return new Vector2((x + 0.5f) * _cfg.CellM, (z + 0.5f) * _cfg.CellM);
    }

    /// <summary>Sets a cell (as a client applies what it was told). True when it was not set; a full total or an index outside is refused.</summary>
    public bool Set(int index, int totalCells)
    {
        if (index < 0 || index >= _bits.Length || _bits[index] || totalCells >= _cfg.MaxCellsTotal) return false;
        _bits[index] = true; Count++;
        return true;
    }

    /// <summary>Stamps the strip along the segment <paramref name="a"/>-<paramref name="b"/> (game x, z), <paramref name="halfWidth"/> to each side: the indices of the cells that were not crushed before (ascending). <paramref name="totalCells"/> is how many cells all fields have crushed, for the cap.</summary>
    public List<int> Mark(Vector2 a, Vector2 b, float halfWidth, int totalCells)
    {
        var fresh = new List<int>();
        Mark(a, b, halfWidth, totalCells, fresh);
        return fresh;
    }

    /// <summary>As <see cref="Mark(Vector2, Vector2, float, int)"/>, appending the new cells to <paramref name="fresh"/> (no allocation per call: the per-frame path of a client). Returns how many were added.</summary>
    public int Mark(Vector2 a, Vector2 b, float halfWidth, int totalCells, List<int> fresh)
    {
        var first = fresh.Count;
        var c = _cfg.CellM;
        var x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.X, b.X) - halfWidth) / c) - OriginX);
        var x1 = Math.Min(Width - 1, (int)MathF.Floor((MathF.Max(a.X, b.X) + halfWidth) / c) - OriginX);
        var z0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.Y, b.Y) - halfWidth) / c) - OriginZ);
        var z1 = Math.Min(Height - 1, (int)MathF.Floor((MathF.Max(a.Y, b.Y) + halfWidth) / c) - OriginZ);
        for (var cz = z0; cz <= z1; cz++)
            for (var cx = x0; cx <= x1; cx++)
            {
                var i = cz * Width + cx;
                if (_bits[i]) continue;
                var centre = new Vector2((OriginX + cx + 0.5f) * c, (OriginZ + cz + 0.5f) * c);
                if (Poly.DistanceToSegment(centre, a, b) > halfWidth) continue;
                if (Set(i, totalCells + fresh.Count - first)) fresh.Add(i);
            }
        return fresh.Count - first;
    }

    /// <summary>The crushed cells as alternating run lengths over the whole grid: first how many are clear, then how many crushed, and so on (a late joiner's copy of the mask).</summary>
    public List<int> Runs()
    {
        var runs = new List<int>();
        var cur = false; var len = 0;
        foreach (var bit in _bits)
        {
            if (bit == cur) { len++; continue; }
            runs.Add(len); cur = bit; len = 1;
        }
        if (cur) runs.Add(len);
        return runs;
    }
}

/// <summary>Samples what a ground vehicle crushes: the path every <see cref="CropCrushConfig.SampleM"/>, on a crop field, while it moves.</summary>
public static class CropCrushRules
{
    /// <summary>Whether a vehicle at <paramref name="speed"/> (m/s, any direction) crushes: moving with somebody at the wheel, or fast enough to be rolling on its own.</summary>
    public static bool Crushes(CropCrushConfig cfg, float speed, bool driven) => MathF.Abs(speed) >= (driven ? cfg.MinSpeedMps : cfg.FreeSpeedMps);

    /// <summary>The half width of the strip for a vehicle of half width <paramref name="halfWidth"/>.</summary>
    public static float HalfStrip(CropCrushConfig cfg, float halfWidth) => halfWidth * cfg.WidthShare;

    /// <summary>How far ahead of the vehicle's centre the strip is stamped, metres: the leading end, so the rounded end of the strip (its half width) ends at the bumper. The track is under the nose, not half a body behind it.</summary>
    public static float LeadM(CropCrushConfig cfg, float halfWidth, float halfLength) => MathF.Max(0f, halfLength - HalfStrip(cfg, halfWidth));
}

/// <summary>Wire payload of <see cref="EventKind.CropCrush"/> (little-endian, varints): u16 field, u8 mode. Mode 0: the cells that are new, as ascending grid indices, each the varint difference to the one before (the first from 0). Mode 1: a stretch of the mask, u32 grid index it starts at, then alternating run lengths (clear first) as varints.</summary>
public static class CrushCodec
{
    public const byte Delta = 0, RunsMode = 1;

    public readonly record struct Msg(ushort Field, byte Mode, int Start, IReadOnlyList<int> Values);

    static void Varint(List<byte> b, uint v) { while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); }

    static bool ReadVarint(ReadOnlySpan<byte> d, ref int pos, out uint v)
    {
        v = 0; var shift = 0;
        while (pos < d.Length && shift <= 28) { var x = d[pos++]; v |= (uint)(x & 0x7F) << shift; if ((x & 0x80) == 0) return true; shift += 7; }
        return false;
    }

    /// <summary>Messages for new cells (ascending indices), none longer than <paramref name="maxBytes"/>.</summary>
    public static List<byte[]> PackDelta(ushort field, IReadOnlyList<int> cells, int maxBytes)
    {
        var list = new List<byte[]>();
        var b = new List<byte>(); var prev = 0;
        void Header() { b.Clear(); b.Add((byte)field); b.Add((byte)(field >> 8)); b.Add(Delta); prev = 0; }
        Header();
        foreach (var c in cells)
        {
            if (b.Count + 5 > maxBytes) { list.Add(b.ToArray()); Header(); }
            Varint(b, (uint)(c - prev)); prev = c;
        }
        if (b.Count > 3) list.Add(b.ToArray());
        return list;
    }

    /// <summary>Messages for a whole mask given as run lengths (<see cref="CrushGrid.Runs"/>), none longer than <paramref name="maxBytes"/>; a stretch ends at a run boundary.</summary>
    public static List<byte[]> PackRuns(ushort field, IReadOnlyList<int> runs, int maxBytes)
    {
        var list = new List<byte[]>();
        long start = 0, here = 0;
        var b = new List<byte>(); var any = false;
        void Header(long at) { b.Clear(); b.Add((byte)field); b.Add((byte)(field >> 8)); b.Add(RunsMode); b.Add((byte)at); b.Add((byte)(at >> 8)); b.Add((byte)(at >> 16)); b.Add((byte)(at >> 24)); any = false; }
        Header(0);
        for (var i = 0; i < runs.Count; i++)
        {
            if (b.Count + 5 > maxBytes && any && i % 2 == 0) { list.Add(b.ToArray()); start = here; Header(start); }
            Varint(b, (uint)runs[i]); here += runs[i]; any = true;
        }
        if (any && runs.Count > 0) list.Add(b.ToArray());
        return list;
    }

    public static Msg? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4) return null;
        var field = (ushort)(d[0] | d[1] << 8); var mode = d[2];
        var pos = 3; var vals = new List<int>();
        if (mode == Delta)
        {
            var prev = 0;
            while (pos < d.Length) { if (!ReadVarint(d, ref pos, out var v)) return null; prev += (int)v; vals.Add(prev); }
            return new Msg(field, mode, 0, vals);
        }
        if (mode != RunsMode || d.Length < 8) return null;
        var start = d[3] | d[4] << 8 | d[5] << 16 | d[6] << 24; pos = 7;
        while (pos < d.Length) { if (!ReadVarint(d, ref pos, out var v)) return null; vals.Add((int)v); }
        return new Msg(field, mode, start, vals);
    }

    /// <summary>The grid indices a message sets: the cells of a delta, or the crushed cells of the runs.</summary>
    public static IEnumerable<int> Cells(Msg m)
    {
        if (m.Mode == Delta) { foreach (var c in m.Values) yield return c; yield break; }
        var at = m.Start; var set = false;
        foreach (var run in m.Values)
        {
            if (set) for (var i = 0; i < run; i++) yield return at + i;
            at += run; set = !set;
        }
    }
}
