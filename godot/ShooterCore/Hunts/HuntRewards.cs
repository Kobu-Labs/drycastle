namespace Drycastle.ShooterCore;

/// <summary>One reward of a collection tier (#473): a weapon or an item that carries the tier as its <c>reward_tier</c> in the data.</summary>
/// <param name="Kind">"weapon", "item" or "companion" (<see cref="CompanionRules.UnlockKind"/>): which icon set and which table the id is in.</param>
/// <param name="ItemName">The item's or the Companion's display name; empty for a weapon (the client names it).</param>
public sealed record HuntReward(HuntTier Tier, string Kind, string Id, string ItemName);

/// <summary>A player's progress to one collection tier: how many Index species have a hunt of the tier or better, of how many there are.</summary>
public sealed record TierProgress(HuntTier Tier, int Have, int Of)
{
    public bool Reached => Have >= Of;
}

/// <summary>The Rewards tab of the Hunter's Log (#473): the progress to each collection tier and what each tier gives. Pure, from the Life List and the data files.</summary>
public static class HuntRewards
{
    /// <summary>The tiers a collection can reach, in the order the tab lists them.</summary>
    public static readonly HuntTier[] Tiers = { HuntTier.Bronze, HuntTier.Silver, HuntTier.Gold };

    /// <summary>
    /// Progress to bronze, silver and gold from a Life List (<see cref="PlayerLogView.Life"/>): each tile of an Index species whose rarest hunt is the tier or better
    /// counts; Mystery creatures (hidden or not) and tiles never hunted do not. Agrees with <see cref="HuntBook.CollectionTier"/> (a tier is reached when all are had).
    /// </summary>
    public static IReadOnlyList<TierProgress> Progress(IReadOnlyList<LifeEntry> life)
    {
        var index = life.Where(l => !l.Hidden && IndexSpecies.Is(l.Species)).ToList();
        return Tiers.Select(t => new TierProgress(t, index.Count(l => l.Caught && l.Tier >= t), IndexSpecies.All.Count)).ToArray();
    }

    /// <summary>Every reward in the data, lowest tier first: the weapons and the items with a <c>reward_tier</c>.</summary>
    public static IReadOnlyList<HuntReward> For(WeaponsFile weapons, ItemsFile items, CompanionsFile? companions = null)
    {
        var list = new List<HuntReward>();
        foreach (var w in weapons.Weapons)
            if (w.IsReward) list.Add(new HuntReward(w.RewardTier, "weapon", w.Id, ""));
        foreach (var i in items.Items)
            if (i.RewardTierOf != HuntTier.Common) list.Add(new HuntReward(i.RewardTierOf, "item", i.Id, i.Name));
        foreach (var c in companions?.Companions ?? new())
            if (c.RewardTierOf != HuntTier.Common) list.Add(new HuntReward(c.RewardTierOf, CompanionRules.UnlockKind, c.Id, c.Name));
        return list.OrderBy(r => r.Tier).ToList();
    }
}
