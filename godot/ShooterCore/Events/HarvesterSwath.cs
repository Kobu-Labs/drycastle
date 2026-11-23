using System.Numerics;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Events;

/// <summary>The reel as a box in machine space (#331): <see cref="CentreForwardM"/> ahead of the origin, <see cref="HalfWidthM"/> to each side, <see cref="HalfDepthM"/> fore and aft, from <see cref="BottomM"/> to <see cref="TopM"/> above the origin. Only it cuts crops and catches players; the body does neither.</summary>
public readonly record struct ReelBox(float CentreForwardM, float HalfWidthM, float HalfDepthM, float BottomM, float TopM)
{
    /// <summary>Game position (x, z) of the reel's middle for a machine at <paramref name="origin"/> facing <paramref name="yaw"/>.</summary>
    public Vector2 Centre(Vector2 origin, float yaw) => origin + FieldPlanner.Forward(yaw) * CentreForwardM;

    /// <summary>Whether the point <paramref name="p"/> (game x, y, z) is inside the reel of a machine at <paramref name="origin"/> (its x, y, z) facing <paramref name="yaw"/>.</summary>
    public bool Contains(Vector3 origin, float yaw, Vector3 p)
    {
        var fwd = FieldPlanner.Forward(yaw);
        var right = new Vector2(-fwd.Y, fwd.X);
        var d = new Vector2(p.X - origin.X, p.Z - origin.Z);
        var along = Vector2.Dot(d, fwd) - CentreForwardM;
        var side = Vector2.Dot(d, right);
        var up = p.Y - origin.Y;
        return MathF.Abs(along) <= HalfDepthM && MathF.Abs(side) <= HalfWidthM && up >= BottomM && up <= TopM;
    }
}

/// <summary>A point of a swath in quarter metres (game x, z): what the server and the clients agree on, so every number derived from it is the same on both.</summary>
public readonly record struct SwathPt(int X, int Z)
{
    public const float Unit = 0.25f;
    public static SwathPt From(Vector2 m) => new((int)MathF.Round(m.X / Unit), (int)MathF.Round(m.Y / Unit));
    public Vector2 Metres => new(X * Unit, Z * Unit);
}

/// <summary>
/// Samples the path of a reel (#331): fed the reel's middle each tick, it gives a segment (a, b) every <see cref="HarvesterConfig.SwathSampleM"/> of travel while the machine cuts (driven, moving forward,
/// over a crop field). Standing still, backing up, leaving the field or a jump starts the path afresh.
/// </summary>
public sealed class SwathSampler
{
    readonly float _sampleM;
    SwathPt? _last;
    int _field = -1;

    public SwathSampler(HarvesterConfig cfg) : this(cfg.SwathSampleM) { }

    /// <summary>A sampler with its own spacing: the vehicles' crop crush (<see cref="CropCrushConfig.SampleM"/>) is not the reel's.</summary>
    public SwathSampler(float sampleM) => _sampleM = sampleM;

    public void Reset() { _last = null; _field = -1; }

    /// <summary>One tick. <paramref name="field"/> is the crop field under the reel (-1: none), <paramref name="active"/> whether the machine cuts now.</summary>
    public bool Step(int field, Vector2 reel, bool active, out SwathPt a, out SwathPt b)
    {
        a = b = default;
        if (!active || field < 0) { Reset(); return false; }
        var q = SwathPt.From(reel);
        if (_last is not { } last || field != _field) { _last = q; _field = field; return false; }
        var step = Vector2.Distance(last.Metres, q.Metres);
        if (step < _sampleM) return false;
        _last = q;
        if (step > 30f) return false; // a jump (a respawn), not a drive
        a = last; b = q;
        return true;
    }
}

/// <summary>
/// What a harvester's reel has cut on one field (#331), as everyone derives it from the same segments: the strokes (for a late joiner), the metres cut, and the round bales the machine dropped on the way
/// (<see cref="Bales"/>: one per <see cref="HarvesterConfig.DroppedBaleEveryM"/> on grain and rapeseed, at most <see cref="HarvesterConfig.MaxDroppedBales"/>, where the unloader was). The server
/// <see cref="TryAdd"/>s the segments of the real path (ground cut before does not count again) and sends the kept ones; a client <see cref="Add"/>s what it received. Nothing here is per plant.
/// </summary>
public sealed class FieldSwath
{
    readonly HarvesterConfig _cfg;
    readonly HashSet<(int X, int Z)> _covered = new();
    readonly List<List<SwathPt>> _strokes = new();
    readonly List<(Vector2 Pos, float Yaw)> _bales = new();

    public FieldSwath(HarvesterConfig cfg, bool dropsBales) { _cfg = cfg; DropsBales = dropsBales; }

    public bool DropsBales { get; }
    public float Metres { get; private set; }
    public int PointCount { get; private set; }
    /// <summary>Every kept stretch, as polylines of quarter-metre points; a new one starts where the path jumped or the ground was already cut.</summary>
    public IReadOnlyList<List<SwathPt>> Strokes => _strokes;
    /// <summary>The first stroke whose metres count towards the bales (the ones before belong to a fade that already happened).</summary>
    public int FirstCounted { get; private set; }
    public IReadOnlyList<(Vector2 Pos, float Yaw)> Bales => _bales;

    /// <summary>The server's side: keeps the segment when it is new ground (at least <see cref="HarvesterConfig.SwathNewShare"/> of its swath) and the history has room. True when kept.</summary>
    public bool TryAdd(SwathPt a, SwathPt b, int totalPoints)
    {
        if (PointCount + 2 > _cfg.MaxSwathPoints || totalPoints + 2 > _cfg.MaxSwathPointsTotal) return false;
        var cells = Cells(a, b);
        var fresh = 0;
        foreach (var c in cells) if (!_covered.Contains(c)) fresh++;
        if (cells.Count == 0 || fresh < _cfg.SwathNewShare * cells.Count) return false;
        foreach (var c in cells) _covered.Add(c);
        Append(a, b);
        return true;
    }

    /// <summary>A client's side: a segment the server kept. Every one is taken (the server did the sorting out); the same bales come out of the same segments.</summary>
    public void Add(SwathPt a, SwathPt b) => Append(a, b);

    void Append(SwathPt a, SwathPt b)
    {
        if (_strokes.Count == 0 || _strokes[^1][^1] != a) _strokes.Add(new List<SwathPt> { a });
        _strokes[^1].Add(b);
        PointCount += _strokes[^1].Count == 2 ? 2 : 1;
        var am = a.Metres; var bm = b.Metres;
        Metres += Vector2.Distance(am, bm);
        while (DropsBales && _bales.Count < _cfg.MaxDroppedBales && _cfg.DroppedBaleEveryM > 0 && Metres >= (_bales.Count + 1) * _cfg.DroppedBaleEveryM)
        {
            var dir = Vector2.Normalize(bm - am);
            if (float.IsNaN(dir.X)) break;
            var left = new Vector2(dir.Y, -dir.X); // game: forward (0,-1) is north, its left is west (-1,0)
            _bales.Add((bm - dir * _cfg.DroppedBaleBehindM + left * _cfg.DroppedBaleLeftM, FieldPlanner.YawOf(dir)));
        }
    }

    /// <summary>The bales faded out: they are gone and counting starts over (the stubble stays). The next stretch starts a new stroke.</summary>
    public void FadeBales()
    {
        _bales.Clear(); Metres = 0;
        FirstCounted = _strokes.Count;
    }

    /// <summary>The 1 m ground cells under the swath of the segment (reel width, sampled every half metre along it): what it covers.</summary>
    List<(int, int)> Cells(SwathPt a, SwathPt b)
    {
        var am = a.Metres; var bm = b.Metres;
        var len = Vector2.Distance(am, bm);
        var dir = len < 1e-4f ? Vector2.UnitX : (bm - am) / len;
        var side = new Vector2(-dir.Y, dir.X);
        var half = _cfg.HeaderWidthM / 2f;
        var set = new HashSet<(int, int)>();
        for (var t = 0f; t <= len + 1e-3f; t += 0.5f)
            for (var w = -half; w <= half + 1e-3f; w += 1f)
            {
                var p = am + dir * MathF.Min(t, len) + side * w;
                set.Add(((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)));
            }
        return set.ToList();
    }
}

/// <summary>Wire payload of <see cref="EventKind.HarvesterCut"/>: a polyline of quarter-metre points on one field, the first absolute, the rest as signed byte deltas; never per plant.</summary>
public static class SwathCodec
{
    /// <summary>The most points in one message (header 15 bytes + 2 per point).</summary>
    public const int MaxPoints = 100;
    public const int HeaderBytes = 15;

    /// <summary><paramref name="Replay"/>: history sent to a joiner, not something that just happened (its bales fall silently; a live stretch's pop).</summary>
    public readonly record struct Msg(ushort Field, float WidthM, ushort AgeS, bool NoBales, IReadOnlyList<SwathPt> Points, bool Replay = false);

    /// <summary>Messages for one stroke: u16 field, u8 width in dm, u16 seconds since the field was last cut, u8 flags (1: no bales: history from before a fade; 2: replay for a joiner, not live; older readers ignore the bit), i32 x, i32 z of the first point, u8 count of the rest, then i8 dx, i8 dz each. A long stroke is split (the next message starts at the last point); a delta beyond a byte starts one afresh.</summary>
    public static List<byte[]> Pack(ushort field, float widthM, ushort ageS, bool noBales, IReadOnlyList<SwathPt> stroke, bool replay = false)
    {
        var list = new List<byte[]>();
        var i = 0;
        while (i + 1 < stroke.Count)
        {
            var pts = new List<SwathPt> { stroke[i] };
            var j = i;
            while (j + 1 < stroke.Count && pts.Count < MaxPoints)
            {
                var dx = stroke[j + 1].X - stroke[j].X; var dz = stroke[j + 1].Z - stroke[j].Z;
                if (dx is < -127 or > 127 || dz is < -127 or > 127) break;
                pts.Add(stroke[++j]);
            }
            if (pts.Count >= 2) list.Add(Encode(new Msg(field, widthM, ageS, noBales, pts, replay)));
            i = pts.Count >= 2 ? j : i + 1;
        }
        return list;
    }

    public static byte[] Encode(Msg m)
    {
        var b = new byte[HeaderBytes + 2 * (m.Points.Count - 1)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), m.Field);
        b[2] = (byte)Math.Clamp((int)MathF.Round(m.WidthM * 10f), 0, 255);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), m.AgeS);
        b[5] = (byte)((m.NoBales ? 1 : 0) | (m.Replay ? 2 : 0));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(6), m.Points[0].X);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), m.Points[0].Z);
        b[14] = (byte)(m.Points.Count - 1);
        for (var i = 1; i < m.Points.Count; i++)
        {
            b[HeaderBytes + 2 * (i - 1)] = (byte)(sbyte)(m.Points[i].X - m.Points[i - 1].X);
            b[HeaderBytes + 2 * (i - 1) + 1] = (byte)(sbyte)(m.Points[i].Z - m.Points[i - 1].Z);
        }
        return b;
    }

    public static Msg? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderBytes) return null;
        int n = d[14];
        if (n < 1 || d.Length != HeaderBytes + 2 * n) return null;
        var pts = new List<SwathPt>(n + 1) { new(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(d[6..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(d[10..])) };
        for (var i = 0; i < n; i++) pts.Add(new SwathPt(pts[^1].X + (sbyte)d[HeaderBytes + 2 * i], pts[^1].Z + (sbyte)d[HeaderBytes + 2 * i + 1]));
        return new Msg(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d), d[2] / 10f, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d[3..]), (d[5] & 1) != 0, pts, (d[5] & 2) != 0);
    }
}
