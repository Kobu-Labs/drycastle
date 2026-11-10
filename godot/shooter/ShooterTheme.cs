using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The shooter's one Theme and its tokens (docs/standards/ui-ux.md section 4), same pattern as the viewer's
/// EditorTheme (kept separate: the shooter is its own Godot project). HUD and later menus use these constants and
/// the theme type variations (HudDim, HudHead, HudBig, HudDisplay, HudPanel, HudSlot, ...); no per-control colour,
/// font-size or style overrides. All sizes are in reference pixels at 1080p; <see cref="Hud"/> scales the root.
/// </summary>
public static class ShooterTheme
{
    // spacing and shape (8 px grid)
    public const int Space = 8;
    public const int Margin = 4 * Space;   // screen edge safe margin
    public const int Radius = 4;
    public const int Border = 1, BorderStrong = 3;

    // text sizes (five); body text is 20 and nothing is under 16 at 1080p
    public const int FontSmall = 16, FontBody = 20, FontHead = 24, FontBig = 32, FontDisplay = 48;

    // colours
    public static readonly Color Surface = new(0.07f, 0.08f, 0.11f, 0.72f);
    public static readonly Color Raised = new(0.12f, 0.14f, 0.19f, 0.80f);
    public static readonly Color Active = new(0.10f, 0.34f, 0.48f, 0.92f);
    public static readonly Color Line = new(0.45f, 0.49f, 0.58f);
    public static readonly Color Text = new(0.96f, 0.97f, 0.98f);
    public static readonly Color Dim = new(0.76f, 0.79f, 0.85f);
    public static readonly Color Accent = new(0.40f, 0.82f, 1f);
    public static readonly Color Good = new(0.45f, 0.90f, 0.55f);
    public static readonly Color Warn = new(1f, 0.74f, 0.30f);
    public static readonly Color Danger = new(1f, 0.42f, 0.38f);
    public static readonly Color Outline = new(0f, 0f, 0f, 0.85f);
    public static readonly Color Scrim = new(0f, 0f, 0f, 0.60f);

    public static StyleBoxFlat Box(Color bg, int pad = Space, Color? border = null, int borderWidth = Border, int radius = Radius)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetContentMarginAll(pad);
        s.SetCornerRadiusAll(radius);
        if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(borderWidth); }
        return s;
    }

    static Theme? _theme;

    public static Theme Build()
    {
        if (_theme != null) return _theme;
        var t = new Theme { DefaultFontSize = FontBody };

        t.SetColor("font_color", "Label", Text);
        t.SetColor("font_outline_color", "Label", Outline);
        t.SetConstant("outline_size", "Label", 4);
        t.SetFontSize("font_size", "Label", FontBody);
        Variation(t, "HudSmall", FontSmall, Dim);
        Variation(t, "HudDim", FontBody, Dim);
        Variation(t, "HudHead", FontHead, Text);
        Variation(t, "HudBig", FontBig, Text);
        Variation(t, "HudDisplay", FontDisplay, Text);
        Variation(t, "HudAccent", FontBody, Accent);
        Variation(t, "HudWarn", FontBody, Warn);
        Variation(t, "HudDanger", FontHead, Danger);
        Variation(t, "HudDisplayDanger", FontDisplay, Danger);

        t.SetStylebox("panel", "PanelContainer", Box(Surface));
        PanelVariation(t, "HudPanel", Box(Surface, Space, Line));
        PanelVariation(t, "HudSlot", Box(Raised, Space, Line));
        PanelVariation(t, "HudSlotSelected", Box(Active, Space, Accent, BorderStrong));
        PanelVariation(t, "HudRow", Box(Raised, Space / 2));
        PanelVariation(t, "HudRowMine", Box(Active, Space / 2, Accent, BorderStrong));
        PanelVariation(t, "HudScrim", Box(Scrim, 0, null, 0, 0));
        PanelVariation(t, "HudBare", Box(Colors.Transparent, 0, null, 0, 0));
        var prot = new StyleBoxFlat { DrawCenter = false, BorderColor = new Color(Accent, 0.45f) };
        prot.SetBorderWidthAll(Space);
        PanelVariation(t, "HudProtection", prot);

        t.SetStylebox("background", "ProgressBar", Box(new Color(0, 0, 0, 0.55f), 0, Line));
        Bar(t, "HealthBar", Good);
        Bar(t, "HealthBarLow", Danger);
        Bar(t, "ReloadBar", Warn);
        t.SetFontSize("font_size", "ProgressBar", FontSmall);
        _theme = t;
        return t;
    }

    static void Variation(Theme t, string name, int size, Color color)
    {
        t.SetTypeVariation(name, "Label");
        t.SetFontSize("font_size", name, size);
        t.SetColor("font_color", name, color);
    }

    static void PanelVariation(Theme t, string name, StyleBox box)
    {
        t.SetTypeVariation(name, "PanelContainer");
        t.SetStylebox("panel", name, box);
    }

    static void Bar(Theme t, string name, Color fill)
    {
        t.SetTypeVariation(name, "ProgressBar");
        t.SetStylebox("fill", name, Box(fill, 0, null, 0, 2));
        t.SetStylebox("background", name, Box(new Color(0, 0, 0, 0.55f), 0, Line, Border, 2));
    }
}
