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
/// A file that cannot be parsed (half-written) is ignored until it changes again; a failed run only
/// prints the error. Also writes editor-state.json (camera, selection) at most once a second.
/// The state at start is assumed to be what the map was built with, so nothing runs at startup.
/// </summary>
public partial class EditsWatcher : Node
{
    public const double PollSeconds = 0.5;
    public const double StateSeconds = 1.0;

    public string EditsPath { get; init; } = "";
    public string Area { get; init; } = "slice";
    public string PipelineDir { get; init; } = "";
    public string StatePath { get; init; } = "";
    public Suchohrad.MapLoader.MapLoader Map { get; init; } = null!;
    /// <summary>Camera in Godot axes: position and the (unit) forward vector.</summary>
    public Func<(Vector3 pos, Vector3 forward)>? CameraProvider { get; init; }

    /// <summary>Selected parcel ids for editor-state.json (the editor mode); null writes an empty selection.</summary>
    public Func<IReadOnlyCollection<string>>? SelectionProvider { get; init; }

    /// <summary>Raised on the main thread when a parcel-run starts: the parcel ids.</summary>
    public event Action<IReadOnlyList<string>>? Launched;
    /// <summary>Raised on the main thread after a swap: parcel ids, edit-to-applied milliseconds.</summary>
    public event Action<IReadOnlyList<string>, double>? Applied;
    /// <summary>Raised on the main thread when a run or the swap failed.</summary>
    public event Action<string>? Failed;
    /// <summary>Raised when the edits file changed but could not be parsed.</summary>
    public event Action<string>? ParseFailed;

    public int AppliedCount { get; private set; }
    public int FailedCount { get; private set; }
    public int ParseFailedCount { get; private set; }
    public bool Busy => _run != null;
    public double LastLatencyMs { get; private set; }

    sealed record Snapshot(
        string Text, Dictionary<string, string> Parcels, Dictionary<string, string> Groups,
        Dictionary<string, List<string>> Members);

    sealed class Run
    {
        public Task<(int code, string err)> Task = null!;
        public Snapshot Snap = null!;
        public List<string> Ids = new();
        public string Fragment = "", Glb = "", Edits = "";
        public long StartedMs, DetectedMs;
        public Process? Process;
    }

    Snapshot _applied = new("", new(), new(), new());
    (DateTime mtime, long len) _seen;
    double _pollT, _stateT;
    bool _dirty;
    long _dirtySinceMs;
    Run? _run;
    int _runCounter;
    string _tmpDir = "";
    string _lastState = "";

    public override void _Ready()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"suchohrad-editor-{System.Environment.ProcessId}");
        Directory.CreateDirectory(_tmpDir);
        _seen = Stat();
        // baseline: the map is assumed to match the file as it is now
        if (TryParse(out var snap, out var err)) _applied = snap!;
        else GD.PushWarning($"edits watcher: initial {EditsPath} unreadable ({err}); starting from empty edits");
        GD.Print($"watching {EditsPath} (area {Area})");
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
        }
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

    static void CanonTo(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
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
        try { text = File.ReadAllText(EditsPath); }
        catch (Exception e) { error = e.Message; return false; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "top level is not an object"; return false; }
            var parcels = new Dictionary<string, string>();
            var groups = new Dictionary<string, string>();
            var members = new Dictionary<string, List<string>>();
            if (doc.RootElement.TryGetProperty("parcels", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var kv in p.EnumerateObject()) parcels[kv.Name] = Canon(kv.Value);
            if (doc.RootElement.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Object)
                foreach (var kv in g.EnumerateObject())
                {
                    groups[kv.Name] = Canon(kv.Value);
                    members[kv.Name] = kv.Value.ValueKind == JsonValueKind.Object
                        && kv.Value.TryGetProperty("parcels", out var m) && m.ValueKind == JsonValueKind.Array
                        ? m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                        : new();
                }
            snap = new Snapshot(text, parcels, groups, members);
            error = "";
            return true;
        }
        catch (JsonException e) { error = e.Message; return false; }
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
        var affected = Diff(_applied, snap!);
        var known = affected.Where(Map.Parcels.ContainsKey).ToList();
        foreach (var unknown in affected.Except(known))
            GD.Print($"warning: edits mention parcel {unknown}, which is not in the map; skipped");
        if (known.Count == 0)
        {
            _applied = snap!;
            return;
        }
        Launch(snap!, known);
    }

    void Launch(Snapshot snap, List<string> ids)
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
        var uv = System.Environment.GetEnvironmentVariable("SUCHOHRAD_UV");
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(uv) ? "uv" : uv)
        {
            WorkingDirectory = PipelineDir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "run", "python", "-m", "suchohrad_pipeline", "parcel-run", "--area", Area })
            psi.ArgumentList.Add(a);
        foreach (var id in ids) { psi.ArgumentList.Add("--parcel"); psi.ArgumentList.Add(id); }
        foreach (var a in new[] { "--edits", run.Edits, "--glb", run.Glb, "--out", run.Fragment })
            psi.ArgumentList.Add(a);
        run.Task = Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start uv");
                run.Process = p;
                var stdout = p.StandardOutput.ReadToEndAsync();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                stdout.Wait();
                return (p.ExitCode, err);
            }
            catch (Exception e)
            {
                return (-1, $"could not run uv ({e.Message}); is uv installed and on PATH? (SUCHOHRAD_UV overrides)");
            }
        });
        _run = run;
        GD.Print($"edits changed: regenerating {string.Join(", ", ids)}");
        Launched?.Invoke(ids);
    }

    void Finish(Run run)
    {
        _run = null;
        var (code, err) = run.Task.Result;
        try
        {
            if (code != 0)
            {
                Fail($"parcel-run failed for {string.Join(", ", run.Ids)} (exit {code}): {err.Trim()}");
                return;
            }
            Suchohrad.MapLoader.FragmentResult result;
            try { result = Map.ApplyFragment(run.Fragment); }
            catch (Exception e)
            {
                Fail($"applying the result for {string.Join(", ", run.Ids)} failed: {e.Message}");
                return;
            }
            _applied = run.Snap;
            var ms = Time.GetTicksMsec() - (ulong)run.DetectedMs;
            LastLatencyMs = ms;
            AppliedCount++;
            GD.Print($"edits applied: {string.Join(", ", result.Replaced)} in {ms} ms " +
                     $"(objects={result.Objects} props_nodes={result.PropsNodes}, parcel-run {Time.GetTicksMsec() - (ulong)run.StartedMs} ms)");
            foreach (var line in err.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                GD.Print(line.Trim());
            Applied?.Invoke(result.Replaced, ms);
        }
        finally
        {
            foreach (var f in new[] { run.Edits, run.Fragment, run.Glb })
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
