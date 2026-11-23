using Drycastle.Viewer.Edits;
using Godot;
using System.Collections.Generic;
using System.Linq;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Zones page of the sidebar: the overlay toggle, the list of zone kinds (swatch = tint, label, how many zones), the zone form
/// (change kind, delete), the marks of the mass delete and the list of zones (the draw action, the delete button and the list itself
/// are the shape editor's, <see cref="ShapePanel"/>). Every control calls <see cref="ZonesTool"/>, <see cref="ZoneOverlay"/> or <see cref="ZoneMarks"/>.
/// </summary>
public sealed class ZonesPanel
{
    /// <summary>Rows of the zone list at most (a map has thousands of zones; picking a kind narrows the list).</summary>
    const int ListCap = 300;

    readonly IEditorHost _host;
    readonly ZonesTool _tool;

    VBoxContainer _kinds = null!;
    Label _error = null!, _kindsHead = null!;
    OptionButton _kindOpt = null!;
    HBoxContainer _kindRow = null!;
    bool _sync;
    string _kindsSig = "\0", _kindOptSig = "\0", _scrolledFor = "";
    readonly Dictionary<string, Button> _kindRows = new();

    Label _markHead = null!;
    Button _markAll = null!, _markNone = null!, _markDelete = null!;

    public ZonesPanel(IEditorHost host, ZonesTool tool)
    {
        _host = host;
        _tool = tool;
    }

    /// <summary>Tab with nothing focused: the selected kind in the list, else the first (the keyboard way into the panel).</summary>
    public void FocusList()
    {
        (_tool.SelectedKind != null && _kindRows.TryGetValue(_tool.SelectedKind, out var b) ? b : _kindRows.Values.FirstOrDefault())?.GrabFocus();
    }

    // ---- build ----

    public void Build(VBoxContainer page)
    {
        var shapes = _host.Shapes;
        page.AddChild(new Label { Text = "Zones", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Note("A zone marks what an area means in play (Bigfoot, deer, player spawn, ...). It never changes how the ground looks, and zones overlap freely. Pick a kind to show its zones alone, with moving bands; click any zone to select it, then drag its corners, change its kind or delete it. N draws a new one."));
        _error = Ui.Note("", "Danger");
        page.AddChild(_error);
        _tool.Overlay.BuildToggle(page);

        _kindsHead = new Label { Text = "Zone kinds", ThemeTypeVariation = "Header" };
        page.AddChild(_kindsHead);
        _kinds = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_kinds);

        page.AddChild(new HSeparator());
        _kindOpt = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _kindOpt.ItemSelected += i =>
        {
            if (!_sync && shapes.SelectedZoneId != null && i >= 0 && i < _host.Map.ZoneKinds.Count) _tool.SetKind(shapes.SelectedZoneId, _host.Map.ZoneKinds[(int)i].Name);
            _kindOpt.ReleaseFocus();
        };
        _kindRow = Ui.FieldRow("Kind", _kindOpt, "The zone kind of the selected zone");
        shapes.Panel.BuildActions(page, ShapeKind.Zone, _kindRow);
        shapes.Panel.BuildDelete(page, ShapeKind.Zone);
        BuildMarks(page);
        shapes.Panel.BuildList(page, ShapeKind.Zone, "All zones", "No zones to list. Pick a kind, press N, click corners on the ground, Enter creates the zone.");
        page.AddChild(Ui.Note("Zones overlap freely and add up. Esc steps back: cancel drawing, deselect the zone, deselect the kind (all kinds show again), leave the tool."));
    }

    void BuildMarks(VBoxContainer page)
    {
        var marks = _tool.Marks;
        page.AddChild(new HSeparator());
        _markHead = new Label { ThemeTypeVariation = "Header" };
        page.AddChild(_markHead);
        page.AddChild(Ui.Note("Delete many zones at once: tick them in the list below, Ctrl+click them on the map, or press X for the selected one. Marked zones are outlined in red on the map."));
        var row = new HBoxContainer();
        _markAll = Ui.FocusBtn("", marks.MarkListed);
        _markAll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _markAll.ClipText = true;
        _markNone = Ui.FocusBtn("Unmark all", marks.Clear);
        _markNone.TooltipText = "Take every mark back. Nothing is deleted.";
        row.AddChild(_markAll); row.AddChild(_markNone);
        page.AddChild(row);
        _markDelete = Ui.FocusBtn("", () => marks.RequestDelete(), "DangerButton");
        page.AddChild(_markDelete);
    }

    // ---- refresh ----

    public void Refresh()
    {
        var shapes = _host.Shapes;
        var zones = _tool.List();
        _tool.SettleSelection(zones);
        var sel = shapes.SelectedZoneId == null ? null : _tool.Find(shapes.SelectedZoneId);
        var kinds = _host.Map.ZoneKinds;

        _error.Visible = kinds.Count == 0;
        _error.Text = "This map's parcels.json lists no zone kinds (it was built before zones existed). Build it again (make map), then reopen the editor. Zones cannot be drawn until then.";
        _tool.Overlay.SyncToggle();
        _kindsHead.Text = $"Zone kinds ({kinds.Count})";

        RefreshKinds(zones, kinds);

        var drawText = _tool.SelectedKind != null ? $"Draw zone of {_tool.KindLabel(_tool.SelectedKind)}  (N)" : "Draw zone of the selected kind  (N)";
        shapes.Panel.RefreshActions(ShapeKind.Zone, drawText, s => _tool.KindLabel(((ZoneInfo)s).Kind));
        RefreshForm(sel, kinds);

        shapes.Panel.RefreshList(ShapeKind.Zone, "zone", s =>
        {
            var z = (ZoneInfo)s;
            var tint = _tool.KindOf(z.Kind)?.Tint ?? "#808080";
            return ($"{z.Label}   {_tool.KindLabel(z.Kind)}   {z.Points.Count} corners", tint, $"Select {z.Label} ({_tool.KindLabel(z.Kind)}, {z.Points.Count} corners, {z.AreaM2:F0} m2)");
        }, string.Concat(kinds.Select(k => k.Name + k.Label + k.Tint)),
            filter: _tool.SelectedKind == null ? null : z => ((ZoneInfo)z).Kind == _tool.SelectedKind,
            head: _tool.SelectedKind == null ? null : $"{_tool.KindLabel(_tool.SelectedKind)} zones", cap: ListCap,
            marks: _tool.Marks.Ids, onMark: (id, on) => _tool.Marks.Mark(new[] { id }, on, false));
        RefreshMarks();

        if (sel != null && _scrolledFor != sel.Id)
        {
            _scrolledFor = sel.Id;
            _host.Chrome.RevealInPanel(shapes.Panel.Of(ShapeKind.Zone).State);
        }
        else if (sel == null) _scrolledFor = "";
    }

    /// <summary>The list of kinds: a toggle button per kind, rebuilt when a kind, its tint, its zone count or the selection changes.</summary>
    void RefreshKinds(List<ZoneInfo> zones, IReadOnlyList<Drycastle.MapLoader.ZoneKindInfo> kinds)
    {
        var counts = zones.GroupBy(z => z.Kind).ToDictionary(g => g.Key, g => g.Count());
        var sig = string.Join("|", kinds.Select(k => k.Name + k.Tint + counts.GetValueOrDefault(k.Name))) + "#" + _tool.SelectedKind;
        if (sig == _kindsSig) return;
        _kindsSig = sig;
        foreach (var b in _kindRows.Values) { _kinds.RemoveChild(b); b.QueueFree(); }
        _kindRows.Clear();
        foreach (var c in _kinds.GetChildren().OfType<Label>().ToList()) { _kinds.RemoveChild(c); c.QueueFree(); }
        if (kinds.Count == 0) _kinds.AddChild(Ui.Note("No zone kinds in this map. Build it again (make map)."));
        foreach (var k in kinds)
        {
            var name = k.Name;
            var n = counts.GetValueOrDefault(name);
            var b = new Button
            {
                Text = $"{k.Label}   {n}", Icon = Ui.Swatch(k.Tint), Alignment = HorizontalAlignment.Left, ClipText = true,
                ToggleMode = true, FocusMode = Control.FocusModeEnum.All,
                TooltipText = $"{k.Label} ({k.Group}): {n} zone{(n == 1 ? "" : "s")}. Select it to show its zones alone on the map (moving bands, the other kinds hidden), list only them and draw a new one of it (N).",
            };
            b.SetPressedNoSignal(name == _tool.SelectedKind);
            b.Pressed += () => { _tool.SelectKind(name == _tool.SelectedKind ? null : name); b.ReleaseFocus(); };
            _kinds.AddChild(b);
            _kindRows[name] = b;
        }
    }

    /// <summary>The form of the selected zone: its kind.</summary>
    void RefreshForm(ZoneInfo? sel, IReadOnlyList<Drycastle.MapLoader.ZoneKindInfo> kinds)
    {
        var showForm = _host.Shapes.Mode == ShapeState.Selected && sel != null;
        _kindRow.Visible = showForm;
        if (!showForm) return;
        var sig = string.Join("|", kinds.Select(k => k.Name + k.Label + k.Tint));
        if (!_kindOpt.GetPopup().Visible && sig != _kindOptSig)
        {
            _kindOptSig = sig;
            _sync = true;
            _kindOpt.Clear();
            foreach (var k in kinds) _kindOpt.AddIconItem(Ui.Swatch(k.Tint), k.Label);
            _sync = false;
        }
        var at = kinds.ToList().FindIndex(k => k.Name == sel!.Kind);
        if (!_kindOpt.GetPopup().Visible && _kindOpt.Selected != at) { _sync = true; _kindOpt.Selected = at; _sync = false; }
        _kindOpt.Disabled = kinds.Count == 0;
    }

    void RefreshMarks()
    {
        var marks = _tool.Marks;
        var n = marks.Count;
        var listed = marks.Listed();
        var unmarked = listed.Count(z => !marks.Contains(z.Id));
        _markHead.Text = $"Marked for deletion ({n})";
        _markAll.Text = _tool.SelectedKind is { } k ? $"Mark all {_tool.KindLabel(k)} ({listed.Count})" : $"Mark all zones ({listed.Count})";
        _markAll.Disabled = unmarked == 0;
        _markAll.TooltipText = listed.Count == 0 ? "There are no zones to mark." : unmarked == 0 ? "All of them are marked already."
            : _tool.SelectedKind != null ? "Mark every zone of the selected kind for deletion. Nothing is deleted yet." : "Mark every zone of the map for deletion. Nothing is deleted yet. Pick a kind first to mark only its zones.";
        _markNone.Disabled = n == 0;
        _markDelete.Text = n == 0 ? "Delete marked zones  (Shift+Del)" : $"Delete {n} marked zone{(n == 1 ? "" : "s")}  (Shift+Del)";
        _markDelete.Disabled = n == 0;
        _markDelete.TooltipText = n == 0 ? "No zone is marked. Tick zones in the list or Ctrl+click them on the map first." : "Delete every marked zone in one step (asks first; one Ctrl+Z brings them all back)";
    }

    // ---- test hooks ----

    public int KindRowCount => _kindRows.Count;
    public string KindRowText(string kind) => _kindRows.TryGetValue(kind, out var b) ? b.Text : "";
    public bool ErrorShown => _error.Visible;
    public void PressKindRow(string kind) { if (_kindRows.TryGetValue(kind, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
    public string KindShown => _kindOpt.Selected >= 0 && _kindOpt.Selected < _host.Map.ZoneKinds.Count ? _host.Map.ZoneKinds[_kindOpt.Selected].Name : "";
    public string MarkDeleteText => _markDelete.Text;
    public bool MarkDeleteEnabled => !_markDelete.Disabled;
}
