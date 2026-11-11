using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The two-option chooser opened by a tap of the interact key at a corpse (ticket #130): pick which hotbar position the
/// corpse's weapon replaces. Keyboard only, as the mouse is captured during play: 1 and 2 choose, Esc cancels. Esc is
/// consumed here so it never reaches the pause menu. Colours and sizes come from <see cref="ShooterTheme"/>.
/// </summary>
public sealed partial class CorpseChooser : CanvasLayer
{
    /// <summary>Raised with the 0-based hotbar position the player picked (0 primary, 1 secondary).</summary>
    public event Action<int>? Picked;

    readonly PanelContainer _panel = new();
    readonly Label _title = new();
    readonly VBoxContainer _rows = new();
    readonly List<int> _valid = new();

    public bool IsOpen { get; private set; }
    /// <summary>The corpse the chooser is about (valid while open).</summary>
    public ushort Corpse { get; private set; }

    public CorpseChooser()
    {
        Layer = 20;
        Visible = false;
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(center);
        _panel.AddThemeStyleboxOverride("panel", ShooterTheme.Box(ShooterTheme.Surface, ShooterTheme.Space * 2, ShooterTheme.Line));
        center.AddChild(_panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", ShooterTheme.Space);
        _panel.AddChild(column);
        Style(_title, ShooterTheme.FontHead, ShooterTheme.Text);
        column.AddChild(_title);
        column.AddChild(_rows);
        var hint = new Label { Text = Strings.F("corpse.cancel", "Esc") };
        Style(hint, ShooterTheme.FontSmall, ShooterTheme.Dim);
        column.AddChild(hint);
    }

    static void Style(Label l, int size, Color c)
    {
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", c);
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    /// <summary>Shows the chooser. <paramref name="options"/> holds (hotbar position, text) pairs; only those positions can be picked.</summary>
    public void Open(ushort corpse, string title, IReadOnlyList<(int Pos, string Text)> options)
    {
        Corpse = corpse;
        _valid.Clear();
        foreach (var child in _rows.GetChildren()) child.QueueFree();
        foreach (var (pos, text) in options)
        {
            _valid.Add(pos);
            var row = new Label { Text = Strings.F("corpse.option", pos + 1, text) };
            Style(row, ShooterTheme.FontBody, ShooterTheme.Text);
            _rows.AddChild(row);
        }
        _title.Text = title;
        IsOpen = true;
        Visible = true;
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Visible = false;
        _valid.Clear();
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        int pos;
        switch (k.Keycode)
        {
            case Key.Key1: pos = 0; break;
            case Key.Key2: pos = 1; break;
            case Key.Escape: Close(); GetViewport().SetInputAsHandled(); return;
            default: return;
        }
        GetViewport().SetInputAsHandled();
        if (!_valid.Contains(pos)) return;
        Picked?.Invoke(pos);
    }
}
