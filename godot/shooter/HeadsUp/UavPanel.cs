using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// UAV remaining-time indicator (issue #121): a small panel right of the minimap, shown only while the owner's UAV is up.
/// The panel and its label have fixed sizes, so the countdown never moves the layout. <see cref="SetSeconds"/> writes
/// the label only when the whole second changes, so nothing allocates per frame.
/// </summary>
internal sealed class UavPanel
{
    readonly PanelContainer _panel;
    readonly Label _time;
    int _shownSeconds = -1;

    public UavPanel(Control root)
    {
        _panel = HudUi.P("HudPanel");
        PlaceBeside(GameSettings.Current.MinimapSize);
        _panel.Visible = false;
        root.AddChild(_panel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _panel.AddChild(v);
        v.AddChild(HudUi.L(Strings.T("hud.uav.title"), "HudSmall"));
        _time = HudUi.L("", "HudAccent", minW: 112);
        v.AddChild(_time);
    }

    /// <summary>Beside the minimap, whatever its size.</summary>
    public void PlaceBeside(float minimapSize) => HudUi.Place(_panel, 0, 0, ShooterTheme.Margin + minimapSize + ShooterTheme.Space, ShooterTheme.Margin + 32, 136, 64);

    /// <summary>Seconds left on the owner's UAV; zero or less hides the indicator.</summary>
    public void SetSeconds(float seconds)
    {
        var whole = seconds > 0 ? (int)MathF.Ceiling(seconds) : 0;
        if (whole == _shownSeconds) return;
        _shownSeconds = whole;
        _panel.Visible = whole > 0;
        if (whole > 0) HudUi.Set(_time, Strings.F("hud.uav.time", whole / 60, whole % 60));
    }
}
