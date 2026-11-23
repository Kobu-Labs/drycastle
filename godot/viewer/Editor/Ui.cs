using Godot;
using System;

namespace Drycastle.Viewer.Editor;

/// <summary>Small control builders the sidebar pages share. Colours and sizes come from <see cref="EditorTheme"/>.</summary>
public static class Ui
{
    public static Button Btn(string text, Action onPress)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += onPress;
        return b;
    }

    /// <summary>A button that gives the focus back when pressed: Enter and Space belong to the viewport again.</summary>
    public static Button FocusBtn(string text, Action onPress, string? variation = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All };
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += () => { onPress(); b.ReleaseFocus(); };
        return b;
    }

    /// <summary>A dim, word-wrapped line of help text.</summary>
    public static Label Note(string text = "", string variation = "Dim")
        => new() { Text = text, ThemeTypeVariation = variation, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static HBoxContainer Row(string label, Control field, int labelWidth = 70)
    {
        var r = new HBoxContainer();
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(labelWidth, 0) };
        r.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        r.AddChild(field);
        return r;
    }

    public static SpinBox Spin(double min, double max, double step, string suffix = "")
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Suffix = suffix, AllowGreater = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        // after Enter in the field, release the focus so the next Enter applies the changes
        s.GetLineEdit().TextSubmitted += _ => s.GetLineEdit().ReleaseFocus();
        return s;
    }

    /// <summary>A small square of one colour with the theme's border, for lists.</summary>
    public static ImageTexture Swatch(string hex) => Swatch(Color.FromHtml(hex));

    public static ImageTexture Swatch(Color colour)
    {
        const int n = EditorTheme.Space * 3;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(colour);
        for (var i = 0; i < n; i++) foreach (var (x, y) in new[] { (i, 0), (i, n - 1), (0, i), (n - 1, i) }) img.SetPixel(x, y, EditorTheme.Border);
        return ImageTexture.CreateFromImage(img);
    }
}
