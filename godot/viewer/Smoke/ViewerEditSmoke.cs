using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Drycastle.Viewer;

/// <summary>
/// `--edit-smoke --edit-smoke-plan plan.json` (run by tools/viewer_edit_smoke.py, `make viewer-edit-smoke`):
/// drives the hot reload end to end in a headless viewer and asserts the result. The plan names the
/// edits file the watcher watches, the step files to copy into it, the parcel to edit and a probe
/// across one of its fence sections.
/// </summary>
public partial class Viewer
{
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

    sealed class EditCtx : SmokeCtx
    {
        public readonly string Fence, ObjId, ObjNode;
        public readonly JsonElement Probe;
        public readonly List<string> BakedIds;
        public int BaseObjects, Nodes1, Shapes1, Nodes2;

        public EditCtx(string planPath, string editsPath) : base(planPath, editsPath)
        {
            Fence = S("fence_parcel");
            Probe = Plan.GetProperty("probe");
            ObjId = S("object_id");
            ObjNode = $"object_{ObjId}";
            BakedIds = Plan.GetProperty("baked_object_ids").EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        public string Step(string name) => Plan.GetProperty("steps").GetProperty(name).GetString()!;
    }

    Task RunEditSmoke() => RunSmoke("edit", EditSmokeBody);

    async Task EditSmokeBody()
    {
        _player.InputEnabled = false;
        await Physics(3);
        using var c = new EditCtx(_opts["edit-smoke-plan"], _watcher!.EditsPath);

        Baseline(c);
        if (!await PlaceObjectNoFence(c)) return;
        if (!await MoveObject(c)) return;
        await BrokenJson(c);
        if (!await RecoverEmptyParcel(c)) return;
        if (!await BackToDefaults(c)) return;
        await RevertBakedParcel(c);
        await EditorStateFile();
    }

    // baseline: baked objects, baked fence
    void Baseline(EditCtx c)
    {
        var bakedIds = c.BakedIds;
        Check(_map.ObjectNodes.ContainsKey(bakedIds[0]) && _map.ObjectNodes.ContainsKey(bakedIds[1]),
            $"baked objects instanced from objects.json: {string.Join(", ", bakedIds)}");
        var placeholder = _map.ObjectNodes.TryGetValue(bakedIds[1], out var bn) && bn.GetNodeOrNull("Placeholder") != null;
        Check(placeholder && _map.Warnings.Any(w => w.Contains("pack:missing")),
            "unknown asset gives a warning and a placeholder box");
        var slab = _map.ObjectNodes[bakedIds[0]];
        Check(HasMesh(slab), "prim:slab object has a mesh from prims/slab.glb");
        var baseVisual = _map.PropsVisuals.GetValueOrDefault(c.Fence);
        Check(baseVisual is { Visible: true } && ProbeHits(c.Probe), $"baseline: {c.Fence} baked fence visible and blocks the probe ray");
        c.BaseObjects = _map.ObjectNodes.Count;
    }

    // 1: fence none, one placed sedan
    async Task<bool> PlaceObjectNoFence(EditCtx c)
    {
        if (!await ApplyStep("1 fence none + object", c.EditsPath, c.Step("1"))) return false;
        var fence = c.Fence;
        var node = _map.ObjectNodes.GetValueOrDefault(c.ObjId);
        Check(node != null && node.Name == c.ObjNode && node.IsInsideTree(), $"(i) object node {c.ObjNode} appears");
        if (node != null)
        {
            var o = c.Plan.GetProperty("object_local");
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
        Check(shapes.Count > 0 && shapes.All(s => s.Disabled) && dynColliders == 0 && !ProbeHits(c.Probe),
            $"(iii) fence of {fence} has no colliders (baked disabled={shapes.Count}, dynamic={dynColliders}, probe ray blocked={ProbeHits(c.Probe)})");
        Check(_map.ObjectNodes.Count == c.BaseObjects + 1 && _map.ObjectNodes.ContainsKey(c.BakedIds[0]),
            "other parcels' baked objects untouched");
        c.Nodes1 = CountNodes(_map.Root);
        c.Shapes1 = CountOfType<CollisionShape3D>(_map.Root);
        return true;
    }

    // 2: another edit replaces the first one without leaking
    async Task<bool> MoveObject(EditCtx c)
    {
        if (!await ApplyStep("2 move object", c.EditsPath, c.Step("2"))) return false;
        var node2 = _map.ObjectNodes.GetValueOrDefault(c.ObjId);
        var o2 = c.Plan.GetProperty("object_local_2");
        var moved = node2 != null && Math.Abs(node2.GlobalPosition.X - o2[0].GetSingle()) < 0.01
                    && Math.Abs(node2.GlobalPosition.Z + o2[1].GetSingle()) < 0.01;
        var objNodesInTree = _map.ObjectsRoot!.GetChildren().SelectMany(ch => ch.GetChildren())
            .Count(n => n.Name.ToString().StartsWith("object_"));
        c.Nodes2 = CountNodes(_map.Root);
        var shapes2 = CountOfType<CollisionShape3D>(_map.Root);
        Check(moved && objNodesInTree == c.BaseObjects + 1 && c.Nodes2 == c.Nodes1 && shapes2 == c.Shapes1
              && _map.DynamicProps.Count == 1,
            $"(iv) second edit replaces without leaking: object moved={moved} object_nodes={objNodesInTree} " +
            $"map_nodes {c.Nodes1}->{c.Nodes2} collision_shapes {c.Shapes1}->{shapes2} dynamic_parcels={_map.DynamicProps.Count}");
        return true;
    }

    // 3: broken JSON must not crash or change anything
    async Task BrokenJson(EditCtx c)
    {
        var appliedBefore = _watcher!.AppliedCount;
        var parseBefore = _watcher.ParseFailedCount;
        PutEdits(c.EditsPath, c.Step("broken"));
        await WaitUntil(() => _watcher.ParseFailedCount > parseBefore, 5);
        await Physics(30);
        Check(_watcher.ParseFailedCount > parseBefore && _watcher.AppliedCount == appliedBefore && !_watcher.Busy
              && CountNodes(_map.Root) == c.Nodes2 && GetTree() != null,
            "(v) broken JSON is ignored: viewer alive, nothing applied, nothing changed");
    }

    // 4: recovery with a valid file: the parcel keeps fence none, the object is removed
    async Task<bool> RecoverEmptyParcel(EditCtx c)
    {
        if (!await ApplyStep("3 empty parcel after broken file", c.EditsPath, c.Step("3"))) return false;
        Check(_map.PropsVisuals[c.Fence].Visible == false && !_map.ObjectNodes.ContainsKey(c.ObjId) && !ProbeHits(c.Probe),
            "recovery: baked props hidden and no fence collider, object removed");
        return true;
    }

    // 5: back to no edits: default fence regenerated by the pipeline blocks the probe again
    async Task<bool> BackToDefaults(EditCtx c)
    {
        if (!await ApplyStep("4 back to defaults", c.EditsPath, c.Step("4"))) return false;
        Check(_map.DynamicProps.ContainsKey(c.Fence) && ProbeHits(c.Probe) && _map.ObjectNodes.Count == c.BaseObjects,
            "defaults regenerated: fence collider is back (dynamic) and the probe ray is blocked again");
        return true;
    }

    // 6: RevertParcel restores the baked nodes
    async Task RevertBakedParcel(EditCtx c)
    {
        _map.RevertParcel(c.Fence);
        await Physics(4);
        Check(_map.PropsVisuals[c.Fence].Visible && !_map.DynamicProps.ContainsKey(c.Fence)
              && _map.PropsShapes[c.Fence].All(s => !s.Disabled) && ProbeHits(c.Probe),
            "RevertParcel restores the baked props and collider");
    }

    // editor-state.json
    async Task EditorStateFile()
    {
        var state = _opts.GetValueOrDefault("state");
        if (state == null) return;
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

