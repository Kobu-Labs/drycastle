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
    sealed record SaveRec(string Name, string Text, bool Run);

    sealed class ObjectsCtx : SmokeCtx
    {
        public readonly string A, B, C, CAR, SLAB;
        public readonly double Ox, Oy;
        public readonly List<SaveRec> Saves = new();
        public readonly List<double> Latencies = new();
        // set by the placement and select steps, used by the later ones
        public Vector3 Pick, Car;
        public double[] Gabs = Array.Empty<double>();

        public ObjectsCtx(string planPath, string editsPath) : base(planPath, editsPath)
        {
            A = S("a"); B = S("b"); C = S("c");
            CAR = S("car"); SLAB = S("slab");
            var origin = D("origin");
            Ox = origin[0]; Oy = origin[1];
        }
    }

    Task RunObjectsSmoke() => RunSmoke("objects", ObjectsSmokeBody);

    async Task ObjectsSmokeBody()
    {
        using var c = new ObjectsCtx(_opts["objects-smoke-plan"], Ed.EditsPath);
        c.MakeStepsDir();
        c.Saves.Add(new SaveRec("seed", c.Disk(), false));
        CheckSeed(c);

        await EditorModeOn(c);
        PaletteListsManifest(c);
        await GhostAndRotation(c);
        await PlaceObjects(c);
        await SelectObject(c);
        await DragToMove(c);
        await RotateWithRing(c);
        await CrossParcelMove(c);
        await FieldEdits(c);
        await KeysNudgeDuplicateDelete(c);
        await MixedChangesStillRun(c);
        await UndoRedoWalk(c);
        await UndoRedoKeys(c);
        await UndoKeepsExternalChange(c);
        UndoStackCap();
        LatencyOfObjectEdit(c);
        await ExternalWriterLatency(c);
        await ObjectOnNoParcel(c);

        Check(await WaitIdle(60) && _watcher!.FailedCount == 0, $"watcher settled, no failed parcel-run ({_watcher!.FailedCount}); parcel-runs started in total: {_watcher.RunCount}, instant object updates: {_watcher.InstantCount}");
    }

    // local ground point (x east, y north in map metres) with the terrain height
    Vector3 GroundAt(double lx, double ly) => new((float)lx, _map.GroundHeightOrMean((float)lx, (float)-ly), (float)-ly);

    // the ground under a placed object
    Vector3 GOf(ObjectsCtx c, string id)
    {
        var o = Ed.Objects.ObjectData(id)!;
        return GroundAt(o.X - c.Ox, o.Y - c.Oy);
    }

    static double Yaw(Node3D n) { var x = n.GlobalBasis.X; return Math.Atan2(-x.Z, x.X) * 180.0 / Math.PI; }

    // one gesture = exactly one save; objects-only saves must not run parcel-run and show at once
    async Task<bool> Op(ObjectsCtx c, string name, Func<bool> act, bool run = false, int frames = 0)
    {
        var runs0 = _watcher!.RunCount; var s0 = Ed.Edits.SaveCount; var inst0 = _watcher.InstantCount; var app0 = _watcher.AppliedCount;
        var ok = act();
        if (frames > 0) await Frame(frames);
        var saved = Ed.Edits.SaveCount == s0 + 1;
        Check(ok && saved, $"[{name}] one save ({Ed.Edits.SaveCount - s0}), status '{Ed.Chrome.StatusMessage}'");
        if (!run)
        {
            Check(_watcher.RunCount == runs0 && _watcher.InstantCount == inst0 + 1 && Ed.Edits.LastSaveInstant,
                $"[{name}] objects-only: applied in process, no parcel-run (runs {_watcher.RunCount - runs0}, save-to-visible {Ed.Edits.LastSaveMs:F1} ms)");
            c.Latencies.Add(Ed.Edits.LastSaveMs);
        }
        else
        {
            var done = await WaitUntil(() => _watcher.AppliedCount > app0 && !_watcher.Busy || _watcher.FailedCount > 0, 90);
            Check(done && _watcher.RunCount > runs0 && _watcher.FailedCount == 0, $"[{name}] mixed change went through parcel-run (runs +{_watcher.RunCount - runs0})");
            await Physics(4);
        }
        FileIs(c, name, name);
        c.SaveStep(name);
        c.Saves.Add(new SaveRec(name, c.Disk(), run));
        Ed.RefreshNow();
        return ok && saved;
    }

    // the group node an object sits in: the parcel it stands in (an object belongs to no parcel), else "_none"
    string GroupOf(ObjectsCtx c, SortedDictionary<string, object?> o)
    {
        var (x, y) = ObjectEdits.Position(o);
        return _map.ParcelAt((float)(x - c.Ox), (float)-(y - c.Oy))?.Id ?? Drycastle.MapLoader.Objects.PlacedObject.NoParcelGroup;
    }

    // every object of the file has its node, where and how the file says; nothing else (but the seed's, which stands in parcel C and is never touched)
    void SceneMatches(ObjectsCtx c, string what)
    {
        var root = EditsFile.Load(c.EditsPath);
        var want = ObjectEdits.All(root).Where(t => GroupOf(c, t.obj) != c.C).ToList();
        var bad = new List<string>();
        foreach (var (_, o) in want)
        {
            var id = (string)o["id"]!;
            var pid = GroupOf(c, o);
            if (!_map.ObjectNodes.TryGetValue(id, out var node)) { bad.Add($"{id} has no node"); continue; }
            var (x, y) = ObjectEdits.Position(o);
            var pos = node.GlobalPosition;
            if (Math.Abs(pos.X - (x - c.Ox)) > 0.01 || Math.Abs(pos.Z + (y - c.Oy)) > 0.01) bad.Add($"{id} at ({pos.X:F3}, {-pos.Z:F3}) not ({x - c.Ox:F3}, {y - c.Oy:F3})");
            if (Math.Abs(Mathf.Wrap((float)(Yaw(node) - ObjectEdits.Rotation(o)), -180, 180)) > 0.01) bad.Add($"{id} rotation {Yaw(node):F2} not {ObjectEdits.Rotation(o)}");
            if (Math.Abs(node.Scale.X - ObjectEdits.Scale(o)) > 1e-4) bad.Add($"{id} scale {node.Scale.X:F3} not {ObjectEdits.Scale(o)}");
            if (node.GetParent()?.Name != $"objects_{pid}" || node.Name != $"object_{id}") bad.Add($"{id}: node {node.GetParent()?.Name}/{node.Name} not objects_{pid}/object_{id}");
        }
        foreach (var id in _map.ObjectNodes.Keys)
            if (want.All(t => (string)t.obj["id"]! != id)) bad.Add($"stray node {id}");
        Check(bad.Count == 0, $"{what}: {want.Count} object nodes match the file" + (bad.Count == 0 ? "" : ": " + string.Join("; ", bad)));
    }

    async Task StepUndoRedo(ObjectsCtx c, bool undo, int k)
    {
        // k = index in saves of the save being undone (undo) or redone (redo)
        var run = c.Saves[k].Run;
        var runs0 = _watcher!.RunCount; var app0 = _watcher.AppliedCount; var inst0 = _watcher.InstantCount;
        var desc = undo ? Ed.Edits.History.NextUndoDescription : Ed.Edits.History.NextRedoDescription;
        var s0 = Ed.Edits.SaveCount;
        var ok = undo ? Ed.Edits.Undo() : Ed.Edits.Redo();
        var label = $"{(undo ? "undo" : "redo")} {c.Saves[k].Name}";
        Check(ok && Ed.Chrome.StatusMessage == $"{(undo ? "undo" : "redo")}: {desc}" && Ed.Edits.SaveCount == s0 + 1, $"[{label}] status '{Ed.Chrome.StatusMessage}'");
        if (run)
        {
            var done = await WaitUntil(() => _watcher.AppliedCount > app0 && !_watcher.Busy || _watcher.FailedCount > 0, 90);
            Check(done && _watcher.RunCount > runs0, $"[{label}] a mixed change went through parcel-run");
            await Physics(4);
        }
        else Check(_watcher.RunCount == runs0 && _watcher.InstantCount == inst0 + 1, $"[{label}] objects-only: instant, no parcel-run");
        var want = c.Saves[undo ? k - 1 : k].Text;
        var got = c.Disk();
        Check(got == want, $"[{label}] edits.json is byte for byte the {(undo ? "earlier" : "later")} state '{c.Saves[undo ? k - 1 : k].Name}'" + (got == want ? "" : $"\n--- got\n{got}--- want\n{want}"));
        Ed.RefreshNow();
        SceneMatches(c, $"[{label}]");
    }

    async Task EditorModeOn(ObjectsCtx c)
    {
        await Physics(3);
        GetWindow().Size = new Vector2I(1600, 900); // the headless window is tiny; the panel must not cover the (0, 0) mouse position
        await Frame(3);
        Press(Key.F2);
        await WaitUntil(() => Ed.Active, 3);
        Check(Ed.Active && _map.OriginEpsg5514 is { } o0 && Math.Abs(o0.X - c.Ox) < 1e-9 && Math.Abs(o0.Y - c.Oy) < 1e-9, "editor mode on, the loader knows the map origin");
        // the camera looks north (arrow keys nudge in screen directions)
        var cam = PlanGround(c, "a_base");
        Ed.Camera.LookFrom(cam + new Vector3(0, 40, 30), cam + new Vector3(0, 0, -5));
        await Frame();
    }

    // ---- 1. the palette lists every manifest asset in its category ----
    void PaletteListsManifest(ObjectsCtx c)
    {
        var manifest = c.Plan.GetProperty("manifest").EnumerateArray().Select(a => (id: a[0].GetString()!, cat: a[1].GetString()!, name: a[2].GetString()!)).ToList();
        var pal = Ed.Objects.Panel.Palette;
        Check(pal.Count == manifest.Count && manifest.All(m => pal.Any(p => p.Id == m.id && p.Category == m.cat && p.Name == m.name && p.Visible)),
            $"palette lists all {manifest.Count} manifest assets with their category and name");
        var cats = manifest.Select(m => m.cat).Distinct().ToList();
        Check(cats.All(cat => Ed.Objects.Panel.PaletteGroupShown(cat) && Ed.Objects.Panel.PaletteGroupTitle(cat).Contains($"({manifest.Count(m => m.cat == cat)})")),
            $"one group per category with its count: {string.Join(", ", cats.Select(cat => Ed.Objects.Panel.PaletteGroupTitle(cat)))}");
        Check(ObjectsTool.Categories.Where(cat => cats.Contains(cat)).SequenceEqual(cats.Where(ObjectsTool.Categories.Contains).OrderBy(cat => Array.IndexOf(ObjectsTool.Categories, cat))),
            "categories are in the planned order (car, slab, pot, flower, bush, tree, wall, fence, other)");
        Check(!Ed.Objects.Panel.PaletteGroupBodyShown("car"), "groups start collapsed");
        Ed.Objects.Panel.SetPaletteGroupOpen("car", true);
        Check(Ed.Objects.Panel.PaletteGroupBodyShown("car") && Ed.Objects.Panel.PaletteGroupTitle("car").StartsWith("v "), "a group opens");
        Ed.Objects.Panel.SetPaletteFilter("flo");
        var vis = Ed.Objects.Panel.Palette.Where(p => p.Visible).Select(p => p.Id).OrderBy(x => x).ToList();
        var wantVis = manifest.Where(m => m.id.Contains("flo", StringComparison.OrdinalIgnoreCase) || m.name.Contains("flo", StringComparison.OrdinalIgnoreCase) || m.cat.Contains("flo")).Select(m => m.id).OrderBy(x => x).ToList();
        Check(vis.Count > 0 && vis.SequenceEqual(wantVis) && !Ed.Objects.Panel.PaletteGroupShown("car") && Ed.Objects.Panel.PaletteGroupBodyShown("flower"),
            $"filter 'flo' leaves {vis.Count} assets, hides empty groups and opens the matching ones");
        Ed.Objects.Panel.SetPaletteFilter("");
        Check(Ed.Objects.Panel.Palette.All(p => p.Visible) && Ed.Objects.Panel.PaletteGroupShown("car") && Ed.Objects.Panel.PaletteGroupBodyShown("car"), "clearing the filter restores the palette and the open state");
        Ed.Objects.Panel.SetPaletteGroupOpen("car", false);
    }

    // ---- 2. placement: ghost, rotation keys ----
    async Task GhostAndRotation(ObjectsCtx c)
    {
        Check(!Ed.Objects.ArmPlacement("pack:nope/nothing") && Ed.Objects.PlacementAsset == null && Ed.Objects.Ghost == null, "an unknown asset cannot be armed");
        Check(Ed.Objects.ArmPlacement(c.CAR) && Ed.Objects.PlacementAsset == c.CAR && Ed.Objects.Ghost != null && Ed.Objects.Ghost.GetChildCount() > 0, "choosing an asset arms placement and creates the ghost");
        await Frame(3);
        Ed.Objects.GhostPinned = true;
        c.Pick = PlanGround(c, "a_base", 0.1, -0.2);
        Ed.Objects.UpdateGhost(c.Pick, false);
        c.Gabs = c.D("a_base_abs");
        Check(Ed.Objects.GhostAbsolute is { } ga && Math.Abs(ga.x - c.Gabs[0]) < 1e-9 && Math.Abs(ga.y - c.Gabs[1]) < 1e-9
              && Math.Abs(Ed.Objects.Ghost!.Position.X - (c.Gabs[0] - c.Ox)) < 1e-3 && Math.Abs(Ed.Objects.Ghost.Position.Z + (c.Gabs[1] - c.Oy)) < 1e-3 && Ed.Objects.Ghost.Visible,
            $"the ghost follows the pick point, snapped to 0.5 m ({Ed.Objects.GhostAbsolute})");
        var freePick = PlanGround(c, "a_base", 0.13, -0.2);
        Ed.Objects.UpdateGhost(freePick, true);
        Check(Ed.Objects.GhostAbsolute is { } gf && Math.Abs(gf.x - Math.Round(c.Ox + freePick.X, 3)) < 1e-9 && Math.Abs(gf.x - c.Gabs[0]) > 0.05,
            $"Ctrl (free) skips the snapping: {Ed.Objects.GhostAbsolute}");
        Ed.Objects.UpdateGhost(PlanGround(c, "outside"), false);
        Check(Ed.Objects.Ghost!.Visible, "the ghost follows the cursor outside every parcel too (an object belongs to no parcel)");
        Ed.Objects.UpdateGhost(c.Pick, false);
        await PressWait(Key.R);
        Check(Math.Abs(Ed.Objects.GhostRotation - 15) < 1e-9 && Math.Abs(Ed.Objects.Ghost!.RotationDegrees.Y - 15) < 1e-3, "R rotates the ghost +15");
        await PressWait(Key.R);
        await PressWait(Key.R, shift: true);
        await PressWait(Key.R, shift: true);
        await PressWait(Key.R, shift: true);
        Check(Math.Abs(Ed.Objects.GhostRotation - 345) < 1e-9, $"Shift+R rotates -15 (R, R, 3x Shift+R = 345): {Ed.Objects.GhostRotation}");
        await PressWait(Key.R);
        Check(Math.Abs(Ed.Objects.GhostRotation) < 1e-9, "back at 0");
    }

    // placing, staying armed
    async Task PlaceObjects(ObjectsCtx c)
    {
        await Op(c, "t1", () => Ed.Objects.PlaceAt(c.Pick));
        Check(Ed.Objects.PlacementAsset == c.CAR && Ed.Objects.Ghost != null, "placement stays armed after placing");
        Check(_map.ObjectNodes.ContainsKey("o-0001") && _map.ObjectNodes["o-0001"].GetChild(0).GetChildCount() > 0, "o-0001 (a pack model) is instanced with the model's meshes");
        SceneMatches(c, "after placing the car");
        Ed.Objects.ArmPlacement(c.SLAB);
        Ed.Objects.RotateGhost(15); await PressWait(Key.R);
        Check(Math.Abs(Ed.Objects.GhostRotation - 30) < 1e-9 && Ed.Objects.PlacementAsset == c.SLAB, "choosing another asset keeps the armed state; ghost rotation 30");
        await Op(c, "t2", () => Ed.Objects.PlaceAt(PlanGround(c, "a_base", 2.1, 1.45)));
        await Op(c, "t3", () => Ed.Objects.PlaceAt(PlanGround(c, "b_base", 0.1, 0.1)));
        Check(_map.ObjectNodes["o-0002"].GetParent().Name == $"objects_{c.A}" && _map.ObjectNodes["o-0003"].GetParent().Name == $"objects_{c.B}",
            "objects sit under the container of the parcel they were placed on (primitive slab too)");
        SceneMatches(c, "after three placements");
        // Esc leaves placement mode
        await PressWait(Key.Escape, frames: 3);
        Check(Ed.Objects.PlacementAsset == null && Ed.Objects.Ghost == null, "Esc disarms and frees the ghost");
        Ed.Objects.ArmPlacement(c.CAR);
        Ed.Objects.DisarmPlacement();
        Check(Ed.Objects.PlacementAsset == null, "DisarmPlacement (right click) also leaves the mode");
    }

    // ---- 3. selecting: a ray against the object's box takes priority over the parcel ----
    async Task SelectObject(ObjectsCtx c)
    {
        c.Car = GOf(c, "o-0001");
        Ed.Click(null, false);
        var pickId = Ed.Objects.PickObject(c.Car + new Vector3(0, 30, 0), Vector3.Down);
        var pickMiss = Ed.Objects.PickObject(c.Car + new Vector3(30, 30, 0), Vector3.Down);
        Check(pickId == "o-0001" && pickMiss == null, $"ray down onto the car hits o-0001 (got {pickId}), a ray 30 m aside nothing ({pickMiss})");
        Check(Ed.Objects.SelectObject("o-0001") && Ed.Objects.SelectedObjectId == "o-0001", "selecting an object");
        await Frame(3);
        Ed.RefreshNow();
        Check(Ed.Objects.Gizmo.Visible && Ed.Objects.Gizmo.Radius > 1.0f && Ed.Objects.Panel.FieldsShown, $"the gizmo (box, ring, arrow) is visible, ring radius {Ed.Objects.Gizmo.Radius:F2} m, the panel shows the fields");
        var gc = _map.ObjectNodes["o-0001"].GlobalPosition;
        Check(Ed.Objects.Gizmo.OnRing(gc + new Vector3(Ed.Objects.Gizmo.Radius, 0, 0)) && !Ed.Objects.Gizmo.OnRing(gc) && !Ed.Objects.Gizmo.OnRing(gc + new Vector3(Ed.Objects.Gizmo.Radius + 3, 0, 0)),
            "a point on the ring is a rotation handle; the object's centre and far points are not");
        Check(Math.Abs(Ed.Objects.Panel.XSpin.Value - Ed.Objects.ObjectData("o-0001")!.X) < 1e-3 && Math.Abs(Ed.Objects.Panel.ScaleSpin.Value - 1) < 1e-9 && !Ed.Objects.Panel.ColourSet, $"the fields show the object's values (x {Ed.Objects.Panel.XSpin.Value} vs {Ed.Objects.ObjectData("o-0001")!.X}, scale {Ed.Objects.Panel.ScaleSpin.Value})");
    }

    // ---- 4. drag to move (0.5 m grid, Ctrl = free), one save per gesture ----
    async Task DragToMove(ObjectsCtx c)
    {
        var before = c.Disk();
        Check(Ed.Objects.StartMove("o-0001", c.Car) && Ed.Objects.Dragging, "press on the object starts a move");
        Ed.Objects.DragTo(PlanGround(c, "a_base", 2.3, 1.2), false);
        var preview = _map.ObjectNodes["o-0001"].GlobalPosition;
        Check(c.Disk() == before && Math.Abs(preview.X - (c.Gabs[0] - c.Ox + 2.5)) < 0.01 && Math.Abs(preview.Z + (c.Gabs[1] - c.Oy + 1.0)) < 0.01 && Ed.Edits.SaveCount == 3,
            "while dragging only the view moves (snapped), the file is untouched");
        await Op(c, "t4", () => Ed.Objects.EndDrag());
        SceneMatches(c, "after the drag");
        var grab = GOf(c, "o-0001") + new Vector3(0.4f, 0, -0.3f);
        Ed.Objects.StartMove("o-0001", grab);
        Ed.Objects.DragTo(grab + new Vector3(1.0f, 0, 0), false);
        await Op(c, "t5", () => Ed.Objects.EndDrag());
        Ed.Objects.StartMove("o-0001", GOf(c, "o-0001"));
        Ed.Objects.DragTo(GOf(c, "o-0001") + new Vector3(0.125f, 0, 0.25f), true);
        await Op(c, "t6", () => Ed.Objects.EndDrag());
        var s1 = Ed.Edits.SaveCount;
        Ed.Objects.StartMove("o-0001", GOf(c, "o-0001"));
        Check(!Ed.Objects.EndDrag() && Ed.Edits.SaveCount == s1, "a click without dragging saves nothing");
    }

    // ---- 5. rotate with the ring (15 degrees, Ctrl = free) ----
    async Task RotateWithRing(ObjectsCtx c)
    {
        Ed.Objects.StartRotate("o-0001");
        Check(Ed.Objects.DragIsRotation, "press on the ring starts a rotation");
        Ed.Objects.DragTo(PlanGround(c, "rot1_pt"), false);
        Check(Math.Abs(Mathf.Wrap((float)(Yaw(_map.ObjectNodes["o-0001"]) - 30), -180, 180)) < 0.01, "the preview turns to the snapped angle");
        await Op(c, "t7", () => Ed.Objects.EndDrag());
        Ed.Objects.StartRotate("o-0001");
        Ed.Objects.DragTo(PlanGround(c, "rot2_pt"), true);
        await Op(c, "t8", () => Ed.Objects.EndDrag());
        SceneMatches(c, "after rotating");
    }

    // ---- 6. cross-parcel move re-homes; outside every parcel is refused ----
    async Task CrossParcelMove(ObjectsCtx c)
    {
        Ed.Objects.StartMove("o-0001", GOf(c, "o-0001"));
        Ed.Objects.DragTo(PlanGround(c, "b_base", 1.1, 0.9), false);
        await Op(c, "t9", () => Ed.Objects.EndDrag());
        Check(_map.ObjectNodes["o-0001"].GetParent().Name == $"objects_{c.B}", "after the move the node sits under the container of the parcel it now stands in (moved, not duplicated)");
        SceneMatches(c, "after the cross-parcel move");
    }

    // ---- 7. field edits (one save each), through the real controls where it counts ----
    async Task FieldEdits(ObjectsCtx c)
    {
        var cur = Ed.Objects.ObjectData("o-0001")!;
        await Op(c, "t10", () => Ed.Objects.SetObjectPosition(cur.X + 2.0, cur.Y));
        Ed.RefreshNow();
        Check(Math.Abs(Ed.Objects.Panel.XSpin.Value - (cur.X + 2.0)) < 1e-3, "the position field follows");
        await Op(c, "t11", () => { Ed.Objects.Panel.RotationSpin.Value = 100; return true; }); // the real SpinBox signal path
        await Op(c, "t12", () => Ed.Objects.SetObjectScale(1.5));
        Check(Math.Abs(_map.ObjectNodes["o-0001"].Scale.X - 1.5) < 1e-4, "scale 1.5 shows at once");
        var sf0 = Ed.Edits.SaveCount;
        Check(!Ed.Objects.SetObjectScale(0) && !Ed.Objects.SetObjectScale(-1) && !Ed.Objects.SetObjectColour("red") && !Ed.Objects.SetObjectColour("#12345") && Ed.Edits.SaveCount == sf0, "scale <= 0 and bad colours are refused, nothing saved");
        await Op(c, "t13", () => Ed.Objects.SetObjectColour("#AABBCC"));
        Ed.RefreshNow();
        var tinted = _map.ObjectNodes["o-0001"].FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Any(m => m.MaterialOverride is StandardMaterial3D { AlbedoColor: var col } && col.ToHtml(false) == "aabbcc");
        Check(tinted && Ed.Objects.Panel.ColourSet, "the colour override shows at once and the 'set' box is on");
        await Op(c, "t14", () => Ed.Objects.SetObjectColour(null));
        await Op(c, "t15", () => Ed.Objects.SetObjectScale(1.0));
    }

    // ---- 8. keys: nudge, duplicate, delete ----
    async Task KeysNudgeDuplicateDelete(ObjectsCtx c)
    {
        var n0 = Ed.Objects.ObjectData("o-0001")!;
        await Op(c, "t16", () => { Press(Key.Right); return true; }, frames: 2);
        await Frame(2);
        Check(Math.Abs(Ed.Objects.ObjectData("o-0001")!.X - (n0.X + 0.5)) < 1e-9, "Right nudges 0.5 m east");
        await Op(c, "t17", () => { Press(Key.Up); return true; }, frames: 2);
        await Frame(2);
        Check(Math.Abs(Ed.Objects.ObjectData("o-0001")!.Y - (n0.Y + 0.5)) < 1e-9, "Up nudges 0.5 m north (camera looks north)");
        await Op(c, "t18", () => { Press(Key.Left); return true; }, frames: 2);
        await Op(c, "t19", () => { Press(Key.Down); return true; }, frames: 2);
        await Frame(2);
        Check(Math.Abs(Ed.Objects.ObjectData("o-0001")!.X - n0.X) < 1e-9 && Math.Abs(Ed.Objects.ObjectData("o-0001")!.Y - n0.Y) < 1e-9, "Left and Down nudge back");
        await Op(c, "t20", () => { Press(Key.D, ctrl: true); return true; }, frames: 2);
        await Frame(2);
        Check(Ed.Objects.SelectedObjectId == "o-0004" && Ed.Objects.ObjectData("o-0004") is { } dup && Math.Abs(dup.X - (n0.X + 1.0)) < 1e-9 && Math.Abs(dup.Y - n0.Y) < 1e-9 && dup.Asset == c.CAR,
            $"Ctrl+D duplicates 1 m east and selects the copy ({Ed.Objects.SelectedObjectId})");
        SceneMatches(c, "after duplicating");
        await Op(c, "t21", () => { Press(Key.Delete); return true; }, frames: 2);
        await Frame(2);
        Check(Ed.Objects.SelectedObjectId == null && Ed.Objects.ObjectData("o-0004") == null && !_map.ObjectNodes.ContainsKey("o-0004") && !Ed.Objects.Gizmo.Visible, "Delete removes the object, the node and the gizmo go");
        SceneMatches(c, "after deleting");
        Check(!Ed.Objects.DeleteSelected() && !Ed.Objects.DuplicateSelected() && !Ed.Objects.NudgeObject(1, 0), "with nothing selected delete, duplicate and nudge do nothing");
    }

    // ---- 9. mixed changes still go through parcel-run; objects stay correct after the fragment swap ----
    async Task MixedChangesStillRun(ObjectsCtx c)
    {
        Ed.Click(null, false); Ed.Click(c.A, false); Ed.Click(c.B, true);
        await Frame();
        var runsG = _watcher!.RunCount;
        await Op(c, "t22", () => { Ed.Groups.GroupSelected(); return Ed.Chrome.StatusMessage.StartsWith("saved"); }, run: true);
        SceneMatches(c, "after the group's parcel-run swapped the fragment in");
        Ed.Objects.SelectObject("o-0002");
        await Op(c, "t23", () => Ed.Objects.NudgeObject(0.5, 0));
        Check(_watcher.RunCount == runsG + 1, "with the group in place an objects-only edit still needs no parcel-run");
        Ed.Click(c.A, false); Ed.Click(c.B, true);
        await Frame();
        Ed.RefreshNow();
        Check(Ed.Fence.Target is { IsGroup: true }, "the selection is the group");
        Ed.Fence.SetMode("none");
        await Op(c, "t24", () => Ed.Fence.Apply(), run: true);
        SceneMatches(c, "after a fence Apply (fragment swap)");
        await Op(c, "t25", () => Ed.Objects.PlacementAsset == null && Ed.Objects.ArmPlacement(c.CAR) && Ed.Objects.PlaceAt(PlanGround(c, "a_base", -1.1, -1.4)));
        Ed.Objects.DisarmPlacement();
        SceneMatches(c, "after placing on a parcel that has a swapped fragment");
    }

    // ---- 10. session undo / redo across the whole sequence ----
    async Task UndoRedoWalk(ObjectsCtx c)
    {
        var n = c.Saves.Count - 1;
        Check(Ed.Edits.History.UndoDepth == n && Ed.Edits.History.RedoDepth == 0, $"every save is on the undo stack ({Ed.Edits.History.UndoDepth} of {n})");
        for (var k = n; k >= 1; k--) await StepUndoRedo(c, true, k);
        Check(Ed.Edits.History.UndoDepth == 0 && Ed.Edits.History.RedoDepth == n && c.Disk() == c.Saves[0].Text, $"everything undone: the file is the seed again, redo stack {Ed.Edits.History.RedoDepth}");
        Check(!Ed.Edits.Undo() && Ed.Chrome.StatusMessage.StartsWith("nothing to undo"), "undo on an empty stack says so");
        for (var k = 1; k <= n; k++) await StepUndoRedo(c, false, k);
        Check(Ed.Edits.History.RedoDepth == 0 && Ed.Edits.History.UndoDepth == n && c.Disk() == c.Saves[n].Text, "everything redone: the file is the final state again");
    }

    // the key bindings: Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z
    async Task UndoRedoKeys(ObjectsCtx c)
    {
        var n = c.Saves.Count - 1;
        var depth = Ed.Edits.History.UndoDepth;
        await PressWait(Key.Z, ctrl: true, frames: 3);
        Check(Ed.Edits.History.UndoDepth == depth - 1 && Ed.Chrome.StatusMessage.StartsWith("undo: ") && c.Disk() == c.Saves[n - 1].Text, $"Ctrl+Z undoes: '{Ed.Chrome.StatusMessage}'");
        await WaitUntil(() => !_watcher!.Busy, 5);
        await PressWait(Key.Y, ctrl: true, frames: 3);
        Check(Ed.Edits.History.UndoDepth == depth && Ed.Chrome.StatusMessage.StartsWith("redo: ") && c.Disk() == c.Saves[n].Text, $"Ctrl+Y redoes: '{Ed.Chrome.StatusMessage}'");
        await PressWait(Key.Z, ctrl: true, frames: 3);
        await PressWait(Key.Z, ctrl: true, shift: true, frames: 3);
        Check(Ed.Edits.History.UndoDepth == depth && c.Disk() == c.Saves[n].Text, "Ctrl+Shift+Z redoes too");
        // a new edit clears the redo stack
        await PressWait(Key.Z, ctrl: true, frames: 3);
        Check(Ed.Edits.History.RedoDepth == 1, "one redo step available");
        Ed.Objects.ArmPlacement(c.SLAB);
        await Op(c, "t26", () => Ed.Objects.PlaceAt(PlanGround(c, "a_base", 2.1, -1.55)));
        Ed.Objects.DisarmPlacement();
        Check(Ed.Edits.History.RedoDepth == 0 && !Ed.Edits.Redo() && Ed.Chrome.StatusMessage.StartsWith("nothing to redo"), "a new edit clears the redo stack");
    }

    // ---- 11. undo only touches the entries its operation touched ----
    async Task UndoKeepsExternalChange(ObjectsCtx c)
    {
        var root = EditsFile.Load(c.EditsPath);
        var stale = (SortedDictionary<string, object?>)((SortedDictionary<string, object?>)root["parcels"]!)["P-9999"]!;
        stale["fence"] = new SortedDictionary<string, object?> { ["mode"] = "auto" };
        var other = EditsFile.Dump(root);
        File.WriteAllText(c.EditsPath + ".tmp", other); File.Move(c.EditsPath + ".tmp", c.EditsPath, true);
        await WaitUntil(() => _watcher!.InstantCount >= 0 && !_watcher.Busy, 5);
        await Frame(40);
        var d0 = Ed.Edits.History.UndoDepth;
        Check(Ed.Edits.Undo() && Ed.Edits.History.UndoDepth == d0 - 1, "undo after an external change");
        var rootAfter = EditsFile.Load(c.EditsPath);
        var staleAfter = ObjectEdits.Parcels(rootAfter)["P-9999"] as SortedDictionary<string, object?>;
        Check(staleAfter?["fence"] is SortedDictionary<string, object?> sfe && sfe["mode"] as string == "auto" && ObjectEdits.All(rootAfter).All(t => (string)t.obj["id"]! != "o-0004"),
            "the undone placement is gone, the external change to P-9999 is kept (only the touched entries are restored)");
        await PressWait(Key.Y, ctrl: true, frames: 3);
        Check(ObjectEdits.All(EditsFile.Load(c.EditsPath)).Any(t => (string)t.obj["id"]! == "o-0004"), "redo puts the placement back");
    }

    // ---- 12. the undo stack is capped at 200 ----
    void UndoStackCap()
    {
        Ed.Objects.SelectObject("o-0001");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 205; i++) Ed.Objects.NudgeObject(i % 2 == 0 ? 0.5 : -0.5, 0);
        GD.Print($"205 nudges in {sw.ElapsedMilliseconds} ms");
        Check(Ed.Edits.History.UndoDepth == UndoHistory.Cap && Ed.Edits.History.RedoDepth == 0, $"the undo stack holds at most {UndoHistory.Cap} ({Ed.Edits.History.UndoDepth})");
    }

    // ---- 13. latency of an object edit ----
    void LatencyOfObjectEdit(ObjectsCtx c)
    {
        GD.Print($"latency: {c.Latencies.Count} editor saves, save-to-visible min {c.Latencies.Min():F1} ms, mean {c.Latencies.Average():F1} ms, max {c.Latencies.Max():F1} ms (instant path, includes reading, validating and writing the file)");
        Check(c.Latencies.Max() < 150, $"every objects-only save was visible within 150 ms (max {c.Latencies.Max():F1} ms)");
    }

    // an external writer (MCP, text editor): found by the poll, at most 0.5 s plus the same update
    async Task ExternalWriterLatency(ObjectsCtx c)
    {
        var root = EditsFile.Load(c.EditsPath);
        ObjectEdits.Add(root, ObjectEdits.Make("o-9001", c.CAR, c.Ox + c.D("a_base")[0] + 0.0, c.Oy + c.D("a_base")[1] + 4.0, 10.0));
        var text = EditsFile.Dump(root);
        var t0 = Time.GetTicksMsec();
        var inst0 = _watcher!.InstantCount; var runs0 = _watcher.RunCount;
        File.WriteAllText(c.EditsPath + ".tmp", text); File.Move(c.EditsPath + ".tmp", c.EditsPath, true);
        var seen = await WaitUntil(() => _map.ObjectNodes.ContainsKey("o-9001"), 3);
        var ms = Time.GetTicksMsec() - t0;
        GD.Print($"latency: external write to visible object {ms} ms (poll 500 ms + update {_watcher.LastInstantMs:F1} ms)");
        Check(seen && ms < 800 && _watcher.InstantCount == inst0 + 1 && _watcher.RunCount == runs0, $"an external objects-only write shows within the poll interval and needs no parcel-run ({ms} ms)");
    }

    // ---- 14. an object belongs to no parcel: it can stand, move and go on ground that is none ----
    async Task ObjectOnNoParcel(ObjectsCtx c)
    {
        const string NoGroup = Drycastle.MapLoader.Objects.PlacedObject.NoParcelGroup;
        var idsBefore = ObjectEdits.All(EditsFile.Load(c.EditsPath)).Select(t => (string)t.obj["id"]!).ToHashSet();
        var runsOut = _watcher!.RunCount;
        Ed.Objects.ArmPlacement(c.SLAB);
        Check(Ed.Objects.PlaceAt(PlanGround(c, "outside")), $"placing outside every parcel works ('{Ed.Chrome.StatusMessage}')");
        Ed.Objects.DisarmPlacement();
        var after = EditsFile.Load(c.EditsPath);
        var outId = ObjectEdits.All(after).Select(t => (string)t.obj["id"]!).FirstOrDefault(id => !idsBefore.Contains(id)) ?? "";
        Check(outId != "" && Ed.Objects.ObjectData(outId) is { Parcel: "" }, $"the new object {outId} is in the top-level list and stands in no parcel");
        Check(!ObjectEdits.Parcels(after).Values.OfType<SortedDictionary<string, object?>>().Any(e => e.ContainsKey("objects")), "and nothing is listed under a parcel entry");
        await WaitIdle(10);
        Check(_map.ObjectNodes.TryGetValue(outId, out var outNode) && outNode.GetParent().Name == $"objects_{NoGroup}",
            "it is instanced at once, under the container for objects on no parcel");
        Check(_watcher.RunCount == runsOut, "and that needed no parcel-run");
        Ed.Objects.SelectObject(outId);
        Ed.Objects.StartMove(outId, GOf(c, outId));
        Ed.Objects.DragTo(PlanGround(c, "outside", 3.0, 0.0), false);
        Check(Ed.Objects.EndDrag() && Ed.Objects.ObjectData(outId) is { Parcel: "" }, "dragging it to another spot that is no parcel works too");
        Check(Ed.Objects.DeleteSelected() && ObjectEdits.Find(EditsFile.Load(c.EditsPath), outId) == null, "and so does deleting it");
    }
}
