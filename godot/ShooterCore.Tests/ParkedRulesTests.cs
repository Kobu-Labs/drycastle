using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ParkedRulesTests
{
    [Fact]
    public void AStillCarOnTheGroundRests() => Assert.True(ParkedRules.RestsAfterStep(placeMoved: false, speed: 0f, yawRate: 0f, supported: true));

    [Fact]
    public void AnyMovementKeepsItAwake()
    {
        Assert.False(ParkedRules.RestsAfterStep(placeMoved: true, speed: 0f, yawRate: 0f, supported: true));
        Assert.False(ParkedRules.RestsAfterStep(placeMoved: false, speed: 0.01f, yawRate: 0f, supported: true));
        Assert.False(ParkedRules.RestsAfterStep(placeMoved: false, speed: 0f, yawRate: 0.001f, supported: true));
    }

    [Fact]
    public void ACarInTheAirFallsSoItIsNotAtRest() => Assert.False(ParkedRules.RestsAfterStep(placeMoved: false, speed: 0f, yawRate: 0f, supported: false));
}
