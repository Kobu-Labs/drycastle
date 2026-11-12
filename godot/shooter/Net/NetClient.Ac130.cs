using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The AC-130 on the client (killstreak "ac130"). The owner's StreakUsed starts the AC-130 view (its turret def, its weapon
/// selector and its time left, counted from the streak's duration in killstreaks.json); StreakEnded ends it. Everyone sees
/// the shell bursts (the gunship FX, #159): the explosion look at the splash radius and the shake.
/// </summary>
public partial class NetClient
{
    /// <summary>True from StreakUsed(ac130) to its StreakEnded: the owner's turret is the AC-130's.</summary>
    bool _ac130Active;
    double _ac130Since;

    /// <summary>The turret the owner is at: the AC-130's while its streak is up, else the gunship's (null without a turret).</summary>
    GunshipDef? GunDef() => _ac130Active ? Killstreaks?.Ac130 : Killstreaks?.Gunship;

    /// <summary>Seconds left in the AC-130 (its streak duration less the time since it was used); 0 when unknown.</summary>
    double Ac130SecondsLeft
    {
        get
        {
            var duration = Killstreaks?.Find(Ac130Streak.StreakId)?.DurationSeconds ?? 0;
            return Math.Max(0, duration - (Now - _ac130Since));
        }
    }

    void Ac130Used()
    {
        _ac130Active = true;
        _ac130Since = Now;
    }

    void Ac130Ended() => _ac130Active = false;
}
