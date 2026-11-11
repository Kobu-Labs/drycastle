using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The attack missile's point-of-view overlay: a title, altitude and time readout, the control hint, and the static that
/// fades in on impact. Shown only while the owner controls a missile (<see cref="NetClient"/> drives it). Mouse-only input
/// by design; it takes no focus and no clicks. Layout and colour come from <see cref="ShooterTheme"/> tokens and every
/// string from <see cref="Strings"/>; the readouts are rewritten only when their whole-number value changes.
/// </summary>
public partial class MissilePovOverlay : CanvasLayer
{
    /// <summary>How long the static takes to clear after an impact (seconds).</summary>
    const double StaticSeconds = 0.6;
    const float StaticPeak = 0.85f;

    Control _root = null!;
    ColorRect _static = null!;
    Label _alt = null!, _time = null!;
    int _shownAlt = int.MinValue, _shownTime = int.MinValue;
    double _impactAt = double.NegativeInfinity;

    public override void _Ready()
    {
        Layer = 5;
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _static = new ColorRect { Color = new Color(0.82f, 0.84f, 0.88f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _static.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_static);

        // title and hint span the width and centre their text, so no size is needed before layout
        var title = MakeLabel(Strings.T("pov.title"), ShooterTheme.FontHead, ShooterTheme.Accent);
        title.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        title.OffsetTop = ShooterTheme.Margin;
        title.OffsetBottom = ShooterTheme.Margin + 4 * ShooterTheme.Space;
        _root.AddChild(title);

        _alt = MakeLabel("", ShooterTheme.FontBody, ShooterTheme.Text);
        _alt.Position = new Vector2(ShooterTheme.Margin, ShooterTheme.Margin * 3);
        _root.AddChild(_alt);

        _time = MakeLabel("", ShooterTheme.FontBody, ShooterTheme.Text);
        _time.Position = new Vector2(ShooterTheme.Margin, ShooterTheme.Margin * 3 + 4 * ShooterTheme.Space);
        _root.AddChild(_time);

        var hint = MakeLabel(Strings.T("pov.hint"), ShooterTheme.FontSmall, ShooterTheme.Dim);
        hint.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        hint.OffsetTop = -ShooterTheme.Margin * 2;
        hint.OffsetBottom = -ShooterTheme.Margin;
        _root.AddChild(hint);
    }

    static Label MakeLabel(string text, int size, Color colour)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", colour);
        l.AddThemeColorOverride("font_outline_color", ShooterTheme.Outline);
        l.AddThemeConstantOverride("outline_size", 4);
        return l;
    }

    /// <summary>Shows or hides the whole overlay (the static, when it is still fading, is part of it).</summary>
    public void SetOn(bool on) => _root.Visible = on;

    /// <summary>The readout: altitude above the launch point and the seconds since the missile was used. Whole numbers only.</summary>
    public void SetReadout(float altitudeMetres, double seconds)
    {
        var a = (int)MathF.Round(altitudeMetres);
        var t = (int)seconds;
        if (a != _shownAlt) { _shownAlt = a; _alt.Text = Strings.F("pov.alt", a); }
        if (t != _shownTime) { _shownTime = t; _time.Text = Strings.F("pov.time", t); }
    }

    /// <summary>The missile hit: the screen goes to static and clears over <see cref="StaticSeconds"/>.</summary>
    public void Impact() => _impactAt = Time.GetTicksMsec() / 1000.0;

    public override void _Process(double delta)
    {
        if (!_root.Visible) return;
        var left = 1 - (Time.GetTicksMsec() / 1000.0 - _impactAt) / StaticSeconds;
        var alpha = (float)(StaticPeak * Math.Clamp(left, 0, 1));
        var c = _static.Color;
        if (Mathf.Abs(c.A - alpha) > 0.005f) _static.Color = new Color(c.R, c.G, c.B, alpha);
    }
}
