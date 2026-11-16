namespace Drycastle.ShooterCore;

/// <summary>
/// The pixel work behind the Hunter's Log pictures, Godot-free: the outline of a baked silhouette (a species not hunted yet is drawn as its outline) and the
/// generic blurred blob a hidden Mystery creature is drawn as (a soft animal-like shape, no species in it). Both are alpha masks, one byte per pixel;
/// the screen turns them into textures once and tints them with the theme.
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

    /// <summary>
    /// A blurred animal-like blob facing left (body, neck, head, four legs) in a <paramref name="width"/> x <paramref name="height"/> mask: filled, or only its outline.
    /// <paramref name="softness"/> is the blur as a share of the picture's height. A signed distance to the shape makes the soft edge, so there is nothing sharp for a client to read a species from.
    /// </summary>
    public static byte[] Blob(int width, int height, bool filled, float softness = 0.07f)
    {
        var data = new byte[width * height];
        var band = softness * 0.6f;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
                var d = Distance(u * width / height, v) ; // in units of the picture's height
                var a = filled ? Smooth(softness, -softness, d) : Smooth(band + softness, band, MathF.Abs(d));
                data[y * width + x] = (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);
            }
        return data;
    }

    /// <summary>Signed distance (negative inside) to the blob, with x in picture heights (the picture is about 1.3 high units wide).</summary>
    static float Distance(float x, float y)
    {
        var d = Ellipse(x, y, 0.72f, 0.42f, 0.42f, 0.18f);                    // body
        d = MathF.Min(d, Capsule(x, y, 0.42f, 0.36f, 0.24f, 0.26f, 0.07f));   // neck
        d = MathF.Min(d, Ellipse(x, y, 0.19f, 0.25f, 0.12f, 0.08f));           // head
        foreach (var lx in new[] { 0.44f, 0.54f, 0.86f, 0.96f })               // legs
            d = MathF.Min(d, Capsule(x, y, lx, 0.5f, lx, 0.88f, 0.038f));
        return d;
    }

    static float Ellipse(float x, float y, float cx, float cy, float rx, float ry)
    {
        var nx = (x - cx) / rx; var ny = (y - cy) / ry;
        return (MathF.Sqrt(nx * nx + ny * ny) - 1f) * MathF.Min(rx, ry);
    }

    static float Capsule(float x, float y, float ax, float ay, float bx, float by, float r)
    {
        float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
        var t = Math.Clamp((px * dx + py * dy) / (dx * dx + dy * dy), 0f, 1f);
        float qx = px - dx * t, qy = py - dy * t;
        return MathF.Sqrt(qx * qx + qy * qy) - r;
    }

    /// <summary>A smoothstep: 0 at <paramref name="far"/>, 1 at <paramref name="near"/>.</summary>
    static float Smooth(float far, float near, float v)
    {
        var t = Math.Clamp((v - far) / (near - far), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
