using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>One fireball in flight. <see cref="Id"/> is the demon's own counter (the world entity id is the host's business).</summary>
public readonly record struct Fireball(int Id, Vector3 Pos, Vector3 Vel, float Age);

/// <summary>Ballistic solutions: the launch velocity of an arc under gravity.</summary>
public static class Ballistics
{
    /// <summary>
    /// The velocity of speed <paramref name="speed"/> that carries a body from <paramref name="from"/> to <paramref name="to"/> under gravity
    /// <paramref name="g"/> (m/s², pulling down y). The flat (low) arc, or the lob with <paramref name="lob"/>. False when the target is out of reach at that speed.
    /// <paramref name="flightTime"/> is the time of flight.
    /// </summary>
    public static bool Solve(Vector3 from, Vector3 to, float speed, float g, bool lob, out Vector3 vel, out float flightTime)
    {
        vel = default; flightTime = 0;
        var dx = to.X - from.X; var dz = to.Z - from.Z; var dy = to.Y - from.Y;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        var v2 = speed * speed;
        if (d < 1e-3f)
        {
            // straight above or below
            if (dy <= 0) { vel = new Vector3(0, -speed, 0); flightTime = (-speed + MathF.Sqrt(v2 - 2 * g * dy)) / g; return true; }
            if (v2 < 2 * g * dy) return false;
            vel = new Vector3(0, speed, 0); flightTime = (speed - MathF.Sqrt(v2 - 2 * g * dy)) / g; return true;
        }
        var disc = v2 * v2 - g * (g * d * d + 2 * dy * v2);
        if (disc < 0) return false;
        var root = MathF.Sqrt(disc);
        var tan = (v2 + (lob ? root : -root)) / (g * d);
        var cos = 1f / MathF.Sqrt(1 + tan * tan);
        var sin = tan * cos;
        vel = new Vector3(dx / d * speed * cos, speed * sin, dz / d * speed * cos);
        flightTime = d / (speed * cos);
        return true;
    }

    /// <summary>
    /// The launch velocity at a moving target: solves for where the target will be at the arc's flight time, <paramref name="lead"/> (0..1) of the way
    /// (twice, which settles for the speeds in play). False when it is out of reach.
    /// </summary>
    public static bool Aim(Vector3 from, Vector3 target, Vector3 targetVel, float speed, float g, float lead, out Vector3 vel)
    {
        var aim = target;
        vel = default;
        for (var i = 0; i < 3; i++)
        {
            if (!Solve(from, aim, speed, g, false, out vel, out var t)) return false;
            aim = target + targetVel * (t * lead);
        }
        return Solve(from, aim, speed, g, false, out vel, out _);
    }
}

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
