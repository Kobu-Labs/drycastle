using Drycastle.ShooterCore.Minimap;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Minimap;

/// <summary>The top-down render's sidecar and where the picture lands on the tactical map's terrain texture.</summary>
public class TopDownImageTests
{
    const string Json = """{"map":"x","origin_x":-685.484,"origin_z":-2459.677,"width_px":2787,"height_px":4182,"metres_per_px":1}""";

    [Fact]
    public void ParsesTheSidecarTheViewerWrites()
    {
        var t = TopDownImage.Parse(Json);
        Assert.NotNull(t);
        Assert.Equal(-685.484f, t!.Value.OriginX);
        Assert.Equal(-2459.677f, t.Value.OriginZ);
        Assert.Equal((2787, 4182, 1f), (t.Value.WidthPx, t.Value.HeightPx, t.Value.MetresPerPx));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("[]")]
    [InlineData("""{"origin_x":0,"origin_z":0,"width_px":10,"height_px":10}""")]                       // no scale
    [InlineData("""{"origin_x":0,"origin_z":0,"width_px":0,"height_px":10,"metres_per_px":1}""")]      // empty
    [InlineData("""{"origin_x":0,"origin_z":0,"width_px":10,"height_px":10,"metres_per_px":0}""")]     // no scale
    [InlineData("""{"origin_x":"a","origin_z":0,"width_px":10,"height_px":10,"metres_per_px":1}""")]   // wrong type
    public void RejectsAnUnusableSidecar(string json) => Assert.Null(TopDownImage.Parse(json));

    [Fact]
    public void PlacementIsInTexelsFromTheExtentsNorthWestCorner()
    {
        // a 100 m picture at (10, 20) inside a 400 m extent at (0, 0), 200 texels: 0.5 texel per metre
        var p = new TopDownImage(10f, 20f, 50, 100, 2f).Placement(0f, 0f, 400f, 200);
        Assert.Equal((5f, 10f, 50f, 100f), p);
    }

    [Fact]
    public void PictureWestAndNorthOfTheExtentStartsOutsideIt()
    {
        var p = new TopDownImage(-100f, -50f, 100, 100, 1f).Placement(0f, 0f, 200f, 200);
        Assert.Equal((-100f, -50f, 100f, 100f), p);
    }
}
