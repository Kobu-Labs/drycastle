using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--patches-smoke --patches-smoke-plan plan.json` (run by patches_smoke.py, `make viewer-patches-smoke`): drives the
/// patch hot reload end to end in a headless viewer. The map was exported with three patches; the steps recolour,
/// reshape, add and remove patches, change a patch and a parcel in one file change, survive a broken file and
/// toggle repeatedly to prove that nothing leaks. Reuses the helpers of ViewerEditSmoke.cs.
/// </summary>
public partial class Viewer
{
    static int NodeMonitor() => (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);

    static string[] Ids(IEnumerable<string> ids) => ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // First vertex colour of the regenerated patch node (raw values; compared as sRGB bytes or as linear).
    Color? DynamicColour(string id)
    {
        if (!_map.DynamicPatches.TryGetValue(id, out var c)) return null;
        var mi = c.GetChildren().OfType<MeshInstance3D>().FirstOrDefault(m => m.Mesh != null);
        if (mi == null) return null;
        var colours = mi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Color].AsColorArray();
        return colours.Length > 0 ? colours[0] : null;
    }

    static bool SameColour(Color got, string hex)
    {
        var want = new Color(hex);
        bool Near(Color a, Color b) => Math.Abs(a.R - b.R) < 0.03 && Math.Abs(a.G - b.G) < 0.03 && Math.Abs(a.B - b.B) < 0.03;
        return Near(got, want) || Near(got, want.SrgbToLinear());
    }

    int DynPatchContainers() => _map.Root.GetChildren().Count(c => c.Name.ToString().StartsWith("dyn_patch_") && !c.IsQueuedForDeletion());

    async Task RunPatchesSmoke()
    {
        try { await PatchesSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL patches smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "PATCHES SMOKE PASS" : "PATCHES SMOKE FAIL");
        if (_latencies.Count > 0)
            GD.Print("edit_to_applied_ms: " + string.Join(", ", _latencies.Select(l => $"{l.step}={l.ms:F0}"))
                     + $" (mean {_latencies.Average(l => l.ms):F0})");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    async Task PatchesSmokeBody()
    {
        _player.InputEnabled = false;
        await Physics(3);
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["patches-smoke-plan"]));
        var plan = planDoc.RootElement;
        var editsPath = _watcher!.EditsPath;
        var steps = plan.GetProperty("steps");
        string Step(string n) => steps.GetProperty(n).GetString()!;
        var colours = plan.GetProperty("colours");
        var plot = plan.GetProperty("parcel").GetString()!;

        // baseline: baked patch nodes, nothing regenerated
        var baked = Ids(_map.BakedPatches.Keys);
        var wanted = plan.GetProperty("baseline_patches").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Check(wanted.All(baked.Contains), $"baseline: baked patch nodes for {string.Join(", ", wanted)} (found {string.Join(", ", baked)})");
        Check(_map.DynamicPatches.Count == 0 && Ids(_map.VisiblePatchIds()).SequenceEqual(baked), "baseline: all baked patchs visible, none regenerated");
        foreach (var (id, nodes) in _map.BakedPatches)
            GD.Print($"info: {id} has {nodes.Count} baked node(s) in the chunk files" + (nodes.Count > 1 ? " (split over chunks)" : ""));
        Check(_map.BakedPatches.Values.All(l => l.All(m => m.GetChildCount() == 0)), "patch nodes have no collider");
        var splits = _map.BakedPatches.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();

        async Task<bool> Apply(string name)
        {
            var ok = await ApplyStep(name, editsPath, Step(name));
            await Physics(4);
            return ok;
        }
        bool PatchesClean(string label)
        {
            // every dynamic container is a live child of the map; a swapped patch has no visible baked node
            var live = _map.DynamicPatches.All(kv => GodotObject.IsInstanceValid(kv.Value) && kv.Value.IsInsideTree() && !kv.Value.IsQueuedForDeletion());
            var onlyOne = DynPatchContainers() == _map.DynamicPatches.Count;
            var noDouble = _map.DynamicPatches.Keys.All(id => !_map.BakedPatches.TryGetValue(id, out var l) || l.All(m => !m.Visible));
            Check(live && onlyOne && noDouble, $"{label}: {_map.DynamicPatches.Count} regenerated patch node(s), no stale or double-visible ones");
            return live && onlyOne && noDouble;
        }

        // 1: recolour GP-0001 (the patch over the chunk border; GP-0002 overlaps it)
        int regionRuns = _watcher.PatchRunCount, parcelRuns = _watcher.ParcelRunCount;
        if (!await Apply("recolour")) return;
        var rep = _watcher.LastPatchesReplaced;
        Check(rep.Contains("GP-0001") && !rep.Contains("GP-0003"), $"(i) recolour replaces GP-0001 and not GP-0003: {string.Join(", ", rep)}");
        Check(_watcher.PatchRunCount == regionRuns + 1 && _watcher.ParcelRunCount == parcelRuns, "a patch-only edit runs patch-run once and no parcel-run");
        Check(_map.DynamicPatches.ContainsKey("GP-0001") && _map.BakedPatches["GP-0001"].All(m => !m.Visible),
            $"GP-0001 regenerated and all {_map.BakedPatches["GP-0001"].Count} baked node(s) hidden");
        var col = DynamicColour("GP-0001");
        Check(col is { } c1 && SameColour(c1, "#11aa99"), $"GP-0001 shows the new colour (vertex colour {col})");
        Check(_map.BakedPatches["GP-0003"].All(m => m.Visible) && !_map.DynamicPatches.ContainsKey("GP-0003"), "GP-0003 untouched");
        PatchesClean("recolour");

        // 2: reshape GP-0003 (far from the others)
        if (!await Apply("reshape")) return;
        rep = _watcher.LastPatchesReplaced;
        Check(rep.Contains("GP-0003") && !rep.Contains("GP-0001"), $"(ii) reshape replaces GP-0003 only: {string.Join(", ", rep)}");
        Check(_map.DynamicPatches.ContainsKey("GP-0003") && _map.DynamicPatches.ContainsKey("GP-0001"), "earlier regenerated GP-0001 is kept");
        PatchesClean("reshape");

        // 3: a new patch appears
        if (!await Apply("add")) return;
        Check(_map.DynamicPatches.ContainsKey("GP-0004") && _map.BakedPatches.ContainsKey("GP-0004") == false
              && Ids(_map.VisiblePatchIds()).Contains("GP-0004"), "(iii) added patch GP-0004 is visible and had no baked node");
        PatchesClean("add");

        // 4: the top patch GP-0002 is removed; its overlapping neighbour GP-0001 is rebuilt, GP-0002 shows nothing any more
        if (!await Apply("remove_top")) return;
        rep = _watcher.LastPatchesReplaced;
        Check(rep.Contains("GP-0002") && rep.Contains("GP-0001"), $"(iv) removing GP-0002 replaces it and its neighbour GP-0001: {string.Join(", ", rep)}");
        Check(!Ids(_map.VisiblePatchIds()).Contains("GP-0002") && !_map.DynamicPatches.ContainsKey("GP-0002"), "removed patch GP-0002 shows nothing (baked hidden, no regenerated node)");
        PatchesClean("remove");

        // 5: a patch and a parcel changed by one file write
        var applied = _watcher.AppliedCount;
        parcelRuns = _watcher.ParcelRunCount; regionRuns = _watcher.PatchRunCount;
        if (!await Apply("both")) return;
        Check(_watcher.AppliedCount == applied + 1 && _watcher.ParcelRunCount == parcelRuns + 1 && _watcher.PatchRunCount == regionRuns + 1,
            "(v) parcel and patch changed in one file: one run, one parcel-run, one patch-run, one apply");
        Check(_map.IsPropsSwapped(plot) && IsRegenColour("GP-0001", "#772299"), $"(v) both applied: parcel {plot} swapped and GP-0001 recoloured");
        PatchesClean("parcel + patch");
        await Physics(4);

        // 6: a half-written file changes nothing
        var parseFails = _watcher.ParseFailedCount;
        var before = Ids(_map.DynamicPatches.Keys);
        PutEdits(editsPath, Step("broken"));
        Check(await WaitUntil(() => _watcher.ParseFailedCount > parseFails, 10) && Ids(_map.DynamicPatches.Keys).SequenceEqual(before),
            "(vi) a broken file is reported and changes no patch");

        // 7: repeated swaps of the same patches do not grow the node count (leak check) and never double free
        var counts = new List<int>();
        foreach (var name in new[] { "toggle_a", "toggle_b", "toggle_a", "toggle_b", "toggle_a", "toggle_b" })
        {
            if (!await Apply(name)) return;
            await Physics(4);
            counts.Add(NodeMonitor());
            if (!PatchesClean(name)) return;
        }
        Check(counts[3] == counts[5] && counts[2] == counts[4], $"(vii) node count is stable over repeated swaps: {string.Join(", ", counts)}");

        // 8: all patches removed; the parcel edit goes too
        if (!await Apply("clear")) return;
        Check(_map.DynamicPatches.Count == 0 && Ids(_map.VisiblePatchIds()).Length == 0 && DynPatchContainers() == 0,
            "(viii) all patches removed: nothing visible, no regenerated node left");
        Check(_watcher.FailedCount == 0, "no failed run during the whole smoke");
        if (splits.Count > 0)
            Check(splits.All(id => !Ids(_map.VisiblePatchIds()).Contains(id)), $"patches split over chunk files ({string.Join(", ", splits)}) were swapped out completely");
        else
            GD.Print("info: no patch was split over chunk files in this map (border not covered by terrain)");
    }

    bool IsRegenColour(string id, string hex) => DynamicColour(id) is { } c && SameColour(c, hex);
}
