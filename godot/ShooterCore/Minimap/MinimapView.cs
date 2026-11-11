namespace Suchohrad.ShooterCore;

/// <summary>Which camera the minimap follows (ticket #150). Precedence matches the camera the player actually sees.</summary>
public enum MinimapViewSource { OnFoot, Vehicle, Turret, Missile }

/// <summary>
/// Minimap view yaw (ticket #150). The minimap turns to the direction the player's view points, not the body's yaw: in a
/// vehicle the body's mouse-look does not follow the car, so the chase camera's heading (the car's yaw) is used instead.
/// Angles are radians in the game's convention (forward = (-sin yaw, -cos yaw)). Every angle change goes the short way round.
/// </summary>
public static class MinimapView
{
    /// <summary>Source precedence: missile POV, then turret, then vehicle chase, then first person on foot.</summary>
    public static MinimapViewSource Select(bool missilePov, bool turret, bool driving) =>
        missilePov ? MinimapViewSource.Missile
        : turret ? MinimapViewSource.Turret
        : driving ? MinimapViewSource.Vehicle
        : MinimapViewSource.OnFoot;

    /// <summary>The yaw of the chosen source. Each argument is the yaw the matching camera is set to.</summary>
    public static float YawFor(MinimapViewSource source, float onFoot, float vehicle, float turret, float missile) => source switch
    {
        MinimapViewSource.Missile => missile,
        MinimapViewSource.Turret => turret,
        MinimapViewSource.Vehicle => vehicle,
        _ => onFoot,
    };

    /// <summary>Wraps an angle into (-pi, pi].</summary>
    public static float Wrap(float a)
    {
        const float twoPi = MathF.PI * 2f;
        a %= twoPi;
        if (a > MathF.PI) a -= twoPi;
        else if (a <= -MathF.PI) a += twoPi;
        return a;
    }

    /// <summary>The signed change from <paramref name="from"/> to <paramref name="to"/> along the shorter arc, in (-pi, pi].</summary>
    public static float ShortestDelta(float from, float to) => Wrap(to - from);

    /// <summary>
    /// Moves <paramref name="current"/> towards <paramref name="target"/> along the shorter arc, exponentially at
    /// <paramref name="rate"/> per second. Never passes the long way round, so a 180-degree or wrap-around change does not
    /// spin the minimap.
    /// </summary>
    public static float Follow(float current, float target, float dt, float rate)
    {
        if (dt <= 0f) return current;
        var k = 1f - MathF.Exp(-rate * dt);
        return Wrap(current + ShortestDelta(current, target) * k);
    }

    /// <summary>
    /// Texture side in texels for the map's whole extent: one texel per device pixel at the view's centre, clamped.
    /// <paramref name="devicePxPerMetre"/> is the minimap's on-screen scale (UI scale and device pixel ratio included).
    /// </summary>
    public static int TextureSide(float extentMetres, float devicePxPerMetre, int min, int max) =>
        Math.Clamp((int)MathF.Ceiling(extentMetres * devicePxPerMetre), min, max);

    /// <summary>Rebuild only when the wanted side differs by more than a quarter, so zoom and resize steps do not thrash.</summary>
    public static bool NeedsRebuild(int built, int wanted) => built <= 0 || Math.Abs(wanted - built) * 4 > built;
}
