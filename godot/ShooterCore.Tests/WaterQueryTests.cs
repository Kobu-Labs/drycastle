using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The box prefilter and array loops in <see cref="Water"/> must give the same answers as a plain scan of every body (T3-017).</summary>
public class WaterQueryTests
{
    const string Json = """
    {
      "version": 1,
      "bodies": [
        { "id": "A", "polygon": { "type": "Polygon", "coordinates": [[[0,0],[40,0],[40,40],[0,40],[0,0]]] }, "surface_y": 10.0, "bed_y": 8.0 },
        { "id": "B", "polygon": { "type": "Polygon", "coordinates": [[[100,0],[110,0],[110,10],[100,10],[100,0]]] }, "surface_y": 10.0, "bed_y": 9.2 },
        { "id": "C", "polygon": { "type": "Polygon", "coordinates": [[[-60,-60],[-50,-60],[-50,-50],[-60,-50],[-60,-60]]] }, "surface_y": 5.0, "bed_y": 1.0 }
      ]
    }
    """;

    [Fact]
    public void ShoreDistance_matches_a_plain_scan_of_every_body()
    {
        var water = Water.Parse(Json);
        for (var x = -120f; x <= 140f; x += 3.7f)
        for (var z = -80f; z <= 60f; z += 2.9f)
        {
            var expected = float.MaxValue;
            foreach (var b in water.Bodies) expected = MathF.Min(expected, b.ShoreDistance(x, z));
            Assert.Equal(expected, water.ShoreDistance(x, z), 4);
        }
    }

    [Fact]
    public void BoxDistance_is_zero_inside_and_the_gap_outside()
    {
        // body A in Godot ground axes (z = -north): x 0..40, z -40..0
        var body = Water.Parse(Json).Bodies[0];
        Assert.Equal(0f, body.BoxDistance(20, -20));
        Assert.Equal(0f, body.BoxDistance(0, 0));
        Assert.Equal(3f, body.BoxDistance(-3, -20), 4);
        Assert.Equal(5f, body.BoxDistance(43, -44), 4); // dx 3, dz 4
    }

    [Fact]
    public void Empty_water_has_no_shore_and_no_body()
    {
        Assert.Equal(float.MaxValue, Water.Empty.ShoreDistance(1, 1));
        Assert.Null(Water.Empty.BodyAt(1, 1));
        Assert.Empty(Water.Empty.Bodies);
    }
}
