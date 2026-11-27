using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// The Cartel's numbers by tier (cartel.json <c>jobs.&lt;kind&gt;</c>, trap_house.json <c>drill</c>): a list of one number per tier, tier 1 first. Every kind reads and checks its lists
/// here, so a tier means the same entry everywhere.
/// </summary>
public static class CartelTiers
{
    /// <summary>The number of a tier: tier 1 is the first entry; a tier past the list takes the last, one below 1 the first.</summary>
    public static T Of<T>(IReadOnlyList<T> perTier, int tier) => perTier[Math.Clamp(tier, 1, perTier.Count) - 1];

    /// <summary>
    /// Checks a list of one number per tier: <paramref name="tiers"/> entries, each one <paramref name="ok"/> takes. <paramref name="what"/> names the list in the error
    /// (<c>cartel.json: jobs.hit.gunmen</c>), <paramref name="rule"/> says what a number must be ("more than 0").
    /// </summary>
    public static void Check<T>(IReadOnlyList<T> perTier, int tiers, string what, Func<T, bool> ok, string rule)
    {
        if (perTier.Count != tiers) throw new DataFileException($"{what} needs one number per tier ({tiers})");
        foreach (var v in perTier) if (!ok(v)) throw new DataFileException($"{what}: every number must be {rule}");
    }

    /// <summary>A timer or a run: every number finite and more than 0.</summary>
    public static bool Positive(float s) => float.IsFinite(s) && s > 0;

    /// <summary>A count: 0 or more.</summary>
    public static bool Count(int n) => n >= 0;
}
