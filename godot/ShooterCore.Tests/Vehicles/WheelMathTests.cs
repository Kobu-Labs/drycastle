using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

public class WheelMathTests
{
    [Fact]
    public void RollIsBackwardsAheadAndByRadius()
    {
        Assert.Equal(-1f, WheelMath.RollDelta(2f, 0.5f, 0.25f), 4);
        Assert.True(WheelMath.RollDelta(-3f, 0.4f, 0.1f) > 0);
        Assert.Equal(WheelMath.RollDelta(1f, WheelMath.FallbackRadius, 1f), WheelMath.RollDelta(1f, 0f, 1f));
    }

    [Fact]
    public void SteerIsAtanOfArmTimesCurvatureAndCapped()
    {
        Assert.Equal(MathF.Atan(3f * 0.1f), WheelMath.SteerAngle(3f, 0.1f), 5);
        Assert.Equal(WheelMath.DefaultLock, WheelMath.SteerAngle(3f, 5f), 5);
        Assert.Equal(-0.3f, WheelMath.SteerAngle(3f, -5f, 0.3f), 5);
    }

    [Fact]
    public void MotionReadsSpeedAlongHeadingAndYawRate()
    {
        // facing -Z (yaw 0): moving -Z is forward
        var (v, w) = WheelMath.Motion(0, -1f, 0, 0.1f, 0.1f);
        Assert.Equal(10f, v, 3); Assert.Equal(1f, w, 3);
        // yaw 90 degrees faces -X: moving -X is forward, +X astern
        Assert.Equal(5f, WheelMath.Motion(-0.5f, 0, MathF.PI / 2, 0, 0.1f).Speed, 3);
        Assert.Equal(-5f, WheelMath.Motion(0.5f, 0, MathF.PI / 2, 0, 0.1f).Speed, 3);
        // the yaw step wraps, and a jump is standing still
        Assert.Equal(-1f, WheelMath.Motion(0, 0, 0, MathF.Tau - 0.1f, 0.1f).YawRate, 3);
        Assert.Equal((0f, 0f), WheelMath.Motion(0, -50f, 0, 0.1f, 0.1f));
    }

    [Fact]
    public void CircleOfKnownRadiusGivesItsWheelAngle()
    {
        // wheelbase 3 m on a 10 m circle at 8 m/s, left: yaw rate 0.8, steer atan(3/10)
        var c = WheelMath.Curvature(0.8f, 8f);
        Assert.Equal(MathF.Atan(0.3f), WheelMath.SteerAngle(3f, c), 2);
        // astern with the same wheel angle: the yaw rate and the speed both flip, the angle does not
        Assert.Equal(c, WheelMath.Curvature(-0.8f, -8f), 5);
        Assert.Equal(0f, WheelMath.Curvature(1f, 0f), 5);
    }

    [Fact]
    public void PivotIsTheMeanOfTheFixedAxles()
    {
        Assert.Equal(1.5f, WheelMath.PivotZ(new[] { -1.5f, -1.5f, 1.5f, 1.5f }, new[] { true, true, false, false }), 5);
        Assert.Equal(2f, WheelMath.PivotZ(new[] { -2f, 1f, 3f }, new[] { true, false, false }), 5);
        Assert.Equal(3f, WheelMath.PivotZ(new[] { -2f, 3f }, new[] { true, true }), 5);
    }

    [Fact]
    public void LayoutsHaveNoDuplicateRowsAndCoverTheNodeTable()
    {
        var all = WheelMath.AllNames();
        foreach (var n in new[] { "wheel_f", "wheel_r", "wheel_fl", "wheel_rr", "wheel_ml", "wheel_l1", "wheel_r4", "wheel_l3" }) Assert.Contains(n, all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
