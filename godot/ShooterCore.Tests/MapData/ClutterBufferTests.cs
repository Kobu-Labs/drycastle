using Drycastle.MapLoader.Vegetation;
using Xunit;

namespace Drycastle.ShooterCore.Tests.MapData;

public class ClutterBufferTests
{
    const float Eps = 1e-5f;

    static void Near(float expected, float actual) => Assert.InRange(actual, expected - Eps, expected + Eps);

    [Fact]
    public void WritesRowsOriginsTintAndBox()
    {
        // rotated a quarter turn, scale 2, a drier tint; and a neutral one. Map (x, y, z) -> Godot (x, z, -y), relative to the cell origin (10, 1, -20).
        var cell = new[]
        {
            new ClutterInstance(12f, 21f, 3f, MathF.PI / 2f, 2f, 200),
            new ClutterInstance(10f, 19f, 1f, 0f, 1f),
        };
        var origin = (10f, 1f, -20f);

        var colours = ClutterBuffer.Build(cell, origin, true, 0.5f, out var min, out var max);
        Assert.Equal(32, colours.Length);
        var (r, g, b) = ClutterTint.Rgb(200);
        float[] first = { 0f, 0f, 2f, 2f,   0f, 2f, 0f, 2f,   -2f, 0f, 0f, -1f,   r, g, b, 1f }; // origin: (2, 3 - 1, -21 + 20)
        float[] second = { 1f, 0f, 0f, 0f,   0f, 1f, 0f, 0f,   0f, 0f, 1f, 1f,   1f, 1f, 1f, 1f }; // origin: (0, 1 - 1, -19 + 20)
        for (var k = 0; k < 16; k++) { Near(first[k], colours[k]); Near(second[k], colours[16 + k]); }

        var plain = ClutterBuffer.Build(cell, origin, false, 0.5f, out _, out _);
        Assert.Equal(24, plain.Length);
        for (var k = 0; k < 12; k++) { Near(first[k], plain[k]); Near(second[k], plain[12 + k]); }

        // reach 0.5 * scale around each origin: 1 around (2, 2, -1) and 0.5 around (0, 0, 1)
        Near(-0.5f, min.X); Near(-0.5f, min.Y); Near(-2f, min.Z);
        Near(3f, max.X); Near(3f, max.Y); Near(1.5f, max.Z);
    }
}
