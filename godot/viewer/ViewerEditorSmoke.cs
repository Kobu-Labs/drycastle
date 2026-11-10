using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--editor-smoke --editor-smoke-plan plan.json` (run by editor_smoke.py, `make viewer-editor-smoke`):
/// drives the editor mode through its APIs (no synthetic mouse) in a headless viewer and asserts the
/// result. Also `--editor-shot PNG` for screenshots of the editor mode in a windowed run.
/// </summary>
public partial class Viewer
{
    async Task<bool> WaitIdle(double seconds)
    {
        // no parcel-run in flight and none starting for 2 s in a row
        var t0 = Time.GetTicksMsec();
        var quietSince = Time.GetTicksMsec();
        while ((Time.GetTicksMsec() - t0) / 1000.0 < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_watcher!.Busy) quietSince = Time.GetTicksMsec();
            else if (Time.GetTicksMsec() - quietSince > 2000) return true;
        }
        return false;
    }

    static float Ground(Suchohrad.MapLoader.MapLoader map, Vector3 p) => map.GroundHeightOrMean(p.X, p.Z);

    async Task RunEditorSmoke()
    {
        try { await EditorSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL editor smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "EDITOR SMOKE PASS" : "EDITOR SMOKE FAIL");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    List<string> StateSelected(string statePath)
    {
        try
        {
            using var sd = JsonDocument.Parse(File.ReadAllText(statePath));
            return sd.RootElement.GetProperty("selected").EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        catch (Exception) { return new List<string> { "<unreadable>" }; }
    }

    async Task EditorSmokeBody()
    {
        var ed = _editor!;
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["editor-smoke-plan"]));
        var plan = planDoc.RootElement;
        string S(string k) => plan.GetProperty(k).GetString()!;
        Vector3 World(string k)
        {
            var a = plan.GetProperty(k);
            var x = a[0].GetSingle(); var z = -a[1].GetSingle();
            return new Vector3(x, _map.GroundHeightOrMean(x, z), z);
        }
        string A = S("parcel_a"), B = S("parcel_b"), C = S("parcel_c");
        var editsPath = ed.EditsPath;
        var statePath = _opts["state"];

        // ---- canonical writer: Python-written fixture round-trips byte for byte, floats match repr ----
        var fixture = File.ReadAllText(S("fixture"));
        Check(EditsFile.Dump(EditsFile.Parse(fixture)) == fixture, "C# writer round-trips the dump_edits fixture byte for byte");
        var badFloat = plan.GetProperty("floats").EnumerateArray()
            .Select(f => (raw: f[0].GetString()!, want: f[1].GetString()!))
            .Where(f => EditsFile.PyNumber(f.raw) != f.want).Take(3).ToList();
        Check(badFloat.Count == 0, $"number formatting matches Python for {plan.GetProperty("floats").GetArrayLength()} samples"
              + (badFloat.Count > 0 ? $" (first miss {badFloat[0].raw} -> {EditsFile.PyNumber(badFloat[0].raw)} want {badFloat[0].want})" : ""));
        var seedText = File.ReadAllText(editsPath);
        Check(EditsFile.Dump(EditsFile.Parse(seedText)) == seedText, "seed edits file is canonical and round-trips");

        // ---- walk mode, then F2 ----
        await Physics(3);
        _player.Velocity = Vector3.Zero;
        _player.GlobalPosition = _map.SpawnPoints.First(sp => sp.Kind == "garden" && !float.IsNaN(_map.GroundHeight(sp.Position.X, sp.Position.Z))).Position + Vector3.Up * 2f; // as in the plain smoke: land on the real ground
        for (var i = 0; i < 600 && !(i > 10 && _player.IsOnFloor()); i++) await Physics(1);
        await Physics(10);
        var mouseAtStart = Input.MouseMode;
        var walkPos = _player.GlobalPosition;
        var walkYaw = _player.RotationDegrees.Y;
        var walkCamPos = _player.Camera.GlobalPosition;
        var inputBefore = _player.InputEnabled;
        Check(!ed.Active && _player.Camera.Current, "starts in walk mode with the player camera");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F2, Pressed = true });
        await Physics(3);
        Check(ed.Active && ed.FlyCam.Current && !_player.Visible && !_player.InputEnabled
              && ed.FlyCam.GlobalPosition.DistanceTo(walkCamPos) < 0.01f,
            $"F2: editor mode, fly camera current at the player camera, player hidden and frozen (cam {ed.FlyCam.GlobalPosition})");
        Check(Input.MouseMode == Input.MouseModeEnum.Visible, $"mouse visible in editor mode (mode {Input.MouseMode})");
        await Physics(30);
        Check(_player.GlobalPosition.DistanceTo(walkPos) < 0.01f, "player stays frozen while in editor mode");

        // ---- top-down snap and restore ----
        var camBefore = ed.FlyCam.GlobalPosition;
        var basisBefore = ed.FlyCam.GlobalBasis;
        ed.ToggleTopDown();
        var down = ed.TopDown && ed.FlyCam.GlobalBasis.Z.Y > 0.999f;
        Check(down, $"T snaps to top-down (camera z {ed.FlyCam.GlobalPosition.Y:F1})");
        ed.ToggleTopDown();
        Check(!ed.TopDown && ed.FlyCam.GlobalPosition.DistanceTo(camBefore) < 1e-3f
              && ed.FlyCam.GlobalBasis.Z.DistanceTo(basisBefore.Z) < 1e-3f, "T again restores the previous camera");
        var speed0 = ed.Speed;
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
        await Physics(2);
        Check(ed.Speed > speed0, $"wheel changes fly speed ({speed0:F0} -> {ed.Speed:F0} m/s)");

        // ---- picking ----
        var pa = World("point_a"); var pb = World("point_b"); var pc = World("point_c");
        Check(_map.ParcelAt(pa.X, pa.Z)?.Id == A && _map.ParcelAt(pb.X, pb.Z)?.Id == B && _map.ParcelAt(pc.X, pc.Z)?.Id == C,
            $"point-in-polygon lookup finds {A}, {B}, {C}");
        Check(_map.ParcelAt(1e6f, 1e6f) == null, "far away point is in no parcel");
        var t0 = Time.GetTicksUsec();
        var found = 0;
        for (var i = 0; i < 20000; i++) if (_map.ParcelAt(pa.X + (i % 200) * 0.5f, pa.Z + (i / 200) * 0.5f) != null) found++;
        var perLookup = (Time.GetTicksUsec() - t0) / 20000.0;
        Check(perLookup < 50, $"point lookup is fast: {perLookup:F2} us per lookup over {_map.Parcels.Count} parcels");
        ed.LookFrom(pa + Vector3.Up * 60f, pa);
        var size = GetViewport().GetVisibleRect().Size;
        var picked = ed.PickScreen(size / 2, out var pt);
        Check(picked?.Id == A, $"camera ray through the screen centre picks {A} (hit {pt})");
        ed.LookFrom(new Vector3(1e5f, 400f, 1e5f), new Vector3(1e5f, 0, 1e5f - 1));
        Check(ed.PickScreen(size / 2, out _) == null, "ray into empty space picks nothing");

        // ---- selection ----
        var selEvents = 0;
        ed.SelectionChanged += () => selEvents++;
        ed.Click(A, false);
        Check(ed.Selection.SetEquals(new[] { A }), $"click selects {A}");
        ed.Click(B, true);
        Check(ed.Selection.SetEquals(new[] { A, B }), $"shift+click adds {B}");
        ed.Click(B, true);
        Check(ed.Selection.SetEquals(new[] { A }), $"shift+click again removes {B}");
        ed.Click(B, true);
        ed.Click(null, true);
        Check(ed.Selection.SetEquals(new[] { A, B }), "shift+click on empty space keeps the selection");
        ed.Click(C, false);
        Check(ed.Selection.SetEquals(new[] { C }), "plain click replaces the selection");
        ed.Click(null, false);
        Check(ed.Selection.Count == 0, "click on empty space clears the selection");
        ed.Click(A, false); ed.Click(B, true);
        await Physics(3);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(selEvents > 0 && ed.PanelSelectionText.Contains(A) && ed.PanelSelectionText.Contains(B) && ed.GroupButtonEnabled,
            "panel lists the selection and enables [Group selected]");

        // ---- outlines before grouping ----
        var selNode = ed.OutlineNode(EditorMode.OutlineSelected);
        var noCollision = ed.OutlineRoot.GetChildren().All(n => n is MeshInstance3D m && m.GetChildCount() == 0);
        Check(selNode?.Mesh != null && ed.SegmentCount(EditorMode.OutlineSelected) == plan.GetProperty("sel_segments").GetInt32() && noCollision,
            $"selected parcels have an outline mesh ({ed.SegmentCount(EditorMode.OutlineSelected)} segments) without collision");
        if (selNode?.Mesh is { } sm)
        {
            var v = sm.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var lift = v.Take(40).Select(p => p.Y - _map.GroundHeightOrMean(p.X, p.Z)).Min();
            Check(lift >= 0.2f && lift <= 0.6f, $"outline hangs {lift:F2} m above the ground");
        }
        ed.SetHover(C);
        ed.RebuildOutlines();
        Check(ed.OutlineNode(EditorMode.OutlineHover)?.Mesh != null && ed.SegmentCount(EditorMode.OutlineHover) == plan.GetProperty("c_segments").GetInt32(),
            "hovered parcel has a (subtle) outline");
        ed.SetHover(null);
        var baseGroupSegs = ed.SegmentCount(EditorMode.OutlineGroup);
        Check(baseGroupSegs == plan.GetProperty("existing_group_segments").GetInt32(),
            $"existing group outlined as one shape ({baseGroupSegs} segments)");

        // ---- refusals ----
        var fileBefore = File.ReadAllText(editsPath);
        ed.Click(C, false); ed.Click(A, true);
        ed.GroupSelected();
        Check(File.ReadAllText(editsPath) == fileBefore && ed.StatusMessage.Contains("already in"),
            $"G refused when a selected parcel is in a group, file untouched ('{ed.StatusMessage}')");
        ed.Click(A, false);
        ed.GroupSelected();
        Check(File.ReadAllText(editsPath) == fileBefore && ed.StatusMessage.Contains("2 or more"), "G refused with fewer than 2 parcels");

        // ---- create a group ----
        ed.Click(A, false); ed.Click(B, true);
        var applied0 = _watcher!.AppliedCount;
        ed.GroupSelected();
        var after = File.ReadAllText(editsPath);
        var gid = _map.Parcels[A].Group;
        Check(gid == plan.GetProperty("next_group").GetString() && _map.Parcels[B].Group == gid && ed.StatusMessage.StartsWith("saved"),
            $"G creates {gid} (next free number) for {A} and {B}, status '{ed.StatusMessage}'");
        File.WriteAllText(S("after_group"), after);
        var doc = EditsFile.Parse(after);
        var members = EditsFile.Members(EditsFile.Groups(doc).GetValueOrDefault(gid ?? ""));
        Check(members.SequenceEqual(new[] { A, B }.OrderBy(x => x, StringComparer.Ordinal)) && EditsFile.Dump(doc) == after,
            "edits.json holds the group, canonical text (also checked against dump_edits by the driver)");
        var keptStale = ((System.Collections.Generic.SortedDictionary<string, object?>)doc["parcels"]!).ContainsKey("P-9999")
                        && EditsFile.Groups(doc).ContainsKey(plan.GetProperty("existing_group").GetString()!);
        Check(keptStale, "unknown parcel entries and other groups preserved");
        await Physics(2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var gseg = ed.SegmentCount(EditorMode.OutlineGroup);
        var expectGroup = baseGroupSegs + plan.GetProperty("group_ab_segments").GetInt32();
        Check(gseg == expectGroup && gseg < baseGroupSegs + plan.GetProperty("sel_segments").GetInt32() && plan.GetProperty("shared_edges").GetInt32() > 0
              && ed.OutlineNode(EditorMode.OutlineGroup)?.Mesh != null,
            $"group outline drawn without shared edges: {gseg} segments (members alone would give {baseGroupSegs + plan.GetProperty("sel_segments").GetInt32()}, shared edges {plan.GetProperty("shared_edges").GetInt32()})");

        // watcher regenerates the new group's parcels by itself
        var ok = await WaitUntil(() => _watcher.AppliedCount > applied0 || _watcher.FailedCount > 0, 40);
        Check(ok && _watcher.AppliedCount > applied0 && ed.StatusMessage.StartsWith("applied in"),
            $"watcher regenerated the group parcels: status '{ed.StatusMessage}'");

        // ---- editor-state.json carries the selection ----
        await WaitUntil(() => StateSelected(statePath).SequenceEqual(new[] { A, B }.OrderBy(x => x, StringComparer.Ordinal)), 4);
        var st = StateSelected(statePath);
        Check(st.SequenceEqual(new[] { A, B }.OrderBy(x => x, StringComparer.Ordinal)), $"editor-state.json selected = [{string.Join(", ", st)}]");

        // ---- another writer changes the file; our next save keeps its change ----
        var ext = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(editsPath))!;
        ext["parcels"]!["P-8888"] = System.Text.Json.Nodes.JsonNode.Parse("{\"trees\": {\"count\": 2}}");
        File.WriteAllText(editsPath + ".ext", ext.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(editsPath + ".ext", editsPath, true);
        ed.Click(A, false);
        ed.DissolveGroup();
        var final = File.ReadAllText(editsPath);
        File.WriteAllText(S("final"), final);
        var fd = EditsFile.Parse(final);
        Check(!EditsFile.Groups(fd).ContainsKey(gid!) && ((SortedDictionary<string, object?>)fd["parcels"]!).ContainsKey("P-8888")
              && _map.Parcels[A].Group == null && _map.Parcels[B].Group == null && EditsFile.Dump(fd) == final,
            "dissolve reloaded the file first (other writer's P-8888 kept), group gone, canonical");
        await Physics(2);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(ed.SegmentCount(EditorMode.OutlineGroup) == baseGroupSegs, "group outline gone after dissolve");

        // ---- state with a cleared selection, then wait for the watcher to settle ----
        ed.Click(null, false);
        await WaitUntil(() => StateSelected(statePath).Count == 0, 4);
        Check(StateSelected(statePath).Count == 0, "editor-state.json selected is empty after clearing");
        Check(await WaitIdle(60), "watcher settled after the edits");
        Check(_watcher.FailedCount == 0, $"no failed parcel-run (failed={_watcher.FailedCount})");

        // ---- back to walk mode ----
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.F2, Pressed = true });
        await Physics(3);
        Check(!ed.Active && _player.Camera.Current && _player.Visible && _player.InputEnabled == inputBefore
              && _player.GlobalPosition.DistanceTo(walkPos) < 0.01f && Mathf.Abs(_player.RotationDegrees.Y - walkYaw) < 0.01f,
            $"F2 back: player restored at {_player.GlobalPosition} yaw {_player.RotationDegrees.Y:F1}, visible, input on");
        Check(Input.MouseMode == mouseAtStart, $"mouse mode back to what walk mode had (headless may ignore capture) ({mouseAtStart}), now {Input.MouseMode}");
    }

    // ---- screenshots of the editor mode (windowed run) ----

    async Task RunEditorShot()
    {
        var ed = _editor!;
        GetWindow().Size = new Vector2I(1600, 900);
        await Physics(30);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        ed.SetActive(true);
        var ids = (_opts.GetValueOrDefault("select") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < ids.Length; i++) ed.Click(ids[i], i > 0);
        // --shot-edits FILE: write it over the edits file first and wait for the regenerated parcels
        if (_opts.GetValueOrDefault("shot-edits") is { } shotEdits)
        {
            PutEdits(ed.EditsPath, shotEdits);
            await WaitIdle(90);
            ed.ReloadGroups();
        }
        ed.RefreshNow();
        foreach (var title in (_opts.GetValueOrDefault("shot-collapse") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            ed.SetSectionOpen(title, false);
        if (_opts.GetValueOrDefault("shot-gate") is { } sg && int.TryParse(sg, out var gi)) ed.SelectGate(gi);
        if (_flags.Contains("shot-reroll")) ed.Reroll(); // leaves a pending change: shows the unsaved marker
        var at = _opts.GetValueOrDefault("shot-at") ?? ids.FirstOrDefault();
        if (at != null && _map.Parcels.TryGetValue(at, out var info))
        {
            var c = info.Bounds.GetCenter();
            var dist = Mathf.Max(60f, Mathf.Max(info.Bounds.Size.X, info.Bounds.Size.Y) * 1.6f);
            // --shot-focus x,y (map metres, y north) and --shot-dist D look closer at one spot
            if (_opts.GetValueOrDefault("shot-focus")?.Split(',') is { Length: 2 } fp
                && float.TryParse(fp[0], System.Globalization.CultureInfo.InvariantCulture, out var fx)
                && float.TryParse(fp[1], System.Globalization.CultureInfo.InvariantCulture, out var fy))
                c = new Vector2(fx, -fy);
            if (_opts.TryGetValue("shot-dist", out var sd) && float.TryParse(sd, System.Globalization.CultureInfo.InvariantCulture, out var sdv)) dist = sdv;
            var target = new Vector3(c.X, _map.GroundHeightOrMean(c.X, c.Y), c.Y);
            var off = new Vector3(-0.15f, 1f, 0.55f).Normalized() * dist;
            var right = (-off).Cross(Vector3.Up).Normalized(); // camera right
            // the 300 px panel covers the right of the window: shift the view so the parcel sits in the left part
            ed.LookFrom(target + off + right * dist * 0.3f, target + right * dist * 0.3f);
            ed.SetHover(_opts.GetValueOrDefault("hover"));
        }
        var frames = _opts.TryGetValue("frames", out var f) && int.TryParse(f, out var fi) ? fi : 60;
        for (var i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GD.Print($"outline segments: selected={ed.SegmentCount(EditorMode.OutlineSelected)} group={ed.SegmentCount(EditorMode.OutlineGroup)} hover={ed.SegmentCount(EditorMode.OutlineHover)}");
        var path = _opts["editor-shot"];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"editor screenshot: {(err == Error.Ok ? "saved" : "FAILED " + err)} {path} gpu='{RenderingServer.GetVideoAdapterName()}'");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
