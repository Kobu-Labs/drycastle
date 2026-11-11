using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// UAV remaining-time indicator (issue #121): a small panel right of the minimap, shown only while the owner's UAV is up.
/// The panel and its label have fixed sizes, so the countdown never moves the layout. <see cref="SetUavSeconds"/> writes
/// the label only when the whole second changes, so nothing allocates per frame.
/// </summary>
public partial class Hud
{
    Control _uavPanel = null!;
    Label _uavTime = null!;
    int _uavShownSeconds = -1;

    void BuildUav()
    {
        _uavPanel = Place(P("HudPanel"), 0, 0, ShooterTheme.Margin + 224 + ShooterTheme.Space, ShooterTheme.Margin + 32, 136, 64);
        _uavPanel.Visible = false;
        _root.AddChild(_uavPanel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _uavPanel.AddChild(v);
        v.AddChild(L(Strings.T("hud.uav.title"), "HudSmall"));
        _uavTime = L("", "HudAccent", minW: 112);
        v.AddChild(_uavTime);
    }

    /// <summary>Seconds left on the owner's UAV; zero or less hides the indicator.</summary>
    public void SetUavSeconds(float seconds)
    {
        var whole = seconds > 0 ? (int)MathF.Ceiling(seconds) : 0;
        if (whole == _uavShownSeconds) return;
        _uavShownSeconds = whole;
        _uavPanel.Visible = whole > 0;
        if (whole > 0) Set(_uavTime, Strings.F("hud.uav.time", whole / 60, whole % 60));
    }
}
