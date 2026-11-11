using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Aircraft on the server (ticket #125): the aircraft world entity, and the gunship turret of its owner. The streak tickets
/// call <see cref="SpawnAircraft"/> from their <see cref="IKillstreak.Activate"/> and wrap the result with
/// <see cref="GunshipFor"/>. Cannon rounds are resolved here like a hitscan rifle (walls, players, vehicles); rockets go into
/// the player rocket sim, so their splash and direct hits are the same code.
/// </summary>
public partial class NetServer
{
    /// <summary>weapons.json index per gunship weapon (same order as the gunship's weapons), resolved once at start.</summary>
    int[] _gunshipWeaponIndex = Array.Empty<int>();

    /// <summary>Registers the aircraft type and resolves the gunship's weapons; called from <see cref="_Ready"/>.</summary>
    void InitAircraft()
    {
        Aircraft.Register(WorldEntityRegistry.Shared);
        var gun = Killstreaks?.Gunship;
        if (gun == null || Weapons == null) return;
        _gunshipWeaponIndex = gun.Weapons.Select(w =>
        {
            var i = Weapons.Weapons.FindIndex(x => x.Id == w.WeaponId);
            if (i < 0) throw new InvalidOperationException($"killstreaks.json: gunship weapon \"{w.Id}\" names unknown weapon \"{w.WeaponId}\"");
            return i;
        }).ToArray();
    }

    /// <summary>
    /// Spawns an aircraft of a kind from killstreaks.json circling <paramref name="centre"/> (its Y is the ground there).
    /// Returns its world entity id, or null when the kind is unknown or the entity cap is reached.
    /// </summary>
    public ushort? SpawnAircraft(string kindId, NVec3 centre, float phaseRad, bool clockwise, ushort owner, float? lifetimeS = null)
    {
        var kinds = Killstreaks?.AircraftKinds ?? Array.Empty<AircraftKindDef>();
        var index = -1;
        for (var i = 0; i < kinds.Count; i++) if (kinds[i].Id == kindId) index = i;
        if (index < 0) { GD.PrintErr($"server: aircraft kind \"{kindId}\" is not in killstreaks.json"); return null; }
        return _match.Entities.Spawn(new Aircraft(kinds[index], index, centre, phaseRad, clockwise, owner, lifetimeS));
    }

    /// <summary>
    /// The gunship control of an aircraft's owner: its shots are resolved on the server and its status goes to the owner.
    /// Feed the owner's inputs to it with <c>KillstreakUse.TakeControl(entityId, i => control.OnInput(i, _elapsed), freezesBody: true)</c>:
    /// the gunship's owner stands still while steering it.
    /// </summary>
    public GunshipControl GunshipFor(ushort owner, Aircraft aircraft, bool ac130 = false)
    {
        var def = (ac130 ? Killstreaks?.Ac130 : Killstreaks?.Gunship) ?? throw new InvalidOperationException($"killstreaks.json has no {(ac130 ? "ac130" : "gunship")}");
        var weapons = ac130 ? _ac130WeaponIndex : _gunshipWeaponIndex;
        return new GunshipControl(def, aircraft,
            shot => ResolveGunshipShot(owner, shot, def, weapons, ac130 ? SpecialWeapons.Ac130 : null),
            status => SendGunshipStatus(owner, status));
    }

    void SendGunshipStatus(ushort owner, GunshipStatus s)
    {
        if (_byId.TryGetValue(owner, out var c)) Send(c, new EventMsg((byte)EventKind.GunshipStatus, GunshipCodec.Encode(s)));
    }

    /// <summary>
    /// One round from a turret: a cannon round is a hitscan from the turret; a rocket joins the rocket sim; a shell is a trace
    /// that bursts at its first hit (<see cref="Ac130Detonate"/>). <paramref name="credit"/> is the kill-feed weapon when the
    /// turret credits its kills to a special weapon (the AC-130); null credits the weapons.json weapon of the round.
    /// </summary>
    void ResolveGunshipShot(ushort owner, GunshipShot shot, GunshipDef gun, int[] weaponIndex, byte? credit)
    {
        var def = gun.Weapons[shot.WeaponIndex];
        var weapon = weaponIndex[shot.WeaponIndex];
        var w = Weapons.Weapons[weapon];
        var by = credit ?? (byte)weapon;
        if (def.Kind == GunshipWeaponKind.Rocket)
        {
            var rocket = new Rocket
            {
                Id = _nextRocket, Owner = owner, Weapon = (byte)weapon,
                Pos = new Vector3(shot.Origin.X, shot.Origin.Y, shot.Origin.Z),
                Dir = new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z),
            };
            _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
            _rockets.Add(rocket);
            _rocketsFired++;
            BroadcastEvent(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, owner, rocket.Weapon, shot.Origin, shot.Direction * w.ProjectileSpeed)));
            return;
        }

        var from = new Vector3(shot.Origin.X, shot.Origin.Y, shot.Origin.Z);
        var dir = new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z);
        var range = def.RangeM;
        var wall = WorldHit(from, dir, range, out var shotCar);
        var hit = HitMath.Raycast(shot.Origin, shot.Direction, range, wall, LiveTargets(), owner);
        var end = shot.Origin + shot.Direction * (hit?.Distance ?? Math.Min(wall, range));
        BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(owner, (byte)weapon, shot.Origin, end)));
        if (def.Kind == GunshipWeaponKind.Shell)
        {
            Ac130Detonate(owner, end, def, hit is { } sh ? sh.Id : null, shotCar as ServerCar);
            return;
        }
        if (hit is { } h) Deal(owner, by, h.Id, def.Damage, false, false);
        else if (shotCar != null && Vehicles != null && wall <= range)
            DamageVehicle(shotCar, def.Damage * Vehicles.BulletDamageMultiplier, owner, by, 0);
    }
}
