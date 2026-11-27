using System.Numerics;

namespace Drycastle.ShooterCore.Fences;

/// <summary>The thin sections of a map in an 8 m grid (flat x, z), for the shot, blast and ram lookups.</summary>
public sealed class FenceIndex
{
    public const float CellM = 8f;

    readonly IReadOnlyList<FenceSectionInfo> _sections;
    readonly Dictionary<long, List<int>> _cells = new();

    public FenceIndex(IReadOnlyList<FenceSectionInfo> sections)
    {
        _sections = sections;
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (!s.Thin) continue;
            var pad = s.ThicknessM * 0.5f;
            var lo = Vector2.Min(s.A, s.B) - new Vector2(pad);
            var hi = Vector2.Max(s.A, s.B) + new Vector2(pad);
            int x0 = Cell(lo.X), x1 = Cell(hi.X), y0 = Cell(lo.Y), y1 = Cell(hi.Y);
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var key = Key(x, y);
                if (!_cells.TryGetValue(key, out var l)) _cells[key] = l = [];
                l.Add(i);
            }
        }
    }

    /// <summary>Adds to <paramref name="into"/> the index of every thin section within <paramref name="radius"/> of <paramref name="p"/> (flat), each once, ascending.</summary>
    public void Near(Vector2 p, float radius, List<int> into)
    {
        var start = into.Count;
        int x0 = Cell(p.X - radius), x1 = Cell(p.X + radius), y0 = Cell(p.Y - radius), y1 = Cell(p.Y + radius);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            if (!_cells.TryGetValue(Key(x, y), out var l)) continue;
            foreach (var i in l)
                if (Distance(_sections[i], p) <= radius) into.Add(i);
        }
        var n = into.Count - start;
        if (n < 2) return;
        into.Sort(start, n, null);
        var w = start + 1;
        for (var r = start + 1; r < into.Count; r++)
            if (into[r] != into[w - 1]) into[w++] = into[r];
        into.RemoveRange(w, into.Count - w);
    }

    /// <summary>Adds to <paramref name="into"/> the index of every thin section in a cell the flat segment <paramref name="from"/> to <paramref name="to"/> passes (a superset of those it crosses), each once, ascending.</summary>
    public void AlongSegment(Vector2 from, Vector2 to, List<int> into)
    {
        var start = into.Count;
        // every cell the segment passes, in order (a grid walk: the next cell is across whichever cell edge the segment meets first)
        int cx = Cell(from.X), cy = Cell(from.Y), ex = Cell(to.X), ey = Cell(to.Y);
        var d = to - from;
        int sx = Math.Sign(d.X), sy = Math.Sign(d.Y);
        var tdx = sx == 0 ? float.PositiveInfinity : CellM / MathF.Abs(d.X);
        var tdy = sy == 0 ? float.PositiveInfinity : CellM / MathF.Abs(d.Y);
        var tx = sx == 0 ? float.PositiveInfinity : ((sx > 0 ? (cx + 1) * CellM : cx * CellM) - from.X) / d.X;
        var ty = sy == 0 ? float.PositiveInfinity : ((sy > 0 ? (cy + 1) * CellM : cy * CellM) - from.Y) / d.Y;
        for (var guard = Math.Abs(ex - cx) + Math.Abs(ey - cy) + 1; guard > 0; guard--)
        {
            if (_cells.TryGetValue(Key(cx, cy), out var l)) into.AddRange(l);
            if (cx == ex && cy == ey) break;
            if (tx < ty) { cx += sx; tx += tdx; }
            else { cy += sy; ty += tdy; }
        }
        var n = into.Count - start;
        if (n < 2) return;
        into.Sort(start, n, null);
        var w = start + 1;
        for (var r = start + 1; r < into.Count; r++)
            if (into[r] != into[w - 1]) into[w++] = into[r];
        into.RemoveRange(w, into.Count - w);
    }

    /// <summary>Flat distance from <paramref name="p"/> to the section's centre line.</summary>
    internal static float Distance(FenceSectionInfo s, Vector2 p)
    {
        var ab = s.B - s.A;
        var l2 = ab.LengthSquared();
        var t = l2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(p - s.A, ab) / l2, 0f, 1f);
        return Vector2.Distance(p, s.A + ab * t);
    }

    static int Cell(float v) => (int)MathF.Floor(v / CellM);
    static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
}
