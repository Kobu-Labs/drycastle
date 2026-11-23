using Drycastle.Viewer.Editor;
using Drycastle.Viewer.Editor.Tools;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Drycastle.Viewer;

/// <summary>
/// `--objects-smoke --objects-smoke-plan plan.json` (run by objects_smoke.py, `make viewer-objects-smoke`): drives the
/// Objects section, the gizmo and the session undo of the editor mode through their APIs and key events (no
/// synthetic mouse) in a headless viewer. The expected edits.json text of every step comes from the plan (computed
/// in Python); the file after every save is copied to the plan's steps folder for the Python validator.
/// </summary>
public partial class Viewer
{
    async Task RunObjectsSmoke()
    {
        try { await ObjectsSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL objects smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "OBJECTS SMOKE PASS" : "OBJECTS SMOKE FAIL");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    sealed record SaveRec(string Name, string Text, bool Run);

    async Task ObjectsSmokeBody()
    {
        var ed = _editor!;
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["objects-smoke-plan"]));
        var plan = planDoc.RootElement;
        string S(string k) => plan.GetProperty(k).GetString()!;
        double[] D(string k) => plan.GetProperty(k).EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var editsPath = ed.EditsPath;
        var stepsDir = S("steps_dir");
        Directory.CreateDirectory(stepsDir);
        string A = S("a"), B = S("b"), C = S("c");
        string CAR = S("car"), SLAB = S("slab");
        var origin = D("origin");
        double ox = origin[0], oy = origin[1];
        string Expect(string k) => File.ReadAllText(plan.GetProperty("expect").GetProperty(k).GetString()!);

        // local ground point (x east, y north in map metres) with the terrain height
        Vector3 Gl(double lx, double ly) => new((float)lx, _map.GroundHeightOrMean((float)lx, (float)-ly), (float)-ly);
        Vector3 Gp(string k, double dx = 0, double dy = 0) { var p = D(k); return Gl(p[0] + dx, p[1] + dy); }
        Vector3 GAbs(double ax, double ay) => Gl(ax - ox, ay - oy);
        Vector3 GOf(string id) { var o = ed.Objects.ObjectData(id)!; return GAbs(o.X, o.Y); }

        async Task Frame(int n = 2) { for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        void Key_(Key k, bool ctrl = false, bool shift = false)
            => Input.ParseInputEvent(new InputEventKey { Keycode = k, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift });
        string Disk() => File.ReadAllText(editsPath);
        void FileIs(string step, string what)
        {
            var now = Disk(); var want = Expect(step);
            Check(now == want, $"{what}: edits.json is exactly the expected text" + (now == want ? "" : $"\n--- got\n{now}--- want\n{want}"));
        }
        var saves = new List<SaveRec> { new("seed", Disk(), false) };
        var latencies = new List<double>();
        Check(saves[0].Text == Expect("seed"), "scratch edits file is the planned seed");

        // one gesture = exactly one save; objects-only saves must not run parcel-run and show at once
        async Task<bool> Op(string name, Func<bool> act, bool run = false, int frames = 0)
        {
            var runs0 = _watcher!.RunCount; var s0 = ed.Edits.SaveCount; var inst0 = _watcher.InstantCount; var app0 = _watcher.AppliedCount;
            var ok = act();
            if (frames > 0) await Frame(frames);
            var saved = ed.Edits.SaveCount == s0 + 1;
            Check(ok && saved, $"[{name}] one save ({ed.Edits.SaveCount - s0}), status '{ed.StatusMessage}'");
            if (!run)
            {
                Check(_watcher.RunCount == runs0 && _watcher.InstantCount == inst0 + 1 && ed.Edits.LastSaveInstant,
                    $"[{name}] objects-only: applied in process, no parcel-run (runs {_watcher.RunCount - runs0}, save-to-visible {ed.Edits.LastSaveMs:F1} ms)");
                latencies.Add(ed.Edits.LastSaveMs);
            }
            else
            {
                var done = await WaitUntil(() => _watcher.AppliedCount > app0 && !_watcher.Busy || _watcher.FailedCount > 0, 90);
                Check(done && _watcher.RunCount > runs0 && _watcher.FailedCount == 0, $"[{name}] mixed change went through parcel-run (runs +{_watcher.RunCount - runs0})");
                await Physics(4);
            }
            FileIs(name, name);
            File.Copy(editsPath, Path.Combine(stepsDir, name + ".json"), true);
            saves.Add(new SaveRec(name, Disk(), run));
            ed.RefreshNow();
            return ok && saved;
        }

        static double Yaw(Node3D n) { var x = n.GlobalBasis.X; return Math.Atan2(-x.Z, x.X) * 180.0 / Math.PI; }

        // the group node an object sits in: the parcel it stands in (an object belongs to no parcel), else "_none"
        string GroupOf(SortedDictionary<string, object?> o)
        {
            var (x, y) = ObjectEdits.Position(o);
            return _map.ParcelAt((float)(x - ox), (float)-(y - oy))?.Id ?? Drycastle.MapLoader.PlacedObject.NoParcelGroup;
        }

        // every object of the file has its node, where and how the file says; nothing else (but the seed's, which stands in parcel C and is never touched)
        void SceneMatches(string what)
        {
            var root = EditsFile.Load(editsPath);
            var want = ObjectEdits.All(root).Where(t => GroupOf(t.obj) != C).ToList();
            var bad = new List<string>();
            foreach (var (_, o) in want)
            {
                var id = (string)o["id"]!;
                var pid = GroupOf(o);
                if (!_map.ObjectNodes.TryGetValue(id, out var node)) { bad.Add($"{id} has no node"); continue; }
                var (x, y) = ObjectEdits.Position(o);
                var pos = node.GlobalPosition;
                if (Math.Abs(pos.X - (x - ox)) > 0.01 || Math.Abs(pos.Z + (y - oy)) > 0.01) bad.Add($"{id} at ({pos.X:F3}, {-pos.Z:F3}) not ({x - ox:F3}, {y - oy:F3})");
                if (Math.Abs(Mathf.Wrap((float)(Yaw(node) - ObjectEdits.Rotation(o)), -180, 180)) > 0.01) bad.Add($"{id} rotation {Yaw(node):F2} not {ObjectEdits.Rotation(o)}");
                if (Math.Abs(node.Scale.X - ObjectEdits.Scale(o)) > 1e-4) bad.Add($"{id} scale {node.Scale.X:F3} not {ObjectEdits.Scale(o)}");
                if (node.GetParent()?.Name != $"objects_{pid}" || node.Name != $"object_{id}") bad.Add($"{id}: node {node.GetParent()?.Name}/{node.Name} not objects_{pid}/object_{id}");
            }
            foreach (var id in _map.ObjectNodes.Keys)
                if (want.All(t => (string)t.obj["id"]! != id)) bad.Add($"stray node {id}");
            Check(bad.Count == 0, $"{what}: {want.Count} object nodes match the file" + (bad.Count == 0 ? "" : ": " + string.Join("; ", bad)));
        }

        await Physics(3);
        GetWindow().Size = new Vector2I(1600, 900); // the headless window is tiny; the panel must not cover the (0, 0) mouse position
        await Frame(3);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F2, Pressed = true });
        await WaitUntil(() => ed.Active, 3);
        Check(ed.Active && _map.OriginEpsg5514 is { } o0 && Math.Abs(o0.X - ox) < 1e-9 && Math.Abs(o0.Y - oy) < 1e-9, "editor mode on, the loader knows the map origin");
        // the camera looks north (arrow keys nudge in screen directions)
        var cam = Gp("a_base");
        ed.Camera.LookFrom(cam + new Vector3(0, 40, 30), cam + new Vector3(0, 0, -5));
        await Frame();

        // ---- 1. the palette lists every manifest asset in its category ----
        var manifest = plan.GetProperty("manifest").EnumerateArray().Select(a => (id: a[0].GetString()!, cat: a[1].GetString()!, name: a[2].GetString()!)).ToList();
        var pal = ed.Objects.Panel.Palette;
        Check(pal.Count == manifest.Count && manifest.All(m => pal.Any(p => p.Id == m.id && p.Category == m.cat && p.Name == m.name && p.Visible)),
            $"palette lists all {manifest.Count} manifest assets with their category and name");
        var cats = manifest.Select(m => m.cat).Distinct().ToList();
        Check(cats.All(c => ed.Objects.Panel.PaletteGroupShown(c) && ed.Objects.Panel.PaletteGroupTitle(c).Contains($"({manifest.Count(m => m.cat == c)})")),
            $"one group per category with its count: {string.Join(", ", cats.Select(c => ed.Objects.Panel.PaletteGroupTitle(c)))}");
        Check(ObjectsTool.Categories.Where(c => cats.Contains(c)).SequenceEqual(cats.Where(ObjectsTool.Categories.Contains).OrderBy(c => Array.IndexOf(ObjectsTool.Categories, c))),
            "categories are in the planned order (car, slab, pot, flower, bush, tree, wall, fence, other)");
        Check(!ed.Objects.Panel.PaletteGroupBodyShown("car"), "groups start collapsed");
        ed.Objects.Panel.SetPaletteGroupOpen("car", true);
        Check(ed.Objects.Panel.PaletteGroupBodyShown("car") && ed.Objects.Panel.PaletteGroupTitle("car").StartsWith("v "), "a group opens");
        ed.Objects.Panel.SetPaletteFilter("flo");
        var vis = ed.Objects.Panel.Palette.Where(p => p.Visible).Select(p => p.Id).OrderBy(x => x).ToList();
        var wantVis = manifest.Where(m => m.id.Contains("flo", StringComparison.OrdinalIgnoreCase) || m.name.Contains("flo", StringComparison.OrdinalIgnoreCase) || m.cat.Contains("flo")).Select(m => m.id).OrderBy(x => x).ToList();
        Check(vis.Count > 0 && vis.SequenceEqual(wantVis) && !ed.Objects.Panel.PaletteGroupShown("car") && ed.Objects.Panel.PaletteGroupBodyShown("flower"),
            $"filter 'flo' leaves {vis.Count} assets, hides empty groups and opens the matching ones");
        ed.Objects.Panel.SetPaletteFilter("");
        Check(ed.Objects.Panel.Palette.All(p => p.Visible) && ed.Objects.Panel.PaletteGroupShown("car") && ed.Objects.Panel.PaletteGroupBodyShown("car"), "clearing the filter restores the palette and the open state");
        ed.Objects.Panel.SetPaletteGroupOpen("car", false);

        // ---- 2. placement: ghost, rotation keys, placing, staying armed ----
        Check(!ed.Objects.ArmPlacement("pack:nope/nothing") && ed.Objects.PlacementAsset == null && ed.Objects.Ghost == null, "an unknown asset cannot be armed");
        Check(ed.Objects.ArmPlacement(CAR) && ed.Objects.PlacementAsset == CAR && ed.Objects.Ghost != null && ed.Objects.Ghost.GetChildCount() > 0, "choosing an asset arms placement and creates the ghost");
        await Frame(3);
        ed.Objects.GhostPinned = true;
        var pick = Gp("a_base", 0.1, -0.2);
        ed.Objects.UpdateGhost(pick, false);
        var gabs = plan.GetProperty("a_base_abs").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Check(ed.Objects.GhostAbsolute is { } ga && Math.Abs(ga.x - gabs[0]) < 1e-9 && Math.Abs(ga.y - gabs[1]) < 1e-9
              && Math.Abs(ed.Objects.Ghost!.Position.X - (gabs[0] - ox)) < 1e-3 && Math.Abs(ed.Objects.Ghost.Position.Z + (gabs[1] - oy)) < 1e-3 && ed.Objects.Ghost.Visible,
            $"the ghost follows the pick point, snapped to 0.5 m ({ed.Objects.GhostAbsolute})");
        var freePick = Gp("a_base", 0.13, -0.2);
        ed.Objects.UpdateGhost(freePick, true);
        Check(ed.Objects.GhostAbsolute is { } gf && Math.Abs(gf.x - Math.Round(ox + freePick.X, 3)) < 1e-9 && Math.Abs(gf.x - gabs[0]) > 0.05,
            $"Ctrl (free) skips the snapping: {ed.Objects.GhostAbsolute}");
        ed.Objects.UpdateGhost(Gp("outside"), false);
        Check(ed.Objects.Ghost!.Visible, "the ghost follows the cursor outside every parcel too (an object belongs to no parcel)");
        ed.Objects.UpdateGhost(pick, false);
        Key_(Key.R); await Frame();
        Check(Math.Abs(ed.Objects.GhostRotation - 15) < 1e-9 && Math.Abs(ed.Objects.Ghost!.RotationDegrees.Y - 15) < 1e-3, "R rotates the ghost +15");
        Key_(Key.R); await Frame();
        Key_(Key.R, shift: true); await Frame();
        Key_(Key.R, shift: true); await Frame();
        Key_(Key.R, shift: true); await Frame();
        Check(Math.Abs(ed.Objects.GhostRotation - 345) < 1e-9, $"Shift+R rotates -15 (R, R, 3x Shift+R = 345): {ed.Objects.GhostRotation}");
        Key_(Key.R); await Frame();
        Check(Math.Abs(ed.Objects.GhostRotation) < 1e-9, "back at 0");

        await Op("t1", () => ed.Objects.PlaceAt(pick));
        Check(ed.Objects.PlacementAsset == CAR && ed.Objects.Ghost != null, "placement stays armed after placing");
        Check(_map.ObjectNodes.ContainsKey("o-0001") && _map.ObjectNodes["o-0001"].GetChild(0).GetChildCount() > 0, "o-0001 (a pack model) is instanced with the model's meshes");
        SceneMatches("after placing the car");
        ed.Objects.ArmPlacement(SLAB);
        ed.Objects.RotateGhost(15); Key_(Key.R); await Frame();
        Check(Math.Abs(ed.Objects.GhostRotation - 30) < 1e-9 && ed.Objects.PlacementAsset == SLAB, "choosing another asset keeps the armed state; ghost rotation 30");
        await Op("t2", () => ed.Objects.PlaceAt(Gp("a_base", 2.1, 1.45)));
        await Op("t3", () => ed.Objects.PlaceAt(Gp("b_base", 0.1, 0.1)));
        Check(_map.ObjectNodes["o-0002"].GetParent().Name == $"objects_{A}" && _map.ObjectNodes["o-0003"].GetParent().Name == $"objects_{B}",
            "objects sit under the container of the parcel they were placed on (primitive slab too)");
        SceneMatches("after three placements");
        // Esc leaves placement mode
        Key_(Key.Escape); await Frame(3);
        Check(ed.Objects.PlacementAsset == null && ed.Objects.Ghost == null, "Esc disarms and frees the ghost");
        ed.Objects.ArmPlacement(CAR);
        ed.Objects.DisarmPlacement();
        Check(ed.Objects.PlacementAsset == null, "DisarmPlacement (right click) also leaves the mode");

        // ---- 3. selecting: a ray against the object's box takes priority over the parcel ----
        var car = GOf("o-0001");
        ed.Click(null, false);
        var pickId = ed.Objects.PickObject(car + new Vector3(0, 30, 0), Vector3.Down);
        var pickMiss = ed.Objects.PickObject(car + new Vector3(30, 30, 0), Vector3.Down);
        Check(pickId == "o-0001" && pickMiss == null, $"ray down onto the car hits o-0001 (got {pickId}), a ray 30 m aside nothing ({pickMiss})");
        Check(ed.Objects.SelectObject("o-0001") && ed.Objects.SelectedObjectId == "o-0001", "selecting an object");
        await Frame(3);
        ed.RefreshNow();
        Check(ed.Objects.Gizmo.Visible && ed.Objects.Gizmo.Radius > 1.0f && ed.Objects.Panel.FieldsShown, $"the gizmo (box, ring, arrow) is visible, ring radius {ed.Objects.Gizmo.Radius:F2} m, the panel shows the fields");
        var gc = _map.ObjectNodes["o-0001"].GlobalPosition;
        Check(ed.Objects.Gizmo.OnRing(gc + new Vector3(ed.Objects.Gizmo.Radius, 0, 0)) && !ed.Objects.Gizmo.OnRing(gc) && !ed.Objects.Gizmo.OnRing(gc + new Vector3(ed.Objects.Gizmo.Radius + 3, 0, 0)),
            "a point on the ring is a rotation handle; the object's centre and far points are not");
        Check(Math.Abs(ed.Objects.Panel.XSpin.Value - ed.Objects.ObjectData("o-0001")!.X) < 1e-3 && Math.Abs(ed.Objects.Panel.ScaleSpin.Value - 1) < 1e-9 && !ed.Objects.Panel.ColourSet, $"the fields show the object's values (x {ed.Objects.Panel.XSpin.Value} vs {ed.Objects.ObjectData("o-0001")!.X}, scale {ed.Objects.Panel.ScaleSpin.Value})");

        // ---- 4. drag to move (0.5 m grid, Ctrl = free), one save per gesture ----
        var before = Disk();
        Check(ed.Objects.StartMove("o-0001", car) && ed.Objects.Dragging, "press on the object starts a move");
        ed.Objects.DragTo(Gp("a_base", 2.3, 1.2), false);
        var preview = _map.ObjectNodes["o-0001"].GlobalPosition;
        Check(Disk() == before && Math.Abs(preview.X - (gabs[0] - ox + 2.5)) < 0.01 && Math.Abs(preview.Z + (gabs[1] - oy + 1.0)) < 0.01 && ed.Edits.SaveCount == 3,
            "while dragging only the view moves (snapped), the file is untouched");
        await Op("t4", () => ed.Objects.EndDrag());
        SceneMatches("after the drag");
        var grab = GOf("o-0001") + new Vector3(0.4f, 0, -0.3f);
        ed.Objects.StartMove("o-0001", grab);
        ed.Objects.DragTo(grab + new Vector3(1.0f, 0, 0), false);
        await Op("t5", () => ed.Objects.EndDrag());
        ed.Objects.StartMove("o-0001", GOf("o-0001"));
        ed.Objects.DragTo(GOf("o-0001") + new Vector3(0.125f, 0, 0.25f), true);
        await Op("t6", () => ed.Objects.EndDrag());
        var s1 = ed.Edits.SaveCount;
        ed.Objects.StartMove("o-0001", GOf("o-0001"));
        Check(!ed.Objects.EndDrag() && ed.Edits.SaveCount == s1, "a click without dragging saves nothing");

        // ---- 5. rotate with the ring (15 degrees, Ctrl = free) ----
        ed.Objects.StartRotate("o-0001");
        Check(ed.Objects.DragIsRotation, "press on the ring starts a rotation");
        ed.Objects.DragTo(Gp("rot1_pt"), false);
        Check(Math.Abs(Mathf.Wrap((float)(Yaw(_map.ObjectNodes["o-0001"]) - 30), -180, 180)) < 0.01, "the preview turns to the snapped angle");
        await Op("t7", () => ed.Objects.EndDrag());
        ed.Objects.StartRotate("o-0001");
        ed.Objects.DragTo(Gp("rot2_pt"), true);
        await Op("t8", () => ed.Objects.EndDrag());
        SceneMatches("after rotating");

        // ---- 6. cross-parcel move re-homes; outside every parcel is refused ----
        ed.Objects.StartMove("o-0001", GOf("o-0001"));
        ed.Objects.DragTo(Gp("b_base", 1.1, 0.9), false);
        await Op("t9", () => ed.Objects.EndDrag());
        Check(_map.ObjectNodes["o-0001"].GetParent().Name == $"objects_{B}", "after the move the node sits under the container of the parcel it now stands in (moved, not duplicated)");
        SceneMatches("after the cross-parcel move");

        // ---- 7. field edits (one save each), through the real controls where it counts ----
        var cur = ed.Objects.ObjectData("o-0001")!;
        await Op("t10", () => ed.Objects.SetObjectPosition(cur.X + 2.0, cur.Y));
        ed.RefreshNow();
        Check(Math.Abs(ed.Objects.Panel.XSpin.Value - (cur.X + 2.0)) < 1e-3, "the position field follows");
        await Op("t11", () => { ed.Objects.Panel.RotationSpin.Value = 100; return true; }); // the real SpinBox signal path
        await Op("t12", () => ed.Objects.SetObjectScale(1.5));
        Check(Math.Abs(_map.ObjectNodes["o-0001"].Scale.X - 1.5) < 1e-4, "scale 1.5 shows at once");
        var sf0 = ed.Edits.SaveCount;
        Check(!ed.Objects.SetObjectScale(0) && !ed.Objects.SetObjectScale(-1) && !ed.Objects.SetObjectColour("red") && !ed.Objects.SetObjectColour("#12345") && ed.Edits.SaveCount == sf0, "scale <= 0 and bad colours are refused, nothing saved");
        await Op("t13", () => ed.Objects.SetObjectColour("#AABBCC"));
        ed.RefreshNow();
        var tinted = _map.ObjectNodes["o-0001"].FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Any(m => m.MaterialOverride is StandardMaterial3D { AlbedoColor: var c } && c.ToHtml(false) == "aabbcc");
        Check(tinted && ed.Objects.Panel.ColourSet, "the colour override shows at once and the 'set' box is on");
        await Op("t14", () => ed.Objects.SetObjectColour(null));
        await Op("t15", () => ed.Objects.SetObjectScale(1.0));

        // ---- 8. keys: nudge, duplicate, delete ----
        var n0 = ed.Objects.ObjectData("o-0001")!;
        await Op("t16", () => { Key_(Key.Right); return true; }, frames: 2);
        await Frame(2);
        Check(Math.Abs(ed.Objects.ObjectData("o-0001")!.X - (n0.X + 0.5)) < 1e-9, "Right nudges 0.5 m east");
        await Op("t17", () => { Key_(Key.Up); return true; }, frames: 2);
        await Frame(2);
        Check(Math.Abs(ed.Objects.ObjectData("o-0001")!.Y - (n0.Y + 0.5)) < 1e-9, "Up nudges 0.5 m north (camera looks north)");
        await Op("t18", () => { Key_(Key.Left); return true; }, frames: 2);
        await Op("t19", () => { Key_(Key.Down); return true; }, frames: 2);
        await Frame(2);
        Check(Math.Abs(ed.Objects.ObjectData("o-0001")!.X - n0.X) < 1e-9 && Math.Abs(ed.Objects.ObjectData("o-0001")!.Y - n0.Y) < 1e-9, "Left and Down nudge back");
        await Op("t20", () => { Key_(Key.D, ctrl: true); return true; }, frames: 2);
        await Frame(2);
        Check(ed.Objects.SelectedObjectId == "o-0004" && ed.Objects.ObjectData("o-0004") is { } dup && Math.Abs(dup.X - (n0.X + 1.0)) < 1e-9 && Math.Abs(dup.Y - n0.Y) < 1e-9 && dup.Asset == CAR,
            $"Ctrl+D duplicates 1 m east and selects the copy ({ed.Objects.SelectedObjectId})");
        SceneMatches("after duplicating");
        await Op("t21", () => { Key_(Key.Delete); return true; }, frames: 2);
        await Frame(2);
        Check(ed.Objects.SelectedObjectId == null && ed.Objects.ObjectData("o-0004") == null && !_map.ObjectNodes.ContainsKey("o-0004") && !ed.Objects.Gizmo.Visible, "Delete removes the object, the node and the gizmo go");
        SceneMatches("after deleting");
        Check(!ed.Objects.DeleteSelected() && !ed.Objects.DuplicateSelected() && !ed.Objects.NudgeObject(1, 0), "with nothing selected delete, duplicate and nudge do nothing");

        // ---- 9. mixed changes still go through parcel-run; objects stay correct after the fragment swap ----
        ed.Click(null, false); ed.Click(A, false); ed.Click(B, true);
        await Frame();
        var runsG = _watcher!.RunCount;
        await Op("t22", () => { ed.GroupSelected(); return ed.StatusMessage.StartsWith("saved"); }, run: true);
        SceneMatches("after the group's parcel-run swapped the fragment in");
        ed.Objects.SelectObject("o-0002");
        await Op("t23", () => ed.Objects.NudgeObject(0.5, 0));
        Check(_watcher.RunCount == runsG + 1, "with the group in place an objects-only edit still needs no parcel-run");
        ed.Click(A, false); ed.Click(B, true);
        await Frame();
        ed.RefreshNow();
        Check(ed.Target is { IsGroup: true }, "the selection is the group");
        ed.SetFenceMode("none");
        await Op("t24", () => ed.Apply(), run: true);
        SceneMatches("after a fence Apply (fragment swap)");
        await Op("t25", () => ed.Objects.PlacementAsset == null && ed.Objects.ArmPlacement(CAR) && ed.Objects.PlaceAt(Gp("a_base", -1.1, -1.4)));
        ed.Objects.DisarmPlacement();
        SceneMatches("after placing on a parcel that has a swapped fragment");

        // ---- 10. session undo / redo across the whole sequence ----
        var kinds = saves.Select(s => s.Run).ToList(); // index 0 is the seed
        var n = saves.Count - 1;
        Check(ed.Edits.History.UndoDepth == n && ed.Edits.History.RedoDepth == 0, $"every save is on the undo stack ({ed.Edits.History.UndoDepth} of {n})");
        async Task StepUndoRedo(bool undo, int k)
        {
            // k = index in saves of the save being undone (undo) or redone (redo)
            var run = saves[k].Run;
            var runs0 = _watcher.RunCount; var app0 = _watcher.AppliedCount; var inst0 = _watcher.InstantCount;
            var desc = undo ? ed.Edits.History.NextUndoDescription : ed.Edits.History.NextRedoDescription;
            var s0 = ed.Edits.SaveCount;
            var ok = undo ? ed.Edits.Undo() : ed.Edits.Redo();
            var label = $"{(undo ? "undo" : "redo")} {saves[k].Name}";
            Check(ok && ed.StatusMessage == $"{(undo ? "undo" : "redo")}: {desc}" && ed.Edits.SaveCount == s0 + 1, $"[{label}] status '{ed.StatusMessage}'");
            if (run)
            {
                var done = await WaitUntil(() => _watcher.AppliedCount > app0 && !_watcher.Busy || _watcher.FailedCount > 0, 90);
                Check(done && _watcher.RunCount > runs0, $"[{label}] a mixed change went through parcel-run");
                await Physics(4);
            }
            else Check(_watcher.RunCount == runs0 && _watcher.InstantCount == inst0 + 1, $"[{label}] objects-only: instant, no parcel-run");
            var want = saves[undo ? k - 1 : k].Text;
            var got = Disk();
            Check(got == want, $"[{label}] edits.json is byte for byte the {(undo ? "earlier" : "later")} state '{saves[undo ? k - 1 : k].Name}'" + (got == want ? "" : $"\n--- got\n{got}--- want\n{want}"));
            ed.RefreshNow();
            SceneMatches($"[{label}]");
        }
        for (var k = n; k >= 1; k--) await StepUndoRedo(true, k);
        Check(ed.Edits.History.UndoDepth == 0 && ed.Edits.History.RedoDepth == n && Disk() == saves[0].Text, $"everything undone: the file is the seed again, redo stack {ed.Edits.History.RedoDepth}");
        Check(!ed.Edits.Undo() && ed.StatusMessage.StartsWith("nothing to undo"), "undo on an empty stack says so");
        for (var k = 1; k <= n; k++) await StepUndoRedo(false, k);
        Check(ed.Edits.History.RedoDepth == 0 && ed.Edits.History.UndoDepth == n && Disk() == saves[n].Text, "everything redone: the file is the final state again");

        // the key bindings: Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z
        var depth = ed.Edits.History.UndoDepth;
        Key_(Key.Z, ctrl: true); await Frame(3);
        Check(ed.Edits.History.UndoDepth == depth - 1 && ed.StatusMessage.StartsWith("undo: ") && Disk() == saves[n - 1].Text, $"Ctrl+Z undoes: '{ed.StatusMessage}'");
        await WaitUntil(() => !_watcher.Busy, 5);
        Key_(Key.Y, ctrl: true); await Frame(3);
        Check(ed.Edits.History.UndoDepth == depth && ed.StatusMessage.StartsWith("redo: ") && Disk() == saves[n].Text, $"Ctrl+Y redoes: '{ed.StatusMessage}'");
        Key_(Key.Z, ctrl: true); await Frame(3);
        Key_(Key.Z, ctrl: true, shift: true); await Frame(3);
        Check(ed.Edits.History.UndoDepth == depth && Disk() == saves[n].Text, "Ctrl+Shift+Z redoes too");
        // a new edit clears the redo stack
        Key_(Key.Z, ctrl: true); await Frame(3);
        Check(ed.Edits.History.RedoDepth == 1, "one redo step available");
        ed.Objects.ArmPlacement(SLAB);
        await Op("t26", () => ed.Objects.PlaceAt(Gp("a_base", 2.1, -1.55)));
        ed.Objects.DisarmPlacement();
        Check(ed.Edits.History.RedoDepth == 0 && !ed.Edits.Redo() && ed.StatusMessage.StartsWith("nothing to redo"), "a new edit clears the redo stack");

        // ---- 11. undo only touches the entries its operation touched ----
        {
            var root = EditsFile.Load(editsPath);
            var stale = (SortedDictionary<string, object?>)((SortedDictionary<string, object?>)root["parcels"]!)["P-9999"]!;
            stale["fence"] = new SortedDictionary<string, object?> { ["mode"] = "auto" };
            var other = EditsFile.Dump(root);
            File.WriteAllText(editsPath + ".tmp", other); File.Move(editsPath + ".tmp", editsPath, true);
            await WaitUntil(() => _watcher.InstantCount >= 0 && !_watcher.Busy, 5);
            await Frame(40);
            var d0 = ed.Edits.History.UndoDepth;
            Check(ed.Edits.Undo() && ed.Edits.History.UndoDepth == d0 - 1, "undo after an external change");
            var rootAfter = EditsFile.Load(editsPath);
            var staleAfter = ObjectEdits.Parcels(rootAfter)["P-9999"] as SortedDictionary<string, object?>;
            Check(staleAfter?["fence"] is SortedDictionary<string, object?> sfe && sfe["mode"] as string == "auto" && ObjectEdits.All(rootAfter).All(t => (string)t.obj["id"]! != "o-0004"),
                "the undone placement is gone, the external change to P-9999 is kept (only the touched entries are restored)");
            Key_(Key.Y, ctrl: true); await Frame(3);
            Check(ObjectEdits.All(EditsFile.Load(editsPath)).Any(t => (string)t.obj["id"]! == "o-0004"), "redo puts the placement back");
        }

        // ---- 12. the undo stack is capped at 200 ----
        ed.Objects.SelectObject("o-0001");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 205; i++) ed.Objects.NudgeObject(i % 2 == 0 ? 0.5 : -0.5, 0);
        GD.Print($"205 nudges in {sw.ElapsedMilliseconds} ms");
        Check(ed.Edits.History.UndoDepth == UndoHistory.Cap && ed.Edits.History.RedoDepth == 0, $"the undo stack holds at most {UndoHistory.Cap} ({ed.Edits.History.UndoDepth})");

        // ---- 13. latency of an object edit ----
        GD.Print($"latency: {latencies.Count} editor saves, save-to-visible min {latencies.Min():F1} ms, mean {latencies.Average():F1} ms, max {latencies.Max():F1} ms (instant path, includes reading, validating and writing the file)");
        Check(latencies.Max() < 150, $"every objects-only save was visible within 150 ms (max {latencies.Max():F1} ms)");
        // an external writer (MCP, text editor): found by the poll, at most 0.5 s plus the same update
        {
            var root = EditsFile.Load(editsPath);
            ObjectEdits.Add(root, ObjectEdits.Make("o-9001", CAR, ox + D("a_base")[0] + 0.0, oy + D("a_base")[1] + 4.0, 10.0));
            var text = EditsFile.Dump(root);
            var t0 = Time.GetTicksMsec();
            var inst0 = _watcher.InstantCount; var runs0 = _watcher.RunCount;
            File.WriteAllText(editsPath + ".tmp", text); File.Move(editsPath + ".tmp", editsPath, true);
            var seen = await WaitUntil(() => _map.ObjectNodes.ContainsKey("o-9001"), 3);
            var ms = Time.GetTicksMsec() - t0;
            GD.Print($"latency: external write to visible object {ms} ms (poll 500 ms + update {_watcher.LastInstantMs:F1} ms)");
            Check(seen && ms < 800 && _watcher.InstantCount == inst0 + 1 && _watcher.RunCount == runs0, $"an external objects-only write shows within the poll interval and needs no parcel-run ({ms} ms)");
        }

        // ---- 14. an object belongs to no parcel: it can stand, move and go on ground that is none ----
        {
            const string NoGroup = Drycastle.MapLoader.PlacedObject.NoParcelGroup;
            var idsBefore = ObjectEdits.All(EditsFile.Load(editsPath)).Select(t => (string)t.obj["id"]!).ToHashSet();
            var runsOut = _watcher.RunCount;
            ed.Objects.ArmPlacement(SLAB);
            Check(ed.Objects.PlaceAt(Gp("outside")), $"placing outside every parcel works ('{ed.StatusMessage}')");
            ed.Objects.DisarmPlacement();
            var after = EditsFile.Load(editsPath);
            var outId = ObjectEdits.All(after).Select(t => (string)t.obj["id"]!).FirstOrDefault(id => !idsBefore.Contains(id)) ?? "";
            Check(outId != "" && ed.Objects.ObjectData(outId) is { Parcel: "" }, $"the new object {outId} is in the top-level list and stands in no parcel");
            Check(!ObjectEdits.Parcels(after).Values.OfType<SortedDictionary<string, object?>>().Any(e => e.ContainsKey("objects")), "and nothing is listed under a parcel entry");
            await WaitIdle(10);
            Check(_map.ObjectNodes.TryGetValue(outId, out var outNode) && outNode.GetParent().Name == $"objects_{NoGroup}",
                "it is instanced at once, under the container for objects on no parcel");
            Check(_watcher.RunCount == runsOut, "and that needed no parcel-run");
            ed.Objects.SelectObject(outId);
            ed.Objects.StartMove(outId, GOf(outId));
            ed.Objects.DragTo(Gp("outside", 3.0, 0.0), false);
            Check(ed.Objects.EndDrag() && ed.Objects.ObjectData(outId) is { Parcel: "" }, "dragging it to another spot that is no parcel works too");
            Check(ed.Objects.DeleteSelected() && ObjectEdits.Find(EditsFile.Load(editsPath), outId) == null, "and so does deleting it");
        }

        Check(await WaitIdle(60) && _watcher.FailedCount == 0, $"watcher settled, no failed parcel-run ({_watcher.FailedCount}); parcel-runs started in total: {_watcher.RunCount}, instant object updates: {_watcher.InstantCount}");
    }
}
