using Drycastle.MapLoader;
using Drycastle.Viewer.Edits;
using Drycastle.Viewer.Editor.Tools;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// Editor mode shell: a free-fly camera, parcel hover and selection, outlines (selection, hovered parcel,
/// property groups) draped on the terrain, a right-hand panel and a status line, and property group
/// operations written to edits.json (see EditsFile). The walk-mode Player is frozen and hidden meanwhile.
/// F2 toggles. The EditsWatcher regenerates whatever the saved file changes; its events show in the status line.
/// </summary>
public partial class EditorMode : Node, IEditorHost
{
    public const float PanelWidth = 360f;
    const float StatusHeight = 58f;

    public EditStore Edits { get; }
    public EditorCamera Camera { get; }

    // the parts of the editor that own their state; Update and ActiveChanged run in this order
    public OrthoLayer Ortho { get; }
    public FloorPlanLayer FloorPlan { get; }
    public SketchTool Sketches { get; }
    public YouDot You { get; }
    public ObjectsTool Objects { get; }
    public PolesTool Poles { get; }
    public FenceTool Fence { get; }
    public ShapeEditor Shapes { get; }
    public GroundTool Ground { get; }
    readonly List<EditorPart> _parts;

    public EditorMode()
    {
        Ortho = new OrthoLayer(this);
        FloorPlan = new FloorPlanLayer(this);
        Sketches = new SketchTool(this);
        You = new YouDot(this);
        Objects = new ObjectsTool(this);
        Poles = new PolesTool(this);
        Fence = new FenceTool(this);
        Shapes = new ShapeEditor(this);
        Ground = new GroundTool(this);
        Camera = new EditorCamera(this);
        Edits = new EditStore(this, SyncCatalogs, doc => Sketches.Replay(doc));
        Commands = new CommandPalette(PaletteAll);
        _parts = new() { Fence, Ortho, FloorPlan, Sketches, You, Objects, Poles, Shapes, Ground };
    }

    public Drycastle.MapLoader.MapLoader Map { get; init; } = null!;
    public Player Player { get; init; } = null!;
    public EditsWatcher? Watcher { get; init; }
    public string EditsPath { get; init; } = "";

    public bool Active { get; private set; }
    public Camera3D FlyCam => Camera.Node;
    public Camera3D CurrentCamera => Active ? FlyCam : Player.Camera;
    public SortedSet<string> Selection { get; } = new(StringComparer.Ordinal);
    public string? HoverId { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public Node3D OutlineRoot { get; private set; } = null!;
    public Node3D MarkerRoot { get; private set; } = null!;
    public event Action? SelectionChanged;

    // outline kinds: node name -> segments last built (ground xz pairs)
    public const string OutlineSelected = "outline_selected", OutlineHover = "outline_hover";
    readonly Dictionary<string, MeshInstance3D> _outlines = new();
    readonly Dictionary<string, int> _segmentCounts = new();
    bool _outlinesDirty = true;
    // which outline kinds need a rebuild: a hover change does not regroup the groups or redraw the selection
    bool _hoverOutlineDirty = true, _selOutlineDirty = true, _groupOutlineDirty = true;
    bool _panelDirty = true;
    // members of each property group and its outer perimeter; both are rebuilt in ReloadGroups only
    readonly Dictionary<string, List<ParcelInfo>> _groupMembers = new();
    readonly Dictionary<string, List<(Vector2, Vector2)>> _perimeters = new();
    // the last ground ray: reused within the same process frame while the map's physics has not changed
    ulong _rayFrame = ulong.MaxValue;
    int _rayVersion = -1;
    Vector3 _rayOrigin, _rayDir, _rayPoint;
    bool _rayHit;

    bool _looking, _rmbMoved;
    Vector2 _rmbPressPos;
    public bool Looking => _looking;
    Vector3? _hoverPoint;

    CanvasLayer _ui = null!;
    PanelContainer _panel = null!, _statusBar = null!;
    Label _unsavedLabel = null!, _hudLabel = null!, _hoverLabel = null!, _statusLabel = null!, _hintLabel = null!;
    Label _selLabel = null!, _groupLabel = null!;
    Button _btnGroup = null!, _btnMerge = null!, _btnAdd = null!, _btnRemove = null!, _btnDissolve = null!;
    double _statusUntil;
    double _pollT;

    public override void _Ready()
    {
        Camera.Build(this);
        OutlineRoot = new Node3D { Name = "EditorOutlines", Visible = false };
        AddChild(OutlineRoot);
        BuildUi();
        Shapes.BuildWorld();
        Sketches.BuildWorld();
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
        }
        Edits.Remember();
        ReloadGroups();
        SetActive(false);
    }

    // ---- mode switch ----

    public void SetActive(bool on)
    {
        if (on == Active && FlyCam.Current == on) return;
        Active = on;
        OutlineRoot.Visible = on;
        MarkerRoot.Visible = on;
        foreach (var part in _parts) part.ActiveChanged(on);
        _ui.Visible = on;
        _looking = false;
        if (on)
        {
            Camera.EnterEditor();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            MarkAllOutlines();
            _panelDirty = true;
            ReloadGroups();
        }
        else
        {
            Camera.LeaveEditor();
            Input.MouseMode = Input.MouseModeEnum.Captured;
            Shapes.CancelShapeGesture();
            if (Fence.Gates.Armed) Fence.Gates.Cancel();
            HoverId = null;
            _hoverLabel.Visible = false;
        }
    }

    /// <summary>Leave the editor and walk from the ground below the fly camera, facing where it looks.</summary>
    public void WalkFromHere()
    {
        if (!Active) return;
        Camera.DropPlayer();
        SetActive(false);
        Say("dropped to the ground; F2 returns to the editor");
    }

    /// <summary>T: snap to a top-down view above the cursor (or the screen centre); again restores the camera.</summary>
    public void ToggleTopDown() => Camera.ToggleTopDown(_hoverPoint);

    // ---- picking ----

    /// <summary>
    /// Ground point under a ray: the first crossing of the terrain height (ray march, buildings and props are
    /// ignored), else a horizontal plane at the mean terrain height.
    /// </summary>
    public bool RayToGround(Vector3 origin, Vector3 dir, out Vector3 point)
    {
        // hover, ghost, gate drag and the shape tools cast the same mouse ray several times a frame
        var frame = Engine.GetProcessFrames();
        if (frame == _rayFrame && _rayVersion == Map.GeometryVersion && origin == _rayOrigin && dir == _rayDir)
        {
            point = _rayPoint;
            return _rayHit;
        }
        var hit = CastGroundRay(origin, dir, out point);
        (_rayFrame, _rayVersion, _rayOrigin, _rayDir, _rayPoint, _rayHit) = (frame, Map.GeometryVersion, origin, dir, point, hit);
        return hit;
    }

    bool CastGroundRay(Vector3 origin, Vector3 dir, out Vector3 point)
    {
        // march the ray against the terrain height (roofs, eaves, fence tops and props must not count as ground)
        var prevT = 0f;
        var h0 = Map.GroundHeight(origin.X, origin.Z);
        var above = float.IsNaN(h0) || origin.Y > h0;
        for (var t = 1f; t <= 3000f; t += Mathf.Max(1f, t * 0.01f))
        {
            var p = origin + dir * t;
            var h = Map.GroundHeight(p.X, p.Z);
            if (float.IsNaN(h)) { above = true; prevT = t; continue; }
            if (p.Y > h) { above = true; prevT = t; continue; }
            if (!above) { prevT = t; continue; } // started below the terrain: wait until the ray comes out of it
            var lo = prevT; var hi = t; // above at lo, below at hi
            for (var i = 0; i < 12; i++)
            {
                var mid = (lo + hi) / 2;
                var m = origin + dir * mid;
                var mh = Map.GroundHeight(m.X, m.Z);
                if (!float.IsNaN(mh) && m.Y <= mh) hi = mid; else lo = mid;
            }
            point = origin + dir * hi;
            return true;
        }
        var plane = Map.MeanTerrainHeight;
        if (dir.Y < -1e-4f)
        {
            var t = (plane - origin.Y) / dir.Y;
            if (t > 0) { point = origin + dir * t; return true; }
        }
        point = default;
        return false;
    }

    /// <summary>Parcel under a ray (null: none); point is the ground point found.</summary>
    public ParcelInfo? PickRay(Vector3 origin, Vector3 dir, out Vector3 point)
        => RayToGround(origin, dir, out point) ? Map.ParcelAt(point.X, point.Z) : null;

    /// <summary>Parcel under a screen position of the main viewport.</summary>
    public ParcelInfo? PickScreen(Vector2 screen, out Vector3 point)
        => PickRay(FlyCam.ProjectRayOrigin(screen), FlyCam.ProjectRayNormal(screen), out point);

    public bool OverUi(Vector2 mouse)
        => _ui.Visible && ((_panel.Visible && _panel.GetGlobalRect().HasPoint(mouse)) || _rail.GetGlobalRect().HasPoint(mouse)
            || _statusBar.GetGlobalRect().HasPoint(mouse) || (_cheat.Visible && _cheat.GetGlobalRect().HasPoint(mouse))
            || Confirm.Contains(mouse) || Commands.Contains(mouse));

    // ---- selection ----

    /// <summary>Click semantics: plain click replaces the selection (empty space clears it), shift toggles one parcel.</summary>
    public void Click(string? parcelId, bool shift)
    {
        var before = string.Join(",", Selection);
        var unit = parcelId == null ? new List<string>() : GroupUnit(parcelId);
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
        // the hover outline depends on the selection too (a selected parcel is not drawn as hover)
        _selOutlineDirty = _hoverOutlineDirty = _outlinesDirty = _panelDirty = true;
        SelectionChanged?.Invoke();
    }

    /// <summary>The parcel itself, or all members of its property group (a group is shown and picked as one shape).</summary>
    List<string> GroupUnit(string parcelId)
    {
        var g = Group(parcelId);
        if (g == null) return new List<string> { parcelId };
        return Members(g).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    IReadOnlyList<ParcelInfo> Members(string group) => _groupMembers.TryGetValue(group, out var l) ? l : Array.Empty<ParcelInfo>();

    /// <summary>Outer perimeter of a property group, computed once until the groups are reloaded.</summary>
    List<(Vector2, Vector2)> Perimeter(string group)
    {
        if (!_perimeters.TryGetValue(group, out var segs)) _perimeters[group] = segs = ParcelOutline.GroupPerimeter(Members(group));
        return segs;
    }

    // ---- groups ----

    public void GroupSelected()
    {
        if (Selection.Count < 2) { Say("select 2 or more parcels to group (Shift+click adds)", true); return; }
        var groups = Selection.Select(Group).Where(g => g != null).Distinct().ToList();
        if (groups.Count == 1 && Selection.All(p => Group(p) == groups[0]))
        {
            Say($"already one group: {groups[0]}", true);
            return;
        }
        if (groups.Count >= 1) { MergeGroups(); return; } // marked parcels touch a group: grouping them makes one new group of everything
        var sel = Selection.ToList();
        Edits.Mutate(root =>
        {
            var (ok, msg, gid) = EditsFile.CreateGroup(root, sel);
            return (ok, ok ? $"created {gid}" : msg);
        });
    }

    /// <summary>Merge the groups of the marked parcels, and marked ungrouped parcels, into one new group. Undoable.</summary>
    public void MergeGroups()
    {
        var gids = Selection.Select(Group).Where(g => g != null).Select(g => g!).Distinct().ToList();
        var extra = Selection.Where(p => Group(p) == null).ToList();
        if (gids.Count < 2 && !(gids.Count == 1 && extra.Count > 0)) { Say("mark parcels from 2 or more groups, or a group and a loose parcel, to merge", true); return; }
        Edits.Mutate(root =>
        {
            var (ok, msg, _) = EditsFile.MergeGroups(root, gids, extra);
            return (ok, msg);
        });
    }

    public void AddSelectedToGroup()
    {
        var groups = Selection.Select(Group).Where(g => g != null).Distinct().ToList();
        if (groups.Count != 1) { Say(groups.Count == 0 ? "select a grouped parcel too, to say which group" : "selection spans several groups", true); return; }
        var add = Selection.Where(p => Group(p) == null).ToList();
        if (add.Count == 0) { Say("nothing to add: every selected parcel is already in a group", true); return; }
        var gid = groups[0]!;
        Edits.Mutate(root =>
        {
            var (ok, msg) = EditsFile.AddToGroup(root, gid, add);
            return (ok, msg);
        });
    }

    public void RemoveSelectedFromGroup()
    {
        var sel = Selection.Where(p => Group(p) != null).ToList();
        if (sel.Count == 0) { Say("none of the selected parcels is in a group", true); return; }
        Edits.Mutate(root => EditsFile.RemoveFromGroups(root, sel));
    }

    public void DissolveGroup()
    {
        var groups = Selection.Select(Group).Where(g => g != null).Distinct().ToList();
        if (groups.Count != 1) { Say(groups.Count == 0 ? "no group in the selection" : "selection spans several groups", true); return; }
        var gid = groups[0]!;
        Edits.Mutate(root => EditsFile.Dissolve(root, gid));
    }

    public string? Group(string parcel) => Map.Parcels.TryGetValue(parcel, out var p) ? p.Group : null;

    /// <summary>Fill ParcelInfo.Group from the edits file on disk.</summary>
    public void ReloadGroups()
    {
        Dictionary<string, string> member;
        try { member = EditsFile.MembershipOf(Edits.Reload()); }
        catch (Exception) { return; } // half-written or invalid: keep what we have
        foreach (var p in Map.Parcels.Values) p.Group = member.GetValueOrDefault(p.Id);
        _groupMembers.Clear();
        _perimeters.Clear();
        foreach (var p in Map.Parcels.Values)
            if (p.Group is { } g)
            {
                if (!_groupMembers.TryGetValue(g, out var l)) _groupMembers[g] = l = new();
                l.Add(p);
            }
        MarkAllOutlines();
        _panelDirty = true;
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
    public void RefreshNow() { RefreshPanel(); RebuildOutlines(); }

    public override void _Notification(int what)
    {
        if (what != NotificationWMCloseRequest) return;
        if (Fence.PendingCount == 0) { GetTree().Quit(); return; }
        if (!Active) SetActive(true); // the confirm needs the editor UI
        GuardUnsaved(() => GetTree().Quit(), "Quit");
    }

    // ---- messages ----

    public void Say(string message, bool error = false)
    {
        StatusMessage = message;
        _statusUntil = Time.GetTicksMsec() / 1000.0 + (error ? 10 : 6);
        _statusLabel.Text = message;
        _statusLabel.Modulate = error ? new Color(1f, 0.55f, 0.45f) : Colors.White;
        GD.Print($"editor: {message}");
    }

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
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wu when !OverUi(wu.Position):
                Camera.SetSpeed(Camera.Speed * 1.25f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wd when !OverUi(wd.Position):
                Camera.SetSpeed(Camera.Speed / 1.25f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Shapes.ShapeTool && !OverUi(mb.Position):
                Shapes.ShapeLeftPress(mb.Position, mb.CtrlPressed);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb when !_looking && Tool == EditorTool.Sketch:
                Sketches.LeftPress(mb.Position, mb.CtrlPressed);
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
                if (HandleZoneKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Sketches.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (Poles.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (HandleUndoKey(k) || Objects.HandleKey(k)) { GetViewport().SetInputAsHandled(); break; }
                if (!k.CtrlPressed && !k.AltPressed && HandleToolKey(k.Keycode)) { GetViewport().SetInputAsHandled(); break; }
                switch (k.Keycode)
                {
                    case Key.F1: ToggleCheatSheet(); break;
                    case Key.F3: GuardUnsaved(WalkFromHere); break;
                    case Key.G: GroupSelected(); break;
                    case Key.T: ToggleTopDown(); break;
                    case Key.O: Ortho.ToggleOrtho(); break;
                    case Key.B: FloorPlan.ToggleFloorPlan(); break;
                    case Key.Enter or Key.KpEnter:
                        if (GetViewport().GuiGetFocusOwner() is (LineEdit or TextEdit)) break;
                        if (Tool == EditorTool.Group) { if (!_btnGroup.Disabled) GroupSelected(); }
                        else Fence.Apply();
                        break;
                    case Key.Escape:
                        if (_cheat.Visible) _cheat.Visible = false;
                        else if (Objects.PlacementAsset != null) Objects.DisarmPlacement();
                        else if (Poles.Plan.Planning) Poles.Plan.Stop();
                        else if (Poles.Connecting) Poles.StopConnecting();
                        else if (Fence.Gates.Armed) Fence.Gates.Cancel();
                        else if (Poles.SelectedCableId != null) Poles.SelectCable(null);
                        else if (Objects.SelectedObjectId != null) Objects.SelectObject(null);
                        else if (Tool == EditorTool.Ground && Ground.Escape()) { }
                        else if (Tool == EditorTool.Zones && ZoneEscape()) { }
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

    // ---- per frame ----

    public override void _Process(double delta)
    {
        if (!Active) return;
        Camera.Fly((float)delta);
        UpdateHover();
        foreach (var part in _parts) part.Update(delta);
        UpdateZoneOverlay();
        _pollT += delta;
        if (_pollT > 0.5)
        {
            _pollT = 0;
            if (Edits.ChangedOnDisk()) ReloadGroups();
        }
        if (_outlinesDirty) RebuildOutlines();
        if (_panelDirty) RefreshPanel();
        _unsavedLabel.Text = Fence.PendingCount == 0 ? "" : $"* {Fence.PendingCount} unsaved (Enter applies)";
        var c = FlyCam.GlobalPosition;
        _hudLabel.Text = $"EDITOR  |  {(Tool == EditorTool.None ? "no tool" : Tool.ToString())}  |  {Selection.Count} selected  |  {Camera.Speed:F0} m/s  |  x {c.X:F0}  y {-c.Z:F0}  z {c.Y:F0}" + (Shapes.SelectedShapeId != null && Shapes.ShapeTool ? $"  |  {(Tool == EditorTool.Zones ? "zone" : "patch")} {Shapes.SelectedShape(Shapes.CurrentKind)?.Label}{(Shapes.SelectedShape(Shapes.CurrentKind) is PatchInfo sp ? "  " + sp.Type : "")}" : "") + (Tool == EditorTool.Zones && MarkedZoneCount > 0 ? $"  |  {MarkedZoneCount} marked for deletion" : "") + (Fence.PendingCount > 0 || Shapes.ShapeDraftPending ? $"  |  * {(Fence.PendingCount > 0 ? $"{Fence.PendingCount} UNSAVED" : "UNSAVED")}" : "");
        if (Time.GetTicksMsec() / 1000.0 > _statusUntil && _statusLabel.Text != "")
            _statusLabel.Text = "";
    }

    bool _hoverForced;

    /// <summary>Force the hovered parcel (smoke tests, screenshots); null returns to mouse hover.</summary>
    public void SetHover(string? id)
    {
        _hoverForced = id != null;
        if (id != HoverId) { HoverId = id; MarkHoverOutline(); }
    }

    void UpdateHover()
    {
        if (_hoverForced) return;
        var mouse = GetViewport().GetMousePosition();
        string? id = null;
        _hoverPoint = null;
        if (!_looking && !OverUi(mouse) && Tool is EditorTool.Zones)
        {
            if (RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var zp)) _hoverPoint = zp; // no parcel highlight: the zones are what is pointed at
        }
        else if (!_looking && !OverUi(mouse) && Tool is not EditorTool.Sketch)
        {
            var p = PickScreen(mouse, out var point);
            if (p != null || point != default) _hoverPoint = point;
            id = p?.Id;
        }
        if (id != HoverId)
        {
            HoverId = id;
            MarkHoverOutline();
        }
        if (Tool is EditorTool.Ground or EditorTool.Zones)
        {
            var calm = _hoverPoint != null && !_looking && !OverUi(mouse) && !Modal && Shapes.Mode is ShapeState.Idle or ShapeState.Selected;
            var text = !calm ? null
                : Tool == EditorTool.Zones ? (ZonesDrawn ? ZoneHoverText(_hoverPoint!.Value) : null)
                : Ground.Overlay.On ? Ground.HoverText(_hoverPoint!.Value) : null;
            _hoverLabel.Visible = text != null;
            if (text != null) { _hoverLabel.Text = text; _hoverLabel.Position = mouse + new Vector2(16, 20); }
            Ground.UpdateHoverOutline(Tool == EditorTool.Ground && calm ? _hoverPoint : null);
            return;
        }
        Ground.UpdateHoverOutline(null);
        if (id == null) { _hoverLabel.Visible = false; return; }
        var info = Map.Parcels[id];
        ShowHoverText(info.Group != null
            ? $"{info.Group} ({GroupUnit(id).Count} parcels)"
            : $"{id} {info.Kind}", mouse);
    }

    /// <summary>The label beside the mouse (a parcel, a zone, a cable target) shows this text.</summary>
    public void ShowHoverText(string text, Vector2 mouse)
    {
        _hoverLabel.Text = text;
        _hoverLabel.Position = mouse + new Vector2(16, 20);
        _hoverLabel.Visible = true;
    }

    // ---- points of the ground on the screen ----

    public SphereMesh HandleMesh => Shapes.World.HandleMesh;
    public StandardMaterial3D HandleHot => Shapes.World.HandleHot;
    const float CornerPickPx = EditorTheme.Space * 2.5f;

    /// <summary>Screen position (px) to the nearest corner of a ring; -1 when none is within reach.</summary>
    public int PickCorner(Vector2 screen, IReadOnlyList<(double x, double y)> ring)
    {
        var best = -1; var bestD = CornerPickPx * (float)Math.Max(1.0, GetViewport().GetVisibleRect().Size.X / 1280.0);
        for (var i = 0; i < ring.Count; i++)
        {
            var w = GroundPoint(ring[i]);
            if (FlyCam.IsPositionBehind(w)) continue;
            var d = FlyCam.UnprojectPosition(w).DistanceTo(screen);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public Vector3 GroundPoint((double x, double y) p)
    {
        var l = Map.FromAbsolute(p.x, p.y);
        return new Vector3(l.X, Map.GroundHeightOrMean(l.X, l.Y) + 0.3f, l.Y);
    }

    public List<(Vector2, Vector2)> RingSegs(IReadOnlyList<(double x, double y)> pts, bool closed)
    {
        var s = new List<(Vector2, Vector2)>();
        var n = closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = Map.FromAbsolute(pts[i].x, pts[i].y); var b = Map.FromAbsolute(pts[(i + 1) % pts.Count].x, pts[(i + 1) % pts.Count].y);
            if (a.DistanceSquaredTo(b) > 1e-8f) s.Add((a, b));
        }
        return s;
    }

    /// <summary>Look at an absolute point from above and south-east, a little left of centre so the sidebar does not cover it (screenshots).</summary>
    public void LookAtAbs(double x, double y, float dist)
    {
        var t = Map.FromAbsolute(x, y);
        var target = new Vector3(t.X, Map.GroundHeightOrMean(t.X, t.Y), t.Y);
        var right = FlyCam.GlobalBasis.X;
        Camera.LookFrom(target + new Vector3(0, dist * 0.8f, dist * 0.6f) + right * dist * 0.15f, target + right * dist * 0.15f);
    }

    // ---- outlines ----

    public int SegmentCount(string kind) => _segmentCounts.GetValueOrDefault(kind);
    public MeshInstance3D? OutlineNode(string kind) => _outlines.GetValueOrDefault(kind);

    public void MarkAllOutlines() => _outlinesDirty = _hoverOutlineDirty = _selOutlineDirty = _groupOutlineDirty = true;

    void MarkHoverOutline() { _hoverOutlineDirty = true; _outlinesDirty = true; }

    /// <summary>Rebuild the outline meshes now (normally done on the next frame after a change). Only the kinds that changed are rebuilt.</summary>
    public void RebuildOutlines()
    {
        _outlinesDirty = false;
        if (_hoverOutlineDirty)
        {
            _hoverOutlineDirty = false;
            var hoverSegs = HoverId != null && !Selection.Contains(HoverId) ? UnitSegments(new[] { HoverId }) : new();
            SetOutline(OutlineHover, hoverSegs, new Color(1f, 1f, 1f, 0.55f), 0.25f, 0.30f);
        }
        if (_selOutlineDirty)
        {
            _selOutlineDirty = false;
            var selSegs = UnitSegments(Selection.Where(Map.Parcels.ContainsKey));
            SetOutline(OutlineSelected, selSegs, new Color(1f, 0.92f, 0.1f), 0.29f, 0.55f);
        }
    }

    /// <summary>Outline of parcels where a property group counts as one shape (outer perimeter only).</summary>
    List<(Vector2, Vector2)> UnitSegments(IEnumerable<string> ids)
    {
        var segs = new List<(Vector2, Vector2)>();
        var seen = new HashSet<string>();
        foreach (var id in ids)
        {
            var g = Group(id);
            if (g == null) { segs.AddRange(ParcelOutline.Edges(Map.Parcels[id])); continue; }
            if (!seen.Add(g)) continue;
            segs.AddRange(Perimeter(g));
        }
        return segs;
    }

    public void SetOutline(string name, List<(Vector2 a, Vector2 b)> segs, Color colour, float lift, float width)
    {
        _segmentCounts[name] = segs.Count;
        if (!_outlines.TryGetValue(name, out var mi))
        {
            mi = new MeshInstance3D
            {
                Name = name,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = colour,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency = colour.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
                },
            };
            OutlineRoot.AddChild(mi);
            _outlines[name] = mi;
        }
        else if (segs.Count > 0 && mi.MaterialOverride is StandardMaterial3D m) // the colour of a kind may change (a selected patch takes its crop's)
        {
            m.AlbedoColor = colour;
            m.Transparency = colour.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
        }
        mi.Mesh = segs.Count == 0 ? null : Gfx.Ribbon(Map, segs, lift, width);
    }

    /// <summary>Texts shown in the panel (for the smoke test).</summary>
    public string PanelSelectionText => _selLabel.Text;
    public string PanelGroupText => _groupLabel.Text;
    public bool GroupButtonEnabled => !_btnGroup.Disabled;

    void RefreshPanel()
    {
        _panelDirty = false;
        var sel = Selection.Where(Map.Parcels.ContainsKey).Select(id => Map.Parcels[id]).ToList();
        var wholeGroup = sel.Count > 0 && sel[0].Group is { } wg && sel.All(p => p.Group == wg)
            && sel.Count == Map.Parcels.Values.Count(p => p.Group == wg) ? wg : null;
        if (sel.Count == 0) _selLabel.Text = "Nothing selected.\nClick a parcel; Shift+click adds.";
        else if (wholeGroup != null)
            _selLabel.Text = $"{wholeGroup}  (property group)\n{sel.Count} parcels\narea: {sel.Sum(p => p.AreaM2):F0} m2\nDissolve the group to edit its parts.";
        else if (sel.Count == 1)
        {
            var p = sel[0];
            _selLabel.Text = $"{p.Id}\nkind: {p.Kind}\narea: {p.AreaM2:F0} m2\ncadastral: {p.CadastralRef}\ngroup: {p.Group ?? "none"}";
        }
        else
        {
            var lines = sel.Take(12).Select(p => $"{p.Id}  {p.Kind}  {p.AreaM2:F0} m2" + (p.Group != null ? $"  [{p.Group}]" : ""));
            _selLabel.Text = $"{sel.Count} parcels selected\n" + string.Join("\n", lines) + (sel.Count > 12 ? $"\n... +{sel.Count - 12} more" : "");
        }
        var gids = sel.Select(p => p.Group).Where(g => g != null).Distinct().ToList();
        if (gids.Count == 0) _groupLabel.Text = "Selection is not in a group.";
        else
        {
            var lines = gids.Select(g => $"{g}: {string.Join(", ", Map.Parcels.Values.Where(p => p.Group == g).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal))}");
            _groupLabel.Text = string.Join("\n", lines);
        }
        _groupCount.Text = sel.Count == 0 ? "Nothing marked yet" : $"{sel.Count} parcel{(sel.Count == 1 ? "" : "s")} marked";
        _btnGroup.Disabled = sel.Count < 2;
        _btnMerge.Disabled = !(gids.Count >= 2 || (gids.Count == 1 && sel.Any(p => p.Group == null)));
        _btnAdd.Disabled = gids.Count != 1 || sel.All(p => p.Group != null);
        _btnRemove.Disabled = gids.Count == 0;
        _btnDissolve.Disabled = gids.Count != 1;
        Fence.RefreshPanel();
        Objects.RefreshPanel();
        RefreshUndoAndHint();
        Poles.RefreshPanel();
        Ground.RefreshPanel();
        RefreshZonesPanel();
        Sketches.RefreshPanel();
    }
}

public partial class EditorMode
{
    Button _btnUndo = null!, _btnRedo = null!; // the rail buttons (EditorUi.cs)
    Label _undoInfo = null!;                   // top of the Objects page

    /// <summary>The hint line of the open shape tool (smoke tests).</summary>
    public string ShapeHint => Shapes.CurrentKind == ShapeKind.Zone ? ZonesHintText() : Ground.HintText();

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

    /// <summary>The undo and redo buttons, the undo line of the Objects page and the hint bar (it names what the tool in use does).</summary>
    void RefreshUndoAndHint()
    {
        var h = Edits.History;
        _btnUndo.Disabled = h.UndoDepth == 0;
        _btnRedo.Disabled = h.RedoDepth == 0;
        _undoInfo.Text = (h.UndoDepth == 0 ? "nothing to undo" : $"undo: {h.NextUndoDescription} ({h.UndoDepth})") + (h.RedoDepth == 0 ? "" : $"; redo: {h.NextRedoDescription} ({h.RedoDepth})");
        var placing = Objects.PlacementAsset != null;
        _hintLabel.Text = Tool == EditorTool.Ground ? Ground.HintText() : Tool == EditorTool.Zones ? ZonesHintText() : Tool == EditorTool.Sketch ? Sketches.HintText() : Tool == EditorTool.Poles && !placing ? Poles.HintText() : Tool == EditorTool.Group && !placing
            ? "marking parcels: click a parcel to mark or unmark it, Enter groups the marked ones, Esc clears the marks"
            : placing
            ? "placing: LMB place, R / Shift+R rotate, Ctrl free position, Esc or RMB stop, Ctrl+Z undo"
            : Objects.SelectedObjectId != null
                ? "object: drag moves, drag the ring rotates, arrows nudge, Del delete, Ctrl+D duplicate, R rotate, Ctrl+Z undo, Esc deselect"
                : "WASD fly, RMB look, LMB select, Shift+LMB add, G group, T top-down, Enter apply, Ctrl+Z undo, Esc back, F1 controls, F2 walk";
    }
}
