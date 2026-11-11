using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The AC-130 on the server (killstreak "ac130"): the streak's adapter (the match's entities, the clock and the turret's
/// control) and the weapons' resolution table. Its rounds are resolved by <see cref="NetServer.ResolveGunshipShot"/> and their
/// blasts by <see cref="NetServer.GunshipBurst"/>; its kills are credited to <see cref="SpecialWeapons.Ac130"/>.
/// </summary>
public partial class NetServer
{
    /// <summary>weapons.json index per AC-130 weapon (same order as the section's weapons), resolved when the streak is built.</summary>
    int[] _ac130WeaponIndex = Array.Empty<int>();

    /// <summary>The streak's implementation, wired to this server. Built once per server by <see cref="BuildStreaks"/>.</summary>
    IKillstreak MakeAc130()
    {
        var ac = Killstreaks.Ac130 ?? throw new InvalidOperationException("killstreaks.json has no ac130 turret");
        var kinds = Killstreaks.AircraftKinds;
        var index = -1;
        for (var i = 0; i < kinds.Count; i++) if (kinds[i].Id == "gunship") index = i;
        if (index < 0) throw new InvalidOperationException("killstreaks.json: the ac130 airframe is the \"gunship\" aircraft kind, which is missing");
        Aircraft.Register(WorldEntityRegistry.Shared);
        _ac130WeaponIndex = ac.Weapons.Select(w =>
        {
            var i = Weapons.Weapons.FindIndex(x => x.Id == w.WeaponId);
            if (i < 0) throw new InvalidOperationException($"killstreaks.json: ac130 weapon \"{w.Id}\" names unknown weapon \"{w.WeaponId}\"");
            return i;
        }).ToArray();
        return new Ac130Streak(new Ac130World(this), kinds[index], index);
    }

    /// <summary>The streak's view of the server.</summary>
    sealed class Ac130World : Ac130Streak.IWorld
    {
        readonly NetServer _s;
        public Ac130World(NetServer server) => _s = server;

        public WorldEntityManager Entities => _s._match.Entities;
        public double Clock => _s._elapsed;
        public GunshipControl Turret(ushort owner, Aircraft aircraft) => _s.GunshipFor(owner, aircraft, SpecialWeapons.Ac130, ac130: true);
    }
}
