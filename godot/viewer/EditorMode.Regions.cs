using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// The Region tool: hand-drawn colour shapes (GLOSSARY "Region"), stored in edits.json `regions` as
/// {R-NNNN: {colour "#rrggbb", polygon [[x, y], ...]}} in absolute S-JTSK metres.
///
/// States (the hint bar names each): Idle, Drawing (corners placed, nothing saved), Colouring (shape closed, choose the colour,
/// still nothing saved), Selected, Dragging (a corner follows the cursor; the file changes on release). Every completed gesture
/// is ONE Mutate = one save = one undo step: create (draw + colour), recolour, move a corner, delete. The saved file is
/// picked up by the EditsWatcher, which runs region-run and swaps the draped mesh in (Regions 2), so there is no extra
/// refresh code here. Esc goes one level up: drag, colouring, drawing, selection, tool.
/// </summary>
public partial class EditorMode
{
    public enum RegionState { Idle, Drawing, Colouring, Selected, Dragging }

    public sealed record RegionInfo(string Id, string Colour, List<(double x, double y)> Points)
    {
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

    public const string OutlineRegionDraft = "outline_region_draft", OutlineRegionGhost = "outline_region_ghost",
        OutlineRegionBad = "outline_region_bad", OutlineRegionSel = "outline_region_sel", OutlineRegionDrag = "outline_region_drag";
    public const string DefaultRegionColour = "#c8a050";
    const float CornerPickPx = EditorTheme.Space * 2.5f;

    readonly List<(double x, double y)> _draft = new();
    bool _colouring, _arming;
    string _newColour = DefaultRegionColour;
    string? _restoreRegion;
    int _activeCorner, _hoverCorner = -1, _dragCorner = -1;
    (double x, double y) _dragPt;
    bool _rgDragMoved;
    (double x, double y)? _cursor;
    string _rgSignature = "", _listSignature = "";
    Node3D _handleRoot = null!;
    readonly List<MeshInstance3D> _handles = new();
    StandardMaterial3D _handleMat = null!, _handleHot = null!;
    // one sphere for every corner handle (the handles differ only by transform and material)
    readonly SphereMesh _handleMesh = new() { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 6 };

    public string? SelectedRegionId { get; private set; }
    public IReadOnlyList<(double x, double y)> RegionDraft => _draft;
    public string NewRegionColour => _newColour;
    public bool RegionDragging => _dragCorner >= 0;
    public int ActiveCorner => _activeCorner;

    public RegionState RegionMode =>
        _dragCorner >= 0 ? RegionState.Dragging
        : _colouring ? RegionState.Colouring
        : _draft.Count > 0 || _arming ? RegionState.Drawing
        : SelectedRegionId != null ? RegionState.Selected : RegionState.Idle;

    /// <summary>A shape drawn but not yet saved (the unsaved marker in the HUD).</summary>
    public bool RegionDraftPending => _draft.Count > 0;

    // ---- data ----

    static Obj? RegionsOf(Obj root) => root.GetValueOrDefault("regions") as Obj;

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

    /// <summary>Regions in the file as last read, sorted by ID.</summary>
    public List<RegionInfo> Regions() => new(RegionList());

    /// <summary>The cached region list of the file on disk (shared: callers must not keep it across a reload).</summary>
    List<RegionInfo> RegionList()
    {
        var disk = Disk(); // _disk is replaced on every reload, so its identity keys the cache
        if (ReferenceEquals(disk, _regionsFor) && _regionsCache != null) return _regionsCache;
        var list = new List<RegionInfo>();
        _regionsFor = disk;
        _regionsCache = list;
        if (RegionsOf(disk) is { } ro)
            foreach (var (id, e) in ro)
                if (e is Obj r && r.GetValueOrDefault("colour") is string c && ParsePolygon(r.GetValueOrDefault("polygon")) is { } ring)
                    list.Add(new RegionInfo(id, c, ring));
        return list;
    }
    Obj? _regionsFor;
    List<RegionInfo>? _regionsCache;

    public RegionInfo? Region(string id)
    {
        foreach (var r in RegionList()) if (r.Id == id) return r;
        return null;
    }

    /// <summary>Next free ID: one above the highest R-NNNN in use (mcp_server._next_id).</summary>
    public string NextRegionIdOf(string text) => NextRegionId(EditsFile.Parse(text));

    /// <summary>Look at an absolute point from above and south-east, a little left of centre so the sidebar does not cover it (screenshots).</summary>
    public void LookAtAbs(double x, double y, float dist)
    {
        var t = FromAbsolute(x, y);
        var target = new Vector3(t.X, Map.GroundHeightOrMean(t.X, t.Y), t.Y);
        var right = FlyCam.GlobalBasis.X;
        LookFrom(target + new Vector3(0, dist * 0.8f, dist * 0.6f) + right * dist * 0.15f, target + right * dist * 0.15f);
    }

    /// <summary>Rebuild the outline meshes now (smoke tests).</summary>
    public void OutlineSegmentsNow() { RegionWorldNow(); }

    public static string NextRegionId(Obj root)
    {
        var top = 0;
        if (RegionsOf(root) is { } ro)
            foreach (var id in ro.Keys)
                if (id.StartsWith("R-") && int.TryParse(id.AsSpan(2), out var n)) top = Math.Max(top, n);
        return $"R-{top + 1:0000}";
    }

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
    public string? RegionDraftProblem()
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

    /// <summary>N: leave any selection and wait for the first corner; clicks then add corners even over existing regions.</summary>
    public void StartRegion()
    {
        if (!RegionsReady()) return;
        if (_colouring || _draft.Count > 0) { Say("finish or cancel the shape you are drawing first (Esc)", true); return; }
        SelectRegion(null);
        _arming = true;
        Say("new region: click corners on the ground, Enter closes the shape, Esc cancels");
        RegionChanged();
    }

    bool RegionsReady()
    {
        if (Map.OriginEpsg5514 != null) return true;
        Say("this map has no origin_epsg5514 (export it again, map 0.4.0+); regions need it", true);
        return false;
    }

    /// <summary>Add a corner (absolute S-JTSK metres). Refused when the new edge would cross or touch the outline so far.</summary>
    public bool AddRegionPoint(double x, double y)
    {
        if (_colouring) { Say("the shape is closed: choose the colour and press Enter, or Esc to go back to the corners", true); return false; }
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (!ObjectEdits.ChainAccepts(_draft, p)) { Say("not placed: the new edge would cross or touch the outline", true); return false; }
        if (_draft.Count == 0) SelectRegion(null);
        _draft.Add(p);
        _arming = false;
        RegionChanged();
        return true;
    }

    /// <summary>Backspace: take the last corner back.</summary>
    public bool RemoveLastRegionPoint()
    {
        if (_colouring) { _colouring = false; RegionChanged(); return true; }
        if (_draft.Count == 0) return false;
        _draft.RemoveAt(_draft.Count - 1);
        RegionChanged();
        return true;
    }

    /// <summary>Enter while drawing: the shape is closed (the closing edge is the last corner back to the first) and the colour is chosen next. Refused when invalid.</summary>
    public bool CloseRegion()
    {
        if (_colouring) return true;
        if (RegionDraftProblem() is { } why) { Say("cannot close the shape: " + why, true); return false; }
        _colouring = true;
        Say("shape closed: choose the colour, Enter creates the region, Esc goes back to the corners");
        RegionChanged();
        return true;
    }

    /// <summary>Create the region from the drawn shape in the chosen colour: one save, one undo step.</summary>
    public bool CommitRegion()
    {
        if (!_colouring) { Say("close the shape first (Enter)", true); return false; }
        var ring = _draft.ToList();
        var colour = _newColour;
        string? id = null;
        var ok = Mutate(root =>
        {
            if (RegionsOf(root) is not { } ro) root["regions"] = ro = EditsFile.NewObject();
            id = NextRegionId(root);
            var e = EditsFile.NewObject();
            e["colour"] = colour;
            e["polygon"] = PolygonValue(ring);
            ro[id] = e;
            return (true, $"created {id}");
        });
        if (!ok) return false;
        _draft.Clear(); _colouring = false; _arming = false;
        SelectRegion(id);
        return true;
    }

    /// <summary>Drop the shape being drawn (Esc). Nothing was saved, so nothing is undone.</summary>
    public void CancelRegionDraft(bool say = true)
    {
        var had = _draft.Count > 0 || _colouring || _arming;
        _draft.Clear(); _colouring = false; _arming = false;
        if (had && say) Say("drawing cancelled, nothing was saved");
        RegionChanged();
    }

    /// <summary>Leave every region gesture (switching tools, leaving the editor).</summary>
    void CancelRegionGesture()
    {
        CancelRegionCornerDrag();
        if (_draft.Count > 0) Say($"unfinished region discarded ({_draft.Count} corner{(_draft.Count == 1 ? "" : "s")}, never saved)");
        _draft.Clear(); _colouring = false; _arming = false;
        RegionChanged();
    }

    // ---- selection and edits ----

    public bool SelectRegion(string? id)
    {
        if (id != null && Region(id) == null) { Say($"region {id} does not exist", true); return false; }
        if (id == SelectedRegionId) return true;
        SelectedRegionId = id;
        _activeCorner = 0;
        RegionChanged();
        SaveUiConfig();
        return true;
    }

    /// <summary>The region under a map point; where regions overlap the higher ID wins, as it does on the ground.</summary>
    public string? RegionAt(double x, double y)
        => Regions().Where(r => PointInRing(r.Points, x, y)).Select(r => r.Id).OrderBy(i => RegionNumber(i)).ThenBy(i => i, StringComparer.Ordinal).LastOrDefault();

    /// <summary>Numeric part of R-NNNN (R-10000 outranks R-9999); matches regions.region_rank.</summary>
    static long RegionNumber(string id) => id.StartsWith("R-") && long.TryParse(id.AsSpan(2), out var n) ? n : -1;

    static bool PointInRing(List<(double x, double y)> ring, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].y > y) != (ring[j].y > y) && x < (ring[j].x - ring[i].x) * (y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x) inside = !inside;
        return inside;
    }

    /// <summary>Change the colour of a region (one save). Same colour: nothing is saved.</summary>
    public bool SetRegionColour(string id, string colour)
    {
        var c = ParseColour(colour);
        if (c == null) { Say($"colour must look like #rrggbb, got {colour}", true); return false; }
        if (Region(id) is { } r && r.Colour == c) { Say($"{id} is already {c}"); return true; }
        return Mutate(root =>
        {
            if (RegionsOf(root)?.GetValueOrDefault(id) is not Obj e) return (false, $"region {id} no longer exists");
            e["colour"] = c;
            return (true, $"recoloured {id} to {c}");
        });
    }

    /// <summary>Move one corner (absolute metres): one save. Refused (and nothing saved) when the shape would cross itself.</summary>
    public bool MoveRegionCorner(string id, int corner, double x, double y)
    {
        if (Region(id) is not { } r) { Say($"region {id} does not exist", true); return false; }
        if (corner < 0 || corner >= r.Points.Count) { Say($"{id} has no corner {corner + 1}", true); return false; }
        var ring = r.Points.ToList();
        ring[corner] = (Math.Round(x, 3), Math.Round(y, 3));
        if (ring[corner] == r.Points[corner]) return false;
        var code = ObjectEdits.RingProblemCode(ring);
        if (code != ObjectEdits.RingOk) { Say($"corner not moved: the outline would {RingMessage(code)}", true); return false; }
        return Mutate(root =>
        {
            if (RegionsOf(root)?.GetValueOrDefault(id) is not Obj e) return (false, $"region {id} no longer exists");
            e["polygon"] = PolygonValue(ring);
            return (true, $"moved corner {corner + 1} of {id}");
        });
    }

    public bool DeleteRegion(string id)
    {
        var ok = Mutate(root =>
        {
            var ro = RegionsOf(root);
            if (ro == null || !ro.Remove(id)) return (false, $"region {id} no longer exists");
            if (ro.Count == 0) root.Remove("regions");
            return (true, $"deleted {id} (Ctrl+Z undoes)");
        });
        if (ok && SelectedRegionId == id) SelectRegion(null);
        return ok;
    }

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

    /// <summary>Del: ask first, naming the region and what happens; accepting deletes it (undo brings it back).</summary>
    public bool RequestDeleteRegion()
    {
        if (SelectedRegionId is not { } id || Region(id) is not { } r) { Say("no region selected", true); return false; }
        var rid = id;
        ShowConfirm($"Delete region {id}?", $"{id} ({r.Colour}, {r.Points.Count} corners, {r.AreaM2:F0} m2) is removed from the ground. Undo (Ctrl+Z) brings it back.", "Delete", () => DeleteRegion(rid));
        return true;
    }

    // ---- corners ----

    /// <summary>Screen position (px) to the nearest corner of the selected region or of the shape being drawn; -1 when none is within reach.</summary>
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
        var l = FromAbsolute(p.x, p.y);
        return new Vector3(l.X, Map.GroundHeightOrMean(l.X, l.Y) + 0.3f, l.Y);
    }

    public bool StartCornerDrag(int corner)
    {
        if (SelectedRegionId is not { } id || Region(id) is not { } r || corner < 0 || corner >= r.Points.Count) return false;
        _dragCorner = corner; _activeCorner = corner; _dragPt = r.Points[corner]; _rgDragMoved = false;
        RegionChanged();
        return true;
    }

    /// <summary>Drag: the corner follows the point (0.5 m grid unless free). Only the preview changes until release.</summary>
    public void DragCornerToAbs(double x, double y)
    {
        if (_dragCorner < 0) return;
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (p == _dragPt) return;
        _dragPt = p; _rgDragMoved = true;
    }

    /// <summary>Release: one save with the final corner (nothing when it did not move; refused when the shape would cross itself).</summary>
    public bool EndCornerDrag()
    {
        if (_dragCorner < 0 || SelectedRegionId is not { } id) return false;
        var c = _dragCorner; var pt = _dragPt; var moved = _rgDragMoved;
        _dragCorner = -1;
        RegionChanged();
        return moved && MoveRegionCorner(id, c, pt.x, pt.y);
    }

    public void CancelRegionCornerDrag()
    {
        if (_dragCorner < 0) return;
        _dragCorner = -1;
        RegionChanged();
    }

    bool RingBad(IReadOnlyList<(double x, double y)> ring) => ObjectEdits.RingProblemCode(ring) != ObjectEdits.RingOk;

    List<(double x, double y)>? DragRing()
    {
        if (_dragCorner < 0 || SelectedRegionId is not { } id || Region(id) is not { } r) return null;
        var ring = r.Points.ToList();
        if (_dragCorner < ring.Count) ring[_dragCorner] = _dragPt;
        return ring;
    }

    /// <summary>[ and ]: choose the active corner of the selected region (keyboard alternative to the mouse); arrows then nudge it.</summary>
    public void CycleCorner(int by)
    {
        if (SelectedRegionId is not { } id || Region(id) is not { } r) return;
        _activeCorner = ((_activeCorner + by) % r.Points.Count + r.Points.Count) % r.Points.Count;
        Say($"{id} corner {_activeCorner + 1} of {r.Points.Count}: arrows move it, Del deletes the region");
        RegionChanged();
    }

    public bool NudgeCorner(int right, int forward)
    {
        if (SelectedRegionId is not { } id || Region(id) is not { } r || _activeCorner >= r.Points.Count) return false;
        var f = -FlyCam.GlobalBasis.Z; var rt = FlyCam.GlobalBasis.X;
        if (Mathf.Abs(f.Y) > 0.98f) f = FlyCam.GlobalBasis.Y;
        var dir = new Vector2(rt.X * right + f.X * forward, -(rt.Z * right + f.Z * forward));
        if (dir.LengthSquared() < 1e-6f) return false;
        var step = Mathf.Abs(dir.X) >= Mathf.Abs(dir.Y) ? new Vector2(Mathf.Sign(dir.X), 0) : new Vector2(0, Mathf.Sign(dir.Y));
        var p = r.Points[_activeCorner];
        return MoveRegionCorner(id, _activeCorner, p.x + step.X * NudgeStep, p.y + step.Y * NudgeStep);
    }

    // ---- keys and mouse ----

    /// <summary>Region tool keys, modal keys and Ctrl+P. True when used.</summary>
    bool HandleRegionKey(InputEventKey k)
    {
        if (k.CtrlPressed && k.Keycode == Key.P) { if (_palette.Visible) ClosePalette(); else OpenPalette(); return true; }
        if (_confirm.Visible)
        {
            if (k.Keycode == Key.Escape) ConfirmCancel();
            return k.Keycode == Key.Escape; // Enter and Tab go to the focused button
        }
        if (_palette.Visible) { if (k.Keycode == Key.Escape) ClosePalette(); return k.Keycode == Key.Escape; }
        if (Tool != EditorTool.Region || _cheat.Visible) return false;
        if (k.CtrlPressed)
            return k.Keycode == Key.Z && !k.ShiftPressed && (_draft.Count > 0 || _colouring) && RemoveLastRegionPoint();
        if (k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.N: StartRegion(); return true;
            case Key.Enter or Key.KpEnter:
                if (_colouring) CommitRegion();
                else if (_draft.Count > 0) CloseRegion();
                else Say("press N, then click corners on the ground; Enter closes the shape");
                return true;
            case Key.Backspace: RemoveLastRegionPoint(); return true;
            case Key.Delete when SelectedRegionId != null && RegionMode == RegionState.Selected: RequestDeleteRegion(); return true;
            case Key.C when !_colouring && (_draft.Count > 0 || _arming || SelectedRegionId == null):
                // corner at the cursor, or at the screen centre when the cursor is over the panel (keyboard-only drawing)
                var mouse = GetViewport().GetMousePosition();
                var at = OverUi(mouse) ? GetViewport().GetVisibleRect().Size / 2 : mouse;
                if (RegionsReady() && RayToGround(FlyCam.ProjectRayOrigin(at), FlyCam.ProjectRayNormal(at), out var g))
                {
                    var (x, y) = SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
                    AddRegionPoint(x, y);
                }
                return true;
            case Key.Bracketleft: CycleCorner(-1); return true;
            case Key.Bracketright: CycleCorner(1); return true;
            case Key.Left when RegionMode == RegionState.Selected: NudgeCorner(-1, 0); return true;
            case Key.Right when RegionMode == RegionState.Selected: NudgeCorner(1, 0); return true;
            case Key.Up when RegionMode == RegionState.Selected: NudgeCorner(0, 1); return true;
            case Key.Down when RegionMode == RegionState.Selected: NudgeCorner(0, -1); return true;
            case Key.Escape:
                RegionEscape();
                return true;
        }
        return false;
    }

    /// <summary>Esc goes one level up: drag, colouring (back to the corners), drawing, selection, tool.</summary>
    public void RegionEscape()
    {
        switch (RegionMode)
        {
            case RegionState.Dragging: CancelRegionCornerDrag(); Say("move cancelled"); break;
            case RegionState.Colouring: _colouring = false; Say("back to the corners: click to add more, Enter closes again"); RegionChanged(); break;
            case RegionState.Drawing: CancelRegionDraft(); break;
            case RegionState.Selected: SelectRegion(null); break;
            default: SelectTool(EditorTool.None); break;
        }
    }

    void RegionLeftPress(Vector2 screen, bool free)
    {
        if (Modal || !RegionsReady()) return;
        GetViewport().GuiReleaseFocus();
        if (_colouring) { Say("the shape is closed: choose the colour, Enter creates it, Esc goes back to the corners", true); return; }
        var drafting = _draft.Count > 0 || _arming;
        if (!drafting && SelectedRegionId != null && Region(SelectedRegionId) is { } sr)
        {
            var c = PickCorner(screen, sr.Points);
            if (c >= 0) { StartCornerDrag(c); return; }
        }
        if (drafting && _draft.Count >= 3 && PickCorner(screen, new[] { _draft[0] }) == 0) { CloseRegion(); return; }
        if (!RayToGround(FlyCam.ProjectRayOrigin(screen), FlyCam.ProjectRayNormal(screen), out var g)) { Say("click on the ground", true); return; }
        var (x, y) = SnapAbs(g, free);
        if (drafting) { AddRegionPoint(x, y); return; }
        var (rx, ry) = RawAbs(g);
        if (RegionAt(rx, ry) is { } hit) { SelectRegion(hit); return; }
        if (SelectedRegionId != null) { SelectRegion(null); return; }
        AddRegionPoint(x, y);
    }

    void RegionChanged() { _panelDirty = true; _rgSignature = ""; }

    // ---- per frame: cursor, hover, drag, world preview ----

    void BuildRegionWorld()
    {
        _handleRoot = new Node3D { Name = "RegionHandles" };
        MarkerRoot.AddChild(_handleRoot);
        StandardMaterial3D Mat(Color c) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _handleMat = Mat(EditorTheme.Accent);
        _handleHot = Mat(Colors.White);
    }

    void UpdateRegionTool()
    {
        if (Tool != EditorTool.Region)
        {
            if (_rgSignature != "off") { _rgSignature = "off"; HideRegionWorld(); }
            return;
        }
        var mouse = GetViewport().GetMousePosition();
        var free = Input.IsKeyPressed(Key.Ctrl);
        var over = OverUi(mouse) || _looking || Modal;
        _cursor = null;
        if (!over && Map.OriginEpsg5514 != null && RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var g)) _cursor = SnapAbs(g, free);
        _hoverCorner = -1;
        if (!over && RegionMode is RegionState.Selected && SelectedRegionId != null && Region(SelectedRegionId) is { } sr) _hoverCorner = PickCorner(mouse, sr.Points);
        if (!over && RegionMode is RegionState.Drawing && _draft.Count >= 3 && PickCorner(mouse, new[] { _draft[0] }) == 0) _hoverCorner = 0;
        if (_dragCorner >= 0 && _cursor is { } c) DragCornerToAbs(c.x, c.y);
        RegionWorld();
    }

    void HideRegionWorld()
    {
        foreach (var n in new[] { OutlineRegionDraft, OutlineRegionGhost, OutlineRegionBad, OutlineRegionSel, OutlineRegionDrag }) SetOutline(n, new(), Colors.White, 0, 0);
        foreach (var h in _handles) h.Visible = false;
    }

    List<(Vector2, Vector2)> RingSegs(IReadOnlyList<(double x, double y)> pts, bool closed)
    {
        var s = new List<(Vector2, Vector2)>();
        var n = closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = FromAbsolute(pts[i].x, pts[i].y); var b = FromAbsolute(pts[(i + 1) % pts.Count].x, pts[(i + 1) % pts.Count].y);
            if (a.DistanceSquaredTo(b) > 1e-8f) s.Add((a, b));
        }
        return s;
    }

    void RegionWorld()
    {
        var sel = SelectedRegionId == null ? null : Region(SelectedRegionId);
        var dragRing = DragRing();
        var sig = string.Join(";", _draft) + "|" + _colouring + "|" + (_cursor?.ToString() ?? "-") + "|" + (sel == null ? "-" : sel.Id + sel.Colour + string.Join(",", sel.Points))
            + "|" + (dragRing == null ? "-" : _dragPt.ToString()) + "|" + _hoverCorner + "|" + _activeCorner;
        var show = new List<(double x, double y)>();
        var hot = -1;
        if (_draft.Count > 0 || _arming)
        {
            show = _draft.ToList();
            if (sig != _rgSignature)
            {
                var chain = RingSegs(_draft, _colouring);
                var ghost = new List<(Vector2, Vector2)>(); var bad = new List<(Vector2, Vector2)>();
                if (!_colouring && _cursor is { } cur && _draft.Count > 0)
                {
                    var ok = ObjectEdits.ChainAccepts(_draft, cur);
                    var a = FromAbsolute(_draft[^1].x, _draft[^1].y); var b = FromAbsolute(cur.x, cur.y);
                    if (a.DistanceSquaredTo(b) > 1e-8f) (ok ? ghost : bad).Add((a, b));
                    if (_draft.Count >= 2 && ok) { var f = FromAbsolute(_draft[0].x, _draft[0].y); if (b.DistanceSquaredTo(f) > 1e-8f) ghost.Add((b, f)); }
                }
                SetOutline(OutlineRegionDraft, _colouring && RingBad(_draft) ? new() : chain, EditorTheme.Accent, 0.31f, 0.45f);
                SetOutline(OutlineRegionGhost, ghost, new Color(EditorTheme.Accent, 0.5f), 0.30f, 0.35f);
                SetOutline(OutlineRegionBad, bad.Concat(_colouring && RingBad(_draft) ? chain : new()).ToList(), EditorTheme.Danger, 0.33f, 0.45f);
                SetOutline(OutlineRegionSel, new(), Colors.White, 0, 0);
                SetOutline(OutlineRegionDrag, new(), Colors.White, 0, 0);
            }
            hot = _hoverCorner;
        }
        else if (sel != null)
        {
            show = dragRing ?? sel.Points;
            if (sig != _rgSignature)
            {
                var bad = dragRing != null && RingBad(dragRing);
                SetOutline(OutlineRegionSel, dragRing == null ? RingSegs(sel.Points, true) : new(), EditorTheme.Accent, 0.31f, 0.45f);
                SetOutline(OutlineRegionDrag, dragRing != null && !bad ? RingSegs(dragRing, true) : new(), EditorTheme.Accent, 0.32f, 0.45f);
                SetOutline(OutlineRegionBad, bad ? RingSegs(dragRing!, true) : new(), EditorTheme.Danger, 0.33f, 0.45f);
                SetOutline(OutlineRegionDraft, new(), Colors.White, 0, 0);
                SetOutline(OutlineRegionGhost, new(), Colors.White, 0, 0);
            }
            hot = _dragCorner >= 0 ? _dragCorner : _hoverCorner >= 0 ? _hoverCorner : _activeCorner;
        }
        else if (sig != _rgSignature)
            foreach (var n in new[] { OutlineRegionDraft, OutlineRegionGhost, OutlineRegionBad, OutlineRegionSel, OutlineRegionDrag }) SetOutline(n, new(), Colors.White, 0, 0);
        _rgSignature = sig;

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

    // ---- sidebar page ----

    Button _rgNew = null!, _rgClose = null!, _rgCreate = null!, _rgBack = null!, _rgCancel = null!, _rgDelete = null!;
    Label _rgState = null!, _rgValid = null!, _rgNoOrigin = null!, _rgEmpty = null!, _rgListHead = null!, _rgColourLabel = null!;
    ColorPickerButton _rgPicker = null!;
    LineEdit _rgHex = null!;
    HBoxContainer _rgColourRow = null!;
    VBoxContainer _rgList = null!;
    bool _rgSync;
    readonly Dictionary<string, Button> _rgRows = new();

    Button FocusBtn(string text, Action onPress, string? variation = null)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All };
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += () => { onPress(); b.ReleaseFocus(); }; // Enter and Space belong to the viewport again
        return b;
    }

    static Label Note(string text, string variation = "Dim")
        => new() { Text = text, ThemeTypeVariation = variation, AutowrapMode = TextServer.AutowrapMode.WordSmart };

    void BuildRegionPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Regions", ThemeTypeVariation = "Header" });
        page.AddChild(Note("A hand-drawn colour shape on the ground, independent of parcel boundaries. Only its colour changes."));
        _rgNoOrigin = Note("This map has no origin_epsg5514, so regions cannot be placed. Export it again (map 0.4.0+).", "Danger");
        page.AddChild(_rgNoOrigin);
        _rgNew = FocusBtn("New region  (N)", StartRegion, "PrimaryButton");
        _rgNew.TooltipText = "Click corners on the ground, Enter closes the shape, then choose the colour";
        page.AddChild(_rgNew);

        _rgState = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Header" };
        _rgValid = Note("");
        page.AddChild(_rgState);
        page.AddChild(_rgValid);

        _rgColourRow = new HBoxContainer();
        _rgColourLabel = new Label { Text = "Colour" };
        _rgColourRow.AddChild(_rgColourLabel);
        _rgPicker = new ColorPickerButton
        {
            CustomMinimumSize = new Vector2(EditorTheme.RailButton, EditorTheme.Space * 4), EditAlpha = false, FocusMode = Control.FocusModeEnum.All,
            TooltipText = "Region colour. Enter opens the picker; closing it saves the change as one undo step.",
        };
        _rgPicker.ColorChanged += c => { if (!_rgSync) OnPickerColour(c); };
        _rgPicker.PopupClosed += CommitPickerColour;
        _rgColourRow.AddChild(_rgPicker);
        _rgHex = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "#rrggbb", MaxLength = 7, TooltipText = "Colour as #rrggbb; Enter applies" };
        _rgHex.TextChanged += t => RegionHexValidate(t);
        _rgHex.TextSubmitted += t => { RegionHexApply(t); _rgHex.ReleaseFocus(); };
        _rgHex.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { _rgHex.ReleaseFocus(); _rgHex.AcceptEvent(); RegionChanged(); } };
        _rgColourRow.AddChild(_rgHex);
        page.AddChild(_rgColourRow);

        _rgClose = FocusBtn("Close the shape  (Enter)", () => CloseRegion(), "PrimaryButton");
        _rgCreate = FocusBtn("Create the region  (Enter)", () => CommitRegion(), "PrimaryButton");
        _rgBack = FocusBtn("Remove the last corner  (Backspace)", () => RemoveLastRegionPoint());
        _rgCancel = FocusBtn("Cancel drawing  (Esc)", () => RegionEscape());
        _rgDelete = FocusBtn("Delete the region  (Del)", () => RequestDeleteRegion(), "DangerButton");
        foreach (var b in new[] { _rgClose, _rgCreate, _rgBack, _rgCancel, _rgDelete }) page.AddChild(b);

        page.AddChild(new HSeparator());
        _rgListHead = new Label { Text = "All regions", ThemeTypeVariation = "Header" };
        page.AddChild(_rgListHead);
        _rgEmpty = Note("No regions yet. Press N, click corners on the ground, Enter closes the shape.");
        page.AddChild(_rgEmpty);
        _rgList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_rgList);
        page.AddChild(Note("Where regions overlap the higher ID wins. Esc steps back: cancel drawing, deselect, leave the tool."));
    }

    void OnPickerColour(Color c)
    {
        var hex = "#" + c.ToHtml(false);
        if (RegionMode == RegionState.Colouring) { _newColour = hex; _rgHex.Text = hex; RegionChanged(); SaveUiConfig(); }
        else _rgHex.Text = hex; // a selected region is only saved when the picker closes
    }

    void CommitPickerColour()
    {
        if (RegionMode == RegionState.Selected && SelectedRegionId != null) SetRegionColour(SelectedRegionId, "#" + _rgPicker.Color.ToHtml(false));
    }

    bool RegionHexValidate(string text)
    {
        var ok = ParseColour(text) != null;
        _rgHex.TooltipText = ok ? "Colour as #rrggbb; Enter applies" : "Not a colour: use #rrggbb, for example #c8a050";
        _rgValid.Text = ok ? _rgValidText : "Colour must look like #rrggbb, for example #c8a050";
        _rgValid.ThemeTypeVariation = ok ? _rgValidVariation : "Danger";
        return ok;
    }

    string _rgValidText = "", _rgValidVariation = "Dim";

    /// <summary>Apply a typed colour: to the shape being coloured, or to the selected region (one save).</summary>
    public bool RegionHexApply(string text)
    {
        if (ParseColour(text) is not { } hex) { RegionHexValidate(text); return false; }
        if (RegionMode == RegionState.Colouring) { _newColour = hex; SaveUiConfig(); RegionChanged(); return true; }
        if (SelectedRegionId != null) return SetRegionColour(SelectedRegionId, hex);
        _newColour = hex; SaveUiConfig(); RegionChanged();
        return true;
    }

    /// <summary>The colour the next new region gets (also chosen after closing the shape).</summary>
    public void SetNewRegionColour(string hex)
    {
        if (ParseColour(hex) is { } c) { _newColour = c; RegionChanged(); SaveUiConfig(); }
    }

    static ImageTexture Swatch(string hex)
    {
        const int n = EditorTheme.Space * 3;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        img.Fill(Color.FromHtml(hex));
        for (var i = 0; i < n; i++) foreach (var (x, y) in new[] { (i, 0), (i, n - 1), (0, i), (n - 1, i) }) img.SetPixel(x, y, EditorTheme.Border);
        return ImageTexture.CreateFromImage(img);
    }

    public string RegionHintText() => RegionMode switch
    {
        RegionState.Dragging => "moving a corner: release saves, Esc cancels, red means the shape would cross itself",
        RegionState.Colouring => "shape closed: pick the colour, Enter creates the region, Esc goes back to the corners",
        RegionState.Drawing => _draft.Count == 0
            ? "new region: LMB or C places the first corner, Ctrl free position, Esc cancels"
            : $"drawing {_draft.Count}: LMB or C adds, Enter closes, Backspace undoes a corner, Esc cancels",
        RegionState.Selected => $"{SelectedRegionId}: drag a corner, [ ] and arrows move one, Del deletes, N new, Esc deselects",
        _ => "regions: click one to select, N or click empty ground to draw, Ctrl+P commands, Esc leaves the tool",
    };

    void RefreshRegionsPanel()
    {
        var hasOrigin = Map.OriginEpsg5514 != null;
        _rgNoOrigin.Visible = !hasOrigin;
        _rgNew.Disabled = !hasOrigin;
        var regions = Regions();
        if (_restoreRegion != null) { if (regions.Any(r => r.Id == _restoreRegion)) SelectedRegionId = _restoreRegion; _restoreRegion = null; }
        if (SelectedRegionId != null && regions.All(r => r.Id != SelectedRegionId)) { SelectedRegionId = null; _dragCorner = -1; }
        var sel = SelectedRegionId == null ? null : regions.First(r => r.Id == SelectedRegionId);
        var mode = RegionMode;

        _rgState.Text = mode switch
        {
            RegionState.Dragging => $"Moving corner {_dragCorner + 1} of {sel?.Id}",
            RegionState.Colouring => $"Shape closed: {_draft.Count} corners. Choose its colour.",
            RegionState.Drawing => _draft.Count == 0 ? "Click the ground to place the first corner" : $"Drawing: {_draft.Count} corner{(_draft.Count == 1 ? "" : "s")}",
            RegionState.Selected => $"{sel!.Id}: {sel.Points.Count} corners, {sel.AreaM2:F0} m2",
            _ => "Nothing selected",
        };
        string text, variation;
        if (mode is RegionState.Drawing or RegionState.Colouring)
        {
            var why = _draft.Count == 0 ? null : RegionDraftProblem();
            (text, variation) = _draft.Count == 0 ? ("Not saved until the shape is closed and coloured.", "Dim")
                : why != null ? (_draft.Count < 3 ? why : "Cannot close: " + why, _draft.Count < 3 ? "Warn" : "Danger")
                : (mode == RegionState.Colouring ? "Shape is valid. Not saved until you create it." : "Shape is valid; Enter closes it. Not saved yet.", "Dim");
        }
        else if (mode == RegionState.Selected) (text, variation) = ("Saved. Every change is saved at once and Ctrl+Z undoes it.", "Dim");
        else if (mode == RegionState.Dragging) (text, variation) = ("Release to save. A red outline would be refused.", "Dim");
        else (text, variation) = ("Click a region in the list or on the ground to select it.", "Dim");
        _rgValidText = text; _rgValidVariation = variation;
        _rgValid.Text = text; _rgValid.ThemeTypeVariation = variation;

        var colouring = mode == RegionState.Colouring;
        _rgColourRow.Visible = colouring || mode == RegionState.Selected;
        _rgSync = true;
        var shown = colouring ? _newColour : sel?.Colour ?? _newColour;
        if (!_rgPicker.GetPopup().Visible) _rgPicker.Color = Color.FromHtml(shown);
        if (!_rgHex.HasFocus()) _rgHex.Text = shown;
        _rgSync = false;
        _rgClose.Visible = mode == RegionState.Drawing && _draft.Count > 0;
        _rgClose.Disabled = RegionDraftProblem() != null;
        _rgCreate.Visible = colouring;
        _rgBack.Visible = (mode == RegionState.Drawing && _draft.Count > 0) || colouring;
        _rgBack.Text = colouring ? "Back to the corners  (Esc)" : "Remove the last corner  (Backspace)";
        _rgCancel.Visible = mode is RegionState.Drawing or RegionState.Colouring;
        _rgDelete.Visible = mode == RegionState.Selected;
        if (sel != null) _rgDelete.Text = $"Delete {sel.Id}  (Del)";

        _rgListHead.Text = $"All regions ({regions.Count})";
        _rgEmpty.Visible = regions.Count == 0;
        var sig = string.Join("|", regions.Select(r => $"{r.Id}{r.Colour}{r.Points.Count}")) + "#" + SelectedRegionId;
        if (sig != _listSignature)
        {
            _listSignature = sig;
            foreach (var b in _rgRows.Values) { _rgList.RemoveChild(b); b.QueueFree(); }
            _rgRows.Clear();
            foreach (var r in regions)
            {
                var id = r.Id;
                var b = new Button
                {
                    Text = $"{r.Id}   {r.Colour}   {r.Points.Count} corners", Icon = Swatch(r.Colour), Alignment = HorizontalAlignment.Left,
                    ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = $"Select {r.Id} ({r.AreaM2:F0} m2)",
                };
                b.SetPressedNoSignal(r.Id == SelectedRegionId);
                b.Pressed += () => { SelectRegion(id); b.ReleaseFocus(); };
                _rgList.AddChild(b);
                _rgRows[r.Id] = b;
            }
        }
        if (mode == RegionState.Selected) _rgState.TooltipText = "";
    }

    // test hooks
    public string RegionPanelState => _rgState.Text;
    public string RegionPanelValidation => _rgValid.Text;
    public int RegionRowCount => _rgRows.Count;
    public bool RegionEmptyShown => _rgEmpty.Visible;
    public string RegionHex => _rgHex.Text;
    public bool RegionDeleteButtonShown => _rgDelete.Visible;
    public bool RegionCreateButtonShown => _rgCreate.Visible;
    public bool RegionRowSelected(string id) => _rgRows.TryGetValue(id, out var b) && b.ButtonPressed;
    public string RegionHint => RegionHintText();
    public void PressRegionRow(string id) { if (_rgRows.TryGetValue(id, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
    public int RegionHandleCount => _handles.Count(h => h.Visible);

    /// <summary>Run the region world update now (smoke tests; normally per frame).</summary>
    public void RegionWorldNow() { _rgSignature = ""; RegionWorld(); }

    // ---- confirm dialog and palette UI ----

    PanelContainer _palette = null!;
    LineEdit _palEdit = null!;
    VBoxContainer _palList = null!;
    Label _palHint = null!;
    readonly List<(string label, Action run)> _palItems = new();
    int _palIndex;

    void BuildRegionOverlays(Control root)
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
        _palEdit = new LineEdit { PlaceholderText = "Type a command, a region ID (R-0001) or a parcel ID (P-0412)", ClearButtonEnabled = true };
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
        yield return ("Region: new  (N)", () => { SelectTool(EditorTool.Region); StartRegion(); });
        yield return ("Edit: undo  (Ctrl+Z)", () => Undo());
        yield return ("Edit: redo  (Ctrl+Y)", () => Redo());
        yield return ("View: top-down  (T)", ToggleTopDown);
        yield return ("Walk: drop to the ground here  (F3)", WalkFromHere);
        yield return ("Help: controls  (F1)", ToggleCheatSheet);
        foreach (var r in Regions()) { var id = r.Id; yield return ($"{r.Id}  region  {r.Colour}  {r.Points.Count} corners", () => { SelectTool(EditorTool.Region); SelectRegion(id); }); }
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
        if (_palItems.Count == 0) _palList.AddChild(new Label { Text = "Nothing matches. Try a tool, region or parcel ID.", ThemeTypeVariation = "Dim" });
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
