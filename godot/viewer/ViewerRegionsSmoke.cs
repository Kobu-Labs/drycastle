using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--regions-smoke --regions-smoke-plan plan.json` (run by regions_smoke.py, `make viewer-regions-smoke`): drives the
/// region hot reload end to end in a headless viewer. The map was exported with three regions; the steps recolour,
/// reshape, add and remove regions, change a region and a parcel in one file change, survive a broken file and
/// toggle repeatedly to prove that nothing leaks. Reuses the helpers of ViewerEditSmoke.cs.
/// </summary>
public partial class Viewer
{
    static int NodeMonitor() => (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);

    static string[] Ids(IEnumerable<string> ids) => ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // First vertex colour of the regenerated region node (raw values; compared as sRGB bytes or as linear).
    Color? DynamicColour(string id)
    {
        if (!_map.DynamicRegions.TryGetValue(id, out var c)) return null;
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

    int DynRegionContainers() => _map.Root.GetChildren().Count(c => c.Name.ToString().StartsWith("dyn_region_") && !c.IsQueuedForDeletion());

    async Task RunRegionsSmoke()
    {
        try { await RegionsSmokeBody(); }
        catch (Exception e)
        {
            GD.Print($"FAIL regions smoke threw: {e}");
            _editOk = false;
        }
        GD.Print(_editOk ? "REGIONS SMOKE PASS" : "REGIONS SMOKE FAIL");
        if (_latencies.Count > 0)
            GD.Print("edit_to_applied_ms: " + string.Join(", ", _latencies.Select(l => $"{l.step}={l.ms:F0}"))
                     + $" (mean {_latencies.Average(l => l.ms):F0})");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    async Task RegionsSmokeBody()
    {
        _player.InputEnabled = false;
        await Physics(3);
        using var planDoc = JsonDocument.Parse(File.ReadAllText(_opts["regions-smoke-plan"]));
        var plan = planDoc.RootElement;
        var editsPath = _watcher!.EditsPath;
        var steps = plan.GetProperty("steps");
        string Step(string n) => steps.GetProperty(n).GetString()!;
        var colours = plan.GetProperty("colours");
        var plot = plan.GetProperty("parcel").GetString()!;

        // baseline: baked region nodes, nothing regenerated
        var baked = Ids(_map.BakedRegions.Keys);
        var wanted = plan.GetProperty("baseline_regions").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Check(wanted.All(baked.Contains), $"baseline: baked region nodes for {string.Join(", ", wanted)} (found {string.Join(", ", baked)})");
        Check(_map.DynamicRegions.Count == 0 && Ids(_map.VisibleRegionIds()).SequenceEqual(baked), "baseline: all baked regions visible, none regenerated");
        foreach (var (id, nodes) in _map.BakedRegions)
            GD.Print($"info: {id} has {nodes.Count} baked node(s) in the chunk files" + (nodes.Count > 1 ? " (split over chunks)" : ""));
        Check(_map.BakedRegions.Values.All(l => l.All(m => m.GetChildCount() == 0)), "region nodes have no collider");
        var splits = _map.BakedRegions.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();

        async Task<bool> Apply(string name)
        {
            var ok = await ApplyStep(name, editsPath, Step(name));
            await Physics(4);
            return ok;
        }
        bool RegionsClean(string label)
        {
            // every dynamic container is a live child of the map; a swapped region has no visible baked node
            var live = _map.DynamicRegions.All(kv => GodotObject.IsInstanceValid(kv.Value) && kv.Value.IsInsideTree() && !kv.Value.IsQueuedForDeletion());
            var onlyOne = DynRegionContainers() == _map.DynamicRegions.Count;
            var noDouble = _map.DynamicRegions.Keys.All(id => !_map.BakedRegions.TryGetValue(id, out var l) || l.All(m => !m.Visible));
            Check(live && onlyOne && noDouble, $"{label}: {_map.DynamicRegions.Count} regenerated region node(s), no stale or double-visible ones");
            return live && onlyOne && noDouble;
        }

        // 1: recolour R-0001 (the region over the chunk border; R-0002 overlaps it)
        int regionRuns = _watcher.RegionRunCount, parcelRuns = _watcher.ParcelRunCount;
        if (!await Apply("recolour")) return;
        var rep = _watcher.LastRegionsReplaced;
        Check(rep.Contains("R-0001") && !rep.Contains("R-0003"), $"(i) recolour replaces R-0001 and not R-0003: {string.Join(", ", rep)}");
        Check(_watcher.RegionRunCount == regionRuns + 1 && _watcher.ParcelRunCount == parcelRuns, "a region-only edit runs region-run once and no parcel-run");
        Check(_map.DynamicRegions.ContainsKey("R-0001") && _map.BakedRegions["R-0001"].All(m => !m.Visible),
            $"R-0001 regenerated and all {_map.BakedRegions["R-0001"].Count} baked node(s) hidden");
        var col = DynamicColour("R-0001");
        Check(col is { } c1 && SameColour(c1, "#11aa99"), $"R-0001 shows the new colour (vertex colour {col})");
        Check(_map.BakedRegions["R-0003"].All(m => m.Visible) && !_map.DynamicRegions.ContainsKey("R-0003"), "R-0003 untouched");
        RegionsClean("recolour");

        // 2: reshape R-0003 (far from the others)
        if (!await Apply("reshape")) return;
        rep = _watcher.LastRegionsReplaced;
        Check(rep.Contains("R-0003") && !rep.Contains("R-0001"), $"(ii) reshape replaces R-0003 only: {string.Join(", ", rep)}");
        Check(_map.DynamicRegions.ContainsKey("R-0003") && _map.DynamicRegions.ContainsKey("R-0001"), "earlier regenerated R-0001 is kept");
        RegionsClean("reshape");

        // 3: a new region appears
        if (!await Apply("add")) return;
        Check(_map.DynamicRegions.ContainsKey("R-0004") && _map.BakedRegions.ContainsKey("R-0004") == false
              && Ids(_map.VisibleRegionIds()).Contains("R-0004"), "(iii) added region R-0004 is visible and had no baked node");
        RegionsClean("add");

        // 4: the top region R-0002 is removed; its overlapping neighbour R-0001 is rebuilt, R-0002 shows nothing any more
        if (!await Apply("remove_top")) return;
        rep = _watcher.LastRegionsReplaced;
        Check(rep.Contains("R-0002") && rep.Contains("R-0001"), $"(iv) removing R-0002 replaces it and its neighbour R-0001: {string.Join(", ", rep)}");
        Check(!Ids(_map.VisibleRegionIds()).Contains("R-0002") && !_map.DynamicRegions.ContainsKey("R-0002"), "removed region R-0002 shows nothing (baked hidden, no regenerated node)");
        RegionsClean("remove");

        // 5: a region and a parcel changed by one file write
        var applied = _watcher.AppliedCount;
        parcelRuns = _watcher.ParcelRunCount; regionRuns = _watcher.RegionRunCount;
        if (!await Apply("both")) return;
        Check(_watcher.AppliedCount == applied + 1 && _watcher.ParcelRunCount == parcelRuns + 1 && _watcher.RegionRunCount == regionRuns + 1,
            "(v) parcel and region changed in one file: one run, one parcel-run, one region-run, one apply");
        Check(_map.IsPropsSwapped(plot) && IsRegenColour("R-0001", "#772299"), $"(v) both applied: parcel {plot} swapped and R-0001 recoloured");
        RegionsClean("parcel + region");
        await Physics(4);

        // 6: a half-written file changes nothing
        var parseFails = _watcher.ParseFailedCount;
        var before = Ids(_map.DynamicRegions.Keys);
        PutEdits(editsPath, Step("broken"));
        Check(await WaitUntil(() => _watcher.ParseFailedCount > parseFails, 10) && Ids(_map.DynamicRegions.Keys).SequenceEqual(before),
            "(vi) a broken file is reported and changes no region");

        // 7: repeated swaps of the same regions do not grow the node count (leak check) and never double free
        var counts = new List<int>();
        foreach (var name in new[] { "toggle_a", "toggle_b", "toggle_a", "toggle_b", "toggle_a", "toggle_b" })
        {
            if (!await Apply(name)) return;
            await Physics(4);
            counts.Add(NodeMonitor());
            if (!RegionsClean(name)) return;
        }
        Check(counts[3] == counts[5] && counts[2] == counts[4], $"(vii) node count is stable over repeated swaps: {string.Join(", ", counts)}");

        // 8: all regions removed; the parcel edit goes too
        if (!await Apply("clear")) return;
        Check(_map.DynamicRegions.Count == 0 && Ids(_map.VisibleRegionIds()).Length == 0 && DynRegionContainers() == 0,
            "(viii) all regions removed: nothing visible, no regenerated node left");
        Check(_watcher.FailedCount == 0, "no failed run during the whole smoke");
        if (splits.Count > 0)
            Check(splits.All(id => !Ids(_map.VisibleRegionIds()).Contains(id)), $"regions split over chunk files ({string.Join(", ", splits)}) were swapped out completely");
        else
            GD.Print("info: no region was split over chunk files in this map (border not covered by terrain)");
    }

    bool IsRegenColour(string id, string hex) => DynamicColour(id) is { } c && SameColour(c, hex);
}
