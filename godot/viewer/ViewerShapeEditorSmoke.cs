using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

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
            var s0 = ed.SaveCount; var u0 = ed.UndoDepth; var a0 = _watcher!.AppliedCount;
            if (driver != null) await driver(); // key events arrive a frame later
            var ok = act();
            Check(ok && ed.SaveCount == s0 + 1 && ed.UndoDepth == u0 + 1, $"[{name}] one save, one undo step (saves {ed.SaveCount - s0}, undo depth {ed.UndoDepth - u0}), status '{ed.StatusMessage}'");
            FileIs(step, name);
            if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
            await Settle(patchRun);
            return ok;
        }
        async Task<bool> Refused(string name, Func<bool> act, string step)
        {
            var s0 = ed.SaveCount; var u0 = ed.UndoDepth;
            var before = Disk();
            var ok = act();
            Check(!ok && ed.SaveCount == s0 && ed.UndoDepth == u0 && Disk() == before && Disk() == Expect(step), $"[{name}] refused: nothing saved, file unchanged, status '{ed.StatusMessage}'");
            return !ok;
        }

        Check(Disk() == Expect("seed"), "scratch edits file is the planned seed");
        Check(ed.NextShapeIdOf(Disk(), ShapeKind.Patch) == "GP-0002", "next free patch ID after GP-0001 is GP-0002");
        Check(ed.NextShapeIdOf(Disk(), ShapeKind.Zone) == "Z-0001", "next free zone ID with no zones is Z-0001");
        await Physics(3);
        ed.SetActive(true);
        ed.SelectTool(EditorTool.Ground);
        await Frame();
        ed.RefreshNow();

        // ---- ground tool: idle, list, empty selection ----
        Check(ed.Tool == EditorTool.Ground && ed.ShapeMode == EditorMode.ShapeState.Idle, "Ground tool opens with no gesture under way");
        Check(ed.ShapeRowCount == 1 && !ed.ShapeEmptyShown && !ed.ShapeRowSelected("GP-0001"), "panel lists the seeded GP-0001");
        Check(ed.ShapeHint.Contains("draws a patch") && ed.ShapePanelState == "No patch selected", $"idle hint and state: '{ed.ShapeHint}'");
        await Key_(Key.N);
        Check(ed.ShapeMode == EditorMode.ShapeState.Idle && ed.StatusMessage.Contains("pick a ground type"), $"N without a selected type is refused with the way out: '{ed.StatusMessage}'");
        Check(ed.SelectGroundType("paint_336699"), "a type is selected in the list");

        // ---- drawing: refusals before any save ----
        var q = new[] { Pt("q0"), Pt("q1"), Pt("q2"), Pt("q3") };
        await Key_(Key.N);
        Check(ed.ShapeMode == EditorMode.ShapeState.Drawing && ed.ShapeHint.Contains("new patch"), "N starts drawing (hint says so)");
        Check(ed.AddShapePoint(q[0][0], q[0][1]) && ed.AddShapePoint(q[1][0], q[1][1]), "two corners placed");
        ed.RefreshNow();
        Check(ed.ShapePanelValidation.Contains("at least 3") && ed.ShapeDraftProblem() != null, $"inline validation before commit: '{ed.ShapePanelValidation}'");
        await Refused("close with 2 corners", () => ed.CloseShape(), "seed");
        Check(!ed.AddShapePoint(q[1][0], q[1][1]), "the same corner twice is refused");
        ed.RemoveLastShapePoint(); ed.RemoveLastShapePoint();
        var bow = Pt("bow");
        // bow tie: the 4th corner's edge would cross the first edge
        Check(ed.AddShapePoint(bow[0], bow[1]) && ed.AddShapePoint(bow[0] + 10, bow[1] + 10) && ed.AddShapePoint(bow[0] + 10, bow[1]), "bow tie: 3 corners placed");
        Check(!ed.AddShapePoint(bow[0], bow[1] + 10) && ed.ShapeDraft.Count == 3, $"self-crossing corner refused: '{ed.StatusMessage}'");
        ed.ShapeEscape(); // Esc cancels drawing
        Check(ed.ShapeMode == EditorMode.ShapeState.Idle && ed.ShapeDraft.Count == 0 && ed.StatusMessage.Contains("nothing was saved"), "Esc cancels drawing, nothing saved");
        // collinear: closes to a shape with no area
        await Key_(Key.N);
        foreach (var dx in new[] { 0.0, 5.0, 10.0 }) ed.AddShapePoint(bow[0] + dx, bow[1]);
        await Refused("close a flat (no area) shape", () => ed.CloseShape(), "seed");
        Check(ed.ShapeDraftProblem()!.Contains("crosses or touches itself, or has no area"), $"validation names the problem: '{ed.ShapeDraftProblem()}'");
        ed.ShapeEscape();
        Check(ed.ShapeMode == EditorMode.ShapeState.Idle, "Esc cancelled the flat shape");

        // ---- draw the quad and create it with Enter (a land type: no extra step) ----
        await Key_(Key.N);
        foreach (var p in q) ed.AddShapePoint(p[0], p[1]);
        Check(ed.ShapeDraft.Count == 4 && ed.ShapeDraftProblem() == null, "quad placed and valid");
        ed.RefreshNow();
        Check(ed.ShapePanelValidation.StartsWith("Shape is valid") && ed.ShapePanelValidation.Contains("Not saved"), $"panel says valid and unsaved: '{ed.ShapePanelValidation}'");
        Check(ed.ShapeDraftPending, "unsaved marker while drawing");
        ed.OutlineSegmentsNow();
        Check(ed.SegmentCount(EditorMode.OutlineShapeDraft) == 3 && ed.ShapeHandleCount == 4, $"live preview: {ed.SegmentCount(EditorMode.OutlineShapeDraft)} edges, {ed.ShapeHandleCount} corner handles");
        Check(Disk() == Expect("seed"), "nothing saved while drawing");
        var saves0 = ed.SaveCount;
        await Gesture("create GP-0002 (Enter)", () => ed.ShapeMode == EditorMode.ShapeState.Selected, "p_created", true, () => Key_(Key.Enter));
        Check(ed.SelectedPatchId == "GP-0002" && ed.SelectedGroundType == "paint_336699" && ed.ShapeDraft.Count == 0, "new patch is selected (and its type), draft cleared");
        Check(ed.SaveCount == saves0 + 1, "creating saved exactly once");
        Check(_map.DynamicPatches.ContainsKey("GP-0002"), "GP-0002 reached the map through the watcher (patch-run)");
        Check(ed.ShapeRowCount == 2 && ed.ShapeRowSelected("GP-0002"), "list shows both patches, GP-0002 selected");
        Check(ed.PatchTypeShown == "paint_336699", "the form shows the patch's type");

        // ---- retype (one gesture) ----
        await Gesture("retype GP-0002", () => ed.SetPatchType("GP-0002", "paint_99cc33"), "p_retyped", true);
        Check(DynamicColour("GP-0002") is { } dc && SameColour(dc, "#99cc33"), "retyped patch is regenerated in the new type's colour");
        Check(ed.SetPatchType("GP-0002", "paint_99cc33") && ed.UndoDepth == 2, "the same type again saves nothing");
        Check(!ed.SetPatchType("GP-0002", "no_such_type") && ed.UndoDepth == 2, "an unknown type is refused");

        // ---- corner drag: preview, refused crossing, valid move ----
        var inv = Pt("invalid_to"); var mv = Pt("move_to");
        Check(ed.StartCornerDrag(2) && ed.ShapeMode == EditorMode.ShapeState.Dragging && ed.ShapeHint.Contains("moving a corner"), "drag starts on corner 3");
        ed.DragCornerToAbs(inv[0], inv[1]);
        ed.ShapeWorldNow();
        Check(ed.SegmentCount(EditorMode.OutlineShapeBad) > 0 && ed.SegmentCount(EditorMode.OutlineShapeDrag) == 0, "crossing drag previews in the refused colour");
        Check(Disk() == Expect("p_retyped"), "file untouched during the drag");
        await Refused("drag corner into a crossing", () => ed.EndCornerDrag(), "p_retyped");
        ed.StartCornerDrag(1);
        ed.DragCornerToAbs(mv[0], mv[1]);
        ed.ShapeWorldNow();
        Check(ed.SegmentCount(EditorMode.OutlineShapeDrag) == 4 && ed.SegmentCount(EditorMode.OutlineShapeBad) == 0, "valid drag previews live");
        await Key_(Key.Escape);
        Check(ed.ShapeMode == EditorMode.ShapeState.Selected && Disk() == Expect("p_retyped"), "Esc cancels the drag, nothing saved");
        ed.StartCornerDrag(1);
        ed.DragCornerToAbs(mv[0], mv[1]);
        await Gesture("drag corner 2", () => ed.EndCornerDrag(), "p_moved", true);

        // ---- a patch of a water type asks for its water level before it is created ----
        var wq = new[] { Pt("w0"), Pt("w1"), Pt("w2"), Pt("w3") };
        Check(ed.SelectGroundType("pond"), "the water type is selected");
        await Key_(Key.N);
        foreach (var p in wq) ed.AddShapePoint(p[0], p[1]);
        await Key_(Key.Enter);
        Check(ed.ShapeMode == EditorMode.ShapeState.Level && ed.PatchWaterRowShown && ed.ShapeCreateButtonShown, "Enter on a water patch asks for the water level: nothing is created yet");
        Check(Disk() == Expect("p_moved"), "nothing saved while the water level is asked for");
        ed.OutlineSegmentsNow();
        Check(ed.SegmentCount(EditorMode.OutlineShapeDraft) == 4, "closing edge is part of the preview");
        await Key_(Key.Escape);
        Check(ed.ShapeMode == EditorMode.ShapeState.Drawing && ed.ShapeDraft.Count == 4, "Esc in the water level step goes back to the corners");
        await Key_(Key.Enter);
        ed.SetDrawWaterLevel(1.5);
        await Gesture("create the water patch GP-0003 (Enter)", () => ed.ShapeMode == EditorMode.ShapeState.Selected, "p_water", true, () => Key_(Key.Enter));
        Check(ed.SelectedPatchId == "GP-0003" && ed.Patch("GP-0003")!.WaterY == 1.5, "the water patch carries its water level");
        Check(!ed.SetPatchWater("GP-0002", 2.0), "a land patch has no water level");
        Check(!ed.SetGroundCarried("pond", "water", false) && ed.UndoDepth == 4, $"unticking water on a type a patch uses is refused: '{ed.StatusMessage}'");
        ed.RequestDeleteGroundType();
        Check(!ed.ConfirmVisible && ed.StatusMessage.Contains("patch"), $"a type a patch uses is not offered for deletion (blocked with the reason, no confirm): '{ed.StatusMessage}'");

        // ---- delete: confirm names the patch; cancel keeps it ----
        await Key_(Key.Delete);
        Check(ed.ConfirmVisible && ed.ConfirmText.Contains("GP-0003"), $"Del asks first and names the patch: '{ed.ConfirmText.Replace('\n', '|')}'");
        await Key_(Key.Escape);
        Check(!ed.ConfirmVisible && ed.Patch("GP-0003") != null && Disk() == Expect("p_water"), "Esc on the confirm cancels: patch kept");
        await Gesture("delete GP-0003", () => { ed.ConfirmAccept(); return !ed.ConfirmVisible && ed.Patch("GP-0003") == null; }, "p_moved", true, () => Key_(Key.Delete));
        Check(ed.SelectedPatchId == null && ed.ShapeRowCount == 2, "deleted patch leaves the selection and the list");

        // ---- zones tool ----
        ed.SelectTool(EditorTool.Zones);
        await Frame();
        ed.RefreshNow();
        ed.SelectZoneKind(null); ed.SelectShape(ShapeKind.Zone, null); ed.RefreshNow(); // the saved editor_ui.cfg of this machine may have restored a kind
        Check(ed.Tool == EditorTool.Zones && !ed.ZoneErrorShown && ed.ZoneKindRowCount > 0, $"Zones tool lists the map's zone kinds ({ed.ZoneKindRowCount}); the map has zone_kinds");
        Check(ed.Zones().All(z => z.FromRule) && ed.ShapePanelState == "No zone selected", "nothing drawn yet: every zone on the map comes from a rule, none is selected");
        var deerRow = ed.ZoneKindRowText("deer");
        Check(!deerRow.Contains("drawn") && !deerRow.Contains("rule"), $"a kind row has one number, no drawn/rule wording: '{deerRow}'");
        Check(ed.ShapeHint.Contains("zones:"), $"zones idle hint: '{ed.ShapeHint}'");
        await Key_(Key.N);
        Check(ed.ShapeMode == EditorMode.ShapeState.Idle && ed.StatusMessage.Contains("pick a zone kind"), $"N without a kind is refused with the way out: '{ed.StatusMessage}'");
        ed.PressZoneKindRow("bigfoot"); ed.RefreshNow();
        Check(ed.SelectedZoneKind == "bigfoot", "clicking a kind row selects it");
        var zq = new[] { Pt("z0"), Pt("z1"), Pt("z2"), Pt("z3") };
        await Key_(Key.N);
        foreach (var p in zq) ed.AddShapePoint(p[0], p[1]);
        Check(ed.ShapeHint.Contains("Enter creates"), $"a zone is created by Enter (no extra step): '{ed.ShapeHint}'");
        await Gesture("create Z-0001 (Enter)", () => ed.SelectedZoneId == "Z-0001", "z_created", false, () => Key_(Key.Enter));
        Check(ed.ShapeRowCount == 1 && ed.ShapeRowSelected("Z-0001") && ed.ZoneKindShown == "bigfoot", "the zone is listed, selected, and its form shows its kind");
        await Gesture("change Z-0001 to deer", () => ed.SetZoneKind("Z-0001", "deer"), "z_rekinded", false);
        Check(ed.SelectedZoneKind == "deer", "the kind selection follows the zone");
        Check(!ed.SetZoneKind("Z-0001", "no_such_kind") && ed.UndoDepth == 7, "an unknown kind is refused");
        Check(ed.ShapeAt(ShapeKind.Zone, plan.GetProperty("inside_z")[0].GetDouble(), plan.GetProperty("inside_z")[1].GetDouble()) == "Z-0001", "hit test finds Z-0001");
        ed.ZoneOverlayNow();
        Check(ed.ZoneOverlayShown, "the overlay is on by default");
        await Key_(Key.M);
        Check(!ed.ZoneOverlayShown, "M hides the zone overlay");
        await Key_(Key.M);
        Check(ed.ZoneOverlayShown, "M shows it again");
        await Key_(Key.Delete);
        Check(ed.ConfirmVisible && ed.ConfirmText.Contains("Z-0001"), $"Del asks first and names the zone: '{ed.ConfirmText.Replace('\n', '|')}'");
        await Gesture("delete Z-0001", () => { ed.ConfirmAccept(); return ed.Zone("Z-0001") == null; }, "p_moved", false);
        Check(ed.SelectedZoneId == null && ed.Zone("Z-0001") == null, "deleted zone leaves the selection (and the zones key is gone, as dump_text)");

        // ---- undo / redo of every step, byte for byte ----
        var chain = new[] { ("z_rekinded", false), ("z_created", false), ("p_moved", false), ("p_water", true), ("p_moved", true), ("p_retyped", true), ("p_created", true), ("seed", true) };
        Check(ed.UndoDepth == 8 && ed.NextUndoDescription!.Contains("deleted Z-0001"), $"undo stack: {ed.UndoDepth} steps, next '{ed.NextUndoDescription}'");
        async Task Walk(string what, bool undo, (string state, bool patchRun)[] states)
        {
            foreach (var (st, patchRun) in states)
            {
                var a0 = _watcher!.AppliedCount;
                var ok = undo ? ed.Undo() : ed.Redo();
                Check(ok, $"{what} -> {st}: status '{ed.StatusMessage}'");
                FileIs(st, $"{what} {st}");
                if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
                await Settle(patchRun);
            }
        }
        await Walk("undo", true, chain);
        Check(ed.UndoDepth == 0 && ed.RedoDepth == 8 && !_map.DynamicPatches.ContainsKey("GP-0002"), "everything undone: GP-0002 gone from the map");
        await Walk("redo", false, new[] { ("p_created", true), ("p_retyped", true), ("p_moved", true), ("p_water", true), ("p_moved", true), ("z_created", false), ("z_rekinded", false), ("p_moved", false) });
        Check(ed.RedoDepth == 0 && ed.UndoDepth == 8, "everything redone");

        // ---- Esc, one level at a time ----
        ed.SelectTool(EditorTool.Ground);
        ed.SelectShape(ShapeKind.Patch, "GP-0001");
        await Key_(Key.Escape);
        Check(ed.SelectedPatchId == null && ed.SelectedGroundType == "paint_aa3322" && ed.Tool == EditorTool.Ground, "Esc deselects the patch, the type and the tool stay");
        await Key_(Key.Escape);
        Check(ed.SelectedGroundType == null && ed.Tool == EditorTool.Ground, "Esc again deselects the type");
        await Key_(Key.Escape);
        Check(ed.Tool == EditorTool.None, "Esc again leaves the tool");
        ed.SelectTool(EditorTool.Ground);
        ed.SelectGroundType("paint_aa3322");
        ed.StartShape();
        ed.AddShapePoint(q[0][0], q[0][1]);
        await Key_(Key.Backspace);
        Check(ed.ShapeDraft.Count == 0, "Backspace takes the last corner back");
        ed.AddShapePoint(q[0][0], q[0][1]);
        ed.SelectTool(EditorTool.View);
        Check(ed.ShapeDraft.Count == 0 && ed.StatusMessage.Contains("never saved"), $"switching tool names the discarded shape: '{ed.StatusMessage}'");
        ed.SelectTool(EditorTool.Ground);
        Check(ed.ShapeAt(ShapeKind.Patch, plan.GetProperty("inside_p1")[0].GetDouble(), plan.GetProperty("inside_p1")[1].GetDouble()) == "GP-0001", "hit test finds GP-0001");
        ed.PressShapeRow("GP-0001"); ed.RefreshNow();
        Check(ed.SelectedPatchId == "GP-0001" && ed.ShapeDeleteButtonShown, "clicking the list row selects it");

        // ---- palette ----
        await Key_(Key.P, ctrl: true);
        Check(ed.PaletteVisible, "Ctrl+P opens the palette");
        ed.PaletteSetFilter("patch");
        Check(ed.PaletteLabels.Any(l => l.StartsWith("Ground: draw a patch")) && ed.PaletteLabels.Any(l => l.StartsWith("GP-0001")), $"palette finds the patch command and GP-0001: {string.Join(" / ", ed.PaletteLabels)}");
        ed.PaletteSetFilter("zone");
        Check(ed.PaletteLabels.Any(l => l.StartsWith("Zones: draw a zone")), $"palette finds the zone command: {string.Join(" / ", ed.PaletteLabels)}");
        await Key_(Key.Escape);
        Check(!ed.PaletteVisible, "Esc closes the palette");
        ed.OpenPalette(); ed.PaletteSetFilter("Ground: draw a patch");
        Check(ed.PaletteRun(0) && ed.ShapeMode == EditorMode.ShapeState.Drawing, "palette runs 'Ground: draw a patch'");
        ed.ShapeEscape();

        // ---- a zone that comes from a rule is edited like any other: one save, one undo step ----
        ed.SelectTool(EditorTool.Zones);
        await Frame();
        ed.RefreshNow();
        var rule = ed.Zones().FirstOrDefault(z => z.FromRule);
        if (rule == null) GD.Print("info: the map has no zone that comes from a rule; skipped");
        else
        {
            var kind = rule.Kind; var other = kind == "bigfoot" ? "deer" : "bigfoot";
            var n0 = ed.ZoneCount(kind); var u0 = ed.UndoDepth; var s0 = ed.SaveCount; var before = Disk();
            Check(ed.ZoneKindRowText(kind) == $"{ed.ZoneLabel(kind)}   {n0}", $"the row of {kind} shows every zone of it as one number: '{ed.ZoneKindRowText(kind)}' ({n0})");
            ed.SelectShape(ShapeKind.Zone, rule.Id); ed.RefreshNow();
            Check(ed.SelectedZoneId == rule.Id && ed.SelectedZoneKind == kind && ed.ShapeRowSelected(rule.Id), $"a zone from a rule ({rule.Label}) is selectable and listed");
            Check(ed.DeleteShape(ShapeKind.Zone, rule.Id) && ed.SaveCount == s0 + 1 && ed.UndoDepth == u0 + 1, "deleting it is one save, one undo step");
            Check(ed.ZoneCount(kind) == n0 - 1 && ed.ZoneCutCount == 1 && !Disk().Contains("\"zones\"") && Disk().Contains("zone_cuts"), "the count drops by one; a cut is recorded, no zone is drawn");
            Check(ed.Undo() && Disk() == before && ed.ZoneCount(kind) == n0 && ed.ZoneCutCount == 0, "one undo brings it back exactly");
            Check(ed.SetZoneKind(rule.Id, other) && ed.SaveCount == s0 + 3 && ed.UndoDepth == u0 + 1, "changing its kind is one save, one undo step");
            Check(ed.SelectedZoneId == "Z-0001" && ed.Zone("Z-0001")!.Kind == other && ed.ZoneCount(kind) == n0 - 1 && ed.ZoneCutCount == 1, "it is now an ordinary zone of the new kind, selected; the old kind lost it");
            Check(ed.Undo() && Disk() == before && ed.ZoneCount(kind) == n0, "one undo brings it back exactly");
            var c = rule.Points; var mid = (c.Average(p => p.x), c.Average(p => p.y));
            var to = (c[0].x + (mid.Item1 - c[0].x) * 0.1, c[0].y + (mid.Item2 - c[0].y) * 0.1);
            Check(ed.MoveShapeCorner(ShapeKind.Zone, rule.Id, 0, to.Item1, to.Item2) && ed.SaveCount == s0 + 5 && ed.UndoDepth == u0 + 1, $"moving a corner is one save, one undo step: '{ed.StatusMessage}'");
            Check(ed.SelectedZoneId == "Z-0001" && ed.Zone("Z-0001")!.Points.Count == c.Count && ed.ZoneCount(kind) == n0, "the moved copy replaces it: same count, selected");
            Check(ed.Undo() && Disk() == before && ed.ZoneCount(kind) == n0, "one undo brings it back exactly");
        }

        Check(_watcher!.FailedCount == 0, "no failed run during the whole smoke");
        GD.Print($"info: undo depth {ed.UndoDepth}, saves {ed.SaveCount}, selected {ed.SelectedPatchId}");
    }
}
