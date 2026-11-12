using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--edit-smoke --edit-smoke-plan plan.json` (run by tools/viewer_edit_smoke.py, `make viewer-edit-smoke`):
/// drives the hot reload end to end in a headless viewer and asserts the result. The plan names the
/// edits file the watcher watches, the step files to copy into it, the parcel to edit and a probe
/// across one of its fence sections.
/// </summary>
public partial class Viewer
{
    bool _editOk = true;
    readonly List<(string step, double ms)> _latencies = new();

    void Check(bool pass, string message)
    {
        _editOk &= pass;
        GD.Print($"{(pass ? "PASS" : "FAIL")} {message}");
    }

    static bool HasMesh(Node n) => n.GetChildren().Any(c => c is MeshInstance3D { Mesh: not null } || HasMesh(c));

    static int CountNodes(Node n)
    {
        var c = 1;
        foreach (var ch in n.GetChildren())
            if (!ch.IsQueuedForDeletion()) c += CountNodes(ch);
        return c;
    }

    static int CountOfType<T>(Node n) where T : Node
    {
        var c = n is T ? 1 : 0;
        foreach (var ch in n.GetChildren())
            if (!ch.IsQueuedForDeletion()) c += CountOfType<T>(ch);
        return c;
    }

    /// <summary>Atomic write like the editor and the MCP server do (temp file, then rename).</summary>
    static void PutEdits(string editsPath, string source)
    {
        var tmp = editsPath + ".tmp";
        File.Copy(source, tmp, true);
        File.Move(tmp, editsPath, true);
    }

    async Task<bool> WaitUntil(Func<bool> cond, double seconds)
    {
        var t0 = Time.GetTicksMsec();
        while (!cond())
        {
            if ((Time.GetTicksMsec() - t0) / 1000.0 > seconds) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    async Task<bool> ApplyStep(string name, string editsPath, string source)
    {
        var before = _watcher!.AppliedCount;
        var t0 = Time.GetTicksMsec();
        PutEdits(editsPath, source);
        var ok = await WaitUntil(() => _watcher.AppliedCount > before || _watcher.FailedCount > 0, 30);
        var applied = ok && _watcher.AppliedCount > before;
        var ms = Time.GetTicksMsec() - t0;
        if (applied) _latencies.Add((name, ms));
        Check(applied, $"edit '{name}' applied: edit_to_applied={ms} ms");
        await Physics(4); // deferred collider disable, queued frees
        return applied;
    }

    // Horizontal ray across the fence section at the probe point; true when it hits a body.
    bool ProbeHits(JsonElement probe)
    {
        float x = probe.GetProperty("x").GetSingle(), y = probe.GetProperty("y").GetSingle();
        float nx = probe.GetProperty("nx").GetSingle(), ny = probe.GetProperty("ny").GetSingle();
        var gy = probe.GetProperty("z").GetSingle() + 0.5f;
        var from = new Vector3(x + nx * 1.0f, gy, -(y + ny * 1.0f));
        var to = new Vector3(x - nx * 1.0f, gy, -(y - ny * 1.0f));
        var q = PhysicsRayQueryParameters3D.Create(from, to);
        q.CollideWithBodies = true;
        return GetViewport().World3D.DirectSpaceState.IntersectRay(q).Count > 0;
    }

    async Task RunEditSmoke()
    {
        try { await EditSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL edit smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "EDIT SMOKE PASS" : "EDIT SMOKE FAIL");
        if (_latencies.Count > 0)
            GD.Print("edit_to_applied_ms: " + string.Join(", ", _latencies.Select(l => $"{l.step}={l.ms:F0}"))
                     + $" (mean {_latencies.Average(l => l.ms):F0})");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    async Task EditSmokeBody()
    {
        _player.InputEnabled = false;
        await Physics(3);
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["edit-smoke-plan"]));
        var plan = planDoc.RootElement;
        var editsPath = _watcher!.EditsPath;
        var steps = plan.GetProperty("steps");
        string Step(string n) => steps.GetProperty(n).GetString()!;
        var fence = plan.GetProperty("fence_parcel").GetString()!;
        var probe = plan.GetProperty("probe");
        var objId = plan.GetProperty("object_id").GetString()!;
        var objNode = $"object_{objId}";
        var bakedIds = plan.GetProperty("baked_object_ids").EnumerateArray().Select(e => e.GetString()!).ToList();

        // baseline: baked objects, baked fence
        Check(_map.ObjectNodes.ContainsKey(bakedIds[0]) && _map.ObjectNodes.ContainsKey(bakedIds[1]),
            $"baked objects instanced from objects.json: {string.Join(", ", bakedIds)}");
        var placeholder = _map.ObjectNodes.TryGetValue(bakedIds[1], out var bn) && bn.GetNodeOrNull("Placeholder") != null;
        Check(placeholder && _map.Warnings.Any(w => w.Contains("pack:missing")),
            "unknown asset gives a warning and a placeholder box");
        var slab = _map.ObjectNodes[bakedIds[0]];
        Check(HasMesh(slab), "prim:slab object has a mesh from prims/slab.glb");
        var baseVisual = _map.PropsVisuals.GetValueOrDefault(fence);
        Check(baseVisual is { Visible: true } && ProbeHits(probe), $"baseline: {fence} baked fence visible and blocks the probe ray");
        var baseObjects = _map.ObjectNodes.Count;

        // 1: fence none, one placed sedan
        if (!await ApplyStep("1 fence none + object", editsPath, Step("1"))) return;
        var node = _map.ObjectNodes.GetValueOrDefault(objId);
        Check(node != null && node.Name == objNode && node.IsInsideTree(), $"(i) object node {objNode} appears");
        if (node != null)
        {
            var o = plan.GetProperty("object_local");
            var pos = node.GlobalPosition;
            Check(Math.Abs(pos.X - o[0].GetSingle()) < 0.01 && Math.Abs(pos.Z + o[1].GetSingle()) < 0.01
                  && node.FindChild("Placeholder", true, false) == null && HasMesh(node),
                $"object at the parcel position with the real sedan model (pos {pos})");
        }
        Check(_map.PropsVisuals[fence].Visible == false && _map.DynamicProps.TryGetValue(fence, out var dyn)
              && dyn.GetNodeOrNull($"parcel_{fence}_props") != null,
            $"(ii) baked props of {fence} hidden, dynamic props node dyn_props_{fence} exists");
        var shapes = _map.PropsShapes.GetValueOrDefault(fence) ?? new();
        var dynColliders = _map.DynamicProps.TryGetValue(fence, out var d1) ? CountOfType<CollisionShape3D>(d1) : 0;
        Check(shapes.Count > 0 && shapes.All(s => s.Disabled) && dynColliders == 0 && !ProbeHits(probe),
            $"(iii) fence of {fence} has no colliders (baked disabled={shapes.Count}, dynamic={dynColliders}, probe ray blocked={ProbeHits(probe)})");
        Check(_map.ObjectNodes.Count == baseObjects + 1 && _map.ObjectNodes.ContainsKey(bakedIds[0]),
            "other parcels' baked objects untouched");
        var nodes1 = CountNodes(_map.Root);
        var shapes1 = CountOfType<CollisionShape3D>(_map.Root);

        // 2: another edit replaces the first one without leaking
        if (!await ApplyStep("2 move object", editsPath, Step("2"))) return;
        var node2 = _map.ObjectNodes.GetValueOrDefault(objId);
        var o2 = plan.GetProperty("object_local_2");
        var moved = node2 != null && Math.Abs(node2.GlobalPosition.X - o2[0].GetSingle()) < 0.01
                    && Math.Abs(node2.GlobalPosition.Z + o2[1].GetSingle()) < 0.01;
        var objNodesInTree = _map.ObjectsRoot!.GetChildren().SelectMany(c => c.GetChildren())
            .Count(n => n.Name.ToString().StartsWith("object_"));
        var nodes2 = CountNodes(_map.Root);
        var shapes2 = CountOfType<CollisionShape3D>(_map.Root);
        Check(moved && objNodesInTree == baseObjects + 1 && nodes2 == nodes1 && shapes2 == shapes1
              && _map.DynamicProps.Count == 1,
            $"(iv) second edit replaces without leaking: object moved={moved} object_nodes={objNodesInTree} " +
            $"map_nodes {nodes1}->{nodes2} collision_shapes {shapes1}->{shapes2} dynamic_parcels={_map.DynamicProps.Count}");

        // 3: broken JSON must not crash or change anything
        var appliedBefore = _watcher.AppliedCount;
        var parseBefore = _watcher.ParseFailedCount;
        PutEdits(editsPath, Step("broken"));
        await WaitUntil(() => _watcher.ParseFailedCount > parseBefore, 5);
        await Physics(30);
        Check(_watcher.ParseFailedCount > parseBefore && _watcher.AppliedCount == appliedBefore && !_watcher.Busy
              && CountNodes(_map.Root) == nodes2 && GetTree() != null,
            "(v) broken JSON is ignored: viewer alive, nothing applied, nothing changed");

        // 4: recovery with a valid file: the parcel keeps fence none, the object is removed
        if (!await ApplyStep("3 empty parcel after broken file", editsPath, Step("3"))) return;
        Check(_map.PropsVisuals[fence].Visible == false && !_map.ObjectNodes.ContainsKey(objId) && !ProbeHits(probe),
            "recovery: baked props hidden and no fence collider, object removed");

        // 5: back to no edits: default fence regenerated by the pipeline blocks the probe again
        if (!await ApplyStep("4 back to defaults", editsPath, Step("4"))) return;
        Check(_map.DynamicProps.ContainsKey(fence) && ProbeHits(probe) && _map.ObjectNodes.Count == baseObjects,
            "defaults regenerated: fence collider is back (dynamic) and the probe ray is blocked again");

        // 6: RevertParcel restores the baked nodes
        _map.RevertParcel(fence);
        await Physics(4);
        Check(_map.PropsVisuals[fence].Visible && !_map.DynamicProps.ContainsKey(fence)
              && _map.PropsShapes[fence].All(s => !s.Disabled) && ProbeHits(probe),
            "RevertParcel restores the baked props and collider");

        // editor-state.json
        var state = _opts.GetValueOrDefault("state");
        if (state != null)
        {
            await WaitUntil(() => File.Exists(state), 3);
            try
            {
                using var sd = JsonDocument.Parse(File.ReadAllText(state));
                var cam = sd.RootElement.GetProperty("camera");
                Check(sd.RootElement.GetProperty("format_version").GetInt32() == 1
                      && sd.RootElement.GetProperty("selected").GetArrayLength() == 0
                      && cam.TryGetProperty("x", out _) && cam.TryGetProperty("y", out _) && cam.TryGetProperty("z", out _)
                      && cam.TryGetProperty("yaw_deg", out _), "editor-state.json written with camera and selection");
            }
            catch (Exception e) { Check(false, $"editor-state.json: {e.Message}"); }
        }
    }
}
