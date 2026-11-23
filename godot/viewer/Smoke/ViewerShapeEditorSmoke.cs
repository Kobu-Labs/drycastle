using Drycastle.Viewer.Editor;
using Drycastle.Viewer.Editor.Tools;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer;

/// <summary>
/// `--shape-editor-smoke --shape-editor-smoke-plan plan.json` (run by shape_editor_smoke.py, `make viewer-shape-editor-smoke`):
/// drives the two polygon tools of the editor headless through their API and key events: ground PATCHES in the Ground tool (select a
/// type, draw, create with Enter, retype, corner drag valid and self-crossing, a water patch that asks for its water level, delete with
/// confirm) and ZONES in the Zones tool (select a kind, draw, create, change kind, delete), undo/redo of every step, Esc one level at a
/// time, the palette. After every save the exact text of edits.json is compared with the text the Python writer produces for the same
/// data (the plan), and a saved patch reaches the map through the watcher (patch-run). Zones have no paint, so no run is awaited for them.
/// Steps are copied to the plan's steps folder. Needs a map whose parcels.json has `zone_kinds` (the ADR 0022 export).
/// </summary>
public partial class Viewer
{
    async Task RunShapeEditorSmoke()
    {
        try { await ShapeEditorSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL shape editor smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "SHAPE EDITOR SMOKE PASS" : "SHAPE EDITOR SMOKE FAIL");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    async Task ShapeEditorSmokeBody()
    {
        var ed = _editor!;
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["shape-editor-smoke-plan"]));
        var plan = planDoc.RootElement;
        var editsPath = ed.EditsPath;
        var stepsDir = plan.GetProperty("steps_dir").GetString()!;
        Directory.CreateDirectory(stepsDir);
        string Expect(string k) => File.ReadAllText(plan.GetProperty("expect").GetProperty(k).GetString()!);
        double[] Pt(string k) => plan.GetProperty(k).EnumerateArray().Select(x => x.GetDouble()).ToArray();
        string Disk() => File.ReadAllText(editsPath);
        async Task Frame(int n = 2) { for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Key_(Key k, bool ctrl = false, bool shift = false)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = k, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift });
            await Frame();
        }
        void FileIs(string step, string what)
        {
            var now = Disk(); var want = Expect(step);
            Check(now == want, $"{what}: edits.json is exactly the expected text" + (now == want ? "" : $"\n--- got\n{now}--- want\n{want}"));
            File.Copy(editsPath, Path.Combine(stepsDir, step + ".json"), true);
        }
        async Task Settle(bool patchRun)
        {
            if (patchRun) await WaitUntil(() => !_watcher!.Busy, 90);
            await Physics(3);
            ed.RefreshNow();
        }
        // one gesture = exactly one save and one undo step; a patch change also waits for the patch-run, a zone change has none
        async Task<bool> Gesture(string name, Func<bool> act, string step, bool patchRun, Func<Task>? driver = null)
        {
            var s0 = ed.Edits.SaveCount; var u0 = ed.Edits.History.UndoDepth; var a0 = _watcher!.AppliedCount;
            if (driver != null) await driver(); // key events arrive a frame later
            var ok = act();
            Check(ok && ed.Edits.SaveCount == s0 + 1 && ed.Edits.History.UndoDepth == u0 + 1, $"[{name}] one save, one undo step (saves {ed.Edits.SaveCount - s0}, undo depth {ed.Edits.History.UndoDepth - u0}), status '{ed.StatusMessage}'");
            FileIs(step, name);
            if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
            await Settle(patchRun);
            return ok;
        }
        async Task<bool> Refused(string name, Func<bool> act, string step)
        {
            var s0 = ed.Edits.SaveCount; var u0 = ed.Edits.History.UndoDepth;
            var before = Disk();
            var ok = act();
            Check(!ok && ed.Edits.SaveCount == s0 && ed.Edits.History.UndoDepth == u0 && Disk() == before && Disk() == Expect(step), $"[{name}] refused: nothing saved, file unchanged, status '{ed.StatusMessage}'");
            return !ok;
        }

        Check(Disk() == Expect("seed"), "scratch edits file is the planned seed");
        Check(ed.Shapes.NextShapeIdOf(Disk(), ShapeKind.Patch) == "GP-0002", "next free patch ID after GP-0001 is GP-0002");
        Check(ed.Shapes.NextShapeIdOf(Disk(), ShapeKind.Zone) == "Z-0001", "next free zone ID with no zones is Z-0001");
        await Physics(3);
        ed.SetActive(true);
        ed.SelectTool(EditorTool.Ground);
        await Frame();
        ed.RefreshNow();

        // ---- ground tool: idle, list, empty selection ----
        Check(ed.Tool == EditorTool.Ground && ed.Shapes.Mode == ShapeState.Idle, "Ground tool opens with no gesture under way");
        Check(ed.Shapes.Panel.RowCount == 1 && !ed.Shapes.Panel.EmptyShown && !ed.Shapes.Panel.RowSelected("GP-0001"), "panel lists the seeded GP-0001");
        Check(ed.ShapeHint.Contains("draws a patch") && ed.Shapes.Panel.State == "No patch selected", $"idle hint and state: '{ed.ShapeHint}'");
        await Key_(Key.N);
        Check(ed.Shapes.Mode == ShapeState.Idle && ed.StatusMessage.Contains("pick a ground type"), $"N without a selected type is refused with the way out: '{ed.StatusMessage}'");
        Check(ed.SelectGroundType("paint_336699"), "a type is selected in the list");

        // ---- drawing: refusals before any save ----
        var q = new[] { Pt("q0"), Pt("q1"), Pt("q2"), Pt("q3") };
        await Key_(Key.N);
        Check(ed.Shapes.Mode == ShapeState.Drawing && ed.ShapeHint.Contains("new patch"), "N starts drawing (hint says so)");
        Check(ed.Shapes.AddShapePoint(q[0][0], q[0][1]) && ed.Shapes.AddShapePoint(q[1][0], q[1][1]), "two corners placed");
        ed.RefreshNow();
        Check(ed.Shapes.Panel.Validation.Contains("at least 3") && ed.Shapes.ShapeDraftProblem() != null, $"inline validation before commit: '{ed.Shapes.Panel.Validation}'");
        await Refused("close with 2 corners", () => ed.Shapes.CloseShape(), "seed");
        Check(!ed.Shapes.AddShapePoint(q[1][0], q[1][1]), "the same corner twice is refused");
        ed.Shapes.RemoveLastShapePoint(); ed.Shapes.RemoveLastShapePoint();
        var bow = Pt("bow");
        // bow tie: the 4th corner's edge would cross the first edge
        Check(ed.Shapes.AddShapePoint(bow[0], bow[1]) && ed.Shapes.AddShapePoint(bow[0] + 10, bow[1] + 10) && ed.Shapes.AddShapePoint(bow[0] + 10, bow[1]), "bow tie: 3 corners placed");
        Check(!ed.Shapes.AddShapePoint(bow[0], bow[1] + 10) && ed.Shapes.ShapeDraft.Count == 3, $"self-crossing corner refused: '{ed.StatusMessage}'");
        ed.Shapes.ShapeEscape(); // Esc cancels drawing
        Check(ed.Shapes.Mode == ShapeState.Idle && ed.Shapes.ShapeDraft.Count == 0 && ed.StatusMessage.Contains("nothing was saved"), "Esc cancels drawing, nothing saved");
        // collinear: closes to a shape with no area
        await Key_(Key.N);
        foreach (var dx in new[] { 0.0, 5.0, 10.0 }) ed.Shapes.AddShapePoint(bow[0] + dx, bow[1]);
        await Refused("close a flat (no area) shape", () => ed.Shapes.CloseShape(), "seed");
        Check(ed.Shapes.ShapeDraftProblem()!.Contains("crosses or touches itself, or has no area"), $"validation names the problem: '{ed.Shapes.ShapeDraftProblem()}'");
        ed.Shapes.ShapeEscape();
        Check(ed.Shapes.Mode == ShapeState.Idle, "Esc cancelled the flat shape");

        // ---- draw the quad and create it with Enter (a land type: no extra step) ----
        await Key_(Key.N);
        foreach (var p in q) ed.Shapes.AddShapePoint(p[0], p[1]);
        Check(ed.Shapes.ShapeDraft.Count == 4 && ed.Shapes.ShapeDraftProblem() == null, "quad placed and valid");
        ed.RefreshNow();
        Check(ed.Shapes.Panel.Validation.StartsWith("Shape is valid") && ed.Shapes.Panel.Validation.Contains("Not saved"), $"panel says valid and unsaved: '{ed.Shapes.Panel.Validation}'");
        Check(ed.Shapes.ShapeDraftPending, "unsaved marker while drawing");
        ed.Shapes.World.Redraw();
        Check(ed.SegmentCount(ShapeWorld.OutlineDraft) == 3 && ed.Shapes.World.HandleCount == 4, $"live preview: {ed.SegmentCount(ShapeWorld.OutlineDraft)} edges, {ed.Shapes.World.HandleCount} corner handles");
        Check(Disk() == Expect("seed"), "nothing saved while drawing");
        var saves0 = ed.Edits.SaveCount;
        await Gesture("create GP-0002 (Enter)", () => ed.Shapes.Mode == ShapeState.Selected, "p_created", true, () => Key_(Key.Enter));
        Check(ed.Shapes.SelectedPatchId == "GP-0002" && ed.SelectedGroundType == "paint_336699" && ed.Shapes.ShapeDraft.Count == 0, "new patch is selected (and its type), draft cleared");
        Check(ed.Edits.SaveCount == saves0 + 1, "creating saved exactly once");
        Check(_map.DynamicPatches.ContainsKey("GP-0002"), "GP-0002 reached the map through the watcher (patch-run)");
        Check(ed.Shapes.Panel.RowCount == 2 && ed.Shapes.Panel.RowSelected("GP-0002"), "list shows both patches, GP-0002 selected");
        Check(ed.PatchTypeShown == "paint_336699", "the form shows the patch's type");

        // ---- retype (one gesture) ----
        await Gesture("retype GP-0002", () => ed.Shapes.SetPatchType("GP-0002", "paint_99cc33"), "p_retyped", true);
        Check(DynamicColour("GP-0002") is { } dc && SameColour(dc, "#99cc33"), "retyped patch is regenerated in the new type's colour");
        Check(ed.Shapes.SetPatchType("GP-0002", "paint_99cc33") && ed.Edits.History.UndoDepth == 2, "the same type again saves nothing");
        Check(!ed.Shapes.SetPatchType("GP-0002", "no_such_type") && ed.Edits.History.UndoDepth == 2, "an unknown type is refused");

        // ---- corner drag: preview, refused crossing, valid move ----
        var inv = Pt("invalid_to"); var mv = Pt("move_to");
        Check(ed.Shapes.StartCornerDrag(2) && ed.Shapes.Mode == ShapeState.Dragging && ed.ShapeHint.Contains("moving a corner"), "drag starts on corner 3");
        ed.Shapes.DragCornerToAbs(inv[0], inv[1]);
        ed.Shapes.World.Redraw();
        Check(ed.SegmentCount(ShapeWorld.OutlineBad) > 0 && ed.SegmentCount(ShapeWorld.OutlineDrag) == 0, "crossing drag previews in the refused colour");
        Check(Disk() == Expect("p_retyped"), "file untouched during the drag");
        await Refused("drag corner into a crossing", () => ed.Shapes.EndCornerDrag(), "p_retyped");
        ed.Shapes.StartCornerDrag(1);
        ed.Shapes.DragCornerToAbs(mv[0], mv[1]);
        ed.Shapes.World.Redraw();
        Check(ed.SegmentCount(ShapeWorld.OutlineDrag) == 4 && ed.SegmentCount(ShapeWorld.OutlineBad) == 0, "valid drag previews live");
        await Key_(Key.Escape);
        Check(ed.Shapes.Mode == ShapeState.Selected && Disk() == Expect("p_retyped"), "Esc cancels the drag, nothing saved");
        ed.Shapes.StartCornerDrag(1);
        ed.Shapes.DragCornerToAbs(mv[0], mv[1]);
        await Gesture("drag corner 2", () => ed.Shapes.EndCornerDrag(), "p_moved", true);

        // ---- a patch of a water type asks for its water level before it is created ----
        var wq = new[] { Pt("w0"), Pt("w1"), Pt("w2"), Pt("w3") };
        Check(ed.SelectGroundType("pond"), "the water type is selected");
        await Key_(Key.N);
        foreach (var p in wq) ed.Shapes.AddShapePoint(p[0], p[1]);
        await Key_(Key.Enter);
        Check(ed.Shapes.Mode == ShapeState.Level && ed.PatchWaterRowShown && ed.Shapes.Panel.CreateButtonShown, "Enter on a water patch asks for the water level: nothing is created yet");
        Check(Disk() == Expect("p_moved"), "nothing saved while the water level is asked for");
        ed.Shapes.World.Redraw();
        Check(ed.SegmentCount(ShapeWorld.OutlineDraft) == 4, "closing edge is part of the preview");
        await Key_(Key.Escape);
        Check(ed.Shapes.Mode == ShapeState.Drawing && ed.Shapes.ShapeDraft.Count == 4, "Esc in the water level step goes back to the corners");
        await Key_(Key.Enter);
        ed.Shapes.SetDrawWaterLevel(1.5);
        await Gesture("create the water patch GP-0003 (Enter)", () => ed.Shapes.Mode == ShapeState.Selected, "p_water", true, () => Key_(Key.Enter));
        Check(ed.Shapes.SelectedPatchId == "GP-0003" && ed.Shapes.Patch("GP-0003")!.WaterY == 1.5, "the water patch carries its water level");
        Check(!ed.Shapes.SetPatchWater("GP-0002", 2.0), "a land patch has no water level");
        Check(!ed.SetGroundCarried("pond", "water", false) && ed.Edits.History.UndoDepth == 4, $"unticking water on a type a patch uses is refused: '{ed.StatusMessage}'");
        ed.RequestDeleteGroundType();
        Check(!ed.Confirm.Visible && ed.StatusMessage.Contains("patch"), $"a type a patch uses is not offered for deletion (blocked with the reason, no confirm): '{ed.StatusMessage}'");

        // ---- delete: confirm names the patch; cancel keeps it ----
        await Key_(Key.Delete);
        Check(ed.Confirm.Visible && ed.Confirm.Text.Contains("GP-0003"), $"Del asks first and names the patch: '{ed.Confirm.Text.Replace('\n', '|')}'");
        await Key_(Key.Escape);
        Check(!ed.Confirm.Visible && ed.Shapes.Patch("GP-0003") != null && Disk() == Expect("p_water"), "Esc on the confirm cancels: patch kept");
        await Gesture("delete GP-0003", () => { ed.Confirm.Accept(); return !ed.Confirm.Visible && ed.Shapes.Patch("GP-0003") == null; }, "p_moved", true, () => Key_(Key.Delete));
        Check(ed.Shapes.SelectedPatchId == null && ed.Shapes.Panel.RowCount == 2, "deleted patch leaves the selection and the list");

        // ---- zones tool ----
        ed.SelectTool(EditorTool.Zones);
        await Frame();
        ed.RefreshNow();
        ed.SelectZoneKind(null); ed.Shapes.SelectShape(ShapeKind.Zone, null); ed.RefreshNow(); // the saved editor_ui.cfg of this machine may have restored a kind
        Check(ed.Tool == EditorTool.Zones && !ed.ZoneErrorShown && ed.ZoneKindRowCount > 0, $"Zones tool lists the map's zone kinds ({ed.ZoneKindRowCount}); the map has zone_kinds");
        Check(ed.Zones().All(z => z.FromRule) && ed.Shapes.Panel.State == "No zone selected", "nothing drawn yet: every zone on the map comes from a rule, none is selected");
        var deerRow = ed.ZoneKindRowText("deer");
        Check(!deerRow.Contains("drawn") && !deerRow.Contains("rule"), $"a kind row has one number, no drawn/rule wording: '{deerRow}'");
        Check(ed.ShapeHint.Contains("zones:"), $"zones idle hint: '{ed.ShapeHint}'");
        await Key_(Key.N);
        Check(ed.Shapes.Mode == ShapeState.Idle && ed.StatusMessage.Contains("pick a zone kind"), $"N without a kind is refused with the way out: '{ed.StatusMessage}'");
        ed.PressZoneKindRow("bigfoot"); ed.RefreshNow();
        Check(ed.SelectedZoneKind == "bigfoot", "clicking a kind row selects it");
        var zq = new[] { Pt("z0"), Pt("z1"), Pt("z2"), Pt("z3") };
        await Key_(Key.N);
        foreach (var p in zq) ed.Shapes.AddShapePoint(p[0], p[1]);
        Check(ed.ShapeHint.Contains("Enter creates"), $"a zone is created by Enter (no extra step): '{ed.ShapeHint}'");
        await Gesture("create Z-0001 (Enter)", () => ed.Shapes.SelectedZoneId == "Z-0001", "z_created", false, () => Key_(Key.Enter));
        Check(ed.Shapes.Panel.RowCount == 1 && ed.Shapes.Panel.RowSelected("Z-0001") && ed.ZoneKindShown == "bigfoot", "the zone is listed, selected, and its form shows its kind");
        await Gesture("change Z-0001 to deer", () => ed.SetZoneKind("Z-0001", "deer"), "z_rekinded", false);
        Check(ed.SelectedZoneKind == "deer", "the kind selection follows the zone");
        Check(!ed.SetZoneKind("Z-0001", "no_such_kind") && ed.Edits.History.UndoDepth == 7, "an unknown kind is refused");
        Check(ed.Shapes.ShapeAt(ShapeKind.Zone, plan.GetProperty("inside_z")[0].GetDouble(), plan.GetProperty("inside_z")[1].GetDouble()) == "Z-0001", "hit test finds Z-0001");
        ed.ZoneOverlayNow();
        Check(ed.ZoneOverlayShown, "the overlay is on by default");
        await Key_(Key.M);
        Check(!ed.ZoneOverlayShown, "M hides the zone overlay");
        await Key_(Key.M);
        Check(ed.ZoneOverlayShown, "M shows it again");
        await Key_(Key.Delete);
        Check(ed.Confirm.Visible && ed.Confirm.Text.Contains("Z-0001"), $"Del asks first and names the zone: '{ed.Confirm.Text.Replace('\n', '|')}'");
        await Gesture("delete Z-0001", () => { ed.Confirm.Accept(); return ed.Zone("Z-0001") == null; }, "p_moved", false);
        Check(ed.Shapes.SelectedZoneId == null && ed.Zone("Z-0001") == null, "deleted zone leaves the selection (and the zones key is gone, as dump_text)");

        // ---- undo / redo of every step, byte for byte ----
        var chain = new[] { ("z_rekinded", false), ("z_created", false), ("p_moved", false), ("p_water", true), ("p_moved", true), ("p_retyped", true), ("p_created", true), ("seed", true) };
        Check(ed.Edits.History.UndoDepth == 8 && ed.Edits.History.NextUndoDescription!.Contains("deleted Z-0001"), $"undo stack: {ed.Edits.History.UndoDepth} steps, next '{ed.Edits.History.NextUndoDescription}'");
        async Task Walk(string what, bool undo, (string state, bool patchRun)[] states)
        {
            foreach (var (st, patchRun) in states)
            {
                var a0 = _watcher!.AppliedCount;
                var ok = undo ? ed.Edits.Undo() : ed.Edits.Redo();
                Check(ok, $"{what} -> {st}: status '{ed.StatusMessage}'");
                FileIs(st, $"{what} {st}");
                if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
                await Settle(patchRun);
            }
        }
        await Walk("undo", true, chain);
        Check(ed.Edits.History.UndoDepth == 0 && ed.Edits.History.RedoDepth == 8 && !_map.DynamicPatches.ContainsKey("GP-0002"), "everything undone: GP-0002 gone from the map");
        await Walk("redo", false, new[] { ("p_created", true), ("p_retyped", true), ("p_moved", true), ("p_water", true), ("p_moved", true), ("z_created", false), ("z_rekinded", false), ("p_moved", false) });
        Check(ed.Edits.History.RedoDepth == 0 && ed.Edits.History.UndoDepth == 8, "everything redone");

        // ---- Esc, one level at a time ----
        ed.SelectTool(EditorTool.Ground);
        ed.Shapes.SelectShape(ShapeKind.Patch, "GP-0001");
        await Key_(Key.Escape);
        Check(ed.Shapes.SelectedPatchId == null && ed.SelectedGroundType == "paint_aa3322" && ed.Tool == EditorTool.Ground, "Esc deselects the patch, the type and the tool stay");
        await Key_(Key.Escape);
        Check(ed.SelectedGroundType == null && ed.Tool == EditorTool.Ground, "Esc again deselects the type");
        await Key_(Key.Escape);
        Check(ed.Tool == EditorTool.None, "Esc again leaves the tool");
        ed.SelectTool(EditorTool.Ground);
        ed.SelectGroundType("paint_aa3322");
        ed.Shapes.StartShape();
        ed.Shapes.AddShapePoint(q[0][0], q[0][1]);
        await Key_(Key.Backspace);
        Check(ed.Shapes.ShapeDraft.Count == 0, "Backspace takes the last corner back");
        ed.Shapes.AddShapePoint(q[0][0], q[0][1]);
        ed.SelectTool(EditorTool.View);
        Check(ed.Shapes.ShapeDraft.Count == 0 && ed.StatusMessage.Contains("never saved"), $"switching tool names the discarded shape: '{ed.StatusMessage}'");
        ed.SelectTool(EditorTool.Ground);
        Check(ed.Shapes.ShapeAt(ShapeKind.Patch, plan.GetProperty("inside_p1")[0].GetDouble(), plan.GetProperty("inside_p1")[1].GetDouble()) == "GP-0001", "hit test finds GP-0001");
        ed.Shapes.Panel.PressRow("GP-0001"); ed.RefreshNow();
        Check(ed.Shapes.SelectedPatchId == "GP-0001" && ed.Shapes.Panel.DeleteButtonShown, "clicking the list row selects it");

        // ---- palette ----
        await Key_(Key.P, ctrl: true);
        Check(ed.Commands.Visible, "Ctrl+P opens the palette");
        ed.Commands.SetFilter("patch");
        Check(ed.Commands.Labels.Any(l => l.StartsWith("Ground: draw a patch")) && ed.Commands.Labels.Any(l => l.StartsWith("GP-0001")), $"palette finds the patch command and GP-0001: {string.Join(" / ", ed.Commands.Labels)}");
        ed.Commands.SetFilter("zone");
        Check(ed.Commands.Labels.Any(l => l.StartsWith("Zones: draw a zone")), $"palette finds the zone command: {string.Join(" / ", ed.Commands.Labels)}");
        await Key_(Key.Escape);
        Check(!ed.Commands.Visible, "Esc closes the palette");
        ed.Commands.Open(); ed.Commands.SetFilter("Ground: draw a patch");
        Check(ed.Commands.Run(0) && ed.Shapes.Mode == ShapeState.Drawing, "palette runs 'Ground: draw a patch'");
        ed.Shapes.ShapeEscape();

        // ---- a zone that comes from a rule is edited like any other: one save, one undo step ----
        ed.SelectTool(EditorTool.Zones);
        await Frame();
        ed.RefreshNow();
        var rule = ed.Zones().FirstOrDefault(z => z.FromRule);
        if (rule == null) GD.Print("info: the map has no zone that comes from a rule; skipped");
        else
        {
            var kind = rule.Kind; var other = kind == "bigfoot" ? "deer" : "bigfoot";
            var n0 = ed.ZoneCount(kind); var u0 = ed.Edits.History.UndoDepth; var s0 = ed.Edits.SaveCount; var before = Disk();
            Check(ed.ZoneKindRowText(kind) == $"{ed.ZoneLabel(kind)}   {n0}", $"the row of {kind} shows every zone of it as one number: '{ed.ZoneKindRowText(kind)}' ({n0})");
            ed.Shapes.SelectShape(ShapeKind.Zone, rule.Id); ed.RefreshNow();
            Check(ed.Shapes.SelectedZoneId == rule.Id && ed.SelectedZoneKind == kind && ed.Shapes.Panel.RowSelected(rule.Id), $"a zone from a rule ({rule.Label}) is selectable and listed");
            Check(ed.Shapes.DeleteShape(ShapeKind.Zone, rule.Id) && ed.Edits.SaveCount == s0 + 1 && ed.Edits.History.UndoDepth == u0 + 1, "deleting it is one save, one undo step");
            Check(ed.ZoneCount(kind) == n0 - 1 && ed.ZoneCutCount == 1 && !Disk().Contains("\"zones\"") && Disk().Contains("zone_cuts"), "the count drops by one; a cut is recorded, no zone is drawn");
            Check(ed.Edits.Undo() && Disk() == before && ed.ZoneCount(kind) == n0 && ed.ZoneCutCount == 0, "one undo brings it back exactly");
            Check(ed.SetZoneKind(rule.Id, other) && ed.Edits.SaveCount == s0 + 3 && ed.Edits.History.UndoDepth == u0 + 1, "changing its kind is one save, one undo step");
            Check(ed.Shapes.SelectedZoneId == "Z-0001" && ed.Zone("Z-0001")!.Kind == other && ed.ZoneCount(kind) == n0 - 1 && ed.ZoneCutCount == 1, "it is now an ordinary zone of the new kind, selected; the old kind lost it");
            Check(ed.Edits.Undo() && Disk() == before && ed.ZoneCount(kind) == n0, "one undo brings it back exactly");
            var c = rule.Points; var mid = (c.Average(p => p.x), c.Average(p => p.y));
            var to = (c[0].x + (mid.Item1 - c[0].x) * 0.1, c[0].y + (mid.Item2 - c[0].y) * 0.1);
            Check(ed.Shapes.MoveShapeCorner(ShapeKind.Zone, rule.Id, 0, to.Item1, to.Item2) && ed.Edits.SaveCount == s0 + 5 && ed.Edits.History.UndoDepth == u0 + 1, $"moving a corner is one save, one undo step: '{ed.StatusMessage}'");
            Check(ed.Shapes.SelectedZoneId == "Z-0001" && ed.Zone("Z-0001")!.Points.Count == c.Count && ed.ZoneCount(kind) == n0, "the moved copy replaces it: same count, selected");
            Check(ed.Edits.Undo() && Disk() == before && ed.ZoneCount(kind) == n0, "one undo brings it back exactly");

            // ---- a selected kind is shown alone; marked zones are deleted together: one confirm, one save, one undo step ----
            ed.Shapes.SelectShape(ShapeKind.Zone, null); ed.SelectZoneKind(kind); ed.RefreshNow(); ed.ZoneOverlayNow();
            Check(ed.ZoneFillAlpha(kind) > 0.5f && ed.ZoneFillAlpha(other) == 0f, $"only the selected kind is drawn: {kind} {ed.ZoneFillAlpha(kind)}, {other} {ed.ZoneFillAlpha(other)}");
            ed.SetZoneOverlay(false, false); ed.ZoneOverlayNow();
            Check(ed.ZonesDrawn && !ed.ZoneOverlayShown, "the selected kind stays shown with the overlay off");
            ed.SetZoneOverlay(true, false);
            var two = ed.Zones().Where(z => z.Kind == kind).Take(2).Select(z => z.Id).ToList();
            Check(!ed.ZoneMarkDeleteEnabled && ed.MarkedZoneCount == 0, "nothing marked: the mass delete is disabled");
            ed.Shapes.Panel.TickRow(two[0], true); if (two.Count > 1) ed.ToggleZoneMark(two[1]); ed.RefreshNow(); ed.ZoneOverlayNow();
            Check(ed.MarkedZoneCount == two.Count && two.All(ed.Shapes.Panel.RowMarked) && ed.ZoneMarkDeleteEnabled && ed.ZoneMarkDeleteText.Contains($"{two.Count} marked"), $"a row tick and a map mark both count: '{ed.ZoneMarkDeleteText}'");
            Check(ed.SegmentCount(EditorMode.OutlineZoneMarked) > 0, "marked zones are outlined on the map");
            s0 = ed.Edits.SaveCount; u0 = ed.Edits.History.UndoDepth;
            await Key_(Key.Delete, shift: true);
            Check(ed.Confirm.Visible && ed.Confirm.Text.Contains($"{two.Count} marked") && ed.Edits.SaveCount == s0, $"Shift+Del asks first and names what goes: '{ed.Confirm.Text.Replace('\n', '|')}'");
            ed.Confirm.Accept(); ed.RefreshNow();
            Check(ed.Edits.SaveCount == s0 + 1 && ed.Edits.History.UndoDepth == u0 + 1 && ed.ZoneCount(kind) == n0 - two.Count && ed.MarkedZoneCount == 0, "the marked zones go in one save, one undo step; the marks are gone");
            Check(ed.Edits.Undo() && Disk() == before && ed.ZoneCount(kind) == n0, "one undo brings them all back exactly");
            ed.MarkListedZones(); ed.RefreshNow();
            Check(ed.MarkedZoneCount == n0, $"Mark all marks every zone of the selected kind ({ed.MarkedZoneCount}/{n0})");
            ed.ClearZoneMarks(); ed.RefreshNow();
            Check(ed.MarkedZoneCount == 0 && !ed.ZoneMarkDeleteEnabled, "Unmark all clears the marks; nothing was deleted");
        }

        Check(_watcher!.FailedCount == 0, "no failed run during the whole smoke");
        GD.Print($"info: undo depth {ed.Edits.History.UndoDepth}, saves {ed.Edits.SaveCount}, selected {ed.Shapes.SelectedPatchId}");
    }
}
