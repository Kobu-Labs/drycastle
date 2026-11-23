using Godot;
using System;

namespace Drycastle.Viewer.Editor;

/// <summary>The confirm dialog: every destructive action names what is lost; Cancel is the default, Esc cancels.</summary>
public sealed class ConfirmDialog
{
    PanelContainer _panel = null!;
    Label _title = null!, _body = null!;
    Button _ok = null!, _cancel = null!;
    Action? _onOk;

    public bool Visible => _panel.Visible;
    public string Text => _title.Text + "\n" + _body.Text;
    public bool Contains(Vector2 point) => _panel.Visible && _panel.GetGlobalRect().HasPoint(point);

    public void Show(string title, string body, string okText, Action onOk)
    {
        _title.Text = title; _body.Text = body; _ok.Text = okText; _onOk = onOk;
        _panel.Visible = true;
        _cancel.GrabFocus(); // safe default: Enter cancels
    }

    public void Accept()
    {
        if (!_panel.Visible) return;
        var a = _onOk;
        Cancel();
        a?.Invoke();
    }

    public void Cancel()
    {
        _panel.Visible = false; _onOk = null;
        _panel.GetViewport().GuiReleaseFocus();
    }

    public void Build(Control root)
    {
        _panel = new PanelContainer { Name = "Confirm", Visible = false };
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Both;
        _panel.CustomMinimumSize = new Vector2(EditorTheme.Space * 50, 0);
        root.AddChild(_panel);
        var box = new VBoxContainer();
        _panel.AddChild(box);
        _title = new Label { ThemeTypeVariation = "Title" };
        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Dim" };
        box.AddChild(_title);
        box.AddChild(_body);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _cancel = new Button { Text = "Cancel  (Esc)", FocusMode = Control.FocusModeEnum.All };
        _cancel.Pressed += Cancel;
        _ok = new Button { Text = "Delete", ThemeTypeVariation = "DangerButton", FocusMode = Control.FocusModeEnum.All };
        _ok.Pressed += Accept;
        row.AddChild(_cancel);
        row.AddChild(_ok);
        box.AddChild(row);
    }
}
