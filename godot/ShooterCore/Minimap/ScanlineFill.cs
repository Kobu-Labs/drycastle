using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Scanline point-in-polygon for the minimap terrain texture (#170, T2-19). Instead of testing every texel of a parcel's
/// box, each texel row takes the edge crossings once and fills the spans between them. Uses the same crossing rule as
/// <c>ParcelInfo.Contains</c>: even-odd over all rings, a point is inside when the crossings to its right are odd.
/// </summary>
public static class ScanlineFill
{
    /// <summary>Clears <paramref name="xs"/> and fills it with the x of every ring edge crossing the horizontal line at <paramref name="y"/>, sorted.</summary>
    public static void Crossings(IReadOnlyList<Vector2[]> rings, float y, List<float> xs)
    {
        xs.Clear();
        foreach (var ring in rings)
        {
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Y > y) != (b.Y > y)) xs.Add((b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X);
            }
        }
        xs.Sort();
    }

    /// <summary>
    /// The inclusive texel spans on one row, clipped to [<paramref name="u0"/>, <paramref name="u1"/>], whose centres are inside
    /// the crossings <paramref name="xs"/> (sorted, from <see cref="Crossings"/>). Texel u's centre is at x0 + (u + 0.5) * step.
    /// </summary>
    public static void Spans(List<float> xs, float x0, float step, int u0, int u1, List<(int From, int To)> spans)
    {
        spans.Clear();
        for (var k = 0; k + 1 < xs.Count; k += 2)
        {
            // a centre at x is inside for xs[k] <= x < xs[k + 1]
            var from = (int)MathF.Ceiling((xs[k] - x0) / step - 0.5f);
            var to = (int)MathF.Ceiling((xs[k + 1] - x0) / step - 0.5f) - 1;
            from = Math.Max(from, u0);
            to = Math.Min(to, u1);
            if (from <= to) spans.Add((from, to));
        }
    }
}
