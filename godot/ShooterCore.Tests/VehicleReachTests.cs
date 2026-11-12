using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Enter distance and radius data of vehicles (#217).</summary>
public class VehicleReachTests
{
    [Fact]
    public void DistanceIsToTheEdgeNotTheCentre()
    {
        // facing -Z (yaw 0), half width 1.5, half length 6: a point 1 m beside the side, level with the tail
        Assert.Equal(1f, VehicleReach.DistanceToFootprint(Vector3.Zero, 0, 1.5f, 6f, new Vector3(2.5f, 0, 5f)), 1e-4f);
        Assert.Equal(0f, VehicleReach.DistanceToFootprint(Vector3.Zero, 0, 1.5f, 6f, new Vector3(0.5f, 0, -5f)), 1e-4f);
    }

    [Fact]
    public void CornerIsMeasuredDiagonally()
    {
        Assert.Equal(MathF.Sqrt(2), VehicleReach.DistanceToFootprint(Vector3.Zero, 0, 1f, 2f, new Vector3(2f, 0, 3f)), 1e-4f);
    }

    [Fact]
    public void YawRotatesTheFootprint()
    {
        // a quarter turn: the long axis now runs along X
        var yaw = MathF.PI / 2;
        Assert.True(VehicleReach.CanEnter(Vector3.Zero, yaw, 1.5f, 6f, new Vector3(5.5f, 0, 2.2f), 1f));
        Assert.False(VehicleReach.CanEnter(Vector3.Zero, 0, 1.5f, 6f, new Vector3(5.5f, 0, 2.2f), 1f));
    }

    [Fact]
    public void BusSeatsAreReachableFromTheRear()
    {
        Assert.True(VehicleReach.CanEnter(Vector3.Zero, 0, 1.3f, 6f, new Vector3(0, 0, 6.9f), 1f));
        Assert.False(VehicleReach.CanEnter(Vector3.Zero, 0, 1.3f, 6f, new Vector3(0, 0, 7.1f), 1f));
    }

    [Fact]
    public void NotOneHoveringOverhead()
    {
        Assert.False(VehicleReach.CanEnter(new Vector3(0, 6, 0), 0, 1.3f, 2f, new Vector3(0, 0, 0), 1f));
    }

    [Fact]
    public void ShippedDataDefaultsToOneMetre()
    {
        var f = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
        Assert.Equal(1f, f.EnterRadius);
        foreach (var v in f.Vehicles)
        {
            Assert.Equal(1f, f.EnterRadiusOf(v));
        }
    }

    [Fact]
    public void TypeRadiusOverridesTheFiles()
    {
        Assert.Equal(2f, new VehiclesFile().EnterRadiusOf(new VehicleDef { EnterRadius = 2f }));
    }
}
