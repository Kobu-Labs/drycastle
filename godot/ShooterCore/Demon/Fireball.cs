using System.Numerics;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Demon;

/// <summary>One fireball in flight. <see cref="Id"/> is the demon's own counter (the world entity id is the host's business).</summary>
public readonly record struct Fireball(int Id, Vector3 Pos, Vector3 Vel, float Age);

public enum FireballOutcome : byte { Flying, Player, World, Expired }

/// <summary>One step of a fireball: the ball afterwards (when it still flies), what it ended in, where, and the victim of a direct hit.</summary>
public readonly record struct FireballStep(Fireball Ball, FireballOutcome Outcome, Vector3 Impact, ushort Victim);

/// <summary>
/// The fireball's flight, pure like <see cref="RocketFlight"/> and sweeping with the same <see cref="ProjectileMath.Sweep"/>: each step is the chord of
/// the arc, so a fast ball does not tunnel through a body or a wall. Nobody owns a fireball (<see cref="Owner"/> skips no player).
/// </summary>
public static class FireballFlight
{
    /// <summary>The "owner" the sweep skips: not a player id.</summary>
    public const ushort Owner = ushort.MaxValue;

    /// <summary>
    /// One step. <paramref name="wallDistance"/> answers where the world blocks a ray (origin, unit direction, length) or infinity: the church's walls
    /// and the floor, which the host raycasts. <paramref name="targets"/> are the living players inside.
    /// </summary>
    public static FireballStep Step(in Fireball b, FireballDef def, float dt, Func<Vector3, Vector3, float, float> wallDistance, IEnumerable<HitTarget> targets)
    {
        var g = new Vector3(0, -def.GravityMps2, 0);
        var move = b.Vel * dt + 0.5f * g * dt * dt;
        var len = move.Length();
        var age = b.Age + dt;
        if (len > 1e-6f)
        {
            var dir = move / len;
            var sweep = ProjectileMath.Sweep(b.Pos, dir, len, wallDistance(b.Pos, dir, len), targets, Owner);
            if (sweep.Kind != ImpactKind.None)
            {
                // a wall hit is pulled back a hair so the blast centre is in front of the surface, as a rocket's
                var d = sweep.Kind == ImpactKind.World ? MathF.Max(0, sweep.Distance - 0.02f) : sweep.Distance;
                return new FireballStep(b, sweep.Kind == ImpactKind.Player ? FireballOutcome.Player : FireballOutcome.World, b.Pos + dir * d, sweep.Victim);
            }
        }
        var next = new Fireball(b.Id, b.Pos + move, b.Vel + g * dt, age);
        return age >= def.LifeS ? new FireballStep(next, FireballOutcome.Expired, next.Pos, 0) : new FireballStep(next, FireballOutcome.Flying, next.Pos, 0);
    }
}

/// <summary>A player a blast reaches: the damage (dealt through the normal damage step), the knockback impulse, and whether it set them ablaze.</summary>
public readonly record struct FireballVictim(ushort Id, float Damage, Vector3 Knockback, bool Ignited);

/// <summary>
/// A fireball's impact: the direct victim takes the full damage, the others the splash by distance (<see cref="ProjectileMath.SplashDamage"/>).
/// Inside the church the blast follows the bunker shelter rule (ADR 0009 section 5): pass the church's rooms as <see cref="Shelters"/> and a blast hurts a
/// player only when both are in the same room (or both in the open); the direct hit does not ask.
/// </summary>
public static class FireballBlast
{
    /// <summary>The fireball as a weapon: only its damage and splash, which is all <see cref="ProjectileMath"/> reads.</summary>
    public static WeaponDef AsWeapon(FireballDef def) => new()
    {
        Id = "demon_fireball",
        Damage = def.Damage,
        Splash = new SplashDef
        {
            Radius = def.Splash.RadiusM, FullDamageRadius = def.Splash.FullDamageRadiusM, EdgeDamage = def.Splash.EdgeDamage,
            SelfDamageMultiplier = 1, KnockbackSpeed = def.Splash.KnockbackSpeed,
        },
    };

    /// <summary>
    /// Who a blast at <paramref name="impact"/> reaches. With <paramref name="burns"/> and <paramref name="burn"/> and <see cref="FireballDef.Ignites"/>
    /// every victim is set ablaze (credited to nobody, <see cref="BurnSet.NoSource"/>).
    /// </summary>
    public static List<FireballVictim> Detonate(Vector3 impact, ushort? direct, IEnumerable<HitTarget> targets, FireballDef def, Shelters? shelters = null,
        BurnSet? burns = null, BurnDef? burn = null)
    {
        var w = AsWeapon(def);
        var list = new List<FireballVictim>();
        foreach (var t in targets)
        {
            var isDirect = direct == t.Id;
            if (!isDirect && shelters != null && !shelters.Reaches(impact, HitMath.Centre(t))) continue;
            var dist = HitMath.DistanceToBody(impact, t);
            var dmg = isDirect ? def.Damage : ProjectileMath.SplashDamage(w, dist, false);
            if (dmg <= 0) continue;
            var k = ProjectileMath.Knockback(w, impact, HitMath.Centre(t), isDirect ? 1f : ProjectileMath.SplashFraction(w, dist));
            var ignite = def.Ignites && burns != null && burn != null;
            if (ignite) burns!.Ignite(t.Id, BurnSet.NoSource, burn!);
            list.Add(new FireballVictim(t.Id, dmg, k, ignite));
        }
        return list;
    }
}
