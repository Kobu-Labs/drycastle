using Godot;
using Suchohrad.ShooterCore;

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
    /// <summary>The compact kill feed of a control session (ticket #154): scale of the feed and its row count.</summary>
    public const float FeedCompactScale = 0.75f;
    public const int FeedCompactRows = 3;
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
    public static readonly Color Accent = new(StatePalette.Accent.R, StatePalette.Accent.G, StatePalette.Accent.B);
    public static readonly Color Hover = new(0.19f, 0.23f, 0.31f, 0.95f);
    public static readonly Color Backdrop = new(0.05f, 0.06f, 0.09f);
    public static readonly Color DisabledText = new(0.58f, 0.61f, 0.68f);
    // good / warn / danger swap to a blue-green / yellow / vermillion set (distinguishable with red-green colour blindness, and from the cyan accent) when the accessibility flag is on
    public static bool ColorBlind { get; private set; }
    public static Color Good => From(StatePalette.Good(ColorBlind));
    public static Color Warn => From(StatePalette.Warn(ColorBlind));
    public static Color Danger => From(StatePalette.Danger(ColorBlind));
    /// <summary>The UI scale for a screen of this size (height over 1080 p times the UI scale setting): overlays that size their own text use it, as the HUD does.</summary>
    public static float ScreenScale(Vector2 screen) => HudLogic.ScreenScale(screen.Y, GameSettings.Current.UiScale);
    static Color From(StatePalette.Rgb c) => new(c.R, c.G, c.B);
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
        _theme = new Theme { DefaultFontSize = FontBody };
        Populate(_theme);
        return _theme;
    }

    /// <summary>Switches the palette flag and refills the shared Theme in place (every control using it restyles).</summary>
    public static void SetColorBlind(bool on)
    {
        if (ColorBlind == on) return;
        ColorBlind = on;
        if (_theme != null) Populate(_theme);
    }

    static void Populate(Theme t)
    {

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
        Bar(t, "StatBar", Accent);       // loadout editor: the chosen item's stat
        Bar(t, "StatBarSaved", Dim);     // loadout editor: the saved item's stat, for comparison
        t.SetFontSize("font_size", "ProgressBar", FontSmall);
        Menu(t);
    }

    static StyleBoxFlat Ring(int pad = Space * 1) => new StyleBoxFlat { DrawCenter = false, BorderColor = Accent }.With(b => { b.SetBorderWidthAll(BorderStrong); b.SetCornerRadiusAll(Radius); b.SetContentMarginAll(pad); });

    static StyleBoxFlat With(this StyleBoxFlat b, Action<StyleBoxFlat> f) { f(b); return b; }

    /// <summary>Menu controls: every state is distinct (normal, hover, pressed, focus ring, disabled); focus is a thick accent ring.</summary>
    static void Menu(Theme t)
    {
        const int pad = 12;
        foreach (var type in new[] { "Button", "OptionButton", "CheckBox" })
        {
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Text);
            t.SetColor("font_pressed_color", type, Text);
            t.SetColor("font_hover_pressed_color", type, Text);
            t.SetColor("font_focus_color", type, Text);
            t.SetColor("font_disabled_color", type, DisabledText);
            t.SetFontSize("font_size", type, FontBody);
            t.SetStylebox("focus", type, Ring(pad));
        }
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal", type, Box(Raised, pad, Line));
            t.SetStylebox("hover", type, Box(Hover, pad, Accent));
            t.SetStylebox("pressed", type, Box(Active, pad, Accent, BorderStrong));
            t.SetStylebox("hover_pressed", type, Box(Active, pad, Accent, BorderStrong));
            t.SetStylebox("disabled", type, Box(Surface, pad, new Color(Line, 0.4f)));
        }
        t.SetConstant("separation", "VBoxContainer", Space);
        t.SetConstant("separation", "HBoxContainer", Space);
        t.SetConstant("h_separation", "Button", Space);
        t.SetConstant("h_separation", "CheckBox", Space);
        t.SetConstant("h_separation", "GridContainer", Space + Space / 2);
        t.SetConstant("v_separation", "GridContainer", Space + Space / 2);
        VariationButton(t, "MenuPrimary", Box(Active, pad, Accent), Box(Hover, pad, Accent, BorderStrong), Box(Raised, pad, Accent, BorderStrong));
        VariationButton(t, "MenuDanger", Box(Raised, pad, Danger), Box(Hover, pad, Danger), Box(Active, pad, Danger, BorderStrong));
        VariationButton(t, "MenuTab", Box(Surface, pad, Line), Box(Hover, pad, Accent), Box(Active, pad, Accent, BorderStrong));
        t.SetStylebox("focus", "MenuTab", Ring(pad));
        t.SetStylebox("hover_pressed", "MenuTab", Box(Active, pad, Accent, BorderStrong));
        t.SetStylebox("normal", "CheckBox", Box(Colors.Transparent, Space / 2, null, 0));
        t.SetStylebox("hover", "CheckBox", Box(Hover, Space / 2, null, 0));
        t.SetStylebox("pressed", "CheckBox", Box(Colors.Transparent, Space / 2, null, 0));
        t.SetStylebox("hover_pressed", "CheckBox", Box(Hover, Space / 2, null, 0));
        t.SetStylebox("disabled", "CheckBox", Box(Colors.Transparent, Space / 2, null, 0));

        // camouflage customiser sliders (#141): a dark track, an accent fill, and the same focus ring as the buttons
        t.SetStylebox("slider", "HSlider", Box(new Color(0, 0, 0, 0.55f), 0, Line, Border, 2));
        t.SetStylebox("grabber_area", "HSlider", Box(Accent, 0, null, 0, 2));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, 0, null, 0, 2));
        t.SetStylebox("focus", "HSlider", Ring(pad));
        t.SetStylebox("normal", "LineEdit", Box(Backdrop, pad, Line));
        t.SetStylebox("focus", "LineEdit", Box(Backdrop, pad, Accent, BorderStrong));
        t.SetStylebox("read_only", "LineEdit", Box(Surface, pad, Line));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", Dim);
        t.SetColor("caret_color", "LineEdit", Accent);
        t.SetColor("selection_color", "LineEdit", Active);
        t.SetFontSize("font_size", "LineEdit", FontBody);

        t.SetStylebox("slider", "HSlider", Box(Backdrop, 0, Line, Border, 3).With(b => b.ContentMarginTop = b.ContentMarginBottom = 4));
        t.SetStylebox("grabber_area", "HSlider", Box(Active, 0, null, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Accent, 0, null, 0, 3));
        t.SetStylebox("focus", "HSlider", Ring(2));
        t.SetConstant("center_grabber", "HSlider", 1);

        t.SetStylebox("panel", "PopupMenu", Box(Raised, Space, Accent));
        t.SetStylebox("hover", "PopupMenu", Box(Active, Space / 2, null, 0));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Text);
        t.SetFontSize("font_size", "PopupMenu", FontBody);

        PanelVariation(t, "MenuBackdrop", Box(Backdrop, 0, null, 0, 0));
        PanelVariation(t, "MenuCard", Box(Surface with { A = 0.94f }, Space * 3, Line));
        PanelVariation(t, "MenuToast", Box(Raised with { A = 0.97f }, Space * 2, Accent, BorderStrong));
        PanelVariation(t, "MenuToastWarn", Box(Raised with { A = 0.97f }, Space * 2, Warn, BorderStrong));
        PanelVariation(t, "MenuToastError", Box(Raised with { A = 0.97f }, Space * 2, Danger, BorderStrong));
        PanelVariation(t, "MenuRow", Box(Raised with { A = 0.6f }, Space, null, 0));
        t.SetStylebox("scroll", "VScrollBar", Box(new Color(0, 0, 0, 0.4f), 0, null, 0, 2).With(b => b.ContentMarginLeft = b.ContentMarginRight = 6));
        t.SetStylebox("grabber", "VScrollBar", Box(Line, 0, null, 0, 2));
        t.SetStylebox("grabber_highlight", "VScrollBar", Box(Accent, 0, null, 0, 2));
        t.SetStylebox("grabber_pressed", "VScrollBar", Box(Accent, 0, null, 0, 2));
        t.SetStylebox("focus", "ScrollContainer", new StyleBoxEmpty());
        Variation(t, "MenuError", FontBody, Danger);
        Variation(t, "MenuOk", FontBody, Good);
    }

    static void VariationButton(Theme t, string name, StyleBox normal, StyleBox hover, StyleBox pressed)
    {
        t.SetTypeVariation(name, "Button");
        t.SetStylebox("normal", name, normal);
        t.SetStylebox("hover", name, hover);
        t.SetStylebox("pressed", name, pressed);
        t.SetStylebox("hover_pressed", name, pressed);
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
