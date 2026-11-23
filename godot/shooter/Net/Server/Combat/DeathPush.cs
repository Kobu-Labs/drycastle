using Drycastle.ShooterCore.Match;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// The throw a killing blow gives its victim (a blast's knockback, a car's run-over): recorded before the damage, kept until the victim's corpse or ragdoll is made, dropped when the hit did not kill.
/// Also what threw each victim, sent with the Death event so the victim's own view can follow the flight (#416).
/// </summary>
internal sealed class DeathPush
{
    readonly Dictionary<ushort, NVec3> _push = new();
    readonly Dictionary<ushort, DeathFlight> _flight = new();

    /// <summary>Records the throw for a victim about to take damage; <see cref="Clear"/> drops it when the hit did not kill.</summary>
    public void Record(ushort victim, NVec3 velocity, DeathFlight flight = DeathFlight.Blast)
    {
        _push[victim] = velocity;
        if (flight == DeathFlight.None) _flight.Remove(victim); else _flight[victim] = flight;
    }

    public void Clear(ushort victim, DamageResult result)
    {
        if (result == DamageResult.Killed) return;
        _push.Remove(victim);
        _flight.Remove(victim);
    }

    public bool Has(ushort victim) => _push.ContainsKey(victim);

    /// <summary>The throw recorded for the victim, taken (true when there was one).</summary>
    public bool Take(ushort victim, out NVec3 push) => _push.Remove(victim, out push);

    /// <summary>The victim is not thrown after all (a kill that leaves no ragdoll).</summary>
    public void Forget(ushort victim) => _push.Remove(victim);

    /// <summary>What threw the victim, taken for the Death event.</summary>
    public DeathFlight TakeFlight(ushort victim) => _flight.Remove(victim, out var f) ? f : DeathFlight.None;
}
