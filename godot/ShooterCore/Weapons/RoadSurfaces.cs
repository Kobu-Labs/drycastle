using System.Text.Json;

namespace Suchohrad.ShooterCore;

/// <summary>
/// The road surface of the base map (roads.json, the graph's edges): a point is on a road when it lies within half the
/// edge's width of one of its polyline segments. Map metres (x east, y north), Godot-free so the server and the tests share it.
/// </summary>
public sealed class RoadSurfaces
{
    public static readonly RoadSurfaces Empty = new(Array.Empty<Seg>());

    readonly record struct Seg(float Ax, float Ay, float Bx, float By, float HalfWidth, float MinX, float MinY, float MaxX, float MaxY);

    readonly Seg[] _segs;

    RoadSurfaces(Seg[] segs) => _segs = segs;

    public int Count => _segs.Length;

    /// <summary>Parses roads.json. Edges without a polyline or a positive width are skipped; an empty or edge-less file gives <see cref="Empty"/>.</summary>
    public static RoadSurfaces Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array) return Empty;
        var segs = new List<Seg>();
        foreach (var e in edges.EnumerateArray())
        {
            if (!e.TryGetProperty("polyline", out var line) || line.ValueKind != JsonValueKind.Array) continue;
            if (!e.TryGetProperty("width", out var wEl) || !wEl.TryGetDouble(out var width) || width <= 0) continue;
            var half = (float)width / 2f;
            var havePrev = false;
            float px = 0, py = 0;
            foreach (var pt in line.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                var x = (float)pt[0].GetDouble();
                var y = (float)pt[1].GetDouble();
                if (havePrev) segs.Add(MakeSeg(px, py, x, y, half));
                px = x; py = y; havePrev = true;
            }
        }
        return segs.Count == 0 ? Empty : new RoadSurfaces(segs.ToArray());
    }

    static Seg MakeSeg(float ax, float ay, float bx, float by, float half) =>
        new(ax, ay, bx, by, half, Math.Min(ax, bx) - half, Math.Min(ay, by) - half, Math.Max(ax, bx) + half, Math.Max(ay, by) + half);

    /// <summary>True when the map-metre point (x east, y north) is on a road.</summary>
    public bool Contains(float x, float y)
    {
        foreach (var s in _segs)
        {
            if (x < s.MinX || x > s.MaxX || y < s.MinY || y > s.MaxY) continue;
            var dx = s.Bx - s.Ax; var dy = s.By - s.Ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 <= 0 ? 0f : Math.Clamp(((x - s.Ax) * dx + (y - s.Ay) * dy) / len2, 0f, 1f);
            var ex = x - (s.Ax + dx * t); var ey = y - (s.Ay + dy * t);
            if (ex * ex + ey * ey <= s.HalfWidth * s.HalfWidth) return true;
        }
        return false;
    }
}
