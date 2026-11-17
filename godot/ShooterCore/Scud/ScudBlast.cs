namespace Drycastle.ShooterCore;

/// <summary>What a Scud blast does to a player: killed outright, hurt by so much, or untouched. See <see cref="ScudBlastRules.PlayerHit"/>.</summary>
public readonly record struct ScudPlayerHit(bool Lethal, float Damage)
{
    public bool Hurts => Lethal || Damage > 0;
}

/// <summary>
/// The numbers of a Scud blast (scud.json "blast"), pure so the tests run the server's own arithmetic. Distances are from the blast to the body (players) or the hull (vehicles).
/// Inside the lethal radius a player dies, in a vehicle or not and spawn protected or not; beyond it the damage starts at <see cref="ScudBlastData.OuterDamage"/> and falls off
/// linearly to <see cref="ScudBlastData.EdgeDamage"/> at the damage radius. A vehicle in the destroy radius is destroyed; beyond it takes hit points, falling off the same way.
/// What stands between the blast and the target (a shelter's walls, the world, a hull) is the server's to ask (<c>NetServer.ScudBlast</c>); this is only the distance.
/// </summary>
public static class ScudBlastRules
{
    /// <summary>The damage a lethal hit asks the match for (more than any hit points).</summary>
    public const float LethalDamage = 9999f;

    public static ScudPlayerHit PlayerHit(ScudBlastData b, float distance)
    {
        if (distance <= b.LethalRadiusM) return new ScudPlayerHit(true, LethalDamage);
        if (distance >= b.DamageRadiusM) return new ScudPlayerHit(false, 0);
        return new ScudPlayerHit(false, MathF.Round(Falloff(distance, (float)b.LethalRadiusM, (float)b.DamageRadiusM, (float)b.OuterDamage, (float)b.EdgeDamage)));
    }

    /// <summary>The hit points a vehicle <paramref name="hullDistance"/> from the blast loses: all of them (<paramref name="hp"/> and one more, so it is doomed) inside the destroy radius, the falloff beyond it, nothing past the damage radius.</summary>
    public static float VehicleDamage(ScudBlastData b, float hullDistance, float hp)
    {
        if (hullDistance <= b.VehicleDestroyRadiusM) return hp + 1f;
        if (hullDistance >= b.VehicleDamageRadiusM) return 0f;
        return MathF.Round(Falloff(hullDistance, (float)b.VehicleDestroyRadiusM, (float)b.VehicleDamageRadiusM, (float)b.VehicleOuterDamage, (float)b.VehicleEdgeDamage));
    }

    /// <summary>Linear from <paramref name="at"/> at <paramref name="inner"/> to <paramref name="edge"/> at <paramref name="outer"/>.</summary>
    static float Falloff(float d, float inner, float outer, float at, float edge)
    {
        var t = Math.Clamp((d - inner) / MathF.Max(1e-4f, outer - inner), 0f, 1f);
        return at + (edge - at) * t;
    }

    /// <summary>
    /// The shake a camera <paramref name="distance"/> from the blast feels, 0 to <see cref="ScudBlastData.ShakeAmplitude"/>: full at the blast, nothing at the shake range, the square of the
    /// distance left in between (so it stays felt far out). Map wide at the shipped range.
    /// </summary>
    public static float Shake(ScudBlastData b, float distance)
    {
        var k = Math.Clamp(1f - distance / (float)b.ShakeRangeM, 0f, 1f);
        return (float)b.ShakeAmplitude * k * k;
    }

    /// <summary>Seconds after the blast that its wave and sound reach a camera <paramref name="distance"/> away.</summary>
    public static float ArrivalSeconds(ScudBlastData b, float distance) => distance / (float)b.WaveSpeedMps;
}
