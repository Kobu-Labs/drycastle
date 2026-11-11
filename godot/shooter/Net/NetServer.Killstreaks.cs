using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

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

    /// <summary>Builds the service once the table is set. Each streak runs its logging stub until its own ticket replaces it.</summary>
    void BuildStreaks()
    {
        _uav = BuildUav();
        _streaks = new KillstreakService(
            Killstreaks,
            Killstreaks.Streaks.Select(s => s.Id switch { MoabStreak.StreakId => (IKillstreak)new MoabStreak(OnMoabPhase), UavStreak.StreakId => _uav, _ => new LoggingKillstreak(s.Id) }),
            (owner, kind, payload) => { if (_byId.TryGetValue(owner, out var c)) Send(c, new EventMsg((byte)kind, payload)); },
            s => GD.Print($"server: {s}"));
    }

    /// <summary>A life starts at spawn (join and every spawn, like the perk): the loadout's streaks and the perk's cost reduction.</summary>
    void StreakArm(Client c, Loadout l) => _streaks.Arm(c.Id, new[] { l.Streak1, l.Streak2, l.Streak3 }, c.Perks.StreakCostReduction);

    /// <summary>The Killstreak key was pressed (its press edge): use the next held streak while the owner is alive.</summary>
    void StreakActivate(Client c)
    {
        var p = c.Body.GlobalPosition;
        _streaks.Activate(c.Id, _elapsed, new System.Numerics.Vector3(p.X, p.Y, p.Z));
    }

    /// <summary>A kill for the Streak counter. Returns whether a streak's own weapon made it (the XP streak award).</summary>
    bool StreakKill(PlayerKilled k)
    {
        var fromStreak = _streaks.IsStreakWeapon(WeaponKey(k.Weapon));
        _streaks.OnKill(k.Killer, k.Victim, fromStreak);
        return fromStreak;
    }

    void StreakDeath(ushort victim) => _streaks.OnDeath(victim);

    void StreakRoundReset() => _streaks.Reset();

    void StreakTick() { _streaks.Tick(_elapsed); _uav.Tick(_elapsed); }

    void StreakLeave(ushort id) => _streaks.Forget(id);
}
