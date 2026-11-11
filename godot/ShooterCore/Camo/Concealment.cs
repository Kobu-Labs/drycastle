namespace Suchohrad.ShooterCore;

/// <summary>
/// Concealment (0..1) from stance and movement, the gameplay hook of the camouflage framework. It is server-side data:
/// the server computes it from the player's settled stance and horizontal speed, and the spotting range it feeds
/// (name tags, kill-cam) is shortened by a data-defined multiplier. Matching the suit to the surroundings or the time
/// of day is out of scope; a future foliage or light term would be added here as another factor.
/// </summary>
public static class Concealment
{
    /// <summary>
    /// The concealment of a player. The stance base (camo.json) is reduced by movement: at or above
    /// <c>full_speed_mps</c> the player loses <c>stillness_weight</c> of it. So prone and still is best, standing and running is worst.
    /// </summary>
    public static float Value(Stance stance, float horizontalSpeed, ConcealmentRules rules)
    {
        var moving = Math.Clamp(horizontalSpeed / rules.FullSpeedMps, 0f, 1f);
        var value = rules.ForStance(stance) * (1f - rules.StillnessWeight * moving);
        return Math.Clamp(value, 0f, 1f);
    }

    /// <summary>The spotting range after concealment: full range at 0, <c>min_multiplier</c> of it at full concealment.</summary>
    public static float SpottingRange(float baseRange, float concealment, float minMultiplier)
    {
        var c = Math.Clamp(concealment, 0f, 1f);
        return baseRange * (1f - c * (1f - minMultiplier));
    }
}
