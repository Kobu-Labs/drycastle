using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class UnderwaterFxTests
{
    // One 40 m square lake, surface 10 m, bed 8 m, in local metres (x east, y north): Godot ground (x, z) is (x, -y).
    static Water Lake() => Water.Parse("""
        {"version": 1, "bodies": [{"id": "P-1", "polygon": {"type": "Polygon", "coordinates": [[[0,0],[40,0],[40,40],[0,40],[0,0]]]}, "surface_y": 10.0, "bed_y": 8.0}]}
        """);

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-2f, 0f)]
    [InlineData(0.6f, 0.5f)]
    [InlineData(5f, 1f)]
    public void ShoreFadeIsClampedAndFullOutFromTheShore(float distance, float expected) =>
        Assert.Equal(expected, UnderwaterFx.ShoreFade(distance), 4);

    [Theory]
    [InlineData(false, true, UnderwaterFx.Crossing.Enter)]
    [InlineData(true, false, UnderwaterFx.Crossing.Leave)]
    [InlineData(true, true, UnderwaterFx.Crossing.None)]
    [InlineData(false, false, UnderwaterFx.Crossing.None)]
    public void SplashesOnlyOnTheChangeIntoOrOutOfTheWater(bool was, bool now, UnderwaterFx.Crossing expected) =>
        Assert.Equal(expected, UnderwaterFx.WaterCrossing(was, now));

    [Fact]
    public void BulletFromAboveHitsTheSurfaceInsideTheLake()
    {
        // Godot (x, z): the lake spans x 0..40 and z -40..0. Shot from 2 m above the surface down to 2 m below it.
        var from = new Vector3(20f, 12f, -20f);
        var to = new Vector3(20f, 8f, -20f);
        Assert.True(UnderwaterFx.FirstSurfaceCrossing(Lake(), from, to, out var hit));
        Assert.Equal(10f, hit.Y, 3);
        Assert.Equal(20f, hit.X, 3);
        Assert.Equal(-20f, hit.Z, 3);
    }

    [Fact]
    public void NoSplashWhenTheShotMissesTheLakeOrStaysOnOneSide()
    {
        var lake = Lake();
        Assert.False(UnderwaterFx.FirstSurfaceCrossing(lake, new Vector3(60f, 12f, -20f), new Vector3(60f, 8f, -20f), out _), "beside the lake");
        Assert.False(UnderwaterFx.FirstSurfaceCrossing(lake, new Vector3(20f, 12f, -20f), new Vector3(20f, 11f, -20f), out _), "stays in the air");
        Assert.False(UnderwaterFx.FirstSurfaceCrossing(lake, new Vector3(20f, 9f, -20f), new Vector3(20f, 8f, -20f), out _), "stays under water");
        Assert.False(UnderwaterFx.FirstSurfaceCrossing(Water.Empty, new Vector3(20f, 12f, -20f), new Vector3(20f, 8f, -20f), out _), "dry map");
    }
}
