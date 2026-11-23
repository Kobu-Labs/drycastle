using Drycastle.ShooterCore.Waters;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Waters;

public class WaterWavesTests
{
    /// <summary>The formula written out with the four waves' numbers: the C# table, the shader (water_surface.gdshader) and this test must agree.</summary>
    static float Formula(float x, float z, float t, float a) =>
        a * (0.4f * MathF.Sin(0.9f * (0.9494f * x + 0.3162f * z) - 1.1f * t)
           + 0.3f * MathF.Sin(1.6f * (0.3827f * x - 0.9239f * z) - 1.7f * t)
           + 0.2f * MathF.Sin(2.7f * (-0.7071f * x + 0.7071f * z) - 2.2f * t)
           + 0.1f * MathF.Sin(4.3f * (0.1961f * x + 0.9806f * z) - 3.4f * t));

    [Theory]
    [InlineData(0f, 0f, 0f, 0.1f)]
    [InlineData(12.5f, -7.25f, 3.0f, 0.1f)]
    [InlineData(-40f, 33f, 17.5f, 0.05f)]
    [InlineData(100.3f, 220.1f, 0.4f, 0.02f)]
    public void HeightMatchesTheFormula(float x, float z, float t, float a)
    {
        Assert.Equal(Formula(x, z, t, a), WaterWaves.Height(x, z, t, a), 5);
    }

    [Fact]
    public void HeightIsBoundedByTheAmplitude()
    {
        for (var i = 0; i < 2000; i++)
        {
            var x = i * 0.37f; var z = i * -0.91f; var t = i * 0.05f;
            Assert.True(MathF.Abs(WaterWaves.Height(x, z, t, 0.1f)) <= 0.1f + 1e-5f);
        }
    }

    [Fact]
    public void ZeroAmplitudeIsAFlatSurface()
    {
        Assert.Equal(0f, WaterWaves.Height(3f, 4f, 5f, 0f));
        Assert.Equal((0f, 0f), WaterWaves.Slope(3f, 4f, 5f, 0f));
    }

    [Fact]
    public void WeightsSumToOne()
    {
        Assert.Equal(1f, WaterWaves.Waves.Sum(w => w.Weight), 5);
    }

    [Theory]
    [InlineData(4.2f, -9.9f, 1.3f)]
    [InlineData(-120f, 60f, 22f)]
    public void SlopeIsTheHeightGradient(float x, float z, float t)
    {
        const float a = 0.1f, h = 1e-3f;
        var (gx, gz) = WaterWaves.Slope(x, z, t, a);
        var nx = (WaterWaves.Height(x + h, z, t, a) - WaterWaves.Height(x - h, z, t, a)) / (2 * h);
        var nz = (WaterWaves.Height(x, z + h, t, a) - WaterWaves.Height(x, z - h, t, a)) / (2 * h);
        Assert.Equal(nx, gx, 3);
        Assert.Equal(nz, gz, 3);
    }

    [Fact]
    public void AmplitudeScalesWithSizeUpToTheCap()
    {
        Assert.Equal(0.1f, WaterWaves.Amplitude(500f, 0.1f), 5);       // a long river: the cap
        Assert.Equal(0.05f, WaterWaves.Amplitude(80f, 0.05f), 5);      // at 80 m: the cap
        Assert.Equal(0.015f, WaterWaves.Amplitude(5f, 0.05f), 5);      // a tiny pond: 30% of the cap
        Assert.True(WaterWaves.Amplitude(40f, 0.05f) < WaterWaves.Amplitude(60f, 0.05f));
    }
}
