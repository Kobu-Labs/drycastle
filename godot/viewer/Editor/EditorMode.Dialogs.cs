using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The two modal pieces of the editor: the confirm dialog (every destructive action names what is lost; Cancel is the default,
/// Esc cancels) and the Ctrl+P command palette (tools, actions, ground types, ground patch and zone IDs, parcel IDs).
/// </summary>
public partial class EditorMode
{
    // ---- delete confirmation ----

    PanelContainer _confirm = null!;
    Label _confirmTitle = null!, _confirmBody = null!;
    Button _confirmOk = null!, _confirmCancel = null!;
    Action? _confirmAction;

    public bool ConfirmVisible => _confirm.Visible;
    public string ConfirmText => _confirmTitle.Text + "\n" + _confirmBody.Text;
    bool Modal => _confirm.Visible || _palette.Visible;

    void ShowConfirm(string title, string body, string okText, Action onOk)
    {
        _confirmTitle.Text = title; _confirmBody.Text = body; _confirmOk.Text = okText; _confirmAction = onOk;
        _confirm.Visible = true;
        _confirmCancel.GrabFocus(); // safe default: Enter cancels
    }

    public void ConfirmAccept()
    {
        if (!_confirm.Visible) return;
        var a = _confirmAction;
        ConfirmCancel();
        a?.Invoke();
    }

    public void ConfirmCancel()
    {
        _confirm.Visible = false; _confirmAction = null;
        GetViewport().GuiReleaseFocus();
    }


    // ---- confirm dialog and palette UI ----

    PanelContainer _palette = null!;
    LineEdit _palEdit = null!;
    VBoxContainer _palList = null!;
    Label _palHint = null!;
    readonly List<(string label, Action run)> _palItems = new();
    int _palIndex;

    void BuildDialogs(Control root)
    {
        _confirm = new PanelContainer { Name = "Confirm", Visible = false };
        _confirm.SetAnchorsPreset(Control.LayoutPreset.Center);
        _confirm.GrowHorizontal = Control.GrowDirection.Both;
        _confirm.GrowVertical = Control.GrowDirection.Both;
        _confirm.CustomMinimumSize = new Vector2(EditorTheme.Space * 50, 0);
        root.AddChild(_confirm);
        var box = new VBoxContainer();
        _confirm.AddChild(box);
        _confirmTitle = new Label { ThemeTypeVariation = "Title" };
        _confirmBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Dim" };
        box.AddChild(_confirmTitle);
        box.AddChild(_confirmBody);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _confirmCancel = new Button { Text = "Cancel  (Esc)", FocusMode = Control.FocusModeEnum.All };
        _confirmCancel.Pressed += ConfirmCancel;
        _confirmOk = new Button { Text = "Delete", ThemeTypeVariation = "DangerButton", FocusMode = Control.FocusModeEnum.All };
        _confirmOk.Pressed += ConfirmAccept;
        row.AddChild(_confirmCancel);
        row.AddChild(_confirmOk);
        box.AddChild(row);

        _palette = new PanelContainer { Name = "Palette", Visible = false };
        _palette.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _palette.GrowHorizontal = Control.GrowDirection.Both;
        _palette.OffsetTop = EditorTheme.Space * 8;
        _palette.CustomMinimumSize = new Vector2(EditorTheme.Space * 64, 0);
        root.AddChild(_palette);
        var pb = new VBoxContainer();
        _palette.AddChild(pb);
        pb.AddChild(new Label { Text = "Commands", ThemeTypeVariation = "Header" });
        _palEdit = new LineEdit { PlaceholderText = "Type a command, a patch (GP-0001), a zone (Z-0001), a sketch mark (L1) or a parcel ID (P-0412)", ClearButtonEnabled = true };
        _palEdit.TextChanged += _ => { _palIndex = 0; PaletteRebuild(); };
        _palEdit.TextSubmitted += _ => PaletteRun(_palIndex);
        _palEdit.GuiInput += e =>
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            if (k.Keycode == Key.Escape) { ClosePalette(); _palEdit.AcceptEvent(); }
            else if (k.Keycode == Key.Down) { _palIndex = Math.Min(_palIndex + 1, Math.Max(0, _palItems.Count - 1)); PaletteHighlight(); _palEdit.AcceptEvent(); }
            else if (k.Keycode == Key.Up) { _palIndex = Math.Max(_palIndex - 1, 0); PaletteHighlight(); _palEdit.AcceptEvent(); }
        };
        pb.AddChild(_palEdit);
        _palList = new VBoxContainer();
        pb.AddChild(_palList);
        _palHint = new Label { ThemeTypeVariation = "Dim", Text = "Up and Down choose, Enter runs, Esc closes." };
        pb.AddChild(_palHint);
    }

    public bool PaletteVisible => _palette.Visible;
    public IReadOnlyList<string> PaletteLabels => _palItems.Select(i => i.label).ToList();

    public void OpenPalette()
    {
        _palette.Visible = true;
        _palEdit.Text = "";
        _palIndex = 0;
        PaletteRebuild();
        _palEdit.GrabFocus();
    }

    public void ClosePalette()
    {
        _palette.Visible = false;
        GetViewport().GuiReleaseFocus();
    }

    public void PaletteSetFilter(string text) { _palEdit.Text = text; _palIndex = 0; PaletteRebuild(); }

    IEnumerable<(string label, Action run)> PaletteAll(string filter)
    {
        foreach (var (tool, _, name, key) in Tools) { var t = tool; yield return ($"Tool: {name}  ({OS.GetKeycodeString(key)})", () => SelectTool(t)); }
        foreach (var g in GroundRows()) { var gn = g.Name; yield return ($"{g.Name}  ground type  {RecipeWord(g)}  {StateWord(g.State)}", () => { SelectTool(EditorTool.Ground); SelectGroundType(gn); }); }
        yield return ("Ground: draw a patch of the selected type  (N)", () => { SelectTool(EditorTool.Ground); StartShape(); });
        yield return (_gmOn ? "Ground: hide the ground map  (M)" : "Ground: show the ground map  (M)", () => { SelectTool(EditorTool.Ground); ToggleGroundMap(); });
        yield return ("Zones: draw a zone of the selected kind  (N)", () => { SelectTool(EditorTool.Zones); StartShape(); });
        if (MarkedZoneCount > 0)
        {
            yield return ($"Zones: delete the {MarkedZoneCount} marked zones  (Shift+Del)", () => { SelectTool(EditorTool.Zones); RequestDeleteMarkedZones(); });
            yield return ("Zones: unmark all zones", () => { SelectTool(EditorTool.Zones); ClearZoneMarks(); });
        }
        yield return (SelectedZoneKind != null ? $"Zones: mark all {ZoneLabel(SelectedZoneKind)} zones for deletion" : "Zones: mark all zones for deletion", () => { SelectTool(EditorTool.Zones); MarkListedZones(); });
        yield return (_zoOn ? "Zones: hide the zone overlay  (M)" : "Zones: show the zone overlay  (M)", () => { SelectTool(EditorTool.Zones); ToggleZoneOverlay(); });
        yield return (_floorParcel != null ? $"View: floor plan off, show the building of {_floorParcel}  (B)" : "View: floor plan of the selected parcel  (B)", ToggleFloorPlan);
        yield return ("Sketch: mark points  (P)", () => { SelectTool(EditorTool.Sketch); SetSketchMode(SketchKind.Point); });
        yield return ("Sketch: draw a line  (L)", () => { SelectTool(EditorTool.Sketch); SetSketchMode(SketchKind.Line); });
        if (_sketch.Count > 0) yield return ("Sketch: clear the active sketch", () => { SelectTool(EditorTool.Sketch); RequestClearSketch(); });
        yield return ("Poles: plan a line  (P)", () => { SelectTool(EditorTool.Poles); StartPlanning(); });
        yield return ("Poles: connect  (C)", () => { SelectTool(EditorTool.Poles); StartConnecting(); });
        yield return ("Edit: undo  (Ctrl+Z)", () => Undo());
        yield return ("Edit: redo  (Ctrl+Y)", () => Redo());
        yield return ("View: top-down  (T)", ToggleTopDown);
        yield return ("Walk: drop to the ground here  (F3)", WalkFromHere);
        yield return ("Help: controls  (F1)", ToggleCheatSheet);
        foreach (var r in Patches()) { var id = r.Id; yield return ($"{r.Id}  ground patch  {r.Type}  {r.Points.Count} corners", () => { SelectTool(EditorTool.Ground); SelectShape(ShapeKind.Patch, id); }); }
        foreach (var z in Zones()) { var id = z.Id; yield return ($"{z.Label}  zone  {ZoneLabel(z.Kind)}  {z.Points.Count} corners", () => { SelectTool(EditorTool.Zones); SelectShape(ShapeKind.Zone, id); }); }
        foreach (var k in Map.ZoneKinds) { var n = k.Name; yield return ($"{k.Label}  zone kind", () => { SelectTool(EditorTool.Zones); SelectZoneKind(n); }); }
        foreach (var p in _sketch.Points) { var id = p.Id; yield return ($"{p.Id}  sketch point", () => { SelectTool(EditorTool.Sketch); SelectSketchItem(id); }); }
        foreach (var l in _sketch.Lines) { var id = l.Id; yield return ($"{l.Id}  sketch {(l.Closed ? "closed line" : "line")}  {l.Points.Count} corners", () => { SelectTool(EditorTool.Sketch); SelectSketchItem(id); }); }
        if (filter.Length >= 2)
            foreach (var p in Map.Parcels.Values.OrderBy(p => p.Id, StringComparer.Ordinal)) { var id = p.Id; yield return ($"{p.Id}  parcel  {p.Kind}", () => { SelectTool(EditorTool.Select); Click(id, false); }); }
    }

    void PaletteRebuild()
    {
        var toks = _palEdit.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _palItems.Clear();
        foreach (var it in PaletteAll(_palEdit.Text.Trim()))
        {
            var l = it.label.ToLowerInvariant();
            if (toks.All(l.Contains)) _palItems.Add(it);
            if (_palItems.Count >= 8) break;
        }
        foreach (var c in _palList.GetChildren()) { _palList.RemoveChild(c); c.QueueFree(); }
        if (_palItems.Count == 0) _palList.AddChild(new Label { Text = "Nothing matches. Try a tool, a patch or zone ID or a parcel ID.", ThemeTypeVariation = "Dim" });
        for (var i = 0; i < _palItems.Count; i++)
        {
            var idx = i;
            var b = new Button { Text = _palItems[i].label, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += () => PaletteRun(idx);
            _palList.AddChild(b);
        }
        PaletteHighlight();
    }

    void PaletteHighlight()
    {
        for (var i = 0; i < _palList.GetChildCount() && i < _palItems.Count; i++)
            if (_palList.GetChild(i) is Button b) b.SetPressedNoSignal(i == _palIndex);
    }

    public bool PaletteRun(int index)
    {
        if (index < 0 || index >= _palItems.Count) return false;
        var run = _palItems[index].run;
        ClosePalette();
        run();
        return true;
    }
}
