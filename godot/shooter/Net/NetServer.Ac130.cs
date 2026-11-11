using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The AC-130 on the server (killstreak "ac130"): the streak's adapter (the match's entities, the clock and the turret's
/// control), the weapons' resolution table, and the shell blast. A 105 mm shell bursts at its first hit with the splash from
/// its own killstreaks.json tuning; its kills are credited to <see cref="SpecialWeapons.Ac130"/>.
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

    /// <summary>Resolves a shell's burst: splash to every player in reach, the vehicles, and the event for clients.</summary>
    void Ac130Detonate(ushort owner, NVec3 at, GunshipWeaponDef def, ushort? direct, ServerCar? car)
    {
        var splash = def.Splash ?? throw new InvalidOperationException($"ac130 weapon \"{def.Id}\" has no splash");
        var w = new WeaponDef { Id = Ac130Streak.StreakId, Damage = def.Damage, Splash = splash };
        if (Vehicles != null) RocketVsVehicles(new Vector3(at.X, at.Y, at.Z), w, owner, SpecialWeapons.Ac130, car);
        BroadcastEvent(EventKind.Ac130Blast, Ac130Codec.EncodeBlast(owner, at, splash.Radius));
        foreach (var t in LiveTargets())
        {
            var isDirect = direct == t.Id;
            var dist = ProjectileMath.DistanceToCapsule(at, t.Feet, HitMath.Height(t.Stance), MoveConsts.CapsuleRadius);
            var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, t.Id == owner);
            if (dmg <= 0) continue;
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var res = Deal(owner, SpecialWeapons.Ac130, t.Id, dmg, false, false);
            if (res == DamageResult.Ignored) continue; // spawn protected: neither damage nor knockback
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v))
            {
                var center = t.Feet + new NVec3(0, HitMath.Height(t.Stance) / 2, 0);
                var k = ProjectileMath.Knockback(w, at, center, isDirect ? 1f : ProjectileMath.SplashFraction(w, dist));
                if (k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
            }
        }
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
