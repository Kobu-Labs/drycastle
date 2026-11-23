using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>The Ctrl+P command palette: type to filter the commands the editor offers (tools, actions, ground types, patch, zone and parcel IDs).</summary>
public sealed class CommandPalette
{
    const int MaxItems = 8;

    readonly Func<string, IEnumerable<(string label, Action run)>> _commands;
    PanelContainer _panel = null!;
    LineEdit _edit = null!;
    VBoxContainer _list = null!;
    readonly List<(string label, Action run)> _items = new();
    int _index;

    /// <param name="commands">Every command for the typed filter (the filter is also applied here; it only lets the source offer more, like parcel IDs, for longer text).</param>
    public CommandPalette(Func<string, IEnumerable<(string label, Action run)>> commands) => _commands = commands;

    public bool Visible => _panel.Visible;
    public IReadOnlyList<string> Labels => _items.Select(i => i.label).ToList();
    public bool Contains(Vector2 point) => _panel.Visible && _panel.GetGlobalRect().HasPoint(point);

    public void Build(Control root)
    {
        _panel = new PanelContainer { Name = "Palette", Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.OffsetTop = EditorTheme.Space * 8;
        _panel.CustomMinimumSize = new Vector2(EditorTheme.Space * 64, 0);
        root.AddChild(_panel);
        var box = new VBoxContainer();
        _panel.AddChild(box);
        box.AddChild(new Label { Text = "Commands", ThemeTypeVariation = "Header" });
        _edit = new LineEdit { PlaceholderText = "Type a command, a patch (GP-0001), a zone (Z-0001), a sketch mark (L1) or a parcel ID (P-0412)", ClearButtonEnabled = true };
        _edit.TextChanged += _ => { _index = 0; Rebuild(); };
        _edit.TextSubmitted += _ => Run(_index);
        _edit.GuiInput += e =>
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            if (k.Keycode == Key.Escape) { Close(); _edit.AcceptEvent(); }
            else if (k.Keycode == Key.Down) { _index = Math.Min(_index + 1, Math.Max(0, _items.Count - 1)); Highlight(); _edit.AcceptEvent(); }
            else if (k.Keycode == Key.Up) { _index = Math.Max(_index - 1, 0); Highlight(); _edit.AcceptEvent(); }
        };
        box.AddChild(_edit);
        _list = new VBoxContainer();
        box.AddChild(_list);
        box.AddChild(new Label { ThemeTypeVariation = "Dim", Text = "Up and Down choose, Enter runs, Esc closes." });
    }

    public void Open()
    {
        _panel.Visible = true;
        _edit.Text = "";
        _index = 0;
        Rebuild();
        _edit.GrabFocus();
    }

    public void Close()
    {
        _panel.Visible = false;
        _panel.GetViewport().GuiReleaseFocus();
    }

    public void SetFilter(string text) { _edit.Text = text; _index = 0; Rebuild(); }

    void Rebuild()
    {
        var toks = _edit.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _items.Clear();
        foreach (var it in _commands(_edit.Text.Trim()))
        {
            var l = it.label.ToLowerInvariant();
            if (toks.All(l.Contains)) _items.Add(it);
            if (_items.Count >= MaxItems) break;
        }
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        if (_items.Count == 0) _list.AddChild(new Label { Text = "Nothing matches. Try a tool, a patch or zone ID or a parcel ID.", ThemeTypeVariation = "Dim" });
        for (var i = 0; i < _items.Count; i++)
        {
            var idx = i;
            var b = new Button { Text = _items[i].label, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += () => Run(idx);
            _list.AddChild(b);
        }
        Highlight();
    }

    void Highlight()
    {
        for (var i = 0; i < _list.GetChildCount() && i < _items.Count; i++)
            if (_list.GetChild(i) is Button b) b.SetPressedNoSignal(i == _index);
    }

    public bool Run(int index)
    {
        if (index < 0 || index >= _items.Count) return false;
        var run = _items[index].run;
        Close();
        run();
        return true;
    }
}
