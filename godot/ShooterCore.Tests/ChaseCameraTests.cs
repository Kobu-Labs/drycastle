using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class ChaseCameraTests
{
    [Fact]
    public void ACarKeepsTheOldDistanceAndTheBusPullsBack()
    {
        var car = ChaseCamera.Wanted(new VehicleDef { HalfLength = 2.18f });
        Assert.Equal(6.5f, car.Distance);
        Assert.Equal(1.5f, car.Height);
        var bus = ChaseCamera.Wanted(new VehicleDef { HalfLength = 6.03f });
        Assert.True(bus.Distance > 10.5f && bus.Height > 2.5f);
    }

    [Fact]
    public void DataOverridesTheDerivedValues()
    {
        var v = ChaseCamera.Wanted(new VehicleDef { HalfLength = 6, ChaseDistance = 20, ChaseHeight = 4 });
        Assert.Equal((20f, 4f), v);
    }

    [Fact]
    public void ComesInAtOnceAndEasesBackOut()
    {
        Assert.Equal(3f, ChaseCamera.Step(6.5f, 6.5f, 3f, 0.016f));
        var out1 = ChaseCamera.Step(3f, 6.5f, 6.5f, 0.1f);
        Assert.Equal(3f + ChaseCamera.RecoverSpeed * 0.1f, out1, 3);
        Assert.Equal(6.5f, ChaseCamera.Step(6.4f, 6.5f, 99f, 1f));
        Assert.Equal(ChaseCamera.MinDistance, ChaseCamera.Step(5f, 6.5f, -2f, 0.016f));
    }
}
