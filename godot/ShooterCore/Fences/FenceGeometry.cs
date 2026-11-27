using System.Numerics;

namespace Drycastle.ShooterCore.Fences;

/// <summary>The collider shape of a thin section with broken pieces (docs/fence-destruction.md F7), Godot axes, map-root local.</summary>
public static class FenceGeometry
{
    const float MinRunM = 1e-4f;

    /// <summary>Closed boxes (12 outward triangles each, counter-clockwise seen from outside) of the standing runs of <paramref name="s"/>, from Bottom to Top, ThicknessM wide, appended to <paramref name="into"/> (3 vertices a triangle).</summary>
    public static void ColliderTriangles(FenceSectionInfo s, ulong broken, List<Vector3> into)
    {
        var runs = new List<(float From, float To)>(4);
        StandingRuns(s, broken, runs);
        var dir = s.Dir;
        var half = new Vector2(-dir.Y, dir.X) * (s.ThicknessM * 0.5f);
        Span<Vector3> c = stackalloc Vector3[8];
        foreach (var (from, to) in runs)
        {
            var p0 = s.A + dir * from;
            var p1 = s.A + dir * to;
            // corners: along (0 = from, 1 = to), side (0 = right, 1 = left), height (0 = bottom, 1 = top)
            for (var k = 0; k < 8; k++)
            {
                var p = (k & 1) == 0 ? p0 : p1;
                p += ((k & 2) == 0 ? -half : half);
                c[k] = new Vector3(p.X, (k & 4) == 0 ? s.Bottom : s.Top, p.Y);
            }
            var mid = (c[0] + c[7]) * 0.5f;
            Quad(c[0], c[1], c[5], c[4], mid, into); // right side
            Quad(c[2], c[3], c[7], c[6], mid, into); // left side
            Quad(c[0], c[1], c[3], c[2], mid, into); // bottom
            Quad(c[4], c[5], c[7], c[6], mid, into); // top
            Quad(c[0], c[2], c[6], c[4], mid, into); // end at from
            Quad(c[1], c[3], c[7], c[5], mid, into); // end at to
        }
    }

    /// <summary>The runs of standing pieces of <paramref name="s"/> as along ranges (metres from A), in order. A section with no pieces never breaks: one run, all of it. A wall has none.</summary>
    public static void StandingRuns(FenceSectionInfo s, ulong broken, List<(float From, float To)> into)
    {
        if (!s.Thin) return;
        var len = s.Length;
        if (len < MinRunM) return;
        if (s.Pieces <= 0) { into.Add((0, len)); return; }
        var from = -1f; var to = 0f;
        for (var j = 0; j < s.Pieces; j++)
        {
            var (f, t) = s.PieceSpan(j);
            if (t - f < MinRunM) continue;
            if ((broken >> j & 1) != 0)
            {
                if (from >= 0) into.Add((from, to));
                from = -1;
                continue;
            }
            if (from < 0) from = f;
            to = t;
        }
        if (from >= 0) into.Add((from, to));
    }

    // a quad as two triangles, wound so that the normal points away from <inside>
    static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 inside, List<Vector3> into)
    {
        var outward = Vector3.Dot(Vector3.Cross(b - a, c - a), a - inside) >= 0;
        if (outward) { into.Add(a); into.Add(b); into.Add(c); into.Add(a); into.Add(c); into.Add(d); }
        else { into.Add(a); into.Add(c); into.Add(b); into.Add(a); into.Add(d); into.Add(c); }
    }
}
