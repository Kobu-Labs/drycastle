using Drycastle.ShooterCore.Editor;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The live part of the Photo to building tool: a change of the form is a request to the building worker (which edits the files atomically, with minimal diffs, see pipeline
/// edits/building_edit.py); requests go one at a time in order, and changes of the same field that arrive close together (a nudge held down) are merged into one request and
/// one undo step. The answer carries the new form and the files' text before and after, which is what undo and redo write back (<c>restore</c>).
/// </summary>
public sealed partial class PhotoTool
{
    const double NudgeDelay = 0.12, MergeUndoSeconds = 1.5;

    sealed class Queued
    {
        public string Op = "edit", Key = "", Label = "", Building = "";
        public Dictionary<string, object?> Fields = new();
        public double Due;
        public Action<JsonElement?, string?>? Done;
    }

    sealed class UndoStep
    {
        public string Key = "", Building = "";
        public JsonElement Before, After;
        public double At;
        public UndoRecord? Record;
    }

    readonly List<Queued> _queue = new();
    bool _inflight;
    double _clock;
    UndoStep? _lastStep;

    /// <summary>Changes are waiting for the worker or on the wire: the form must not be overwritten by an older answer meanwhile.</summary>
    public bool Applying => _inflight || _queue.Count > 0;

    void PumpQueue(double delta)
    {
        _clock += delta;
        if (_inflight || _queue.Count == 0 || _queue[0].Due > _clock) return;
        var q = _queue[0];
        _queue.RemoveAt(0);
        var w = Host.Watcher;
        if (w == null) { Host.Say("no building worker: start the viewer with --watch", true); return; }
        _inflight = true;
        q.Fields["building"] = q.Building;
        w.BuildingOp(q.Op, q.Fields, (res, err) =>
        {
            _inflight = false;
            if (q.Done != null) q.Done(res, err);
            else if (err != null) { Host.Say($"{q.Label}: {err}", true); if (_queue.Count == 0) LoadForm(); }
            else Applied(q, res!.Value);
            Bump();
        });
    }

    void Applied(Queued q, JsonElement res)
    {
        if (q.Building == Current && res.TryGetProperty("state", out var st))
        {
            Form = BuildingForm.Parse(st);
            FormError = null;
            if (res.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.String) SelectedOpening = sel.GetString();
            if (SelectedOpening != null && Form.Detail?.Openings.All(o => o.Id != SelectedOpening) != false) SelectedOpening = null;
        }
        if (q.Op != "edit" && q.Op != "reset") return;
        if (!res.TryGetProperty("before", out var before) || !res.TryGetProperty("after", out var after)) return;
        var now = Time.GetTicksMsec() / 1000.0;
        // a held arrow key or a spin box dragged: one step, from the first change's before to the last one's after
        if (_lastStep is { } last && last.Key == q.Key && last.Building == q.Building && now - last.At < MergeUndoSeconds && ReferenceEquals(Host.Edits.History.Peek(true), last.Record))
        {
            last.After = after; last.At = now;
            Host.Say($"{q.Label} (Ctrl+Z undoes)");
            return;
        }
        var step = new UndoStep { Key = q.Key, Building = q.Building, Before = before, After = after, At = now };
        step.Record = new UndoRecord($"{q.Label} ({q.Building})", new List<EntryChange>(), Replay: undo => Restore(step.Building, undo ? step.Before : step.After));
        Host.Edits.History.Push(step.Record);
        _lastStep = step;
        Host.Say($"{q.Label} (Ctrl+Z undoes)");
    }

    /// <summary>An undo or redo step of a building's files: write the recorded text back. False (and a message) when the worker is busy with an earlier change.</summary>
    bool Restore(string building, JsonElement snapshot)
    {
        if (Host.Watcher == null) { Host.Say("no building worker: cannot undo", true); return false; }
        if (Applying) { Host.Say("still applying the last change: try again in a moment", true); return false; }
        _queue.Add(new Queued
        {
            Op = "restore", Building = building, Key = "restore", Label = "restored", Due = _clock,
            Fields = new() { ["snapshot"] = snapshot },
            Done = (res, err) =>
            {
                if (err != null) Host.Say("could not restore the building's files: " + err, true);
                else if (building == Current && res!.Value.TryGetProperty("state", out var st))
                {
                    Form = BuildingForm.Parse(st);
                    if (SelectedOpening != null && Form.Detail?.Openings.All(o => o.Id != SelectedOpening) != false) SelectedOpening = null;
                }
            },
        });
        return true;
    }

    /// <summary>Queue a change of the form. <paramref name="key"/> names the field: a later change of the same key still waiting is replaced, and one right after merges into the same undo step.</summary>
    void Edit(string key, string label, Dictionary<string, object?> change, bool nudge)
    {
        if (Current == null || Form == null) return;
        var existing = _queue.FirstOrDefault(x => x.Op == "edit" && x.Key == key);
        if (existing != null)
        {
            existing.Fields["change"] = change; existing.Label = label; existing.Due = _clock + NudgeDelay;
            return;
        }
        _queue.Add(new Queued { Key = key, Label = label, Building = Current, Fields = new() { ["change"] = change }, Due = _clock + (nudge ? NudgeDelay : 0) });
    }

    static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    public void SetForm(string field, double value, string label)
        => Edit("form." + field, $"{label} {Num(value)} m", new() { ["form"] = new Dictionary<string, object?> { [field] = value } }, true);

    public void SetRoof(string roof)
        => Edit("form.roof", $"roof: {roof}", new() { ["form"] = new Dictionary<string, object?> { ["roof"] = roof } }, false);

    public void SetOverride(string field, string? value, string label)
        => Edit("override." + field, label, new() { ["override"] = new Dictionary<string, object?> { [field] = value } }, false);

    public void SetDetail(string field, object? value, string label, bool nudge = false)
        => Edit("detail." + field, label, new() { ["detail"] = new Dictionary<string, object?> { [field] = value } }, nudge);

    public void SetOpening(string id, string field, object value, string label, bool nudge)
        => Edit($"opening.{id}.{field}", $"{id} {label}", new() { ["opening"] = new Dictionary<string, object?> { ["id"] = id, ["set"] = new Dictionary<string, object?> { [field] = value } } }, nudge);

    public void SetOpeningWall(string id, int edge)
        => Edit($"opening.{id}.edge", $"{id} to wall {edge}", new() { ["opening"] = new Dictionary<string, object?> { ["id"] = id, ["set"] = new Dictionary<string, object?> { ["edge"] = edge } } }, false);

    public void AddOpening(string kind)
    {
        var add = new Dictionary<string, object?> { ["kind"] = kind };
        if (SelectedEdge() is { } edge) add["edge"] = edge; // beside the selected opening; else the worker picks the longest wall
        Edit("opening.add." + _clock, $"add {kind.Replace('_', ' ')}", new() { ["opening"] = new Dictionary<string, object?> { ["add"] = add } }, false);
    }

    public void DeleteOpening(string id)
        => Edit("opening.delete." + id, $"delete {id}", new() { ["opening"] = new Dictionary<string, object?> { ["delete"] = id } }, false);

    public void DuplicateOpening(string id)
        => Edit("opening.dup." + _clock, $"duplicate {id}", new() { ["opening"] = new Dictionary<string, object?> { ["duplicate"] = id } }, false);

    int? SelectedEdge() => Form?.Detail?.Openings.FirstOrDefault(o => o.Id == SelectedOpening)?.Edge;

    /// <summary>Back to what the last build measured (<c>measured.json</c> beside the photos): one undo step.</summary>
    public void ResetToMeasured()
    {
        if (Current == null) return;
        _queue.Add(new Queued { Op = "reset", Key = "reset", Label = "reset to measured", Building = Current, Due = _clock });
    }

    // ---- selection of an opening, in the panel and in 3D ----

    public void SelectOpening(string? id)
    {
        if (id == SelectedOpening) return;
        SelectedOpening = id;
        Bump();
    }

    /// <summary>The opening under a click in the 3D view (a ray against the wall planes of the building), selected; false when none is hit. The click then does nothing else.</summary>
    public bool PickOpening(Vector2 screen)
    {
        if (Form?.Detail is not { } d || d.Openings.Count == 0 || Host.Tool != EditorTool.Photo) return false;
        var cam = Host.FlyCam;
        var o = cam.ProjectRayOrigin(screen); var n = cam.ProjectRayNormal(screen);
        // Godot x, y, z to the map's x east, y north, z up
        var hit = BuildingGeom.Pick(Form.Edges, d.Openings.Select(r => r.Spec).ToList(), Form.BaseZ, (o.X, -o.Z, o.Y), (n.X, -n.Z, n.Y));
        if (hit == null) return false;
        SelectOpening(hit);
        Host.Say($"selected {hit}");
        return true;
    }

    /// <summary>Keys of the tool while an opening is selected (no text field has the focus when this is reached). True when used.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (Host.Tool != EditorTool.Photo || Form?.Detail is not { } d) return false;
        var sel = SelectedOpening == null ? null : d.Openings.FirstOrDefault(o => o.Id == SelectedOpening);
        if (k.CtrlPressed || k.AltPressed) { if (k.CtrlPressed && k.Keycode == Key.D && sel != null) { DuplicateOpening(sel.Id); return true; } return false; }
        switch (k.Keycode)
        {
            case Key.Bracketright or Key.Bracketleft when d.Openings.Count > 0:
            {
                var i = sel == null ? -1 : d.Openings.ToList().FindIndex(o => o.Id == sel.Id);
                var step = k.Keycode == Key.Bracketright ? 1 : -1;
                SelectOpening(d.Openings[((i + step) % d.Openings.Count + d.Openings.Count) % d.Openings.Count].Id);
                return true;
            }
            case Key.Delete or Key.Backspace when sel != null: DeleteOpening(sel.Id); return true;
            case Key.Left or Key.Right when sel != null:
            {
                var s = Nudge.Step(0.05, k.ShiftPressed) * (k.Keycode == Key.Right ? 1 : -1);
                var edge = Form.Edges.FirstOrDefault(e => e.Index == sel.Edge);
                var at = Math.Round(Math.Clamp(sel.At + s, 0, Math.Max(0, edge.Length - sel.Width)), 2);
                SetOpening(sel.Id, "at_m", at, $"along {Num(at)} m", true);
                Optimistic(sel with { At = at });
                return true;
            }
            case Key.Up or Key.Down when sel != null:
            {
                var s = Nudge.Step(0.05, k.ShiftPressed) * (k.Keycode == Key.Up ? 1 : -1);
                var sill = Math.Round(Math.Max(0, sel.Sill + s), 2);
                SetOpening(sel.Id, "sill_m", sill, $"sill {Num(sill)} m", true);
                Optimistic(sel with { Sill = sill });
                return true;
            }
        }
        return false;
    }

    /// <summary>Show a nudge in 3D at once (the marker), before the worker has rewritten the file and the house is rebuilt.</summary>
    void Optimistic(OpeningRow row)
    {
        if (Form?.Detail is not { } d) return;
        Form.Detail = d with { Openings = d.Openings.Select(o => o.Id == row.Id ? row : o).ToList() };
        Bump();
    }
}
