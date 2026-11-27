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

    /// <summary>Shows "Copied" on the button for a moment, then its own text again.</summary>
    public static void FlashCopied(Button b)
    {
        var text = b.HasMeta("flash_text") ? (string)b.GetMeta("flash_text") : b.Text;
        b.SetMeta("flash_text", text);
        b.SetMeta("flash_until", (long)Time.GetTicksMsec() + 1500);
        b.Text = "Copied";
        b.GetTree().CreateTimer(1.5).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(b) || Flashing(b)) return;
            b.Text = (string)b.GetMeta("flash_text");
            b.RemoveMeta("flash_text");
        };
    }

    /// <summary>The button shows "Copied" now (its text must not be overwritten).</summary>
    public static bool Flashing(Button b) => b.HasMeta("flash_until") && (long)Time.GetTicksMsec() < (long)b.GetMeta("flash_until");

    /// <summary>A button that gives the focus back when pressed: Enter and Space belong to the viewport again.</summary>
    public static Button FocusBtn(string text, Action onPress, string? variation = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All };
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += () => { onPress(); b.ReleaseFocus(); };
        return b;
    }

    /// <summary>A square rail-style button with a drawn icon; it gives the focus back when pressed (Space and Enter fly and apply in the viewport).</summary>
    public static Button IconBtn(string icon, string tip, Action onPress, bool toggle = false, int size = EditorTheme.RailButton)
    {
        var b = new Button
        {
            CustomMinimumSize = new Vector2(size, size), ToggleMode = toggle, TooltipText = tip,
            ThemeTypeVariation = "RailButton", FocusMode = Control.FocusModeEnum.All,
        };
        b.AddChild(new IconGlyph { Kind = icon, Owner = b });
        b.Pressed += () => { onPress(); b.ReleaseFocus(); };
        return b;
    }

    /// <summary>The small "back to default" button next to a field; shown only while the field is overridden.</summary>
    public static Button ResetBtn(Action reset)
    {
        var b = IconBtn("reset", "Back to the default", reset, size: EditorTheme.Space * 4);
        b.Visible = false;
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

    /// <summary>A label of fixed width and its field, for the forms of the Ground and Zones pages.</summary>
    public static HBoxContainer FieldRow(string label, Control field, string? tip = null)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, TooltipText = tip ?? "", CustomMinimumSize = new Vector2(EditorTheme.Space * 12, 0) });
        row.AddChild(field);
        return row;
    }

    /// <summary>A number field whose Esc leaves it (one level up); <paramref name="onEscape"/> runs after it let go (the page is redrawn).</summary>
    public static SpinBox NumberField(double min, double max, double step, string tip, Action onEscape)
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tip, Rounded = false, Alignment = HorizontalAlignment.Right };
        s.GetLineEdit().GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { s.GetLineEdit().ReleaseFocus(); s.GetLineEdit().AcceptEvent(); onEscape(); } };
        return s;
    }

    /// <summary>Up and Down in the number field's text step it (Shift: five steps), PageUp and PageDown ten; the value changes as if the arrows of the box were pressed.</summary>
    public static SpinBox Nudging(SpinBox s)
    {
        s.GetLineEdit().GuiInput += e =>
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            var dir = k.Keycode switch { Key.Up => 1, Key.Down => -1, Key.Pageup => 10, Key.Pagedown => -10, _ => 0 };
            if (dir == 0) return;
            s.Value = Math.Round(s.Value + dir * s.Step * (k.ShiftPressed ? 5 : 1), 4);
            s.GetLineEdit().AcceptEvent();
        };
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
