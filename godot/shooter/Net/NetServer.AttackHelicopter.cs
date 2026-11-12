using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The attack helicopter on the server (killstreak "attack_helicopter", ticket #126): the streak's adapter to the server. It
/// spawns the helicopter over the map's centre and gives the owner a cannon-only gunner control whose kills are credited to
/// <see cref="SpecialWeapons.AttackHelicopter"/>. The streak rules are in <see cref="AttackHelicopterStreakImpl"/>.
/// </summary>
public partial class NetServer
{
    /// <summary>The streak's implementation, wired to this server. Built once per server by <see cref="BuildStreaks"/>.</summary>
    IKillstreak MakeAttackHelicopter()
    {
        if (Killstreaks?.Gunship == null) throw new InvalidOperationException("killstreaks.json: attack_helicopter needs the gunship section");
        if (GunshipWeaponIndex(AttackHelicopterStreak.GunWeaponId) < 0)
            throw new InvalidOperationException($"killstreaks.json: the gunship has no weapon \"{AttackHelicopterStreak.GunWeaponId}\" for the attack helicopter");
        return new AttackHelicopterStreakImpl(new AttackHelicopterWorld(this));
    }

    /// <summary>The helicopter's view of the server: the map centre, the spawn, and the removal.</summary>
    sealed class AttackHelicopterWorld : IAttackHelicopterWorld
    {
        readonly NetServer _s;
        public AttackHelicopterWorld(NetServer server) => _s = server;

        /// <summary>The average of the spawn points (on the ground); the origin when the map has none.</summary>
        public NVec3 Centre
        {
            get
            {
                var sp = _s.Spawns;
                if (sp.Count == 0) return NVec3.Zero;
                var sum = NVec3.Zero;
                foreach (var p in sp) sum += p;
                return sum / sp.Count;
            }
        }

        public AttackHelicopterLaunch? Launch(ushort owner, NVec3 centre, float phaseRad)
        {
            var kinds = _s.Killstreaks.AircraftKinds;
            var index = kinds.ToList().FindIndex(k => k.Id == AttackHelicopterStreak.AircraftKind);
            if (index < 0) return null;
            var aircraft = new Aircraft(kinds[index], index, centre, phaseRad, clockwise: false, owner);
            if (_s._match.Entities.Spawn(aircraft) is not { } id) return null;
            var gunner = _s.GunshipFor(owner, aircraft, SpecialWeapons.AttackHelicopter,
                new[] { _s.GunshipWeaponIndex(AttackHelicopterStreak.GunWeaponId) });
            return new AttackHelicopterLaunch(id, input => gunner.OnInput(input, _s._elapsed));
        }

        public void Remove(ushort aircraftId) => _s._match.Entities.Despawn(aircraftId, WorldDespawnReason.Removed);
    }
}
