using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The HUD's level, XP bar and perk name (ticket #143). The client reads the level and XP from the profile and the XP
/// thresholds from levels.json (<see cref="XpCurve"/>, set by the host). Pushed on change, not per frame.
/// </summary>
public partial class NetClient
{
    /// <summary>The XP curve (levels.json), so the XP bar knows where the current level starts and ends. Null hides the bar's span.</summary>
    public LevelCurve? XpCurve;

    /// <summary>Sends the level, XP and the active loadout's perk name to the HUD.</summary>
    void RefreshProgress()
    {
        if (_hud == null) return;
        var p = Profile;
        var level = p?.Level ?? 1;
        var xp = p?.Xp ?? 0;
        var curve = XpCurve;
        var levelXp = curve?.XpForLevel(level) ?? 0;
        var nextXp = curve != null && level < curve.MaxLevel ? curve.XpForLevel(level + 1) : -1;
        var active = p is { } pr && pr.ActiveSlot >= 0 && pr.ActiveSlot < pr.Loadouts.Count ? pr.Loadouts[pr.ActiveSlot] : null;
        var perk = active == null || active.Perk == "" ? Strings.T("loadout.none") : PerkData?.Find(active.Perk)?.Name ?? active.Perk;
        _hud.SetProgress(level, xp, levelXp, curve == null ? -1 : nextXp, perk);
    }
}
