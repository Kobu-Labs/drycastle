using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>What a rocket's tick ended in: still flying, a player hit, the world hit, or its life ran out.</summary>
public enum RocketOutcome : byte { Flying, Player, World, Expired }

/// <summary>One tick of a rocket: the outcome, the distance it moved (or to the impact), and the victim of a player hit.</summary>
public readonly record struct RocketStep(RocketOutcome Outcome, float Distance, ushort Victim);

/// <summary>
/// The rocket sim's per-tick rule, pure so the tests run the server's own arithmetic. A rocket moves <c>speed * dt</c> a tick, sweeping
/// players (not its owner) and the world; it explodes on the first hit, or when its life (seconds) runs out.
/// A gunship's rocket has its own speed and a life that covers its whole range (<see cref="LifeFor"/>); the bazooka keeps weapons.json's.
/// </summary>
public static class RocketFlight
{
    /// <summary>Seconds added to a rocket's life on top of the flight time to its range, so it reaches the full range.</summary>
    public const float LifeMarginS = 0.5f;

    /// <summary>The life that lets a rocket fly its whole range at its speed (range / speed, plus <see cref="LifeMarginS"/>).</summary>
    public static float LifeFor(float rangeM, float speedMps) => rangeM / speedMps + LifeMarginS;

    /// <summary>
    /// One tick. <paramref name="wallDistance"/> is where the world blocks the ray from <paramref name="pos"/> along <paramref name="dir"/>
    /// (infinity if nothing); <paramref name="targets"/> are the live players (the owner is skipped by the sweep).
    /// </summary>
    public static RocketStep Step(Vector3 pos, Vector3 dir, float age, float speed, float life, float dt, float wallDistance, IEnumerable<HitTarget> targets, ushort owner)
    {
        var len = speed * dt;
        var sweep = ProjectileMath.Sweep(pos, dir, len, wallDistance, targets, owner);
        switch (sweep.Kind)
        {
            case ImpactKind.Player: return new RocketStep(RocketOutcome.Player, sweep.Distance, sweep.Victim);
            case ImpactKind.World: return new RocketStep(RocketOutcome.World, sweep.Distance, 0);
        }
        return age + dt >= life ? new RocketStep(RocketOutcome.Expired, len, 0) : new RocketStep(RocketOutcome.Flying, len, 0);
    }
}
