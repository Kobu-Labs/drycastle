using Godot;


/// <summary>
/// The editor's one Theme and its tokens (docs/standards/ui-ux.md section 4). Screens use these constants and the
/// theme type variations ("Header", "Dim", "RailButton"); no per-control colour or font-size overrides.
/// </summary>
public static class EditorTheme
{
    // sizes (8 px grid)
    public const int Space = 8;
    public const int Radius = 4;
    public const int RailButton = 48;
    public const int RailWidth = RailButton + 2 * Space;

    // text sizes (three)
    public const int FontBody = 16, FontHead = 18, FontTitle = 20;

    // colours
    public static readonly Color Surface = new(0.09f, 0.10f, 0.13f, 0.96f);
    public static readonly Color Raised = new(0.16f, 0.18f, 0.23f);
    public static readonly Color Hover = new(0.23f, 0.26f, 0.33f);
    public static readonly Color Active = new(0.10f, 0.34f, 0.48f);
    public static readonly Color Border = new(0.32f, 0.35f, 0.43f);
    public static readonly Color Text = new(0.94f, 0.95f, 0.97f);
    public static readonly Color Dim = new(0.70f, 0.73f, 0.79f);
    public static readonly Color Accent = new(0.40f, 0.82f, 1f);
    public static readonly Color Warn = new(1f, 0.68f, 0.25f);
    public static readonly Color Danger = new(1f, 0.50f, 0.45f);
    public static readonly Color Scrim = new(0f, 0f, 0f, 0.75f);

    public static StyleBoxFlat Box(Color bg, int pad = Space, Color? border = null, int radius = Radius)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetContentMarginAll(pad);
        s.SetCornerRadiusAll(radius);
        if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(1); }
        return s;
    }

    static StyleBoxFlat Focus(int radius = Radius)
    {
        var s = new StyleBoxFlat { DrawCenter = false, BorderColor = Accent };
        s.SetBorderWidthAll(2);
        s.SetCornerRadiusAll(radius);
        return s;
    }

    static Theme? _theme;

    public static Theme Build()
    {
        if (_theme != null) return _theme;
        var t = new Theme { DefaultFontSize = FontBody };

        foreach (var type in new[] { "Button", "OptionButton", "ColorPickerButton", "MenuButton" })
        {
            t.SetStylebox("normal", type, Box(Raised, 8, Border));
            t.SetStylebox("hover", type, Box(Hover, 8, Border));
            t.SetStylebox("pressed", type, Box(Active, 8, Accent));
            t.SetStylebox("hover_pressed", type, Box(Active, 8, Accent));
            t.SetStylebox("disabled", type, Box(new Color(Raised, 0.45f), 8));
            t.SetStylebox("focus", type, Focus());
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Colors.White);
            t.SetColor("font_pressed_color", type, Colors.White);
            t.SetColor("font_hover_pressed_color", type, Colors.White);
            t.SetColor("font_focus_color", type, Text);
            t.SetColor("font_disabled_color", type, new Color(Dim, 0.55f));
            t.SetFontSize("font_size", type, FontBody);
        }

        // rail buttons: square, icon only
        t.SetTypeVariation("RailButton", "Button");
        t.SetStylebox("normal", "RailButton", Box(Colors.Transparent, 0));
        t.SetStylebox("hover", "RailButton", Box(Hover, 0));
        t.SetStylebox("pressed", "RailButton", Box(Active, 0, Accent));
        t.SetStylebox("hover_pressed", "RailButton", Box(Active, 0, Accent));
        t.SetStylebox("disabled", "RailButton", Box(Colors.Transparent, 0));

        // section header buttons
        t.SetTypeVariation("FlatButton", "Button");
        t.SetStylebox("normal", "FlatButton", Box(Colors.Transparent, 4));
        t.SetStylebox("hover", "FlatButton", Box(Hover, 4));
        t.SetStylebox("pressed", "FlatButton", Box(Hover, 4));
        t.SetStylebox("hover_pressed", "FlatButton", Box(Hover, 4));
        t.SetColor("font_color", "FlatButton", Accent);
        t.SetColor("font_hover_color", "FlatButton", Accent);
        t.SetColor("font_pressed_color", "FlatButton", Accent);

        t.SetStylebox("panel", "PanelContainer", Box(Surface, Space, null, 0));
        t.SetStylebox("normal", "LineEdit", Box(Raised, 8, Border));
        t.SetStylebox("focus", "LineEdit", Focus());
        t.SetStylebox("read_only", "LineEdit", Box(new Color(Raised, 0.45f), 8));
        t.SetColor("font_color", "LineEdit", Text);

        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", FontBody);
        t.SetTypeVariation("Header", "Label");
        t.SetColor("font_color", "Header", Accent);
        t.SetFontSize("font_size", "Header", FontHead);
        t.SetTypeVariation("Title", "Label");
        t.SetFontSize("font_size", "Title", FontTitle);
        t.SetTypeVariation("Dim", "Label");
        t.SetColor("font_color", "Dim", Dim);
        t.SetTypeVariation("Warn", "Label");
        t.SetColor("font_color", "Warn", Warn);

        t.SetColor("font_color", "CheckBox", Text);
        t.SetColor("font_hover_color", "CheckBox", Colors.White);
        t.SetStylebox("focus", "CheckBox", Focus());
        t.SetConstant("separation", "VBoxContainer", Space);
        t.SetConstant("separation", "HBoxContainer", Space);
        _theme = t;
        return t;
    }
}
