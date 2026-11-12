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
/// gives the same cut.
/// </summary>
public static class GlassShards
{
    public static List<GlassShard> Cut(float width, float height, int target, uint seed)
    {
        var cells = Math.Max(1, target / 2);
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
        return shards;
    }
}
