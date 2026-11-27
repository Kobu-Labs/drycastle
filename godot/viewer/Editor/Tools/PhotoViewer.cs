using Godot;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// A picture of the Photo to building tool shown large over the 3D view (a capture, an overlay, a comparison). A click, Esc or Enter closes it; the arrow keys step through
/// the pictures of the same list. It is a modal of the editor (the keys and the mouse belong to it while it is open).
/// </summary>
public sealed class PhotoViewer
{
    Control _root = null!;
    TextureRect _pic = null!;
    Label _caption = null!;
    List<(string Path, string Caption)> _items = new();
    int _at;

    public bool Visible => _root.Visible;

    public void Build(Control parent)
    {
        _root = new Control { Name = "PhotoViewer", Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        parent.AddChild(_root);
        var scrim = new ColorRect { Color = EditorTheme.Scrim, MouseFilter = Control.MouseFilterEnum.Ignore };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(scrim);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        box.OffsetLeft = box.OffsetTop = EditorTheme.Space * 2;
        box.OffsetRight = box.OffsetBottom = -EditorTheme.Space * 2;
        _root.AddChild(box);
        _caption = new Label { ThemeTypeVariation = "Title", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_caption);
        _pic = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        box.AddChild(_pic);
        box.AddChild(new Label { Text = "Click, Esc or Enter closes  |  Left and Right step through the pictures", ThemeTypeVariation = "Dim", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore });
        _root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) Close(); };
    }

    public void Open(IReadOnlyList<(string Path, string Caption)> items, int at)
    {
        _items = new List<(string, string)>(items);
        _at = Mathf.Clamp(at, 0, _items.Count - 1);
        if (_items.Count == 0) return;
        Show();
    }

    void Show()
    {
        var (path, caption) = _items[_at];
        _pic.Texture = PhotoFiles.Load(path, 0);
        _caption.Text = _pic.Texture == null ? $"{caption}: the picture cannot be read" : $"{caption}   ({_at + 1} of {_items.Count})";
        _root.Visible = true;
    }

    public void Close()
    {
        _root.Visible = false;
        _pic.Texture = null;
    }

    /// <summary>A key while the picture is open: Left and Right step, Esc and Enter close. Every key is used up.</summary>
    public void OnKey(InputEventKey k)
    {
        switch (k.Keycode)
        {
            case Key.Escape or Key.Enter or Key.KpEnter: Close(); break;
            case Key.Left when _items.Count > 1: _at = (_at + _items.Count - 1) % _items.Count; Show(); break;
            case Key.Right when _items.Count > 1: _at = (_at + 1) % _items.Count; Show(); break;
        }
    }
}
