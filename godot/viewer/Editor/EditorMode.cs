using Drycastle.Viewer.Edits;
using Drycastle.Viewer.Editor.Tools;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// Editor mode shell: it routes input (keys, mouse, Esc) to the parts and shares what they use: the edits file (<see cref="Edits"/>),
/// the chrome (rail, sidebar, status bar), the outlines draped on the terrain, parcel selection and property groups, picking.
/// A free-fly camera, a right-hand panel and a status line; edits go to edits.json (see EditsFile). The walk-mode Player is frozen and
/// hidden meanwhile. F2 toggles. The EditsWatcher regenerates whatever the saved file changes; its events show in the status line.
/// </summary>
public partial class EditorMode : Node, IEditorHost
{
    public EditStore Edits { get; }
    public EditorCamera Camera { get; }
    public EditorChrome Chrome { get; }
    public ParcelGroups Groups { get; }
    public ParcelPanel Parcels { get; }
    public OutlineLayer Outlines { get; }
    public GroundPicker Picker { get; }

    // the parts of the editor that own their state; Update and ActiveChanged run in this order
    internal OrthoLayer Ortho { get; }
    internal FloorPlanLayer FloorPlan { get; }
    public SketchTool Sketches { get; }
    public PhotoTool Photo { get; }
    internal YouDot You { get; }
    public ObjectsTool Objects { get; }
    public PolesTool Poles { get; }
    public FenceTool Fence { get; }
    public ShapeEditor Shapes { get; }
    public GroundTool Ground { get; }
    public ZonesTool Zones { get; }
    readonly List<EditorPart> _parts;

    public EditorMode()
    {
        Ortho = new OrthoLayer(this);
        FloorPlan = new FloorPlanLayer(this);
        Sketches = new SketchTool(this);
        Photo = new PhotoTool(this);
        You = new YouDot(this);
        Objects = new ObjectsTool(this);
        Poles = new PolesTool(this);
        Fence = new FenceTool(this);
        Shapes = new ShapeEditor(this);
        Ground = new GroundTool(this);
        Zones = new ZonesTool(this);
        Camera = new EditorCamera(this);
        Picker = new GroundPicker(this);
        Groups = new ParcelGroups(this);
        Outlines = new OutlineLayer(this, Groups, () => HoverId);
        Parcels = new ParcelPanel(this, Groups);
        Chrome = new EditorChrome(this);
        Edits = new EditStore(this, () => { Ground.Rows(); Zones.SyncCatalog(); }, doc => Sketches.Replay(doc));
        Commands = new CommandPalette(PaletteAll);
        _parts = new() { Fence, Ortho, FloorPlan, Sketches, Photo, You, Objects, Poles, Shapes, Ground, Zones };
    }

    public Drycastle.MapLoader.MapLoader Map { get; init; } = null!;
    public Player Player { get; init; } = null!;
    public EditsWatcher? Watcher { get; init; }
    public string EditsPath { get; init; } = "";

    public bool Active { get; private set; }
    public Camera3D FlyCam => Camera.Node;
    public Camera3D CurrentCamera => Active ? FlyCam : Player.Camera;
    public SortedSet<string> Selection { get; } = new(StringComparer.Ordinal);
    public Node3D MarkerRoot { get; private set; } = null!;
    public event Action? SelectionChanged;

    string? HoverId { get; set; }
    bool _panelDirty = true;
    bool _looking, _rmbMoved;
    Vector2 _rmbPressPos;
    public bool Looking => _looking;
    Vector3? _hoverPoint;
    double _pollT;

    public override void _Ready()
    {
        Camera.Build(this);
        AddChild(Outlines.Root);
        MarkerRoot = new Node3D { Name = "EditorMarkers", Visible = false };
        AddChild(MarkerRoot);
        Chrome.Build();
        LoadUiConfig();
        SelectTool(Tool, save: false);
        Shapes.BuildWorld();
        Sketches.BuildWorld();
        Photo.BuildWorld();
        Sketches.Restore();
        GetTree().AutoAcceptQuit = false; // window close asks first when fence or tree edits are unapplied (_Notification)
        if (Watcher != null)
        {
            Watcher.Launched += ids => Say("regenerating " + string.Join(", ", ids.Take(3)) + "...");
            Watcher.Applied += (_, ms) => Say($"applied in {ms:F0} ms");
            Watcher.Failed += m => Say("error: " + m, true);
            Watcher.InstantApplied += (_, _) => _panelDirty = true;
            Watcher.ParseFailed += m => Say("edits file is not valid JSON yet: " + m, true);
            Watcher.MapReloading += n => Say($"map re-exported: loading the buildings of {n} chunk{(n == 1 ? "" : "s")}...");
            Watcher.MapReloaded += (n, _, ms) => Say($"map reloaded: buildings of {n} chunk{(n == 1 ? "" : "s")} in {ms:F0} ms");
            Watcher.MapReloadFailed += m => Say("map not reloaded: " + m, true);
            Watcher.BuildingLaunched += files => Say("rebuilding " + string.Join(", ", files.Take(3)) + "...");
            Watcher.BuildingApplied += (parcels, ms) => Say(parcels.Count == 0 ? $"building files changed nothing on the map ({ms:F0} ms)" : $"building applied: {string.Join(", ", parcels.Take(3))} in {ms:F0} ms");
            Watcher.BuildingFailed += m => Say("buildings: " + m, true);
        }
        Edits.Remember();
        Groups.Reload();
        SetActive(false);
    }

    // ---- mode switch ----

    public void SetActive(bool on)
    {
        if (on == Active && FlyCam.Current == on) return;
        Active = on;
        Outlines.Root.Visible = on;
        MarkerRoot.Visible = on;
        foreach (var part in _parts) part.ActiveChanged(on);
        Chrome.Visible = on;
        _looking = false;
        if (on)
        {
            Camera.EnterEditor();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Outlines.MarkAll();
            _panelDirty = true;
            Groups.Reload();
        }
        else
        {
            Camera.LeaveEditor();
            Input.MouseMode = Input.MouseModeEnum.Captured;
            Shapes.CancelShapeGesture();
            if (Fence.Gates.Armed) Fence.Gates.Cancel();
            HoverId = null;
            Chrome.HideHoverText();
        }
    }

    /// <summary>Leave the editor and walk from the ground below the fly camera, facing where it looks.</summary>
    internal void WalkFromHere()
    {
        if (!Active) return;
        Camera.DropPlayer();
        SetActive(false);
        Say("dropped to the ground; F2 returns to the editor");
    }

    /// <summary>T: snap to a top-down view above the cursor (or the screen centre); again restores the camera.</summary>
    public void ToggleTopDown() => Camera.ToggleTopDown(_hoverPoint);

    // ---- selection ----

    /// <summary>Click semantics: plain click replaces the selection (empty space clears it), shift toggles one parcel.</summary>
    public void Click(string? parcelId, bool shift)
    {
        var before = string.Join(",", Selection);
        var unit = parcelId == null ? new List<string>() : Groups.Unit(parcelId);
        if (shift)
        {
            if (unit.Count > 0)
            {
                // a group is one thing: toggle all of its members together
                if (unit.Any(Selection.Contains)) foreach (var m in unit) Selection.Remove(m);
                else foreach (var m in unit) Selection.Add(m);
            }
        }
        else
        {
            Selection.Clear();
            foreach (var m in unit) Selection.Add(m);
        }
        if (string.Join(",", Selection) == before) return;
        Outlines.MarkSelection();
        _panelDirty = true;
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Run <paramref name="proceed"/> now, or after a confirm when fence or tree edits are unapplied (they are
    /// kept in memory only). Accepting discards them; Cancel stays here so Apply (Enter) can save them first.
    /// </summary>
    void GuardUnsaved(Action proceed, string okText = "Discard")
    {
        if (Fence.PendingCount == 0) { proceed(); return; }
        var n = Fence.PendingCount;
        Confirm.Show($"Discard {n} unsaved edit{(n == 1 ? "" : "s")}?",
            $"Fence and tree edits for {Fence.PendingNames}{(n > 4 ? ", ..." : "")} are not applied yet and exist only in memory. Cancel, then press Enter to apply them.",
            okText, () => { Fence.Discard(); proceed(); });
    }

    public void MarkPanelDirty() => _panelDirty = true;

    /// <summary>Refresh the panel and the outlines now (normally done on the next frame).</summary>
    public void RefreshNow() { RefreshPanel(); Outlines.Rebuild(); }

    public override void _ExitTree() => Photo.Shutdown();

    public override void _Notification(int what)
    {
        if (what != NotificationWMCloseRequest) return;
        if (Fence.PendingCount == 0) { GetTree().Quit(); return; }
        if (!Active) SetActive(true); // the confirm needs the editor UI
        GuardUnsaved(() => GetTree().Quit(), "Quit");
    }

    public void Say(string message, bool error = false) => Chrome.Say(message, error);

    // ---- input ----

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F2 })
        {
            if (Active) GuardUnsaved(() => SetActive(false)); else SetActive(true);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!Active) return;
        switch (e)
        {
            case InputEventMouseMotion mm when _looking:
                _rmbMoved = true;
                Camera.Look(mm.Relative);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } rb:
                _rmbPressPos = rb.Position;
                _looking = true;
                _rmbMoved = false;
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wu when !Chrome.OverUi(wu.Position):
                Camera.SetSpeed(Camera.Speed * 1.25f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wd when !Chrome.OverUi(wd.Position):
                Camera.SetSpeed(Camera.Speed / 1.25f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Shapes.ShapeTool && !Chrome.OverUi(mb.Position):
                Shapes.ShapeLeftPress(mb.Position, mb.CtrlPressed);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Tool == EditorTool.Sketch:
                Sketches.LeftPress(mb.Position, mb.CtrlPressed);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && !Modal && Tool == EditorTool.Photo && !Chrome.OverUi(mb.Position) && Photo.PickOpening(mb.Position):
                GetViewport().GuiReleaseFocus();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when !_looking && Modal:
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Fence.Gates.Armed:
                Fence.Gates.LeftPress(mb.Position);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Objects.PlacementAsset != null:
                Objects.PlaceClick(mb.Position, mb.CtrlPressed);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Tool == EditorTool.Poles:
                GetViewport().GuiReleaseFocus();
                Poles.LeftPress(mb.Position, mb.ShiftPressed);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking:
                GetViewport().GuiReleaseFocus();
                Objects.LeftPress(mb.Position, mb.ShiftPressed);
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                if (HandleModalKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Modal) break;
                if (Shapes.HandleShapeKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Ground.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Zones.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Sketches.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Photo.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Poles.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (HandleUndoKey(k) || Objects.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (!k.CtrlPressed && !k.AltPressed && HandleToolKey(k.Keycode)) { GetViewport().SetInputAsHandled(); break; }
                switch (k.Keycode)
                {
                    case Key.F1: Chrome.ToggleCheatSheet(); break;
                    case Key.F3: GuardUnsaved(WalkFromHere); break;
                    case Key.G: Groups.GroupSelected(); break;
                    case Key.T: ToggleTopDown(); break;
                    case Key.O: Ortho.ToggleOrtho(); break;
                    case Key.B: FloorPlan.ToggleFloorPlan(); break;
                    case Key.Enter or Key.KpEnter:
                        if (GetViewport().GuiGetFocusOwner() is (LineEdit or TextEdit)) break;
                        if (Tool == EditorTool.Group) { if (Parcels.CanGroup) Groups.GroupSelected(); }
                        else Fence.Apply();
                        break;
                    case Key.Escape:
                        if (Chrome.CheatSheetShown) Chrome.CheatSheetShown = false;
                        else if (Tool == EditorTool.Photo && Photo.Escape()) { }
                        else if (Objects.PlacementAsset != null) Objects.DisarmPlacement();
                        else if (Poles.Plan.Planning) Poles.Plan.Stop();
                        else if (Poles.Connecting) Poles.StopConnecting();
                        else if (Fence.Gates.Armed) Fence.Gates.Cancel();
                        else if (Poles.SelectedCableId != null) Poles.SelectCable(null);
                        else if (Objects.SelectedObjectId != null) Objects.SelectObject(null);
                        else if (Tool == EditorTool.Ground && Ground.Escape()) { }
                        else if (Tool == EditorTool.Zones && Zones.Escape()) { }
                        else if (Selection.Count > 0) GuardUnsaved(() => Click(null, false));
                        else if (Tool != EditorTool.None) SelectTool(EditorTool.None);
                        break;
                }
                break;
        }
    }

    public override void _Input(InputEvent e)
    {
        // release is handled before the GUI so it is never swallowed
        if (Active && _looking && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Right })
        {
            _looking = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            if (!_rmbMoved && Poles.Plan.Planning && Tool == EditorTool.Poles) Poles.Plan.RightClick(_rmbPressPos); // plan mode: a right click marks a house
            else if (!_rmbMoved && Objects.PlacementAsset != null) Objects.DisarmPlacement(); // a right click (no look drag) leaves placement mode
            else if (!_rmbMoved && Poles.Connecting) Poles.StopConnecting(); // and connect mode
        }
        if (Active && Objects.Dragging && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            Objects.EndDrag();
        if (Active && Shapes.ShapeDragging && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            Shapes.EndCornerDrag();
        if (Active && Fence.Gates.Dragging && e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
            Fence.Gates.End();
    }

    /// <summary>Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z; true when the key was used. Not reached while a text field has the focus.</summary>
    bool HandleUndoKey(InputEventKey k)
    {
        if (!k.CtrlPressed) return false;
        switch (k.Keycode)
        {
            case Key.Z: if (k.ShiftPressed) Edits.Redo(); else Edits.Undo(); return true;
            case Key.Y: Edits.Redo(); return true;
        }
        return false;
    }

    // ---- per frame ----

    public override void _Process(double delta)
    {
        if (!Active) return;
        Camera.Fly((float)delta);
        UpdateHover();
        foreach (var part in _parts) part.Update(delta);
        _pollT += delta;
        if (_pollT > 0.5)
        {
            _pollT = 0;
            if (Edits.ChangedOnDisk()) Groups.Reload();
        }
        Outlines.Update();
        if (_panelDirty) RefreshPanel();
        Chrome.Update();
    }

    /// <summary>Redraw the sidebar of every tool (and the undo buttons and the hint) from the current state.</summary>
    void RefreshPanel()
    {
        _panelDirty = false;
        Parcels.Refresh();
        Fence.RefreshPanel();
        Objects.RefreshPanel();
        Chrome.RefreshUndoAndHint();
        Poles.RefreshPanel();
        Ground.RefreshPanel();
        Zones.RefreshPanel();
        Sketches.RefreshPanel();
        Photo.RefreshPanel();
    }

    // ---- hover ----

    bool _hoverForced;

    /// <summary>Force the hovered parcel (smoke tests, screenshots); null returns to mouse hover.</summary>
    public void SetHover(string? id)
    {
        _hoverForced = id != null;
        if (id != HoverId) { HoverId = id; Outlines.MarkHover(); }
    }

    /// <summary>The hover of the frame: the parcel under the mouse (a zone or a ground patch in those tools), outlined and named beside the mouse.</summary>
    void UpdateHover()
    {
        if (_hoverForced) return;
        var mouse = GetViewport().GetMousePosition();
        string? id = null;
        _hoverPoint = null;
        if (!_looking && !Chrome.OverUi(mouse) && Tool is EditorTool.Zones)
        {
            if (Picker.RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var zp)) _hoverPoint = zp; // no parcel highlight: the zones are what is pointed at
        }
        else if (!_looking && !Chrome.OverUi(mouse) && Tool is not EditorTool.Sketch)
        {
            var p = Picker.PickScreen(mouse, out var point);
            if (p != null || point != default) _hoverPoint = point;
            id = p?.Id;
        }
        if (id != HoverId)
        {
            HoverId = id;
            Outlines.MarkHover();
        }
        if (Tool is EditorTool.Ground or EditorTool.Zones)
        {
            var calm = _hoverPoint != null && !_looking && !Chrome.OverUi(mouse) && !Modal && Shapes.Mode is ShapeState.Idle or ShapeState.Selected;
            var text = !calm ? null
                : Tool == EditorTool.Zones ? (Zones.Drawn ? Zones.HoverText(_hoverPoint!.Value) : null)
                : Ground.Overlay.On ? Ground.HoverText(_hoverPoint!.Value) : null;
            if (text != null) Chrome.ShowHoverText(text, mouse); else Chrome.HideHoverText();
            Ground.UpdateHoverOutline(Tool == EditorTool.Ground && calm ? _hoverPoint : null);
            return;
        }
        Ground.UpdateHoverOutline(null);
        if (id == null) { Chrome.HideHoverText(); return; }
        var info = Map.Parcels[id];
        Chrome.ShowHoverText(info.Group != null
            ? $"{info.Group} ({Groups.Unit(id).Count} parcels)"
            : $"{id} {info.Kind}", mouse);
    }

    /// <summary>The hint line of the open shape tool (smoke tests).</summary>
    public string ShapeHint => Shapes.CurrentKind == ShapeKind.Zone ? Zones.HintText() : Ground.HintText();
}
