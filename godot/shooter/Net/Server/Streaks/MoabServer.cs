using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// The MOAB on the server (ticket #129). <see cref="MoabStreak"/> decides when it goes off; this class applies it: a
/// reliable <see cref="EventKind.Moab"/> to everyone at each phase, and on detonation every living player but the owner
/// is killed through the match (so the owner gets the kill, its XP and the streak award) and every standing vehicle is
/// destroyed through the normal damage path (so the chain explosion runs).
/// </summary>
internal sealed class MoabServer
{
    /// <summary>The blast's kills and vehicle hits are credited to the owner with this weapon byte (a streak kill).</summary>
    const byte MoabWeapon = SpecialWeapons.Moab;

    readonly ServerContext _ctx;
    readonly TargetBook _targets;
    readonly HuntLogServer _hunts;
    readonly Fleet _fleet;
    readonly Action<ServerCar, float, ushort, byte, float> _damageVehicle;
    readonly Action<ushort, byte> _hitBigfoot;
    readonly ImpactFx _impacts;
    readonly Random _rng;

    /// <param name="hitBigfoot">A hit on the Bigfoot by an owner with a weapon (it takes the blast as an explosive hit, not as a kill: issue #264).</param>
    public MoabServer(ServerContext ctx, TargetBook targets, HuntLogServer hunts, Fleet fleet, Action<ServerCar, float, ushort, byte, float> damageVehicle, Action<ushort, byte> hitBigfoot, ImpactFx impacts, Random rng)
    {
        _ctx = ctx; _targets = targets; _hunts = hunts; _fleet = fleet; _damageVehicle = damageVehicle; _hitBigfoot = hitBigfoot; _impacts = impacts; _rng = rng;
    }

    public IKillstreak Streak() => new MoabStreak(OnPhase);

    void OnPhase(MoabPhase phase, KillstreakUse use)
    {
        var owner = use.Owner;
        if (phase == MoabPhase.Armed)
        {
            _ctx.Broadcast(EventKind.Moab, MoabEvents.Encode(new MoabInfo(phase, owner, OwnerPosition(owner, use), 0, (float)use.Def.DurationSeconds)));
            GD.Print($"server: MOAB armed by #{owner}");
            return;
        }
        if (phase == MoabPhase.Cancelled)
        {
            _ctx.Broadcast(EventKind.Moab, MoabEvents.Encode(new MoabInfo(phase, owner, use.Position, 0, (float)_ctx.Now)));
            GD.Print($"server: MOAB of #{owner} cancelled");
            return;
        }

        var at = OwnerPosition(owner, use);
        var seed = (uint)_rng.Next();
        _ctx.Broadcast(EventKind.Moab, MoabEvents.Encode(new MoabInfo(phase, owner, at, seed, (float)_ctx.Now)));
        _impacts.Broadcast(at, ImpactSize.Plume); // the blast's plume on the ground (ticket #154)

        var live = _targets.Live();
        var victims = MoabStreak.Victims(live.Select(t => t.Id), owner).ToList();
        foreach (var id in victims)
        {
            if (id == BigfootRules.TargetId) _hitBigfoot(owner, MoabWeapon); // the Bigfoot takes it as an explosive hit, not as a kill (issue #264)
            else
            {
                _hunts.Pending = new WildHit(MoabWeapon, KillCause.Standard, null, null); // a wild animal in the blast is a hunt with the MOAB
                _ctx.Match.ApplyDamage(id, owner, MoabWeapon, 9999, ignoreProtection: true);
                _hunts.Pending = null;
            }
        }
        foreach (var car in _fleet.Cars.Where(c => !c.Destroyed).ToList()) _damageVehicle(car, car.Hp + 1, owner, MoabWeapon, 0);
        GD.Print($"server: MOAB by #{owner} detonated: {victims.Count} killed, vehicles destroyed");
    }

    /// <summary>Where the blast is centred: the owner's position now, else where they called it in.</summary>
    NVec3 OwnerPosition(ushort owner, KillstreakUse use) => _ctx.Players.TryGetValue(owner, out var c) ? c.Body.GlobalPosition.ToNumerics() : use.Position;
}
