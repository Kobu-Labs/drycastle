using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>A player as a hit target: feet position, stance (sets the capsule height) and the yaw they face.</summary>
public readonly record struct HitTarget(ushort Id, Vector3 Feet, Stance Stance, float Yaw);

public readonly record struct RayHit(ushort Id, float Distance, bool Head);
public readonly record struct MeleeHit(ushort Id, bool Backstab, float Distance);

/// <summary>
/// Server-side hit resolution without Godot: ray against the player capsule and head sphere, damage falloff, aim
/// direction with spread, melee reach and backstab test. No lag compensation: targets are at their current positions.
/// </summary>
public static class HitMath
{
    public const float HeadRadius = PlayerHitBox.HeadRadius;
    public const float MeleeHalfAngleDegrees = 60f;
    public const float MeleeVerticalReach = 1.6f;

    /// <summary>Height of the upright body for a stance (the prone figure is horizontal; see <see cref="Axis"/>).</summary>
    public static float Height(Stance stance) => MovementStep.CapsuleHeight(stance);

    /// <summary>Spread in degrees for a stance: prone scales it by the weapon's prone spread multiplier, other stances leave it alone.</summary>
    public static float StanceSpread(float degrees, WeaponDef w, Stance stance) => stance == Stance.Prone ? degrees * w.ProneSpreadMultiplier : degrees;

    /// <summary>
    /// The body's hit capsule axis (segment end points; the capsule radius is <see cref="PlayerHitBox.Radius"/>). Stand and crouch are upright
    /// from the feet. Prone is horizontal along the facing direction, centred on the feet, as the drawn figure lies.
    /// </summary>
    public static (Vector3 A, Vector3 B) Axis(in HitTarget t)
    {
        if (t.Stance == Stance.Prone)
        {
            var half = PlayerHitBox.StandHeight / 2f - PlayerHitBox.Radius;
            var up = new Vector3(0, PlayerHitBox.ProneLift, 0);
            var along = Forward(t.Yaw, 0) * half;
            return (t.Feet + up - along, t.Feet + up + along);
        }
        return (t.Feet + new Vector3(0, PlayerHitBox.Radius, 0), t.Feet + new Vector3(0, Height(t.Stance) - PlayerHitBox.Radius, 0));
    }

    /// <summary>Centre of the body's hit capsule.</summary>
    public static Vector3 Centre(in HitTarget t)
    {
        var (a, b) = Axis(t);
        return (a + b) / 2f;
    }

    /// <summary>Head hit sphere centre: upright, the top of the body minus the head radius; prone, forward of the body centre at the head height.</summary>
    public static Vector3 HeadCenter(in HitTarget t)
    {
        if (t.Stance == Stance.Prone)
            return t.Feet + Forward(t.Yaw, 0) * PlayerHitBox.ProneHeadOffset + new Vector3(0, PlayerHitBox.ProneLift, 0);
        return t.Feet + new Vector3(0, Height(t.Stance) - HeadRadius, 0);
    }

    /// <summary>Distance from a point to the body's hit volume (0 inside it), for projectiles and splash.</summary>
    public static float DistanceToBody(Vector3 p, in HitTarget t)
    {
        var (a, b) = Axis(t);
        var ab = b - a;
        var len2 = ab.LengthSquared();
        var s = len2 > 1e-8f ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return MathF.Max(0, Vector3.Distance(p, a + ab * s) - PlayerHitBox.Radius);
    }

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
    public static float HitscanDamage(WeaponDef w, float distance, bool head) => Falloff(w, w.Damage, distance, head);

    /// <summary>Damage of one shotgun pellet: the hitscan falloff applied to <see cref="WeaponDef.DamagePerPellet"/>. Rounded per pellet, at least 1.</summary>
    public static float PelletDamage(WeaponDef w, float distance, bool head) => Falloff(w, w.DamagePerPellet, distance, head);

    static float Falloff(WeaponDef w, float baseDamage, float distance, bool head)
    {
        var mult = 1f;
        if (distance > w.FalloffStart && w.FalloffEnd > w.FalloffStart)
        {
            var t = Math.Min(1f, (distance - w.FalloffStart) / (w.FalloffEnd - w.FalloffStart));
            mult = 1f + (w.FalloffMultiplier - 1f) * t;
        }
        var dmg = baseDamage * mult * (head ? Math.Max(1f, w.HeadshotMultiplier) : 1f);
        return MathF.Max(1f, MathF.Round(dmg));
    }

    /// <summary>Damage one target took from a pellet fan: the summed pellet damage, and whether any pellet hit the head.</summary>
    public readonly record struct PelletTotal(float Damage, bool Head);

    /// <summary>
    /// Pellet directions of one shotgun shot: <paramref name="pellets"/> unit vectors uniform by area in a cone of half angle
    /// <paramref name="coneDegrees"/> around <paramref name="aim"/>. Derived only from the seed, so the server and every client
    /// get the same fan from one broadcast event.
    /// </summary>
    public static Vector3[] PelletDirections(Vector3 aim, float coneDegrees, int pellets, uint seed)
    {
        var f = Vector3.Normalize(aim);
        var up = MathF.Abs(f.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(f, up));
        var u = Vector3.Cross(right, f);
        var maxAngle = coneDegrees * MathF.PI / 180f;
        var state = (ulong)seed;
        var dirs = new Vector3[Math.Max(0, pellets)];
        for (var i = 0; i < dirs.Length; i++)
        {
            var angle = maxAngle * MathF.Sqrt(NextUnit(ref state));
            var phi = NextUnit(ref state) * MathF.Tau;
            dirs[i] = Vector3.Normalize(f * MathF.Cos(angle) + (right * MathF.Cos(phi) + u * MathF.Sin(phi)) * MathF.Sin(angle));
        }
        return dirs;
    }

    /// <summary>SplitMix64 step mapped to [0, 1). Fixed arithmetic, so the same seed gives the same numbers on every platform.</summary>
    static float NextUnit(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) * (1f / (1 << 24));
    }

    /// <summary>
    /// Resolves one pellet fan against the targets: pellet i flies along <paramref name="dirs"/>[i] for at most
    /// <paramref name="maxDistance"/> and stops at <paramref name="walls"/>[i] (the world distance, infinity if none). Each pellet
    /// hits the nearest target in its path with its own falloff; damage is summed per target. Walls and vehicles are the caller's business.
    /// </summary>
    public static Dictionary<ushort, PelletTotal> ResolvePellets(WeaponDef w, Vector3 origin, IReadOnlyList<Vector3> dirs, float maxDistance,
        IReadOnlyList<float> walls, IReadOnlyList<HitTarget> targets, ushort shooter)
    {
        var totals = new Dictionary<ushort, PelletTotal>();
        for (var i = 0; i < dirs.Count; i++)
        {
            if (Raycast(origin, dirs[i], maxDistance, walls[i], targets, shooter) is not { } hit) continue;
            var prev = totals.GetValueOrDefault(hit.Id);
            totals[hit.Id] = new PelletTotal(prev.Damage + PelletDamage(w, hit.Distance, hit.Head), prev.Head || hit.Head);
        }
        return totals;
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
            var (p0, p1) = Axis(t);
            if (RayCapsule(origin, dir, p0, p1, PlayerHitBox.Radius) is not { } d || d > limit) continue;
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

    /// <summary>Ray against an upright capsule (feet position, total height, radius), as <see cref="RayCapsule(Vector3, Vector3, Vector3, Vector3, float)"/>.</summary>
    public static float? RayCapsule(Vector3 o, Vector3 d, Vector3 feet, float height, float radius) =>
        RayCapsule(o, d, feet + new Vector3(0, radius, 0), feet + new Vector3(0, height - radius, 0), radius);

    /// <summary>Ray against a capsule with end points <paramref name="a"/> and <paramref name="b"/> (the axis) and radius: two end spheres plus the cylinder between them.</summary>
    public static float? RayCapsule(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float radius)
    {
        float? best = null;
        void Take(float? t) { if (t is { } v && (best is null || v < best)) best = v; }
        Take(RaySphere(o, d, a, radius));
        Take(RaySphere(o, d, b, radius));
        // side: infinite cylinder around the axis, clipped to the segment a..b
        var axis = b - a;
        var len2 = axis.LengthSquared();
        if (len2 > 1e-8f)
        {
            var len = MathF.Sqrt(len2);
            var u = axis / len;
            var oo = o - a;
            var dPerp = d - u * Vector3.Dot(d, u);
            var oPerp = oo - u * Vector3.Dot(oo, u);
            var qa = dPerp.LengthSquared();
            if (qa > 1e-8f)
            {
                var qb = Vector3.Dot(oPerp, dPerp);
                var qc = oPerp.LengthSquared() - radius * radius;
                var disc = qb * qb - qa * qc;
                if (disc >= 0)
                {
                    var s = MathF.Sqrt(disc);
                    foreach (var t in new[] { (-qb - s) / qa, (-qb + s) / qa })
                    {
                        if (t < 0) continue;
                        var along = Vector3.Dot(oo + d * t, u);
                        if (along >= 0 && along <= len) { Take(t); break; }
                    }
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
            var edge = len - PlayerHitBox.Radius;
            if (edge > reach) continue;
            if (len > PlayerHitBox.Radius && Vector3.Dot(to / len, fwd) < cos) continue;
            if (best is { } b && b.Distance <= edge) continue;
            best = new MeleeHit(t.Id, IsBackstab(attackerFeet, t.Feet, t.Yaw), edge);
        }
        return best;
    }

    /// <summary>Melee damage: backstab damage when behind the victim (and the weapon has one), else the normal damage.</summary>
    public static float MeleeDamage(WeaponDef w, bool backstab) => backstab && w.BackstabDamage > 0 ? w.BackstabDamage : w.Damage;
}
