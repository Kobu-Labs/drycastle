using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class CrosshairRulesTests
{
    [Fact]
    public void ShownOnlyWhenEnabledAndNothingCoversIt()
    {
        Assert.True(CrosshairRules.Visible(true, false, false, false, false));
        Assert.False(CrosshairRules.Visible(false, false, false, false, false));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void AnyCoverHidesIt(bool covered, bool scoped, bool bino, bool pounce) =>
        Assert.False(CrosshairRules.Visible(true, covered, scoped, bino, pounce));

    [Fact]
    public void EndingOnePounceDoesNotRevealItUnderTheScope() =>
        Assert.False(CrosshairRules.Visible(true, false, scoped: true, binoculars: false, pounce: false));
}
