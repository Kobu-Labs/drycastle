using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// A place on a placed utility pole or house connector where a wire sits, in map space (Godot axes, Y up). Kind is "line" (crossarm and
/// top insulators), "service" (side brackets) or "bundle" (hooks); Tier is the crossarm's index, -1 for a seat that is
/// on no crossarm.
/// </summary>
public readonly record struct WireSeat(string Kind, int Tier, Vector3 Pos);

/// <summary>One wire of a cable: the points it hangs through (first on one pole, last on the other) and its radius.</summary>
public sealed record CableWire(Vector3[] Points, float Radius, string Kind);

/// <summary>
/// The wires of a cable between two placed utility poles. The poles stand however they were turned; the wires adapt:
/// on each pole the crossarm that stands most squarely across the span is used, its seats are ordered from left to
/// right as seen along the span, and the n-th seat of one pole is joined to the n-th of the other. So wires keep their
/// spacing and never cross, whichever way a pole faces. When one pole has more seats, the spare ones stay empty (the
/// ones used are spread evenly over the arm). A lower service cable joins the side brackets when both poles have one,
/// and a thick bundled cable the hooks.
/// A house connector is an end like a pole: one with insulators takes as many of the pole's line wires as it has
/// insulators; one that is only a hook for a bundled cable gets a single thick cable from the pole's hook, else from
/// its side bracket, else from the nearest insulator of its crossarm.
/// </summary>
public static class CableWires
{
    public const float LineRadius = 0.011f, ServiceRadius = 0.014f, BundleRadius = 0.02f;
    /// <summary>How deep a wire hangs at mid span, as a fraction of its length (each wire a little more or less).</summary>
    public const float LineSag = 0.025f, ServiceSag = 0.035f;

    public static List<CableWire> Span(IReadOnlyList<WireSeat> a, IReadOnlyList<WireSeat> b, string cableId)
    {
        var wires = new List<CableWire>();
        if (a.Count == 0 || b.Count == 0) return wires;
        Vector3 ca = Centre(a), cb = Centre(b);
        var d = new Vector3(cb.X - ca.X, 0, cb.Z - ca.Z);
        if (d.Length() < 0.05f) return wires;
        d = Vector3.Normalize(d);
        var side = new Vector3(-d.Z, 0, d.X);

        var la = LineSeats(a, side);
        var lb = LineSeats(b, side);
        var n = Math.Min(la.Count, lb.Count);
        var pa = Spread(la, n);
        var pb = Spread(lb, n);
        for (var i = 0; i < n; i++)
            wires.Add(Hang(pa[i], pb[i], LineSag * (0.85f + 0.35f * Hash01(cableId, i)), LineRadius, "line"));

        if (Nearest(a, "service", cb) is { } sa && Nearest(b, "service", ca) is { } sb)
            wires.Add(Hang(sa, sb, ServiceSag * (0.85f + 0.35f * Hash01(cableId, 100)), ServiceRadius, "service"));
        var ba = Nearest(a, "bundle", cb);
        var bb = Nearest(b, "bundle", ca);
        // an end that is nothing but a hook (a house connector) takes its cable from whatever the other end has
        if (ba != null && bb == null && la.Count == 0) bb = Nearest(b, "service", ca) ?? Nearest(b, "line", ca);
        if (bb != null && ba == null && lb.Count == 0) ba = Nearest(a, "service", cb) ?? Nearest(a, "line", cb);
        if (ba is { } p && bb is { } q)
            wires.Add(Hang(p, q, ServiceSag * (0.9f + 0.3f * Hash01(cableId, 200)), BundleRadius, "bundle"));
        return wires;
    }

    static Vector3 Centre(IReadOnlyList<WireSeat> seats)
    {
        var c = Vector3.Zero;
        foreach (var s in seats) c += s.Pos;
        return c / seats.Count;
    }

    /// <summary>
    /// The line seats of the crossarm that is widest across the span, left to right. Seats on no crossarm (the pin on
    /// top of the column) count to the first crossarm, or stand alone when the pole has none.
    /// </summary>
    static List<Vector3> LineSeats(IReadOnlyList<WireSeat> seats, Vector3 side)
    {
        var line = seats.Where(s => s.Kind == "line").ToList();
        if (line.Count == 0) return new();
        var first = line.Where(s => s.Tier >= 0).Select(s => s.Tier).DefaultIfEmpty(-1).Min();
        var best = new List<Vector3>();
        var bestWidth = -1f;
        foreach (var group in line.GroupBy(s => s.Tier < 0 ? first : s.Tier).OrderBy(g => g.Key))
        {
            var pts = group.Select(s => s.Pos).OrderBy(p => Vector3.Dot(p, side)).ToList();
            var width = Vector3.Dot(pts[^1] - pts[0], side);
            if (width > bestWidth + 0.05f) { best = pts; bestWidth = width; } // a tie goes to the upper arm
        }
        return best;
    }

    /// <summary>n of the seats, spread evenly over the arm and still in order (all of them when n is their number).</summary>
    static List<Vector3> Spread(List<Vector3> seats, int n)
    {
        if (n >= seats.Count) return seats;
        var picked = new List<Vector3>(n);
        for (var i = 0; i < n; i++)
            picked.Add(seats[n == 1 ? (seats.Count - 1) / 2 : (int)MathF.Round(i * (seats.Count - 1) / (float)(n - 1))]);
        return picked;
    }

    static Vector3? Nearest(IReadOnlyList<WireSeat> seats, string kind, Vector3 to)
    {
        Vector3? best = null;
        var bestD = float.MaxValue;
        foreach (var s in seats)
        {
            if (s.Kind != kind) continue;
            var dist = Vector3.DistanceSquared(s.Pos, to);
            if (dist < bestD) { bestD = dist; best = s.Pos; }
        }
        return best;
    }

    /// <summary>A wire hanging from p to q: a parabola, sagRatio of its length deep in the middle.</summary>
    public static CableWire Hang(Vector3 p, Vector3 q, float sagRatio, float radius, string kind)
    {
        var length = Vector3.Distance(p, q);
        var n = Math.Clamp((int)MathF.Round(length / 3f), 6, 16);
        var pts = new Vector3[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var t = i / (float)n;
            pts[i] = Vector3.Lerp(p, q, t) - Vector3.UnitY * (length * sagRatio * 4f * t * (1f - t));
        }
        return new CableWire(pts, radius, kind);
    }

    /// <summary>A stable number in [0, 1) from a cable id and a wire index (so a cable hangs the same every time).</summary>
    public static float Hash01(string id, int index)
    {
        var h = 2166136261u;
        foreach (var c in id) h = (h ^ c) * 16777619u;
        h = (h ^ (uint)index) * 16777619u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)(1 << 24);
    }
}
