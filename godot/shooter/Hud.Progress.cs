using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The level, XP bar and perk name above the ammo block (ticket #143). The strip sits right above the ammo panel and
/// pushes the vehicle speed readout up so the two never overlap. Values are set on change, not per frame: the client calls
/// <see cref="SetProgress"/> at the spawn, on a profile, a level up and the round XP. The perk is named in text, not an icon,
/// so it is never colour- or icon-only.
/// </summary>
public partial class Hud
{
    const float ProgressStripHeight = 72;

    Label _progLevel = null!, _progPerk = null!, _progXp = null!;
    ProgressBar _xpBar = null!;

    void BuildProgress()
    {
        var strip = Place(P("HudPanel"), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - 112 - ShooterTheme.Space, 320, ProgressStripHeight);
        _root.AddChild(strip);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 2);
        strip.AddChild(v);
        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(top);
        _progLevel = L("", "HudSmall");
        _progLevel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _progPerk = L("", "HudSmall", HorizontalAlignment.Right);
        _progPerk.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(_progLevel); top.AddChild(_progPerk);
        _xpBar = new ProgressBar { MaxValue = 1, Value = 0, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(_xpBar);
        _progXp = L("", "HudSmall");
        v.AddChild(_progXp);
        // the speed readout (vehicles only) sits above the strip, so the strip never covers it
        Place(_speed, 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - 112 - ShooterTheme.Space - ProgressStripHeight - ShooterTheme.Space, 320, 40);
    }

    /// <summary>
    /// The level, the XP into this level and the XP the next level needs (<paramref name="nextXp"/> is -1 at the top level),
    /// and the perk's name (the loadout's, or "None").
    /// </summary>
    public void SetProgress(int level, int xp, int levelXp, int nextXp, string perkName)
    {
        Set(_progLevel, Strings.F("hud2.progress.level", level));
        Set(_progPerk, Strings.F("hud2.perk", perkName));
        if (nextXp < 0)
        {
            _xpBar.MaxValue = 1; _xpBar.Value = 1;
            Set(_progXp, Strings.F("hud2.progress.max", xp));
            return;
        }
        var span = Math.Max(1, nextXp - levelXp);
        var into = Math.Clamp(xp - levelXp, 0, span);
        _xpBar.MaxValue = span; _xpBar.Value = into;
        Set(_progXp, Strings.F("hud2.progress.xp", into, span));
    }
}
