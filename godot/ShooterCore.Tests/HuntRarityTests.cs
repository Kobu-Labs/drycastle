using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class HuntRarityTests
{
    static readonly float[] Range = { 100f, 200f };

    [Fact]
    public void TheHeaviestFivePercentAreGoldTenSilverTwentyBronze()
    {
        // the share at least as heavy as a roll r >= 0.5 is 2 (1 - r)^2: 5 % at 0.842, 10 % at 0.776, 20 % at 0.684
        Assert.Equal(HuntTier.Gold, HuntRarity.Of(Range, 200f));
        Assert.Equal(HuntTier.Gold, HuntRarity.Of(Range, 184.3f));
        Assert.Equal(HuntTier.Silver, HuntRarity.Of(Range, 184.1f));
        Assert.Equal(HuntTier.Silver, HuntRarity.Of(Range, 177.7f));
        Assert.Equal(HuntTier.Bronze, HuntRarity.Of(Range, 177.5f));
        Assert.Equal(HuntTier.Bronze, HuntRarity.Of(Range, 168.5f));
        Assert.Equal(HuntTier.Common, HuntRarity.Of(Range, 168.2f));
        Assert.Equal(HuntTier.Common, HuntRarity.Of(Range, 100f));
        Assert.Equal(HuntTier.Common, HuntRarity.Of(new[] { 5f, 5f }, 5f)); // no width: nothing is rare
    }

    [Fact]
    public void TheSharesMatchTheRollThatMakesTheWeights()
    {
        var rng = new Random(7);
        int n = 200_000, gold = 0, silver = 0, bronze = 0;
        for (var i = 0; i < n; i++)
        {
            var tier = HuntRarity.Of(Range, AnimalRules.WeightOf(Range, AnimalRules.RollSize(rng)));
            if (tier >= HuntTier.Gold) gold++;
            if (tier >= HuntTier.Silver) silver++;
            if (tier >= HuntTier.Bronze) bronze++;
        }
        Assert.InRange(gold / (double)n, 0.045, 0.055);
        Assert.InRange(silver / (double)n, 0.095, 0.105);
        Assert.InRange(bronze / (double)n, 0.195, 0.205);
    }

    static AnimalsFile Animals() => new()
    {
        Animals =
        {
            new AnimalDef { Id = "deer", WeightKg = new() { 60, 120 } },
            new AnimalDef { Id = "doe", WeightKg = new() { 50, 100 } },
            new AnimalDef { Id = "moose", WeightKg = new() { 270, 400 }, Variants = { new AnimalVariant { Id = "bull", WeightKg = new() { 380, 650 } } } },
            new AnimalDef { Id = "dog" },
        },
    };

    static HuntRecord Hunt(string species, string exact, string variant, float? kg, long player = 1) =>
        new(0, player, species, exact, variant, kg, 50, "sniper", null, 0, 0, 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void AHuntIsTieredByItsExactSpeciesAndLook()
    {
        var a = Animals();
        Assert.Equal(HuntTier.Gold, HuntRarity.Of(a, Hunt("deer", "doe", "", 99f)));      // the top of a doe's range
        Assert.Equal(HuntTier.Common, HuntRarity.Of(a, Hunt("deer", "deer", "", 99f)));   // the middle of a deer's
        Assert.Equal(HuntTier.Gold, HuntRarity.Of(a, Hunt("moose", "moose", "", 395f)));  // a cow
        Assert.Equal(HuntTier.Common, HuntRarity.Of(a, Hunt("moose", "moose", "bull", 395f)));
        Assert.Equal(HuntTier.Common, HuntRarity.Of(a, Hunt("unicorn", "unicorn", "", null)));
        Assert.Equal(HuntTier.Common, HuntRarity.Of(a, Hunt("dog", "dog", "", 20f)));
        Assert.Equal(HuntTier.Common, HuntRarity.Of(a, Hunt("gnu", "gnu", "", 20f)));
    }

    [Fact]
    public void ATileKeepsTheRarestWeightNotTheHeaviest()
    {
        var a = Animals();
        var book = new HuntBook(h => HuntRarity.Of(a, h));
        book.SetName(1, "Vesna");
        book.Add(Hunt("deer", "doe", "", 99f));   // gold for a doe
        book.Add(Hunt("deer", "deer", "", 105f)); // heavier, and common for a deer
        var viewer = book.CaughtBy(1);
        var tile = book.PlayerLog(1, viewer)!.Life.Single(e => e.Species == "deer");
        Assert.Equal(105f, tile.BestKg);
        Assert.Equal(HuntTier.Gold, tile.Tier);
        Assert.Equal(HuntTier.Gold, LifeTileModel.From(tile).Tier);
        Assert.All(book.PlayerLog(1, viewer)!.Life.Where(e => e.Species != "deer"), e => Assert.Equal(HuntTier.Common, e.Tier));
        Assert.Equal(HuntTier.Common, new HuntBook().PlayerLog(1, viewer)?.Life[0].Tier ?? HuntTier.Common); // no rarity given: common
    }

    [Fact]
    public void TheTierCrossesTheWireInThePlayerLog()
    {
        var log = new PlayerLogView(42, "Vesna", 2, 1, null, null, new[]
        {
            new LifeEntry("deer", false, true, 105f, 80f, 1_780_000_000, HuntTier.Gold),
            new LifeEntry("fox", false, true, 6f, null, 1_780_000_000, HuntTier.Bronze),
            new LifeEntry("wolf", false, false, null, null, 0),
        });
        var back = HuntEvents.DecodePlayerLog(HuntEvents.EncodePlayerLog(new PlayerLogAnswer(6, HuntStatus.Ok, 42, log)))!;
        Assert.Equal(log.Life, back.Log!.Life);
    }

    [Fact]
    public void AMysteryOrUnhuntedTileHasNoRim()
    {
        Assert.Equal(HuntTier.Common, LifeTileModel.From(new LifeEntry("unicorn", false, true, null, 9f, 5, HuntTier.Gold)).Tier);
        Assert.Equal(HuntTier.Common, LifeTileModel.From(new LifeEntry("mystery-1", true, true, null, null, 0, HuntTier.Gold)).Tier);
        Assert.Equal(HuntTier.Common, LifeTileModel.From(new LifeEntry("fox", false, false, null, null, 0, HuntTier.Gold)).Tier);
        Assert.Equal(HuntTier.Silver, LifeTileModel.From(new LifeEntry("fox", false, true, 8f, null, 5, HuntTier.Silver)).Tier);
    }
}
