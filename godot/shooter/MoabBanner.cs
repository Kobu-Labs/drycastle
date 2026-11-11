using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The MOAB's on-screen part (ticket #129): a top-centre banner that counts the countdown down, a full-screen flash at
/// detonation and a short "MOAB by X" banner after it. Tokens come from <see cref="ShooterTheme"/>; the countdown is
/// text and a danger border (never colour alone). Reduce motion drops the flash and the banner fade; the text stays.
/// Built once by <see cref="NetClient"/> and only ever shown for MOAB events.
/// </summary>
public partial class MoabBanner : Control
{
    /// <summary>How long the "MOAB by X" banner stays after detonation.</summary>
    const double BlastBannerSeconds = 3.0;
    const double FlashSeconds = 0.6;

    readonly Label _label;
    readonly PanelContainer _panel;
    readonly ColorRect _flash;
    double _countdownEnds = -1;   // match-independent wall clock (seconds), -1 = not counting
    double _bannerEnds = -1;
    double _flashEnds = -1;
    int _shownSeconds = -1;

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

        _label = new Label { ThemeTypeVariation = "HudBig", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddChild(_label);
    }

    /// <summary>The countdown starts: the banner shows the seconds left until detonation.</summary>
    public void Armed(float seconds)
    {
        _countdownEnds = Now() + Math.Max(0, seconds);
        _bannerEnds = -1;
        _shownSeconds = -1;
        ShowCountdown();
    }

    /// <summary>Detonation: a full-screen flash (none with reduce motion) and the "MOAB by X" banner.</summary>
    public void Blast(string owner)
    {
        _countdownEnds = -1;
        _bannerEnds = Now() + BlastBannerSeconds;
        _flashEnds = GameSettings.Current.ReduceMotion ? -1 : Now() + FlashSeconds;
        _flash.Visible = _flashEnds > 0;
        SetText(Strings.F("moab.blast", owner));
        _panel.Visible = true;
    }

    /// <summary>The countdown was stopped: the banner says so briefly.</summary>
    public void Cancelled()
    {
        _countdownEnds = -1;
        _bannerEnds = Now() + BlastBannerSeconds;
        SetText(Strings.T("moab.cancelled"));
        _panel.Visible = true;
    }

    public override void _Process(double delta)
    {
        var now = Now();
        if (_countdownEnds >= 0)
        {
            if (now >= _countdownEnds) { _countdownEnds = -1; }
            else ShowCountdown();
        }
        if (_bannerEnds >= 0 && now >= _bannerEnds && _countdownEnds < 0) { _bannerEnds = -1; _panel.Visible = false; }
        if (_flashEnds > 0)
        {
            var k = (float)Math.Clamp((_flashEnds - now) / FlashSeconds, 0, 1);
            _flash.Color = new Color(1f, 0.97f, 0.85f, 0.85f * k);
            if (now >= _flashEnds) { _flashEnds = -1; _flash.Visible = false; }
        }
        if (_countdownEnds < 0 && _bannerEnds < 0) _panel.Visible = false;
    }

    /// <summary>Whole seconds left, rounded up so the banner reads 5, 4, 3, 2, 1.</summary>
    void ShowCountdown()
    {
        var left = (int)Math.Ceiling(Math.Max(0, _countdownEnds - Now()));
        if (left == _shownSeconds && _panel.Visible) return;
        _shownSeconds = left;
        SetText(Strings.F("moab.inbound", left));
        _panel.Visible = true;
    }

    void SetText(string text) { if (_label.Text != text) _label.Text = text; }

    static double Now() => Time.GetTicksMsec() / 1000.0;
}
