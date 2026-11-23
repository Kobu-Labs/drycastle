using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

public enum EditorTool { None, Select, Group, Fence, Ground, Objects, Zones, Poles, View, Sketch }

/// <summary>
/// The editor's chrome, Blender/Photoshop style: a left rail of icon buttons (one per tool), and a sidebar that shows
/// only the page of the active tool. Clicking the active tool again hides the sidebar. Also the HUD line, the label beside the mouse,
/// the status bar (the last message, the hint of the tool in use), the controls sheet and the place of the confirm dialog and palette.
/// Built from the shared theme (EditorTheme); no per-control colours or font sizes. <see cref="EditorMode"/> owns what the controls do.
/// </summary>
public sealed class EditorChrome
{
    const float PanelWidth = 360f;
    const float StatusHeight = 58f;

    internal static readonly (EditorTool tool, string icon, string name, Key key)[] Tools =
    {
        (EditorTool.Select, "select", "Select", Key.Key1),
        (EditorTool.Group, "group", "Group", Key.Key2),
        (EditorTool.Fence, "fence", "Fence", Key.Key3),
        (EditorTool.Ground, "ground", "Ground", Key.Key4),
        (EditorTool.Objects, "objects", "Objects", Key.Key5),
        (EditorTool.Zones, "zone", "Zones", Key.Key6),
        (EditorTool.Poles, "poles", "Poles", Key.Key7),
        (EditorTool.View, "view", "View", Key.Key8),
        (EditorTool.Sketch, "sketch", "Sketch", Key.Key9),
    };

    readonly EditorMode _ed;
    CanvasLayer _ui = null!;
    PanelContainer _rail = null!, _panel = null!, _statusBar = null!, _cheat = null!;
    ScrollContainer _scroll = null!;
    Label _panelTitle = null!, _unsavedLabel = null!, _hudLabel = null!, _hoverLabel = null!, _statusLabel = null!, _hintLabel = null!;
    VBoxContainer _editHead = null!, _editFoot = null!;
    Button _btnUndo = null!, _btnRedo = null!; // the rail buttons
    Label _undoInfo = null!;                   // top of the Objects page
    readonly Dictionary<EditorTool, VBoxContainer> _pages = new();
    readonly Dictionary<EditorTool, Button> _railButtons = new();
    double _statusUntil;

    public EditorChrome(EditorMode ed) => _ed = ed;

    /// <summary>The whole editor UI is shown (the editor is open).</summary>
    public bool Visible { get => _ui.Visible; set => _ui.Visible = value; }

    public string StatusMessage { get; private set; } = "";

    public void Build()
    {
        _ui = new CanvasLayer { Name = "EditorUi", Layer = 10, Visible = false };
        _ed.AddChild(_ui);
        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = EditorTheme.Build() };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(root);

        _ed.Sketches.BuildTags(root);
        BuildRail(root);
        BuildHud(root);
        BuildSidebar(root);
        BuildStatusBar(root);
        BuildCheatSheet(root);
        _ed.Confirm.Build(root);
        _ed.Commands.Build(root);
    }

    // ---- rail ----

    void BuildRail(Control root)
    {
        _rail = new PanelContainer { Name = "Rail" };
        _rail.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        _rail.OffsetRight = EditorTheme.RailWidth;
        root.AddChild(_rail);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", EditorTheme.Space);
        _rail.AddChild(col);

        foreach (var (tool, icon, name, key) in Tools)
        {
            if (tool == EditorTool.View) col.AddChild(new HSeparator());
            var t = tool;
            var b = Ui.IconBtn(icon, $"{name}  ({OS.GetKeycodeString(key)})", () => _ed.SelectTool(_ed.Tool == t ? EditorTool.None : t), toggle: true);
            _railButtons[tool] = b;
            col.AddChild(b);
        }
        col.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        _btnUndo = Ui.IconBtn("undo", "Undo  (Ctrl+Z)", () => _ed.Edits.Undo());
        _btnRedo = Ui.IconBtn("redo", "Redo  (Ctrl+Y)", () => _ed.Edits.Redo());
        col.AddChild(_btnUndo);
        col.AddChild(_btnRedo);
        col.AddChild(Ui.IconBtn("walk", "Drop to the ground here and walk  (F3)", _ed.WalkFromHere));
        col.AddChild(Ui.IconBtn("help", "Controls  (F1)", ToggleCheatSheet));
    }

    // ---- HUD ----

    void BuildHud(Control root)
    {
        _hudLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hudLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hudLabel.AddThemeConstantOverride("outline_size", 4);
        _hudLabel.Position = new Vector2(EditorTheme.RailWidth + EditorTheme.Space * 2, EditorTheme.Space);
        root.AddChild(_hudLabel);

        _hoverLabel = new Label { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hoverLabel.AddThemeStyleboxOverride("normal", EditorTheme.Box(EditorTheme.Scrim, EditorTheme.Space));
        root.AddChild(_hoverLabel);
    }

    /// <summary>The label beside the mouse (a parcel, a zone, a cable target) shows this text.</summary>
    public void ShowHoverText(string text, Vector2 mouse)
    {
        _hoverLabel.Text = text;
        _hoverLabel.Position = mouse + new Vector2(16, 20);
        _hoverLabel.Visible = true;
    }

    public void HideHoverText() => _hoverLabel.Visible = false;

    // ---- sidebar ----

    void BuildSidebar(Control root)
    {
        _panel = new PanelContainer { Name = "Panel" };
        _panel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        _panel.OffsetLeft = -PanelWidth;
        root.AddChild(_panel);
        var outer = new VBoxContainer();
        _panel.AddChild(outer);

        var top = new HBoxContainer();
        _panelTitle = new Label { ThemeTypeVariation = "Header", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AddChild(_panelTitle);
        top.AddChild(Ui.IconBtn("close", "Close the sidebar  (Esc)", () => _ed.SelectTool(EditorTool.None)));
        outer.AddChild(top);
        outer.AddChild(new HSeparator());

        _editHead = new VBoxContainer();
        _ed.Fence.BuildHeader(_editHead);
        _editHead.AddChild(new HSeparator());
        outer.AddChild(_editHead);

        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(_scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _scroll.AddChild(box);

        _editFoot = new VBoxContainer();
        _ed.Fence.BuildFooter(_editFoot);
        var footSep = new HSeparator();
        _editFoot.AddChild(footSep);
        _editFoot.MoveChild(footSep, 0);
        outer.AddChild(_editFoot);

        VBoxContainer Page(EditorTool t)
        {
            var p = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            box.AddChild(p);
            _pages[t] = p;
            return p;
        }

        _ed.Parcels.BuildSelect(Page(EditorTool.Select));
        _ed.Parcels.BuildGroup(Page(EditorTool.Group));
        _ed.Fence.BuildPage(Page(EditorTool.Fence));
        var objects = Page(EditorTool.Objects);
        _undoInfo = Ui.Note();
        objects.AddChild(_undoInfo);
        _ed.Objects.BuildPage(objects);
        _ed.Ground.BuildPage(Page(EditorTool.Ground));
        _ed.Zones.BuildPage(Page(EditorTool.Zones));
        _ed.Poles.BuildPage(Page(EditorTool.Poles));
        BuildViewPage(Page(EditorTool.View));
        _ed.Sketches.BuildPage(Page(EditorTool.Sketch));
    }

    void BuildViewPage(VBoxContainer page)
    {
        _ed.Camera.BuildSpeedSection(page);

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Camera", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Btn("Top-down view (T)", _ed.ToggleTopDown));

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Walk", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Btn("Drop to the ground here and walk  (F3)", _ed.WalkFromHere));
        page.AddChild(Ui.Btn("Walk from where I was  (F2)", () => _ed.SetActive(false)));
        page.AddChild(new Label { Text = "Dropping puts you on the ground below the camera, facing the way you look.", ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        page.AddChild(new Label { Text = "WASD fly, Space up, Shift down, hold the right mouse button to look.", ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart });

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Layers", ThemeTypeVariation = "Header" });
        _ed.FloorPlan.BuildToggle(page);
        _ed.Ortho.BuildSection(page);
    }

    /// <summary>The sidebar shows the page of this tool (none: no sidebar).</summary>
    public void ShowTool(EditorTool tool)
    {
        _panel.Visible = tool != EditorTool.None;
        foreach (var (t, p) in _pages) p.Visible = t == tool;
        foreach (var (t, b) in _railButtons) b.SetPressedNoSignal(t == tool);
        var edit = tool is EditorTool.Fence;
        _editHead.Visible = _editFoot.Visible = edit;
        _panelTitle.Text = Tools.FirstOrDefault(x => x.tool == tool).name ?? "";
        _scroll.ScrollVertical = 0;
        _statusBar.OffsetRight = tool == EditorTool.None ? 0 : -PanelWidth;
    }

    /// <summary>The sidebar scrolls (on the next frame) until the control is in view.</summary>
    public void RevealInPanel(Control control) => Callable.From(() => _scroll.EnsureControlVisible(control)).CallDeferred();

    /// <summary>The mouse is over the sidebar, the rail, the status bar or a dialog.</summary>
    public bool OverUi(Vector2 mouse)
        => _ui.Visible && ((_panel.Visible && _panel.GetGlobalRect().HasPoint(mouse)) || _rail.GetGlobalRect().HasPoint(mouse)
            || _statusBar.GetGlobalRect().HasPoint(mouse) || (_cheat.Visible && _cheat.GetGlobalRect().HasPoint(mouse))
            || _ed.Confirm.Contains(mouse) || _ed.Commands.Contains(mouse));

    // ---- status bar and hint ----

    void BuildStatusBar(Control root)
    {
        _statusBar = new PanelContainer { Name = "StatusBar" };
        _statusBar.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _statusBar.OffsetLeft = EditorTheme.RailWidth;
        _statusBar.OffsetTop = -StatusHeight;
        _statusBar.GrowVertical = Control.GrowDirection.Begin;
        root.AddChild(_statusBar);
        var sbox = new VBoxContainer();
        sbox.AddThemeConstantOverride("separation", 0);
        _statusBar.AddChild(sbox);
        _statusLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 24) };
        _hintLabel = new Label { ThemeTypeVariation = "Dim", MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true };
        _hintLabel.Text = DefaultHint;
        var srow = new HBoxContainer();
        _statusLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _unsavedLabel = new Label { ThemeTypeVariation = "Warn", MouseFilter = Control.MouseFilterEnum.Ignore };
        srow.AddChild(_statusLabel);
        srow.AddChild(_unsavedLabel);
        sbox.AddChild(srow);
        sbox.AddChild(_hintLabel);
    }

    /// <summary>One line in the status bar (and the log); errors stay longer and are red.</summary>
    public void Say(string message, bool error = false)
    {
        StatusMessage = message;
        _statusUntil = Time.GetTicksMsec() / 1000.0 + (error ? 10 : 6);
        _statusLabel.Text = message;
        _statusLabel.Modulate = error ? new Color(1f, 0.55f, 0.45f) : Colors.White;
        GD.Print($"editor: {message}");
    }

    const string DefaultHint = "WASD fly, RMB look, LMB select, Shift+LMB add, 1-9 tools, G group, T top-down, Enter apply, Ctrl+Z undo, Ctrl+P commands, Esc back, F1 controls, F2 walk";

    /// <summary>The undo and redo buttons, the undo line of the Objects page and the hint bar (it names what the tool in use does).</summary>
    public void RefreshUndoAndHint()
    {
        var h = _ed.Edits.History;
        _btnUndo.Disabled = h.UndoDepth == 0;
        _btnRedo.Disabled = h.RedoDepth == 0;
        _undoInfo.Text = (h.UndoDepth == 0 ? "nothing to undo" : $"undo: {h.NextUndoDescription} ({h.UndoDepth})") + (h.RedoDepth == 0 ? "" : $"; redo: {h.NextRedoDescription} ({h.RedoDepth})");
        var tool = _ed.Tool;
        var placing = _ed.Objects.PlacementAsset != null;
        _hintLabel.Text = tool == EditorTool.Ground ? _ed.Ground.HintText() : tool == EditorTool.Zones ? _ed.Zones.HintText() : tool == EditorTool.Sketch ? _ed.Sketches.HintText() : tool == EditorTool.Poles && !placing ? _ed.Poles.HintText() : tool == EditorTool.Group && !placing
            ? "marking parcels: click a parcel to mark or unmark it, Enter groups the marked ones, Esc clears the marks"
            : placing
            ? "placing: LMB place, R / Shift+R rotate, Ctrl free position, Esc or RMB stop, Ctrl+Z undo"
            : _ed.Objects.SelectedObjectId != null
                ? "object: drag moves, drag the ring rotates, arrows nudge, Del delete, Ctrl+D duplicate, R rotate, Ctrl+Z undo, Esc deselect"
                : "WASD fly, RMB look, LMB select, Shift+LMB add, G group, T top-down, Enter apply, Ctrl+Z undo, Esc back, F1 controls, F2 walk";
    }

    /// <summary>Per frame: the unsaved count, the HUD line, and the status line fades out.</summary>
    public void Update()
    {
        var fence = _ed.Fence;
        var shapes = _ed.Shapes;
        var tool = _ed.Tool;
        _unsavedLabel.Text = fence.PendingCount == 0 ? "" : $"* {fence.PendingCount} unsaved (Enter applies)";
        var c = _ed.FlyCam.GlobalPosition;
        _hudLabel.Text = $"EDITOR  |  {(tool == EditorTool.None ? "no tool" : tool.ToString())}  |  {_ed.Selection.Count} selected  |  {_ed.Camera.Speed:F0} m/s  |  x {c.X:F0}  y {-c.Z:F0}  z {c.Y:F0}" + (shapes.SelectedShapeId != null && shapes.ShapeTool ? $"  |  {(tool == EditorTool.Zones ? "zone" : "patch")} {shapes.SelectedShape(shapes.CurrentKind)?.Label}{(shapes.SelectedShape(shapes.CurrentKind) is PatchInfo sp ? "  " + sp.Type : "")}" : "") + (tool == EditorTool.Zones && _ed.Zones.Marks.Count > 0 ? $"  |  {_ed.Zones.Marks.Count} marked for deletion" : "") + (fence.PendingCount > 0 || shapes.ShapeDraftPending ? $"  |  * {(fence.PendingCount > 0 ? $"{fence.PendingCount} UNSAVED" : "UNSAVED")}" : "");
        if (Time.GetTicksMsec() / 1000.0 > _statusUntil && _statusLabel.Text != "")
            _statusLabel.Text = "";
    }

    // ---- cheat sheet ----

    const string CheatText =
        "Fly\tWASD, Space up, Shift down, Alt x3, hold RMB to look, wheel changes speed\n" +
        "Select\tLMB select, Shift+LMB add or remove, Esc clear\n" +
        "Walk\tF2 walk from where you were, F3 drop to the ground below the camera and walk\n" +
        "Tools\t1 Select, 2 Group, 3 Fence, 4 Ground, 5 Objects, 6 Zones, 7 Poles, 8 View, 9 Sketch (press again to hide)\n" +
        "Group\tG group the selection (marked groups merge)\n" +
        "Edit\tEnter apply, Ctrl+Z undo, Ctrl+Y or Ctrl+Shift+Z redo\n" +
        "Objects\tLMB place, R / Shift+R rotate, Ctrl free position, Ctrl+D duplicate, Del delete, arrows nudge\n" +
        "Ground\tpick a type in the list (Tab reaches it) or click the ground; tick what it carries (colours, recipe, water) and edit colours and OSM values: each change saves and Ctrl+Z undoes; Shift+N new type, Del delete type (asks); M shows the ground map; type changes show after make map\n" +
        "Ground patch\tN draws a patch of the selected type: LMB or C adds a corner, Enter closes and creates it (a water type asks for the water level first); click a patch to select it, change its type in the form (a crop_ type is a crop: its outline is coloured per crop and named in the form); only the hovered and the selected patch are outlined\n" +
        "Zones\tpick a kind in the list, N draws a zone of it: LMB or C adds a corner, Enter creates it; click a zone to select it, change its kind in the form; M shows the zone overlay; a selected kind is shown alone with moving bands (also with the overlay off), Esc shows all kinds again; every zone, also one the map gives a kind by itself, can be selected, reshaped, given another kind or deleted\n" +
        "Zones mass delete\tmark zones: tick them in the list, Ctrl+LMB on the map, X for the selected one, or Mark all of the kind; Shift+Del deletes every marked zone (asks), one Ctrl+Z brings them all back\n" +
        "Patch and zone edit\tdrag a corner, [ ] pick one, arrows move it, Backspace last corner, Del delete (asks), Ctrl+Z undo, Esc one level back\n" +
        "Poles plan\tP plans a line: LMB a pole point, RMB a house, Backspace removes the last, Enter builds poles, cables and house connections\n" +
        "Poles\tchoose a pole and LMB places it, C connects: LMB on a pole, then on the next poles or on a house, Esc stops\n" +
        "Poles edit\tdrag a pole or its ring, R / Shift+R turn, LMB on a cable selects it, Del removes the cable or the pole with its cables\n" +
        "Sketch\tP points, L lines: LMB or C marks, Enter finishes a line, Shift+Enter or the first corner closes it\n" +
        "Sketch edit\tLMB on a mark or [ ] selects it, Del deletes it, Backspace last corner, Ctrl+Z undo; marks are temporary and named in prompts\n" +
        "Sketches\tpick, New, Rename, Delete in the Sketch panel: marks go into the active sketch, prompts can name a whole sketch (\"sketch ROAD\")\n" +
        "Commands\tCtrl+P: tools, actions, ground types, patch, zone and parcel IDs\n" +
        "View\tT top-down, O aerial photo (reference), B floor plan of the selected parcel (building hidden, wall lines and a 1 m grid to sketch on), F1 this sheet, F2 walk mode\n" +
        "Esc\tgoes one level back: drawing, water level, patch, zone or sketch selection, placement, planning, connecting, gate, cable, object, selection, sidebar";

    void BuildCheatSheet(Control root)
    {
        _cheat = new PanelContainer { Name = "CheatSheet", Visible = false };
        _cheat.SetAnchorsPreset(Control.LayoutPreset.Center);
        _cheat.GrowHorizontal = Control.GrowDirection.Both;
        _cheat.GrowVertical = Control.GrowDirection.Both;
        root.AddChild(_cheat);
        var box = new VBoxContainer();
        _cheat.AddChild(box);
        box.AddChild(new Label { Text = "Controls", ThemeTypeVariation = "Title" });
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.Space * 2);
        foreach (var line in CheatText.Split('\n'))
        {
            var kv = line.Split('\t');
            grid.AddChild(new Label { Text = kv[0], ThemeTypeVariation = "Header" });
            grid.AddChild(new Label { Text = kv[1], ThemeTypeVariation = "Dim" });
        }
        box.AddChild(grid);
        box.AddChild(new Label { Text = "F1 or Esc closes this.", ThemeTypeVariation = "Dim" });
    }

    public bool CheatSheetShown { get => _cheat.Visible; set => _cheat.Visible = value; }
    public void ToggleCheatSheet() => _cheat.Visible = !_cheat.Visible;
}
