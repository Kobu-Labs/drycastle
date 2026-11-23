using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The polygon machinery the two shape tools share: ground PATCHES in the Ground tool (edits.json `ground_patches`,
/// {GP-NNNN: {type, polygon, water_y?}}) and ZONES in the Zones tool (`zones`, {Z-NNNN: {kind, polygon}}), both in absolute
/// S-JTSK metres (GLOSSARY "Ground patch", "Zone"; ADR 0022). What differs per tool (what a shape carries, its form, its
/// list) is in EditorMode.Patches.cs and EditorMode.Zones.cs.
///
/// States (the hint bar names each): Idle, Drawing (corners placed, nothing saved), Level (a patch of a water type is closed and
/// asks for its water level, still nothing saved), Selected, Dragging (a corner follows the cursor; the file changes on release).
/// Every completed gesture is ONE Mutate = one save = one undo step: create (draw, Enter), retype, water level, move a corner,
/// delete. The saved file is picked up by the EditsWatcher, which runs patch-run for patches and swaps the paint in; zones
/// have no paint, so there is no extra refresh code here. Esc goes one level up: drag, water level, drawing, selection
/// (shape, then type or kind), tool.
/// </summary>
public enum ShapeKind { Patch, Zone }

public partial class EditorMode
{
    /// <summary>One drawn polygon: its ID and corners in absolute metres.</summary>
    public record ShapeInfo(string Id, List<(double x, double y)> Points)
    {
        /// <summary>What the user reads for this shape (its ID; a zone that comes from a rule has the ground area's).</summary>
        public virtual string Label => Id;
        public double AreaM2
        {
            get
            {
                double a = 0;
                for (var i = 0; i < Points.Count; i++) { var p = Points[i]; var q = Points[(i + 1) % Points.Count]; a += p.x * q.y - q.x * p.y; }
                return Math.Abs(a) / 2;
            }
        }
    }

    /// <summary>A ground patch: exactly one ground type, plus the height of the water surface when the type is water.</summary>
    public sealed record PatchInfo(string Id, string Type, List<(double x, double y)> Points, double? WaterY) : ShapeInfo(Id, Points);

    /// <summary>
    /// A zone: exactly one zone kind of the catalog. Drawn ones are `zones.Z-NNNN`; one that comes from a rule (a ground area of
    /// the kind's where_ground types, minus the cut ones) has <see cref="AreaPart"/> (its ground area, `A-0012`, `A-0012.2` for the
    /// second outline of a multi-part one) and an ID of area and kind, since one area is a zone of several kinds. Both behave the
    /// same to the user (EditorMode.Zones.cs "detach" makes an edit of a rule-derived one an ordinary zone plus a cut).
    /// Points are the outline in absolute metres; Holes are not selectable, they only keep clicks and the overlay honest.
    /// </summary>
    public sealed record ZoneInfo(string Id, string Kind, List<(double x, double y)> Points, string? AreaPart = null, List<List<(double x, double y)>>? Holes = null) : ShapeInfo(Id, Points)
    {
        public bool FromRule => AreaPart != null;
        public override string Label => AreaPart ?? Id;
        readonly (double MinX, double MinY, double MaxX, double MaxY) _box = Points.Count == 0 ? default : (Points.Min(p => p.x), Points.Min(p => p.y), Points.Max(p => p.x), Points.Max(p => p.y));

        public bool Contains(double x, double y)
            => x >= _box.MinX && x <= _box.MaxX && y >= _box.MinY && y <= _box.MaxY && PointInRing(Points, x, y) && (Holes == null || !Holes.Any(h => PointInRing(h, x, y)));
    }

    public const string OutlineShapeDraft = "outline_shape_draft", OutlineShapeGhost = "outline_shape_ghost",
        OutlineShapeBad = "outline_shape_bad", OutlineShapeSel = "outline_shape_sel", OutlineShapeDrag = "outline_shape_drag";
    static readonly string[] ShapeOutlines = { OutlineShapeDraft, OutlineShapeGhost, OutlineShapeBad, OutlineShapeSel, OutlineShapeDrag };
    const float CornerPickPx = EditorTheme.Space * 2.5f;

    readonly List<(double x, double y)> _draft = new();
    string? _drawType, _drawKind;
    double _newWaterY;
    string? _restorePatch, _restoreZone;
    int _activeCorner, _hoverCorner = -1, _dragCorner = -1; // _dragCorner is the payload of the Dragging state
    (double x, double y) _dragPt;
    bool _cornerMoved;
    (double x, double y)? _cursor;
    string _shSignature = "";
    Node3D _handleRoot = null!;
    readonly List<MeshInstance3D> _handles = new();
    StandardMaterial3D _handleMat = null!, _handleHot = null!;
    // one sphere for every corner handle (the handles differ only by transform and material)
    readonly SphereMesh _handleMesh = new() { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };

    // Editor.Shape (docs/state-machines.md): the stored state. Drawing is entered by N or the first corner, Level by closing a water patch, Dragging by grabbing a
    // corner; Idle and Selected follow the selection, so every selection change goes through the setters below.
    readonly Drycastle.ShooterCore.Fsm.StateMachine<ShapeState> _shape = new(Drycastle.ShooterCore.Editor.EditorMachines.ShapeSpec);
    string? _selectedPatchId, _selectedZoneId;
    public string? SelectedPatchId { get => _selectedPatchId; private set { _selectedPatchId = value; ShapeRest(); } }
    public string? SelectedZoneId { get => _selectedZoneId; private set { _selectedZoneId = value; ShapeRest(); } }
    public IReadOnlyList<(double x, double y)> ShapeDraft => _draft;
    public bool ShapeDragging => _dragCorner >= 0;
    public int ActiveCorner => _activeCorner;

    /// <summary>True in the two tools that draw polygons (Ground: patches, Zones: zones).</summary>
    bool ShapeTool => Tool is EditorTool.Ground or EditorTool.Zones;
    /// <summary>What the open tool draws.</summary>
    public ShapeKind CurrentKind => Tool == EditorTool.Zones ? ShapeKind.Zone : ShapeKind.Patch;
    public string? SelectedShapeId => CurrentKind == ShapeKind.Zone ? SelectedZoneId : SelectedPatchId;

    public ShapeState ShapeMode => _shape.Current;
    bool InLevel => _shape.Is(ShapeState.Level);

    /// <summary>Idle or Selected, whichever the selection says (no gesture under way); a gesture keeps its state.</summary>
    void ShapeRest()
    {
        if (_shape.IsAny(ShapeState.Idle, ShapeState.Selected)) _shape.Enter(SelectedShapeId != null ? ShapeState.Selected : ShapeState.Idle);
    }

    /// <summary>The Drawing or Level gesture ends (created, cancelled, last corner taken back): back to Idle or Selected. A corner drag is not touched.</summary>
    void ShapeLeaveDraft()
    {
        if (_shape.IsAny(ShapeState.Drawing, ShapeState.Level)) _shape.Enter(SelectedShapeId != null ? ShapeState.Selected : ShapeState.Idle);
    }

    /// <summary>The one place a corner drag ends (release, Esc, selection changed, shape gone, tool left): payload cleared, Dragging left.</summary>
    void EndCornerGesture()
    {
        _dragCorner = -1;
        if (_shape.Is(ShapeState.Dragging)) _shape.Enter(SelectedShapeId != null ? ShapeState.Selected : ShapeState.Idle);
    }

    /// <summary>A shape drawn but not yet saved (the unsaved marker in the HUD).</summary>
    public bool ShapeDraftPending => _draft.Count > 0;

    static string KeyOf(ShapeKind k) => k == ShapeKind.Patch ? "ground_patches" : "zones";
    static string PrefixOf(ShapeKind k) => k == ShapeKind.Patch ? "GP-" : "Z-";
    static string NounOf(ShapeKind k) => k == ShapeKind.Patch ? "patch" : "zone";

    // ---- data ----

    static Obj? ShapesOf(Obj root, ShapeKind k) => root.GetValueOrDefault(KeyOf(k)) as Obj;

    public static List<(double x, double y)>? ParsePolygon(object? polygon)
    {
        if (polygon is not List<object?> pts) return null;
        var ring = new List<(double x, double y)>();
        foreach (var p in pts)
        {
            if (p is List<object?> { Count: 2 } xy && ObjectEdits.Number(xy[0]) is double x && ObjectEdits.Number(xy[1]) is double y) ring.Add((x, y));
            else return null;
        }
        if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    /// <summary>The cached lists of the file on disk (shared: callers must not keep them across a reload).</summary>
    List<PatchInfo> PatchList()
    {
        var disk = Disk(); // _disk is replaced on every reload, so its identity keys the cache
        if (ReferenceEquals(disk, _patchesFor) && _patchCache != null) return _patchCache;
        var list = new List<PatchInfo>();
        _patchesFor = disk;
        _patchCache = list;
        if (ShapesOf(disk, ShapeKind.Patch) is { } po)
            foreach (var (id, e) in po)
                if (e is Obj r && r.GetValueOrDefault("type") is string t && ParsePolygon(r.GetValueOrDefault("polygon")) is { } ring)
                    list.Add(new PatchInfo(id, t, ring, r.ContainsKey("water_y") ? ObjectEdits.Number(r["water_y"]) : null));
        return list;
    }
    Obj? _patchesFor;
    List<PatchInfo>? _patchCache;

    /// <summary>Patches in the file as last read, sorted by ID.</summary>
    public List<PatchInfo> Patches() => new(PatchList());
    public List<ZoneInfo> Zones() => new(ZoneList());
    public PatchInfo? Patch(string id) => PatchList().FirstOrDefault(p => p.Id == id);
    public ZoneInfo? Zone(string id) { ZoneList(); return _zoneById.GetValueOrDefault(id); }

    List<ShapeInfo> ShapeList(ShapeKind k) => k == ShapeKind.Patch ? PatchList().Cast<ShapeInfo>().ToList() : ZoneList().Cast<ShapeInfo>().ToList();
    ShapeInfo? ShapeById(ShapeKind k, string id) => k == ShapeKind.Patch ? Patch(id) : Zone(id);
    ShapeInfo? SelectedShape(ShapeKind k) => SelectedOf(k) is { } id ? ShapeById(k, id) : null;
    string? SelectedOf(ShapeKind k) => k == ShapeKind.Patch ? SelectedPatchId : SelectedZoneId;

    /// <summary>Next free ID: one above the highest in use (edits/mcp/ids.py next_id).</summary>
    public static string NextShapeId(Obj root, ShapeKind k) => NextId(root, KeyOf(k), PrefixOf(k));

    static string NextId(Obj root, string key, string prefix)
    {
        var top = 0;
        if (root.GetValueOrDefault(key) is Obj so)
            foreach (var id in so.Keys)
                if (id.StartsWith(prefix) && int.TryParse(id.AsSpan(prefix.Length), out var n)) top = Math.Max(top, n);
        return $"{prefix}{top + 1:0000}";
    }

    public string NextShapeIdOf(string text, ShapeKind k) => NextShapeId(EditsFile.Parse(text), k);

    /// <summary>Numeric part of GP-NNNN / Z-NNNN (GP-10000 outranks GP-9999): the higher number wins where patches overlap.</summary>
    static long ShapeNumber(string id) => id.IndexOf('-') is var i and >= 0 && long.TryParse(id.AsSpan(i + 1), out var n) ? n : -1;

    /// <summary>The shape of a kind under a map point; where shapes overlap the higher ID wins (patches: as on the ground; zones: it is the one clicked).</summary>
    public string? ShapeAt(ShapeKind k, double x, double y)
        => k == ShapeKind.Zone ? ZoneAtPoint(x, y) : ShapeList(k).Where(s => PointInRing(s.Points, x, y)).Select(s => s.Id).OrderBy(ShapeNumber).ThenBy(i => i, StringComparer.Ordinal).LastOrDefault();

    static bool PointInRing(List<(double x, double y)> ring, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].y > y) != (ring[j].y > y) && x < (ring[j].x - ring[i].x) * (y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x) inside = !inside;
        return inside;
    }

    /// <summary>Look at an absolute point from above and south-east, a little left of centre so the sidebar does not cover it (screenshots).</summary>
    public void LookAtAbs(double x, double y, float dist)
    {
        var t = Map.FromAbsolute(x, y);
        var target = new Vector3(t.X, Map.GroundHeightOrMean(t.X, t.Y), t.Y);
        var right = FlyCam.GlobalBasis.X;
        LookFrom(target + new Vector3(0, dist * 0.8f, dist * 0.6f) + right * dist * 0.15f, target + right * dist * 0.15f);
    }

    /// <summary>Rebuild the outline meshes now (smoke tests).</summary>
    public void OutlineSegmentsNow() { ShapeWorldNow(); }

    static List<object?> PolygonValue(IEnumerable<(double x, double y)> ring)
        => ring.Select(p => (object?)new List<object?> { N3(p.x), N3(p.y) }).ToList();

    static EditsFile.Num N3(double v) => new(EditsFile.PyFloat(Math.Round(v, 3, MidpointRounding.AwayFromZero)));

    static string RingMessage(int code) => code switch
    {
        ObjectEdits.RingTooFew => "needs at least 3 corners",
        ObjectEdits.RingRepeated => "has two identical neighbouring corners",
        ObjectEdits.RingNotSimple => "crosses or touches itself, or has no area",
        _ => "",
    };

    /// <summary>Why the shape being drawn cannot be closed yet, or null when it can. Shown in the panel before anything is committed.</summary>
    public string? ShapeDraftProblem()
    {
        if (_draft.Count == 0) return "no corners yet";
        var code = ObjectEdits.RingProblemCode(_draft);
        return code == ObjectEdits.RingOk ? null : code == ObjectEdits.RingTooFew ? $"needs at least 3 corners ({_draft.Count} placed)" : "the outline " + RingMessage(code);
    }

    public static string? ParseColour(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        return EditSchema.ColourRx.IsMatch(t) ? t.ToLowerInvariant() : null;
    }

    // ---- drawing ----

    /// <summary>Why a shape cannot be started now (null: it can): no origin, no type or kind to draw, a gesture in progress.</summary>
    string? CannotDraw(ShapeKind k)
    {
        if (Map.OriginEpsg5514 == null) return "this map has no origin_epsg5514 (export it again, map 0.4.0+); patches and zones need it";
        if (ShapeMode is ShapeState.Drawing or ShapeState.Level or ShapeState.Dragging) return "finish or cancel the shape you are drawing first (Esc)";
        return k == ShapeKind.Patch ? CannotDrawPatch() : CannotDrawZone();
    }

    /// <summary>N: leave any selection and wait for the first corner; clicks then add corners even over existing shapes.</summary>
    public void StartShape()
    {
        if (!ShapeTool) return;
        var k = CurrentKind;
        if (CannotDraw(k) is { } why) { Say(why, true); return; }
        SelectShape(k, null);
        if (k == ShapeKind.Patch) _drawType = SelectedGroundType; else _drawKind = SelectedZoneKind;
        _shape.Enter(ShapeState.Drawing); // waiting for the first corner
        Say(k == ShapeKind.Patch
            ? $"new patch of {_drawType}: click corners on the ground, Enter closes the shape, Esc cancels"
            : $"new {ZoneLabel(_drawKind!)} zone: click corners on the ground, Enter creates it, Esc cancels");
        ShapeChanged();
    }

    bool ShapesReady()
    {
        if (Map.OriginEpsg5514 != null) return true;
        Say("this map has no origin_epsg5514 (export it again, map 0.4.0+); patches and zones need it", true);
        return false;
    }

    /// <summary>Add a corner (absolute S-JTSK metres). Refused when the new edge would cross or touch the outline so far.</summary>
    public bool AddShapePoint(double x, double y)
    {
        if (_shape.Is(ShapeState.Level)) { Say("the shape is closed: set the water level and press Enter, or Esc to go back to the corners", true); return false; }
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (!ObjectEdits.ChainAccepts(_draft, p)) { Say("not placed: the new edge would cross or touch the outline", true); return false; }
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
        if (ShapeDraftProblem() is { } why) { Say("cannot close the shape: " + why, true); return false; }
        if (CurrentKind == ShapeKind.Patch && PatchTypeIsWater(_drawType))
        {
            _shape.Enter(ShapeState.Level);
            _newWaterY = DefaultWaterLevel(_draft);
            Say("shape closed: set the water level (m), Enter creates the patch, Esc goes back to the corners");
            ShapeChanged();
            return true;
        }
        return CommitShape();
    }

    /// <summary>Create the shape from the drawn corners: one save, one undo step.</summary>
    public bool CommitShape()
    {
        var k = CurrentKind;
        if (ShapeDraftProblem() is { } why) { Say("cannot create the " + NounOf(k) + ": " + why, true); return false; }
        var ring = _draft.ToList();
        var type = _drawType; var kind = _drawKind; var level = _shape.Is(ShapeState.Level) ? _newWaterY : (double?)null;
        if (k == ShapeKind.Patch && type == null) { Say("no ground type to draw: pick one in the list", true); return false; }
        if (k == ShapeKind.Zone && kind == null) { Say("no zone kind to draw: pick one in the list", true); return false; }
        string? id = null;
        var ok = Mutate(root =>
        {
            if (ShapesOf(root, k) is not { } so) root[KeyOf(k)] = so = EditsFile.NewObject();
            id = NextShapeId(root, k);
            var e = EditsFile.NewObject();
            if (k == ShapeKind.Patch) { e["type"] = type; if (level is { } wy) e["water_y"] = N3(wy); }
            else e["kind"] = kind;
            e["polygon"] = PolygonValue(ring);
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
        if (had && say) Say("drawing cancelled, nothing was saved");
        ShapeChanged();
    }

    /// <summary>Leave every shape gesture (switching tools, leaving the editor).</summary>
    void CancelShapeGesture()
    {
        CancelShapeCornerDrag();
        if (_draft.Count > 0) Say($"unfinished shape discarded ({_draft.Count} corner{(_draft.Count == 1 ? "" : "s")}, never saved)");
        _draft.Clear();
        ShapeLeaveDraft();
        ShapeChanged();
    }

    // ---- selection and edits ----

    public bool SelectShape(ShapeKind k, string? id)
    {
        var noun = NounOf(k);
        if (id != null && ShapeById(k, id) == null) { Say($"{noun} {id} does not exist", true); return false; }
        if (id == SelectedOf(k)) return true;
        EndCornerGesture(); // a corner drag belongs to the shape that was selected
        if (k == ShapeKind.Patch) SelectedPatchId = id; else SelectedZoneId = id;
        _activeCorner = 0;
        if (id != null) FollowShape(k, id);
        ShapeChanged();
        SaveUiConfig();
        return true;
    }

    /// <summary>Selecting a shape selects its type or kind too (the list row, the form and the map highlight follow it).</summary>
    void FollowShape(ShapeKind k, string id)
    {
        if (k == ShapeKind.Patch) { if (Patch(id) is { } p) { SelectGroundType(p.Type); _groundFocusPatch = true; } }
        else if (Zone(id) is { } z) SelectZoneKind(z.Kind);
    }

    /// <summary>Move one corner (absolute metres): one save. Refused (and nothing saved) when the shape would cross itself.</summary>
    public bool MoveShapeCorner(ShapeKind k, string id, int corner, double x, double y)
    {
        var noun = NounOf(k);
        if (ShapeById(k, id) is not { } r) { Say($"{noun} {id} does not exist", true); return false; }
        if (corner < 0 || corner >= r.Points.Count) { Say($"{r.Label} has no corner {corner + 1}", true); return false; }
        var ring = r.Points.ToList();
        ring[corner] = (Math.Round(x, 3), Math.Round(y, 3));
        if (ring[corner] == r.Points[corner]) return false;
        var code = ObjectEdits.RingProblemCode(ring);
        if (code != ObjectEdits.RingOk) { Say($"corner not moved: the outline would {RingMessage(code)}", true); return false; }
        if (r is ZoneInfo { FromRule: true } rz) return EditRuleZone(rz, null, ring, $"moved corner {corner + 1} of {rz.Label}");
        return Mutate(root =>
        {
            if (ShapesOf(root, k)?.GetValueOrDefault(id) is not Obj e) return (false, $"{noun} {id} no longer exists");
            e["polygon"] = PolygonValue(ring);
            return (true, $"moved corner {corner + 1} of {id}");
        });
    }

    public bool DeleteShape(ShapeKind k, string id)
    {
        var noun = NounOf(k);
        if (k == ShapeKind.Zone && Zone(id) is { FromRule: true } rz)
        {
            var gone = EditRuleZone(rz, null, null, $"deleted {rz.Label} (Ctrl+Z undoes)");
            if (gone && SelectedOf(k) == id) SelectShape(k, null);
            return gone;
        }
        var ok = Mutate(root =>
        {
            var so = ShapesOf(root, k);
            if (so == null || !so.Remove(id)) return (false, $"{noun} {id} no longer exists");
            if (so.Count == 0) root.Remove(KeyOf(k));
            return (true, $"deleted {id} (Ctrl+Z undoes)");
        });
        if (ok && SelectedOf(k) == id) SelectShape(k, null);
        return ok;
    }

    /// <summary>Del: ask first, naming the shape and what happens; accepting deletes it (undo brings it back).</summary>
    public bool RequestDeleteShape()
    {
        var k = CurrentKind;
        if (SelectedShape(k) is not { } s) { Say($"no {NounOf(k)} selected", true); return false; }
        var id = s.Id; var name = s.Label;
        var what = s is PatchInfo p ? $"{name} ({p.Type}, {p.Points.Count} corners, {p.AreaM2:F0} m2) is removed from the ground; the land under it goes back to what is below."
            : s is ZoneInfo z ? $"{name} ({ZoneLabel(z.Kind)}, {z.Points.Count} corners, {z.AreaM2:F0} m2) is removed; the ground is not changed." : name;
        Confirm.Show($"Delete {NounOf(k)} {name}?", what + " Undo (Ctrl+Z) brings it back.", "Delete", () => DeleteShape(k, id));
        return true;
    }

    // ---- corners ----

    /// <summary>Screen position (px) to the nearest corner of a ring; -1 when none is within reach.</summary>
    int PickCorner(Vector2 screen, IReadOnlyList<(double x, double y)> ring)
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

    Vector3 GroundPoint((double x, double y) p)
    {
        var l = Map.FromAbsolute(p.x, p.y);
        return new Vector3(l.X, Map.GroundHeightOrMean(l.X, l.Y) + 0.3f, l.Y);
    }

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

    bool RingBad(IReadOnlyList<(double x, double y)> ring) => ObjectEdits.RingProblemCode(ring) != ObjectEdits.RingOk;

    List<(double x, double y)>? DragRing()
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
        Say($"{r.Label} corner {_activeCorner + 1} of {r.Points.Count}: arrows move it, Del deletes the {NounOf(CurrentKind)}");
        ShapeChanged();
    }

    public bool NudgeCorner(int right, int forward)
    {
        if (SelectedShape(CurrentKind) is not { } r || _activeCorner >= r.Points.Count) return false;
        var f = -FlyCam.GlobalBasis.Z; var rt = FlyCam.GlobalBasis.X;
        if (Mathf.Abs(f.Y) > 0.98f) f = FlyCam.GlobalBasis.Y;
        var dir = new Vector2(rt.X * right + f.X * forward, -(rt.Z * right + f.Z * forward));
        if (dir.LengthSquared() < 1e-6f) return false;
        var step = Mathf.Abs(dir.X) >= Mathf.Abs(dir.Y) ? new Vector2(Mathf.Sign(dir.X), 0) : new Vector2(0, Mathf.Sign(dir.Y));
        var p = r.Points[_activeCorner];
        return MoveShapeCorner(CurrentKind, r.Id, _activeCorner, p.x + step.X * NudgeStep, p.y + step.Y * NudgeStep);
    }

    // ---- keys and mouse ----

    /// <summary>Keys of the shape tools (Ground and Zones): drawing, corners, delete. True when used.</summary>
    bool HandleShapeKey(InputEventKey k)
    {
        if (!ShapeTool || _cheat.Visible) return false;
        var mode = ShapeMode;
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
            case Key.Delete when k.ShiftPressed && CurrentKind == ShapeKind.Zone && mode is ShapeState.Idle or ShapeState.Selected: RequestDeleteMarkedZones(); return true;
            case Key.Delete when mode == ShapeState.Selected && (CurrentKind == ShapeKind.Zone || _groundFocusPatch): RequestDeleteShape(); return true;
            case Key.C when _shape.Is(ShapeState.Drawing):
                // corner at the cursor, or at the screen centre when the cursor is over the panel (keyboard-only drawing)
                var mouse = GetViewport().GetMousePosition();
                var at = OverUi(mouse) ? GetViewport().GetVisibleRect().Size / 2 : mouse;
                if (ShapesReady() && RayToGround(FlyCam.ProjectRayOrigin(at), FlyCam.ProjectRayNormal(at), out var g))
                {
                    var (x, y) = Map.SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
                    AddShapePoint(x, y);
                }
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

    /// <summary>Esc goes one level up: drag, water level (back to the corners), drawing, the selected shape. False when there is nothing of that left (the tool then steps back itself).</summary>
    public bool ShapeEscape()
    {
        switch (ShapeMode)
        {
            case ShapeState.Dragging: CancelShapeCornerDrag(); Say("move cancelled"); return true;
            case ShapeState.Level: _shape.Enter(ShapeState.Drawing); Say("back to the corners: click to add more, Enter closes again"); ShapeChanged(); return true;
            case ShapeState.Drawing: CancelShapeDraft(); return true;
            case ShapeState.Selected when CurrentKind == ShapeKind.Zone || _groundFocusPatch: SelectShape(CurrentKind, null); _groundFocusPatch = false; return true;
        }
        return false;
    }

    /// <summary>Left press in a shape tool: add a corner while drawing, else drag a corner of the selected shape, select the shape under the cursor, or fall back to the tool's own pick (the type under the cursor, the zone kind).</summary>
    void ShapeLeftPress(Vector2 screen, bool free)
    {
        if (Modal || !ShapesReady()) return;
        GetViewport().GuiReleaseFocus();
        var k = CurrentKind;
        if (InLevel) { Say("the shape is closed: set the water level, Enter creates it, Esc goes back to the corners", true); return; }
        var drafting = _shape.Is(ShapeState.Drawing);
        if (!drafting && SelectedShape(k) is { } sr)
        {
            var c = PickCorner(screen, sr.Points);
            if (c >= 0) { StartCornerDrag(c); return; }
        }
        if (drafting && _draft.Count >= 3 && PickCorner(screen, new[] { _draft[0] }) == 0) { CloseShape(); return; }
        if (!RayToGround(FlyCam.ProjectRayOrigin(screen), FlyCam.ProjectRayNormal(screen), out var g)) { Say("click on the ground", true); return; }
        if (drafting) { var (x, y) = Map.SnapAbs(g, free); AddShapePoint(x, y); return; }
        var (rx, ry) = Map.RawAbs(g);
        if (ShapeAt(k, rx, ry) is { } hit)
        {
            if (k == ShapeKind.Zone && free) ToggleZoneMark(hit); else SelectShape(k, hit); // Ctrl+click: mark for the mass delete
            return;
        }
        if (k == ShapeKind.Zone && free) { Say("no zone there to mark"); return; }
        if (SelectedOf(k) != null) SelectShape(k, null);
        if (k == ShapeKind.Patch) GroundPick(g);
        else Say(SelectedZoneKind is { } zk ? $"no {ZoneLabel(zk)} zone there (Esc shows all kinds)" : "no zone there");
    }

    void ShapeChanged() { _panelDirty = true; _shSignature = ""; }

    // ---- per frame: cursor, hover, drag, world preview ----

    void BuildShapeWorld()
    {
        _handleRoot = new Node3D { Name = "ShapeHandles" };
        MarkerRoot.AddChild(_handleRoot);
        StandardMaterial3D Mat(Color c) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _handleMat = Mat(EditorTheme.Accent);
        _handleHot = Mat(Colors.White);
    }

    void UpdateShapeTool()
    {
        if (!ShapeTool)
        {
            if (_shSignature != "off") { _shSignature = "off"; HideShapeWorld(); }
            return;
        }
        var mouse = GetViewport().GetMousePosition();
        var free = Input.IsKeyPressed(Key.Ctrl);
        var over = OverUi(mouse) || _looking || Modal;
        _cursor = null;
        if (!over && Map.OriginEpsg5514 != null && RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var g)) _cursor = Map.SnapAbs(g, free);
        _hoverCorner = -1;
        var mode = ShapeMode;
        if (!over && mode is ShapeState.Selected && SelectedShape(CurrentKind) is { } sr) _hoverCorner = PickCorner(mouse, sr.Points);
        if (!over && mode is ShapeState.Drawing && _draft.Count >= 3 && PickCorner(mouse, new[] { _draft[0] }) == 0) _hoverCorner = 0;
        if (_dragCorner >= 0 && _cursor is { } c) DragCornerToAbs(c.x, c.y);
        ShapeWorld();
    }

    void HideShapeWorld()
    {
        foreach (var n in ShapeOutlines) SetOutline(n, new(), Colors.White, 0, 0);
        foreach (var h in _handles) h.Visible = false;
    }

    List<(Vector2, Vector2)> RingSegs(IReadOnlyList<(double x, double y)> pts, bool closed)
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

    /// <summary>The outline of a selected shape: its crop's colour for a patch of a crop_* type, else the accent.</summary>
    static Color ShapeOutlineColour(ShapeInfo s) => s is PatchInfo p && EditorTheme.CropColour(p.Type) is { } c ? c : EditorTheme.Accent;

    static string ShapeTag(ShapeInfo s) => s is PatchInfo p ? p.Type + p.WaterY : s is ZoneInfo z ? z.Kind : "";

    void ShapeWorld()
    {
        var sel = SelectedShape(CurrentKind);
        var dragRing = DragRing();
        var sig = string.Join(";", _draft) + "|" + InLevel + "|" + (_cursor?.ToString() ?? "-") + "|" + (sel == null ? "-" : sel.Id + ShapeTag(sel) + string.Join(",", sel.Points))
            + "|" + (dragRing == null ? "-" : _dragPt.ToString()) + "|" + _hoverCorner + "|" + _activeCorner;
        var show = new List<(double x, double y)>();
        var hot = -1;
        if (_shape.IsAny(ShapeState.Drawing, ShapeState.Level))
        {
            show = _draft.ToList();
            if (sig != _shSignature)
            {
                var chain = RingSegs(_draft, InLevel);
                var ghost = new List<(Vector2, Vector2)>(); var bad = new List<(Vector2, Vector2)>();
                if (!InLevel && _cursor is { } cur && _draft.Count > 0)
                {
                    var ok = ObjectEdits.ChainAccepts(_draft, cur);
                    var a = Map.FromAbsolute(_draft[^1].x, _draft[^1].y); var b = Map.FromAbsolute(cur.x, cur.y);
                    if (a.DistanceSquaredTo(b) > 1e-8f) (ok ? ghost : bad).Add((a, b));
                    if (_draft.Count >= 2 && ok) { var f = Map.FromAbsolute(_draft[0].x, _draft[0].y); if (b.DistanceSquaredTo(f) > 1e-8f) ghost.Add((b, f)); }
                }
                SetOutline(OutlineShapeDraft, InLevel && RingBad(_draft) ? new() : chain, EditorTheme.Accent, 0.31f, 0.45f);
                SetOutline(OutlineShapeGhost, ghost, new Color(EditorTheme.Accent, 0.5f), 0.30f, 0.35f);
                SetOutline(OutlineShapeBad, bad.Concat(InLevel && RingBad(_draft) ? chain : new()).ToList(), EditorTheme.Danger, 0.33f, 0.45f);
                SetOutline(OutlineShapeSel, new(), Colors.White, 0, 0);
                SetOutline(OutlineShapeDrag, new(), Colors.White, 0, 0);
            }
            hot = _hoverCorner;
        }
        else if (sel != null)
        {
            show = dragRing ?? sel.Points;
            if (sig != _shSignature)
            {
                var bad = dragRing != null && RingBad(dragRing);
                SetOutline(OutlineShapeSel, dragRing == null ? RingSegs(sel.Points, true) : new(), ShapeOutlineColour(sel), 0.31f, 0.45f);
                SetOutline(OutlineShapeDrag, dragRing != null && !bad ? RingSegs(dragRing, true) : new(), ShapeOutlineColour(sel), 0.32f, 0.45f);
                SetOutline(OutlineShapeBad, bad ? RingSegs(dragRing!, true) : new(), EditorTheme.Danger, 0.33f, 0.45f);
                SetOutline(OutlineShapeDraft, new(), Colors.White, 0, 0);
                SetOutline(OutlineShapeGhost, new(), Colors.White, 0, 0);
            }
            hot = _dragCorner >= 0 ? _dragCorner : _hoverCorner >= 0 ? _hoverCorner : _activeCorner;
        }
        else if (sig != _shSignature)
            foreach (var n in ShapeOutlines) SetOutline(n, new(), Colors.White, 0, 0);
        _shSignature = sig;

        // corner handles: sized by distance so they stay grabbable from any height
        while (_handles.Count < show.Count)
        {
            var mi = new MeshInstance3D { Mesh = _handleMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _handleRoot.AddChild(mi);
            _handles.Add(mi);
        }
        for (var i = 0; i < _handles.Count; i++)
        {
            var h = _handles[i];
            h.Visible = i < show.Count;
            if (i >= show.Count) continue;
            var p = GroundPoint(show[i]);
            h.Position = p;
            var s = Mathf.Clamp(p.DistanceTo(FlyCam.GlobalPosition) * 0.011f, 0.35f, 8f) * (i == hot ? 1.5f : 1f);
            h.Scale = new Vector3(s, s, s);
            h.MaterialOverride = i == hot ? _handleHot : _handleMat;
        }
    }

    /// <summary>Run the shape world update now (smoke tests; normally per frame).</summary>
    public void ShapeWorldNow() { _shSignature = ""; ShapeWorld(); }

    public int ShapeHandleCount => _handles.Count(h => h.Visible);

    // ---- hints ----

    /// <summary>The hint line while a shape gesture is under way or a shape is selected; null when there is none (the tool's own hint applies).</summary>
    public string? ShapeHintText()
    {
        var noun = NounOf(CurrentKind);
        return ShapeMode switch
        {
            ShapeState.Dragging => "moving a corner: release saves, Esc cancels, red means the shape would cross itself",
            ShapeState.Level => "shape closed: set the water level, Enter creates the patch, Esc goes back to the corners",
            ShapeState.Drawing => _draft.Count == 0
                ? $"new {noun}: LMB or C places the first corner, Ctrl free position, Esc cancels"
                : $"drawing {_draft.Count}: LMB or C adds, Enter {(CurrentKind == ShapeKind.Patch && PatchTypeIsWater(_drawType) ? "closes" : "creates")}, Backspace undoes a corner, Esc cancels",
            ShapeState.Selected when CurrentKind == ShapeKind.Zone || _groundFocusPatch =>
                $"{SelectedShape(CurrentKind)?.Label}: drag a corner, [ ] and arrows move one, Del deletes, {(CurrentKind == ShapeKind.Zone ? "X marks for deletion, " : "")}N new {noun}, Ctrl+Z undoes, Esc deselects",
            _ => null,
        };
    }

    // ---- shared widgets ----

    Button FocusBtn(string text, Action onPress, string? variation = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All };
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += () => { onPress(); b.ReleaseFocus(); }; // Enter and Space belong to the viewport again
        return b;
    }

    static Label Note(string text, string variation = "Dim")
        => new() { Text = text, ThemeTypeVariation = variation, AutowrapMode = TextServer.AutowrapMode.WordSmart };

    static ImageTexture Swatch(string hex) => Swatch(Color.FromHtml(hex));

    static ImageTexture Swatch(Color colour)
    {
        const int n = EditorTheme.Space * 3;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(colour);
        for (var i = 0; i < n; i++) foreach (var (x, y) in new[] { (i, 0), (i, n - 1), (0, i), (n - 1, i) }) img.SetPixel(x, y, EditorTheme.Border);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The controls of one shape tool's page that the gesture drives (the same in both).</summary>
    sealed class ShapeUi
    {
        public Button New = null!, Finish = null!, Create = null!, Back = null!, Cancel = null!, Delete = null!;
        public Label State = null!, Valid = null!, NoOrigin = null!, Empty = null!, ListHead = null!, ListNote = null!;
        public HBoxContainer LevelRow = null!;
        public SpinBox Level = null!;
        public VBoxContainer List = null!;
        public readonly Dictionary<string, Button> Rows = new();
        public readonly Dictionary<string, CheckBox> Marks = new(); // the tick of each row, when the list marks (zones: mass delete)
        public string ListSig = "";
        public bool Sync;
    }
    readonly Dictionary<ShapeKind, ShapeUi> _su = new();

    /// <summary>The draw action and its feedback: the New button, the state and validity lines, the water level row and the Finish / Create / Back / Cancel buttons.</summary>
    void BuildShapeActions(VBoxContainer page, ShapeKind k, Control? form = null)
    {
        var u = new ShapeUi();
        _su[k] = u;
        var noun = NounOf(k);
        u.NoOrigin = Note("This map has no origin_epsg5514, so patches and zones cannot be placed. Export it again (map 0.4.0+).", "Danger");
        page.AddChild(u.NoOrigin);
        u.New = FocusBtn("", StartShape, "PrimaryButton");
        page.AddChild(u.New);
        u.State = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Header" };
        u.Valid = Note("");
        page.AddChild(u.State);
        page.AddChild(u.Valid);
        if (form != null) page.AddChild(form);
        if (k == ShapeKind.Patch)
        {
            u.LevelRow = new HBoxContainer();
            u.LevelRow.AddChild(new Label { Text = "Water level (m)", CustomMinimumSize = new Vector2(EditorTheme.Space * 16, 0) });
            u.Level = NumberField(-500, 5000, 0.1, "Height of the water surface in metres, on the terrain's own scale (the ground map and the terrain use it). A patch of a water type needs one. Enter applies; each change is one undo step.");
            u.Level.ValueChanged += v => { if (!u.Sync) OnWaterLevelChanged(v); };
            u.LevelRow.AddChild(u.Level);
            page.AddChild(u.LevelRow);
        }
        u.Finish = FocusBtn("", () => CloseShape(), "PrimaryButton");
        u.Create = FocusBtn($"Create the {noun}  (Enter)", () => CommitShape(), "PrimaryButton");
        u.Back = FocusBtn("Remove the last corner  (Backspace)", () => RemoveLastShapePoint());
        u.Cancel = FocusBtn("Cancel drawing  (Esc)", () => ShapeEscape());
        foreach (var b in new[] { u.Finish, u.Create, u.Back, u.Cancel }) page.AddChild(b);
    }

    /// <summary>The list of all shapes of a kind with its head and empty state.</summary>
    void BuildShapeList(VBoxContainer page, ShapeKind k, string head, string emptyText)
    {
        var u = _su[k];
        u.ListHead = new Label { Text = head, ThemeTypeVariation = "Header" };
        page.AddChild(u.ListHead);
        u.Empty = Note(emptyText);
        page.AddChild(u.Empty);
        u.ListNote = Note("");
        u.ListNote.Visible = false;
        page.AddChild(u.ListNote);
        u.List = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(u.List);
    }

    void BuildShapeDelete(VBoxContainer page, ShapeKind k)
    {
        var u = _su[k];
        u.Delete = FocusBtn("", () => RequestDeleteShape(), "DangerButton");
        page.AddChild(u.Delete);
    }

    /// <summary>
    /// Refresh the draw action and feedback of the open tool's page. <paramref name="drawText"/> is the New button's text,
    /// <paramref name="describe"/> names the selected shape for the state line (its type or kind).
    /// </summary>
    void RefreshShapeActions(ShapeKind k, string drawText, Func<ShapeInfo, string> describe)
    {
        var u = _su[k];
        var noun = NounOf(k);
        var mode = ShapeMode;
        var sel = SelectedShape(k);
        u.NoOrigin.Visible = Map.OriginEpsg5514 == null;
        var why = CannotDraw(k);
        u.New.Text = drawText;
        u.New.Disabled = why != null;
        u.New.TooltipText = why != null ? why[0].ToString().ToUpperInvariant() + why[1..] : $"Click corners on the ground, Enter closes the shape (N). Nothing is saved until it is created.";

        u.State.Text = mode switch
        {
            ShapeState.Dragging => $"Moving corner {_dragCorner + 1} of {sel?.Label}",
            ShapeState.Level => $"Patch closed: {_draft.Count} corners. Set its water level.",
            ShapeState.Drawing => _draft.Count == 0 ? "Click the ground to place the first corner" : $"Drawing: {_draft.Count} corner{(_draft.Count == 1 ? "" : "s")}",
            ShapeState.Selected => $"{sel!.Label} ({describe(sel)}): {sel.Points.Count} corners, {sel.AreaM2:F0} m2",
            _ => $"No {noun} selected",
        };
        string text, variation;
        if (mode is ShapeState.Drawing or ShapeState.Level)
        {
            var problem = _draft.Count == 0 ? null : ShapeDraftProblem();
            (text, variation) = _draft.Count == 0 ? ($"Not saved until the shape is closed and created.", "Dim")
                : problem != null ? (_draft.Count < 3 ? problem : "Cannot close: " + problem, _draft.Count < 3 ? "Warn" : "Danger")
                : (mode == ShapeState.Level ? "Shape is valid. Not saved until you create it." : "Shape is valid; Enter closes it. Not saved yet.", "Dim");
        }
        else if (mode == ShapeState.Selected) (text, variation) = ("Saved. Every change is saved at once and Ctrl+Z undoes it.", "Dim");
        else if (mode == ShapeState.Dragging) (text, variation) = ("Release to save. A red outline would be refused.", "Dim");
        else (text, variation) = ($"Click a {noun} in the list or on the ground to select it.", "Dim");
        u.Valid.Text = text; u.Valid.ThemeTypeVariation = variation;

        var drafting = mode == ShapeState.Drawing && _draft.Count > 0;
        var water = k == ShapeKind.Patch && PatchTypeIsWater(_drawType);
        u.Finish.Visible = drafting;
        u.Finish.Text = water ? "Close the shape  (Enter)" : $"Create the {noun}  (Enter)";
        u.Finish.Disabled = ShapeDraftProblem() != null;
        u.Finish.TooltipText = ShapeDraftProblem() is { } p ? $"Not yet: {p}" : water ? "Close the shape; the water level comes next" : $"Close the shape and save the {noun} (one undo step)";
        u.Create.Visible = mode == ShapeState.Level;
        u.Back.Visible = drafting || mode == ShapeState.Level;
        u.Back.Text = mode == ShapeState.Level ? "Back to the corners  (Esc)" : "Remove the last corner  (Backspace)";
        u.Cancel.Visible = mode is ShapeState.Drawing or ShapeState.Level;
        if (k == ShapeKind.Patch)
        {
            u.LevelRow.Visible = mode == ShapeState.Level;
            if (mode == ShapeState.Level && !u.Level.GetLineEdit().HasFocus()) { u.Sync = true; u.Level.SetValueNoSignal(_newWaterY); u.Sync = false; }
        }
        u.Delete.Visible = mode == ShapeState.Selected && sel != null && (k == ShapeKind.Zone || _groundFocusPatch);
        if (sel != null) u.Delete.Text = $"Delete {sel.Label}  (Del)";
        u.Delete.TooltipText = $"Delete the {noun} (asks first; Ctrl+Z undoes)";
    }

    /// <summary>
    /// Refresh a list of shapes; rows are rebuilt only when what they show changes. <paramref name="row"/> gives a row's text, swatch colour and tooltip.
    /// With <paramref name="marks"/> every row has a tick in front (marked for deletion); ticking calls <paramref name="onMark"/> and does not rebuild the rows (the tick keeps the focus).
    /// </summary>
    void RefreshShapeList(ShapeKind k, string noun, Func<ShapeInfo, (string Text, string Colour, string Tip)> row, string extraSig = "",
        Func<ShapeInfo, bool>? filter = null, string? head = null, int cap = int.MaxValue, IReadOnlySet<string>? marks = null, Action<string, bool>? onMark = null)
    {
        var u = _su[k];
        var all = ShapeList(k);
        if (filter != null) all = all.Where(filter).ToList();
        var shapes = all.Count > cap ? all.Take(cap).ToList() : all;
        u.ListHead.Text = head != null ? $"{head} ({all.Count})" : $"All {noun}s ({all.Count})";
        u.Empty.Visible = all.Count == 0;
        u.ListNote.Visible = shapes.Count < all.Count;
        u.ListNote.Text = $"Showing the first {shapes.Count} of {all.Count}. Pick a kind in the list above to see only its {noun}s.";
        var sig = string.Join("|", shapes.Select(s => s.Id + row(s).Text + row(s).Colour + s.Points.Count)) + "#" + SelectedOf(k) + extraSig + "#" + all.Count;
        if (sig != u.ListSig)
        {
            u.ListSig = sig;
            foreach (var c in u.List.GetChildren()) { u.List.RemoveChild(c); c.QueueFree(); }
            u.Rows.Clear(); u.Marks.Clear();
            foreach (var s in shapes)
            {
                var id = s.Id;
                var (text, colour, tip) = row(s);
                var b = new Button
                {
                    Text = text, Icon = Swatch(colour), Alignment = HorizontalAlignment.Left, ClipText = true,
                    ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip,
                };
                b.SetPressedNoSignal(id == SelectedOf(k));
                b.Pressed += () => { SelectShape(k, id == SelectedOf(k) ? null : id); b.ReleaseFocus(); };
                u.Rows[id] = b;
                if (marks == null) { u.List.AddChild(b); continue; }
                var line = new HBoxContainer();
                var tick = new CheckBox { FocusMode = Control.FocusModeEnum.All, TooltipText = $"Mark {s.Label} for deletion (Space). \"Delete marked {noun}s\" above removes every marked one in one step." };
                tick.Toggled += on => onMark?.Invoke(id, on);
                b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                line.AddChild(tick); line.AddChild(b);
                u.List.AddChild(line);
                u.Marks[id] = tick;
            }
        }
        if (marks != null) foreach (var (id, tick) in u.Marks) if (tick.ButtonPressed != marks.Contains(id)) tick.SetPressedNoSignal(marks.Contains(id));
    }

    // test hooks
    public string ShapePanelState => _su[CurrentKind].State.Text;
    public string ShapePanelValidation => _su[CurrentKind].Valid.Text;
    public int ShapeRowCount => _su[CurrentKind].Rows.Count;
    public bool ShapeEmptyShown => _su[CurrentKind].Empty.Visible;
    public bool ShapeDeleteButtonShown => _su[CurrentKind].Delete.Visible;
    public bool ShapeCreateButtonShown => _su[CurrentKind].Create.Visible || _su[CurrentKind].Finish.Visible;
    public bool ShapeRowSelected(string id) => _su[CurrentKind].Rows.TryGetValue(id, out var b) && b.ButtonPressed;
    public string ShapeHint => CurrentKind == ShapeKind.Zone ? ZonesHintText() : GroundHintText();
    public bool ShapeRowMarked(string id) => _su[CurrentKind].Marks.TryGetValue(id, out var t) && t.ButtonPressed;
    public void TickShapeRow(string id, bool on) { if (_su[CurrentKind].Marks.TryGetValue(id, out var t)) t.ButtonPressed = on; }
    public void PressShapeRow(string id) { if (_su[CurrentKind].Rows.TryGetValue(id, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
}
