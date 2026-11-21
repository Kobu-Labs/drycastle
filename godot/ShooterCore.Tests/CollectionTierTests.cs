using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>#473: the Hunter's Log collection tier (every Index species at a rarity or better) and the weapon it rewards.</summary>
public class CollectionTierTests
{
    static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    // the weight stands for the rarity here: 100+ bronze, 200+ silver, 300+ gold
    static HuntTier ByKg(HuntRecord h) => h.Kg is >= 300 ? HuntTier.Gold : h.Kg is >= 200 ? HuntTier.Silver : h.Kg is >= 100 ? HuntTier.Bronze : HuntTier.Common;

    static HuntRecord Hunt(long player, string species, float kg) =>
        HuntRecord.ForAnimal(player, species, "", kg, 50f, "sniper", null, 0, 0, 0, T0)!;

    static HuntBook BookWith(long player, params (string Species, float Kg)[] hunts)
    {
        var book = new HuntBook(ByKg);
        foreach (var (s, kg) in hunts) book.Add(Hunt(player, s, kg));
        return book;
    }

    static (string, float)[] AllIndex(float kg, string? except = null, float exceptKg = 0) =>
        IndexSpecies.All.Select(s => (s, s == except ? exceptKg : kg)).ToArray();

    [Fact]
    public void NoHuntsAndUnknownPlayerAreCommon()
    {
        var book = new HuntBook(ByKg);
        Assert.Equal(HuntTier.Common, book.CollectionTier(1));
        Assert.Equal(HuntTier.Common, BookWith(1, ("deer", 300)).CollectionTier(1)); // one species of ten
    }

    [Fact]
    public void AMissingSpeciesHoldsTheTierAtCommon()
    {
        var hunts = AllIndex(250).Where(h => h.Item1 != "wolf").ToArray();
        Assert.Equal(HuntTier.Common, BookWith(1, hunts).CollectionTier(1));
    }

    [Fact]
    public void TheTierIsTheWeakestSpeciesRarestHunt()
    {
        Assert.Equal(HuntTier.Silver, BookWith(1, AllIndex(250)).CollectionTier(1));
        Assert.Equal(HuntTier.Bronze, BookWith(1, AllIndex(300, "fox", 120)).CollectionTier(1));
        Assert.Equal(HuntTier.Common, BookWith(1, AllIndex(300, "fox", 50)).CollectionTier(1));
        Assert.Equal(HuntTier.Gold, BookWith(1, AllIndex(300)).CollectionTier(1));
    }

    [Fact]
    public void ABetterHuntOfASpeciesRaisesItAndAWorseOneNeverLowersIt()
    {
        var book = BookWith(1, AllIndex(120));
        Assert.Equal(HuntTier.Bronze, book.CollectionTier(1));
        book.Add(Hunt(1, "deer", 220));
        Assert.Equal(HuntTier.Bronze, book.CollectionTier(1)); // the other nine are still bronze
        foreach (var s in IndexSpecies.All.Where(s => s != "deer")) book.Add(Hunt(1, s, 220));
        Assert.Equal(HuntTier.Silver, book.CollectionTier(1));
        book.Add(Hunt(1, "deer", 10));
        Assert.Equal(HuntTier.Silver, book.CollectionTier(1));
    }

    [Fact]
    public void PlayersAreCountedApartAndMysteriesDoNotCount()
    {
        var book = BookWith(1, AllIndex(250));
        Assert.Equal(HuntTier.Common, book.CollectionTier(2));
        Assert.Equal(HuntTier.Silver, book.CollectionTier(1)); // no mystery caught
    }

    static readonly WeaponsFile Weapons = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
    static readonly PerksFile Perks = PerksFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/perks.json"));

    [Fact]
    public void TheHuntingRifleIsTheSilverRewardWithoutALevel()
    {
        var w = Weapons.Find("hunting_rifle")!;
        Assert.Equal(HuntTier.Silver, w.RewardTier);
        Assert.True(w.IsReward);
        Assert.Equal(WeaponDef.RewardUnlockLevel, w.UnlockLevel);
        var sniper = Weapons.Find("sniper")!;
        Assert.True(w.ZoomFov < sniper.ZoomFov); // much more zoom than the level 5 sniper
        Assert.All(Weapons.Weapons.Where(x => x.Id != "hunting_rifle"), x => Assert.False(x.IsReward));
    }

    [Fact]
    public void OnlyAnOwnedUnlockOpensARewardWeapon()
    {
        PickerOption Pick(IEnumerable<Unlock> owned, int level) => LoadoutEditor.Options(LoadoutPicker.Primary, Weapons, Perks, new Dictionary<string, int>(), level, owned)
            .Single(o => o.Id == "hunting_rifle");
        Assert.False(Pick(Array.Empty<Unlock>(), 999).Unlocked);
        Assert.Equal(HuntTier.Silver, Pick(Array.Empty<Unlock>(), 1).Reward);
        Assert.True(Pick(new[] { new Unlock("weapon", "hunting_rifle") }, 1).Unlocked);
    }
}

/// <summary>#473: the Rewards tab of the Hunter's Log (progress to each tier, what each gives, the navigation to it).</summary>
public class HuntRewardsTests
{
    static LifeEntry Tile(string species, HuntTier tier, bool caught = true, bool hidden = false) =>
        new(species, hidden, caught, caught ? 50f : null, null, 0, caught ? tier : HuntTier.Common);

    static List<LifeEntry> Life(Func<string, LifeEntry> f) => IndexSpecies.All.Select(f).Append(Tile("unicorn", HuntTier.Gold)).ToList();

    [Fact]
    public void ProgressCountsTheIndexSpeciesAtTheTierOrBetter()
    {
        var life = Life(s => s switch { "deer" => Tile(s, HuntTier.Gold), "stag" => Tile(s, HuntTier.Silver), "moose" => Tile(s, HuntTier.Bronze), "bear" => Tile(s, HuntTier.Common), "boar" => Tile(s, HuntTier.Gold, caught: false), _ => Tile(s, HuntTier.Silver) });
        var p = HuntRewards.Progress(life);
        Assert.Equal(new[] { HuntTier.Bronze, HuntTier.Silver, HuntTier.Gold }, p.Select(t => t.Tier));
        Assert.All(p, t => Assert.Equal(IndexSpecies.All.Count, t.Of));
        Assert.Equal(new[] { 8, 7, 1 }, p.Select(t => t.Have)); // 10 minus the common bear and the unhunted boar; the silvers and the gold; the gold; mystery never counts
    }

    [Fact]
    public void AllGoldReachesEveryTierAndAnEmptyLogNone()
    {
        Assert.All(HuntRewards.Progress(Life(s => Tile(s, HuntTier.Gold))), t => Assert.True(t.Reached));
        Assert.All(HuntRewards.Progress(Life(s => Tile(s, HuntTier.Gold, caught: false))), t => Assert.False(t.Reached));
    }

    [Fact]
    public void AMysteryOrAHiddenTileNeverCounts()
    {
        var life = Life(s => Tile(s, HuntTier.Gold)).Append(Tile("mystery_x", HuntTier.Gold, hidden: true)).ToList();
        Assert.All(HuntRewards.Progress(life), t => Assert.Equal(IndexSpecies.All.Count, t.Have));
    }

    [Fact]
    public void TheRewardsListsTheShippedWeaponAndItemLowestTierFirst()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data");
        var r = HuntRewards.For(WeaponsFile.Load(Path.Combine(dir, "weapons.json")), ItemsFile.Load(Path.Combine(dir, "items.json")));
        Assert.Equal(new[] { ("item", "hunting_binoculars", HuntTier.Bronze), ("weapon", "hunting_rifle", HuntTier.Silver) }, r.Select(x => (x.Kind, x.Id, x.Tier)));
        Assert.Equal("Hunting binoculars", r[0].ItemName);
    }

    [Fact]
    public void TheRewardsTabIsTheFourthAndAnyTabLeavesAParkedLogOnTheFirst()
    {
        var nav = new HuntLogNav();
        nav.OpenPlayer(7, "Ann");
        nav.ShowTab(3);
        Assert.Equal(HuntLogPage.Rewards, nav.Page);
        Assert.Equal(3, nav.TabIndex);
        Assert.Equal(HuntLogBack.Close, nav.Back());
        nav.ShowTab(0);
        Assert.Equal(HuntLogPage.PlayerLog, nav.Page); // still parked on Ann's log
    }
}
