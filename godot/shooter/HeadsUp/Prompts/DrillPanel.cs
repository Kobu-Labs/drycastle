using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Cartel;

namespace Drycastle.Shooter.HeadsUp.Prompts;

/// <summary>What the Drill panel shows: the state of the Drill on the safe the player is near, how far it is, the seconds left, and the key that sets or fixes it.</summary>
public readonly record struct DrillHudInfo(DrillState State, float Fraction, float SecondsLeft, string Key);

/// <summary>
/// The Drill on a Trap house's safe, for a party player near it (#496): a fixed-size panel under the prompt line with three lines, the state in words (a jam is also the warning colour),
/// the bar and what to do or how long is left. Faded in and out; written only when a shown value changes.
/// </summary>
internal sealed class DrillPanel
{
    const int Width = 45 * ShooterTheme.Space, Height = 13 * ShooterTheme.Space, Below = 6 * ShooterTheme.Space, BarHeight = 2 * ShooterTheme.Space;

    readonly PanelContainer _panel;
    readonly Label _state, _detail;
    readonly ProgressBar _bar;
    DrillHudInfo? _shown;
    int _shownSeconds = -1;

    public DrillPanel(Control root)
    {
        _panel = HudUi.P("HudPanel");
        _panel.AnchorLeft = _panel.AnchorRight = 0.5f;
        _panel.AnchorTop = _panel.AnchorBottom = 0.7f;
        _panel.OffsetLeft = -Width / 2; _panel.OffsetRight = Width / 2;
        _panel.OffsetTop = Below; _panel.OffsetBottom = Below + Height;
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _panel.AddChild(v);
        _state = HudUi.L("", "HudHead", HorizontalAlignment.Center); v.AddChild(_state);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0, ShowPercentage = false, ThemeTypeVariation = "DrillBar", CustomMinimumSize = new Vector2(0, BarHeight), MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(_bar);
        _detail = HudUi.L("", "HudTextSmall", HorizontalAlignment.Center); v.AddChild(_detail);
        root.AddChild(_panel);
        _panel.Modulate = new Color(1, 1, 1, 0);
    }

    /// <summary>The Drill to show, or null to hide the panel.</summary>
    public void Set(DrillHudInfo? info)
    {
        if (info is not { } i) { if (_shown != null) { _shown = null; HudUi.Fade(_panel, 0); } return; }
        var seconds = (int)MathF.Ceiling(i.SecondsLeft);
        var first = _shown is not { } was || was.State != i.State || was.Key != i.Key;
        if (!Mathf.IsEqualApprox((float)_bar.Value, i.Fraction)) _bar.Value = i.Fraction;
        _shown = i;
        if (!first && seconds == _shownSeconds) return;
        _shownSeconds = seconds;
        HudUi.Fade(_panel, 1);
        var jammed = i.State == DrillState.Jammed;
        _state.ThemeTypeVariation = jammed ? "HudWarn" : "HudHead";
        _bar.ThemeTypeVariation = jammed ? "DrillBarJammed" : "DrillBar";
        HudUi.Set(_state, Strings.T(i.State switch { DrillState.Armed => "hud.drill.armed", DrillState.Jammed => "hud.drill.jammed", DrillState.Done => "hud.drill.done", _ => "hud.drill.running" }));
        HudUi.Set(_detail, i.State switch
        {
            DrillState.Armed => Strings.F("hud.drill.armed.detail", i.Key),
            DrillState.Jammed => Strings.F("hud.drill.jammed.detail", i.Key),
            DrillState.Done => Strings.T("hud.drill.done.detail"),
            _ => Strings.F("hud.drill.left", seconds),
        });
    }
}
