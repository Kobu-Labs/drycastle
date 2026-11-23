using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The gunner streaks' HUD state on the client: the AC-130 (its turret def, its weapon selector and its time left, counted from the streak's duration in killstreaks.json) and the attack helicopter (#126: the time left and
/// the owner's kills during the streak). The server's owner-only events start and end them (StreakUsed, StreakEnded); the time is counted from the use on this client, so it can lag the server by one round trip.
/// Only the gunner overlay reads it.
/// </summary>
public sealed class GunnerStreaks
{
    readonly ClientContext _ctx;
    double _ac130Since;
    bool _heliActive;
    double _heliUsedAt;
    int _heliKills;

    public GunnerStreaks(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The hints of the gunner overlay change (no next-weapon key under the attack helicopter); it presents them again.</summary>
    public event Action? HintsChanged;

    /// <summary>True from StreakUsed(ac130) to its StreakEnded: the owner's turret is the AC-130's.</summary>
    public bool Ac130Active { get; private set; }

    /// <summary>The turret the owner is at: the AC-130's while its streak is up, else the gunship's (null without a turret).</summary>
    public GunshipDef? GunDef() => Ac130Active ? _ctx.Killstreaks?.Ac130 : _ctx.Killstreaks?.Gunship;

    /// <summary>Seconds left in the AC-130 (its streak duration less the time since it was used); 0 when unknown.</summary>
    public double Ac130SecondsLeft
    {
        get
        {
            var duration = _ctx.Killstreaks?.Find(Ac130Streak.StreakId)?.DurationSeconds ?? 0;
            return Math.Max(0, duration - (_ctx.Now - _ac130Since));
        }
    }

    /// <summary>Whether the cannon is the only weapon of the gunner's turret (the next-weapon hint is then left out).</summary>
    public bool HeliCycleWeapons => !_heliActive;

    /// <summary>The timer and kill count for the gunner overlay, or nothing when no timed streak is up.</summary>
    public (string Timer, string Kills) HeliClock()
    {
        if (!_heliActive || _ctx.Killstreaks?.Find(AttackHelicopterStreak.StreakId) is not { } def) return ("", "");
        var left = Math.Max(0, (int)Math.Ceiling(def.DurationSeconds - (_ctx.Now - _heliUsedAt)));
        return (Strings.F("heli.clock", $"{left / 60}:{left % 60:00}"), Strings.F("heli.kills", _heliKills));
    }

    /// <summary>StreakUsed: the AC-130's clock starts, or the helicopter's timer starts and its kill count resets.</summary>
    public void OnUsed(string streakId)
    {
        if (streakId == Ac130Streak.StreakId) { Ac130Active = true; _ac130Since = _ctx.Now; }
        if (streakId != AttackHelicopterStreak.StreakId) return;
        _heliActive = true;
        _heliUsedAt = _ctx.Now;
        _heliKills = 0;
        HintsChanged?.Invoke();
    }

    /// <summary>StreakEnded: the clocks go away (the helicopter despawns with it).</summary>
    public void OnEnded(string streakId)
    {
        if (streakId == Ac130Streak.StreakId) Ac130Active = false;
        if (streakId == AttackHelicopterStreak.StreakId) EndHeli();
    }

    /// <summary>The owner died: every gunner view ends, without waiting for the StreakEnded that may be lost.</summary>
    public void OnOwnerDied()
    {
        Ac130Active = false;
        EndHeli();
    }

    /// <summary>A kill from the match feed: the owner's kills while the helicopter is up.</summary>
    public void OnKill(KillMsg k)
    {
        if (_heliActive && k.Killer == _ctx.MyId && k.Killer != k.Victim) _heliKills++;
    }

    void EndHeli()
    {
        _heliActive = false;
        _heliKills = 0;
        HintsChanged?.Invoke();
    }
}
