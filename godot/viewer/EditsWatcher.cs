using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Hot reload of hand edits. Polls the mtime of the edits file about twice a second, diffs the parsed
/// file per parcel and per group against the last applied state, regenerates the changed parcels with
/// a `uv run ... parcel-run` subprocess (never blocking the main thread) and swaps the result into the
/// map with MapLoader.ApplyFragment. Edits arriving during a run are coalesced into the next run.
/// Regions (hand-drawn colour shapes) are diffed the same way: an added, recoloured, reshaped or removed region
/// (plus the regions overlapping it) is regenerated with `region-run --glb` and swapped in with
/// MapLoader.ApplyRegions. Parcel and region work of one file change run one after the other in one run and are
/// applied together, or not at all when either subprocess fails.
/// A file that cannot be parsed (half-written) is ignored until it changes again; a failed run only
/// prints the error. Also writes editor-state.json (camera, selection) at most once a second.
/// The state at start is assumed to be what the map was built with, so nothing runs at startup.
/// A re-exported map (its buildings) is reloaded too, see EditsWatcher.Map.cs.
/// </summary>
public partial class EditsWatcher : Node
{
    public const double PollSeconds = 0.5;
    public const double StateSeconds = 1.0;
    /// <summary>A parcel-run or region-run that takes longer is killed (with its children) and reported as failed.</summary>
    public const int RunTimeoutSeconds = 120;

    public string EditsPath { get; init; } = "";
    public string PipelineDir { get; init; } = "";
    public string StatePath { get; init; } = "";
    public Drycastle.MapLoader.MapLoader Map { get; init; } = null!;
    /// <summary>Camera in Godot axes: position and the (unit) forward vector.</summary>
    public Func<(Vector3 pos, Vector3 forward)>? CameraProvider { get; init; }

    /// <summary>Selected parcel ids for editor-state.json (the editor mode); null writes an empty selection.</summary>
    public Func<IReadOnlyCollection<string>>? SelectionProvider { get; init; }

    /// <summary>Write editor-state.json on the next frame instead of waiting for the once-a-second tick.</summary>
    public void StateSoon() => _stateT = StateSeconds;

    /// <summary>Raised on the main thread when a parcel-run starts: the parcel ids.</summary>
    public event Action<IReadOnlyList<string>>? Launched;
    /// <summary>Raised on the main thread after a swap: parcel ids, edit-to-applied milliseconds.</summary>
    public event Action<IReadOnlyList<string>, double>? Applied;
    /// <summary>Raised on the main thread when a run or the swap failed.</summary>
    public event Action<string>? Failed;
    /// <summary>Raised when the edits file changed but could not be parsed.</summary>
    public event Action<string>? ParseFailed;

    /// <summary>Raised on the main thread after an instant objects-only update (no parcel-run): parcel ids, milliseconds the update took.</summary>
    public event Action<IReadOnlyList<string>, double>? InstantApplied;

    public int AppliedCount { get; private set; }
    /// <summary>Number of objects-only updates done in process (no subprocess).</summary>
    public int InstantCount { get; private set; }
    /// <summary>Number of runs started (each a parcel-run, a region-run or both). Stays flat while only placed objects change.</summary>
    public int RunCount => _runCounter;
    public double LastInstantMs { get; private set; }
    /// <summary>Number of parcel-run subprocesses started (RunCount counts runs, which may also be region-only).</summary>
    public int ParcelRunCount { get; private set; }
    /// <summary>Number of region-run subprocesses started.</summary>
    public int RegionRunCount => _regionRunCounter;
    /// <summary>Region ids replaced by the last applied run (empty when it had no region work).</summary>
    public IReadOnlyList<string> LastRegionsReplaced { get; private set; } = Array.Empty<string>();
    public int FailedCount { get; private set; }
    public int ParseFailedCount { get; private set; }
    public bool Busy => _run != null;
    public double LastLatencyMs { get; private set; }

    sealed record Snapshot(
        string Text, Dictionary<string, string> Parcels, Dictionary<string, string> Groups,
        Dictionary<string, List<string>> Members, Dictionary<string, string> Objects,
        Dictionary<string, string> Regions, Dictionary<string, (double X0, double Y0, double X1, double Y1)> RegionBoxes,
        string CablesText, List<Drycastle.MapLoader.CableSpec> Cables);

    sealed class Run
    {
        public Task<(int code, string err, string tool)> Task = null!;
        public Snapshot Snap = null!;
        public List<string> Ids = new();
        public List<string> RegionIds = new();
        public string Fragment = "", Glb = "", Edits = "", RegionSummary = "", RegionGlb = "";
        public long StartedMs, DetectedMs;
        public Process? Process;
    }

    Snapshot _applied = new("", new(), new(), new(), new(), new(), new(), "", new());
    (DateTime mtime, long len) _seen, _parsedStat;
    double _pollT, _stateT;
    bool _dirty;
    long _dirtySinceMs;
    Run? _run;
    int _runCounter, _regionRunCounter;
    string _tmpDir = "";
    string _lastState = "";

    public override void _Ready()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"drycastle-editor-{System.Environment.ProcessId}");
        Directory.CreateDirectory(_tmpDir);
        _seen = Stat();
        // baseline: the map is assumed to match the file as it is now
        if (TryParse(out var snap, out var err))
        {
            _applied = snap!;
            // cables are drawn by the game from the poles that stand: show the file's, even when the map was exported before them
            if (snap!.CablesText != CablesText(Map.Cables)) Map.SetCables(snap.Cables);
        }
        else GD.PushWarning($"edits watcher: initial {EditsPath} unreadable ({err}); starting from empty edits");
        GD.Print($"watching {EditsPath}");
        StartMapWatch();
    }

    public override void _ExitTree()
    {
        try { _run?.Process?.Kill(true); } catch { /* already gone */ }
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
    }

    (DateTime, long) Stat()
    {
        try
        {
            var fi = new FileInfo(EditsPath);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException) { return (DateTime.MinValue, -1); }
    }

    public override void _Process(double delta)
    {
        _pollT += delta;
        if (_pollT >= PollSeconds)
        {
            _pollT = 0;
            var st = Stat();
            if (st != _seen)
            {
                _seen = st;
                _dirty = true;
                _dirtySinceMs = (long)Time.GetTicksMsec();
            }
            PollMap();
        }
        StepMapReload();
        if (_run != null && _run.Task.IsCompleted) Finish(_run);
        if (_run == null && _dirty) StartIfChanged();
        _stateT += delta;
        if (_stateT >= StateSeconds && StatePath != "")
        {
            _stateT = 0;
            WriteState();
        }
    }

    // ---- parsing and diffing ----

    static string Canon(JsonElement e)
    {
        var sb = new StringBuilder();
        CanonTo(e, sb);
        return sb.ToString();
    }

    static void CanonTo(JsonElement e, StringBuilder sb, string? skipKey = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (var p in e.EnumerateObject().Where(p => p.Name != skipKey).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    CanonTo(p.Value, sb);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                foreach (var x in e.EnumerateArray()) { CanonTo(x, sb); sb.Append(','); }
                sb.Append(']');
                break;
            default:
                sb.Append(e.GetRawText());
                break;
        }
    }

    bool TryParse(out Snapshot? snap, out string error)
    {
        snap = null;
        string text;
        _parsedStat = Stat(); // before the read: a write after it changes the stat and is picked up by the poll
        try { text = File.ReadAllText(EditsPath); }
        catch (Exception e) { error = e.Message; return false; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "top level is not an object"; return false; }
            var parcels = new Dictionary<string, string>();
            var groups = new Dictionary<string, string>();
            var members = new Dictionary<string, List<string>>();
            var objects = new Dictionary<string, string>(); // object id -> its canonical text: an object belongs to no parcel
            foreach (var o in EnumerateObjects(doc.RootElement))
                if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("id", out var oid) && oid.ValueKind == JsonValueKind.String)
                    objects[oid.GetString()!] = Canon(o);
            if (doc.RootElement.TryGetProperty("parcels", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var kv in p.EnumerateObject())
                {
                    // without a (legacy) "objects" list, so a parcel entry changes only when its fence does
                    var sbn = new StringBuilder();
                    CanonTo(kv.Value, sbn, "objects");
                    parcels[kv.Name] = sbn.ToString();
                }
            if (doc.RootElement.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Object)
                foreach (var kv in g.EnumerateObject())
                {
                    groups[kv.Name] = Canon(kv.Value);
                    members[kv.Name] = kv.Value.ValueKind == JsonValueKind.Object
                        && kv.Value.TryGetProperty("parcels", out var m) && m.ValueKind == JsonValueKind.Array
                        ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                        : new();
                }
            var regions = new Dictionary<string, string>();
            var boxes = new Dictionary<string, (double, double, double, double)>();
            if (doc.RootElement.TryGetProperty("regions", out var rg) && rg.ValueKind == JsonValueKind.Object)
                foreach (var kv in rg.EnumerateObject())
                {
                    regions[kv.Name] = Canon(kv.Value);
                    if (RegionBox(kv.Value) is { } box) boxes[kv.Name] = box;
                }
            var cables = new List<Drycastle.MapLoader.CableSpec>();
            if (doc.RootElement.TryGetProperty("cables", out var cb) && cb.ValueKind == JsonValueKind.Object)
                foreach (var kv in cb.EnumerateObject().OrderBy(kv => kv.Name, StringComparer.Ordinal))
                    if (kv.Value.ValueKind == JsonValueKind.Object
                        && kv.Value.TryGetProperty("from", out var cf) && cf.ValueKind == JsonValueKind.String
                        && kv.Value.TryGetProperty("to", out var ct) && ct.ValueKind == JsonValueKind.String)
                        cables.Add(new Drycastle.MapLoader.CableSpec(kv.Name, cf.GetString()!, ct.GetString()!));
            snap = new Snapshot(text, parcels, groups, members, objects, regions, boxes, CablesText(cables), cables);
            error = "";
            return true;
        }
        catch (JsonException e) { error = e.Message; return false; }
    }

    /// <summary>
    /// Every placed object of an edits document: the top-level <c>objects</c> list, then (a file in the old shape) the
    /// lists under <c>parcels.&lt;ID&gt;.objects</c>. An object belongs to no parcel (edits.py).
    /// </summary>
    static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.TryGetProperty("objects", out var top) && top.ValueKind == JsonValueKind.Array)
            foreach (var o in top.EnumerateArray()) yield return o;
        if (root.TryGetProperty("parcels", out var parcels) && parcels.ValueKind == JsonValueKind.Object)
            foreach (var entry in parcels.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("objects", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
                    foreach (var o in legacy.EnumerateArray()) yield return o;
    }

    static string CablesText(IEnumerable<Drycastle.MapLoader.CableSpec> cables)
        => string.Join(";", cables.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => $"{c.Id}:{c.From}>{c.To}"));

    /// <summary>
    /// Cables need no pipeline run: the game strings them between the poles as they stand. Called with every snapshot
    /// that becomes the applied state; the poles' own moves are followed by MapLoader when it re-instances objects.
    /// </summary>
    void ApplyCables(Snapshot snap)
    {
        if (snap.CablesText == _applied.CablesText) return;
        Map.SetCables(snap.Cables);
        CablesAppliedCount++;
    }

    /// <summary>Number of times the cable list changed and was drawn again.</summary>
    public int CablesAppliedCount { get; private set; }

    static (double, double, double, double)? RegionBox(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("polygon", out var poly)
            || poly.ValueKind != JsonValueKind.Array) return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var pt in poly.EnumerateArray())
        {
            if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2
                || pt[0].ValueKind != JsonValueKind.Number || pt[1].ValueKind != JsonValueKind.Number) return null;
            x0 = Math.Min(x0, pt[0].GetDouble()); x1 = Math.Max(x1, pt[0].GetDouble());
            y0 = Math.Min(y0, pt[1].GetDouble()); y1 = Math.Max(y1, pt[1].GetDouble());
        }
        return x0 <= x1 ? (x0, y0, x1, y1) : null;
    }

    /// <summary>
    /// Regions affected by the change from old to new: added, removed or changed (colour or polygon) entries, plus every
    /// other region whose bounding box touches the old or new shape of one of them (its visible area changes with it;
    /// the higher ID wins where regions overlap). Bounding boxes only: a few extra regions are rebuilt, none are missed.
    /// </summary>
    static SortedSet<string> RegionDiff(Snapshot old, Snapshot now)
    {
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rid in old.Regions.Keys.Union(now.Regions.Keys))
        {
            old.Regions.TryGetValue(rid, out var a);
            now.Regions.TryGetValue(rid, out var b);
            if (a != b) changed.Add(rid);
        }
        if (changed.Count == 0) return changed;
        var shapes = new List<(double X0, double Y0, double X1, double Y1)>();
        foreach (var rid in changed)
        {
            if (old.RegionBoxes.TryGetValue(rid, out var ob)) shapes.Add(ob);
            if (now.RegionBoxes.TryGetValue(rid, out var nb)) shapes.Add(nb);
        }
        var result = new SortedSet<string>(changed, StringComparer.Ordinal);
        foreach (var (rid, box) in now.RegionBoxes)
            if (!changed.Contains(rid)
                && shapes.Any(s => s.X0 <= box.X1 && box.X0 <= s.X1 && s.Y0 <= box.Y1 && box.Y0 <= s.Y1))
                result.Add(rid);
        return result;
    }

    /// <summary>
    /// Parcels affected by the change from old to new: changed, appeared or disappeared parcel entries,
    /// every member (old and new) of a changed group, and every member of a group that has a changed parcel.
    /// </summary>
    static SortedSet<string> Diff(Snapshot old, Snapshot now)
    {
        var parcels = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pid in old.Parcels.Keys.Union(now.Parcels.Keys))
        {
            old.Parcels.TryGetValue(pid, out var a);
            now.Parcels.TryGetValue(pid, out var b);
            if (a != b) parcels.Add(pid);
        }
        var changedGroups = new HashSet<string>();
        foreach (var gid in old.Groups.Keys.Union(now.Groups.Keys))
        {
            old.Groups.TryGetValue(gid, out var a);
            now.Groups.TryGetValue(gid, out var b);
            if (a != b) changedGroups.Add(gid);
        }
        var affected = new SortedSet<string>(parcels, StringComparer.Ordinal);
        foreach (var gid in changedGroups)
        {
            if (old.Members.TryGetValue(gid, out var om)) affected.UnionWith(om);
            if (now.Members.TryGetValue(gid, out var nm)) affected.UnionWith(nm);
        }
        // a changed parcel pulls in the rest of its group (the fence is on the combined perimeter)
        foreach (var members in old.Members.Values.Concat(now.Members.Values))
            if (members.Any(parcels.Contains)) affected.UnionWith(members);
        return affected;
    }

    // ---- instant path: only placed objects changed ----

    /// <summary>
    /// Whether nothing but placed objects (and cables) changed: no group, region or known parcel entry differs. Objects
    /// are in none of those, so an object edit leaves them all equal.
    /// </summary>
    static bool ObjectsOnly(Snapshot old, Snapshot now, Func<string, bool> known)
    {
        if (RegionDiff(old, now).Count > 0) return false; // regions need a region-run
        if (old.Groups.Count != now.Groups.Count) return false;
        foreach (var (gid, text) in now.Groups)
            if (!old.Groups.TryGetValue(gid, out var o) || o != text) return false;
        foreach (var pid in old.Parcels.Keys.Union(now.Parcels.Keys))
        {
            if (!known(pid)) continue; // entries of parcels that are not in the map are skipped anyway
            old.Parcels.TryGetValue(pid, out var a);
            now.Parcels.TryGetValue(pid, out var b);
            if (a != b) return false;
        }
        return true;
    }

    /// <summary>The ids of the objects that are gone, and those that are new or differ, between two snapshots.</summary>
    static (List<string> removed, List<string> changed) ObjectDiff(Snapshot old, Snapshot now)
    {
        var removed = old.Objects.Keys.Where(id => !now.Objects.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var changed = now.Objects.Where(kv => !old.Objects.TryGetValue(kv.Key, out var t) || t != kv.Value).Select(kv => kv.Key)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        return (removed, changed);
    }

    /// <summary>
    /// Call right after writing the edits file: when only placed objects changed, update them now (no subprocess)
    /// and remember the file as applied, so the poll does not redo it. False when nothing was done (something else
    /// changed, a run is in flight, or the file is unreadable); the poll then handles the change as usual.
    /// </summary>
    public bool NotifySaved()
    {
        if (_run != null) return false;
        if (!TryParse(out var snap, out _)) return false;
        return TryInstant(snap!);
    }

    bool TryInstant(Snapshot snap)
    {
        if (_run != null || Map.OriginEpsg5514 is not { } origin) return false;
        if (!ObjectsOnly(_applied, snap, Map.Parcels.ContainsKey)) return false;
        var t0 = Stopwatch.StartNew();
        var (removed, changed) = ObjectDiff(_applied, snap);
        if (removed.Count == 0 && changed.Count == 0) { _seen = _parsedStat; _dirty = false; ApplyCables(snap); _applied = snap; return true; }
        List<Drycastle.MapLoader.PlacedObject> objects;
        // on failure _applied stays as it was and the caller falls through to the parcel-run path
        try { objects = ReadObjects(snap.Text, changed, origin); }
        catch (Exception e) { Fail($"reading the placed objects failed: {e.Message}"); return false; }
        _seen = _parsedStat; // the stat taken before the file was read, so a write in between is seen by the poll
        _dirty = false;
        Map.ApplyObjectChanges(removed, objects);
        ApplyCables(snap);
        _applied = snap;
        InstantCount++;
        LastInstantMs = t0.Elapsed.TotalMilliseconds;
        var touched = removed.Concat(changed).Distinct().ToList();
        GD.Print($"objects updated: {string.Join(", ", touched)} ({objects.Count} placed, {removed.Count} removed, no parcel-run) in {LastInstantMs:F1} ms");
        InstantApplied?.Invoke(touched, LastInstantMs);
        return true;
    }

    /// <summary>The objects with the given ids as map-local PlacedObjects (S-JTSK minus the map origin).</summary>
    List<Drycastle.MapLoader.PlacedObject> ReadObjects(string text, IReadOnlyCollection<string> ids, (double X, double Y) origin)
    {
        var list = new List<Drycastle.MapLoader.PlacedObject>();
        var want = new HashSet<string>(ids);
        using var doc = JsonDocument.Parse(text);
        foreach (var o in EnumerateObjects(doc.RootElement))
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("id", out var idp) || idp.GetString() is not { } id || !want.Contains(id)) continue;
            var pos = o.GetProperty("position");
            var x = (float)Math.Round(pos[0].GetDouble() - origin.X, 3);
            var y = (float)Math.Round(pos[1].GetDouble() - origin.Y, 3);
            list.Add(new Drycastle.MapLoader.PlacedObject
            {
                Id = id,
                Asset = o.GetProperty("asset").GetString() ?? "",
                X = x,
                Y = y,
                Z = o.TryGetProperty("z", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetSingle() : null,
                RotationDeg = o.TryGetProperty("rotation_deg", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : 0f,
                Scale = o.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetSingle() : 1f,
                Colour = o.TryGetProperty("colour", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                Parcel = Map.ParcelAt(x, -y)?.Id,
            });
        }
        return list;
    }

    // ---- running parcel-run ----

    void StartIfChanged()
    {
        _dirty = false;
        if (!TryParse(out var snap, out var err))
        {
            ParseFailedCount++;
            GD.PushWarning($"edits file not readable yet ({err}); waiting for the next change");
            GD.Print($"edits not applied: {EditsPath} is not valid JSON yet ({err})");
            ParseFailed?.Invoke(err);
            return;
        }
        if (TryInstant(snap!)) return;
        var affected = Diff(_applied, snap!);
        var known = affected.Where(Map.Parcels.ContainsKey).ToList();
        foreach (var unknown in affected.Except(known))
            GD.Print($"warning: edits mention parcel {unknown}, which is not in the map; skipped");
        var regionIds = RegionDiff(_applied, snap!).ToList();
        if (known.Count == 0 && regionIds.Count == 0)
        {
            ApplyCables(snap!);
            _applied = snap!;
            return;
        }
        ApplyObjectDiff(snap!); // the objects of this save show at once; the run is only for what it makes (fences, regions)
        Launch(snap!, known, regionIds);
    }

    /// <summary>Instance the objects that differ between the applied state and <paramref name="snap"/> (no subprocess), when this save also needs a run.</summary>
    void ApplyObjectDiff(Snapshot snap)
    {
        if (Map.OriginEpsg5514 is not { } origin) return;
        var (removed, changed) = ObjectDiff(_applied, snap);
        if (removed.Count == 0 && changed.Count == 0) return;
        try { Map.ApplyObjectChanges(removed, ReadObjects(snap.Text, changed, origin)); }
        catch (Exception e) { GD.PushWarning($"placed objects not applied before the run: {e.Message}"); }
    }

    ProcessStartInfo UvStart(params string[] args)
    {
        var uv = System.Environment.GetEnvironmentVariable("DRYCASTLE_UV");
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(uv) ? "uv" : uv)
        {
            WorkingDirectory = PipelineDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "run", "python", "-m", "drycastle_pipeline" }.Concat(args))
            psi.ArgumentList.Add(a);
        return psi;
    }

    static (int code, string err) RunProcess(Run run, ProcessStartInfo psi)
    {
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start uv");
            run.Process = p;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(RunTimeoutSeconds * 1000))
            {
                try { p.Kill(true); } catch { /* already gone */ }
                p.WaitForExit(2000);
                return (-1, $"timed out after {RunTimeoutSeconds} s and was killed");
            }
            p.WaitForExit(); // flush the redirected streams
            stdout.Wait();
            return (p.ExitCode, stderr.Result);
        }
        catch (Exception e)
        {
            return (-1, $"could not run uv ({e.Message}); is uv installed and on PATH? (DRYCASTLE_UV overrides)");
        }
    }

    void Launch(Snapshot snap, List<string> ids, List<string> regionIds)
    {
        var n = ++_runCounter;
        var run = new Run
        {
            Snap = snap,
            Ids = ids,
            Edits = Path.Combine(_tmpDir, $"edits-{n}.json"),
            Fragment = Path.Combine(_tmpDir, $"fragment-{n}.json"),
            Glb = Path.Combine(_tmpDir, $"props-{n}.glb"),
            StartedMs = (long)Time.GetTicksMsec(),
            DetectedMs = _dirtySinceMs,
        };
        // the subprocess reads exactly the snapshot we diffed, not whatever the file is by then
        File.WriteAllText(run.Edits, snap.Text);
        run.RegionIds = regionIds;
        run.RegionSummary = Path.Combine(_tmpDir, $"regions-{n}.json");
        run.RegionGlb = Path.Combine(_tmpDir, $"regions-{n}.glb");
        ProcessStartInfo? parcelPsi = null, regionPsi = null;
        if (ids.Count > 0)
        {
            ParcelRunCount++;
            var a = new List<string> { "parcel-run" };
            foreach (var id in ids) { a.Add("--parcel"); a.Add(id); }
            a.AddRange(new[] { "--edits", run.Edits, "--glb", run.Glb, "--out", run.Fragment });
            parcelPsi = UvStart(a.ToArray());
        }
        if (regionIds.Count > 0)
        {
            _regionRunCounter++;
            var a = new List<string> { "region-run" };
            foreach (var id in regionIds) { a.Add("--region"); a.Add(id); }
            a.AddRange(new[] { "--edits", run.Edits, "--glb", run.RegionGlb, "--out", run.RegionSummary });
            regionPsi = UvStart(a.ToArray());
        }
        run.Task = Task.Run(() =>
        {
            var log = new StringBuilder();
            foreach (var (psi, tool) in new[] { (parcelPsi, "parcel-run"), (regionPsi, "region-run") })
            {
                if (psi == null) continue;
                var (code, err) = RunProcess(run, psi);
                log.Append(err);
                if (code != 0) return (code, log.ToString(), tool);
            }
            return (0, log.ToString(), "");
        });
        _run = run;
        GD.Print($"edits changed: regenerating {string.Join(", ", ids.Concat(regionIds))}");
        Launched?.Invoke(ids.Concat(regionIds).ToList());
    }

    void Finish(Run run)
    {
        _run = null;
        var (code, err, failedTool) = run.Task.Result;
        try
        {
            if (code != 0)
            {
                Fail($"{failedTool} failed for {string.Join(", ", run.Ids.Concat(run.RegionIds))} (exit {code}): {err.Trim()}");
                return;
            }
            Drycastle.MapLoader.FragmentResult result = new(Array.Empty<string>(), 0, 0);
            Drycastle.MapLoader.RegionResult? regions = null;
            try
            {
                // applied together or not at all: parse the region result first (it changes nothing),
                // so neither apply below can fail after the other has already swapped nodes
                if (run.RegionIds.Count > 0) Map.ValidateRegions(run.RegionSummary);
                if (run.Ids.Count > 0) result = Map.ApplyFragment(run.Fragment);
                if (run.RegionIds.Count > 0) regions = Map.ApplyRegions(run.RegionSummary);
            }
            catch (Exception e)
            {
                Fail($"applying the result for {string.Join(", ", run.Ids.Concat(run.RegionIds))} failed: {e.Message}");
                return;
            }
            LastRegionsReplaced = regions?.Replaced ?? Array.Empty<string>();
            ApplyCables(run.Snap);
            _applied = run.Snap;
            var ms = Time.GetTicksMsec() - (ulong)run.DetectedMs;
            LastLatencyMs = ms;
            AppliedCount++;
            GD.Print($"edits applied: {string.Join(", ", result.Replaced)} in {ms} ms " +
                     $"(objects={result.Objects} props_nodes={result.PropsNodes}, parcel-run {Time.GetTicksMsec() - (ulong)run.StartedMs} ms)");
            if (regions != null)
                GD.Print($"regions applied: {string.Join(", ", regions.Replaced)} (region_nodes={regions.Nodes})");
            foreach (var line in err.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                GD.Print(line.Trim());
            Applied?.Invoke(result.Replaced.Concat(regions?.Replaced ?? Array.Empty<string>()).ToList(), ms);
        }
        finally
        {
            foreach (var f in new[] { run.Edits, run.Fragment, run.Glb, run.RegionSummary, run.RegionGlb })
                try { File.Delete(f); } catch { /* best effort */ }
        }
        // edits that arrived while the run was in flight set _dirty in the poll and start the next run
    }

    void Fail(string message)
    {
        FailedCount++;
        GD.PushError(message);
        GD.Print($"edits error: {message}");
        Failed?.Invoke(message);
    }

    // ---- editor-state.json ----

    void WriteState()
    {
        if (CameraProvider == null) return;
        try
        {
            var (pos, fwd) = CameraProvider();
            var selected = string.Join(",", (SelectionProvider?.Invoke() ?? Array.Empty<string>())
                .OrderBy(x => x, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x)));
            var yaw = Mathf.RadToDeg(Mathf.Atan2(-fwd.Z, fwd.X)); // counter-clockwise from east, like rotation_deg
            var json = "{\"camera\":{" +
                       $"\"x\":{F(pos.X)},\"y\":{F(-pos.Z)},\"yaw_deg\":{F(yaw)},\"z\":{F(pos.Y)}" +
                       "},\"format_version\":1,\"selected\":[" + selected + "]}\n";
            if (json == _lastState) return;
            _lastState = json;
            var dir = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StatePath, true);
        }
        catch (Exception e)
        {
            GD.PushWarning($"could not write {StatePath}: {e.Message}");
        }
    }

    static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
