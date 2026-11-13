using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ParkedRulesTests
{
    [Fact]
    public void AStillCarOnTheGroundRests() => Assert.True(ParkedRules.IsAtRest(0f, 0f, 0f, supported: true));

    [Fact]
    public void FloatJitterIsStillRest() => Assert.True(ParkedRules.IsAtRest(1e-6f, 0f, 0f, supported: true));

    [Fact]
    public void AnyRealMovementKeepsItAwake()
    {
        Assert.False(ParkedRules.IsAtRest(0.0006f, 0f, 0f, supported: true)); // a 1 degree slope creeps about 0.6 mm a tick
        Assert.False(ParkedRules.IsAtRest(0f, 0.01f, 0f, supported: true));
        Assert.False(ParkedRules.IsAtRest(0f, 0f, 0.001f, supported: true));
    }

    [Fact]
    public void ACarInTheAirFallsSoItIsNotAtRest() => Assert.False(ParkedRules.IsAtRest(0f, 0f, 0f, supported: false));

    [Fact]
    public void HysteresisNeedsSeveralQuietTicks()
    {
        var n = 0;
        for (var i = 0; i < ParkedRules.RestTicksToSleep - 1; i++) { n = ParkedRules.NextRestCount(n, true); Assert.False(ParkedRules.Sleeps(n)); }
        n = ParkedRules.NextRestCount(n, true);
        Assert.True(ParkedRules.Sleeps(n));
        Assert.Equal(0, ParkedRules.NextRestCount(n, false)); // one moving tick starts the count again
    }
}
