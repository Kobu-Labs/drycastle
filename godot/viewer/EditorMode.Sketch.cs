using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Godot;

/// <summary>
/// The Sketch tool: temporary marks on the ground that a prompt can name ("plant trees inside L1", "a lamp at A").
/// A sketch holds any number of points (A, B, C, ...) and lines (L1, L2, ...; a closed line joins its last corner to its first and marks the ground inside).
/// It is never written to edits.json: the marks go to editor-state.json next to the selection (see EditsWatcher), where
/// the MCP server's get_sketch reads them, and they are restored from that file when the editor starts.
///
/// States (the hint bar names each): Idle, Drawing (a line's corners are being placed), Selected. Every change of the
/// sketch (a new mark, a delete, a clear) is one step on the editor's undo stack, in order with the saves.
/// Esc goes one level up: drawing, selection, tool. The marks stay visible in every tool; they are edited only in this one.
/// </summary>
public partial class EditorMode
{
    public enum SketchKind { Point, Line }
    public enum SketchState { Idle, Drawing, Selected }

    public sealed record SketchPoint(string Id, double X, double Y);

    public sealed record SketchLine(string Id, bool Closed, IReadOnlyList<(double x, double y)> Points)
    {
        public double LengthM
        {
            get
            {
                double len = 0;
                var n = Closed ? Points.Count : Points.Count - 1;
                for (var i = 0; i < n; i++) { var p = Points[i]; var q = Points[(i + 1) % Points.Count]; len += Math.Sqrt((q.x - p.x) * (q.x - p.x) + (q.y - p.y) * (q.y - p.y)); }
                return len;
            }
        }

        public double AreaM2
        {
            get
            {
                if (!Closed) return 0;
                double a = 0;
                for (var i = 0; i < Points.Count; i++) { var p = Points[i]; var q = Points[(i + 1) % Points.Count]; a += p.x * q.y - q.x * p.y; }
                return Math.Abs(a) / 2;
            }
        }
    }

    /// <summary>One state of the sketch. Never changed in place: every edit makes a new one, so undo keeps the old.</summary>
    public sealed record SketchDoc(IReadOnlyList<SketchPoint> Points, IReadOnlyList<SketchLine> Lines, int NextPoint, int NextLine)
    {
        public static readonly SketchDoc Empty = new(Array.Empty<SketchPoint>(), Array.Empty<SketchLine>(), 0, 0);
        public int Count => Points.Count + Lines.Count;
    }

    public const string OutlineSketch = "outline_sketch", OutlineSketchSel = "outline_sketch_sel",
        OutlineSketchDraft = "outline_sketch_draft", OutlineSketchGhost = "outline_sketch_ghost";
    public static readonly string EmptySketchJson = BuildSketchJson(SketchDoc.Empty);

    SketchDoc _sketch = SketchDoc.Empty;
    SketchKind _skMode = SketchKind.Line;
    readonly List<(double x, double y)> _skDraft = new();
    string? _skSelected, _skHover;
    (double x, double y)? _skCursor;
    string _sketchJson = EmptySketchJson;
    int _skVersion;
    // every corner that can be clicked to select its mark, and the mark it belongs to
    readonly List<(double x, double y)> _skPickPts = new();
    readonly List<string> _skPickIds = new();

    public SketchDoc Sketch => _sketch;
    /// <summary>The sketch as the `sketch` value of editor-state.json (absolute S-JTSK metres).</summary>
    public string SketchJson => _sketchJson;
    public SketchKind SketchMode => _skMode;
    public string? SelectedSketchId => _skSelected;
    public IReadOnlyList<(double x, double y)> SketchDraft => _skDraft;
    public SketchState SketchPhase => _skDraft.Count > 0 ? SketchState.Drawing : _skSelected != null ? SketchState.Selected : SketchState.Idle;

    // ---- data ----

    /// <summary>Point names: A to Z, then AA, AB, ...</summary>
    public static string SketchPointLabel(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string BuildSketchJson(SketchDoc d)
    {
        var sb = new StringBuilder("{\"lines\":[");
        for (var i = 0; i < d.Lines.Count; i++)
        {
            var l = d.Lines[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"closed\":").Append(l.Closed ? "true" : "false").Append(",\"id\":").Append(JsonSerializer.Serialize(l.Id)).Append(",\"points\":[");
            for (var j = 0; j < l.Points.Count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append('[').Append(Num(l.Points[j].x)).Append(',').Append(Num(l.Points[j].y)).Append(']');
            }
            sb.Append("]}");
        }
        sb.Append("],\"next_line\":").Append(d.NextLine).Append(",\"next_point\":").Append(d.NextPoint).Append(",\"points\":[");
        for (var i = 0; i < d.Points.Count; i++)
        {
            var p = d.Points[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(p.Id)).Append(",\"x\":").Append(Num(p.X)).Append(",\"y\":").Append(Num(p.Y)).Append('}');
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>The sketch stored in a state document (the `sketch` value BuildSketchJson wrote); null when it has none.</summary>
    public static SketchDoc? ParseSketch(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("sketch", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var points = new List<SketchPoint>();
        var lines = new List<SketchLine>();
        if (s.TryGetProperty("points", out var ps))
            foreach (var p in ps.EnumerateArray())
                points.Add(new SketchPoint(p.GetProperty("id").GetString()!, p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble()));
        if (s.TryGetProperty("lines", out var ls))
            foreach (var l in ls.EnumerateArray())
            {
                var pts = l.GetProperty("points").EnumerateArray().Select(q => (q[0].GetDouble(), q[1].GetDouble())).ToList();
                if (pts.Count >= 2) lines.Add(new SketchLine(l.GetProperty("id").GetString()!, l.GetProperty("closed").GetBoolean() && pts.Count >= 3, pts));
            }
        var np = s.TryGetProperty("next_point", out var a) && a.TryGetInt32(out var ai) ? ai : 0;
        var nl = s.TryGetProperty("next_line", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
        return new SketchDoc(points, lines, Math.Max(np, points.Count), Math.Max(nl, lines.Count));
    }

    /// <summary>Bring back the sketch the last session left in editor-state.json (before the watcher writes that file again).</summary>
    void LoadSketchState()
    {
        if (Watcher == null || Watcher.StatePath == "" || Map.OriginEpsg5514 == null) return;
        try
        {
            if (!File.Exists(Watcher.StatePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(Watcher.StatePath));
            if (ParseSketch(doc.RootElement) is { Count: > 0 } sketch) ApplySketch(sketch);
        }
        catch (Exception e) { GD.PushWarning($"sketch not restored from {Watcher.StatePath}: {e.Message}"); }
    }

    bool SketchHas(string id) => _sketch.Points.Any(p => p.Id == id) || _sketch.Lines.Any(l => l.Id == id);

    List<string> SketchIds() => _sketch.Points.Select(p => p.Id).Concat(_sketch.Lines.Select(l => l.Id)).ToList();

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"2 points and 1 line", for messages and the confirm.</summary>
    public string SketchSummary()
    {
        var parts = new List<string>();
        if (_sketch.Points.Count > 0) parts.Add(Plural(_sketch.Points.Count, "point"));
        if (_sketch.Lines.Count > 0) parts.Add(Plural(_sketch.Lines.Count, "line"));
        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    void SketchChanged() { _skVersion++; _panelDirty = true; }

    /// <summary>Make <paramref name="doc"/> the sketch: selection, pick list, the state file (written within a frame or two) and the views follow.</summary>
    void ApplySketch(SketchDoc doc)
    {
        _sketch = doc;
        if (_skSelected != null && !SketchHas(_skSelected)) _skSelected = null;
        _skPickPts.Clear(); _skPickIds.Clear();
        foreach (var p in doc.Points) { _skPickPts.Add((p.X, p.Y)); _skPickIds.Add(p.Id); }
        foreach (var l in doc.Lines) foreach (var c in l.Points) { _skPickPts.Add(c); _skPickIds.Add(l.Id); }
        _sketchJson = BuildSketchJson(doc);
        Watcher?.StateSoon();
        SketchChanged();
    }

    /// <summary>One sketch edit: one undo step (no file is written, so it never fails).</summary>
    void CommitSketch(SketchDoc next, string undoText, string message)
    {
        PushUndo(new UndoRecord(undoText, new List<EntryChange>(), _sketch, next));
        ApplySketch(next);
        Say(message);
    }

    bool SketchReady()
    {
        if (Map.OriginEpsg5514 != null) return true;
        Say("this map has no origin_epsg5514 (export it again, map 0.4.0+); a sketch needs it", true);
        return false;
    }

    // ---- drawing ----

    /// <summary>P / L: what a click on the ground makes. Refused while a line is being drawn.</summary>
    public bool SetSketchMode(SketchKind mode)
    {
        if (mode == _skMode) { SketchChanged(); return true; }
        if (_skDraft.Count > 0) { Say("finish the line (Enter) or cancel it (Esc) first", true); SketchChanged(); return false; }
        _skMode = mode;
        Say(mode == SketchKind.Point ? "sketch: each click marks a point" : "sketch: clicks place the corners of a line, Enter finishes it");
        SketchChanged();
        SaveUiConfig();
        return true;
    }

    /// <summary>Mark a point (absolute S-JTSK metres). Returns its name.</summary>
    public string? AddSketchPoint(double x, double y, string? message = null)
    {
        if (!SketchReady()) return null;
        var id = SketchPointLabel(_sketch.NextPoint);
        var p = new SketchPoint(id, Math.Round(x, 3), Math.Round(y, 3));
        _skSelected = null;
        CommitSketch(_sketch with { Points = _sketch.Points.Append(p).ToList(), NextPoint = _sketch.NextPoint + 1 }, $"sketch point {id}", message ?? $"point {id} marked");
        return id;
    }

    /// <summary>Add a corner to the line being drawn. Refused on the spot of the corner before it.</summary>
    public bool AddSketchCorner(double x, double y)
    {
        if (!SketchReady()) return false;
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (_skDraft.Count > 0 && _skDraft[^1] == p) { Say("not placed: the same spot as the corner before it", true); return false; }
        if (_skDraft.Count == 0) _skSelected = null;
        _skDraft.Add(p);
        SketchChanged();
        return true;
    }

    /// <summary>Why the line being drawn cannot be closed, or null when it can.</summary>
    public string? SketchCloseProblem()
    {
        if (_skDraft.Count < 3) return $"a closed line needs at least 3 corners ({_skDraft.Count} placed)";
        var code = ObjectEdits.RingProblemCode(_skDraft);
        return code == ObjectEdits.RingOk ? null : "the outline " + RingMessage(code);
    }

    /// <summary>
    /// Enter: the corners placed so far become a line (closed: the last corner joins the first; a single corner: a point). Returns the new mark's
    /// name, or null when there is nothing to finish or the closed line would cross itself.
    /// </summary>
    public string? FinishSketchLine(bool closed = false)
    {
        if (_skDraft.Count == 0) { Say("no line is being drawn: click the ground to place its first corner", true); return null; }
        if (closed && SketchCloseProblem() is { } why) { Say("cannot close the line: " + why, true); return null; }
        if (_skDraft.Count == 1)
        {
            var (x, y) = _skDraft[0];
            _skDraft.Clear();
            var pid = AddSketchPoint(x, y);
            if (pid != null) Say($"one corner only: marked as point {pid}");
            return pid;
        }
        var pts = _skDraft.ToList();
        if (closed && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
        var id = $"L{_sketch.NextLine + 1}";
        var line = new SketchLine(id, closed, pts);
        _skDraft.Clear();
        _skSelected = null;
        CommitSketch(_sketch with { Lines = _sketch.Lines.Append(line).ToList(), NextLine = _sketch.NextLine + 1 }, $"sketch {(closed ? "closed line" : "line")} {id}",
            closed ? $"closed line {id} drawn: {pts.Count} corners, {line.AreaM2:F0} m2" : $"line {id} drawn: {pts.Count} corners, {line.LengthM:F1} m");
        return id;
    }

    /// <summary>Backspace: take the last corner back.</summary>
    public bool RemoveLastSketchCorner()
    {
        if (_skDraft.Count == 0) return false;
        _skDraft.RemoveAt(_skDraft.Count - 1);
        SketchChanged();
        return true;
    }

    /// <summary>Drop the line being drawn (Esc). It was not part of the sketch yet.</summary>
    public void CancelSketchDraft()
    {
        if (_skDraft.Count == 0) return;
        _skDraft.Clear();
        Say("line cancelled; the marks already made stay");
        SketchChanged();
    }

    /// <summary>Leave the drawing gesture (switching tools, leaving the editor). Finished marks stay.</summary>
    void CancelSketchGesture()
    {
        if (_skDraft.Count == 0) return;
        Say($"unfinished sketch line discarded ({Plural(_skDraft.Count, "corner")})");
        _skDraft.Clear();
        SketchChanged();
    }

    // ---- selection, delete, clear ----

    public bool SelectSketchItem(string? id)
    {
        if (id != null && !SketchHas(id)) { Say($"the sketch has no {id}", true); return false; }
        if (id == _skSelected) return true;
        _skSelected = id;
        SketchChanged();
        return true;
    }

    /// <summary>[ and ]: step through the marks (keyboard alternative to clicking one).</summary>
    public void CycleSketchItem(int by)
    {
        var ids = SketchIds();
        if (ids.Count == 0) { Say("the sketch is empty"); return; }
        if (_skDraft.Count > 0) { Say("finish the line (Enter) or cancel it (Esc) first", true); return; }
        var i = _skSelected == null ? (by > 0 ? -1 : 0) : ids.IndexOf(_skSelected);
        SelectSketchItem(ids[((i + by) % ids.Count + ids.Count) % ids.Count]);
        Say($"{_skSelected} selected: Del deletes it");
    }

    /// <summary>Del: remove one mark. Undo brings it back, so nothing is asked.</summary>
    public bool DeleteSketchItem(string id)
    {
        if (!SketchHas(id)) { Say($"the sketch has no {id}", true); return false; }
        var isPoint = _sketch.Points.Any(p => p.Id == id);
        var next = _sketch with { Points = _sketch.Points.Where(p => p.Id != id).ToList(), Lines = _sketch.Lines.Where(l => l.Id != id).ToList() };
        CommitSketch(next, $"delete sketch {(isPoint ? "point" : "line")} {id}", $"deleted {id} (Ctrl+Z brings it back)");
        return true;
    }

    /// <summary>Remove every mark: asks first and names what goes.</summary>
    public bool RequestClearSketch()
    {
        if (_sketch.Count == 0 && _skDraft.Count == 0) { Say("the sketch is already empty"); return false; }
        ShowConfirm("Clear the sketch?", $"{SketchSummary()} {(_sketch.Count == 1 ? "is" : "are")} removed from the ground and the names start again at A and L1. Undo (Ctrl+Z) brings them back.", "Clear", ClearSketch);
        return true;
    }

    public void ClearSketch()
    {
        _skDraft.Clear();
        if (_sketch.Count == 0) { SketchChanged(); return; }
        CommitSketch(SketchDoc.Empty, "clear the sketch", "sketch cleared (Ctrl+Z brings it back)");
    }

    // ---- keys and mouse ----

    /// <summary>Sketch tool keys. True when used.</summary>
    bool HandleSketchKey(InputEventKey k)
    {
        if (Tool != EditorTool.Sketch || _cheat.Visible) return false;
        if (k.CtrlPressed) return k.Keycode == Key.Z && !k.ShiftPressed && RemoveLastSketchCorner();
        if (k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.P: SetSketchMode(SketchKind.Point); return true;
            case Key.L: SetSketchMode(SketchKind.Line); return true;
            case Key.C:
                // a mark at the cursor, or at the screen centre when the cursor is over the panel (keyboard-only drawing)
                var mouse = GetViewport().GetMousePosition();
                var at = OverUi(mouse) ? GetViewport().GetVisibleRect().Size / 2 : mouse;
                if (SketchReady() && RayToGround(FlyCam.ProjectRayOrigin(at), FlyCam.ProjectRayNormal(at), out var g))
                {
                    var (x, y) = SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
                    if (_skMode == SketchKind.Point) AddSketchPoint(x, y); else AddSketchCorner(x, y);
                }
                return true;
            case Key.Enter or Key.KpEnter:
                if (_skDraft.Count > 0) FinishSketchLine(k.ShiftPressed);
                else Say(_skMode == SketchKind.Line ? "click corners on the ground first; Enter then finishes the line" : "click the ground to mark a point");
                return true;
            case Key.Backspace: RemoveLastSketchCorner(); return true;
            case Key.Delete when _skSelected != null && _skDraft.Count == 0: DeleteSketchItem(_skSelected); return true;
            case Key.Bracketleft: CycleSketchItem(-1); return true;
            case Key.Bracketright: CycleSketchItem(1); return true;
            case Key.Escape: SketchEscape(); return true;
        }
        return false;
    }

    /// <summary>Esc goes one level up: drawing, selection, tool.</summary>
    public void SketchEscape()
    {
        switch (SketchPhase)
        {
            case SketchState.Drawing: CancelSketchDraft(); break;
            case SketchState.Selected: SelectSketchItem(null); break;
            default: SelectTool(EditorTool.None); break;
        }
    }

    /// <summary>The mark whose point or corner is under a screen position; null when none is within reach.</summary>
    string? SketchItemAt(Vector2 screen)
    {
        var i = PickCorner(screen, _skPickPts);
        return i < 0 ? null : _skPickIds[i];
    }

    void SketchLeftPress(Vector2 screen, bool free)
    {
        if (Modal || !SketchReady()) return;
        GetViewport().GuiReleaseFocus();
        if (_skDraft.Count > 0)
        {
            // the first corner closes the line, the last corner again finishes it open
            if (_skDraft.Count >= 3 && PickCorner(screen, new[] { _skDraft[0] }) == 0) { FinishSketchLine(true); return; }
            if (PickCorner(screen, new[] { _skDraft[^1] }) == 0) { FinishSketchLine(); return; }
        }
        else if (SketchItemAt(screen) is { } hit) { SelectSketchItem(hit); return; }
        if (!RayToGround(FlyCam.ProjectRayOrigin(screen), FlyCam.ProjectRayNormal(screen), out var g)) { Say("click on the ground", true); return; }
        var (x, y) = SnapAbs(g, free);
        if (_skMode == SketchKind.Point) AddSketchPoint(x, y); else AddSketchCorner(x, y);
    }

    // ---- per frame: cursor, hover, world marks, name tags ----

    const int HandlePlain = 0, HandleHot = 1, HandleGhost = 2;
    Node3D _skRoot = null!;
    Control _skTagLayer = null!;
    StandardMaterial3D _skMat = null!, _skGhostMat = null!;
    readonly List<MeshInstance3D> _skHandles = new();
    readonly List<Label> _skTags = new();
    // what the handles and tags show; rebuilt when the sketch, the hover or the cursor changes, placed every frame
    readonly List<(Vector3 pos, float size, int look)> _skFixed = new(), _skLive = new();
    readonly List<Vector3> _skTagAt = new();
    (int version, string? hover) _skFixedSig = (-1, null);
    (int version, (double x, double y)? cursor, int hoverDraft, bool active, string? hover) _skLiveSig = (-1, null, -1, false, null);

    void BuildSketchTags(Control root)
    {
        // added first, so the rail, the sidebar and the status bar cover the tags
        _skTagLayer = new Control { Name = "SketchTags", MouseFilter = Control.MouseFilterEnum.Ignore };
        _skTagLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_skTagLayer);
    }

    void BuildSketchWorld()
    {
        _skRoot = new Node3D { Name = "SketchHandles" };
        MarkerRoot.AddChild(_skRoot);
        StandardMaterial3D Mat(Color c) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = true, RenderPriority = 10,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = c.A < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        };
        _skMat = Mat(EditorTheme.Sketch);
        _skGhostMat = Mat(new Color(EditorTheme.Sketch, 0.5f));
    }

    void UpdateSketchTool()
    {
        var active = Tool == EditorTool.Sketch;
        _skCursor = null; _skHover = null;
        var hoverDraft = -1;
        if (active && Map.OriginEpsg5514 != null)
        {
            var mouse = GetViewport().GetMousePosition();
            if (!(OverUi(mouse) || _looking || Modal))
            {
                if (RayToGround(FlyCam.ProjectRayOrigin(mouse), FlyCam.ProjectRayNormal(mouse), out var g)) _skCursor = SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
                if (_skDraft.Count == 0) _skHover = SketchItemAt(mouse);
                else if (_skDraft.Count >= 3 && PickCorner(mouse, new[] { _skDraft[0] }) == 0) hoverDraft = 0;
                else if (PickCorner(mouse, new[] { _skDraft[^1] }) == 0) hoverDraft = _skDraft.Count - 1;
            }
        }
        SketchWorld(active, hoverDraft);
    }

    static (double x, double y) TagAnchor(SketchLine l)
    {
        if (!l.Closed) return ((l.Points[0].x + l.Points[1].x) / 2, (l.Points[0].y + l.Points[1].y) / 2);
        return (l.Points.Average(p => p.x), l.Points.Average(p => p.y));
    }

    void SketchWorld(bool active, int hoverDraft)
    {
        if (Map.OriginEpsg5514 == null) return;
        var fixedSig = (_skVersion, _skHover);
        if (!fixedSig.Equals(_skFixedSig))
        {
            _skFixedSig = fixedSig;
            _skFixed.Clear(); _skTagAt.Clear();
            var plain = new List<(Vector2, Vector2)>(); var sel = new List<(Vector2, Vector2)>();
            var tag = 0;
            void Tag((double x, double y) at, string text)
            {
                if (tag == _skTags.Count)
                {
                    var l = new Label { ThemeTypeVariation = "SketchTag", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
                    _skTagLayer.AddChild(l);
                    _skTags.Add(l);
                }
                _skTags[tag++].Text = text;
                _skTagAt.Add(GroundPoint(at));
            }
            foreach (var p in _sketch.Points)
            {
                _skFixed.Add((GroundPoint((p.X, p.Y)), 1.4f, p.Id == _skSelected || p.Id == _skHover ? HandleHot : HandlePlain));
                Tag((p.X, p.Y), p.Id);
            }
            foreach (var l in _sketch.Lines)
            {
                (l.Id == _skSelected ? sel : plain).AddRange(RingSegs(l.Points, l.Closed));
                var look = l.Id == _skSelected || l.Id == _skHover ? HandleHot : HandlePlain;
                foreach (var c in l.Points) _skFixed.Add((GroundPoint(c), 0.7f, look));
                Tag(TagAnchor(l), l.Id);
            }
            for (var i = tag; i < _skTags.Count; i++) _skTags[i].Visible = false;
            SetOutline(OutlineSketch, plain, EditorTheme.Sketch, 0.36f, 0.4f);
            SetOutline(OutlineSketchSel, sel, EditorTheme.Text, 0.37f, 0.55f);
        }

        var liveSig = (_skVersion, _skCursor, hoverDraft, active, _skHover);
        if (!liveSig.Equals(_skLiveSig))
        {
            _skLiveSig = liveSig;
            _skLive.Clear();
            var ghost = new List<(Vector2, Vector2)>();
            for (var i = 0; i < _skDraft.Count; i++) _skLive.Add((GroundPoint(_skDraft[i]), 1f, i == hoverDraft ? HandleHot : HandlePlain));
            if (active && _skCursor is { } cur)
            {
                // the mark the next click makes: a ghost point, and the edge to it (to the first corner when the click would close the line)
                if (_skDraft.Count > 0)
                {
                    var to = hoverDraft == 0 ? _skDraft[0] : cur;
                    var a = FromAbsolute(_skDraft[^1].x, _skDraft[^1].y); var b = FromAbsolute(to.x, to.y);
                    if (a.DistanceSquaredTo(b) > 1e-8f) ghost.Add((a, b));
                }
                if (hoverDraft < 0 && _skHover == null) _skLive.Add((GroundPoint(cur), _skMode == SketchKind.Point ? 1.4f : 1f, HandleGhost));
            }
            SetOutline(OutlineSketchDraft, RingSegs(_skDraft, false), EditorTheme.Sketch, 0.38f, 0.4f);
            SetOutline(OutlineSketchGhost, ghost, new Color(EditorTheme.Sketch, 0.5f), 0.35f, 0.35f);
        }

        // handles: sized by distance so they stay clickable from any height
        var n = _skFixed.Count + _skLive.Count;
        while (_skHandles.Count < n)
        {
            var mi = new MeshInstance3D { Mesh = _handleMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _skRoot.AddChild(mi);
            _skHandles.Add(mi);
        }
        var cam = FlyCam.GlobalPosition;
        for (var i = 0; i < _skHandles.Count; i++)
        {
            var h = _skHandles[i];
            h.Visible = i < n;
            if (i >= n) continue;
            var (pos, size, look) = i < _skFixed.Count ? _skFixed[i] : _skLive[i - _skFixed.Count];
            h.Position = pos;
            var s = Mathf.Clamp(pos.DistanceTo(cam) * 0.011f, 0.35f, 8f) * size * (look == HandleHot ? 1.3f : 1f);
            h.Scale = new Vector3(s, s, s);
            h.MaterialOverride = look == HandleHot ? _handleHot : look == HandleGhost ? _skGhostMat : _skMat;
        }

        // name tags follow their marks on the screen
        var offset = new Vector2(EditorTheme.Space, -EditorTheme.Space * 4);
        for (var i = 0; i < _skTagAt.Count; i++)
        {
            var t = _skTags[i];
            var behind = FlyCam.IsPositionBehind(_skTagAt[i]);
            t.Visible = !behind;
            if (!behind) t.Position = FlyCam.UnprojectPosition(_skTagAt[i]) + offset;
        }
    }

    // ---- sidebar page ----

    Button _skBtnPoint = null!, _skBtnLine = null!, _skFinish = null!, _skCloseBtn = null!, _skBack = null!, _skCancel = null!, _skDelete = null!, _skClear = null!;
    Label _skNoOrigin = null!, _skStateLabel = null!, _skNote = null!, _skListHead = null!, _skEmpty = null!;
    VBoxContainer _skList = null!;
    string _skListSig = "";
    readonly Dictionary<string, Button> _skRows = new();

    void BuildSketchPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Sketch", ThemeTypeVariation = "Header" });
        page.AddChild(Note("Temporary points and lines on the ground that a prompt can name: \"plant trees inside L1\", \"a lamp at A\". They are not part of the map and are never saved to it."));
        _skNoOrigin = Note("This map has no origin_epsg5514, so nothing can be marked. Export it again (map 0.4.0+).", "Danger");
        page.AddChild(_skNoOrigin);

        var modes = new HBoxContainer();
        Button Mode(string text, string tip, SketchKind kind)
        {
            var b = new Button { Text = text, ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            b.Pressed += () => { SetSketchMode(kind); b.ReleaseFocus(); };
            modes.AddChild(b);
            return b;
        }
        _skBtnPoint = Mode("Point  (P)", "Each click on the ground marks a point: A, B, C, ...", SketchKind.Point);
        _skBtnLine = Mode("Line  (L)", "Clicks place the corners of a line: L1, L2, ... Enter finishes it; a click on its first corner closes it, to mark the ground inside", SketchKind.Line);
        page.AddChild(modes);

        _skStateLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Header" };
        _skNote = Note("");
        page.AddChild(_skStateLabel);
        page.AddChild(_skNote);

        _skFinish = FocusBtn("Finish the line  (Enter)", () => FinishSketchLine(), "PrimaryButton");
        _skCloseBtn = FocusBtn("Close the line  (Shift+Enter)", () => FinishSketchLine(true));
        _skCloseBtn.TooltipText = "The last corner is joined to the first; a click on the first corner does the same";
        _skBack = FocusBtn("Remove the last corner  (Backspace)", () => RemoveLastSketchCorner());
        _skCancel = FocusBtn("Cancel the line  (Esc)", CancelSketchDraft);
        _skDelete = FocusBtn("Delete  (Del)", () => { if (_skSelected != null) DeleteSketchItem(_skSelected); }, "DangerButton");
        foreach (var b in new[] { _skFinish, _skCloseBtn, _skBack, _skCancel, _skDelete }) page.AddChild(b);

        page.AddChild(new HSeparator());
        _skListHead = new Label { Text = "Marks", ThemeTypeVariation = "Header" };
        page.AddChild(_skListHead);
        _skEmpty = Note("Nothing marked yet. Click the ground, or press C to mark the spot under the cursor.");
        page.AddChild(_skEmpty);
        _skList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_skList);
        _skClear = FocusBtn("Clear the sketch", () => RequestClearSketch(), "DangerButton");
        _skClear.TooltipText = "Remove every mark (asks first; Ctrl+Z brings them back)";
        page.AddChild(_skClear);
        page.AddChild(Note("Marks stay visible in every tool. Esc steps back: cancel the line, deselect, leave the tool."));
    }

    public string SketchHintText() => SketchPhase switch
    {
        SketchState.Drawing => $"line, {Plural(_skDraft.Count, "corner")}: LMB or C adds, Enter finishes, Shift+Enter or the first corner closes it, Backspace undoes a corner, Esc cancels",
        SketchState.Selected => $"{_skSelected}: Del deletes, [ ] next mark, click the ground to draw, Ctrl+Z undo, Esc deselects",
        _ => _skMode == SketchKind.Point
            ? "sketch points: LMB or C marks a point, Ctrl free position, L draws lines, click a mark to select it, Esc leaves the tool"
            : "sketch lines: LMB or C places the first corner, Ctrl free position, P marks points, click a mark to select it, Esc leaves the tool",
    };

    static string SketchRowText(SketchLine l)
        => $"{l.Id}   {(l.Closed ? "closed line" : "line")}   {Plural(l.Points.Count, "corner")}   " + (l.Closed ? $"{l.AreaM2:F0} m2" : $"{l.LengthM:F0} m");

    void RefreshSketchPanel()
    {
        var hasOrigin = Map.OriginEpsg5514 != null;
        _skNoOrigin.Visible = !hasOrigin;
        _skBtnPoint.Disabled = _skBtnLine.Disabled = !hasOrigin;
        _skBtnPoint.SetPressedNoSignal(_skMode == SketchKind.Point);
        _skBtnLine.SetPressedNoSignal(_skMode == SketchKind.Line);
        var phase = SketchPhase;
        var n = _skDraft.Count;
        var selPoint = _skSelected == null ? null : _sketch.Points.FirstOrDefault(p => p.Id == _skSelected);
        var selLine = _skSelected == null ? null : _sketch.Lines.FirstOrDefault(l => l.Id == _skSelected);

        string state, note, variation = "Dim";
        if (phase == SketchState.Drawing)
        {
            state = $"Drawing a line: {Plural(n, "corner")}, {new SketchLine("", false, _skDraft).LengthM:F1} m";
            var why = n >= 3 ? SketchCloseProblem() : null;
            if (n == 1) note = "Enter now marks a single point. Click to add corners.";
            else if (n == 2) note = "Enter finishes the line. A closed line needs 3 corners.";
            else if (why == null) note = "Enter finishes the line. Shift+Enter, or a click on the first corner, closes it to mark the ground inside.";
            else { note = $"Cannot be closed: {why}. Enter still finishes it as an open line."; variation = "Warn"; }
        }
        else if (selPoint != null) (state, note) = ($"{selPoint.Id}: point at x {selPoint.X:F1}, y {selPoint.Y:F1}", $"Name it in a prompt: \"at {selPoint.Id}\". Del removes it, Ctrl+Z brings it back.");
        else if (selLine != null)
            (state, note) = ($"{selLine.Id}: {(selLine.Closed ? "closed line" : "line")}, {Plural(selLine.Points.Count, "corner")}, {selLine.LengthM:F1} m{(selLine.Closed ? $" around, {selLine.AreaM2:F0} m2" : "")}",
                $"Name it in a prompt: \"{(selLine.Closed ? "inside" : "along")} {selLine.Id}\". Del removes it, Ctrl+Z brings it back.");
        else (state, note) = (_skMode == SketchKind.Point ? "Click the ground to mark a point" : "Click the ground to place the first corner", "Click a mark, or a row below, to select it.");
        _skStateLabel.Text = state;
        _skNote.Text = note; _skNote.ThemeTypeVariation = variation;

        var drawing = phase == SketchState.Drawing;
        _skFinish.Visible = drawing;
        _skFinish.Text = n == 1 ? "Mark it as a point  (Enter)" : "Finish the line  (Enter)";
        _skCloseBtn.Visible = drawing && n >= 3;
        _skCloseBtn.Disabled = drawing && SketchCloseProblem() != null;
        _skBack.Visible = _skCancel.Visible = drawing;
        _skDelete.Visible = phase == SketchState.Selected;
        if (_skSelected != null) _skDelete.Text = $"Delete {_skSelected}  (Del)";

        _skListHead.Text = $"Marks ({_sketch.Count})";
        _skEmpty.Visible = _sketch.Count == 0;
        _skClear.Visible = _sketch.Count > 0;
        var sig = _sketchJson + "#" + _skSelected;
        if (sig == _skListSig) return;
        _skListSig = sig;
        foreach (var b in _skRows.Values) { _skList.RemoveChild(b); b.QueueFree(); }
        _skRows.Clear();
        void Row(string id, string text, string tip)
        {
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip };
            b.SetPressedNoSignal(id == _skSelected);
            b.Pressed += () => { if (_skDraft.Count > 0) CancelSketchDraft(); SelectSketchItem(id); b.ReleaseFocus(); };
            _skList.AddChild(b);
            _skRows[id] = b;
        }
        foreach (var p in _sketch.Points) Row(p.Id, $"{p.Id}   point", $"Select point {p.Id} (x {p.X:F1}, y {p.Y:F1})");
        foreach (var l in _sketch.Lines) Row(l.Id, SketchRowText(l), $"Select {(l.Closed ? "closed line" : "line")} {l.Id}");
    }

    // test hooks
    public string SketchPanelState => _skStateLabel.Text;
    public string SketchPanelNote => _skNote.Text;
    public int SketchRowCount => _skRows.Count;
    public bool SketchEmptyShown => _skEmpty.Visible;
    public string SketchHint => SketchHintText();
    public int SketchHandleCount => _skHandles.Count(h => h.Visible);
    public int SketchTagCount => _skTagAt.Count;

    /// <summary>Run the sketch world update now (smoke tests; normally per frame).</summary>
    public void SketchWorldNow() { _skFixedSig = (-1, null); _skLiveSig = (-1, null, -1, false, null); SketchWorld(Tool == EditorTool.Sketch, -1); }
}
