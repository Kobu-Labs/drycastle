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
        using var c = new ShapeCtx(_opts["shape-editor-smoke-plan"], Ed.EditsPath);
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
        GD.Print($"info: undo depth {Ed.Edits.History.UndoDepth}, saves {Ed.Edits.SaveCount}, selected {Ed.Shapes.SelectedPatchId}");
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
        Ed.RefreshNow();
    }

    // one gesture = exactly one save and one undo step; a patch change also waits for the patch-run, a zone change has none
    async Task<bool> Gesture(ShapeCtx c, string name, Func<bool> act, string step, bool patchRun, Func<Task>? driver = null)
    {
        var s0 = Ed.Edits.SaveCount; var u0 = Ed.Edits.History.UndoDepth; var a0 = _watcher!.AppliedCount;
        if (driver != null) await driver(); // key events arrive a frame later
        var ok = act();
        Check(ok && Ed.Edits.SaveCount == s0 + 1 && Ed.Edits.History.UndoDepth == u0 + 1, $"[{name}] one save, one undo step (saves {Ed.Edits.SaveCount - s0}, undo depth {Ed.Edits.History.UndoDepth - u0}), status '{Ed.Chrome.StatusMessage}'");
        ShapeFileIs(c, step, name);
        if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
        await Settle(patchRun);
        return ok;
    }

    bool Refused(ShapeCtx c, string name, Func<bool> act, string step)
    {
        var s0 = Ed.Edits.SaveCount; var u0 = Ed.Edits.History.UndoDepth;
        var before = c.Disk();
        var ok = act();
        Check(!ok && Ed.Edits.SaveCount == s0 && Ed.Edits.History.UndoDepth == u0 && c.Disk() == before && c.Disk() == c.Expect(step), $"[{name}] refused: nothing saved, file unchanged, status '{Ed.Chrome.StatusMessage}'");
        return !ok;
    }

    void FirstFreeIds(ShapeCtx c)
    {
        Check(Ed.Shapes.NextShapeIdOf(c.Disk(), ShapeKind.Patch) == "GP-0002", "next free patch ID after GP-0001 is GP-0002");
        Check(Ed.Shapes.NextShapeIdOf(c.Disk(), ShapeKind.Zone) == "Z-0001", "next free zone ID with no zones is Z-0001");
    }

    async Task OpenGroundTool()
    {
        await Physics(3);
        Ed.SetActive(true);
        Ed.SelectTool(EditorTool.Ground);
        await Frame();
        Ed.RefreshNow();
    }

    // ---- ground tool: idle, list, empty selection ----
    async Task GroundToolIdle()
    {
        Check(Ed.Tool == EditorTool.Ground && Ed.Shapes.Mode == ShapeState.Idle, "Ground tool opens with no gesture under way");
        Check(Ed.Shapes.Panel.RowCount == 1 && !Ed.Shapes.Panel.EmptyShown && !Ed.Shapes.Panel.RowSelected("GP-0001"), "panel lists the seeded GP-0001");
        Check(Ed.ShapeHint.Contains("draws a patch") && Ed.Shapes.Panel.State == "No patch selected", $"idle hint and state: '{Ed.ShapeHint}'");
        await PressWait(Key.N);
        Check(Ed.Shapes.Mode == ShapeState.Idle && Ed.Chrome.StatusMessage.Contains("pick a ground type"), $"N without a selected type is refused with the way out: '{Ed.Chrome.StatusMessage}'");
        Check(Ed.Ground.SelectType("paint_336699"), "a type is selected in the list");
    }

    // ---- drawing: refusals before any save ----
    async Task DrawingRefusals(ShapeCtx c)
    {
        var q = c.Q;
        await PressWait(Key.N);
        Check(Ed.Shapes.Mode == ShapeState.Drawing && Ed.ShapeHint.Contains("new patch"), "N starts drawing (hint says so)");
        Check(Ed.Shapes.AddShapePoint(q[0][0], q[0][1]) && Ed.Shapes.AddShapePoint(q[1][0], q[1][1]), "two corners placed");
        Ed.RefreshNow();
        Check(Ed.Shapes.Panel.Validation.Contains("at least 3") && Ed.Shapes.ShapeDraftProblem() != null, $"inline validation before commit: '{Ed.Shapes.Panel.Validation}'");
        Refused(c, "close with 2 corners", () => Ed.Shapes.CloseShape(), "seed");
        Check(!Ed.Shapes.AddShapePoint(q[1][0], q[1][1]), "the same corner twice is refused");
        Ed.Shapes.RemoveLastShapePoint(); Ed.Shapes.RemoveLastShapePoint();
        var bow = c.D("bow");
        // bow tie: the 4th corner's edge would cross the first edge
        Check(Ed.Shapes.AddShapePoint(bow[0], bow[1]) && Ed.Shapes.AddShapePoint(bow[0] + 10, bow[1] + 10) && Ed.Shapes.AddShapePoint(bow[0] + 10, bow[1]), "bow tie: 3 corners placed");
        Check(!Ed.Shapes.AddShapePoint(bow[0], bow[1] + 10) && Ed.Shapes.ShapeDraft.Count == 3, $"self-crossing corner refused: '{Ed.Chrome.StatusMessage}'");
        Ed.Shapes.ShapeEscape(); // Esc cancels drawing
        Check(Ed.Shapes.Mode == ShapeState.Idle && Ed.Shapes.ShapeDraft.Count == 0 && Ed.Chrome.StatusMessage.Contains("nothing was saved"), "Esc cancels drawing, nothing saved");
        // collinear: closes to a shape with no area
        await PressWait(Key.N);
        foreach (var dx in new[] { 0.0, 5.0, 10.0 }) Ed.Shapes.AddShapePoint(bow[0] + dx, bow[1]);
        Refused(c, "close a flat (no area) shape", () => Ed.Shapes.CloseShape(), "seed");
        Check(Ed.Shapes.ShapeDraftProblem()!.Contains("crosses or touches itself, or has no area"), $"validation names the problem: '{Ed.Shapes.ShapeDraftProblem()}'");
        Ed.Shapes.ShapeEscape();
        Check(Ed.Shapes.Mode == ShapeState.Idle, "Esc cancelled the flat shape");
    }

    // ---- draw the quad and create it with Enter (a land type: no extra step) ----
    async Task CreatePatchWithEnter(ShapeCtx c)
    {
        await PressWait(Key.N);
        foreach (var p in c.Q) Ed.Shapes.AddShapePoint(p[0], p[1]);
        Check(Ed.Shapes.ShapeDraft.Count == 4 && Ed.Shapes.ShapeDraftProblem() == null, "quad placed and valid");
        Ed.RefreshNow();
        Check(Ed.Shapes.Panel.Validation.StartsWith("Shape is valid") && Ed.Shapes.Panel.Validation.Contains("Not saved"), $"panel says valid and unsaved: '{Ed.Shapes.Panel.Validation}'");
        Check(Ed.Shapes.ShapeDraftPending, "unsaved marker while drawing");
        Ed.Shapes.World.Redraw();
        Check(Ed.Outlines.SegmentCount(ShapeWorld.OutlineDraft) == 3 && Ed.Shapes.World.HandleCount == 4, $"live preview: {Ed.Outlines.SegmentCount(ShapeWorld.OutlineDraft)} edges, {Ed.Shapes.World.HandleCount} corner handles");
        Check(c.Disk() == c.Expect("seed"), "nothing saved while drawing");
        var saves0 = Ed.Edits.SaveCount;
        await Gesture(c, "create GP-0002 (Enter)", () => Ed.Shapes.Mode == ShapeState.Selected, "p_created", true, () => PressWait(Key.Enter));
        Check(Ed.Shapes.SelectedPatchId == "GP-0002" && Ed.Ground.SelectedType == "paint_336699" && Ed.Shapes.ShapeDraft.Count == 0, "new patch is selected (and its type), draft cleared");
        Check(Ed.Edits.SaveCount == saves0 + 1, "creating saved exactly once");
        Check(_map.DynamicPatches.ContainsKey("GP-0002"), "GP-0002 reached the map through the watcher (patch-run)");
        Check(Ed.Shapes.Panel.RowCount == 2 && Ed.Shapes.Panel.RowSelected("GP-0002"), "list shows both patches, GP-0002 selected");
        Check(Ed.Ground.PatchPage.TypeShown == "paint_336699", "the form shows the patch's type");
    }

    // ---- retype (one gesture) ----
    async Task RetypePatch(ShapeCtx c)
    {
        await Gesture(c, "retype GP-0002", () => Ed.Shapes.SetPatchType("GP-0002", "paint_99cc33"), "p_retyped", true);
        Check(DynamicColour("GP-0002") is { } dc && SameColour(dc, "#99cc33"), "retyped patch is regenerated in the new type's colour");
        Check(Ed.Shapes.SetPatchType("GP-0002", "paint_99cc33") && Ed.Edits.History.UndoDepth == 2, "the same type again saves nothing");
        Check(!Ed.Shapes.SetPatchType("GP-0002", "no_such_type") && Ed.Edits.History.UndoDepth == 2, "an unknown type is refused");
    }

    // ---- corner drag: preview, refused crossing, valid move ----
    async Task CornerDrag(ShapeCtx c)
    {
        var inv = c.D("invalid_to"); var mv = c.D("move_to");
        Check(Ed.Shapes.StartCornerDrag(2) && Ed.Shapes.Mode == ShapeState.Dragging && Ed.ShapeHint.Contains("moving a corner"), "drag starts on corner 3");
        Ed.Shapes.DragCornerToAbs(inv[0], inv[1]);
        Ed.Shapes.World.Redraw();
        Check(Ed.Outlines.SegmentCount(ShapeWorld.OutlineBad) > 0 && Ed.Outlines.SegmentCount(ShapeWorld.OutlineDrag) == 0, "crossing drag previews in the refused colour");
        Check(c.Disk() == c.Expect("p_retyped"), "file untouched during the drag");
        Refused(c, "drag corner into a crossing", () => Ed.Shapes.EndCornerDrag(), "p_retyped");
        Ed.Shapes.StartCornerDrag(1);
        Ed.Shapes.DragCornerToAbs(mv[0], mv[1]);
        Ed.Shapes.World.Redraw();
        Check(Ed.Outlines.SegmentCount(ShapeWorld.OutlineDrag) == 4 && Ed.Outlines.SegmentCount(ShapeWorld.OutlineBad) == 0, "valid drag previews live");
        await PressWait(Key.Escape);
        Check(Ed.Shapes.Mode == ShapeState.Selected && c.Disk() == c.Expect("p_retyped"), "Esc cancels the drag, nothing saved");
        Ed.Shapes.StartCornerDrag(1);
        Ed.Shapes.DragCornerToAbs(mv[0], mv[1]);
        await Gesture(c, "drag corner 2", () => Ed.Shapes.EndCornerDrag(), "p_moved", true);
    }

    // ---- a patch of a water type asks for its water level before it is created ----
    async Task WaterPatch(ShapeCtx c)
    {
        var wq = new[] { c.D("w0"), c.D("w1"), c.D("w2"), c.D("w3") };
        Check(Ed.Ground.SelectType("pond"), "the water type is selected");
        await PressWait(Key.N);
        foreach (var p in wq) Ed.Shapes.AddShapePoint(p[0], p[1]);
        await PressWait(Key.Enter);
        Check(Ed.Shapes.Mode == ShapeState.Level && Ed.Ground.PatchPage.WaterRowShown && Ed.Shapes.Panel.CreateButtonShown, "Enter on a water patch asks for the water level: nothing is created yet");
        Check(c.Disk() == c.Expect("p_moved"), "nothing saved while the water level is asked for");
        Ed.Shapes.World.Redraw();
        Check(Ed.Outlines.SegmentCount(ShapeWorld.OutlineDraft) == 4, "closing edge is part of the preview");
        await PressWait(Key.Escape);
        Check(Ed.Shapes.Mode == ShapeState.Drawing && Ed.Shapes.ShapeDraft.Count == 4, "Esc in the water level step goes back to the corners");
        await PressWait(Key.Enter);
        Ed.Shapes.SetDrawWaterLevel(1.5);
        await Gesture(c, "create the water patch GP-0003 (Enter)", () => Ed.Shapes.Mode == ShapeState.Selected, "p_water", true, () => PressWait(Key.Enter));
        Check(Ed.Shapes.SelectedPatchId == "GP-0003" && Ed.Shapes.Patch("GP-0003")!.WaterY == 1.5, "the water patch carries its water level");
        Check(!Ed.Shapes.SetPatchWater("GP-0002", 2.0), "a land patch has no water level");
        Check(!Ed.Ground.Ops.SetCarried("pond", "water", false) && Ed.Edits.History.UndoDepth == 4, $"unticking water on a type a patch uses is refused: '{Ed.Chrome.StatusMessage}'");
        Ed.Ground.Ops.RequestDelete();
        Check(!Ed.Confirm.Visible && Ed.Chrome.StatusMessage.Contains("patch"), $"a type a patch uses is not offered for deletion (blocked with the reason, no confirm): '{Ed.Chrome.StatusMessage}'");
    }

    // ---- delete: confirm names the patch; cancel keeps it ----
    async Task DeletePatchWithConfirm(ShapeCtx c)
    {
        await PressWait(Key.Delete);
        Check(Ed.Confirm.Visible && Ed.Confirm.Text.Contains("GP-0003"), $"Del asks first and names the patch: '{Ed.Confirm.Text.Replace('\n', '|')}'");
        await PressWait(Key.Escape);
        Check(!Ed.Confirm.Visible && Ed.Shapes.Patch("GP-0003") != null && c.Disk() == c.Expect("p_water"), "Esc on the confirm cancels: patch kept");
        await Gesture(c, "delete GP-0003", () => { Ed.Confirm.Accept(); return !Ed.Confirm.Visible && Ed.Shapes.Patch("GP-0003") == null; }, "p_moved", true, () => PressWait(Key.Delete));
        Check(Ed.Shapes.SelectedPatchId == null && Ed.Shapes.Panel.RowCount == 2, "deleted patch leaves the selection and the list");
    }

    // ---- zones tool ----
    async Task ZonesToolIdle()
    {
        Ed.SelectTool(EditorTool.Zones);
        await Frame();
        Ed.RefreshNow();
        Ed.Zones.SelectKind(null); Ed.Shapes.SelectShape(ShapeKind.Zone, null); Ed.RefreshNow(); // the saved editor_ui.cfg of this machine may have restored a kind
        Check(Ed.Tool == EditorTool.Zones && !Ed.Zones.Panel.ErrorShown && Ed.Zones.Panel.KindRowCount > 0, $"Zones tool lists the map's zone kinds ({Ed.Zones.Panel.KindRowCount}); the map has zone_kinds");
        Check(Ed.Zones.List().All(z => z.FromRule) && Ed.Shapes.Panel.State == "No zone selected", "nothing drawn yet: every zone on the map comes from a rule, none is selected");
        var deerRow = Ed.Zones.Panel.KindRowText("deer");
        Check(!deerRow.Contains("drawn") && !deerRow.Contains("rule"), $"a kind row has one number, no drawn/rule wording: '{deerRow}'");
        Check(Ed.ShapeHint.Contains("zones:"), $"zones idle hint: '{Ed.ShapeHint}'");
        await PressWait(Key.N);
        Check(Ed.Shapes.Mode == ShapeState.Idle && Ed.Chrome.StatusMessage.Contains("pick a zone kind"), $"N without a kind is refused with the way out: '{Ed.Chrome.StatusMessage}'");
        Ed.Zones.Panel.PressKindRow("bigfoot"); Ed.RefreshNow();
        Check(Ed.Zones.SelectedKind == "bigfoot", "clicking a kind row selects it");
    }

    async Task CreateAndRekindZone(ShapeCtx c)
    {
        var zq = new[] { c.D("z0"), c.D("z1"), c.D("z2"), c.D("z3") };
        await PressWait(Key.N);
        foreach (var p in zq) Ed.Shapes.AddShapePoint(p[0], p[1]);
        Check(Ed.ShapeHint.Contains("Enter creates"), $"a zone is created by Enter (no extra step): '{Ed.ShapeHint}'");
        await Gesture(c, "create Z-0001 (Enter)", () => Ed.Shapes.SelectedZoneId == "Z-0001", "z_created", false, () => PressWait(Key.Enter));
        Check(Ed.Shapes.Panel.RowCount == 1 && Ed.Shapes.Panel.RowSelected("Z-0001") && Ed.Zones.Panel.KindShown == "bigfoot", "the zone is listed, selected, and its form shows its kind");
        await Gesture(c, "change Z-0001 to deer", () => Ed.Zones.SetKind("Z-0001", "deer"), "z_rekinded", false);
        Check(Ed.Zones.SelectedKind == "deer", "the kind selection follows the zone");
        Check(!Ed.Zones.SetKind("Z-0001", "no_such_kind") && Ed.Edits.History.UndoDepth == 7, "an unknown kind is refused");
        Check(Ed.Shapes.ShapeAt(ShapeKind.Zone, c.Plan.GetProperty("inside_z")[0].GetDouble(), c.Plan.GetProperty("inside_z")[1].GetDouble()) == "Z-0001", "hit test finds Z-0001");
    }

    async Task ZoneOverlayKey()
    {
        Ed.Zones.Overlay.Now();
        Check(Ed.Zones.Overlay.On, "the overlay is on by default");
        await PressWait(Key.M);
        Check(!Ed.Zones.Overlay.On, "M hides the zone overlay");
        await PressWait(Key.M);
        Check(Ed.Zones.Overlay.On, "M shows it again");
    }

    async Task DeleteZone(ShapeCtx c)
    {
        await PressWait(Key.Delete);
        Check(Ed.Confirm.Visible && Ed.Confirm.Text.Contains("Z-0001"), $"Del asks first and names the zone: '{Ed.Confirm.Text.Replace('\n', '|')}'");
        await Gesture(c, "delete Z-0001", () => { Ed.Confirm.Accept(); return Ed.Zones.Find("Z-0001") == null; }, "p_moved", false);
        Check(Ed.Shapes.SelectedZoneId == null && Ed.Zones.Find("Z-0001") == null, "deleted zone leaves the selection (and the zones key is gone, as dump_text)");
    }

    // ---- undo / redo of every step, byte for byte ----
    async Task UndoRedoEverything(ShapeCtx c)
    {
        var chain = new[] { ("z_rekinded", false), ("z_created", false), ("p_moved", false), ("p_water", true), ("p_moved", true), ("p_retyped", true), ("p_created", true), ("seed", true) };
        Check(Ed.Edits.History.UndoDepth == 8 && Ed.Edits.History.NextUndoDescription!.Contains("deleted Z-0001"), $"undo stack: {Ed.Edits.History.UndoDepth} steps, next '{Ed.Edits.History.NextUndoDescription}'");
        await Walk(c, "undo", true, chain);
        Check(Ed.Edits.History.UndoDepth == 0 && Ed.Edits.History.RedoDepth == 8 && !_map.DynamicPatches.ContainsKey("GP-0002"), "everything undone: GP-0002 gone from the map");
        await Walk(c, "redo", false, new[] { ("p_created", true), ("p_retyped", true), ("p_moved", true), ("p_water", true), ("p_moved", true), ("z_created", false), ("z_rekinded", false), ("p_moved", false) });
        Check(Ed.Edits.History.RedoDepth == 0 && Ed.Edits.History.UndoDepth == 8, "everything redone");
    }

    async Task Walk(ShapeCtx c, string what, bool undo, (string state, bool patchRun)[] states)
    {
        foreach (var (st, patchRun) in states)
        {
            var a0 = _watcher!.AppliedCount;
            var ok = undo ? Ed.Edits.Undo() : Ed.Edits.Redo();
            Check(ok, $"{what} -> {st}: status '{Ed.Chrome.StatusMessage}'");
            ShapeFileIs(c, st, $"{what} {st}");
            if (patchRun) await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
            await Settle(patchRun);
        }
    }

    // ---- Esc, one level at a time ----
    async Task EscOneLevelAtATime(ShapeCtx c)
    {
        var q = c.Q;
        Ed.SelectTool(EditorTool.Ground);
        Ed.Shapes.SelectShape(ShapeKind.Patch, "GP-0001");
        await PressWait(Key.Escape);
        Check(Ed.Shapes.SelectedPatchId == null && Ed.Ground.SelectedType == "paint_aa3322" && Ed.Tool == EditorTool.Ground, "Esc deselects the patch, the type and the tool stay");
        await PressWait(Key.Escape);
        Check(Ed.Ground.SelectedType == null && Ed.Tool == EditorTool.Ground, "Esc again deselects the type");
        await PressWait(Key.Escape);
        Check(Ed.Tool == EditorTool.None, "Esc again leaves the tool");
        Ed.SelectTool(EditorTool.Ground);
        Ed.Ground.SelectType("paint_aa3322");
        Ed.Shapes.StartShape();
        Ed.Shapes.AddShapePoint(q[0][0], q[0][1]);
        await PressWait(Key.Backspace);
        Check(Ed.Shapes.ShapeDraft.Count == 0, "Backspace takes the last corner back");
        Ed.Shapes.AddShapePoint(q[0][0], q[0][1]);
        Ed.SelectTool(EditorTool.View);
        Check(Ed.Shapes.ShapeDraft.Count == 0 && Ed.Chrome.StatusMessage.Contains("never saved"), $"switching tool names the discarded shape: '{Ed.Chrome.StatusMessage}'");
        Ed.SelectTool(EditorTool.Ground);
        Check(Ed.Shapes.ShapeAt(ShapeKind.Patch, c.Plan.GetProperty("inside_p1")[0].GetDouble(), c.Plan.GetProperty("inside_p1")[1].GetDouble()) == "GP-0001", "hit test finds GP-0001");
        Ed.Shapes.Panel.PressRow("GP-0001"); Ed.RefreshNow();
        Check(Ed.Shapes.SelectedPatchId == "GP-0001" && Ed.Shapes.Panel.DeleteButtonShown, "clicking the list row selects it");
    }

    // ---- palette ----
    async Task Palette()
    {
        await PressWait(Key.P, ctrl: true);
        Check(Ed.Commands.Visible, "Ctrl+P opens the palette");
        Ed.Commands.SetFilter("patch");
        Check(Ed.Commands.Labels.Any(l => l.StartsWith("Ground: draw a patch")) && Ed.Commands.Labels.Any(l => l.StartsWith("GP-0001")), $"palette finds the patch command and GP-0001: {string.Join(" / ", Ed.Commands.Labels)}");
        Ed.Commands.SetFilter("zone");
        Check(Ed.Commands.Labels.Any(l => l.StartsWith("Zones: draw a zone")), $"palette finds the zone command: {string.Join(" / ", Ed.Commands.Labels)}");
        await PressWait(Key.Escape);
        Check(!Ed.Commands.Visible, "Esc closes the palette");
        Ed.Commands.Open(); Ed.Commands.SetFilter("Ground: draw a patch");
        Check(Ed.Commands.Run(0) && Ed.Shapes.Mode == ShapeState.Drawing, "palette runs 'Ground: draw a patch'");
        Ed.Shapes.ShapeEscape();
    }

    // ---- a zone that comes from a rule is edited like any other: one save, one undo step ----
    async Task RuleZoneSave(ShapeCtx c)
    {
        Ed.SelectTool(EditorTool.Zones);
        await Frame();
        Ed.RefreshNow();
        var rule = Ed.Zones.List().FirstOrDefault(z => z.FromRule);
        if (rule == null) { GD.Print("info: the map has no zone that comes from a rule; skipped"); return; }
        c.Rule = rule;
        c.Kind = rule.Kind; c.Other = c.Kind == "bigfoot" ? "deer" : "bigfoot";
        c.N0 = Ed.Zones.Count(c.Kind); c.Before = c.Disk();
        RuleZoneEdits(c);
        await KindShownAloneAndMassDelete(c);
    }

    void RuleZoneEdits(ShapeCtx c)
    {
        var rule = c.Rule; var kind = c.Kind; var other = c.Other; var n0 = c.N0; var before = c.Before;
        var u0 = Ed.Edits.History.UndoDepth; var s0 = Ed.Edits.SaveCount;
        Check(Ed.Zones.Panel.KindRowText(kind) == $"{Ed.Zones.KindLabel(kind)}   {n0}", $"the row of {kind} shows every zone of it as one number: '{Ed.Zones.Panel.KindRowText(kind)}' ({n0})");
        Ed.Shapes.SelectShape(ShapeKind.Zone, rule.Id); Ed.RefreshNow();
        Check(Ed.Shapes.SelectedZoneId == rule.Id && Ed.Zones.SelectedKind == kind && Ed.Shapes.Panel.RowSelected(rule.Id), $"a zone from a rule ({rule.Label}) is selectable and listed");
        Check(Ed.Shapes.DeleteShape(ShapeKind.Zone, rule.Id) && Ed.Edits.SaveCount == s0 + 1 && Ed.Edits.History.UndoDepth == u0 + 1, "deleting it is one save, one undo step");
        Check(Ed.Zones.Count(kind) == n0 - 1 && Ed.Zones.CutCount == 1 && !c.Disk().Contains("\"zones\"") && c.Disk().Contains("zone_cuts"), "the count drops by one; a cut is recorded, no zone is drawn");
        Check(Ed.Edits.Undo() && c.Disk() == before && Ed.Zones.Count(kind) == n0 && Ed.Zones.CutCount == 0, "one undo brings it back exactly");
        Check(Ed.Zones.SetKind(rule.Id, other) && Ed.Edits.SaveCount == s0 + 3 && Ed.Edits.History.UndoDepth == u0 + 1, "changing its kind is one save, one undo step");
        Check(Ed.Shapes.SelectedZoneId == "Z-0001" && Ed.Zones.Find("Z-0001")!.Kind == other && Ed.Zones.Count(kind) == n0 - 1 && Ed.Zones.CutCount == 1, "it is now an ordinary zone of the new kind, selected; the old kind lost it");
        Check(Ed.Edits.Undo() && c.Disk() == before && Ed.Zones.Count(kind) == n0, "one undo brings it back exactly");
        var corners = rule.Points; var mid = (corners.Average(p => p.x), corners.Average(p => p.y));
        var to = (corners[0].x + (mid.Item1 - corners[0].x) * 0.1, corners[0].y + (mid.Item2 - corners[0].y) * 0.1);
        Check(Ed.Shapes.MoveShapeCorner(ShapeKind.Zone, rule.Id, 0, to.Item1, to.Item2) && Ed.Edits.SaveCount == s0 + 5 && Ed.Edits.History.UndoDepth == u0 + 1, $"moving a corner is one save, one undo step: '{Ed.Chrome.StatusMessage}'");
        Check(Ed.Shapes.SelectedZoneId == "Z-0001" && Ed.Zones.Find("Z-0001")!.Points.Count == corners.Count && Ed.Zones.Count(kind) == n0, "the moved copy replaces it: same count, selected");
        Check(Ed.Edits.Undo() && c.Disk() == before && Ed.Zones.Count(kind) == n0, "one undo brings it back exactly");
    }

    // ---- a selected kind is shown alone; marked zones are deleted together: one confirm, one save, one undo step ----
    async Task KindShownAloneAndMassDelete(ShapeCtx c)
    {
        var kind = c.Kind; var other = c.Other; var n0 = c.N0; var before = c.Before;
        Ed.Shapes.SelectShape(ShapeKind.Zone, null); Ed.Zones.SelectKind(kind); Ed.RefreshNow(); Ed.Zones.Overlay.Now();
        Check(Ed.Zones.Overlay.FillAlpha(kind) > 0.5f && Ed.Zones.Overlay.FillAlpha(other) == 0f, $"only the selected kind is drawn: {kind} {Ed.Zones.Overlay.FillAlpha(kind)}, {other} {Ed.Zones.Overlay.FillAlpha(other)}");
        Ed.Zones.Overlay.Set(false, false); Ed.Zones.Overlay.Now();
        Check(Ed.Zones.Drawn && !Ed.Zones.Overlay.On, "the selected kind stays shown with the overlay off");
        Ed.Zones.Overlay.Set(true, false);
        var two = Ed.Zones.List().Where(z => z.Kind == kind).Take(2).Select(z => z.Id).ToList();
        Check(!Ed.Zones.Panel.MarkDeleteEnabled && Ed.Zones.Marks.Count == 0, "nothing marked: the mass delete is disabled");
        Ed.Shapes.Panel.TickRow(two[0], true); if (two.Count > 1) Ed.Zones.Marks.Toggle(two[1]); Ed.RefreshNow(); Ed.Zones.Overlay.Now();
        Check(Ed.Zones.Marks.Count == two.Count && two.All(Ed.Shapes.Panel.RowMarked) && Ed.Zones.Panel.MarkDeleteEnabled && Ed.Zones.Panel.MarkDeleteText.Contains($"{two.Count} marked"), $"a row tick and a map mark both count: '{Ed.Zones.Panel.MarkDeleteText}'");
        Check(Ed.Outlines.SegmentCount(ZoneMarks.Outline) > 0, "marked zones are outlined on the map");
        var s0 = Ed.Edits.SaveCount; var u0 = Ed.Edits.History.UndoDepth;
        await PressWait(Key.Delete, shift: true);
        Check(Ed.Confirm.Visible && Ed.Confirm.Text.Contains($"{two.Count} marked") && Ed.Edits.SaveCount == s0, $"Shift+Del asks first and names what goes: '{Ed.Confirm.Text.Replace('\n', '|')}'");
        Ed.Confirm.Accept(); Ed.RefreshNow();
        Check(Ed.Edits.SaveCount == s0 + 1 && Ed.Edits.History.UndoDepth == u0 + 1 && Ed.Zones.Count(kind) == n0 - two.Count && Ed.Zones.Marks.Count == 0, "the marked zones go in one save, one undo step; the marks are gone");
        Check(Ed.Edits.Undo() && c.Disk() == before && Ed.Zones.Count(kind) == n0, "one undo brings them all back exactly");
        Ed.Zones.Marks.MarkListed(); Ed.RefreshNow();
        Check(Ed.Zones.Marks.Count == n0, $"Mark all marks every zone of the selected kind ({Ed.Zones.Marks.Count}/{n0})");
        Ed.Zones.Marks.Clear(); Ed.RefreshNow();
        Check(Ed.Zones.Marks.Count == 0 && !Ed.Zones.Panel.MarkDeleteEnabled, "Unmark all clears the marks; nothing was deleted");
    }
}
