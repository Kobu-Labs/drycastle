using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SketchState = Drycastle.ShooterCore.Editor.EditorSketchState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Sketch tool: temporary marks on the ground that a prompt can name ("plant trees inside L1", "a lamp at A").
/// Marks are grouped in named sketches (ROAD, FENCE_PLAN, ...); each sketch holds any number of points (A, B, C, ...) and lines (L1, L2, ...; a closed line
/// joins its last corner to its first and marks the ground inside). Mark names are unique across all sketches. New marks go to the active sketch.
/// It is never written to edits.json: the marks live in the shared per-user sketch.json (~/.local/state/drycastle/, every worktree sees it), a file the MCP server reads and writes too
/// (sketch_add_*, get_sketch): the editor reloads it when its version changes, as one undo step, and writes it on every local change.
///
/// States (the hint bar names each): Idle, Drawing (a line's corners are being placed), Selected. Every change of the
/// sketch (a new mark, a delete, a clear) is one step on the editor's undo stack, in order with the saves.
/// Esc goes one level up: drawing, selection, tool. The marks stay visible in every tool; they are edited only in this one.
/// </summary>
public sealed class SketchTool : EditorPart
{
    SketchDoc _sketch = SketchDoc.Empty;
    SketchKind _mode = SketchKind.Line;
    readonly List<(double x, double y)> _draft = new();
    // Editor.Sketch (docs/state-machines.md): Drawing is entered with the first corner of a line and left when the draft ends; Idle and Selected follow the
    // selection (every change goes through the Selected setter) while no line is being drawn. Draft changes go through DraftAdd/DraftEnd only.
    readonly Drycastle.ShooterCore.Fsm.StateMachine<SketchState> _fsm = new(Drycastle.ShooterCore.Editor.EditorMachines.SketchSpec);
    string? _selected;

    void Rest()
    {
        if (_fsm.IsAny(SketchState.Idle, SketchState.Selected)) _fsm.Enter(_selected != null ? SketchState.Selected : SketchState.Idle);
    }

    void DraftAdd((double x, double y) p) { _draft.Add(p); _fsm.Enter(SketchState.Drawing); }

    /// <summary>The draft is gone (finished, cancelled, taken back to nothing, a mark deleted or the sketch replaced): Drawing ends.</summary>
    void DraftEnd()
    {
        _draft.Clear();
        if (_fsm.Is(SketchState.Drawing)) _fsm.Enter(_selected != null ? SketchState.Selected : SketchState.Idle);
    }

    string _sketchJson = "";
    int _version;                        // goes up on every change: what the marks and the panel redraw on
    string _active = SketchDoc.DefaultSketch;
    readonly SketchFileSync _file;
    readonly SketchMarks _marks;
    readonly SketchPanel _panel;
    // every corner that can be clicked to select its mark, and the mark it belongs to
    readonly List<(double x, double y)> _pickPts = new();
    readonly List<string> _pickIds = new();

    public SketchTool(IEditorHost host) : base(host)
    {
        _file = new SketchFileSync(host);
        _marks = new SketchMarks(host);
        _panel = new SketchPanel(host, this);
    }

    public SketchDoc Doc => _sketch;

    // what the panel shows
    internal SketchState Phase => _fsm.Current;
    internal string? Selected { get => _selected; private set { _selected = value; Rest(); } }
    internal IReadOnlyList<(double x, double y)> Draft => _draft;
    internal SketchKind Mode => _mode;
    internal string ActiveSketch => _active;
    /// <summary>The sketch as the text of sketch.json: changes whenever a mark does.</summary>
    internal string Json => _sketchJson;

    // ---- the parts' hooks ----

    public override void Update(double delta)
    {
        UpdateMarks();
        _file.Poll(delta, _sketch, AdoptFile);
    }

    /// <summary>Leaving the editor drops the line being drawn.</summary>
    public override void ActiveChanged(bool on)
    {
        if (!on) CancelGesture();
    }

    public override void LoadConfig(ConfigFile cfg)
    {
        if (Enum.TryParse<SketchKind>((string)cfg.GetValue("ui", "sketch_mode", _mode.ToString()), true, out var sk)) _mode = sk;
    }

    public override void SaveConfig(ConfigFile cfg) => cfg.SetValue("ui", "sketch_mode", _mode.ToString());

    public void BuildTags(Control root) => _marks.BuildTags(root);
    public void BuildWorld() => _marks.BuildWorld();
    public void BuildPage(VBoxContainer page) => _panel.Build(page);
    public void RefreshPanel() => _panel.Refresh();

    // ---- sketch.json ----

    /// <summary>Bring back the sketch the last session left, before anything is written.</summary>
    public void Restore() => _file.Load((doc, write) => ApplySketch(doc, write));

    /// <summary>Something else (the MCP server) wrote a newer sketch.json: take it over. One undo step.</summary>
    void AdoptFile(SketchDoc next)
    {
        if (_draft.Count > 0) { DraftEnd(); Changed(); }
        Host.Edits.History.Push(new UndoRecord("sketch changed by the MCP server", new List<EntryChange>(), _sketch, next));
        ApplySketch(next, write: false);
        Host.Say($"sketch updated from sketch.json: {Summary()} (Ctrl+Z undoes it)");
    }

    /// <summary>An undo or redo step of the sketch: the sketch becomes <paramref name="doc"/>.</summary>
    public void Replay(SketchDoc doc)
    {
        DraftEnd();
        ApplySketch(doc);
    }

    string? SketchOf(string id) => _sketch.Points.FirstOrDefault(p => p.Id == id)?.Sketch ?? _sketch.Lines.FirstOrDefault(l => l.Id == id)?.Sketch;

    bool Has(string id) => _sketch.Points.Any(p => p.Id == id) || _sketch.Lines.Any(l => l.Id == id);

    List<string> Ids() => _sketch.Points.Select(p => p.Id).Concat(_sketch.Lines.Select(l => l.Id)).ToList();

    internal static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"2 points and 1 line" (of one sketch, or of all when null), for messages and the confirm.</summary>
    string Summary(string? sketch = null)
    {
        var pts = sketch == null ? _sketch.Points.Count : _sketch.Points.Count(p => p.Sketch == sketch);
        var lns = sketch == null ? _sketch.Lines.Count : _sketch.Lines.Count(l => l.Sketch == sketch);
        var parts = new List<string>();
        if (pts > 0) parts.Add(Plural(pts, "point"));
        if (lns > 0) parts.Add(Plural(lns, "line"));
        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    internal void Changed() { _version++; Host.MarkPanelDirty(); }

    /// <summary>Make <paramref name="doc"/> the sketch: selection, pick list, the state file (written within a frame or two) and the views follow.</summary>
    void ApplySketch(SketchDoc doc, bool write = true)
    {
        if (doc.Count == 0) doc = doc with { NextPoint = 0, NextLine = 0 }; // nothing left: the names start again at A and L1
        _sketch = doc;
        if (Selected != null && !Has(Selected)) Selected = null;
        if (!doc.Names.Contains(_active)) _active = doc.Names.Count > 0 ? doc.Names[0] : SketchDoc.DefaultSketch;
        _pickPts.Clear(); _pickIds.Clear();
        foreach (var p in doc.Points) { _pickPts.Add((p.X, p.Y)); _pickIds.Add(p.Id); }
        foreach (var l in doc.Lines) foreach (var c in l.Points) { _pickPts.Add(c); _pickIds.Add(l.Id); }
        _sketchJson = doc.ToJson();
        if (write) _file.Write(doc);
        Changed();
    }

    /// <summary>The sketch with its active sketch's name listed (a first mark makes DEFAULT), for an edit that adds a mark to it.</summary>
    SketchDoc WithActive()
        => _sketch.Names.Contains(_active) ? _sketch : _sketch with { Names = _sketch.Names.Append(_active).ToList() };

    /// <summary>One sketch edit: one undo step (sketch.json is written, edits.json never).</summary>
    void CommitSketch(SketchDoc next, string undoText, string message)
    {
        Host.Edits.History.Push(new UndoRecord(undoText, new List<EntryChange>(), _sketch, next));
        ApplySketch(next);
        Host.Say(message);
    }

    bool RequireOrigin()
    {
        if (Host.Map.OriginEpsg5514 != null) return true;
        Host.Say("this map has no origin_epsg5514 (export it again, map 0.4.0+); a sketch needs it", true);
        return false;
    }

    // ---- drawing ----

    /// <summary>P / L: what a click on the ground makes. Refused while a line is being drawn.</summary>
    public bool SetMode(SketchKind mode)
    {
        if (mode == _mode) { Changed(); return true; }
        if (_draft.Count > 0) { Host.Say("finish the line (Enter) or cancel it (Esc) first", true); Changed(); return false; }
        _mode = mode;
        Host.Say(mode == SketchKind.Point ? "sketch: each click marks a point" : "sketch: clicks place the corners of a line, Enter finishes it");
        Changed();
        Host.SaveUiConfig();
        return true;
    }

    /// <summary>Mark a point (absolute S-JTSK metres). Returns its name.</summary>
    string? AddPoint(double x, double y, string? message = null)
    {
        if (!RequireOrigin()) return null;
        var id = SketchDoc.PointLabel(_sketch.NextPoint);
        var p = new SketchPoint(id, Math.Round(x, 3), Math.Round(y, 3), _active);
        Selected = null;
        CommitSketch(WithActive() with { Points = _sketch.Points.Append(p).ToList(), NextPoint = _sketch.NextPoint + 1 }, $"sketch point {id}", message ?? $"point {id} marked in {_active}");
        return id;
    }

    /// <summary>Add a corner to the line being drawn. Refused on the spot of the corner before it.</summary>
    bool AddCorner(double x, double y)
    {
        if (!RequireOrigin()) return false;
        var p = (Math.Round(x, 3), Math.Round(y, 3));
        if (_draft.Count > 0 && _draft[^1] == p) { Host.Say("not placed: the same spot as the corner before it", true); return false; }
        if (_draft.Count == 0) Selected = null;
        DraftAdd(p);
        Changed();
        return true;
    }

    /// <summary>Why the line being drawn cannot be closed, or null when it can.</summary>
    internal string? CloseProblem()
    {
        if (_draft.Count < 3) return $"a closed line needs at least 3 corners ({_draft.Count} placed)";
        var code = ObjectEdits.RingProblemCode(_draft);
        return code == ObjectEdits.RingOk ? null : "the outline " + EditorMode.RingMessage(code);
    }

    /// <summary>
    /// Enter: the corners placed so far become a line (closed: the last corner joins the first; a single corner: a point). Returns the new mark's
    /// name, or null when there is nothing to finish or the closed line would cross itself.
    /// </summary>
    internal string? FinishLine(bool closed = false)
    {
        if (_draft.Count == 0) { Host.Say("no line is being drawn: click the ground to place its first corner", true); return null; }
        if (closed && CloseProblem() is { } why) { Host.Say("cannot close the line: " + why, true); return null; }
        if (_draft.Count == 1)
        {
            var (x, y) = _draft[0];
            DraftEnd();
            var pid = AddPoint(x, y);
            if (pid != null) Host.Say($"one corner only: marked as point {pid}");
            return pid;
        }
        var pts = _draft.ToList();
        if (closed && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
        var id = $"L{_sketch.NextLine + 1}";
        var line = new SketchLine(id, closed, pts, _active);
        DraftEnd();
        Selected = null;
        CommitSketch(WithActive() with { Lines = _sketch.Lines.Append(line).ToList(), NextLine = _sketch.NextLine + 1 }, $"sketch {(closed ? "closed line" : "line")} {id}",
            closed ? $"closed line {id} drawn in {_active}: {pts.Count} corners, {line.AreaM2:F0} m2" : $"line {id} drawn in {_active}: {pts.Count} corners, {line.LengthM:F1} m");
        return id;
    }

    /// <summary>Backspace: take the last corner back.</summary>
    internal bool RemoveLastCorner()
    {
        if (_draft.Count == 0) return false;
        _draft.RemoveAt(_draft.Count - 1);
        if (_draft.Count == 0) DraftEnd();
        Changed();
        return true;
    }

    /// <summary>Drop the line being drawn (Esc). It was not part of the sketch yet.</summary>
    internal void CancelDraft()
    {
        if (_draft.Count == 0) return;
        DraftEnd();
        Host.Say("line cancelled; the marks already made stay");
        Changed();
    }

    /// <summary>Leave the drawing gesture (switching tools, leaving the editor). Finished marks stay.</summary>
    public void CancelGesture()
    {
        if (_draft.Count == 0) return;
        Host.Say($"unfinished sketch line discarded ({Plural(_draft.Count, "corner")})");
        DraftEnd();
        Changed();
    }

    // ---- selection, delete, clear ----

    public bool SelectItem(string? id)
    {
        if (id != null && !Has(id)) { Host.Say($"the sketch has no {id}", true); return false; }
        if (id == Selected) return true;
        Selected = id;
        if (id != null) _active = SketchOf(id) ?? _active;
        Changed();
        return true;
    }

    /// <summary>[ and ]: step through the marks (keyboard alternative to clicking one).</summary>
    void CycleItem(int by)
    {
        var ids = Ids();
        if (ids.Count == 0) { Host.Say("the sketch is empty"); return; }
        if (_draft.Count > 0) { Host.Say("finish the line (Enter) or cancel it (Esc) first", true); return; }
        var i = Selected == null ? (by > 0 ? -1 : 0) : ids.IndexOf(Selected);
        SelectItem(ids[((i + by) % ids.Count + ids.Count) % ids.Count]);
        Host.Say($"{Selected} selected: Del deletes it");
    }

    /// <summary>Del: remove one mark. Undo brings it back, so nothing is asked.</summary>
    internal bool DeleteItem(string id)
    {
        if (!Has(id)) { Host.Say($"the sketch has no {id}", true); return false; }
        var isPoint = _sketch.Points.Any(p => p.Id == id);
        var next = _sketch with { Points = _sketch.Points.Where(p => p.Id != id).ToList(), Lines = _sketch.Lines.Where(l => l.Id != id).ToList() };
        CommitSketch(next, $"delete sketch {(isPoint ? "point" : "line")} {id}", $"deleted {id} (Ctrl+Z brings it back)");
        return true;
    }

    /// <summary>Remove every mark of the active sketch (it stays, empty): asks first and names what goes.</summary>
    public bool RequestClear()
    {
        if (_sketch.CountIn(_active) == 0 && _draft.Count == 0) { Host.Say($"sketch {_active} is already empty"); return false; }
        var n = _sketch.CountIn(_active);
        Host.Confirm.Show($"Clear sketch {_active}?", $"{Summary(_active)} {(n == 1 ? "is" : "are")} removed from the ground; the sketch itself stays. Undo (Ctrl+Z) brings {(n == 1 ? "it" : "them")} back.", "Clear", ClearSketch);
        return true;
    }

    void ClearSketch()
    {
        DraftEnd();
        var name = _active;
        if (_sketch.CountIn(name) == 0) { Changed(); return; }
        CommitSketch(_sketch with { Points = _sketch.Points.Where(p => p.Sketch != name).ToList(), Lines = _sketch.Lines.Where(l => l.Sketch != name).ToList() },
            $"clear sketch {name}", $"sketch {name} cleared (Ctrl+Z brings it back)");
    }

    // ---- sketches: new, switch, rename, delete ----

    internal string? NameProblem(string name, string? except = null)
    {
        if (!SketchDoc.ValidName(name)) return "a sketch name is 1 to 24 letters, digits, _ or -";
        if (_sketch.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) && n != except)) return $"there is a sketch {name} already";
        return null;
    }

    /// <summary>Make a new, empty sketch and draw into it from now on.</summary>
    internal bool NewSketch(string name)
    {
        name = name.Trim();
        if (NameProblem(name) is { } why) { Host.Say(why, true); return false; }
        if (_draft.Count > 0) { Host.Say("finish the line (Enter) or cancel it (Esc) first", true); return false; }
        var names = _sketch.Names.Append(name).ToList();
        _active = name; Selected = null;
        CommitSketch(_sketch with { Names = names }, $"new sketch {name}", $"sketch {name} made: the next marks go into it");
        return true;
    }

    internal bool SetActiveSketch(string name)
    {
        if (!_sketch.Names.Contains(name)) { Host.Say($"there is no sketch {name}", true); return false; }
        if (_draft.Count > 0) { Host.Say("finish the line (Enter) or cancel it (Esc) first", true); Changed(); return false; }
        if (name == _active) return true;
        _active = name;
        if (Selected != null && SketchOf(Selected) != name) Selected = null;
        Host.Say($"drawing into sketch {name}");
        Changed();
        return true;
    }

    internal bool RenameSketch(string from, string to)
    {
        to = to.Trim();
        if (!_sketch.Names.Contains(from)) { Host.Say($"there is no sketch {from}", true); return false; }
        if (to == from) return true;
        if (NameProblem(to, from) is { } why) { Host.Say(why, true); return false; }
        var next = _sketch with
        {
            Names = _sketch.Names.Select(n => n == from ? to : n).ToList(),
            Points = _sketch.Points.Select(p => p.Sketch == from ? p with { Sketch = to } : p).ToList(),
            Lines = _sketch.Lines.Select(l => l.Sketch == from ? l with { Sketch = to } : l).ToList(),
        };
        if (_active == from) _active = to;
        CommitSketch(next, $"rename sketch {from} to {to}", $"sketch {from} is now {to}");
        return true;
    }

    /// <summary>Asks first and names what goes (the sketch with all its marks).</summary>
    internal bool RequestDeleteSketch(string name)
    {
        if (!_sketch.Names.Contains(name)) { Host.Say($"there is no sketch {name}", true); return false; }
        var n = _sketch.CountIn(name);
        Host.Confirm.Show($"Delete sketch {name}?", $"{name} and its {Summary(name)} are removed from the ground. Undo (Ctrl+Z) brings {(n == 1 ? "it" : "them")} back.", "Delete", () => DeleteSketch(name));
        return true;
    }

    void DeleteSketch(string name)
    {
        if (!_sketch.Names.Contains(name)) return;
        DraftEnd();
        var next = _sketch with
        {
            Names = _sketch.Names.Where(n => n != name).ToList(),
            Points = _sketch.Points.Where(p => p.Sketch != name).ToList(),
            Lines = _sketch.Lines.Where(l => l.Sketch != name).ToList(),
        };
        CommitSketch(next, $"delete sketch {name}", $"sketch {name} deleted (Ctrl+Z brings it back)");
    }

    // ---- keys and mouse ----

    /// <summary>Sketch tool keys. True when used.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (Host.Tool != EditorTool.Sketch || Host.CheatSheetShown) return false;
        if (k.CtrlPressed) return k.Keycode == Key.Z && !k.ShiftPressed && RemoveLastCorner();
        if (k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.P: SetMode(SketchKind.Point); return true;
            case Key.L: SetMode(SketchKind.Line); return true;
            case Key.C:
                // a mark at the cursor, or at the screen centre when the cursor is over the panel (keyboard-only drawing)
                var mouse = Host.GetViewport().GetMousePosition();
                var at = Host.OverUi(mouse) ? Host.GetViewport().GetVisibleRect().Size / 2 : mouse;
                if (RequireOrigin() && Host.RayToGround(Host.FlyCam.ProjectRayOrigin(at), Host.FlyCam.ProjectRayNormal(at), out var g))
                {
                    var (x, y) = Host.Map.SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
                    if (_mode == SketchKind.Point) AddPoint(x, y); else AddCorner(x, y);
                }
                return true;
            case Key.Enter or Key.KpEnter:
                if (_draft.Count > 0) FinishLine(k.ShiftPressed);
                else Host.Say(_mode == SketchKind.Line ? "click corners on the ground first; Enter then finishes the line" : "click the ground to mark a point");
                return true;
            case Key.Backspace: RemoveLastCorner(); return true;
            case Key.Delete when Selected != null && _draft.Count == 0: DeleteItem(Selected); return true;
            case Key.Bracketleft: CycleItem(-1); return true;
            case Key.Bracketright: CycleItem(1); return true;
            case Key.Escape: Escape(); return true;
        }
        return false;
    }

    /// <summary>Esc goes one level up: drawing, selection, tool.</summary>
    void Escape()
    {
        if (_panel.IsNaming) { _panel.CancelNaming(); return; }
        switch (Phase)
        {
            case SketchState.Drawing: CancelDraft(); break;
            case SketchState.Selected: SelectItem(null); break;
            default: Host.SelectTool(EditorTool.None); break;
        }
    }

    /// <summary>The mark whose point or corner is under a screen position; null when none is within reach.</summary>
    string? ItemAt(Vector2 screen)
    {
        var i = Host.PickCorner(screen, _pickPts);
        return i < 0 ? null : _pickIds[i];
    }

    public void LeftPress(Vector2 screen, bool free)
    {
        if (Host.Modal || !RequireOrigin()) return;
        Host.GetViewport().GuiReleaseFocus();
        if (_draft.Count > 0)
        {
            // the first corner closes the line, the last corner again finishes it open
            if (_draft.Count >= 3 && Host.PickCorner(screen, new[] { _draft[0] }) == 0) { FinishLine(true); return; }
            if (Host.PickCorner(screen, new[] { _draft[^1] }) == 0) { FinishLine(); return; }
        }
        else if (ItemAt(screen) is { } hit) { SelectItem(hit); return; }
        if (!Host.RayToGround(Host.FlyCam.ProjectRayOrigin(screen), Host.FlyCam.ProjectRayNormal(screen), out var g)) { Host.Say("click on the ground", true); return; }
        var (x, y) = Host.Map.SnapAbs(g, free);
        if (_mode == SketchKind.Point) AddPoint(x, y); else AddCorner(x, y);
    }

    // ---- per frame: cursor, hover, world marks, name tags ----

    /// <summary>The mark and the ground under the mouse (in this tool only), then the marks are drawn.</summary>
    void UpdateMarks()
    {
        var active = Host.Tool == EditorTool.Sketch;
        (double x, double y)? cursor = null;
        string? hover = null;
        var hoverDraft = -1;
        if (active && Host.Map.OriginEpsg5514 != null)
        {
            var mouse = Host.GetViewport().GetMousePosition();
            if (!(Host.OverUi(mouse) || Host.Looking || Host.Modal))
            {
                if (Host.RayToGround(Host.FlyCam.ProjectRayOrigin(mouse), Host.FlyCam.ProjectRayNormal(mouse), out var g)) cursor = Host.Map.SnapAbs(g, Input.IsKeyPressed(Key.Ctrl));
                if (_draft.Count == 0) hover = ItemAt(mouse);
                else if (_draft.Count >= 3 && Host.PickCorner(mouse, new[] { _draft[0] }) == 0) hoverDraft = 0;
                else if (Host.PickCorner(mouse, new[] { _draft[^1] }) == 0) hoverDraft = _draft.Count - 1;
            }
        }
        _marks.Show(new SketchView(_sketch, _version, Selected, hover, _draft, cursor, _mode), active, hoverDraft);
    }

    public string HintText() => Phase switch
    {
        SketchState.Drawing => $"line, {Plural(_draft.Count, "corner")}: LMB or C adds, Enter finishes, Shift+Enter or the first corner closes it, Backspace undoes a corner, Esc cancels",
        SketchState.Selected => $"{Selected}: Del deletes, [ ] next mark, click the ground to draw, Ctrl+Z undo, Esc deselects",
        _ => _mode == SketchKind.Point
            ? $"sketch {_active}, points: LMB or C marks a point, Ctrl free position, L draws lines, click a mark to select it, Esc leaves the tool"
            : $"sketch {_active}, lines: LMB or C places the first corner, Ctrl free position, P marks points, click a mark to select it, Esc leaves the tool",
    };
}
