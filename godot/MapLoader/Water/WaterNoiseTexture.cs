using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// The tiling detail textures of the water surface, generated once by the engine (no binary asset; Godot builds them on
/// a worker thread, the surface is merely smooth until they are ready). Both are seamless, so they repeat without a seam.
/// </summary>
public static class WaterNoiseTexture
{
    static NoiseTexture2D? _normal;
    static NoiseTexture2D? _foam;

    /// <summary>The ripples: a normal map of four octaves of simplex noise (512 texels). The shader samples it at two scales.</summary>
    public static NoiseTexture2D Normal() => _normal ??= new NoiseTexture2D
    {
        Width = 512, Height = 512, Seamless = true, GenerateMipmaps = true,
        AsNormalMap = true, BumpStrength = 5f,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Seed = 20240611, Frequency = 0.018f,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 4, FractalGain = 0.55f,
        },
    };

    /// <summary>The foam breakup: ridged simplex noise (256 texels), thin bright veins on a dark ground.</summary>
    public static NoiseTexture2D Foam() => _foam ??= new NoiseTexture2D
    {
        Width = 256, Height = 256, Seamless = true, GenerateMipmaps = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Seed = 7, Frequency = 0.03f,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged, FractalOctaves = 3,
        },
    };
}
