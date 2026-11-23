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
    sealed class ShapeCtx : SmokeCtx
    {
        public readonly double[][] Q;
        // the zone that comes from a rule, picked for the rule-zone scenarios
        public ZoneInfo Rule = null!;
        public string Kind = "", Other = "", Before = "";
        public int N0;

        public ShapeCtx(string planPath, string editsPath) : base(planPath, editsPath)
            => Q = new[] { D("q0"), D("q1"), D("q2"), D("q3") };
    }

    Task RunShapeEditorSmoke() => RunSmoke("shape editor", ShapeEditorSmokeBody);

    async Task ShapeEditorSmokeBody()
    {
        using var c = new ShapeCtx(_opts["shape-editor-smoke-plan"], _editor!.EditsPath);
        c.MakeStepsDir();

        CheckSeed(c);
        FirstFreeIds(c);
        await OpenGroundTool();
        await GroundToolIdle();
        await DrawingRefusals(c);
        await CreatePatchWithEnter(c);
        await RetypePatch(c);
        await CornerDrag(c);
        await WaterPatch(c);
        await DeletePatchWithConfirm(c);
        await ZonesToolIdle();
        await CreateAndRekindZone(c);
        await ZoneOverlayKey();
        await DeleteZone(c);
        await UndoRedoEverything(c);
        await EscOneLevelAtATime(c);
        await Palette();
        await RuleZoneSave(c);

        Check(_watcher!.FailedCount == 0, "no failed run during the whole smoke");
        var ed = _editor!;
        GD.Print($"info: undo depth {ed.Edits.History.UndoDepth}, saves {ed.Edits.SaveCount}, selected {ed.Shapes.SelectedPatchId}");
    }

    // the edits file is exactly the text the Python writer produced for this step; the file is kept for the driver's validator
    void ShapeFileIs(ShapeCtx c, string step, string what)
    {
        FileIs(c, step, what);
        c.SaveStep(step);
    }

    async Task Settle(bool patchRun)
    {
        if (patchRun) await WaitUntil(() => !_watcher!.Busy, 90);
        await Physics(3);
        _editor!.RefreshNow();
    }

    // one gesture = exactly one save and one undo step; a patch change also waits for the patch-run, a zone change has none
    async Task<bool> Gesture(ShapeCtx c, string name, Func<bool> act, string step, bool patchRun, Func<Task>? driver = null)
    {
        var ed = _editor!;
        var s0 = ed.Edits.SaveCount; var u0 = ed.Edits.History.UndoDepth; var a0 = _watcher!.AppliedCount;
        if (driver != null) await driver(); // key events arrive a frame later
        var ok = act();
        Check(ok && ed.Edits.SaveCount == s0 + 1 && ed.Edits.History.UndoDepth == u0 + 1, $"[{name}] one save, one undo step (saves {ed.Edits.SaveCount - s0}, undo depth {ed.Edits.History.UndoDepth - u0}), status '{ed.Chrome.StatusMessage}'");
        ShapeFileIs(c, step, name);
        if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
        await Settle(patchRun);
        return ok;
    }

    bool Refused(ShapeCtx c, string name, Func<bool> act, string step)
    {
        var ed = _editor!;
        var s0 = ed.Edits.SaveCount; var u0 = ed.Edits.History.UndoDepth;
        var before = c.Disk();
        var ok = act();
        Check(!ok && ed.Edits.SaveCount == s0 && ed.Edits.History.UndoDepth == u0 && c.Disk() == before && c.Disk() == c.Expect(step), $"[{name}] refused: nothing saved, file unchanged, status '{ed.Chrome.StatusMessage}'");
        return !ok;
    }

    void FirstFreeIds(ShapeCtx c)
    {
        var ed = _editor!;
        Check(ed.Shapes.NextShapeIdOf(c.Disk(), ShapeKind.Patch) == "GP-0002", "next free patch ID after GP-0001 is GP-0002");
        Check(ed.Shapes.NextShapeIdOf(c.Disk(), ShapeKind.Zone) == "Z-0001", "next free zone ID with no zones is Z-0001");
    }

    async Task OpenGroundTool()
    {
        var ed = _editor!;
        await Physics(3);
        ed.SetActive(true);
        ed.SelectTool(EditorTool.Ground);
        await Frame();
        ed.RefreshNow();
    }

    // ---- ground tool: idle, list, empty selection ----
    async Task GroundToolIdle()
    {
        var ed = _editor!;
        Check(ed.Tool == EditorTool.Ground && ed.Shapes.Mode == ShapeState.Idle, "Ground tool opens with no gesture under way");
        Check(ed.Shapes.Panel.RowCount == 1 && !ed.Shapes.Panel.EmptyShown && !ed.Shapes.Panel.RowSelected("GP-0001"), "panel lists the seeded GP-0001");
        Check(ed.ShapeHint.Contains("draws a patch") && ed.Shapes.Panel.State == "No patch selected", $"idle hint and state: '{ed.ShapeHint}'");
        await PressWait(Key.N);
        Check(ed.Shapes.Mode == ShapeState.Idle && ed.Chrome.StatusMessage.Contains("pick a ground type"), $"N without a selected type is refused with the way out: '{ed.Chrome.StatusMessage}'");
        Check(ed.Ground.SelectType("paint_336699"), "a type is selected in the list");
    }

    // ---- drawing: refusals before any save ----
    async Task DrawingRefusals(ShapeCtx c)
    {
        var ed = _editor!;
        var q = c.Q;
        await PressWait(Key.N);
        Check(ed.Shapes.Mode == ShapeState.Drawing && ed.ShapeHint.Contains("new patch"), "N starts drawing (hint says so)");
        Check(ed.Shapes.AddShapePoint(q[0][0], q[0][1]) && ed.Shapes.AddShapePoint(q[1][0], q[1][1]), "two corners placed");
        ed.RefreshNow();
        Check(ed.Shapes.Panel.Validation.Contains("at least 3") && ed.Shapes.ShapeDraftProblem() != null, $"inline validation before commit: '{ed.Shapes.Panel.Validation}'");
        Refused(c, "close with 2 corners", () => ed.Shapes.CloseShape(), "seed");
        Check(!ed.Shapes.AddShapePoint(q[1][0], q[1][1]), "the same corner twice is refused");
        ed.Shapes.RemoveLastShapePoint(); ed.Shapes.RemoveLastShapePoint();
        var bow = c.D("bow");
        // bow tie: the 4th corner's edge would cross the first edge
        Check(ed.Shapes.AddShapePoint(bow[0], bow[1]) && ed.Shapes.AddShapePoint(bow[0] + 10, bow[1] + 10) && ed.Shapes.AddShapePoint(bow[0] + 10, bow[1]), "bow tie: 3 corners placed");
        Check(!ed.Shapes.AddShapePoint(bow[0], bow[1] + 10) && ed.Shapes.ShapeDraft.Count == 3, $"self-crossing corner refused: '{ed.Chrome.StatusMessage}'");
        ed.Shapes.ShapeEscape(); // Esc cancels drawing
        Check(ed.Shapes.Mode == ShapeState.Idle && ed.Shapes.ShapeDraft.Count == 0 && ed.Chrome.StatusMessage.Contains("nothing was saved"), "Esc cancels drawing, nothing saved");
        // collinear: closes to a shape with no area
        await PressWait(Key.N);
        foreach (var dx in new[] { 0.0, 5.0, 10.0 }) ed.Shapes.AddShapePoint(bow[0] + dx, bow[1]);
        Refused(c, "close a flat (no area) shape", () => ed.Shapes.CloseShape(), "seed");
        Check(ed.Shapes.ShapeDraftProblem()!.Contains("crosses or touches itself, or has no area"), $"validation names the problem: '{ed.Shapes.ShapeDraftProblem()}'");
        ed.Shapes.ShapeEscape();
        Check(ed.Shapes.Mode == ShapeState.Idle, "Esc cancelled the flat shape");
    }

    // ---- draw the quad and create it with Enter (a land type: no extra step) ----
    async Task CreatePatchWithEnter(ShapeCtx c)
    {
        var ed = _editor!;
        await PressWait(Key.N);
        foreach (var p in c.Q) ed.Shapes.AddShapePoint(p[0], p[1]);
        Check(ed.Shapes.ShapeDraft.Count == 4 && ed.Shapes.ShapeDraftProblem() == null, "quad placed and valid");
        ed.RefreshNow();
        Check(ed.Shapes.Panel.Validation.StartsWith("Shape is valid") && ed.Shapes.Panel.Validation.Contains("Not saved"), $"panel says valid and unsaved: '{ed.Shapes.Panel.Validation}'");
        Check(ed.Shapes.ShapeDraftPending, "unsaved marker while drawing");
        ed.Shapes.World.Redraw();
        Check(ed.Outlines.SegmentCount(ShapeWorld.OutlineDraft) == 3 && ed.Shapes.World.HandleCount == 4, $"live preview: {ed.Outlines.SegmentCount(ShapeWorld.OutlineDraft)} edges, {ed.Shapes.World.HandleCount} corner handles");
        Check(c.Disk() == c.Expect("seed"), "nothing saved while drawing");
        var saves0 = ed.Edits.SaveCount;
        await Gesture(c, "create GP-0002 (Enter)", () => ed.Shapes.Mode == ShapeState.Selected, "p_created", true, () => PressWait(Key.Enter));
        Check(ed.Shapes.SelectedPatchId == "GP-0002" && ed.Ground.SelectedType == "paint_336699" && ed.Shapes.ShapeDraft.Count == 0, "new patch is selected (and its type), draft cleared");
        Check(ed.Edits.SaveCount == saves0 + 1, "creating saved exactly once");
        Check(_map.DynamicPatches.ContainsKey("GP-0002"), "GP-0002 reached the map through the watcher (patch-run)");
        Check(ed.Shapes.Panel.RowCount == 2 && ed.Shapes.Panel.RowSelected("GP-0002"), "list shows both patches, GP-0002 selected");
        Check(ed.Ground.PatchPage.TypeShown == "paint_336699", "the form shows the patch's type");
    }

    // ---- retype (one gesture) ----
    async Task RetypePatch(ShapeCtx c)
    {
        var ed = _editor!;
        await Gesture(c, "retype GP-0002", () => ed.Shapes.SetPatchType("GP-0002", "paint_99cc33"), "p_retyped", true);
        Check(DynamicColour("GP-0002") is { } dc && SameColour(dc, "#99cc33"), "retyped patch is regenerated in the new type's colour");
        Check(ed.Shapes.SetPatchType("GP-0002", "paint_99cc33") && ed.Edits.History.UndoDepth == 2, "the same type again saves nothing");
        Check(!ed.Shapes.SetPatchType("GP-0002", "no_such_type") && ed.Edits.History.UndoDepth == 2, "an unknown type is refused");
    }

    // ---- corner drag: preview, refused crossing, valid move ----
    async Task CornerDrag(ShapeCtx c)
    {
        var ed = _editor!;
        var inv = c.D("invalid_to"); var mv = c.D("move_to");
        Check(ed.Shapes.StartCornerDrag(2) && ed.Shapes.Mode == ShapeState.Dragging && ed.ShapeHint.Contains("moving a corner"), "drag starts on corner 3");
        ed.Shapes.DragCornerToAbs(inv[0], inv[1]);
        ed.Shapes.World.Redraw();
        Check(ed.Outlines.SegmentCount(ShapeWorld.OutlineBad) > 0 && ed.Outlines.SegmentCount(ShapeWorld.OutlineDrag) == 0, "crossing drag previews in the refused colour");
        Check(c.Disk() == c.Expect("p_retyped"), "file untouched during the drag");
        Refused(c, "drag corner into a crossing", () => ed.Shapes.EndCornerDrag(), "p_retyped");
        ed.Shapes.StartCornerDrag(1);
        ed.Shapes.DragCornerToAbs(mv[0], mv[1]);
        ed.Shapes.World.Redraw();
        Check(ed.Outlines.SegmentCount(ShapeWorld.OutlineDrag) == 4 && ed.Outlines.SegmentCount(ShapeWorld.OutlineBad) == 0, "valid drag previews live");
        await PressWait(Key.Escape);
        Check(ed.Shapes.Mode == ShapeState.Selected && c.Disk() == c.Expect("p_retyped"), "Esc cancels the drag, nothing saved");
        ed.Shapes.StartCornerDrag(1);
        ed.Shapes.DragCornerToAbs(mv[0], mv[1]);
        await Gesture(c, "drag corner 2", () => ed.Shapes.EndCornerDrag(), "p_moved", true);
    }

    // ---- a patch of a water type asks for its water level before it is created ----
    async Task WaterPatch(ShapeCtx c)
    {
        var ed = _editor!;
        var wq = new[] { c.D("w0"), c.D("w1"), c.D("w2"), c.D("w3") };
        Check(ed.Ground.SelectType("pond"), "the water type is selected");
        await PressWait(Key.N);
        foreach (var p in wq) ed.Shapes.AddShapePoint(p[0], p[1]);
        await PressWait(Key.Enter);
        Check(ed.Shapes.Mode == ShapeState.Level && ed.Ground.PatchPage.WaterRowShown && ed.Shapes.Panel.CreateButtonShown, "Enter on a water patch asks for the water level: nothing is created yet");
        Check(c.Disk() == c.Expect("p_moved"), "nothing saved while the water level is asked for");
        ed.Shapes.World.Redraw();
        Check(ed.Outlines.SegmentCount(ShapeWorld.OutlineDraft) == 4, "closing edge is part of the preview");
        await PressWait(Key.Escape);
        Check(ed.Shapes.Mode == ShapeState.Drawing && ed.Shapes.ShapeDraft.Count == 4, "Esc in the water level step goes back to the corners");
        await PressWait(Key.Enter);
        ed.Shapes.SetDrawWaterLevel(1.5);
        await Gesture(c, "create the water patch GP-0003 (Enter)", () => ed.Shapes.Mode == ShapeState.Selected, "p_water", true, () => PressWait(Key.Enter));
        Check(ed.Shapes.SelectedPatchId == "GP-0003" && ed.Shapes.Patch("GP-0003")!.WaterY == 1.5, "the water patch carries its water level");
        Check(!ed.Shapes.SetPatchWater("GP-0002", 2.0), "a land patch has no water level");
        Check(!ed.Ground.Ops.SetCarried("pond", "water", false) && ed.Edits.History.UndoDepth == 4, $"unticking water on a type a patch uses is refused: '{ed.Chrome.StatusMessage}'");
        ed.Ground.Ops.RequestDelete();
        Check(!ed.Confirm.Visible && ed.Chrome.StatusMessage.Contains("patch"), $"a type a patch uses is not offered for deletion (blocked with the reason, no confirm): '{ed.Chrome.StatusMessage}'");
    }

    // ---- delete: confirm names the patch; cancel keeps it ----
    async Task DeletePatchWithConfirm(ShapeCtx c)
    {
        var ed = _editor!;
        await PressWait(Key.Delete);
        Check(ed.Confirm.Visible && ed.Confirm.Text.Contains("GP-0003"), $"Del asks first and names the patch: '{ed.Confirm.Text.Replace('\n', '|')}'");
        await PressWait(Key.Escape);
        Check(!ed.Confirm.Visible && ed.Shapes.Patch("GP-0003") != null && c.Disk() == c.Expect("p_water"), "Esc on the confirm cancels: patch kept");
        await Gesture(c, "delete GP-0003", () => { ed.Confirm.Accept(); return !ed.Confirm.Visible && ed.Shapes.Patch("GP-0003") == null; }, "p_moved", true, () => PressWait(Key.Delete));
        Check(ed.Shapes.SelectedPatchId == null && ed.Shapes.Panel.RowCount == 2, "deleted patch leaves the selection and the list");
    }

    // ---- zones tool ----
    async Task ZonesToolIdle()
    {
        var ed = _editor!;
        ed.SelectTool(EditorTool.Zones);
        await Frame();
        ed.RefreshNow();
        ed.Zones.SelectKind(null); ed.Shapes.SelectShape(ShapeKind.Zone, null); ed.RefreshNow(); // the saved editor_ui.cfg of this machine may have restored a kind
        Check(ed.Tool == EditorTool.Zones && !ed.Zones.Panel.ErrorShown && ed.Zones.Panel.KindRowCount > 0, $"Zones tool lists the map's zone kinds ({ed.Zones.Panel.KindRowCount}); the map has zone_kinds");
        Check(ed.Zones.List().All(z => z.FromRule) && ed.Shapes.Panel.State == "No zone selected", "nothing drawn yet: every zone on the map comes from a rule, none is selected");
        var deerRow = ed.Zones.Panel.KindRowText("deer");
        Check(!deerRow.Contains("drawn") && !deerRow.Contains("rule"), $"a kind row has one number, no drawn/rule wording: '{deerRow}'");
        Check(ed.ShapeHint.Contains("zones:"), $"zones idle hint: '{ed.ShapeHint}'");
        await PressWait(Key.N);
        Check(ed.Shapes.Mode == ShapeState.Idle && ed.Chrome.StatusMessage.Contains("pick a zone kind"), $"N without a kind is refused with the way out: '{ed.Chrome.StatusMessage}'");
        ed.Zones.Panel.PressKindRow("bigfoot"); ed.RefreshNow();
        Check(ed.Zones.SelectedKind == "bigfoot", "clicking a kind row selects it");
    }

    async Task CreateAndRekindZone(ShapeCtx c)
    {
        var ed = _editor!;
        var zq = new[] { c.D("z0"), c.D("z1"), c.D("z2"), c.D("z3") };
        await PressWait(Key.N);
        foreach (var p in zq) ed.Shapes.AddShapePoint(p[0], p[1]);
        Check(ed.ShapeHint.Contains("Enter creates"), $"a zone is created by Enter (no extra step): '{ed.ShapeHint}'");
        await Gesture(c, "create Z-0001 (Enter)", () => ed.Shapes.SelectedZoneId == "Z-0001", "z_created", false, () => PressWait(Key.Enter));
        Check(ed.Shapes.Panel.RowCount == 1 && ed.Shapes.Panel.RowSelected("Z-0001") && ed.Zones.Panel.KindShown == "bigfoot", "the zone is listed, selected, and its form shows its kind");
        await Gesture(c, "change Z-0001 to deer", () => ed.Zones.SetKind("Z-0001", "deer"), "z_rekinded", false);
        Check(ed.Zones.SelectedKind == "deer", "the kind selection follows the zone");
        Check(!ed.Zones.SetKind("Z-0001", "no_such_kind") && ed.Edits.History.UndoDepth == 7, "an unknown kind is refused");
        Check(ed.Shapes.ShapeAt(ShapeKind.Zone, c.Plan.GetProperty("inside_z")[0].GetDouble(), c.Plan.GetProperty("inside_z")[1].GetDouble()) == "Z-0001", "hit test finds Z-0001");
    }

    async Task ZoneOverlayKey()
    {
        var ed = _editor!;
        ed.Zones.Overlay.Now();
        Check(ed.Zones.Overlay.On, "the overlay is on by default");
        await PressWait(Key.M);
        Check(!ed.Zones.Overlay.On, "M hides the zone overlay");
        await PressWait(Key.M);
        Check(ed.Zones.Overlay.On, "M shows it again");
    }

    async Task DeleteZone(ShapeCtx c)
    {
        var ed = _editor!;
        await PressWait(Key.Delete);
        Check(ed.Confirm.Visible && ed.Confirm.Text.Contains("Z-0001"), $"Del asks first and names the zone: '{ed.Confirm.Text.Replace('\n', '|')}'");
        await Gesture(c, "delete Z-0001", () => { ed.Confirm.Accept(); return ed.Zones.Find("Z-0001") == null; }, "p_moved", false);
        Check(ed.Shapes.SelectedZoneId == null && ed.Zones.Find("Z-0001") == null, "deleted zone leaves the selection (and the zones key is gone, as dump_text)");
    }

    // ---- undo / redo of every step, byte for byte ----
    async Task UndoRedoEverything(ShapeCtx c)
    {
        var ed = _editor!;
        var chain = new[] { ("z_rekinded", false), ("z_created", false), ("p_moved", false), ("p_water", true), ("p_moved", true), ("p_retyped", true), ("p_created", true), ("seed", true) };
        Check(ed.Edits.History.UndoDepth == 8 && ed.Edits.History.NextUndoDescription!.Contains("deleted Z-0001"), $"undo stack: {ed.Edits.History.UndoDepth} steps, next '{ed.Edits.History.NextUndoDescription}'");
        await Walk(c, "undo", true, chain);
        Check(ed.Edits.History.UndoDepth == 0 && ed.Edits.History.RedoDepth == 8 && !_map.DynamicPatches.ContainsKey("GP-0002"), "everything undone: GP-0002 gone from the map");
        await Walk(c, "redo", false, new[] { ("p_created", true), ("p_retyped", true), ("p_moved", true), ("p_water", true), ("p_moved", true), ("z_created", false), ("z_rekinded", false), ("p_moved", false) });
        Check(ed.Edits.History.RedoDepth == 0 && ed.Edits.History.UndoDepth == 8, "everything redone");
    }

    async Task Walk(ShapeCtx c, string what, bool undo, (string state, bool patchRun)[] states)
    {
        var ed = _editor!;
        foreach (var (st, patchRun) in states)
        {
            var a0 = _watcher!.AppliedCount;
            var ok = undo ? ed.Edits.Undo() : ed.Edits.Redo();
            Check(ok, $"{what} -> {st}: status '{ed.Chrome.StatusMessage}'");
            ShapeFileIs(c, st, $"{what} {st}");
            if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
            await Settle(patchRun);
        }
    }

    // ---- Esc, one level at a time ----
    async Task EscOneLevelAtATime(ShapeCtx c)
    {
        var ed = _editor!;
        var q = c.Q;
        ed.SelectTool(EditorTool.Ground);
        ed.Shapes.SelectShape(ShapeKind.Patch, "GP-0001");
        await PressWait(Key.Escape);
        Check(ed.Shapes.SelectedPatchId == null && ed.Ground.SelectedType == "paint_aa3322" && ed.Tool == EditorTool.Ground, "Esc deselects the patch, the type and the tool stay");
        await PressWait(Key.Escape);
        Check(ed.Ground.SelectedType == null && ed.Tool == EditorTool.Ground, "Esc again deselects the type");
        await PressWait(Key.Escape);
        Check(ed.Tool == EditorTool.None, "Esc again leaves the tool");
        ed.SelectTool(EditorTool.Ground);
        ed.Ground.SelectType("paint_aa3322");
        ed.Shapes.StartShape();
        ed.Shapes.AddShapePoint(q[0][0], q[0][1]);
        await PressWait(Key.Backspace);
        Check(ed.Shapes.ShapeDraft.Count == 0, "Backspace takes the last corner back");
        ed.Shapes.AddShapePoint(q[0][0], q[0][1]);
        ed.SelectTool(EditorTool.View);
        Check(ed.Shapes.ShapeDraft.Count == 0 && ed.Chrome.StatusMessage.Contains("never saved"), $"switching tool names the discarded shape: '{ed.Chrome.StatusMessage}'");
        ed.SelectTool(EditorTool.Ground);
        Check(ed.Shapes.ShapeAt(ShapeKind.Patch, c.Plan.GetProperty("inside_p1")[0].GetDouble(), c.Plan.GetProperty("inside_p1")[1].GetDouble()) == "GP-0001", "hit test finds GP-0001");
        ed.Shapes.Panel.PressRow("GP-0001"); ed.RefreshNow();
        Check(ed.Shapes.SelectedPatchId == "GP-0001" && ed.Shapes.Panel.DeleteButtonShown, "clicking the list row selects it");
    }

    // ---- palette ----
    async Task Palette()
    {
        var ed = _editor!;
        await PressWait(Key.P, ctrl: true);
        Check(ed.Commands.Visible, "Ctrl+P opens the palette");
        ed.Commands.SetFilter("patch");
        Check(ed.Commands.Labels.Any(l => l.StartsWith("Ground: draw a patch")) && ed.Commands.Labels.Any(l => l.StartsWith("GP-0001")), $"palette finds the patch command and GP-0001: {string.Join(" / ", ed.Commands.Labels)}");
        ed.Commands.SetFilter("zone");
        Check(ed.Commands.Labels.Any(l => l.StartsWith("Zones: draw a zone")), $"palette finds the zone command: {string.Join(" / ", ed.Commands.Labels)}");
        await PressWait(Key.Escape);
        Check(!ed.Commands.Visible, "Esc closes the palette");
        ed.Commands.Open(); ed.Commands.SetFilter("Ground: draw a patch");
        Check(ed.Commands.Run(0) && ed.Shapes.Mode == ShapeState.Drawing, "palette runs 'Ground: draw a patch'");
        ed.Shapes.ShapeEscape();
    }

    // ---- a zone that comes from a rule is edited like any other: one save, one undo step ----
    async Task RuleZoneSave(ShapeCtx c)
    {
        var ed = _editor!;
        ed.SelectTool(EditorTool.Zones);
        await Frame();
        ed.RefreshNow();
        var rule = ed.Zones.List().FirstOrDefault(z => z.FromRule);
        if (rule == null) { GD.Print("info: the map has no zone that comes from a rule; skipped"); return; }
        c.Rule = rule;
        c.Kind = rule.Kind; c.Other = c.Kind == "bigfoot" ? "deer" : "bigfoot";
        c.N0 = ed.Zones.Count(c.Kind); c.Before = c.Disk();
        RuleZoneEdits(c);
        await KindShownAloneAndMassDelete(c);
    }

    void RuleZoneEdits(ShapeCtx c)
    {
        var ed = _editor!;
        var rule = c.Rule; var kind = c.Kind; var other = c.Other; var n0 = c.N0; var before = c.Before;
        var u0 = ed.Edits.History.UndoDepth; var s0 = ed.Edits.SaveCount;
        Check(ed.Zones.Panel.KindRowText(kind) == $"{ed.Zones.KindLabel(kind)}   {n0}", $"the row of {kind} shows every zone of it as one number: '{ed.Zones.Panel.KindRowText(kind)}' ({n0})");
        ed.Shapes.SelectShape(ShapeKind.Zone, rule.Id); ed.RefreshNow();
        Check(ed.Shapes.SelectedZoneId == rule.Id && ed.Zones.SelectedKind == kind && ed.Shapes.Panel.RowSelected(rule.Id), $"a zone from a rule ({rule.Label}) is selectable and listed");
        Check(ed.Shapes.DeleteShape(ShapeKind.Zone, rule.Id) && ed.Edits.SaveCount == s0 + 1 && ed.Edits.History.UndoDepth == u0 + 1, "deleting it is one save, one undo step");
        Check(ed.Zones.Count(kind) == n0 - 1 && ed.Zones.CutCount == 1 && !c.Disk().Contains("\"zones\"") && c.Disk().Contains("zone_cuts"), "the count drops by one; a cut is recorded, no zone is drawn");
        Check(ed.Edits.Undo() && c.Disk() == before && ed.Zones.Count(kind) == n0 && ed.Zones.CutCount == 0, "one undo brings it back exactly");
        Check(ed.Zones.SetKind(rule.Id, other) && ed.Edits.SaveCount == s0 + 3 && ed.Edits.History.UndoDepth == u0 + 1, "changing its kind is one save, one undo step");
        Check(ed.Shapes.SelectedZoneId == "Z-0001" && ed.Zones.Find("Z-0001")!.Kind == other && ed.Zones.Count(kind) == n0 - 1 && ed.Zones.CutCount == 1, "it is now an ordinary zone of the new kind, selected; the old kind lost it");
        Check(ed.Edits.Undo() && c.Disk() == before && ed.Zones.Count(kind) == n0, "one undo brings it back exactly");
        var corners = rule.Points; var mid = (corners.Average(p => p.x), corners.Average(p => p.y));
        var to = (corners[0].x + (mid.Item1 - corners[0].x) * 0.1, corners[0].y + (mid.Item2 - corners[0].y) * 0.1);
        Check(ed.Shapes.MoveShapeCorner(ShapeKind.Zone, rule.Id, 0, to.Item1, to.Item2) && ed.Edits.SaveCount == s0 + 5 && ed.Edits.History.UndoDepth == u0 + 1, $"moving a corner is one save, one undo step: '{ed.Chrome.StatusMessage}'");
        Check(ed.Shapes.SelectedZoneId == "Z-0001" && ed.Zones.Find("Z-0001")!.Points.Count == corners.Count && ed.Zones.Count(kind) == n0, "the moved copy replaces it: same count, selected");
        Check(ed.Edits.Undo() && c.Disk() == before && ed.Zones.Count(kind) == n0, "one undo brings it back exactly");
    }

    // ---- a selected kind is shown alone; marked zones are deleted together: one confirm, one save, one undo step ----
    async Task KindShownAloneAndMassDelete(ShapeCtx c)
    {
        var ed = _editor!;
        var kind = c.Kind; var other = c.Other; var n0 = c.N0; var before = c.Before;
        ed.Shapes.SelectShape(ShapeKind.Zone, null); ed.Zones.SelectKind(kind); ed.RefreshNow(); ed.Zones.Overlay.Now();
        Check(ed.Zones.Overlay.FillAlpha(kind) > 0.5f && ed.Zones.Overlay.FillAlpha(other) == 0f, $"only the selected kind is drawn: {kind} {ed.Zones.Overlay.FillAlpha(kind)}, {other} {ed.Zones.Overlay.FillAlpha(other)}");
        ed.Zones.Overlay.Set(false, false); ed.Zones.Overlay.Now();
        Check(ed.Zones.Drawn && !ed.Zones.Overlay.On, "the selected kind stays shown with the overlay off");
        ed.Zones.Overlay.Set(true, false);
        var two = ed.Zones.List().Where(z => z.Kind == kind).Take(2).Select(z => z.Id).ToList();
        Check(!ed.Zones.Panel.MarkDeleteEnabled && ed.Zones.Marks.Count == 0, "nothing marked: the mass delete is disabled");
        ed.Shapes.Panel.TickRow(two[0], true); if (two.Count > 1) ed.Zones.Marks.Toggle(two[1]); ed.RefreshNow(); ed.Zones.Overlay.Now();
        Check(ed.Zones.Marks.Count == two.Count && two.All(ed.Shapes.Panel.RowMarked) && ed.Zones.Panel.MarkDeleteEnabled && ed.Zones.Panel.MarkDeleteText.Contains($"{two.Count} marked"), $"a row tick and a map mark both count: '{ed.Zones.Panel.MarkDeleteText}'");
        Check(ed.Outlines.SegmentCount(ZoneMarks.Outline) > 0, "marked zones are outlined on the map");
        var s0 = ed.Edits.SaveCount; var u0 = ed.Edits.History.UndoDepth;
        await PressWait(Key.Delete, shift: true);
        Check(ed.Confirm.Visible && ed.Confirm.Text.Contains($"{two.Count} marked") && ed.Edits.SaveCount == s0, $"Shift+Del asks first and names what goes: '{ed.Confirm.Text.Replace('\n', '|')}'");
        ed.Confirm.Accept(); ed.RefreshNow();
        Check(ed.Edits.SaveCount == s0 + 1 && ed.Edits.History.UndoDepth == u0 + 1 && ed.Zones.Count(kind) == n0 - two.Count && ed.Zones.Marks.Count == 0, "the marked zones go in one save, one undo step; the marks are gone");
        Check(ed.Edits.Undo() && c.Disk() == before && ed.Zones.Count(kind) == n0, "one undo brings them all back exactly");
        ed.Zones.Marks.MarkListed(); ed.RefreshNow();
        Check(ed.Zones.Marks.Count == n0, $"Mark all marks every zone of the selected kind ({ed.Zones.Marks.Count}/{n0})");
        ed.Zones.Marks.Clear(); ed.RefreshNow();
        Check(ed.Zones.Marks.Count == 0 && !ed.Zones.Panel.MarkDeleteEnabled, "Unmark all clears the marks; nothing was deleted");
    }
}
