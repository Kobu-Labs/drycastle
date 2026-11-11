using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>#170: the minimap's scanline fill agrees with a per-point even-odd test on every texel.</summary>
public class ScanlineFillTests
{
    // the same crossing rule as ParcelInfo.Contains, one point at a time
    static bool Inside(IReadOnlyList<Vector2[]> rings, Vector2 p)
    {
        var inside = false;
        foreach (var ring in rings)
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
        return inside;
    }

    static void AssertMatchesPointTest(IReadOnlyList<Vector2[]> rings, float x0, float y0, float step, int side)
    {
        var xs = new List<float>();
        var spans = new List<(int From, int To)>();
        for (var v = 0; v < side; v++)
        {
            var y = y0 + (v + 0.5f) * step;
            ScanlineFill.Crossings(rings, y, xs);
            ScanlineFill.Spans(xs, x0, step, 0, side - 1, spans);
            var filled = new bool[side];
            foreach (var (from, to) in spans)
                for (var u = from; u <= to; u++) filled[u] = true;
            for (var u = 0; u < side; u++)
            {
                var p = new Vector2(x0 + (u + 0.5f) * step, y);
                Assert.True(filled[u] == Inside(rings, p), $"texel ({u},{v}) at {p}: scanline {filled[u]}, point test {Inside(rings, p)}");
            }
        }
    }

    [Fact]
    public void ConvexTriangleMatchesThePointTest()
    {
        var tri = new[] { new Vector2(1.3f, 0.7f), new Vector2(9.1f, 2.4f), new Vector2(4.2f, 8.8f) };
        AssertMatchesPointTest(new[] { tri }, 0f, 0f, 0.37f, 30);
    }

    [Fact]
    public void RingWithAHoleIsEvenOdd()
    {
        var outer = new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) };
        var hole = new[] { new Vector2(3, 3), new Vector2(7, 3), new Vector2(7, 7), new Vector2(3, 7) };
        AssertMatchesPointTest(new[] { outer, hole }, 0f, 0f, 0.25f, 40);
    }

    [Fact]
    public void ConcaveOutlineMatchesThePointTest()
    {
        // an L shape: the notch must stay empty
        var l = new[]
        {
            new Vector2(0, 0), new Vector2(8, 0), new Vector2(8, 3), new Vector2(3, 3),
            new Vector2(3, 8), new Vector2(0, 8),
        };
        AssertMatchesPointTest(new[] { l }, -1f, -1f, 0.2f, 60);
    }

    [Fact]
    public void SpansAreClippedToTheBoxAndEmptyRowsGiveNothing()
    {
        var xs = new List<float> { 2f, 6f };
        var spans = new List<(int From, int To)>();
        ScanlineFill.Spans(xs, 0f, 1f, 0, 10, spans);
        Assert.Equal(new (int, int)[] { (2, 5) }, spans.ToArray());
        ScanlineFill.Spans(xs, 0f, 1f, 4, 4, spans);
        Assert.Equal(new (int, int)[] { (4, 4) }, spans.ToArray());
        ScanlineFill.Spans(new List<float>(), 0f, 1f, 0, 10, spans);
        Assert.Empty(spans);
    }
}
