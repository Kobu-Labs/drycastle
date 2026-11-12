using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The corner indicator "Dev: all unlocked" (issue #149). Shown on the loadout screen and the pause menu while the server runs
/// with <c>--unlock-all</c>, so the unlocks are never taken for real progression. It is text plus the warning colour (never
/// colour alone), ignores the mouse, and sits at the top right inside the screen margin.
/// </summary>
public static class DevBadge
{
    public static Label Create(bool shown)
    {
        var l = Ui.Lbl(Strings.T("dev.unlock_all"), "HudWarn");
        l.Visible = shown;
        l.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.KeepSize, ShooterTheme.Margin);
        return l;
    }
}
