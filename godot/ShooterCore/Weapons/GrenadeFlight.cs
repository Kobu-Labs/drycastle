using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where a grenade's tick ended: its new position and velocity, and whether it struck a surface (a bounce, or coming to rest).</summary>
public readonly record struct GrenadeStep(Vector3 Pos, Vector3 Vel, bool Bounced, bool AtRest);

/// <summary>What stops a ray: the distance (infinity if nothing) and the unit normal of the surface.</summary>
public readonly record struct GrenadeStop(float Distance, Vector3 Normal);

/// <summary>
/// A thrown grenade's per-tick rule (weapons.json <c>grenade</c>), pure so the tests run the server's own arithmetic. It falls under gravity, flies through
/// players, and bounces off the world and vehicles: the speed along the surface's normal is kept by <see cref="GrenadeDef.Bounce"/>, across it reduced by
/// <see cref="GrenadeDef.GroundFriction"/>; below <see cref="RestSpeed"/> on a floor it lies still. The fuse is a <see cref="Countdown"/> the owner ticks.
/// </summary>
public static class GrenadeFlight
{
    /// <summary>A grenade slower than this (m/s) after striking a floor lies still.</summary>
    public const float RestSpeed = 0.8f;

    /// <summary>The floor: a surface whose normal points up by at least this much (cosine of its slope).</summary>
    public const float FloorNormalY = 0.6f;

    /// <summary>How far in front of a surface the grenade is stopped, metres, so the next tick does not start inside it. About the grenade's own radius: it lies on the floor, not in it (#421).</summary>
    public const float Skin = 0.07f;

    public static GrenadeStep Step(GrenadeDef g, Vector3 pos, Vector3 vel, float dt, Func<Vector3, Vector3, float, GrenadeStop> cast)
    {
        vel.Y -= g.Gravity * dt;
        var move = vel * dt;
        var len = move.Length();
        if (len < 1e-6f) return new GrenadeStep(pos, vel, false, vel.LengthSquared() < 1e-6f);
        var dir = move / len;
        var stop = cast(pos, dir, len + Skin);
        if (stop.Distance > len + Skin) return new GrenadeStep(pos + move, vel, false, false);

        var at = pos + dir * MathF.Max(0, stop.Distance - Skin);
        var n = stop.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(stop.Normal) : -dir;
        var vn = Vector3.Dot(vel, n);
        var normal = n * vn;
        var across = (vel - normal) * (1f - g.GroundFriction);
        var bounced = across - normal * g.Bounce; // vn is negative into the surface: this reverses it
        var rest = n.Y >= FloorNormalY && bounced.Length() < RestSpeed;
        return new GrenadeStep(at, rest ? Vector3.Zero : bounced, true, rest);
    }
}
