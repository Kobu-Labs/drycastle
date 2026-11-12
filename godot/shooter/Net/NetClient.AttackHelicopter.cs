using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The attack helicopter's gunner HUD state on the client (ticket #126): the time left and the owner's kills during the
/// streak. The server's owner-only events start and end it (StreakUsed, StreakEnded); the time is counted from the use on this
/// client, so it can lag the server by one round trip. Only the gunner overlay reads it.
/// </summary>
public partial class NetClient
{
    bool _heliActive;
    double _heliUsedAt;
    int _heliKills;

    /// <summary>StreakUsed(attack_helicopter): the timer starts and the kill count resets.</summary>
    void HeliStreakUsed(string streakId)
    {
        if (streakId != AttackHelicopterStreak.StreakId) return;
        _heliActive = true;
        _heliUsedAt = Now;
        _heliKills = 0;
        _gunDirty = true; // the hints change with the streak (no next-weapon key)
    }

    /// <summary>StreakEnded(attack_helicopter): the gunner's HUD clock goes away (the helicopter despawns with it).</summary>
    void HeliStreakEnded(string streakId)
    {
        if (streakId != AttackHelicopterStreak.StreakId) return;
        _heliActive = false;
        _gunDirty = true;
    }

    /// <summary>A kill from the match feed: the owner's kills while the helicopter is up.</summary>
    void HeliKill(KillMsg k)
    {
        if (_heliActive && k.Killer == _myId && k.Killer != k.Victim) _heliKills++;
    }

    /// <summary>The timer and kill count for the gunner overlay, or nothing when no timed streak is up.</summary>
    (string Timer, string Kills) HeliClock()
    {
        if (!_heliActive || Killstreaks?.Find(AttackHelicopterStreak.StreakId) is not { } def) return ("", "");
        var left = Math.Max(0, (int)Math.Ceiling(def.DurationSeconds - (Now - _heliUsedAt)));
        return (Strings.F("heli.clock", $"{left / 60}:{left % 60:00}"), Strings.F("heli.kills", _heliKills));
    }

    /// <summary>Whether the cannon is the only weapon of the gunner's turret (the next-weapon hint is then left out).</summary>
    bool HeliCycleWeapons => !_heliActive;
}
