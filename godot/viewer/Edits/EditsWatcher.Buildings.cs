using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Drycastle.ShooterCore.Editor;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.Viewer.Edits;

/// <summary>
/// Live buildings (photo to building): with --watch, a changed <c>pipeline/config/overrides.yaml</c> or <c>pipeline/config/buildings/&lt;id&gt;.yaml</c> shows
/// as a rebuilt building about a second later, with no <c>make rules export</c>. The files are polled by mtime four times a second; what changed goes to
/// one long-lived <c>building-serve</c> process (pipeline edits/building_run.py: JSON lines on stdin and stdout, the work files loaded once) and the
/// answer, a GLB and a fragment, is swapped in with MapLoader.ApplyBuildingFragment. The worker does the diff of the overrides (entries by building id
/// or parcel ID) against <c>overrides-base.yaml</c>, a copy of the file as it was last applied, so the viewer parses no YAML and a restarted worker
/// forgets nothing. Edits that arrive during a run are coalesced into the next one. The state at start is taken to be what the map was exported with.
/// A re-exported map wins: MapLoader.ReloadChunkBuildings drops the live buildings of the chunks it reloads (EditsWatcher.Map.cs).
/// Footprints, the ground under them and the documents the game reads (interiors, windows.json, voxels) are not rebuilt here.
/// The worker's life is <see cref="EditorMachines.BuildingWorkerSpec"/> (docs/state-machines.md).
/// </summary>
public partial class EditsWatcher
{
    public const double BuildingPollSeconds = 0.25;
    /// <summary>A worker that has not said ready after this, or a request not answered, is killed and reported (the work files are big; a request is under a second).</summary>
    public const int BuildingStartTimeoutSeconds = 60, BuildingRunTimeoutSeconds = 120;
    const float BuildingRestartSeconds = 2f;

    /// <summary>Raised on the main thread when a request goes to the worker: the changed files (overrides.yaml, building ids).</summary>
    public event Action<IReadOnlyList<string>>? BuildingLaunched;
    /// <summary>Raised on the main thread after a swap: parcel ids (none: the edit changed no building), edit-to-applied milliseconds.</summary>
    public event Action<IReadOnlyList<string>, double>? BuildingApplied;
    /// <summary>Raised on the main thread when a rebuild or its swap failed, or the worker could not run.</summary>
    public event Action<string>? BuildingFailed;

    public int BuildingAppliedCount { get; private set; }
    public int BuildingFailedCount { get; private set; }
    /// <summary>Edit-to-applied milliseconds of the last swap.</summary>
    public double LastBuildingMs { get; private set; }
    /// <summary>The worker's own time of the last answer, milliseconds (the rest of the edit-to-applied time is polling, the pipes and the swap).</summary>
    public double LastBuildingWorkerMs { get; private set; }
    public BuildingWorkerState BuildingWorker => _bw.Current;

    sealed class BuildingRequest
    {
        public int Id;
        public HashSet<string> Files = new();
        public bool Overrides;
        public string Snapshot = "", Glb = "";
        public long DetectedMs, SentMs;
    }

    readonly StateMachine<BuildingWorkerState> _bw = new(EditorMachines.BuildingWorkerSpec);
    string _overridesPath = "", _buildingsDir = "", _overridesBase = "";
    (DateTime, long) _overridesSeen;
    Dictionary<string, (DateTime, long)> _buildingFilesSeen = new();
    // what changed and is not sent yet (building files by name, whether overrides.yaml); what failed waits in _retry* for the next change
    HashSet<string> _pendingFiles = new(), _retryFiles = new();
    bool _pendingOverrides, _retryOverrides;
    long _pendingSinceMs;
    double _buildingPollT;
    Process? _worker;
    readonly ConcurrentQueue<string> _workerOut = new(), _workerErr = new();
    readonly List<string> _workerErrTail = new();
    volatile bool _workerEof;
    BuildingRequest? _request;
    int _requestSeq;
    long _workerStartedMs;

    bool HasPendingBuildings => _pendingFiles.Count > 0 || _pendingOverrides;

    /// <summary>Remember the files as they are (the map was exported from them) and start the worker, so the first edit does not wait for it.</summary>
    void StartBuildingWatch()
    {
        if (PipelineDir == "") return;
        var config = Path.Combine(PipelineDir, "config");
        _overridesPath = Path.Combine(config, "overrides.yaml");
        _buildingsDir = Path.Combine(config, "buildings");
        if (!File.Exists(_overridesPath) && !Directory.Exists(_buildingsDir)) return;
        _overridesSeen = StatOf(_overridesPath);
        _buildingFilesSeen = ScanBuildingFiles();
        _overridesBase = Path.Combine(_tmpDir, "overrides-base.yaml");
        try
        {
            if (File.Exists(_overridesPath)) File.Copy(_overridesPath, _overridesBase, true);
            else File.WriteAllText(_overridesBase, "buildings: {}\n");
        }
        catch (Exception e)
        {
            GD.PushWarning($"buildings watcher: could not copy {_overridesPath} ({e.Message}); buildings will not follow the files");
            _overridesBase = "";
            return;
        }
        GD.Print($"watching {_overridesPath} and {_buildingsDir} ({_buildingFilesSeen.Count} building files)");
        StartBuildingWorker();
    }

    Dictionary<string, (DateTime, long)> ScanBuildingFiles()
    {
        var found = new Dictionary<string, (DateTime, long)>();
        try
        {
            if (Directory.Exists(_buildingsDir))
                foreach (var f in new DirectoryInfo(_buildingsDir).EnumerateFiles("*.yaml")) found[f.Name] = (f.LastWriteTimeUtc, f.Length);
        }
        catch (IOException) { /* the directory is being rewritten: look again on the next poll */ return _buildingFilesSeen; }
        return found;
    }

    void StopBuildingWorker()
    {
        _bw.Enter(BuildingWorkerState.Stopped);
        KillWorker();
    }

    void KillWorker()
    {
        var p = _worker;
        _worker = null;
        if (p == null) return;
        try { p.StandardInput.Close(); } catch { /* already closed */ }
        try { if (!p.HasExited) p.Kill(true); } catch { /* already gone */ }
        p.Dispose();
    }

    /// <summary>Every poll: note the files that changed on disk (mtime and size), and queue them.</summary>
    void PollBuildings(double delta)
    {
        if (_overridesBase == "") return;
        _buildingPollT += delta;
        if (_buildingPollT < BuildingPollSeconds) return;
        _buildingPollT = 0;
        var wasEmpty = !HasPendingBuildings;
        var changed = false;
        var st = StatOf(_overridesPath);
        if (st != _overridesSeen) { _overridesSeen = st; _pendingOverrides = true; changed = true; }
        var now = ScanBuildingFiles();
        foreach (var (name, stat) in now)
            if (!_buildingFilesSeen.TryGetValue(name, out var old) || old != stat) { _pendingFiles.Add(name); changed = true; }
        foreach (var name in _buildingFilesSeen.Keys)
            if (!now.ContainsKey(name)) { _pendingFiles.Add(name); changed = true; } // a deleted detail: the building goes back to the plain massing
        _buildingFilesSeen = now;
        if (!changed) return;
        if (wasEmpty) _pendingSinceMs = (long)Time.GetTicksMsec(); // edit-to-applied counts from the first change of a run's batch
        // what failed before goes again with this change
        _pendingFiles.UnionWith(_retryFiles);
        _pendingOverrides |= _retryOverrides;
        _retryFiles.Clear();
        _retryOverrides = false;
        if (_bw.Is(BuildingWorkerState.Running)) _bw.Enter(BuildingWorkerState.Queued);
    }

    /// <summary>Every frame: read what the worker said, watch its health, send the next request.</summary>
    void StepBuildings(double delta)
    {
        if (_overridesBase == "") return;
        _bw.Tick((float)delta);
        while (_workerErr.TryDequeue(out var line))
        {
            GD.Print($"building-serve: {line}");
            _workerErrTail.Add(line);
            if (_workerErrTail.Count > 8) _workerErrTail.RemoveAt(0);
        }
        while (_workerOut.TryDequeue(out var line)) HandleWorkerLine(line);
        var life = _bw.Current;
        if (life is BuildingWorkerState.Starting or BuildingWorkerState.Idle or BuildingWorkerState.Running or BuildingWorkerState.Queued)
        {
            if (_worker == null || _worker.HasExited || (_workerEof && _workerOut.IsEmpty))
                WorkerDown($"the building worker exited{(_worker is { HasExited: true } w ? $" (code {w.ExitCode})" : "")}");
            else if (life == BuildingWorkerState.Starting && _bw.Elapsed > BuildingStartTimeoutSeconds)
                WorkerDown($"the building worker did not start in {BuildingStartTimeoutSeconds} s");
            else if (life is BuildingWorkerState.Running or BuildingWorkerState.Queued && _bw.Elapsed > BuildingRunTimeoutSeconds)
                WorkerDown($"the building worker did not answer in {BuildingRunTimeoutSeconds} s");
        }
        CheckOpTimeouts();
        if (!HasPendingBuildings) return;
        if (_bw.Is(BuildingWorkerState.Idle)) SendBuildingRequest();
        else if (_bw.Is(BuildingWorkerState.Down) && _bw.Expired) StartBuildingWorker();
    }

    void StartBuildingWorker()
    {
        KillWorker();
        while (_workerOut.TryDequeue(out _)) { }
        while (_workerErr.TryDequeue(out _)) { }
        _workerEof = false;
        _workerErrTail.Clear();
        _bw.Enter(BuildingWorkerState.Starting);
        var psi = UvStart("building-serve");
        psi.RedirectStandardInput = true;
        try
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start uv");
            _worker = p;
            _workerStartedMs = (long)Time.GetTicksMsec();
            Task.Run(() =>
            {
                try { while (p.StandardOutput.ReadLine() is { } line) _workerOut.Enqueue(line); } catch { /* the process went away */ }
                _workerEof = true;
            });
            Task.Run(() =>
            {
                try { while (p.StandardError.ReadLine() is { } line) _workerErr.Enqueue(line); } catch { /* the process went away */ }
            });
        }
        catch (Exception e)
        {
            WorkerDown($"could not run uv ({e.Message}); is uv installed and on PATH? (DRYCASTLE_UV overrides)");
        }
    }

    /// <summary>The worker is gone or stuck: kill it, keep what was on the wire or waiting for the next change, say so. It is started again by the next change.</summary>
    void WorkerDown(string reason)
    {
        KillWorker();
        if (_request != null)
        {
            _retryFiles.UnionWith(_request.Files);
            _retryOverrides |= _request.Overrides;
            DropTemp(_request);
            _request = null;
        }
        _retryFiles.UnionWith(_pendingFiles);
        _retryOverrides |= _pendingOverrides;
        _pendingFiles.Clear();
        _pendingOverrides = false;
        FailOps(reason);
        var tail = _workerErrTail.Count > 0 ? ": " + string.Join(" | ", _workerErrTail.TakeLast(3)) : "";
        BuildingFail(reason + tail);
        _bw.Enter(BuildingWorkerState.Down, BuildingRestartSeconds);
    }

    void HandleWorkerLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { GD.Print($"building-serve: {line}"); return; } // something printed on stdout that is no answer
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("ready", out var ready))
            {
                if (!_bw.Is(BuildingWorkerState.Starting)) return;
                if (ready.GetBoolean())
                {
                    GD.Print($"building worker ready in {Time.GetTicksMsec() - (ulong)_workerStartedMs} ms");
                    _bw.Enter(BuildingWorkerState.Idle);
                    FlushWaitingOps();
                }
                else WorkerDown($"the building worker could not start: {(root.TryGetProperty("error", out var e) ? e.GetString() : "?")}");
                return;
            }
            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            {
                if (_ops.ContainsKey(id.GetInt32())) HandleOpAnswer(id.GetInt32(), root);
                else if (_request != null && id.GetInt32() == _request.Id) HandleAnswer(root);
            }
        }
    }

    void SendBuildingRequest()
    {
        var req = new BuildingRequest
        {
            Id = ++_requestSeq,
            Files = _pendingFiles,
            Overrides = _pendingOverrides,
            DetectedMs = _pendingSinceMs,
            SentMs = (long)Time.GetTicksMsec(),
        };
        req.Glb = Path.Combine(_tmpDir, $"buildings-{req.Id}.glb");
        if (req.Overrides)
        {
            // the worker builds from exactly the file we diffed, not whatever it is by then
            req.Snapshot = Path.Combine(_tmpDir, $"overrides-{req.Id}.yaml");
            try { File.Copy(_overridesPath, req.Snapshot, true); }
            catch (Exception e)
            {
                _retryFiles.UnionWith(req.Files);
                _retryOverrides = true;
                _pendingFiles = new();
                _pendingOverrides = false;
                BuildingFail($"could not read {_overridesPath}: {e.Message}");
                return;
            }
        }
        _pendingFiles = new();
        _pendingOverrides = false;
        var line = JsonSerializer.Serialize(new
        {
            id = req.Id,
            glb = req.Glb,
            files = req.Files.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            overrides = req.Overrides ? req.Snapshot : null,
            overrides_base = req.Overrides ? _overridesBase : null,
        });
        _request = req;
        try
        {
            _worker!.StandardInput.WriteLine(line);
            _worker.StandardInput.Flush();
        }
        catch (Exception e)
        {
            WorkerDown($"could not write to the building worker ({e.Message})");
            return;
        }
        _bw.Enter(BuildingWorkerState.Running);
        var names = req.Files.Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (req.Overrides) names.Insert(0, "overrides.yaml");
        GD.Print($"buildings changed: rebuilding for {string.Join(", ", names)}");
        BuildingLaunched?.Invoke(names);
    }

    void HandleAnswer(JsonElement root)
    {
        var req = _request!;
        _request = null;
        string? error = null;
        var parcels = new List<string>();
        try
        {
            if (!root.GetProperty("ok").GetBoolean()) error = root.TryGetProperty("error", out var e) ? e.GetString() : "failed";
            else
            {
                var fragment = root.GetProperty("fragment");
                parcels = fragment.GetProperty("parcels").EnumerateArray().Select(p => p.GetString()!).ToList();
                var workerMs = fragment.TryGetProperty("timing_s", out var t) && t.TryGetProperty("total", out var tt) ? tt.GetDouble() * 1000 : 0;
                if (parcels.Count == 0) GD.Print("buildings: the change touches no building on the map");
                else
                {
                    var result = Map.ApplyBuildingFragment(fragment.GetRawText());
                    GD.Print($"building nodes added: {result.Nodes} ({result.Triangles} triangles), worker {workerMs:F0} ms");
                }
                if (fragment.TryGetProperty("warnings", out var w))
                    foreach (var line in w.EnumerateArray()) GD.Print($"building warning: {line.GetString()}");
                LastBuildingWorkerMs = workerMs;
            }
        }
        catch (Exception e) { error = $"applying the result failed: {e.Message}"; }
        if (error != null)
        {
            _retryFiles.UnionWith(req.Files);
            _retryOverrides |= req.Overrides;
            BuildingFail($"buildings not applied ({string.Join(", ", req.Files.Select(Path.GetFileNameWithoutExtension).Concat(req.Overrides ? new[] { "overrides.yaml" } : Array.Empty<string>()))}): {error}");
        }
        else
        {
            if (req.Overrides) try { File.Copy(req.Snapshot, _overridesBase, true); } catch (Exception e) { GD.PushWarning($"could not remember the applied overrides: {e.Message}"); }
            var ms = Time.GetTicksMsec() - (ulong)req.DetectedMs;
            LastBuildingMs = ms;
            BuildingAppliedCount++;
            GD.Print($"building applied: {(parcels.Count == 0 ? "-" : string.Join(", ", parcels))} in {ms} ms");
            BuildingApplied?.Invoke(parcels, ms);
        }
        DropTemp(req);
        // files that changed while this ran go out now, the rest waits
        if (_bw.Is(BuildingWorkerState.Queued) && HasPendingBuildings) SendBuildingRequest();
        else _bw.Enter(BuildingWorkerState.Idle);
    }

    void BuildingFail(string message)
    {
        BuildingFailedCount++;
        GD.PushError(message);
        GD.Print($"building error: {message}");
        BuildingFailed?.Invoke(message);
    }

    static void DropTemp(BuildingRequest req)
    {
        foreach (var f in new[] { req.Glb, req.Snapshot })
            if (f != "") try { File.Delete(f); } catch { /* best effort */ }
    }
}
