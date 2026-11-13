namespace Drycastle.ShooterCore;

/// <summary>
/// Pure rules for walking through water by depth (ADR 0010): the speed eases from land speed at the ankles to the swim
/// speed at swim depth, and a landing in water takes off part of the fall damage. No Godot here: prediction and the server
/// call the same functions with the same depth.
/// </summary>
public static class Wading
{
    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Smoothstep: 0 at 0, 1 at 1, with no kink at either end.</summary>
    static float Ease(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Walking speed (m/s) for a land speed at a water depth under the feet. Dry or ankle-deep: the land speed.
    /// Ankle to <see cref="WaterConsts.WadeDepth"/>: eases to <see cref="WaterConsts.WadeSpeedFloor"/> of it.
    /// From there to <see cref="WaterConsts.SwimDepth"/>: blends into <see cref="WaterConsts.SwimSpeed"/>. Swim depth or deeper: the swim speed.
    /// </summary>
    public static float Speed(float landSpeed, float depth)
    {
        if (depth >= WaterConsts.SwimDepth) return WaterConsts.SwimSpeed;
        if (depth <= WaterConsts.AnkleDepth) return landSpeed;
        if (depth <= WaterConsts.WadeDepth)
        {
            var t = (depth - WaterConsts.AnkleDepth) / (WaterConsts.WadeDepth - WaterConsts.AnkleDepth);
            return landSpeed * Lerp(1f, WaterConsts.WadeSpeedFloor, Ease(t));
        }
        var u = (depth - WaterConsts.WadeDepth) / (WaterConsts.SwimDepth - WaterConsts.WadeDepth);
        return Lerp(landSpeed * WaterConsts.WadeSpeedFloor, WaterConsts.SwimSpeed, Ease(u));
    }

    /// <summary>
    /// Share of the fall damage a landing keeps, by the water depth at the landing: all of it on dry ground or at the
    /// ankles, half at <see cref="WaterConsts.WadeDepth"/> (shallow water still hurts), none from <see cref="WaterConsts.SwimDepth"/> on.
    /// </summary>
    public static float LandingDamageShare(float depth)
    {
        if (depth <= WaterConsts.AnkleDepth) return 1f;
        if (depth <= WaterConsts.WadeDepth)
            return Lerp(1f, 0.5f, Ease((depth - WaterConsts.AnkleDepth) / (WaterConsts.WadeDepth - WaterConsts.AnkleDepth)));
        if (depth >= WaterConsts.SwimDepth) return 0f;
        return Lerp(0.5f, 0f, Ease((depth - WaterConsts.WadeDepth) / (WaterConsts.SwimDepth - WaterConsts.WadeDepth)));
    }
}
