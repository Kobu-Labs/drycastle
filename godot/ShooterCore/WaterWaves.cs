namespace Drycastle.ShooterCore;

/// <summary>
/// The cosmetic wave height of the water surface. One function drives the water shader (water_surface.gdshader,
/// WaveSum) and the visual bob and tilt of boats on the water. It is visual only: boat physics, swimming and hit
/// detection stay on the flat surface, and nothing of it goes on the wire. Keep the table and the sum in step with the shader.
/// </summary>
public static class WaterWaves
{
    /// <summary>One travelling wave: unit direction (x, z), wavenumber (rad/m), angular speed (rad/s), weight (the weights sum to 1).</summary>
    public readonly record struct Wave(float Dx, float Dz, float K, float Omega, float Weight);

    /// <summary>
    /// The four waves (wavelengths about 1.5 to 7 m). Weights sum to 1, so the height is within +-amplitude.
    /// </summary>
    public static readonly Wave[] Waves =
    {
        new(0.9494f, 0.3162f, 0.9f, 1.1f, 0.4f),
        new(0.3827f, -0.9239f, 1.6f, 1.7f, 0.3f),
        new(-0.7071f, 0.7071f, 2.7f, 2.2f, 0.2f),
        new(0.1961f, 0.9806f, 4.3f, 3.4f, 0.1f),
    };

    /// <summary>
    /// Surface height offset (m) at ground point (x, z) and time <paramref name="time"/> (s), for a surface whose largest
    /// wave is <paramref name="amplitude"/> m. Zero amplitude (reduce motion) gives a flat surface.
    /// </summary>
    public static float Height(float x, float z, float time, float amplitude)
    {
        var sum = 0f;
        foreach (var w in Waves) sum += w.Weight * MathF.Sin(Phase(w, x, z, time));
        return amplitude * sum;
    }

    /// <summary>Slope of the surface, (dh/dx, dh/dz), in m per m, at the same point and time. The visual tilt of a boat comes from it.</summary>
    public static (float DhDx, float DhDz) Slope(float x, float z, float time, float amplitude)
    {
        var gx = 0f;
        var gz = 0f;
        foreach (var w in Waves)
        {
            var c = w.Weight * w.K * MathF.Cos(Phase(w, x, z, time));
            gx += c * w.Dx;
            gz += c * w.Dz;
        }
        return (amplitude * gx, amplitude * gz);
    }

    /// <summary>
    /// Wave amplitude (m) of a body from its size: <paramref name="capMetres"/> for a body at least 80 m across (its longer
    /// side), down to 30% of the cap for a small one. Rivers use the larger cap and ponds the smaller.
    /// </summary>
    public static float Amplitude(float extentMetres, float capMetres) =>
        capMetres * Math.Clamp(extentMetres / 80f, 0.3f, 1f);

    static float Phase(Wave w, float x, float z, float time) => w.K * (w.Dx * x + w.Dz * z) - w.Omega * time;
}
