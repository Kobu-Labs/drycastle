using Godot;
using Drycastle.ShooterCore.CarePackages;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Killstreaks.Uav;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Turrets;

namespace Drycastle.Shooter;

/// <summary>
/// Killstreaks on the server (ticket #118): the streak counter and loadout streaks per life, activation on the Killstreak
/// key's press edge, and the owner-only streak events. The rules live in <see cref="KillstreakService"/>; this file only
/// feeds it the server's events. Each streak's real behaviour comes with its own ticket (see <see cref="IKillstreak"/>).
/// </summary>
public partial class NetServer
{
    /// <summary>killstreaks.json: the streak table. Set by the host before the server starts.</summary>
    public KillstreakFile Killstreaks = null!;

    KillstreakService _streaks = null!;

    /// <summary>Builds the service once the table and the match are set. Streaks without a ticket yet keep their logging stub.</summary>
    void BuildStreaks()
    {
        _uav = BuildUav();
        _streaks = new KillstreakService(
            Killstreaks,
            Killstreaks.Streaks.Select(s =>
                s.Id == MoabStreak.StreakId ? (IKillstreak)new MoabStreak(OnMoabPhase) :
                s.Id == CarePackageRules.StreakId ? CarePackageImpl() :
                s.Id == AttackMissileStreak.StreakId ? MakeMissileStreak() :
                s.Id == TurretStreak.StreakId ? new TurretStreak(Turret.Def, _match.Entities, new TurretWorld(this)) :
                s.Id == UavStreak.StreakId ? _uav :
                s.Id == AttackHelicopterStreak.StreakId ? MakeAttackHelicopter() :
                s.Id == Ac130Streak.StreakId ? MakeAc130() :
                s.Id == HelicopterHelperStreak.StreakId ? MakeHelicopterHelper() :
                new LoggingKillstreak(s.Id)),
            (owner, kind, payload) => { if (_byId.TryGetValue(owner, out var c)) Send(c, new EventMsg((byte)kind, payload)); },
            s => GD.Print($"server: {s}"));
    }

    /// <summary>A life starts at spawn (join and every spawn, like the perk): the loadout's streaks and the perk's cost reduction.</summary>
    void StreakArm(Client c, Loadout l) => _streaks.Arm(c.Id, new[] { l.Streak1, l.Streak2, l.Streak3 }, c.Perks.StreakCostReduction);

    /// <summary>The Killstreak key was pressed (its press edge): use the next held streak while the owner is alive.</summary>
    void StreakActivate(Client c)
    {
        if (SandboxStreakKey(c)) return; // a sandbox player's held streak goes first (#156)
        var p = c.Body.GlobalPosition;
        _streaks.Activate(c.Id, _elapsed, new System.Numerics.Vector3(p.X, p.Y, p.Z));
    }

    /// <summary>
    /// A kill for the Streak counter. Returns whether a streak's own weapon made it (the XP streak award). A streak's own
    /// weapon never advances the counter; the kill-feed id list (ticket #143) also covers the aircraft and the vehicle gun.
    /// </summary>
    bool StreakKill(PlayerKilled k)
    {
        var fromStreak = _streaks.IsStreakWeapon(WeaponKey(k.Weapon));
        _streaks.OnKill(k.Killer, k.Victim, fromStreak || StreakWeapons.Is(k.Weapon));
        return fromStreak;
    }

    void StreakDeath(ushort victim)
    {
        _streaks.OnDeath(victim);
        _match.Entities.DespawnOwned(victim, TurretRules.TypeId, WorldDespawnReason.Removed); // a turret does not outlive its owner
    }

    /// <summary>
    /// An admin moved a player to the other team (#312): they climb out of any vehicle, and their turrets and killstreak progress are dropped like at a death
    /// (a turret would else turn on its owner's new teammates). The respawn at the new side's spawn follows from the match.
    /// </summary>
    void OnTeamChanged(PlayerTeamChanged tc)
    {
        if (_byId.TryGetValue(tc.Id, out var c) && c.Car != null) LeaveSeat(c, false);
        _streaks.Wipe(tc.Id);
        _match.Entities.DespawnOwned(tc.Id, TurretRules.TypeId, WorldDespawnReason.Removed);
        if (tc.To == Teams.None) BroadcastEvent(EventKind.Death, MatchCodec.Encode(new DeathMsg(tc.Id, tc.Id, DeathMsg.WaitsForAdmin))); // out of the round: down until an admin puts them on a team, no corpse, no death on the record
        GD.Print($"server: #{tc.Id} moved from team {Teams.Tag(tc.From)} to {Teams.Tag(tc.To)}");
    }

    void StreakRoundReset() => _streaks.Reset();

    void StreakTick(float dt)
    {
        _streaks.Tick(_elapsed);
        _uav.Tick(_elapsed);
        _helper?.Tick(dt);
    }

    void StreakLeave(ushort id)
    {
        _streaks.Forget(id);
        _match.Entities.DespawnOwned(id, TurretRules.TypeId, WorldDespawnReason.Removed);
    }
}
