using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Drycastle.ShooterCore.Editor;

namespace Drycastle.Viewer.Edits;

/// <summary>
/// Requests of the editor to the building worker other than "rebuild" (pipeline edits/building_edit.py: <c>state</c>, <c>edit</c>, <c>restore</c>, <c>snapshot</c>, <c>reset</c>): the
/// Photo to building tool reads a building's files as a form and writes its changes through the worker, which edits <c>overrides.yaml</c> and
/// <c>config/buildings/&lt;id&gt;.yaml</c> atomically and with minimal diffs. The rebuild itself is the usual loop: the files change, the poll sees it, the building is
/// swapped in (an answered op makes the next poll come at once). Requests go in the order given and the worker answers them in that order. Callbacks run on the main thread.
/// </summary>
public partial class EditsWatcher
{
    /// <summary>A request the worker does not answer in this many seconds takes the worker down (it is started again by the next request).</summary>
    public const int BuildingOpTimeoutSeconds = 30;

    sealed record PendingOp(Action<JsonElement?, string?> Done, long SentMs);

    readonly Dictionary<int, PendingOp> _ops = new();
    readonly List<(int Id, string Line, Action<JsonElement?, string?> Done)> _opsWaiting = new();

    /// <summary>The viewer follows building files at all (the pipeline's overrides.yaml or building folder exists and the worker was started).</summary>
    public bool BuildingOpsAvailable => _overridesBase != "";

    /// <summary>The pipeline directory's parent: where <c>data/village/photos</c> and the repo-root <c>.env</c> are.</summary>
    public string RepoDir => PipelineDir == "" ? "" : System.IO.Path.GetFullPath(System.IO.Path.Combine(PipelineDir, ".."));

    /// <summary>
    /// Send one op (<c>op</c>, and its fields) to the worker. <paramref name="done"/> gets the answer's <c>result</c>, or an error text (the worker's, or "the worker is not running").
    /// A worker that is still starting, or is down, is waited for (a down one is started).
    /// </summary>
    public void BuildingOp(string op, IReadOnlyDictionary<string, object?> fields, Action<JsonElement?, string?> done)
    {
        if (!BuildingOpsAvailable) { done(null, "live buildings are not running: no pipeline/config/overrides.yaml was found next to the viewer's map"); return; }
        var id = ++_requestSeq;
        var doc = new Dictionary<string, object?>(fields) { ["id"] = id, ["op"] = op };
        var line = JsonSerializer.Serialize(doc);
        if (_bw.IsAny(BuildingWorkerState.Idle, BuildingWorkerState.Running, BuildingWorkerState.Queued)) SendOp(id, line, done);
        else
        {
            _opsWaiting.Add((id, line, done));
            if (_bw.IsAny(BuildingWorkerState.Down, BuildingWorkerState.Stopped)) StartBuildingWorker();
        }
    }

    void SendOp(int id, string line, Action<JsonElement?, string?> done)
    {
        _ops[id] = new PendingOp(done, (long)Time.GetTicksMsec());
        try
        {
            _worker!.StandardInput.WriteLine(line);
            _worker.StandardInput.Flush();
        }
        catch (Exception e)
        {
            WorkerDown($"could not write to the building worker ({e.Message})");
        }
    }

    void FlushWaitingOps()
    {
        var waiting = _opsWaiting.ToList();
        _opsWaiting.Clear();
        foreach (var (id, line, done) in waiting) SendOp(id, line, done);
    }

    void HandleOpAnswer(int id, JsonElement root)
    {
        var op = _ops[id];
        _ops.Remove(id);
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            _buildingPollT = BuildingPollSeconds; // the files changed: look at them now, not up to a quarter second from now
            op.Done(root.TryGetProperty("result", out var r) ? r.Clone() : null, null);
        }
        else op.Done(null, root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "the building worker refused the request");
    }

    void FailOps(string reason)
    {
        var all = _ops.Values.Select(o => o.Done).Concat(_opsWaiting.Select(w => w.Done)).ToList();
        _ops.Clear();
        _opsWaiting.Clear();
        foreach (var done in all) done(null, reason);
    }

    void CheckOpTimeouts()
    {
        if (_ops.Count == 0) return;
        var now = (long)Time.GetTicksMsec();
        if (_ops.Values.Any(o => now - o.SentMs > BuildingOpTimeoutSeconds * 1000L)) WorkerDown($"the building worker did not answer in {BuildingOpTimeoutSeconds} s");
    }

    /// <summary>A uv process in the pipeline directory that runs <paramref name="module"/> (<c>python -m</c>) with the arguments; DRYCASTLE_UV names uv as for the other workers.</summary>
    public ProcessStartInfo UvModuleStart(string module, IEnumerable<string> args)
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
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        foreach (var a in new[] { "run", "python", "-u", "-m", module }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }
}
