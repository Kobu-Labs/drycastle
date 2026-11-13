using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class WadingTests
{
    const float Land = 5f; // the walk speed

    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    [InlineData(WaterConsts.AnkleDepth)]
    public void AnkleDeepOrLessWalksAtLandSpeed(float depth) =>
        Assert.Equal(Land, Wading.Speed(Land, depth), 5);

    [Fact]
    public void WadingEasesDownToTheFloorAtWadeDepth()
    {
        Assert.Equal(Land * WaterConsts.WadeSpeedFloor, Wading.Speed(Land, WaterConsts.WadeDepth), 5);
        // the middle of the ease is between the two, and it is continuous and falling
        var mid = Wading.Speed(Land, (WaterConsts.AnkleDepth + WaterConsts.WadeDepth) / 2);
        Assert.InRange(mid, Land * WaterConsts.WadeSpeedFloor, Land);
        var prev = float.MaxValue;
        for (var d = 0f; d <= WaterConsts.WadeDepth; d += 0.01f)
        {
            var v = Wading.Speed(Land, d);
            Assert.True(v <= prev + 1e-5f, $"speed rises at depth {d}");
            prev = v;
        }
    }

    [Fact]
    public void WadingBlendsIntoTheSwimSpeedByTheSwimDepth()
    {
        // between the wade depth and the swim depth the speed goes from the floor to the swim speed
        var mid = Wading.Speed(Land, (WaterConsts.WadeDepth + WaterConsts.SwimDepth) / 2);
        Assert.InRange(mid, WaterConsts.SwimSpeed, Land * WaterConsts.WadeSpeedFloor);
        Assert.Equal(WaterConsts.SwimSpeed, Wading.Speed(Land, WaterConsts.SwimDepth), 5);
    }

    [Fact]
    public void DeepWaterSwimsAtTheSwimSpeedWhateverTheLandSpeed()
    {
        Assert.Equal(WaterConsts.SwimSpeed, Wading.Speed(Land, WaterConsts.SwimDepth + 0.5f), 5);
        Assert.Equal(WaterConsts.SwimSpeed, Wading.Speed(MoveConsts.SprintSpeed, 3f), 5);
        // the swim speed is the same as the walk speed scaled by the floor, so the blend is seamless at the swim depth
        Assert.Equal(Land * WaterConsts.WadeSpeedFloor, WaterConsts.SwimSpeed, 5);
    }

    [Fact]
    public void LandingShareIsWholeOnDryGroundHalfInShallowAndNoneInDeepWater()
    {
        Assert.Equal(1f, Wading.LandingDamageShare(0f));
        Assert.Equal(1f, Wading.LandingDamageShare(WaterConsts.AnkleDepth));
        Assert.Equal(0.5f, Wading.LandingDamageShare(WaterConsts.WadeDepth), 5);
        Assert.Equal(0f, Wading.LandingDamageShare(WaterConsts.SwimDepth), 5);
        Assert.Equal(0f, Wading.LandingDamageShare(WaterConsts.SwimDepth + 2f), 5);
    }

    [Fact]
    public void LandingShareFallsMonotonicallyWithDepth()
    {
        var prev = float.MaxValue;
        for (var d = 0f; d <= 2f; d += 0.01f)
        {
            var v = Wading.LandingDamageShare(d);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v <= prev + 1e-5f, $"share rises at depth {d}");
            prev = v;
        }
    }
}
