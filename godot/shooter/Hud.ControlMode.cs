using System.Collections.Generic;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The control-mode switch (ticket #152): while the owner flies a streak's point of view (a gunship turret, the attack
/// missile), the HUD panels are hidden and the streak's own overlay (reticle, weapon, timer, kills, hit marker) is the main
/// thing on screen. The kill feed stays, compact and top right, and shows only the owner's kills (ticket #154). Hiding the
/// panels changes no layout, so restoring them is instant when the session ends (death, leave, timeout). The caller computes
/// the mode each frame from the control state, so it cannot get stuck.
/// </summary>
public partial class Hud
{
    /// <summary>True while the regular HUD is hidden for a control session.</summary>
    public bool ControlMode { get; private set; }

    /// <summary>The owner's kills, kept apart from the feed of everyone's kills, for the compact feed of a control session.</summary>
    readonly List<(KillMsg Msg, string Killer, string Victim)> _mineKills = new();
    readonly List<double> _mineTimes = new();

    /// <summary>
    /// Hides the HUD panels for a control session (on) or shows them again (off). Idempotent; cheap to call every frame. The
    /// kill feed is not under the panels root, so it stays up; it switches to the owner's kills and the compact size.
    /// </summary>
    public void SetControlMode(bool on)
    {
        if (ControlMode == on) return;
        ControlMode = on;
        _root.Visible = !on;
        _feedBox.Scale = on ? new Vector2(ShooterTheme.FeedCompactScale, ShooterTheme.FeedCompactScale) : Vector2.One;
        _feedShown = (-1, -1); // redraw the feed from the other list
    }

    /// <summary>Records a kill for the compact feed when the owner made it (a suicide is not a kill).</summary>
    void AddOwnKill(KillMsg k, string killer, string victim)
    {
        if (k.Killer != MyId || k.Killer == k.Victim) return;
        _mineKills.Add((k, killer, victim)); _mineTimes.Add(Now);
        if (_mineKills.Count > 64) { _mineKills.RemoveRange(0, 32); _mineTimes.RemoveRange(0, 32); }
    }
}
