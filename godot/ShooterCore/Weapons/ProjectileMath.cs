using System.Numerics;

namespace Suchohrad.ShooterCore;

public enum ImpactKind { None, World, Player }

/// <summary>Result of sweeping a projectile segment: what it hit first and how far along the segment.</summary>
public readonly record struct SweepResult(ImpactKind Kind, float Distance, ushort Victim);

/// <summary>
/// Godot-free projectile math for the bazooka: swept segment test against the world distance and player capsules,
/// splash damage falloff, distance to a player capsule and knockback impulse.
/// Splash ignores walls in v1 (no occlusion test), so nothing here looks at geometry besides the swept segment.
/// </summary>
public static class ProjectileMath
{
    /// <summary>
    /// Sweep the segment from <paramref name="from"/> along unit <paramref name="dir"/> for <paramref name="length"/>.
    /// <paramref name="wallDistance"/> is where the world blocks it (infinity if nothing). The owner is never hit directly.
    /// A player at exactly the same distance as the wall counts as hit first. <paramref name="exposedGunner"/> is the exposed gunner of the vehicle the segment
    /// stopped on, and <paramref name="wallPastHull"/> where the world stops it with that hull left out (<see cref="GunnerCover"/>).
    /// </summary>
    public static SweepResult Sweep(Vector3 from, Vector3 dir, float length, float wallDistance, IEnumerable<HitTarget> targets, ushort owner,
        HitTarget? exposedGunner = null, float wallPastHull = float.PositiveInfinity)
    {
        var hit = GunnerCover.Raycast(from, dir, length, wallDistance, targets, owner, exposedGunner, wallPastHull);
        if (hit is { } h) return new SweepResult(ImpactKind.Player, h.Distance, h.Id);
        if (wallDistance <= length) return new SweepResult(ImpactKind.World, wallDistance, 0);
        return new SweepResult(ImpactKind.None, length, 0);
    }

    /// <summary>Shortest distance from a point to the surface of an upright player capsule (0 inside it).</summary>
    public static float DistanceToCapsule(Vector3 p, Vector3 feet, float height, float radius)
    {
        var y0 = feet.Y + radius; var y1 = feet.Y + height - radius;
        var cy = Math.Clamp(p.Y, y0, MathF.Max(y0, y1));
        var d = Vector3.Distance(p, new Vector3(feet.X, cy, feet.Z));
        return MathF.Max(0, d - radius);
    }

    /// <summary>
    /// Splash damage at surface distance <paramref name="distance"/>: full inside FullDamageRadius, linear down to EdgeDamage
    /// at Radius, none beyond. Self damage is scaled by SelfDamageMultiplier. Rounded, at least 1 inside the radius.
    /// </summary>
    public static float SplashDamage(WeaponDef w, float distance, bool self)
    {
        var s = w.Splash;
        if (s == null || distance >= s.Radius) return 0;
        float dmg;
        if (distance <= s.FullDamageRadius) dmg = w.Damage;
        else
        {
            var t = (distance - s.FullDamageRadius) / MathF.Max(1e-4f, s.Radius - s.FullDamageRadius);
            dmg = w.Damage + (s.EdgeDamage - w.Damage) * t;
        }
        if (self) dmg *= s.SelfDamageMultiplier;
        return MathF.Max(1f, MathF.Round(dmg));
    }

    /// <summary>
    /// The weapon as one launch explodes: the same weapon with its blast replaced by <paramref name="blast"/> (a gunship's rocket
    /// has its own 8 m blast; the bazooka keeps weapons.json's). Null keeps the weapon's own blast.
    /// </summary>
    public static WeaponDef WithBlast(WeaponDef w, SplashDef? blast) => blast == null ? w : new WeaponDef { Id = w.Id, Kind = w.Kind, Damage = w.Damage, Splash = blast };

    /// <summary>Splash strength 0..1 (before the self multiplier), used to scale knockback.</summary>
    public static float SplashFraction(WeaponDef w, float distance) =>
        w.Splash == null || w.Damage <= 0 ? 0 : SplashDamage(w, distance, false) / w.Damage;

    /// <summary>
    /// Knockback velocity added to a player: away from the explosion towards the player's body centre with a slight upward
    /// bias, <c>KnockbackSpeed * fraction</c> m/s. A blast right under the feet therefore lifts the player almost straight up.
    /// </summary>
    public static Vector3 Knockback(WeaponDef w, Vector3 explosion, Vector3 bodyCenter, float fraction) => Knockback(w.Splash?.KnockbackSpeed ?? 0, explosion, bodyCenter, fraction);

    public static Vector3 Knockback(float speed, Vector3 explosion, Vector3 bodyCenter, float fraction)
    {
        if (speed <= 0 || fraction <= 0) return Vector3.Zero;
        var d = bodyCenter - explosion;
        if (d.LengthSquared() < 1e-6f) d = Vector3.UnitY;
        d = Vector3.Normalize(d) + Vector3.UnitY * 0.3f;
        return Vector3.Normalize(d) * speed * fraction;
    }
}
