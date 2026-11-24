using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.ShooterCore.Hunts;

/// <summary>How rare a hunted animal's weight is for its kind (GLOSSARY "Hunt rarity"): the Life List tile's rim. Ordered, so the best of two is the larger.</summary>
public enum HuntTier : byte { Common = 0, Bronze = 1, Silver = 2, Gold = 3 }

/// <summary>
/// The rarity of a hunt, from where its weight lies in the weight distribution of its exact species and look (a doe and a deer, a moose cow and a bull
/// have their own ranges): the heaviest 5 % are gold, 10 % silver, 20 % bronze, the rest common. A weight is the range at a roll that is the mean of two
/// uniform draws (<see cref="AnimalRules.RollSize"/>), so the share of animals at least as heavy is known exactly and nothing is counted from the stored hunts.
/// A Mystery creature has no weight and no rarity.
/// </summary>
public static class HuntRarity
{
    public const float GoldShare = 0.05f, SilverShare = 0.10f, BronzeShare = 0.20f;

    /// <summary>The share of animals whose roll is at least <paramref name="roll"/> (0..1): the upper tail of the mean of two uniform draws.</summary>
    public static float TopShare(float roll)
    {
        var r = Math.Clamp(roll, 0f, 1f);
        return r >= 0.5f ? 2f * (1f - r) * (1f - r) : 1f - 2f * r * r;
    }

    public static HuntTier OfShare(float share) =>
        share <= GoldShare ? HuntTier.Gold : share <= SilverShare ? HuntTier.Silver : share <= BronzeShare ? HuntTier.Bronze : HuntTier.Common;

    /// <summary>The tier of a weight within a [min, max] kg range; a range with no width has no rare weights.</summary>
    public static HuntTier Of(IReadOnlyList<float> range, float kg)
    {
        if (range.Count != 2 || !(range[1] > range[0])) return HuntTier.Common;
        return OfShare(TopShare((kg - range[0]) / (range[1] - range[0])));
    }

    /// <summary>The tier of a hunt by the ranges of <paramref name="animals"/>; common for a mystery, a species or a look the file does not know.</summary>
    public static HuntTier Of(AnimalsFile animals, HuntRecord h)
    {
        if (h.Kg is not { } kg) return HuntTier.Common;
        foreach (var def in animals.Animals)
        {
            if (def.Id != h.Exact) continue;
            var variant = 0;
            for (var i = 0; i < def.Variants.Count; i++) if (def.Variants[i].Id == h.Variant) variant = i + 1;
            return def.WeightKgOf(variant) is { } range ? Of(range, kg) : HuntTier.Common;
        }
        return HuntTier.Common;
    }
}
