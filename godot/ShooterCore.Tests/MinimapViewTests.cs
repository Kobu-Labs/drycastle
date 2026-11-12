using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #150: the minimap follows the player's view (vehicle, turret, missile, on foot), eased on the shorter arc.</summary>
public class MinimapViewTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(MathF.PI, MathF.PI)]
    [InlineData(-MathF.PI, MathF.PI)]          // -pi and pi are the same heading; (-pi, pi] keeps pi
    [InlineData(3f * MathF.PI, MathF.PI)]
    [InlineData(-0.5f, -0.5f)]
    [InlineData(7f, 7f - 2f * MathF.PI)]
    public void WrapStaysInPiRange(float input, float expected)
    {
        Assert.Equal(expected, MinimapView.Wrap(input), 4);
    }

    [Fact]
    public void ShortestDeltaCrossesTheSeamTheShortWay()
    {
        // from just under +pi to just over -pi is a tiny step forward, not a near-full turn back
        Assert.Equal(0.2f, MinimapView.ShortestDelta(MathF.PI - 0.1f, -MathF.PI + 0.1f), 4);
        Assert.Equal(-0.2f, MinimapView.ShortestDelta(-MathF.PI + 0.1f, MathF.PI - 0.1f), 4);
        Assert.Equal(-0.3f, MinimapView.ShortestDelta(0.2f, -0.1f), 4);
    }

    [Fact]
    public void FollowMovesTheShortWayAcrossTheSeam()
    {
        var yaw = MathF.PI - 0.05f;
        for (var i = 0; i < 120; i++)
        {
            var next = MinimapView.Follow(yaw, -MathF.PI + 0.05f, 1f / 60f, 14f);
            // every step stays within a small arc of the seam: no step ever approaches a full turn
            Assert.True(MathF.Abs(MinimapView.ShortestDelta(yaw, next)) < 0.2f);
            yaw = next;
        }
        Assert.True(MathF.Abs(MinimapView.ShortestDelta(yaw, -MathF.PI + 0.05f)) < 0.01f);
    }

    [Fact]
    public void FollowConvergesAndIgnoresZeroTime()
    {
        Assert.Equal(0.4f, MinimapView.Follow(0.4f, 1.2f, 0f, 14f), 6);
        var y = 0f;
        for (var i = 0; i < 600; i++) y = MinimapView.Follow(y, 1.2f, 1f / 60f, 14f);
        Assert.Equal(1.2f, y, 3);
    }

    [Fact]
    public void FollowNeverOvershootsTheTarget()
    {
        var y = 0f;
        for (var i = 0; i < 600; i++)
        {
            y = MinimapView.Follow(y, 1.2f, 1f / 30f, 14f);
            Assert.True(y <= 1.2f + 1e-5f);
        }
    }

    [Fact]
    public void SourcePrecedenceMatchesTheCameraThePlayerSees()
    {
        Assert.Equal(MinimapViewSource.OnFoot, MinimapView.Select(false, false, false));
        Assert.Equal(MinimapViewSource.Vehicle, MinimapView.Select(false, false, true));
        Assert.Equal(MinimapViewSource.Turret, MinimapView.Select(false, true, false));
        Assert.Equal(MinimapViewSource.Missile, MinimapView.Select(true, false, true));
        Assert.Equal(MinimapViewSource.Missile, MinimapView.Select(true, true, true));
    }

    [Fact]
    public void YawFollowsTheSelectedSourceOnly()
    {
        const float body = 0.1f, car = 1.5f, gun = -2.0f, missile = 2.9f;
        Assert.Equal(body, MinimapView.YawFor(MinimapViewSource.OnFoot, body, car, gun, missile));
        Assert.Equal(car, MinimapView.YawFor(MinimapViewSource.Vehicle, body, car, gun, missile));
        Assert.Equal(gun, MinimapView.YawFor(MinimapViewSource.Turret, body, car, gun, missile));
        Assert.Equal(missile, MinimapView.YawFor(MinimapViewSource.Missile, body, car, gun, missile));
    }

    [Fact]
    public void VehicleHeadingIsWhatKeepsForwardUp()
    {
        // a car turned to yaw 1.5 with the body still at 0: the view yaw (the car's) puts the car's forward at the top
        var carYaw = 1.5f;
        var forward = new Vector2(-MathF.Sin(carYaw), -MathF.Cos(carYaw));
        var up = MinimapMath.Rotate(forward, MinimapMath.RotationFor(MinimapView.YawFor(MinimapViewSource.Vehicle, 0f, carYaw, 0f, 0f)));
        Assert.Equal(0f, up.X, 4);
        Assert.Equal(-1f, up.Y, 4);
    }

    [Fact]
    public void TextureSideFollowsTheOnScreenScaleWithinBounds()
    {
        Assert.Equal(512, MinimapView.TextureSide(1000f, 0.2f, 512, 2048));   // small view: the floor
        Assert.Equal(1250, MinimapView.TextureSide(1000f, 1.25f, 512, 2048)); // 1 texel per device px
        Assert.Equal(2048, MinimapView.TextureSide(1000f, 9f, 512, 2048));    // capped
    }

    [Fact]
    public void RebuildOnlyForLargeScaleChanges()
    {
        Assert.True(MinimapView.NeedsRebuild(0, 512));
        Assert.False(MinimapView.NeedsRebuild(1024, 1100));  // within a quarter: keep the texture
        Assert.True(MinimapView.NeedsRebuild(1024, 1400));   // beyond a quarter: rebuild
        Assert.True(MinimapView.NeedsRebuild(1024, 700));
    }
}
