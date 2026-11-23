namespace Drycastle.ShooterCore.Hunts;

/// <summary>
/// The pixel work behind the Hunter's Log pictures, Godot-free: the outline of a baked silhouette (a species not hunted yet is drawn as its outline), an alpha
/// mask with one byte per pixel; the screen turns it into a texture once and tints it with the theme.
/// </summary>
public static class HuntArtMath
{
    /// <summary>
    /// The edge of a mask: the pixels of <paramref name="alpha"/> (at least 128) with an empty pixel (or the picture's edge) within <paramref name="reach"/> pixels
    /// either way. A band about <paramref name="reach"/> pixels wide just inside the outline.
    /// </summary>
    public static byte[] Outline(byte[] alpha, int width, int height, int reach)
    {
        var inside = new byte[alpha.Length];
        for (var i = 0; i < alpha.Length; i++) inside[i] = alpha[i] >= 128 ? (byte)1 : (byte)0;
        var narrow = Min1D(Min1D(inside, width, height, reach, horizontal: true), width, height, reach, horizontal: false);
        var edge = new byte[alpha.Length];
        for (var i = 0; i < edge.Length; i++) edge[i] = inside[i] == 1 && narrow[i] == 0 ? (byte)255 : (byte)0;
        return edge;
    }

    static byte[] Min1D(byte[] src, int w, int h, int r, bool horizontal)
    {
        var dst = new byte[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                byte m = 1;
                for (var k = -r; k <= r && m == 1; k++)
                {
                    int sx = horizontal ? x + k : x, sy = horizontal ? y : y + k;
                    if (sx < 0 || sy < 0 || sx >= w || sy >= h || src[sy * w + sx] == 0) m = 0;
                }
                dst[y * w + x] = m;
            }
        return dst;
    }
}
