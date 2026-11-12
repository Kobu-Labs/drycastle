namespace Drycastle.ShooterCore;

/// <summary>
/// The third-person camera on a driven ground vehicle (#218): how far behind and how high above the vehicle it wants to sit, and how it
/// pulls in when the line to that place is blocked. Pure, so the numbers are testable; Player.Chase does the ray and the node moves.
/// See docs/vehicle-chase-camera.md for the reasoning behind the defaults.
/// </summary>
public static class ChaseCamera
{
    /// <summary>Distance and height the camera had for every vehicle before #218 (a car's half length is about 2.2 m).</summary>
    public const float BaseDistance = 6.5f, BaseHeight = 1.5f;
    /// <summary>Half length at and below which the base values apply; longer vehicles pull back from there.</summary>
    public const float ReferenceHalfLength = 2.2f;
    /// <summary>Extra metres of distance and height per metre of half length above the reference.</summary>
    public const float DistancePerMetre = 1.2f, HeightPerMetre = 0.4f;
    /// <summary>The camera stops this far short of what the ray hit, metres.</summary>
    public const float Margin = 0.4f;
    /// <summary>The closest the camera may be pulled in to, metres.</summary>
    public const float MinDistance = 1.0f;
    /// <summary>How fast the camera eases back out after the obstacle is gone, m/s. Pulling in is instant (never see through a wall).</summary>
    public const float RecoverSpeed = 8f;

    /// <summary>The wanted (distance, height): the data file's override if given, else derived from the vehicle's length. Cars and smaller keep the old values.</summary>
    public static (float Distance, float Height) Wanted(VehicleDef v)
    {
        var extra = Math.Max(0f, v.HalfLength - ReferenceHalfLength);
        return (v.ChaseDistance ?? BaseDistance + DistancePerMetre * extra, v.ChaseHeight ?? BaseHeight + HeightPerMetre * extra);
    }

    /// <summary>
    /// One smoothing step of the camera's distance. <paramref name="free"/> is how far along the line the camera may go this frame (the wanted
    /// distance when nothing is hit, else the hit distance less the margin). Coming in is instant; going back out is limited to <see cref="RecoverSpeed"/>.
    /// </summary>
    public static float Step(float current, float wanted, float free, float dt)
    {
        var limit = Math.Max(MinDistance, Math.Min(wanted, free));
        return limit <= current ? limit : Math.Min(limit, current + RecoverSpeed * Math.Max(0f, dt));
    }
}
