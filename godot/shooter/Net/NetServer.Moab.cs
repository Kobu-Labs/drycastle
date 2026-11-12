using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The MOAB on the server (ticket #129). <see cref="MoabStreak"/> decides when it goes off; this file applies it: a
/// reliable <see cref="EventKind.Moab"/> to everyone at each phase, and on detonation every living player but the owner
/// is killed through the match (so the owner gets the kill, its XP and the streak award) and every standing vehicle is
/// destroyed through the normal damage path (so the chain explosion runs).
/// </summary>
public partial class NetServer
{
    /// <summary>The blast's kills and vehicle hits are credited to the owner with this weapon byte (a streak kill).</summary>
    const byte MoabWeapon = SpecialWeapons.Moab;

    void OnMoabPhase(MoabPhase phase, KillstreakUse use)
    {
        var owner = use.Owner;
        if (phase == MoabPhase.Armed)
        {
            BroadcastEvent(EventKind.Moab, MoabEvents.Encode(new MoabInfo(phase, owner, OwnerPosition(owner, use), 0, (float)use.Def.DurationSeconds)));
            GD.Print($"server: MOAB armed by #{owner}");
            return;
        }
        if (phase == MoabPhase.Cancelled)
        {
            BroadcastEvent(EventKind.Moab, MoabEvents.Encode(new MoabInfo(phase, owner, use.Position, 0, (float)_elapsed)));
            GD.Print($"server: MOAB of #{owner} cancelled");
            return;
        }

        var at = OwnerPosition(owner, use);
        var seed = (uint)_rng.Next();
        BroadcastEvent(EventKind.Moab, MoabEvents.Encode(new MoabInfo(phase, owner, at, seed, (float)_elapsed)));
        BroadcastImpact(at, ImpactSize.Plume); // the blast's plume on the ground (ticket #154)

        var victims = MoabStreak.Victims(LiveTargets().Select(t => t.Id), owner).ToList();
        foreach (var id in victims) _match.ApplyDamage(id, owner, MoabWeapon, 9999, ignoreProtection: true);
        foreach (var car in _cars.Where(c => !c.Destroyed).ToList()) DamageVehicle(car, car.Hp + 1, owner, MoabWeapon, 0);
        GD.Print($"server: MOAB by #{owner} detonated: {victims.Count} killed, vehicles destroyed");
    }

    /// <summary>Where the blast is centred: the owner's position now, else where they called it in.</summary>
    System.Numerics.Vector3 OwnerPosition(ushort owner, KillstreakUse use)
    {
        if (_byId.TryGetValue(owner, out var c))
        {
            var p = c.Body.GlobalPosition;
            return new System.Numerics.Vector3(p.X, p.Y, p.Z);
        }
        return use.Position;
    }
}
