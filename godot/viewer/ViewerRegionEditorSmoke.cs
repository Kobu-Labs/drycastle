using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--region-editor-smoke --region-editor-smoke-plan plan.json` (run by region_editor_smoke.py, `make viewer-region-editor-smoke`):
/// drives the Region tool of the editor headless through its API and key events: draw, close, colour, corner drag (valid and
/// self-crossing), recolour, delete with confirm, undo/redo of every step, Esc one level at a time, the palette. After every
/// save the exact text of edits.json is compared with the text the Python writer produces for the same data (the plan), and
/// the saved region reaches the map through the watcher (region-run). Steps are copied to the plan's steps folder.
/// </summary>
public partial class Viewer
{
    async Task RunRegionEditorSmoke()
    {
        try { await RegionEditorSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL region editor smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "REGION EDITOR SMOKE PASS" : "REGION EDITOR SMOKE FAIL");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    async Task RegionEditorSmokeBody()
    {
        var ed = _editor!;
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["region-editor-smoke-plan"]));
        var plan = planDoc.RootElement;
        var editsPath = ed.EditsPath;
        var stepsDir = plan.GetProperty("steps_dir").GetString()!;
        Directory.CreateDirectory(stepsDir);
        string Expect(string k) => File.ReadAllText(plan.GetProperty("expect").GetProperty(k).GetString()!);
        double[] Pt(string k) => plan.GetProperty(k).EnumerateArray().Select(x => x.GetDouble()).ToArray();
        string Disk() => File.ReadAllText(editsPath);
        async Task Frame(int n = 2) { for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Key_(Key k, bool ctrl = false)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = k, Pressed = true, CtrlPressed = ctrl });
            await Frame();
        }
        void FileIs(string step, string what)
        {
            var now = Disk(); var want = Expect(step);
            Check(now == want, $"{what}: edits.json is exactly the expected text" + (now == want ? "" : $"\n--- got\n{now}--- want\n{want}"));
            File.Copy(editsPath, Path.Combine(stepsDir, step + ".json"), true);
        }
        async Task Settle()
        {
            await WaitUntil(() => !_watcher!.Busy, 90);
            await Physics(3);
            ed.RefreshNow();
        }
        // one gesture = exactly one save and one undo step
        async Task<bool> Gesture(string name, Func<bool> act, string step, Func<Task>? driver = null)
        {
            var s0 = ed.SaveCount; var u0 = ed.UndoDepth; var a0 = _watcher!.AppliedCount;
            if (driver != null) await driver(); // key events arrive a frame later
            var ok = act();
            Check(ok && ed.SaveCount == s0 + 1 && ed.UndoDepth == u0 + 1, $"[{name}] one save, one undo step (saves {ed.SaveCount - s0}, undo depth {ed.UndoDepth - u0}), status '{ed.StatusMessage}'");
            FileIs(step, name);
            await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
            await Settle();
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
        Check(ed.NextRegionIdOf(Disk()) == "R-0002", "next free ID after R-0001 is R-0002");
        await Physics(3);
        ed.SetActive(true);
        ed.SelectTool(EditorTool.Region);
        await Frame();
        ed.RefreshNow();

        // ---- states: idle, panel, empty/list ----
        Check(ed.Tool == EditorTool.Region && ed.RegionMode == EditorMode.RegionState.Idle, "Region tool opens in Idle");
        Check(ed.RegionRowCount == 1 && !ed.RegionEmptyShown && ed.RegionRowSelected("R-0001") == false, "panel lists the seeded R-0001 with a swatch row");
        Check(ed.RegionHint.Contains("click one to select") && ed.RegionPanelState == "Nothing selected", $"idle hint and state: '{ed.RegionHint}'");

        // ---- drawing: refusals before any save ----
        var q = new[] { Pt("q0"), Pt("q1"), Pt("q2"), Pt("q3") };
        await Key_(Key.N);
        Check(ed.RegionMode == EditorMode.RegionState.Drawing && ed.RegionHint.Contains("new region"), "N starts drawing (hint says so)");
        Check(ed.AddRegionPoint(q[0][0], q[0][1]) && ed.AddRegionPoint(q[1][0], q[1][1]), "two corners placed");
        ed.RefreshNow();
        Check(ed.RegionPanelValidation.Contains("at least 3") && ed.RegionDraftProblem() != null, $"inline validation before commit: '{ed.RegionPanelValidation}'");
        await Refused("close with 2 corners", () => ed.CloseRegion(), "seed");
        Check(!ed.AddRegionPoint(q[1][0], q[1][1]), "the same corner twice is refused");
        ed.RemoveLastRegionPoint(); ed.RemoveLastRegionPoint();
        var bow = Pt("bow");
        // bow tie: the 4th corner's edge would cross the first edge
        Check(ed.AddRegionPoint(0 + bow[0], 0 + bow[1]) && ed.AddRegionPoint(bow[0] + 10, bow[1] + 10) && ed.AddRegionPoint(bow[0] + 10, bow[1]), "bow tie: 3 corners placed");
        Check(!ed.AddRegionPoint(bow[0], bow[1] + 10) && ed.RegionDraft.Count == 3, $"self-crossing corner refused: '{ed.StatusMessage}'");
        ed.RegionEscape(); // Esc cancels drawing
        Check(ed.RegionMode == EditorMode.RegionState.Idle && ed.RegionDraft.Count == 0 && ed.StatusMessage.Contains("nothing was saved"), "Esc cancels drawing, nothing saved");
        // collinear: closes to a shape with no area
        await Key_(Key.N);
        foreach (var dx in new[] { 0.0, 5.0, 10.0 }) ed.AddRegionPoint(bow[0] + dx, bow[1]);
        await Refused("close a flat (no area) shape", () => ed.CloseRegion(), "seed");
        Check(ed.RegionDraftProblem()!.Contains("crosses or touches itself, or has no area"), $"validation names the problem: '{ed.RegionDraftProblem()}'");
        ed.RegionEscape();
        ed.RegionEscape(); // idle: leaves the tool
        Check(ed.Tool == EditorTool.None, "Esc in Idle leaves the Region tool");
        ed.SelectTool(EditorTool.Region);

        // ---- draw the quad, close, back, close, colour, create ----
        await Key_(Key.N);
        foreach (var p in q) ed.AddRegionPoint(p[0], p[1]);
        Check(ed.RegionDraft.Count == 4 && ed.RegionDraftProblem() == null, "quad placed and valid");
        ed.RefreshNow();
        Check(ed.RegionPanelValidation.StartsWith("Shape is valid") && ed.RegionPanelValidation.Contains("Not saved"), $"panel says valid and unsaved: '{ed.RegionPanelValidation}'");
        Check(ed.RegionDraftPending, "unsaved marker while drawing");
        ed.OutlineSegmentsNow();
        Check(ed.SegmentCount(EditorMode.OutlineRegionDraft) == 3 && ed.RegionHandleCount == 4, $"live preview: {ed.SegmentCount(EditorMode.OutlineRegionDraft)} edges, {ed.RegionHandleCount} corner handles");
        await Key_(Key.Enter);
        Check(ed.RegionMode == EditorMode.RegionState.Colouring && ed.RegionCreateButtonShown, "Enter closes the shape: colour step");
        ed.OutlineSegmentsNow();
        Check(ed.SegmentCount(EditorMode.OutlineRegionDraft) == 4, "closing edge is part of the preview");
        await Key_(Key.Escape);
        Check(ed.RegionMode == EditorMode.RegionState.Drawing && ed.RegionDraft.Count == 4, "Esc in the colour step goes back to the corners");
        await Key_(Key.Enter);
        ed.SetNewRegionColour("#336699");
        Check(!ed.RegionHexApply("#12345") && ed.RegionPanelValidation.Contains("#rrggbb"), $"bad hex refused inline: '{ed.RegionPanelValidation}'");
        Check(Disk() == Expect("seed"), "nothing saved while drawing and colouring");
        var saves0 = ed.SaveCount;
        await Gesture("create R-0002 (Enter in the colour step)", () => ed.RegionMode == EditorMode.RegionState.Selected, "created", () => Key_(Key.Enter));
        Check(ed.SelectedRegionId == "R-0002" && ed.RegionMode == EditorMode.RegionState.Selected && ed.RegionDraft.Count == 0, "new region is selected, draft cleared");
        Check(ed.SaveCount == saves0 + 1, "creating saved exactly once");
        Check(_map.DynamicRegions.ContainsKey("R-0002"), "R-0002 reached the map through the watcher (region-run)");
        Check(ed.RegionRowCount == 2 && ed.RegionRowSelected("R-0002"), "list shows both regions, R-0002 selected");

        // ---- recolour (one gesture) ----
        await Gesture("recolour R-0002", () => ed.SetRegionColour("R-0002", "#99CC33"), "recoloured");
        Check(DynamicColour("R-0002") is { } dc && SameColour(dc, "#99cc33"), "recoloured region is regenerated in the new colour");
        Check(ed.SetRegionColour("R-0002", "#99cc33") && ed.UndoDepth == 2, "same colour again saves nothing");

        // ---- corner drag: preview, refused crossing, valid move ----
        var inv = Pt("invalid_to"); var mv = Pt("move_to");
        Check(ed.StartCornerDrag(2) && ed.RegionMode == EditorMode.RegionState.Dragging && ed.RegionHint.Contains("moving a corner"), "drag starts on corner 3");
        ed.DragCornerToAbs(inv[0], inv[1]);
        ed.RegionWorldNow();
        Check(ed.SegmentCount(EditorMode.OutlineRegionBad) > 0 && ed.SegmentCount(EditorMode.OutlineRegionDrag) == 0, "crossing drag previews in the refused colour");
        Check(Disk() == Expect("recoloured"), "file untouched during the drag");
        await Refused("drag corner into a crossing", () => ed.EndCornerDrag(), "recoloured");
        ed.StartCornerDrag(1);
        ed.DragCornerToAbs(mv[0], mv[1]);
        ed.RegionWorldNow();
        Check(ed.SegmentCount(EditorMode.OutlineRegionDrag) == 4 && ed.SegmentCount(EditorMode.OutlineRegionBad) == 0, "valid drag previews live");
        await Key_(Key.Escape);
        Check(ed.RegionMode == EditorMode.RegionState.Selected && Disk() == Expect("recoloured"), "Esc cancels the drag, nothing saved");
        ed.StartCornerDrag(1);
        ed.DragCornerToAbs(mv[0], mv[1]);
        await Gesture("drag corner 2", () => ed.EndCornerDrag(), "moved");

        // ---- delete: confirm names the region; cancel keeps it ----
        await Key_(Key.Delete);
        Check(ed.ConfirmVisible && ed.ConfirmText.Contains("R-0002"), $"Del asks first and names the region: '{ed.ConfirmText.Replace('\n', '|')}'");
        await Key_(Key.Escape);
        Check(!ed.ConfirmVisible && ed.Region("R-0002") != null && Disk() == Expect("moved"), "Esc on the confirm cancels: region kept");
        await Gesture("delete R-0002", () => { ed.ConfirmAccept(); return !ed.ConfirmVisible && ed.Region("R-0002") == null; }, "seed", () => Key_(Key.Delete));
        Check(ed.SelectedRegionId == null && ed.RegionRowCount == 1, "deleted region leaves the selection and the list");

        // ---- undo / redo of every step, byte for byte ----
        var chain = new[] { "moved", "recoloured", "created", "seed" };
        Check(ed.UndoDepth == 4 && ed.NextUndoDescription!.Contains("deleted R-0002"), $"undo stack: {ed.UndoDepth} steps, next '{ed.NextUndoDescription}'");
        async Task Walk(string what, bool undo, string[] states)
        {
            foreach (var st in states)
            {
                var a0 = _watcher!.AppliedCount;
                var ok = undo ? ed.Undo() : ed.Redo();
                Check(ok, $"{what} -> {st}: status '{ed.StatusMessage}'");
                FileIs(st, $"{what} {st}");
                await WaitUntil(() => _watcher.AppliedCount > a0 || _watcher.FailedCount > 0, 90);
                await Settle();
            }
        }
        await Walk("undo", true, chain);
        Check(ed.UndoDepth == 0 && ed.RedoDepth == 4 && !_map.DynamicRegions.ContainsKey("R-0002"), "everything undone: R-0002 gone from the map");
        await Walk("redo", false, new[] { "created", "recoloured", "moved", "seed" });
        Check(ed.RedoDepth == 0 && ed.UndoDepth == 4, "everything redone");
        await Walk("undo", true, new[] { "moved" });
        await Walk("redo", false, new[] { "seed" });

        // ---- the last region goes: the regions key disappears; undo brings it back ----
        ed.SelectRegion("R-0001");
        await Gesture("delete the last region", () => ed.DeleteRegion("R-0001"), "empty");
        Check(!Disk().Contains("\"regions\""), "no regions key when there are no regions (as dump_text)");
        ed.RefreshNow();
        Check(ed.RegionEmptyShown && ed.RegionRowCount == 0, "empty state shown in the panel");
        await Walk("undo", true, new[] { "seed" });

        // ---- Esc, one level at a time ----
        ed.SelectRegion("R-0001");
        await Key_(Key.Escape);
        Check(ed.SelectedRegionId == null && ed.Tool == EditorTool.Region, "Esc deselects the region, tool stays");
        await Key_(Key.Escape);
        Check(ed.Tool == EditorTool.None, "Esc again leaves the tool");
        ed.SelectTool(EditorTool.Region);
        ed.StartRegion();
        ed.AddRegionPoint(q[0][0], q[0][1]);
        await Key_(Key.Backspace);
        Check(ed.RegionDraft.Count == 0, "Backspace takes the last corner back");
        ed.AddRegionPoint(q[0][0], q[0][1]);
        ed.SelectTool(EditorTool.View);
        Check(ed.RegionDraft.Count == 0 && ed.StatusMessage.Contains("never saved"), $"switching tool names the discarded shape: '{ed.StatusMessage}'");
        ed.SelectTool(EditorTool.Region);
        Check(ed.RegionAt(plan.GetProperty("inside_r1")[0].GetDouble(), plan.GetProperty("inside_r1")[1].GetDouble()) == "R-0001", "hit test finds R-0001");
        ed.PressRegionRow("R-0001"); ed.RefreshNow();
        Check(ed.SelectedRegionId == "R-0001" && ed.RegionDeleteButtonShown, "clicking the list row selects it");

        // ---- palette ----
        await Key_(Key.P, ctrl: true);
        Check(ed.PaletteVisible, "Ctrl+P opens the palette");
        ed.PaletteSetFilter("region");
        Check(ed.PaletteLabels.Any(l => l.StartsWith("Region: new")) && ed.PaletteLabels.Any(l => l.StartsWith("R-0001")), $"palette finds the region command and R-0001: {string.Join(" / ", ed.PaletteLabels)}");
        await Key_(Key.Escape);
        Check(!ed.PaletteVisible, "Esc closes the palette");
        ed.OpenPalette(); ed.PaletteSetFilter("Region: new");
        Check(ed.PaletteRun(0) && ed.RegionMode == EditorMode.RegionState.Drawing, "palette runs 'Region: new'");
        ed.RegionEscape();

        Check(_watcher!.FailedCount == 0, "no failed run during the whole smoke");
        var finalCfg = ed.SelectedRegionId;
        GD.Print($"info: undo depth {ed.UndoDepth}, saves {ed.SaveCount}, selected {finalCfg}");
    }
}
