using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The MOAB's on-screen part (ticket #129): a top-centre banner that counts the countdown down, a full-screen flash at
/// detonation and a short "MOAB by X" banner after it. Tokens come from <see cref="ShooterTheme"/>; the countdown is
/// text and a danger border (never colour alone). Reduce motion drops the flash and the banner fade; the text stays.
/// Built once by <see cref="NetClient"/> and only ever shown for MOAB events.
/// </summary>
public partial class MoabBanner : Control
{
    readonly Label _label;
    readonly PanelContainer _panel;
    readonly ColorRect _flash;
    readonly MoabBannerModel _model = new(); // the phase and its deadlines, on the match-independent wall clock
    Change<int> _shownSeconds; // the seconds the label last drew: it only redraws when they change

    public MoabBanner()
    {
        Theme = ShooterTheme.Build();
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _flash = new ColorRect { Color = new Color(1f, 0.97f, 0.85f, 0f), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _flash.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_flash);

        _panel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", ShooterTheme.Box(ShooterTheme.Surface, ShooterTheme.Space * 2, ShooterTheme.Danger, ShooterTheme.BorderStrong));
        _panel.SetAnchorsPreset(LayoutPreset.CenterTop);
        _panel.OffsetTop = ShooterTheme.Margin * 4;
        _panel.GrowHorizontal = GrowDirection.Both;
        AddChild(_panel);

        Resized += Rescale;
        _panel.Resized += () => _panel.PivotOffset = new Vector2(_panel.Size.X / 2f, 0); // the text length changes the width
        _label = new Label { ThemeTypeVariation = "HudBig", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddChild(_label);
    }

    /// <summary>Scales the banner with the screen and the UI scale setting, growing down and sideways from its top centre.</summary>
    void Rescale()
    {
        var s = ShooterTheme.ScreenScale(Size);
        _panel.Scale = new Vector2(s, s);
        _panel.OffsetTop = ShooterTheme.Margin * 4 * s;
        _panel.PivotOffset = new Vector2(_panel.Size.X / 2f, 0);
    }

    /// <summary>The countdown starts: the banner shows the seconds left until detonation.</summary>
    public void Armed(float seconds)
    {
        Rescale();
        _model.Armed(Now(), seconds);
        _shownSeconds.Reset();
        ShowCountdown();
    }

    /// <summary>Detonation: a full-screen flash (none with reduce motion) and the "MOAB by X" banner.</summary>
    public void Blast(string owner)
    {
        Rescale();
        _model.Blast(Now(), owner, GameSettings.Current.ReduceMotion);
        _flash.Visible = _model.FlashActive;
        SetText(Strings.F("moab.blast", owner));
        _panel.Visible = _model.PanelVisible;
    }

    /// <summary>The countdown was stopped: the banner says so briefly.</summary>
    public void Cancelled()
    {
        _model.Cancelled(Now());
        SetText(Strings.T("moab.cancelled"));
        _panel.Visible = _model.PanelVisible;
    }

    public override void _Process(double delta)
    {
        var now = Now();
        _model.Tick(now);
        if (_model.Phase == MoabBannerPhase.Countdown) ShowCountdown();
        if (_model.FlashActive) _flash.Color = new Color(1f, 0.97f, 0.85f, _model.FlashAlpha(now));
        else if (_flash.Visible) _flash.Color = new Color(1f, 0.97f, 0.85f, 0f); // ended: the next flash starts clear
        _flash.Visible = _model.FlashActive;
        _panel.Visible = _model.PanelVisible; // shown exactly while a phase is up
    }

    /// <summary>Whole seconds left, rounded up so the banner reads 5, 4, 3, 2, 1.</summary>
    void ShowCountdown()
    {
        var left = _model.SecondsLeft(Now());
        if (!_shownSeconds.Update(left) && _panel.Visible) return;
        SetText(Strings.F("moab.inbound", left));
        _panel.Visible = _model.PanelVisible;
    }

    void SetText(string text) { if (_label.Text != text) _label.Text = text; }

    static double Now() => Time.GetTicksMsec() / 1000.0;
}
