using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>One fragment of a shattered pane: a triangle in the pane's plane (x along its width, y up it, the origin at its centre).</summary>
public readonly record struct GlassShard(Vector2 A, Vector2 B, Vector2 C)
{
    public Vector2 Centre => (A + B + C) / 3f;
    public float Area => MathF.Abs((B.X - A.X) * (C.Y - A.Y) - (C.X - A.X) * (B.Y - A.Y)) / 2f;
}

/// <summary>
/// Cuts a pane into fragments for the shatter animation: a grid of near-square cells with its inner corners moved at random, each cell
/// split along one diagonal. The fragments tile the pane exactly, so the first frame of the animation is the whole pane. The same seed
/// gives the same cut. A pane with an outline (a triangle, a trapezoid: convex, counter-clockwise) is cut from the same grid: every triangle
/// is clipped to the outline, so the fragments tile the outline exactly and the ones on its edge take its shape.
/// </summary>
public static class GlassShards
{
    public static List<GlassShard> Cut(float width, float height, int target, uint seed, IReadOnlyList<Vector2>? outline = null)
    {
        if (outline is { Count: < 3 }) outline = null;
        // the grid covers the bounding box: a pane that fills less of it gets more cells, so it still breaks into about `target` fragments
        var fill = outline == null ? 1f : Math.Clamp(Area(outline) / (width * height), 0.25f, 1f);
        var cells = Math.Max(1, (int)MathF.Round(target / 2 / fill));
        var cols = Math.Clamp((int)MathF.Round(MathF.Sqrt(cells * width / height)), 1, cells);
        var rows = Math.Max(1, (int)MathF.Ceiling(cells / (float)cols));
        float cw = width / cols, ch = height / rows;
        var rng = new Random(unchecked((int)seed));
        var pts = new Vector2[cols + 1, rows + 1];
        for (var i = 0; i <= cols; i++)
        for (var j = 0; j <= rows; j++)
        {
            var p = new Vector2(-width / 2 + i * cw, -height / 2 + j * ch);
            // the pane's edge stays where it is; inner corners move by up to 22 % of a cell, so every cell stays convex and either diagonal splits it cleanly
            if (i > 0 && i < cols) p.X += (rng.NextSingle() * 2 - 1) * 0.22f * cw;
            if (j > 0 && j < rows) p.Y += (rng.NextSingle() * 2 - 1) * 0.22f * ch;
            pts[i, j] = p;
        }
        var shards = new List<GlassShard>(cols * rows * 2);
        for (var i = 0; i < cols; i++)
        for (var j = 0; j < rows; j++)
        {
            Vector2 a = pts[i, j], b = pts[i + 1, j], c = pts[i + 1, j + 1], d = pts[i, j + 1];
            if (rng.Next(2) == 0) { shards.Add(new GlassShard(a, b, c)); shards.Add(new GlassShard(a, c, d)); }
            else { shards.Add(new GlassShard(a, b, d)); shards.Add(new GlassShard(b, c, d)); }
        }
        return outline == null ? shards : Clip(shards, outline);
    }

    /// <summary>Area of a polygon (any winding).</summary>
    public static float Area(IReadOnlyList<Vector2> poly)
    {
        float a = 0;
        for (var i = 0; i < poly.Count; i++) { var p = poly[i]; var q = poly[(i + 1) % poly.Count]; a += p.X * q.Y - q.X * p.Y; }
        return MathF.Abs(a) / 2;
    }

    /// <summary>Each fragment cut to the convex counter-clockwise <paramref name="outline"/> (Sutherland-Hodgman, one edge at a time), the rest fanned into triangles. Slivers go.</summary>
    static List<GlassShard> Clip(List<GlassShard> shards, IReadOnlyList<Vector2> outline)
    {
        var result = new List<GlassShard>(shards.Count);
        var poly = new List<Vector2>(8);
        var next = new List<Vector2>(8);
        foreach (var shard in shards)
        {
            poly.Clear(); poly.Add(shard.A); poly.Add(shard.B); poly.Add(shard.C);
            for (var e = 0; e < outline.Count && poly.Count >= 3; e++)
            {
                Vector2 a = outline[e], b = outline[(e + 1) % outline.Count];
                float Side(Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X); // >= 0: on the inside of a counter-clockwise edge
                next.Clear();
                for (var i = 0; i < poly.Count; i++)
                {
                    Vector2 p = poly[i], q = poly[(i + 1) % poly.Count];
                    float sp = Side(p), sq = Side(q);
                    if (sp >= 0) next.Add(p);
                    if (sp >= 0 != sq >= 0) next.Add(p + (q - p) * (sp / (sp - sq)));
                }
                (poly, next) = (next, poly);
            }
            for (var i = 1; i + 1 < poly.Count; i++)
            {
                var piece = new GlassShard(poly[0], poly[i], poly[i + 1]);
                if (piece.Area > 1e-5f) result.Add(piece);
            }
        }
        return result;
    }
}
