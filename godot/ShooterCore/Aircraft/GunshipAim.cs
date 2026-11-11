using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Turret aim, world space and ground-stabilised (#160). The aim is a world point: the ground point the turret's look meets
/// when the look is set (the mouse moves it), and it stays put while the platform orbits. The turret's view is the direction
/// from the muzzle to that point, so the orbit never moves the reticle; only the parallax of the moving muzzle changes the
/// direction. There is no yaw limit (an airframe-relative limit moves with the orbit and pins the view); only the pitch is
/// limited (looking down). Yaw is a plain world angle: <see cref="Unwrap"/> keeps the camera's angle continuous across +-pi.
/// </summary>
public static class GunshipAim
{
    /// <summary>Wraps an angle to [-pi, pi]. For a difference of two angles; never for a value that is drawn.</summary>
    public static float Wrap(float a) => MathF.IEEERemainder(a, MathF.PI * 2f);

    /// <summary>
    /// The representative of <paramref name="next"/> (an angle mod 2 pi) nearest <paramref name="previous"/>: a continuous angle
    /// sequence, so the camera never takes the long way round at the +-pi cut.
    /// </summary>
    public static float Unwrap(float previous, float next) => previous + Wrap(next - previous);

    /// <summary>Clamps a pitch (radians) to the turret's range.</summary>
    public static float ClampPitch(float pitch, GunshipDef def) =>
        Math.Clamp(pitch, def.PitchMinDeg * MathF.PI / 180f, def.PitchMaxDeg * MathF.PI / 180f);

    /// <summary>The farthest the aim point is placed: the longest weapon range of the turret (a look that finds no ground in that range).</summary>
    public static float MaxDistance(GunshipDef def) => def.Weapons.Max(w => w.RangeM);

    /// <summary>
    /// The aim point of a look: where the ray from the muzzle along (yaw, pitch) meets the ground plane <paramref name="groundY"/>,
    /// or the point <paramref name="maxDistance"/> along the ray when it meets no ground that near. A plane stands in for the
    /// ground so the client and the server compute the same point (the physics raycast is not shared).
    /// </summary>
    public static Vector3 AimPoint(Vector3 muzzle, float yaw, float pitch, float groundY, float maxDistance)
    {
        var d = HitMath.Forward(yaw, pitch);
        var t = maxDistance;
        if (d.Y < -1e-4f)
        {
            var hit = (muzzle.Y - groundY) / -d.Y;
            if (hit > 0 && hit < t) t = hit;
        }
        return muzzle + d * t;
    }

    /// <summary>
    /// The turret's view from the muzzle toward an aim point: the engine yaw and pitch (as <see cref="HitMath.Forward"/>),
    /// with the pitch limited. Yaw is the full world angle, so the view can face any way.
    /// </summary>
    public static (float Yaw, float Pitch) View(Vector3 muzzle, Vector3 point, GunshipDef def)
    {
        var d = point - muzzle;
        var yaw = MathF.Atan2(-d.X, -d.Z);
        var pitch = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
        return (yaw, ClampPitch(pitch, def));
    }

    /// <summary>
    /// The look after a mouse move of <paramref name="dYaw"/> and <paramref name="dPitch"/> (radians, as the mouse gave them),
    /// scaled down by the zoom: x4 turns a quarter as fast. The pitch stays in range.
    /// </summary>
    public static (float Yaw, float Pitch) Turn(float yaw, float pitch, float dYaw, float dPitch, float zoom, GunshipDef def)
    {
        var z = zoom < 1f ? 1f : zoom;
        return (yaw + dYaw / z, ClampPitch(pitch + dPitch / z, def));
    }
}
