using System;
using System.Collections.Generic;
using System.Linq;
using Godot;


/// <summary>
/// The editor's chrome, Blender/Photoshop style: a left rail of icon buttons (one per tool), and a sidebar that shows
/// only the page of the active tool. Clicking the active tool again hides the sidebar. Built from the shared theme
/// (EditorTheme); no per-control colours or font sizes.
/// </summary>
public enum EditorTool { None, Select, Group, Fence, Trees, Objects, Region, View }

public partial class EditorMode
{
    public EditorTool Tool { get; private set; } = EditorTool.None;

    PanelContainer _rail = null!, _cheat = null!;
    ScrollContainer _scroll = null!;
    Label _panelTitle = null!, _speedLabel = null!;
    VBoxContainer _editHead = null!, _editFoot = null!;
    Button _btnTopDown = null!;
    Label _groupCount = null!;
    readonly Dictionary<EditorTool, VBoxContainer> _pages = new();
    readonly Dictionary<EditorTool, Button> _railButtons = new();

    static readonly (EditorTool tool, string icon, string name, Key key)[] Tools =
    {
        (EditorTool.Select, "select", "Select", Key.Key1),
        (EditorTool.Group, "group", "Group", Key.Key2),
        (EditorTool.Fence, "fence", "Fence", Key.Key3),
        (EditorTool.Trees, "trees", "Trees", Key.Key4),
        (EditorTool.Objects, "objects", "Objects", Key.Key5),
        (EditorTool.Region, "region", "Region", Key.Key6),
        (EditorTool.View, "view", "View", Key.Key7),
    };

    const string CfgPath = "user://editor_ui.cfg";

    void BuildUi()
    {
        _ui = new CanvasLayer { Name = "EditorUi", Layer = 10, Visible = false };
        AddChild(_ui);
        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore, Theme = EditorTheme.Build() };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(root);

        BuildRail(root);
        BuildHud(root);
        BuildSidebar(root);
        BuildStatusBar(root);
        BuildCheatSheet(root);
        BuildRegionOverlays(root);
        LoadUiConfig();
        SelectTool(Tool, save: false);
    }

    // ---- rail ----

    Button IconBtn(string icon, string tip, Action onPress, bool toggle = false, int size = EditorTheme.RailButton)
    {
        var b = new Button
        {
            CustomMinimumSize = new Vector2(size, size), ToggleMode = toggle, TooltipText = tip,
            ThemeTypeVariation = "RailButton", FocusMode = Control.FocusModeEnum.All,
        };
        b.AddChild(new IconGlyph { Kind = icon, Owner = b });
        // Space and Enter fly and apply in the viewport; a clicked button must not keep them
        b.Pressed += () => { onPress(); b.ReleaseFocus(); };
        return b;
    }

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
            var b = IconBtn(icon, $"{name}  ({OS.GetKeycodeString(key)})", () => SelectTool(Tool == t ? EditorTool.None : t), toggle: true);
            _railButtons[tool] = b;
            col.AddChild(b);
        }
        col.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        _btnUndo = IconBtn("undo", "Undo  (Ctrl+Z)", () => Undo());
        _btnRedo = IconBtn("redo", "Redo  (Ctrl+Y)", () => Redo());
        col.AddChild(_btnUndo);
        col.AddChild(_btnRedo);
        col.AddChild(IconBtn("walk", "Drop to the ground here and walk  (F3)", WalkFromHere));
        col.AddChild(IconBtn("help", "Controls  (F1)", ToggleCheatSheet));
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
        top.AddChild(IconBtn("close", "Close the sidebar  (Esc)", () => SelectTool(EditorTool.None)));
        outer.AddChild(top);
        outer.AddChild(new HSeparator());

        _editHead = new VBoxContainer();
        BuildPanelHeader(_editHead);
        _editHead.AddChild(new HSeparator());
        outer.AddChild(_editHead);

        _scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(_scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _scroll.AddChild(box);

        _editFoot = new VBoxContainer();
        BuildPanelFooter(_editFoot);
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

        var sel = Page(EditorTool.Select);
        _selLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        sel.AddChild(_selLabel);

        var grp = Page(EditorTool.Group);
        grp.AddChild(new Label { Text = "Marking parcels", ThemeTypeVariation = "Header" });
        grp.AddChild(new Label
        {
            Text = "Click parcels in the 3D view to mark them (click a marked parcel again to unmark). Then group them: a group is one property with one fence around its outer edge.",
            ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _groupCount = new Label { ThemeTypeVariation = "Header" };
        grp.AddChild(_groupCount);
        _groupLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        grp.AddChild(_groupLabel);
        _btnGroup = Btn("Group the marked parcels  (Enter)", GroupSelected);
        _btnGroup.ThemeTypeVariation = "PrimaryButton";
        grp.AddChild(_btnGroup);
        grp.AddChild(new HSeparator());
        grp.AddChild(new Label { Text = "Existing groups", ThemeTypeVariation = "Header" });
        _btnMerge = Btn("Merge marked groups and parcels into one group", MergeGroups);
        _btnMerge.TooltipText = "Mark parcels from two or more groups, or a group and loose parcels (also G). Everything goes into one new group; the fence they share is kept, else the first group's. Ctrl+Z undoes it.";
        _btnAdd = Btn("Add the marked parcels to the group", AddSelectedToGroup);
        _btnRemove = Btn("Take the marked parcels out of their group", RemoveSelectedFromGroup);
        _btnDissolve = Btn("Dissolve the group", DissolveGroup);
        foreach (var b in new[] { _btnMerge, _btnAdd, _btnRemove, _btnDissolve }) grp.AddChild(b);
        grp.AddChild(new Label { Text = "Clear the marks with Esc.", ThemeTypeVariation = "Dim" });

        BuildFenceSection(Page(EditorTool.Fence));
        BuildTreesSection(Page(EditorTool.Trees));
        BuildObjectsSection(Page(EditorTool.Objects));
        BuildRegionPage(Page(EditorTool.Region));
        BuildViewPage(Page(EditorTool.View));
    }

    void BuildViewPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Fly speed", ThemeTypeVariation = "Header" });
        _speedSlider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 0.5, Value = SpeedToSlider(Speed), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Scrollable = false, // the mouse wheel must scroll the sidebar, never move the slider
            TooltipText = "Fly speed (m/s, logarithmic). The mouse wheel over the 3D view changes it too.",
        };
        _speedSlider.ValueChanged += v => SetSpeed((float)SliderToSpeed(v), fromSlider: true);
        _speedSlider.DragEnded += _ => SaveUiConfig();
        page.AddChild(_speedSlider);
        _speedLabel = new Label { Text = $"{Speed:F0} m/s" };
        page.AddChild(_speedLabel);
        page.AddChild(new Label { Text = "Mouse wheel over the 3D view changes it too. Hold Alt for x3.", ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart });

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Camera", ThemeTypeVariation = "Header" });
        _btnTopDown = Btn("Top-down view (T)", ToggleTopDown);
        page.AddChild(_btnTopDown);

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Walk", ThemeTypeVariation = "Header" });
        page.AddChild(Btn("Drop to the ground here and walk  (F3)", WalkFromHere));
        page.AddChild(Btn("Walk from where I was  (F2)", () => SetActive(false)));
        page.AddChild(new Label { Text = "Dropping puts you on the ground below the camera, facing the way you look.", ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        page.AddChild(new Label { Text = "WASD fly, Space up, Shift down, hold the right mouse button to look.", ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    static Button Btn(string text, Action onPress)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += onPress;
        return b;
    }

    // ---- status bar and cheat sheet ----

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

    const string DefaultHint = "WASD fly, RMB look, LMB select, Shift+LMB add, 1-7 tools, G group, T top-down, Enter apply, Ctrl+Z undo, Ctrl+P commands, Esc back, F1 controls, F2 walk";

    const string CheatText =
        "Fly\tWASD, Space up, Shift down, Alt x3, hold RMB to look, wheel changes speed\n" +
        "Select\tLMB select, Shift+LMB add or remove, Esc clear\n" +
        "Walk\tF2 walk from where you were, F3 drop to the ground below the camera and walk\n" +
        "Tools\t1 Select, 2 Group, 3 Fence, 4 Trees, 5 Objects, 6 Region, 7 View (press again to hide)\n" +
        "Group\tG group the selection (marked groups merge)\n" +
        "Edit\tEnter apply, Ctrl+Z undo, Ctrl+Y or Ctrl+Shift+Z redo\n" +
        "Objects\tLMB place, R / Shift+R rotate, Ctrl free position, Ctrl+D duplicate, Del delete, arrows nudge\n" +
        "Region\tN new region, LMB or C adds a corner, Enter closes, then choose the colour and Enter creates\n" +
        "Region edit\tdrag a corner, [ ] pick one, arrows move it, Backspace last corner, Del delete (asks), Ctrl+Z undo\n" +
        "Commands\tCtrl+P: tools, undo, region and parcel IDs\n" +
        "View\tT top-down, F1 this sheet, F2 walk mode\n" +
        "Esc\tgoes one level back: drawing, colour, region selection, placement, gate, object, selection, sidebar";

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

    public bool CheatSheetShown => _cheat.Visible;
    public void ToggleCheatSheet() => _cheat.Visible = !_cheat.Visible;

    // ---- tool switching ----

    public void SelectTool(EditorTool tool, bool save = true)
    {
        if (Tool == EditorTool.Region && tool != EditorTool.Region) CancelRegionGesture();
        if (tool == EditorTool.Region) { DisarmPlacement(false); if (GateArmed) CancelGate(); }
        Tool = tool;
        _panel.Visible = tool != EditorTool.None;
        foreach (var (t, p) in _pages) p.Visible = t == tool;
        foreach (var (t, b) in _railButtons) b.SetPressedNoSignal(t == tool);
        var edit = tool is EditorTool.Fence or EditorTool.Trees;
        _editHead.Visible = _editFoot.Visible = edit;
        _panelTitle.Text = Tools.FirstOrDefault(x => x.tool == tool).name ?? "";
        _scroll.ScrollVertical = 0;
        _statusBar.OffsetRight = tool == EditorTool.None ? 0 : -PanelWidth;
        _panelDirty = true;
        if (save) SaveUiConfig();
    }

    /// <summary>Open a tool by name (screenshots, smoke tests); an unknown name closes the sidebar.</summary>
    public void SetTool(string name)
        => SelectTool(Enum.TryParse<EditorTool>(name, true, out var t) ? t : EditorTool.None);

    /// <summary>Compatibility with the old collapsible sections: opening a title opens its tool.</summary>
    public void SetSectionOpen(string title, bool open)
    {
        if (!Enum.TryParse<EditorTool>(title, true, out var t)) return;
        if (open) SelectTool(t);
        else if (Tool == t) SelectTool(EditorTool.None);
    }

    bool HandleToolKey(Key key)
    {
        foreach (var (t, _, _, k) in Tools)
            if (k == key) { SelectTool(Tool == t ? EditorTool.None : t); return true; }
        return false;
    }

    // ---- persistence ----

    void LoadUiConfig()
    {
        try
        {
            var cfg = new ConfigFile();
            if (cfg.Load(CfgPath) != Error.Ok) return;
            if (Enum.TryParse<EditorTool>((string)cfg.GetValue("ui", "tool", "None"), true, out var t)) Tool = t;
            SetSpeed((float)(double)cfg.GetValue("ui", "speed", (double)Speed));
            if (ParseColour((string)cfg.GetValue("ui", "region_colour", _newColour)) is { } rc) _newColour = rc;
            var rsel = (string)cfg.GetValue("ui", "region", "");
            if (rsel != "") _restoreRegion = rsel;
        }
        catch (Exception e) { GD.PushWarning($"editor ui settings not loaded: {e.Message}"); }
    }

    void SaveUiConfig()
    {
        try
        {
            var cfg = new ConfigFile();
            cfg.SetValue("ui", "tool", Tool.ToString());
            cfg.SetValue("ui", "speed", (double)Speed);
            cfg.SetValue("ui", "region_colour", _newColour);
            cfg.SetValue("ui", "region", SelectedRegionId ?? "");
            cfg.Save(CfgPath);
        }
        catch (Exception e) { GD.PushWarning($"editor ui settings not saved: {e.Message}"); }
    }
}
