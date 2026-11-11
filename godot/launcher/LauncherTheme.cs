using Godot;

namespace Suchohrad.Launcher;

/// <summary>The launcher's one Theme and tokens (docs/standards/ui-ux.md section 4): 8 px grid, five text sizes, no per-control overrides in screens.</summary>
public static class LauncherTheme
{
    public const int Space = 8, Radius = 4, Border = 1, BorderStrong = 3;
    public const int FontSmall = 16, FontBody = 20, FontHead = 24, FontBig = 32, FontDisplay = 48;
    public const int ButtonHeight = Space * 7;

    public static readonly Color Backdrop = new(0.05f, 0.06f, 0.09f);
    public static readonly Color Surface = new(0.09f, 0.10f, 0.14f);
    public static readonly Color Raised = new(0.14f, 0.16f, 0.22f);
    public static readonly Color Hover = new(0.19f, 0.23f, 0.31f);
    public static readonly Color Pressed = new(0.10f, 0.34f, 0.48f);
    public static readonly Color Line = new(0.45f, 0.49f, 0.58f);
    public static readonly Color Text = new(0.96f, 0.97f, 0.98f);
    public static readonly Color Dim = new(0.76f, 0.79f, 0.85f);
    public static readonly Color Disabled = new(0.58f, 0.61f, 0.68f);
    public static readonly Color Accent = new(0.40f, 0.82f, 1f);
    public static readonly Color AccentText = new(0.03f, 0.08f, 0.12f);
    public static readonly Color Danger = new(1f, 0.62f, 0.30f);   // orange, not red: readable with red-green colour blindness

    static Theme? _theme;

    static StyleBoxFlat Box(Color bg, Color? border = null, int borderWidth = Border, int padX = Space * 2, int padY = Space)
    {
        var s = new StyleBoxFlat { BgColor = bg, ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY };
        s.SetCornerRadiusAll(Radius);
        if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(borderWidth); }
        return s;
    }

    static StyleBoxFlat Ring() => new StyleBoxFlat { DrawCenter = false, BorderColor = Accent, ContentMarginLeft = Space * 2, ContentMarginRight = Space * 2, ContentMarginTop = Space, ContentMarginBottom = Space }
        .With(b => { b.SetBorderWidthAll(BorderStrong); b.SetCornerRadiusAll(Radius); });

    static StyleBoxFlat With(this StyleBoxFlat s, Action<StyleBoxFlat> f) { f(s); return s; }

    public static Theme Build()
    {
        if (_theme != null) return _theme;
        var t = _theme = new Theme { DefaultFontSize = FontBody };
        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", FontBody);
        Label(t, "Dim", FontBody, Dim);
        Label(t, "Small", FontSmall, Dim);
        Label(t, "Head", FontHead, Text);
        Label(t, "Display", FontDisplay, Text);
        Label(t, "Error", FontBody, Danger);
        t.SetStylebox("panel", "PanelContainer", Box(Surface, Line, Border, Space * 4, Space * 4));

        foreach (var type in new[] { "Button", "OptionButton", "CheckBox" })
        {
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Text);
            t.SetColor("font_pressed_color", type, Text);
            t.SetColor("font_focus_color", type, Text);
            t.SetColor("font_disabled_color", type, Disabled);
            t.SetFontSize("font_size", type, FontBody);
        }
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal", type, Box(Raised, Line));
            t.SetStylebox("hover", type, Box(Hover, Line));
            t.SetStylebox("pressed", type, Box(Pressed, Accent));
            t.SetStylebox("focus", type, Ring());
            t.SetStylebox("disabled", type, Box(Surface, Line));
        }
        // primary action: filled accent, dark text (contrast > 4.5)
        t.SetTypeVariation("Primary", "Button");
        t.SetStylebox("normal", "Primary", Box(Accent));
        t.SetStylebox("hover", "Primary", Box(new Color(0.60f, 0.90f, 1f)));
        t.SetStylebox("pressed", "Primary", Box(new Color(0.28f, 0.68f, 0.90f)));
        t.SetStylebox("focus", "Primary", Ring().With(b => { b.BorderColor = Text; }));
        t.SetStylebox("disabled", "Primary", Box(Raised));
        foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" }) t.SetColor(c, "Primary", AccentText);
        t.SetColor("font_disabled_color", "Primary", Disabled);
        t.SetFontSize("font_size", "Primary", FontHead);

        t.SetStylebox("background", "ProgressBar", Box(Raised, Line, Border, 0, Space));
        t.SetStylebox("fill", "ProgressBar", Box(Accent, null, 0, 0, Space));
        t.SetColor("font_color", "ProgressBar", Text);
        t.SetFontSize("font_size", "ProgressBar", FontSmall);
        t.SetStylebox("panel", "PopupMenu", Box(Surface, Line));
        t.SetStylebox("hover", "PopupMenu", Box(Hover));
        t.SetFontSize("font_size", "PopupMenu", FontBody);
        return t;
    }

    static void Label(Theme t, string name, int size, Color color)
    {
        t.SetTypeVariation(name, "Label");
        t.SetFontSize("font_size", name, size);
        t.SetColor("font_color", name, color);
    }
}
