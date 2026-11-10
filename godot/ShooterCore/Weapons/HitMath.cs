using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>A player as a hit target: feet position, crouch state and the yaw they face.</summary>
public readonly record struct HitTarget(ushort Id, Vector3 Feet, bool Crouching, float Yaw);

public readonly record struct RayHit(ushort Id, float Distance, bool Head);
public readonly record struct MeleeHit(ushort Id, bool Backstab, float Distance);

/// <summary>
/// Server-side hit resolution without Godot: ray against the player capsule and head sphere, damage falloff, aim
/// direction with spread, melee reach and backstab test. No lag compensation: targets are at their current positions.
/// </summary>
public static class HitMath
{
    public const float HeadRadius = 0.2f;
    public const float MeleeHalfAngleDegrees = 60f;
    public const float MeleeVerticalReach = 1.6f;

    public static float Height(bool crouching) => MovementStep.CapsuleHeight(crouching);

    /// <summary>Head hit sphere centre: the top of the body capsule minus the head radius.</summary>
    public static Vector3 HeadCenter(in HitTarget t) => t.Feet + new Vector3(0, Height(t.Crouching) - HeadRadius, 0);

    /// <summary>Unit view direction for Godot yaw (0 faces -Z, positive turns left) and pitch (positive looks up).</summary>
    public static Vector3 Forward(float yaw, float pitch)
    {
        var (sy, cy) = MathF.SinCos(yaw);
        var (sp, cp) = MathF.SinCos(pitch);
        return new Vector3(-sy * cp, sp, -cy * cp);
    }

    /// <summary>Forward rotated by a random offset uniformly inside a cone of half angle <paramref name="spreadDegrees"/>.</summary>
    public static Vector3 SpreadDirection(float yaw, float pitch, float spreadDegrees, Random rng)
    {
        var f = Forward(yaw, pitch);
        if (spreadDegrees <= 0) return f;
        var up = MathF.Abs(f.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(f, up));
        var u = Vector3.Cross(right, f);
        var angle = spreadDegrees * MathF.PI / 180f * MathF.Sqrt((float)rng.NextDouble());
        var phi = (float)rng.NextDouble() * MathF.Tau;
        var d = f * MathF.Cos(angle) + (right * MathF.Cos(phi) + u * MathF.Sin(phi)) * MathF.Sin(angle);
        return Vector3.Normalize(d);
    }

    /// <summary>Damage of a hitscan hit: full to the falloff start, linear to the falloff multiplier at the end, flat beyond.</summary>
    public static float HitscanDamage(WeaponDef w, float distance, bool head)
    {
        var mult = 1f;
        if (distance > w.FalloffStart && w.FalloffEnd > w.FalloffStart)
        {
            var t = Math.Min(1f, (distance - w.FalloffStart) / (w.FalloffEnd - w.FalloffStart));
            mult = 1f + (w.FalloffMultiplier - 1f) * t;
        }
        var dmg = w.Damage * mult * (head ? Math.Max(1f, w.HeadshotMultiplier) : 1f);
        return MathF.Max(1f, MathF.Round(dmg));
    }

    /// <summary>
    /// Nearest target hit by the ray, or null. <paramref name="wallDistance"/> is where the world blocks the ray
    /// (infinity if nothing); targets behind it are not hit. A hit is a headshot when the ray also passes through the head sphere.
    /// </summary>
    public static RayHit? Raycast(Vector3 origin, Vector3 dir, float maxDistance, float wallDistance, IEnumerable<HitTarget> targets, ushort shooter)
    {
        var limit = MathF.Min(maxDistance, wallDistance);
        RayHit? best = null;
        foreach (var t in targets)
        {
            if (t.Id == shooter) continue;
            if (RayCapsule(origin, dir, t.Feet, Height(t.Crouching), MoveConsts.CapsuleRadius) is not { } d || d > limit) continue;
            if (best is { } b && b.Distance <= d) continue;
            var head = RaySphere(origin, dir, HeadCenter(t), HeadRadius) is not null;
            best = new RayHit(t.Id, d, head);
        }
        return best;
    }

    /// <summary>Distance along the ray to the sphere, 0 if the origin is inside, null on a miss (only forward hits).</summary>
    public static float? RaySphere(Vector3 o, Vector3 d, Vector3 c, float r)
    {
        var m = o - c;
        var b = Vector3.Dot(m, d);
        var cc = Vector3.Dot(m, m) - r * r;
        if (cc > 0 && b > 0) return null;
        var disc = b * b - cc;
        if (disc < 0) return null;
        return MathF.Max(0, -b - MathF.Sqrt(disc));
    }

    /// <summary>Ray against an upright capsule (feet position, total height, radius): cylinder side plus two end spheres.</summary>
    public static float? RayCapsule(Vector3 o, Vector3 d, Vector3 feet, float height, float radius)
    {
        var y0 = feet.Y + radius; var y1 = feet.Y + height - radius;
        float? best = null;
        void Take(float? t) { if (t is { } v && (best is null || v < best)) best = v; }
        Take(RaySphere(o, d, new Vector3(feet.X, y0, feet.Z), radius));
        Take(RaySphere(o, d, new Vector3(feet.X, y1, feet.Z), radius));
        // side: infinite cylinder around the vertical axis, clipped to y0..y1
        var ox = o.X - feet.X; var oz = o.Z - feet.Z;
        var a = d.X * d.X + d.Z * d.Z;
        if (a > 1e-8f)
        {
            var b = ox * d.X + oz * d.Z;
            var c = ox * ox + oz * oz - radius * radius;
            var disc = b * b - a * c;
            if (disc >= 0)
            {
                var s = MathF.Sqrt(disc);
                foreach (var t in new[] { (-b - s) / a, (-b + s) / a })
                {
                    if (t < 0) continue;
                    var y = o.Y + d.Y * t;
                    if (y >= y0 && y <= y1) { Take(t); break; }
                }
            }
        }
        return best;
    }

    /// <summary>True if <paramref name="attacker"/> is inside the back 90 degrees of the victim (45 degrees either side of straight behind).</summary>
    public static bool IsBackstab(Vector3 attackerFeet, Vector3 victimFeet, float victimYaw)
    {
        var to = attackerFeet - victimFeet; to.Y = 0;
        if (to.LengthSquared() < 1e-6f) return false;
        var vf = Forward(victimYaw, 0);
        return Vector3.Dot(Vector3.Normalize(to), vf) <= -MathF.Cos(MathF.PI / 4f) + 1e-4f;
    }

    /// <summary>
    /// Melee overlap: the nearest target whose capsule edge is within <paramref name="reach"/> of the attacker, in front
    /// (inside a 120 degree cone) and at about the same height. Walls are the caller's business.
    /// </summary>
    public static MeleeHit? Melee(Vector3 attackerFeet, float attackerYaw, float reach, IEnumerable<HitTarget> targets, ushort attacker)
    {
        var fwd = Forward(attackerYaw, 0);
        var cos = MathF.Cos(MeleeHalfAngleDegrees * MathF.PI / 180f);
        MeleeHit? best = null;
        foreach (var t in targets)
        {
            if (t.Id == attacker) continue;
            var to = t.Feet - attackerFeet;
            if (MathF.Abs(to.Y) > MeleeVerticalReach) continue;
            to.Y = 0;
            var len = to.Length();
            var edge = len - MoveConsts.CapsuleRadius;
            if (edge > reach) continue;
            if (len > MoveConsts.CapsuleRadius && Vector3.Dot(to / len, fwd) < cos) continue;
            if (best is { } b && b.Distance <= edge) continue;
            best = new MeleeHit(t.Id, IsBackstab(attackerFeet, t.Feet, t.Yaw), edge);
        }
        return best;
    }

    /// <summary>Melee damage: backstab damage when behind the victim (and the weapon has one), else the normal damage.</summary>
    public static float MeleeDamage(WeaponDef w, bool backstab) => backstab && w.BackstabDamage > 0 ? w.BackstabDamage : w.Damage;
}
