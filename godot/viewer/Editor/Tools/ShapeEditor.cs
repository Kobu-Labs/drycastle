using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The polygon editor the two shape tools share: ground PATCHES in the Ground tool and ZONES in the Zones tool (data and file
/// format: <see cref="ShapeDoc"/>). What differs per tool (what a shape carries, its form, its list) is in <see cref="GroundPatchPage"/> and
/// Tools/ZonesTool.cs; the water level of a patch and its retyping are here. The 3D preview is <see cref="ShapeWorld"/>, the
/// sidebar parts are <see cref="ShapePanel"/>.
///
/// States (the hint bar names each): Idle, Drawing (corners placed, nothing saved), Level (a patch of a water type is closed and
/// asks for its water level, still nothing saved), Selected, Dragging (a corner follows the cursor; the file changes on release).
/// Every completed gesture is ONE Mutate = one save = one undo step: create (draw, Enter), retype, water level, move a corner,
/// delete. The saved file is picked up by the EditsWatcher, which runs patch-run for patches and swaps the paint in; zones
/// have no paint, so there is no extra refresh code here. Esc goes one level up: drag, water level, drawing, selection
/// (shape, then type or kind), tool.
/// </summary>
public sealed class ShapeEditor : EditorPart
{
    readonly List<(double x, double y)> _draft = new();
    string? _drawKind;
    int _activeCorner, _dragCorner = -1; // _dragCorner is the payload of the Dragging state
    (double x, double y) _dragPt;
    bool _cornerMoved;

    // Editor.Shape (docs/state-machines.md): the stored state. Drawing is entered by N or the first corner, Level by closing a water patch, Dragging by grabbing a
    // corner; Idle and Selected follow the selection, so every selection change goes through the setters below.
    readonly Drycastle.ShooterCore.Fsm.StateMachine<ShapeState> _shape = new(Drycastle.ShooterCore.Editor.EditorMachines.ShapeSpec);
    string? _selectedPatchId, _selectedZoneId;

    public ShapeEditor(IEditorHost host) : base(host)
    {
        World = new ShapeWorld(host, this);
        Panel = new ShapePanel(host, this);
    }

    public ShapeWorld World { get; }
    public ShapePanel Panel { get; }

    // ---- the parts' hooks ----

    public void BuildWorld() => World.Build();

    public override void LoadConfig(ConfigFile cfg)
    {
        var patch = (string)cfg.GetValue("ui", "patch", "");
        if (patch != "") RestorePatch = patch;
        var zone = (string)cfg.GetValue("ui", "zone", "");
        if (zone != "") RestoreZone = zone;
    }

    public override void SaveConfig(ConfigFile cfg)
    {
        cfg.SetValue("ui", "patch", SelectedPatchId ?? "");
        cfg.SetValue("ui", "zone", SelectedZoneId ?? "");
    }

    /// <summary>Per frame: the cursor and the corner under it, a dragged corner follows, the preview is redrawn.</summary>
    public override void Update(double delta)
    {
        if (!ShapeTool) { World.Off(); return; }
        var mouse = Host.GetViewport().GetMousePosition();
        var free = Input.IsKeyPressed(Key.Ctrl);
        var over = Host.Chrome.OverUi(mouse) || Host.Looking || Host.Modal;
        Cursor = null;
        if (!over && Host.Map.OriginEpsg5514 != null && Host.Picker.RayToGround(Host.FlyCam.ProjectRayOrigin(mouse), Host.FlyCam.ProjectRayNormal(mouse), out var g)) Cursor = Host.Map.SnapAbs(g, free);
        HoverCorner = -1;
        var mode = Mode;
        if (!over && mode is ShapeState.Selected && SelectedShape(CurrentKind) is { } sr) HoverCorner = Host.Picker.PickCorner(mouse, sr.Points);
        if (!over && mode is ShapeState.Drawing && _draft.Count >= 3 && Host.Picker.PickCorner(mouse, new[] { _draft[0] }) == 0) HoverCorner = 0;
        if (_dragCorner >= 0 && Cursor is { } c) DragCornerToAbs(c.x, c.y);
        World.Draw();
    }

    // ---- state ----

    public string? SelectedPatchId { get => _selectedPatchId; internal set { _selectedPatchId = value; ShapeRest(); } }
    public string? SelectedZoneId { get => _selectedZoneId; internal set { _selectedZoneId = value; ShapeRest(); } }
    /// <summary>What the editor was left with (editor_ui.cfg): picked up again by the page once the lists are there.</summary>
    public string? RestorePatch { get; set; }
    public string? RestoreZone { get; set; }
    public IReadOnlyList<(double x, double y)> ShapeDraft => _draft;
    public bool ShapeDragging => _dragCorner >= 0;
    public int ActiveCorner { get => _activeCorner; internal set => _activeCorner = value; }
    public int DragCorner => _dragCorner;
    internal (double x, double y) DragPoint => _dragPt;
    /// <summary>The ground point under the mouse (absolute metres, on the grid unless Ctrl); null when the mouse is not over the ground.</summary>
    internal (double x, double y)? Cursor { get; private set; }
    internal int HoverCorner { get; private set; } = -1;
    internal string? DrawType { get; private set; }
    internal double NewWaterY => _newWaterY;
    double _newWaterY;

    /// <summary>True in the two tools that draw polygons (Ground: patches, Zones: zones).</summary>
    public bool ShapeTool => Host.Tool is EditorTool.Ground or EditorTool.Zones;
    /// <summary>What the open tool draws.</summary>
    public ShapeKind CurrentKind => Host.Tool == EditorTool.Zones ? ShapeKind.Zone : ShapeKind.Patch;
    public string? SelectedShapeId => CurrentKind == ShapeKind.Zone ? SelectedZoneId : SelectedPatchId;

    public ShapeState Mode => _shape.Current;
    internal bool InLevel => _shape.Is(ShapeState.Level);

    /// <summary>Idle or Selected, whichever the selection says (no gesture under way); a gesture keeps its state.</summary>
    public void ShapeRest()
    {
        if (_shape.IsAny(ShapeState.Idle, ShapeState.Selected)) _shape.Enter(SelectedShapeId != null ? ShapeState.Selected : ShapeState.Idle);
    }

    /// <summary>The Drawing or Level gesture ends (created, cancelled, last corner taken back): back to Idle or Selected. A corner drag is not touched.</summary>
    void ShapeLeaveDraft()
    {
        if (_shape.IsAny(ShapeState.Drawing, ShapeState.Level)) _shape.Enter(SelectedShapeId != null ? ShapeState.Selected : ShapeState.Idle);
    }

    /// <summary>The one place a corner drag ends (release, Esc, selection changed, shape gone, tool left): payload cleared, Dragging left.</summary>
    public void EndCornerGesture()
    {
        _dragCorner = -1;
        if (_shape.Is(ShapeState.Dragging)) _shape.Enter(SelectedShapeId != null ? ShapeState.Selected : ShapeState.Idle);
    }

    /// <summary>A shape drawn but not yet saved (the unsaved marker in the HUD).</summary>
    public bool ShapeDraftPending => _draft.Count > 0;

    void ShapeChanged()
    {
        Host.MarkPanelDirty();
        World.Invalidate();
    }

    // ---- data ----

    /// <summary>The cached lists of the file on disk (shared: callers must not keep them across a reload).</summary>
    internal List<PatchInfo> PatchList()
    {
        var disk = Host.Edits.Disk(); // _disk is replaced on every reload, so its identity keys the cache
        if (ReferenceEquals(disk, _patchesFor) && _patchCache != null) return _patchCache;
        var list = new List<PatchInfo>();
        _patchesFor = disk;
        _patchCache = list;
        if (ShapeDoc.ShapesOf(disk, ShapeKind.Patch) is { } po)
            foreach (var (id, e) in po)
                if (e is Obj r && r.GetValueOrDefault("type") is string t && ShapeDoc.ParsePolygon(r.GetValueOrDefault("polygon")) is { } ring)
                    list.Add(new PatchInfo(id, t, ring, r.ContainsKey("water_y") ? ObjectEdits.Number(r["water_y"]) : null));
        return list;
    }
    Obj? _patchesFor;
    List<PatchInfo>? _patchCache;

    /// <summary>Patches in the file as last read, sorted by ID.</summary>
    public List<PatchInfo> Patches() => new(PatchList());
    public PatchInfo? Patch(string id) => PatchList().FirstOrDefault(p => p.Id == id);

    internal List<ShapeInfo> ShapeList(ShapeKind k) => k == ShapeKind.Patch ? PatchList().Cast<ShapeInfo>().ToList() : Host.Zones.List().Cast<ShapeInfo>().ToList();
    ShapeInfo? ShapeById(ShapeKind k, string id) => k == ShapeKind.Patch ? Patch(id) : Host.Zones.Find(id);
    public ShapeInfo? SelectedShape(ShapeKind k) => SelectedOf(k) is { } id ? ShapeById(k, id) : null;
    public string? SelectedOf(ShapeKind k) => k == ShapeKind.Patch ? SelectedPatchId : SelectedZoneId;

    public string NextShapeIdOf(string text, ShapeKind k) => ShapeDoc.NextShapeId(EditsFile.Parse(text), k);

    /// <summary>The shape of a kind under a map point; where shapes overlap the higher ID wins (patches: as on the ground; zones: it is the one clicked).</summary>
    public string? ShapeAt(ShapeKind k, double x, double y)
        => k == ShapeKind.Zone ? Host.Zones.AtPoint(x, y) : ShapeList(k).Where(s => ShapeDoc.PointInRing(s.Points, x, y)).Select(s => s.Id).OrderBy(ShapeDoc.ShapeNumber).ThenBy(i => i, StringComparer.Ordinal).LastOrDefault();

    /// <summary>Why the shape being drawn cannot be closed yet, or null when it can. Shown in the panel before anything is committed.</summary>
    public string? ShapeDraftProblem()
    {
        if (_draft.Count == 0) return "no corners yet";
        var code = ObjectEdits.RingProblemCode(_draft);
        return code == ObjectEdits.RingOk ? null : code == ObjectEdits.RingTooFew ? $"needs at least 3 corners ({_draft.Count} placed)" : "the outline " + ShapeDoc.RingMessage(code);
    }

    // ---- drawing ----

    /// <summary>Why a shape cannot be started now (null: it can): no origin, no type or kind to draw, a gesture in progress.</summary>
    internal string? CannotDraw(ShapeKind k)
    {
        if (Host.Map.OriginEpsg5514 == null) return "this map has no origin_epsg5514 (export it again, map 0.4.0+); patches and zones need it";
        if (Mode is ShapeState.Drawing or ShapeState.Level or ShapeState.Dragging) return "finish or cancel the shape you are drawing first (Esc)";
        return k == ShapeKind.Patch ? Host.Ground.CannotDrawPatch() : Host.Zones.CannotDraw();
    }

    /// <summary>N: leave any selection and wait for the first corner; clicks then add corners even over existing shapes.</summary>
    public void StartShape()
    {
        if (!ShapeTool) return;
        var k = CurrentKind;
        if (CannotDraw(k) is { } why) { Host.Say(why, true); return; }
        SelectShape(k, null);
        if (k == ShapeKind.Patch) DrawType = Host.Ground.SelectedType; else _drawKind = Host.Zones.SelectedKind;
        _shape.Enter(ShapeState.Drawing); // waiting for the first corner
        Host.Say(k == ShapeKind.Patch
            ? $"new patch of {DrawType}: click corners on the ground, Enter closes the shape, Esc cancels"
            : $"new {Host.Zones.KindLabel(_drawKind!)} zone: click corners on the ground, Enter creates it, Esc cancels");
        ShapeChanged();
    }

    bool ShapesReady()
    {
        if (Host.Map.OriginEpsg5514 != null) return true;
        Host.Say("this map has no origin_epsg5514 (export it again, map 0.4.0+); patches and zones need it", true);
        return false;
    }

    /// <summary>Add a corner (absolute S-JTSK metres). Refused when the new edge would cross or touch the outline so far.</summary>
    public bool AddShapePoint(double x, double y)
    {
        if (_shape.Is(ShapeState.Level)) { Host.Say("the shape is closed: set the water level and press Enter, or Esc to go back to the corners", true); return false; }
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (!ObjectEdits.ChainAccepts(_draft, p)) { Host.Say("not placed: the new edge would cross or touch the outline", true); return false; }
        if (_draft.Count == 0) SelectShape(CurrentKind, null);
        _draft.Add(p);
        _shape.Enter(ShapeState.Drawing);
        ShapeChanged();
        return true;
    }

    /// <summary>Backspace: take the last corner back.</summary>
    public bool RemoveLastShapePoint()
    {
        if (_shape.Is(ShapeState.Level)) { _shape.Enter(ShapeState.Drawing); ShapeChanged(); return true; }
        if (_draft.Count == 0) return false;
        _draft.RemoveAt(_draft.Count - 1);
        if (_draft.Count == 0) ShapeLeaveDraft();
        ShapeChanged();
        return true;
    }

    /// <summary>
    /// Enter while drawing: the closing edge is the last corner back to the first. A zone, or a patch of a type that is not water, is
    /// created now; a patch of a water type asks for its water level first (Enter again creates it). Refused when the shape is invalid.
    /// </summary>
    public bool CloseShape()
    {
        if (_shape.Is(ShapeState.Level)) return CommitShape();
        if (ShapeDraftProblem() is { } why) { Host.Say("cannot close the shape: " + why, true); return false; }
        if (CurrentKind == ShapeKind.Patch && Host.Ground.PatchTypeIsWater(DrawType))
        {
            _shape.Enter(ShapeState.Level);
            _newWaterY = DefaultWaterLevel(_draft);
            Host.Say("shape closed: set the water level (m), Enter creates the patch, Esc goes back to the corners");
            ShapeChanged();
            return true;
        }
        return CommitShape();
    }

    /// <summary>Create the shape from the drawn corners: one save, one undo step.</summary>
    public bool CommitShape()
    {
        var k = CurrentKind;
        if (ShapeDraftProblem() is { } why) { Host.Say("cannot create the " + ShapeDoc.NounOf(k) + ": " + why, true); return false; }
        var ring = _draft.ToList();
        var type = DrawType; var kind = _drawKind; var level = _shape.Is(ShapeState.Level) ? _newWaterY : (double?)null;
        if (k == ShapeKind.Patch && type == null) { Host.Say("no ground type to draw: pick one in the list", true); return false; }
        if (k == ShapeKind.Zone && kind == null) { Host.Say("no zone kind to draw: pick one in the list", true); return false; }
        string? id = null;
        var ok = Host.Edits.Mutate(root =>
        {
            if (ShapeDoc.ShapesOf(root, k) is not { } so) root[ShapeDoc.KeyOf(k)] = so = EditsFile.NewObject();
            id = ShapeDoc.NextShapeId(root, k);
            var e = EditsFile.NewObject();
            if (k == ShapeKind.Patch) { e["type"] = type; if (level is { } wy) e["water_y"] = ObjectEdits.N3(wy); }
            else e["kind"] = kind;
            e["polygon"] = ShapeDoc.PolygonValue(ring);
            so[id] = e;
            return (true, $"created {id}");
        });
        if (!ok) return false;
        _draft.Clear();
        ShapeLeaveDraft();
        SelectShape(k, id);
        return true;
    }

    /// <summary>Drop the shape being drawn (Esc). Nothing was saved, so nothing is undone.</summary>
    public void CancelShapeDraft(bool say = true)
    {
        var had = _draft.Count > 0 || _shape.IsAny(ShapeState.Drawing, ShapeState.Level);
        _draft.Clear();
        ShapeLeaveDraft();
        if (had && say) Host.Say("drawing cancelled, nothing was saved");
        ShapeChanged();
    }

    /// <summary>Leave every shape gesture (switching tools, leaving the editor).</summary>
    public void CancelShapeGesture()
    {
        CancelShapeCornerDrag();
        if (_draft.Count > 0) Host.Say($"unfinished shape discarded ({_draft.Count} corner{(_draft.Count == 1 ? "" : "s")}, never saved)");
        _draft.Clear();
        ShapeLeaveDraft();
        ShapeChanged();
    }

    // ---- selection and edits ----

    public bool SelectShape(ShapeKind k, string? id)
    {
        var noun = ShapeDoc.NounOf(k);
        if (id != null && ShapeById(k, id) == null) { Host.Say($"{noun} {id} does not exist", true); return false; }
        if (id == SelectedOf(k)) return true;
        EndCornerGesture(); // a corner drag belongs to the shape that was selected
        if (k == ShapeKind.Patch) SelectedPatchId = id; else SelectedZoneId = id;
        _activeCorner = 0;
        if (id != null) FollowShape(k, id);
        ShapeChanged();
        Host.SaveUiConfig();
        return true;
    }

    /// <summary>Selecting a shape selects its type or kind too (the list row, the form and the map highlight follow it).</summary>
    void FollowShape(ShapeKind k, string id)
    {
        if (k == ShapeKind.Patch) { if (Patch(id) is { } p) { Host.Ground.SelectType(p.Type); Host.Ground.FocusPatch = true; } }
        else if (Host.Zones.Find(id) is { } z) Host.Zones.SelectKind(z.Kind);
    }

    /// <summary>Move one corner (absolute metres): one save. Refused (and nothing saved) when the shape would cross itself.</summary>
    public bool MoveShapeCorner(ShapeKind k, string id, int corner, double x, double y)
    {
        var noun = ShapeDoc.NounOf(k);
        if (ShapeById(k, id) is not { } r) { Host.Say($"{noun} {id} does not exist", true); return false; }
        if (corner < 0 || corner >= r.Points.Count) { Host.Say($"{r.Label} has no corner {corner + 1}", true); return false; }
        var ring = r.Points.ToList();
        ring[corner] = (Math.Round(x, 3), Math.Round(y, 3));
        if (ring[corner] == r.Points[corner]) return false;
        var code = ObjectEdits.RingProblemCode(ring);
        if (code != ObjectEdits.RingOk) { Host.Say($"corner not moved: the outline would {ShapeDoc.RingMessage(code)}", true); return false; }
        if (r is ZoneInfo { FromRule: true } rz) return Host.Zones.EditRuleZone(rz, null, ring, $"moved corner {corner + 1} of {rz.Label}");
        return Host.Edits.Mutate(root =>
        {
            if (ShapeDoc.ShapesOf(root, k)?.GetValueOrDefault(id) is not Obj e) return (false, $"{noun} {id} no longer exists");
            e["polygon"] = ShapeDoc.PolygonValue(ring);
            return (true, $"moved corner {corner + 1} of {id}");
        });
    }

    public bool DeleteShape(ShapeKind k, string id)
    {
        var noun = ShapeDoc.NounOf(k);
        if (k == ShapeKind.Zone && Host.Zones.Find(id) is { FromRule: true } rz)
        {
            var gone = Host.Zones.EditRuleZone(rz, null, null, $"deleted {rz.Label} (Ctrl+Z undoes)");
            if (gone && SelectedOf(k) == id) SelectShape(k, null);
            return gone;
        }
        var ok = Host.Edits.Mutate(root =>
        {
            var so = ShapeDoc.ShapesOf(root, k);
            if (so == null || !so.Remove(id)) return (false, $"{noun} {id} no longer exists");
            if (so.Count == 0) root.Remove(ShapeDoc.KeyOf(k));
            return (true, $"deleted {id} (Ctrl+Z undoes)");
        });
        if (ok && SelectedOf(k) == id) SelectShape(k, null);
        return ok;
    }

    /// <summary>Del: ask first, naming the shape and what happens; accepting deletes it (undo brings it back).</summary>
    public bool RequestDeleteShape()
    {
        var k = CurrentKind;
        if (SelectedShape(k) is not { } s) { Host.Say($"no {ShapeDoc.NounOf(k)} selected", true); return false; }
        var id = s.Id; var name = s.Label;
        var what = s is PatchInfo p ? $"{name} ({p.Type}, {p.Points.Count} corners, {p.AreaM2:F0} m2) is removed from the ground; the land under it goes back to what is below."
            : s is ZoneInfo z ? $"{name} ({Host.Zones.KindLabel(z.Kind)}, {z.Points.Count} corners, {z.AreaM2:F0} m2) is removed; the ground is not changed." : name;
        Host.Confirm.Show($"Delete {ShapeDoc.NounOf(k)} {name}?", what + " Undo (Ctrl+Z) brings it back.", "Delete", () => DeleteShape(k, id));
        return true;
    }

    // ---- patches: the type and the water level ----

    /// <summary>A starting water level for a patch: the terrain height at the middle of its corners, to one decimal; the form then asks for the real one.</summary>
    double DefaultWaterLevel(IReadOnlyList<(double x, double y)> ring)
    {
        if (ring.Count == 0) return 0;
        var l = Host.Map.FromAbsolute(ring.Average(p => p.x), ring.Average(p => p.y));
        return Math.Round(Host.Map.GroundHeightOrMean(l.X, l.Y), 1);
    }

    /// <summary>Give a patch another ground type (one save). A water type needs a water level: the patch gets the terrain height at its middle to start from (edit it in the form); a land type takes the level away.</summary>
    public bool SetPatchType(string id, string type)
    {
        if (Patch(id) is not { } p) { Host.Say($"patch {id} does not exist", true); return false; }
        if (p.Type == type) return true;
        if (Host.Ground.PatchTypeChoices().All(c => c.Name != type)) { Host.Say($"{type} is not a ground type a patch can be of (deleted, a rule's, or not in this map)", true); return false; }
        var water = Host.Ground.PatchTypeIsWater(type);
        var level = DefaultWaterLevel(p.Points);
        return Host.Edits.Mutate(root =>
        {
            if (ShapeDoc.ShapesOf(root, ShapeKind.Patch)?.GetValueOrDefault(id) is not Obj e) return (false, $"patch {id} no longer exists");
            e["type"] = type;
            if (water) { if (!e.ContainsKey("water_y")) e["water_y"] = ObjectEdits.N3(level); }
            else { e.Remove("water_y"); e.Remove("max_depth"); }
            return (true, $"{id} is now {type}" + (water && p.WaterY == null ? $", water level {level:0.0} m to start from" : ""));
        });
    }

    /// <summary>Set the water level of a patch of a water type (one save).</summary>
    public bool SetPatchWater(string id, double level)
    {
        if (Patch(id) is not { } p) { Host.Say($"patch {id} does not exist", true); return false; }
        if (!Host.Ground.PatchTypeIsWater(p.Type)) { Host.Say($"{id} is of {p.Type}, which is not water: it has no water level", true); return false; }
        var v = Math.Round(level, 3, MidpointRounding.AwayFromZero);
        if (p.WaterY == v) return true;
        return Host.Edits.Mutate(root =>
        {
            if (ShapeDoc.ShapesOf(root, ShapeKind.Patch)?.GetValueOrDefault(id) is not Obj e) return (false, $"patch {id} no longer exists");
            e["water_y"] = ObjectEdits.N3(v);
            return (true, $"{id} water level {v:0.0###} m");
        });
    }

    /// <summary>The water level field changed: it sets the level of the patch being drawn, or of the selected patch (one save).</summary>
    internal void OnWaterLevelChanged(double v)
    {
        if (Mode == ShapeState.Level) { _newWaterY = v; return; }
        if (CurrentKind == ShapeKind.Patch && Mode == ShapeState.Selected && SelectedPatchId is { } id) SetPatchWater(id, v);
    }

    // test hooks
    public void ChoosePatchType(string type) { if (SelectedPatchId != null) SetPatchType(SelectedPatchId, type); }
    public void SetDrawWaterLevel(double v) { _newWaterY = v; ShapePanel.SetLevel(Panel.Of(ShapeKind.Patch), v); }

    // ---- corners ----

    public bool StartCornerDrag(int corner)
    {
        if (SelectedShape(CurrentKind) is not { } r || corner < 0 || corner >= r.Points.Count) return false;
        _dragCorner = corner; _activeCorner = corner; _dragPt = r.Points[corner]; _cornerMoved = false;
        _shape.Enter(ShapeState.Dragging);
        ShapeChanged();
        return true;
    }

    /// <summary>Drag: the corner follows the point (0.5 m grid unless free). Only the preview changes until release.</summary>
    public void DragCornerToAbs(double x, double y)
    {
        if (_dragCorner < 0) return;
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (p == _dragPt) return;
        _dragPt = p; _cornerMoved = true;
    }

    /// <summary>Release: one save with the final corner (nothing when it did not move; refused when the shape would cross itself).</summary>
    public bool EndCornerDrag()
    {
        if (_dragCorner < 0) return false;
        var id = SelectedShapeId;
        var c = _dragCorner; var pt = _dragPt; var moved = _cornerMoved;
        EndCornerGesture(); // always, even when the shape is gone: a drag left behind would block N and every other gesture
        ShapeChanged();
        return id != null && moved && MoveShapeCorner(CurrentKind, id, c, pt.x, pt.y);
    }

    public void CancelShapeCornerDrag()
    {
        if (_dragCorner < 0) return;
        EndCornerGesture();
        ShapeChanged();
    }

    internal static bool RingBad(IReadOnlyList<(double x, double y)> ring) => ObjectEdits.RingProblemCode(ring) != ObjectEdits.RingOk;

    /// <summary>The selected shape's corners with the dragged one at its new place; null when no corner is dragged.</summary>
    internal List<(double x, double y)>? DragRing()
    {
        if (_dragCorner < 0 || SelectedShape(CurrentKind) is not { } r) return null;
        var ring = r.Points.ToList();
        if (_dragCorner < ring.Count) ring[_dragCorner] = _dragPt;
        return ring;
    }

    /// <summary>[ and ]: choose the active corner of the selected shape (keyboard alternative to the mouse); arrows then nudge it.</summary>
    public void CycleCorner(int by)
    {
        if (SelectedShape(CurrentKind) is not { } r) return;
        _activeCorner = ((_activeCorner + by) % r.Points.Count + r.Points.Count) % r.Points.Count;
        Host.Say($"{r.Label} corner {_activeCorner + 1} of {r.Points.Count}: arrows move it, Del deletes the {ShapeDoc.NounOf(CurrentKind)}");
        ShapeChanged();
    }

    public bool NudgeCorner(int right, int forward)
    {
        if (SelectedShape(CurrentKind) is not { } r || _activeCorner >= r.Points.Count) return false;
        var cam = Host.FlyCam;
        var f = -cam.GlobalBasis.Z; var rt = cam.GlobalBasis.X;
        if (Mathf.Abs(f.Y) > 0.98f) f = cam.GlobalBasis.Y;
        var dir = new Vector2(rt.X * right + f.X * forward, -(rt.Z * right + f.Z * forward));
        if (dir.LengthSquared() < 1e-6f) return false;
        var step = Mathf.Abs(dir.X) >= Mathf.Abs(dir.Y) ? new Vector2(Mathf.Sign(dir.X), 0) : new Vector2(0, Mathf.Sign(dir.Y));
        var p = r.Points[_activeCorner];
        return MoveShapeCorner(CurrentKind, r.Id, _activeCorner, p.x + step.X * ObjectsTool.NudgeStep, p.y + step.Y * ObjectsTool.NudgeStep);
    }

    // ---- keys and mouse ----

    /// <summary>Keys of the shape tools (Ground and Zones): drawing, corners, delete. True when used.</summary>
    public bool HandleShapeKey(InputEventKey k)
    {
        if (!ShapeTool || Host.Chrome.CheatSheetShown) return false;
        var mode = Mode;
        if (k.CtrlPressed)
            return k.Keycode == Key.Z && !k.ShiftPressed && (_draft.Count > 0 || InLevel) && RemoveLastShapePoint();
        if (k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.N when !k.ShiftPressed: StartShape(); return true;
            case Key.Enter or Key.KpEnter:
                if (InLevel || _draft.Count > 0) { CloseShape(); return true; }
                return false;
            case Key.Backspace: return RemoveLastShapePoint();
            case Key.Delete when k.ShiftPressed && CurrentKind == ShapeKind.Zone && mode is ShapeState.Idle or ShapeState.Selected: Host.Zones.Marks.RequestDelete(); return true;
            case Key.Delete when mode == ShapeState.Selected && (CurrentKind == ShapeKind.Zone || Host.Ground.FocusPatch): RequestDeleteShape(); return true;
            case Key.C when _shape.Is(ShapeState.Drawing):
                AddCornerAtCursor();
                return true;
            case Key.Bracketleft when mode == ShapeState.Selected: CycleCorner(-1); return true;
            case Key.Bracketright when mode == ShapeState.Selected: CycleCorner(1); return true;
            case Key.Left when mode == ShapeState.Selected: NudgeCorner(-1, 0); return true;
            case Key.Right when mode == ShapeState.Selected: NudgeCorner(1, 0); return true;
            case Key.Up when mode == ShapeState.Selected: NudgeCorner(0, 1); return true;
            case Key.Down when mode == ShapeState.Selected: NudgeCorner(0, -1); return true;
            case Key.Escape: return ShapeEscape();
        }
        return false;
    }

    /// <summary>C: a corner at the cursor, or at the screen centre when the cursor is over the panel (keyboard-only drawing).</summary>
    void AddCornerAtCursor()
    {
        var mouse = Host.GetViewport().GetMousePosition();
        var at = Host.Chrome.OverUi(mouse) ? Host.GetViewport().GetVisibleRect().Size / 2 : mouse;
        if (ShapesReady() && Host.Picker.RayToGround(Host.FlyCam.ProjectRayOrigin(at), Host.FlyCam.ProjectRayNormal(at), out var g))
        {
            var (x, y) = Host.Map.SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
            AddShapePoint(x, y);
        }
    }

    /// <summary>Esc goes one level up: drag, water level (back to the corners), drawing, the selected shape. False when there is nothing of that left (the tool then steps back itself).</summary>
    public bool ShapeEscape()
    {
        switch (Mode)
        {
            case ShapeState.Dragging: CancelShapeCornerDrag(); Host.Say("move cancelled"); return true;
            case ShapeState.Level: _shape.Enter(ShapeState.Drawing); Host.Say("back to the corners: click to add more, Enter closes again"); ShapeChanged(); return true;
            case ShapeState.Drawing: CancelShapeDraft(); return true;
            case ShapeState.Selected when CurrentKind == ShapeKind.Zone || Host.Ground.FocusPatch: SelectShape(CurrentKind, null); Host.Ground.FocusPatch = false; return true;
        }
        return false;
    }

    /// <summary>Left press in a shape tool: add a corner while drawing, else drag a corner of the selected shape, select the shape under the cursor, or fall back to the tool's own pick (the type under the cursor, the zone kind).</summary>
    public void ShapeLeftPress(Vector2 screen, bool free)
    {
        if (Host.Modal || !ShapesReady()) return;
        Host.GetViewport().GuiReleaseFocus();
        var k = CurrentKind;
        if (InLevel) { Host.Say("the shape is closed: set the water level, Enter creates it, Esc goes back to the corners", true); return; }
        var drafting = _shape.Is(ShapeState.Drawing);
        if (!drafting && SelectedShape(k) is { } sr)
        {
            var c = Host.Picker.PickCorner(screen, sr.Points);
            if (c >= 0) { StartCornerDrag(c); return; }
        }
        if (drafting && _draft.Count >= 3 && Host.Picker.PickCorner(screen, new[] { _draft[0] }) == 0) { CloseShape(); return; }
        if (!Host.Picker.RayToGround(Host.FlyCam.ProjectRayOrigin(screen), Host.FlyCam.ProjectRayNormal(screen), out var g)) { Host.Say("click on the ground", true); return; }
        if (drafting) { var (x, y) = Host.Map.SnapAbs(g, free); AddShapePoint(x, y); return; }
        var (rx, ry) = Host.Map.RawAbs(g);
        if (ShapeAt(k, rx, ry) is { } hit)
        {
            if (k == ShapeKind.Zone && free) Host.Zones.Marks.Toggle(hit); else SelectShape(k, hit); // Ctrl+click: mark for the mass delete
            return;
        }
        if (k == ShapeKind.Zone && free) { Host.Say("no zone there to mark"); return; }
        if (SelectedOf(k) != null) SelectShape(k, null);
        if (k == ShapeKind.Patch) Host.Ground.Pick(g);
        else Host.Say(Host.Zones.SelectedKind is { } zk ? $"no {Host.Zones.KindLabel(zk)} zone there (Esc shows all kinds)" : "no zone there");
    }

    // ---- hints ----

    /// <summary>The hint line while a shape gesture is under way or a shape is selected; null when there is none (the tool's own hint applies).</summary>
    public string? ShapeHintText()
    {
        var noun = ShapeDoc.NounOf(CurrentKind);
        return Mode switch
        {
            ShapeState.Dragging => "moving a corner: release saves, Esc cancels, red means the shape would cross itself",
            ShapeState.Level => "shape closed: set the water level, Enter creates the patch, Esc goes back to the corners",
            ShapeState.Drawing => _draft.Count == 0
                ? $"new {noun}: LMB or C places the first corner, Ctrl free position, Esc cancels"
                : $"drawing {_draft.Count}: LMB or C adds, Enter {(CurrentKind == ShapeKind.Patch && Host.Ground.PatchTypeIsWater(DrawType) ? "closes" : "creates")}, Backspace undoes a corner, Esc cancels",
            ShapeState.Selected when CurrentKind == ShapeKind.Zone || Host.Ground.FocusPatch =>
                $"{SelectedShape(CurrentKind)?.Label}: drag a corner, [ ] and arrows move one, Del deletes, {(CurrentKind == ShapeKind.Zone ? "X marks for deletion, " : "")}N new {noun}, Ctrl+Z undoes, Esc deselects",
            _ => null,
        };
    }
}
