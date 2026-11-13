using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The tiling detail texture of the water surface, generated once in code (no binary asset): a 128 texel periodic value
/// noise of two octaves. Each texel holds the tangent normal of the height field (rgb, encoded 0..1 as the shader decodes
/// it) and the height itself (alpha, used for the foam breakup).
/// </summary>
public static class WaterNoiseTexture
{
    const int Size = 128;
    const float NormalStrength = 6f;
    static ImageTexture? _tex;

    public static ImageTexture Get() => _tex ??= Build();

    static ImageTexture Build()
    {
        var h = Height();
        var img = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            // central differences with wrap-around, so the texture tiles
            var dx = (At(h, x + 1, y) - At(h, x - 1, y)) * 0.5f * NormalStrength;
            var dy = (At(h, x, y + 1) - At(h, x, y - 1)) * 0.5f * NormalStrength;
            var n = new Vector3(-dx, -dy, 1f).Normalized();
            img.SetPixel(x, y, new Color(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f, h[y * Size + x]));
        }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    static float At(float[] h, int x, int y) => h[((y % Size) + Size) % Size * Size + ((x % Size) + Size) % Size];

    /// <summary>Two octaves of periodic value noise, 8 and 16 cells per tile, normalised to 0..1.</summary>
    static float[] Height()
    {
        var rng = new Random(20240611);
        var coarse = Lattice(8, rng);
        var fine = Lattice(16, rng);
        var h = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var u = x / (float)Size;
            var v = y / (float)Size;
            h[y * Size + x] = 0.65f * Sample(coarse, 8, u, v) + 0.35f * Sample(fine, 16, u, v);
        }
        return h;
    }

    static float[] Lattice(int cells, Random rng)
    {
        var g = new float[cells * cells];
        for (var i = 0; i < g.Length; i++) g[i] = (float)rng.NextDouble();
        return g;
    }

    static float Sample(float[] g, int cells, float u, float v)
    {
        var fx = u * cells;
        var fy = v * cells;
        var ix = (int)MathF.Floor(fx);
        var iy = (int)MathF.Floor(fy);
        var tx = Smooth(fx - ix);
        var ty = Smooth(fy - iy);
        float G(int x, int y) => g[((y % cells) + cells) % cells * cells + ((x % cells) + cells) % cells];
        var top = Mathf.Lerp(G(ix, iy), G(ix + 1, iy), tx);
        var bottom = Mathf.Lerp(G(ix, iy + 1), G(ix + 1, iy + 1), tx);
        return Mathf.Lerp(top, bottom, ty);
    }

    static float Smooth(float t) => t * t * (3f - 2f * t);
}
