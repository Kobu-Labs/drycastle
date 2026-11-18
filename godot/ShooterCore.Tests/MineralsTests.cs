using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class MineralsTests
{
    [Fact]
    public void TheLogHoldsEightRealAndTwentyFantasyMineralsEachOnce()
    {
        Assert.Equal(28, Minerals.All.Count);
        Assert.Equal(8, Minerals.All.Count(m => !m.Fantasy));
        Assert.Equal(28, Minerals.All.Select(m => m.Id).Distinct().Count());
        Assert.NotNull(Minerals.Find("pheonixite"));
        Assert.Null(Minerals.Find("gold"));
    }

    [Fact]
    public void TheOrderIsAFixedShuffleOfThemAll()
    {
        Assert.Equal(Minerals.All.OrderBy(m => m.Id), Minerals.LogOrder.OrderBy(m => m.Id));
        Assert.NotEqual(Minerals.All, Minerals.LogOrder);
        // the two kinds are mixed: neither the first eight nor the last eight are all real-life stones
        Assert.Contains(Minerals.LogOrder.Take(8), m => m.Fantasy);
        Assert.Contains(Minerals.LogOrder.Skip(8), m => !m.Fantasy);
    }

    [Fact]
    public void ACardIsFoundWithItsPlaceAmongTheFinders()
    {
        var none = MineralTileModel.For(null);
        Assert.Equal(28, none.Count);
        Assert.All(none, t => Assert.False(t.Found));
        var some = MineralTileModel.For(new Dictionary<string, int> { ["opal"] = 7, ["micin"] = 1, ["agate"] = 0 });
        Assert.Equal(Minerals.LogOrder, some.Select(t => t.Mineral));
        Assert.Equal((true, 7), (some.Single(t => t.Mineral.Id == "opal").Found, some.Single(t => t.Mineral.Id == "opal").Rank));
        Assert.Equal(2, some.Count(t => t.Found));
    }

    [Theory]
    [InlineData(1, "1st"), InlineData(2, "2nd"), InlineData(3, "3rd"), InlineData(4, "4th"), InlineData(7, "7th"), InlineData(11, "11th"), InlineData(12, "12th"),
     InlineData(13, "13th"), InlineData(21, "21st"), InlineData(22, "22nd"), InlineData(103, "103rd"), InlineData(111, "111th")]
    public void OrdinalsReadAsEnglish(int n, string text) => Assert.Equal(text, MineralFormat.Ordinal(n));
}
