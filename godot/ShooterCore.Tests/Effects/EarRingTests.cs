using Xunit;
using Drycastle.ShooterCore.Effects;

namespace Drycastle.ShooterCore.Tests.Effects;

public class EarRingTests
{
    [Theory]
    [InlineData(0f, 8f, 1f)]
    [InlineData(6f, 8f, 1f)]      // 75% of 8 m
    [InlineData(7f, 8f, 0.5f)]    // halfway through the falloff
    [InlineData(8f, 8f, 0f)]
    [InlineData(20f, 8f, 0f)]
    [InlineData(22.5f, 150f, 1f)] // Scud: capped at 30 m, full to 22.5 m
    [InlineData(30f, 150f, 0f)]
    public void LevelByDistance(float dist, float radius, float expect) => Assert.Equal(expect, EarRingRules.Level(dist, radius), 3);

    [Fact]
    public void FadeIsLongerWhenLouder()
    {
        Assert.Equal(4f, EarRingRules.FadeSeconds(1f), 3);
        Assert.Equal(1f, EarRingRules.FadeSeconds(0f), 3);
        Assert.True(EarRingRules.FadeSeconds(0.8f) > EarRingRules.FadeSeconds(0.3f));
    }

    [Fact]
    public void RisesThenFadesToSilence()
    {
        var r = new EarRing();
        Assert.True(r.Hit(1f));
        r.Tick(0.1f);
        Assert.Equal(1f, r.Level, 3);
        r.Tick(2f);
        Assert.InRange(r.Level, 0.4f, 0.6f);
        r.Tick(2.1f);
        Assert.Equal(0f, r.Level);
        Assert.False(r.Active);
    }

    [Fact]
    public void QuieterOrEqualBlastDoesNotStackOrRestart()
    {
        var r = new EarRing();
        r.Hit(0.8f); r.Tick(0.1f); r.Tick(1f);
        var before = r.Level;
        Assert.False(r.Hit(0.8f));
        Assert.False(r.Hit(0.3f));
        r.Tick(0.01f);
        Assert.True(r.Level < before);
    }

    [Fact]
    public void AfterItFadedAWeakerBlastRingsAgain()
    {
        var r = new EarRing();
        r.Hit(1f); r.Tick(0.1f); r.Tick(5f);
        Assert.False(r.Active);
        Assert.True(r.Hit(0.3f));
    }

    [Fact]
    public void LouderBlastRaisesTheRing()
    {
        var r = new EarRing();
        r.Hit(0.4f); r.Tick(0.1f); r.Tick(0.5f);
        Assert.True(r.Hit(0.9f));
        r.Tick(0.1f);
        Assert.Equal(0.9f, r.Level, 3);
    }

    [Fact]
    public void TooFaintIsIgnoredAndInactive()
    {
        var r = new EarRing();
        Assert.False(r.Hit(0.01f));
        Assert.False(r.Active);
    }
}
