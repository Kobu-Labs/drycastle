using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>#169: client effects are culled by distance from the camera.</summary>
public class EffectCullTests
{
    [Fact]
    public void DistanceToSegmentUsesTheClosestPointOnTheSpan()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(10, 0, 0);
        Assert.Equal(3f, EffectCull.DistanceToSegment(new Vector3(5, 3, 0), a, b), 4);
        Assert.Equal(5f, EffectCull.DistanceToSegment(new Vector3(14, 0, 3), a, b), 4); // beyond b: distance to the end point
        Assert.Equal(2f, EffectCull.DistanceToSegment(new Vector3(-2, 0, 0), a, b), 4); // before a
    }

    [Fact]
    public void ZeroLengthSpanIsAPoint()
    {
        Assert.Equal(5f, EffectCull.DistanceToSegment(new Vector3(3, 4, 0), Vector3.Zero, Vector3.Zero), 4);
    }

    [Fact]
    public void NearIsInclusiveOfTheRange()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(1000, 0, 0);
        Assert.True(EffectCull.Near(new Vector3(500, 0, 0), a, b, 10f));
        Assert.False(EffectCull.Near(new Vector3(500, 50, 0), a, b, 10f));
        Assert.True(EffectCull.Near(new Vector3(500, 10, 0), a, b, 10f));
    }

    [Fact]
    public void LongTracerIsKeptWhenItPassesTheCamera()
    {
        // a span that passes the camera is kept; one that stays far away is culled
        Assert.True(EffectCull.Near(new Vector3(0, 0, 0), new Vector3(-100, 5, 0), new Vector3(100, 5, 0)));
        Assert.False(EffectCull.Near(new Vector3(0, 0, 0), new Vector3(1000, 0, 0), new Vector3(1300, 0, 0)));
    }
}
