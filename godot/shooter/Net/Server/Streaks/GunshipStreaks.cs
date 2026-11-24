using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// The AC-130 on the server (killstreak "ac130"): the streak's adapter (the match's entities, the clock and the turret's control) and the weapons' resolution table. Its rounds are
/// resolved by <see cref="GunshipServer"/>; its kills are credited to <see cref="SpecialWeapons.Ac130"/>.
/// </summary>
internal sealed class Ac130Server : Ac130Streak.IWorld
{
    readonly ServerContext _ctx;
    readonly KillstreakFile _killstreaks;
    readonly GunshipServer _gunships;

    public Ac130Server(ServerContext ctx, KillstreakFile killstreaks, GunshipServer gunships) { _ctx = ctx; _killstreaks = killstreaks; _gunships = gunships; }

    /// <summary>The streak's implementation, wired to this server.</summary>
    public IKillstreak Streak()
    {
        var ac = _killstreaks.Ac130 ?? throw new InvalidOperationException("killstreaks.json has no ac130 turret");
        var kinds = _killstreaks.AircraftKinds;
        var index = -1;
        for (var i = 0; i < kinds.Count; i++) if (kinds[i].Id == "gunship") index = i;
        if (index < 0) throw new InvalidOperationException("killstreaks.json: the ac130 airframe is the \"gunship\" aircraft kind, which is missing");
        Aircraft.Register(WorldEntityRegistry.Shared);
        _gunships.ResolveAc130Weapons(ac);
        return new Ac130Streak(this, kinds[index], index);
    }

    public WorldEntityManager Entities => _ctx.Match.Entities;
    public double Clock => _ctx.Now;
    public GunshipControl Turret(ushort owner, Aircraft aircraft) => _gunships.ControlFor(owner, aircraft, SpecialWeapons.Ac130, ac130: true);
}

/// <summary>
/// The attack helicopter on the server (killstreak "attack_helicopter", ticket #126): the streak's adapter to the server. It spawns the helicopter over the map's centre and gives the owner a
/// cannon-only gunner control whose kills are credited to <see cref="SpecialWeapons.AttackHelicopter"/>. The streak rules are in <see cref="AttackHelicopterStreakImpl"/>.
/// </summary>
internal sealed class AttackHelicopterServer : IAttackHelicopterWorld
{
    readonly ServerContext _ctx;
    readonly KillstreakFile _killstreaks;
    readonly GunshipServer _gunships;

    public AttackHelicopterServer(ServerContext ctx, KillstreakFile killstreaks, GunshipServer gunships) { _ctx = ctx; _killstreaks = killstreaks; _gunships = gunships; }

    /// <summary>The streak's implementation, wired to this server.</summary>
    public IKillstreak Streak()
    {
        if (_killstreaks?.Gunship == null) throw new InvalidOperationException("killstreaks.json: attack_helicopter needs the gunship section");
        if (_gunships.WeaponIndex(AttackHelicopterStreak.GunWeaponId) < 0)
            throw new InvalidOperationException($"killstreaks.json: the gunship has no weapon \"{AttackHelicopterStreak.GunWeaponId}\" for the attack helicopter");
        return new AttackHelicopterStreakImpl(this);
    }

    public AttackHelicopterLaunch? Launch(ushort owner, NVec3 centre, float phaseRad)
    {
        var kinds = _killstreaks.AircraftKinds;
        var index = kinds.ToList().FindIndex(k => k.Id == AttackHelicopterStreak.AircraftKind);
        if (index < 0) return null;
        var aircraft = new Aircraft(kinds[index], index, centre, phaseRad, clockwise: false, owner);
        if (_ctx.Match.Entities.Spawn(aircraft) is not { } id) return null;
        var gunner = _gunships.ControlFor(owner, aircraft, SpecialWeapons.AttackHelicopter, new[] { _gunships.WeaponIndex(AttackHelicopterStreak.GunWeaponId) });
        return new AttackHelicopterLaunch(id, input => gunner.OnInput(input, _ctx.Now));
    }

    public void Remove(ushort aircraftId) => _ctx.Match.Entities.Despawn(aircraftId, WorldDespawnReason.Removed);
}
