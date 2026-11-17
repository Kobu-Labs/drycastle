using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>#357: a killed body that was not thrown topples flat, never past flat, and comes to rest.</summary>
public class CorpseCollapseTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.6f)]
    [InlineData(1f)]
    public void FallsFlatAndRests(float strength)
    {
        var c = new CorpseCollapse(strength);
        var seenSettling = false;
        for (var i = 0; i < 600 && !c.AtRest; i++)
        {
            c.Step(1f / 60f);
            seenSettling |= c.Phase == CollapsePhase.Settling;
            Assert.InRange(c.Spin, 0f, CorpseCollapse.Lying + 1e-4f); // never through the ground, never backwards
        }
        Assert.True(seenSettling);
        Assert.True(c.AtRest);
        Assert.Equal(CorpseCollapse.Lying, c.Spin);
        Assert.Equal(CorpseCollapse.RestAmount, c.Amount);
    }

    [Fact]
    public void HarderHitFallsSooner()
    {
        float Time(float s) { var c = new CorpseCollapse(s); var t = 0f; while (c.Phase == CollapsePhase.Falling && t < 5f) { c.Step(0.01f); t += 0.01f; } return t; }
        Assert.True(Time(1f) < Time(0f));
    }
}
