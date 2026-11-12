using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #143: the minimap settings ranges and clamps (size, opacity, zoom).</summary>
public class MinimapSettingsTests
{
    [Fact]
    public void DefaultsAreInsideTheirRanges()
    {
        Assert.Equal(MinimapSettings.DefaultSize, MinimapSettings.ClampSize(MinimapSettings.DefaultSize));
        Assert.Equal(MinimapSettings.DefaultOpacity, MinimapSettings.ClampOpacity(MinimapSettings.DefaultOpacity));
        Assert.Equal(MinimapSettings.DefaultZoom, MinimapSettings.ClampZoom(MinimapSettings.DefaultZoom));
    }

    [Theory]
    [InlineData(0, 160)]
    [InlineData(999, 320)]
    [InlineData(250, 250)]
    public void SizeClampsToItsRange(float value, float expected) => Assert.Equal(expected, MinimapSettings.ClampSize(value));

    [Theory]
    [InlineData(0, 0.3f)]
    [InlineData(2, 1f)]
    [InlineData(0.5f, 0.5f)]
    public void OpacityClampsToItsRange(float value, float expected) => Assert.Equal(expected, MinimapSettings.ClampOpacity(value), 4);

    [Theory]
    [InlineData(1, 40)]
    [InlineData(500, 200)]
    [InlineData(120, 120)]
    public void ZoomClampsToItsRange(float value, float expected) => Assert.Equal(expected, MinimapSettings.ClampZoom(value));
}
