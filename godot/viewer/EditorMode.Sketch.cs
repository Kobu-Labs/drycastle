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
/// Marks are grouped in named sketches (ROAD, FENCE_PLAN, ...); each sketch holds any number of points (A, B, C, ...) and lines (L1, L2, ...; a closed line
/// joins its last corner to its first and marks the ground inside). Mark names are unique across all sketches. New marks go to the active sketch.
/// It is never written to edits.json: the marks live in sketch.json next to editor-state.json, a file the MCP server reads and writes too
/// (sketch_add_*, get_sketch): the editor reloads it when its version changes, as one undo step, and writes it on every local change.
///
/// States (the hint bar names each): Idle, Drawing (a line's corners are being placed), Selected. Every change of the
/// sketch (a new mark, a delete, a clear) is one step on the editor's undo stack, in order with the saves.
/// Esc goes one level up: drawing, selection, tool. The marks stay visible in every tool; they are edited only in this one.
/// </summary>
public partial class EditorMode
{
    public enum SketchKind { Point, Line }
    public enum SketchState { Idle, Drawing, Selected }

    public sealed record SketchPoint(string Id, double X, double Y, string Sketch = DefaultSketch);

    public sealed record SketchLine(string Id, bool Closed, IReadOnlyList<(double x, double y)> Points, string Sketch = DefaultSketch)
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
    public sealed record SketchDoc(IReadOnlyList<SketchPoint> Points, IReadOnlyList<SketchLine> Lines, int NextPoint, int NextLine, IReadOnlyList<string> Names)
    {
        public static readonly SketchDoc Empty = new(Array.Empty<SketchPoint>(), Array.Empty<SketchLine>(), 0, 0, Array.Empty<string>());
        public int Count => Points.Count + Lines.Count;
        public int CountIn(string sketch) => Points.Count(p => p.Sketch == sketch) + Lines.Count(l => l.Sketch == sketch);
    }

    public const string DefaultSketch = "DEFAULT";
    /// <summary>A sketch name: 1 to 24 letters, digits, _ or - (the MCP server checks the same).</summary>
    public static bool ValidSketchName(string n) => n.Length is >= 1 and <= 24 && n.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    public const string OutlineSketch = "outline_sketch", OutlineSketchSel = "outline_sketch_sel",
        OutlineSketchDraft = "outline_sketch_draft", OutlineSketchGhost = "outline_sketch_ghost";
    
    SketchDoc _sketch = SketchDoc.Empty;
    SketchKind _skMode = SketchKind.Line;
    readonly List<(double x, double y)> _skDraft = new();
    string? _skSelected, _skHover;
    (double x, double y)? _skCursor;
    string _sketchJson = "";
    int _skVersion;
    string _skActive = DefaultSketch;
    int _skFileVersion;                  // the version of sketch.json this editor has written or read last
    long _skFileStamp = -1;              // its modification time then (ticks), -1 when there was no file
    double _skPollT;
    // every corner that can be clicked to select its mark, and the mark it belongs to
    readonly List<(double x, double y)> _skPickPts = new();
    readonly List<string> _skPickIds = new();

    public SketchDoc Sketch => _sketch;
    /// <summary>The sketch as the text of sketch.json (absolute S-JTSK metres).</summary>
    public string SketchJson => _sketchJson;
    /// <summary>Where new marks go.</summary>
    public string ActiveSketch => _skActive;
    string SketchPath => Watcher == null || Watcher.StatePath == "" ? "" : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Watcher.StatePath)) ?? ".", "sketch.json");
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

    /// <summary>sketch.json: format_version 1, a version that goes up on every write, the sketch names and the marks (see pipeline/src/drycastle_pipeline/sketchfile.py).</summary>
    public static string BuildSketchJson(SketchDoc d, int version = 0)
    {
        var sb = new StringBuilder("{\"format_version\":1,\"lines\":[");
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
            sb.Append("],\"sketch\":").Append(JsonSerializer.Serialize(l.Sketch)).Append('}');
        }
        sb.Append("],\"next_line\":").Append(d.NextLine).Append(",\"next_point\":").Append(d.NextPoint).Append(",\"points\":[");
        for (var i = 0; i < d.Points.Count; i++)
        {
            var p = d.Points[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(p.Id)).Append(",\"sketch\":").Append(JsonSerializer.Serialize(p.Sketch))
              .Append(",\"x\":").Append(Num(p.X)).Append(",\"y\":").Append(Num(p.Y)).Append('}');
        }
        sb.Append("],\"sketches\":[").Append(string.Join(",", d.Names.Select(n => JsonSerializer.Serialize(n)))).Append("],\"version\":").Append(version).Append('}');
        return sb.ToString();
    }

    /// <summary>The sketch of a sketch.json document (or of the `sketch` value an older editor-state.json held: its marks join DEFAULT).</summary>
    public static SketchDoc ParseSketch(JsonElement s, out int version)
    {
        version = s.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
        var points = new List<SketchPoint>();
        var lines = new List<SketchLine>();
        var names = new List<string>();
        string SketchOf(JsonElement m) => m.TryGetProperty("sketch", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } t ? t : DefaultSketch;
        if (s.TryGetProperty("sketches", out var ns) && ns.ValueKind == JsonValueKind.Array)
            foreach (var n in ns.EnumerateArray()) if (n.GetString() is { } t && ValidSketchName(t) && !names.Contains(t)) names.Add(t);
        if (s.TryGetProperty("points", out var ps))
            foreach (var p in ps.EnumerateArray())
                points.Add(new SketchPoint(p.GetProperty("id").GetString()!, p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), SketchOf(p)));
        if (s.TryGetProperty("lines", out var ls))
            foreach (var l in ls.EnumerateArray())
            {
                var pts = l.GetProperty("points").EnumerateArray().Select(q => (q[0].GetDouble(), q[1].GetDouble())).ToList();
                if (pts.Count >= 2) lines.Add(new SketchLine(l.GetProperty("id").GetString()!, l.GetProperty("closed").GetBoolean() && pts.Count >= 3, pts, SketchOf(l)));
            }
        foreach (var n in points.Select(p => p.Sketch).Concat(lines.Select(l => l.Sketch))) if (!names.Contains(n)) names.Add(n);
        var np = s.TryGetProperty("next_point", out var a) && a.TryGetInt32(out var ai) ? ai : 0;
        var nl = s.TryGetProperty("next_line", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
        return new SketchDoc(points, lines, Math.Max(np, points.Count), Math.Max(nl, lines.Count), names);
    }

    /// <summary>Bring back the sketch the last session left (sketch.json; before it existed, the sketch in editor-state.json) before anything is written.</summary>
    void LoadSketchState()
    {
        if (Watcher == null || Watcher.StatePath == "" || Map.OriginEpsg5514 == null) return;
        try
        {
            var path = SketchPath;
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var sketch = ParseSketch(doc.RootElement, out var version);
                _skFileVersion = version;
                _skFileStamp = File.GetLastWriteTimeUtc(path).Ticks;
                ApplySketch(sketch, write: false);
            }
            else if (File.Exists(Watcher.StatePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Watcher.StatePath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("sketch", out var old) && old.ValueKind == JsonValueKind.Object
                    && ParseSketch(old, out _) is { Count: > 0 } sketch) ApplySketch(sketch);
            }
        }
        catch (Exception e) { GD.PushWarning($"sketch not restored from {SketchPath}: {e.Message}"); }
    }

    /// <summary>Every half second: take over sketch.json when something else (the MCP server) wrote a newer version. One undo step.</summary>
    void PollSketchFile(double delta)
    {
        _skPollT += delta;
        if (_skPollT < 0.25) return;
        _skPollT = 0;
        var path = SketchPath;
        if (path == "" || Map.OriginEpsg5514 == null) return;
        try
        {
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;
            if (stamp == _skFileStamp) return;
            _skFileStamp = stamp;
            if (stamp < 0) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var next = ParseSketch(doc.RootElement, out var version);
            if (version <= _skFileVersion) return;
            _skFileVersion = version;
            if (BuildSketchJson(next) == BuildSketchJson(_sketch)) return;
            if (_skDraft.Count > 0) { _skDraft.Clear(); SketchChanged(); }
            PushUndo(new UndoRecord("sketch changed by the MCP server", new List<EntryChange>(), _sketch, next));
            ApplySketch(next, write: false);
            Say($"sketch updated from sketch.json: {SketchSummary()} (Ctrl+Z undoes it)");
        }
        catch (Exception e) { GD.PushWarning($"sketch.json not read: {e.Message}"); }
    }

    void WriteSketchFile(SketchDoc doc)
    {
        var path = SketchPath;
        if (path == "") return;
        try
        {
            var version = _skFileVersion;
            try
            {
                // never go below what is on disk, so a reader that compares versions sees this write as new
                if (File.Exists(path)) { using var cur = JsonDocument.Parse(File.ReadAllText(path)); version = Math.Max(version, ParseSketchVersion(cur.RootElement)); }
            }
            catch { /* unreadable: overwritten */ }
            _skFileVersion = version + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, BuildSketchJson(doc, _skFileVersion) + "\n");
            File.Move(tmp, path, true);
            _skFileStamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception e) { GD.PushWarning($"could not write {path}: {e.Message}"); }
    }

    static int ParseSketchVersion(JsonElement root) => root.TryGetProperty("version", out var v) && v.TryGetInt32(out var i) ? i : 0;

    string? SketchOfMark(string id) => _sketch.Points.FirstOrDefault(p => p.Id == id)?.Sketch ?? _sketch.Lines.FirstOrDefault(l => l.Id == id)?.Sketch;

    bool SketchHas(string id) => _sketch.Points.Any(p => p.Id == id) || _sketch.Lines.Any(l => l.Id == id);

    List<string> SketchIds() => _sketch.Points.Select(p => p.Id).Concat(_sketch.Lines.Select(l => l.Id)).ToList();

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>"2 points and 1 line" (of one sketch, or of all when null), for messages and the confirm.</summary>
    public string SketchSummary(string? sketch = null)
    {
        var pts = sketch == null ? _sketch.Points.Count : _sketch.Points.Count(p => p.Sketch == sketch);
        var lns = sketch == null ? _sketch.Lines.Count : _sketch.Lines.Count(l => l.Sketch == sketch);
        var parts = new List<string>();
        if (pts > 0) parts.Add(Plural(pts, "point"));
        if (lns > 0) parts.Add(Plural(lns, "line"));
        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    void SketchChanged() { _skVersion++; _panelDirty = true; }

    /// <summary>Make <paramref name="doc"/> the sketch: selection, pick list, the state file (written within a frame or two) and the views follow.</summary>
    void ApplySketch(SketchDoc doc, bool write = true)
    {
        if (doc.Count == 0) doc = doc with { NextPoint = 0, NextLine = 0 }; // nothing left: the names start again at A and L1
        _sketch = doc;
        if (_skSelected != null && !SketchHas(_skSelected)) _skSelected = null;
        if (!doc.Names.Contains(_skActive)) _skActive = doc.Names.Count > 0 ? doc.Names[0] : DefaultSketch;
        _skPickPts.Clear(); _skPickIds.Clear();
        foreach (var p in doc.Points) { _skPickPts.Add((p.X, p.Y)); _skPickIds.Add(p.Id); }
        foreach (var l in doc.Lines) foreach (var c in l.Points) { _skPickPts.Add(c); _skPickIds.Add(l.Id); }
        _sketchJson = BuildSketchJson(doc);
        if (write) WriteSketchFile(doc);
        SketchChanged();
    }

    /// <summary>The sketch with its active sketch's name listed (a first mark makes DEFAULT), for an edit that adds a mark to it.</summary>
    SketchDoc WithActive()
        => _sketch.Names.Contains(_skActive) ? _sketch : _sketch with { Names = _sketch.Names.Append(_skActive).ToList() };

    /// <summary>One sketch edit: one undo step (sketch.json is written, edits.json never).</summary>
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
        var p = new SketchPoint(id, Math.Round(x, 3), Math.Round(y, 3), _skActive);
        _skSelected = null;
        CommitSketch(WithActive() with { Points = _sketch.Points.Append(p).ToList(), NextPoint = _sketch.NextPoint + 1 }, $"sketch point {id}", message ?? $"point {id} marked in {_skActive}");
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
        var line = new SketchLine(id, closed, pts, _skActive);
        _skDraft.Clear();
        _skSelected = null;
        CommitSketch(WithActive() with { Lines = _sketch.Lines.Append(line).ToList(), NextLine = _sketch.NextLine + 1 }, $"sketch {(closed ? "closed line" : "line")} {id}",
            closed ? $"closed line {id} drawn in {_skActive}: {pts.Count} corners, {line.AreaM2:F0} m2" : $"line {id} drawn in {_skActive}: {pts.Count} corners, {line.LengthM:F1} m");
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
        if (id != null) _skActive = SketchOfMark(id) ?? _skActive;
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

    /// <summary>Remove every mark of the active sketch (it stays, empty): asks first and names what goes.</summary>
    public bool RequestClearSketch()
    {
        if (_sketch.CountIn(_skActive) == 0 && _skDraft.Count == 0) { Say($"sketch {_skActive} is already empty"); return false; }
        var n = _sketch.CountIn(_skActive);
        ShowConfirm($"Clear sketch {_skActive}?", $"{SketchSummary(_skActive)} {(n == 1 ? "is" : "are")} removed from the ground; the sketch itself stays. Undo (Ctrl+Z) brings {(n == 1 ? "it" : "them")} back.", "Clear", ClearSketch);
        return true;
    }

    public void ClearSketch()
    {
        _skDraft.Clear();
        var name = _skActive;
        if (_sketch.CountIn(name) == 0) { SketchChanged(); return; }
        CommitSketch(_sketch with { Points = _sketch.Points.Where(p => p.Sketch != name).ToList(), Lines = _sketch.Lines.Where(l => l.Sketch != name).ToList() },
            $"clear sketch {name}", $"sketch {name} cleared (Ctrl+Z brings it back)");
    }

    // ---- sketches: new, switch, rename, delete ----

    string? SketchNameProblem(string name, string? except = null)
    {
        if (!ValidSketchName(name)) return "a sketch name is 1 to 24 letters, digits, _ or -";
        if (_sketch.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) && n != except)) return $"there is a sketch {name} already";
        return null;
    }

    /// <summary>Make a new, empty sketch and draw into it from now on.</summary>
    public bool NewSketch(string name)
    {
        name = name.Trim();
        if (SketchNameProblem(name) is { } why) { Say(why, true); return false; }
        if (_skDraft.Count > 0) { Say("finish the line (Enter) or cancel it (Esc) first", true); return false; }
        var names = _sketch.Names.Append(name).ToList();
        _skActive = name; _skSelected = null;
        CommitSketch(_sketch with { Names = names }, $"new sketch {name}", $"sketch {name} made: the next marks go into it");
        return true;
    }

    public bool SetActiveSketch(string name)
    {
        if (!_sketch.Names.Contains(name)) { Say($"there is no sketch {name}", true); return false; }
        if (_skDraft.Count > 0) { Say("finish the line (Enter) or cancel it (Esc) first", true); SketchChanged(); return false; }
        if (name == _skActive) return true;
        _skActive = name;
        if (_skSelected != null && SketchOfMark(_skSelected) != name) _skSelected = null;
        Say($"drawing into sketch {name}");
        SketchChanged();
        return true;
    }

    public bool RenameSketch(string from, string to)
    {
        to = to.Trim();
        if (!_sketch.Names.Contains(from)) { Say($"there is no sketch {from}", true); return false; }
        if (to == from) return true;
        if (SketchNameProblem(to, from) is { } why) { Say(why, true); return false; }
        var next = _sketch with
        {
            Names = _sketch.Names.Select(n => n == from ? to : n).ToList(),
            Points = _sketch.Points.Select(p => p.Sketch == from ? p with { Sketch = to } : p).ToList(),
            Lines = _sketch.Lines.Select(l => l.Sketch == from ? l with { Sketch = to } : l).ToList(),
        };
        if (_skActive == from) _skActive = to;
        CommitSketch(next, $"rename sketch {from} to {to}", $"sketch {from} is now {to}");
        return true;
    }

    /// <summary>Asks first and names what goes (the sketch with all its marks).</summary>
    public bool RequestDeleteSketch(string name)
    {
        if (!_sketch.Names.Contains(name)) { Say($"there is no sketch {name}", true); return false; }
        var n = _sketch.CountIn(name);
        ShowConfirm($"Delete sketch {name}?", $"{name} and its {SketchSummary(name)} are removed from the ground. Undo (Ctrl+Z) brings {(n == 1 ? "it" : "them")} back.", "Delete", () => DeleteSketch(name));
        return true;
    }

    public void DeleteSketch(string name)
    {
        if (!_sketch.Names.Contains(name)) return;
        _skDraft.Clear();
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
        if (_skNaming != SketchNaming.None) { CancelSketchNaming(); return; }
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

    /// <summary>A mark's tag in the 3D view: its name, with the sketch in front when there are several ("ROAD · L1").</summary>
    string MarkTag(string sketch, string id) => _sketch.Names.Count > 1 ? $"{sketch} · {id}" : id;

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
                Tag((p.X, p.Y), MarkTag(p.Sketch, p.Id));
            }
            foreach (var l in _sketch.Lines)
            {
                (l.Id == _skSelected ? sel : plain).AddRange(RingSegs(l.Points, l.Closed));
                var look = l.Id == _skSelected || l.Id == _skHover ? HandleHot : HandlePlain;
                foreach (var c in l.Points) _skFixed.Add((GroundPoint(c), 0.7f, look));
                Tag(TagAnchor(l), MarkTag(l.Sketch, l.Id));
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
    Label _skNoOrigin = null!, _skStateLabel = null!, _skNote = null!, _skListHead = null!, _skEmpty = null!, _skNameNote = null!;
    VBoxContainer _skList = null!;
    string _skListSig = "", _skPickSig = "";
    OptionButton _skPick = null!;
    Button _skNewSketch = null!, _skRenameSketch = null!, _skDeleteSketch = null!;
    HBoxContainer _skNameRow = null!;
    LineEdit _skName = null!;
    enum SketchNaming { None, New, Rename }
    SketchNaming _skNaming;
    readonly Dictionary<string, Button> _skRows = new();

    void BuildSketchPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Sketch", ThemeTypeVariation = "Header" });
        page.AddChild(Note("Temporary points and lines on the ground that a prompt can name: \"plant trees inside L1\", \"a lamp at A\", \"everything in sketch ROAD\". They are not part of the map and are never saved to it; Claude can draw into them too."));

        var pickRow = new HBoxContainer();
        pickRow.AddChild(new Label { Text = "Sketch" });
        _skPick = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "The sketch new marks go into. Marks of every sketch stay visible." };
        _skPick.ItemSelected += i => { if (i >= 0 && i < _sketch.Names.Count) SetActiveSketch(_sketch.Names[(int)i]); _skPick.ReleaseFocus(); };
        pickRow.AddChild(_skPick);
        page.AddChild(pickRow);
        var sketchBtns = new HBoxContainer();
        _skNewSketch = FocusBtn("New", () => StartSketchNaming(SketchNaming.New));
        _skNewSketch.TooltipText = "Make a new, empty sketch and draw into it";
        _skRenameSketch = FocusBtn("Rename", () => StartSketchNaming(SketchNaming.Rename));
        _skRenameSketch.TooltipText = "Rename the active sketch (prompts name sketches, so keep it short: ROAD, FENCE_PLAN)";
        _skDeleteSketch = FocusBtn("Delete", () => RequestDeleteSketch(_skActive), "DangerButton");
        _skDeleteSketch.TooltipText = "Delete the active sketch with all its marks (asks first; Ctrl+Z brings it back)";
        foreach (var b in new[] { _skNewSketch, _skRenameSketch, _skDeleteSketch }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; sketchBtns.AddChild(b); }
        page.AddChild(sketchBtns);
        _skNameRow = new HBoxContainer { Visible = false };
        _skName = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "name, e.g. ROAD", MaxLength = 24, ClearButtonEnabled = true, TooltipText = "1 to 24 letters, digits, _ or -. Enter applies, Esc cancels" };
        _skName.TextChanged += t => RefreshSketchNaming(t);
        _skName.TextSubmitted += t => ApplySketchNaming(t);
        _skName.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { CancelSketchNaming(); _skName.AcceptEvent(); } };
        _skNameRow.AddChild(_skName);
        var nameOk = FocusBtn("OK", () => ApplySketchNaming(_skName.Text), "PrimaryButton");
        var nameCancel = FocusBtn("Cancel", CancelSketchNaming);
        _skNameRow.AddChild(nameOk); _skNameRow.AddChild(nameCancel);
        page.AddChild(_skNameRow);
        _skNameNote = Note("", "Dim");
        _skNameNote.Visible = false;
        page.AddChild(_skNameNote);
        page.AddChild(new HSeparator());
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
        _skClear.TooltipText = "Remove the marks of the active sketch (asks first; Ctrl+Z brings them back)";
        page.AddChild(_skClear);
        page.AddChild(Note("Marks stay visible in every tool. Esc steps back: cancel the line, deselect, leave the tool."));
    }

    void StartSketchNaming(SketchNaming kind)
    {
        if (_skDraft.Count > 0) { Say("finish the line (Enter) or cancel it (Esc) first", true); return; }
        if (kind == SketchNaming.Rename && !_sketch.Names.Contains(_skActive)) { Say("this sketch has no marks yet; mark something first", true); return; }
        _skNaming = kind;
        _skName.Text = kind == SketchNaming.Rename ? _skActive : "";
        _skNameRow.Visible = _skNameNote.Visible = true;
        RefreshSketchNaming(_skName.Text);
        _skName.GrabFocus();
        _skName.SelectAll();
        SketchChanged();
    }

    void RefreshSketchNaming(string text)
    {
        var t = text.Trim();
        var why = t == "" ? null : SketchNameProblem(t, _skNaming == SketchNaming.Rename ? _skActive : null);
        _skNameNote.Text = why ?? (_skNaming == SketchNaming.Rename ? $"Enter renames {_skActive}. Esc cancels." : "Enter makes the sketch and draws into it. Esc cancels.");
        _skNameNote.ThemeTypeVariation = why == null ? "Dim" : "Warn";
    }

    void ApplySketchNaming(string text)
    {
        var kind = _skNaming;
        if (kind == SketchNaming.None) return;
        var ok = kind == SketchNaming.New ? NewSketch(text) : RenameSketch(_skActive, text);
        if (ok) CancelSketchNaming(); else RefreshSketchNaming(text);
    }

    public void CancelSketchNaming()
    {
        if (_skNaming == SketchNaming.None) return;
        _skNaming = SketchNaming.None;
        _skName.ReleaseFocus();
        _skNameRow.Visible = _skNameNote.Visible = false;
        SketchChanged();
    }

    public string SketchHintText() => SketchPhase switch
    {
        SketchState.Drawing => $"line, {Plural(_skDraft.Count, "corner")}: LMB or C adds, Enter finishes, Shift+Enter or the first corner closes it, Backspace undoes a corner, Esc cancels",
        SketchState.Selected => $"{_skSelected}: Del deletes, [ ] next mark, click the ground to draw, Ctrl+Z undo, Esc deselects",
        _ => _skMode == SketchKind.Point
            ? $"sketch {_skActive}, points: LMB or C marks a point, Ctrl free position, L draws lines, click a mark to select it, Esc leaves the tool"
            : $"sketch {_skActive}, lines: LMB or C places the first corner, Ctrl free position, P marks points, click a mark to select it, Esc leaves the tool",
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

        var named = _sketch.Names.Contains(_skActive);
        var pickSig = string.Join("|", _sketch.Names.Select(n => $"{n}:{_sketch.CountIn(n)}")) + "#" + _skActive + "#" + hasOrigin;
        if (pickSig != _skPickSig)
        {
            _skPickSig = pickSig;
            _skPick.Clear();
            var idx = 0;
            foreach (var nm in named ? _sketch.Names : _sketch.Names.Append(_skActive).ToList()) // an unused active sketch (DEFAULT) is listed until its first mark
            {
                _skPick.AddItem($"{nm}  ({Plural(_sketch.CountIn(nm), "mark")})");
                if (nm == _skActive) _skPick.Select(idx);
                idx++;
            }
        }
        _skPick.Disabled = _skNewSketch.Disabled = !hasOrigin;
        _skRenameSketch.Disabled = _skDeleteSketch.Disabled = !hasOrigin || !named;
        _skDeleteSketch.Text = named ? $"Delete {_skActive}" : "Delete";
        _skListHead.Text = $"Marks ({_sketch.Count})";
        _skEmpty.Visible = _sketch.Count == 0;
        var inActive = _sketch.CountIn(_skActive);
        _skClear.Visible = inActive > 0;
        _skClear.TooltipText = "Remove every mark of the active sketch (asks first; Ctrl+Z brings them back)";
        _skClear.Text = _sketch.Names.Count > 1 ? $"Clear sketch {_skActive}" : "Clear the sketch";
        var sig = _sketchJson + "#" + _skSelected + "#" + _skActive;
        if (sig == _skListSig) return;
        _skListSig = sig;
        foreach (var c in _skList.GetChildren()) { _skList.RemoveChild(c); c.QueueFree(); }
        _skRows.Clear();
        void Row(string id, string text, string tip)
        {
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip };
            b.SetPressedNoSignal(id == _skSelected);
            b.Pressed += () => { if (_skDraft.Count > 0) CancelSketchDraft(); SelectSketchItem(id); b.ReleaseFocus(); };
            _skList.AddChild(b);
            _skRows[id] = b;
        }
        foreach (var name in _sketch.Names)
        {
            if (_sketch.Names.Count > 1) _skList.AddChild(new Label { Text = $"{name}{(name == _skActive ? "  (drawing here)" : "")}", ThemeTypeVariation = "Header" });
            foreach (var p in _sketch.Points.Where(p => p.Sketch == name)) Row(p.Id, $"{p.Id}   point", $"Select point {p.Id} in {name} (x {p.X:F1}, y {p.Y:F1})");
            foreach (var l in _sketch.Lines.Where(l => l.Sketch == name)) Row(l.Id, SketchRowText(l), $"Select {(l.Closed ? "closed line" : "line")} {l.Id} in {name}");
        }
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
