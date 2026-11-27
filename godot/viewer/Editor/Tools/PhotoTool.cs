using Drycastle.ShooterCore.Editor;
using Drycastle.ShooterCore.Fsm;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>A building on the selected parcels.</summary>
public sealed record BuildingRef(string Id, string Parcel);

/// <summary>
/// The Photo to building tool (docs/photo-to-building.md, "In the viewer"): works on the buildings of the selected parcels. It lists the building's Street View captures and the
/// user's own photos, runs <c>facade_pipeline auto</c> as a subprocess (the model's two questions, the measuring, the two files), shows the result for review, and then edits the
/// building's files live: every form change is a request to the building worker (<see cref="Drycastle.Viewer.Edits.EditsWatcher.BuildingOp"/>), which writes
/// <c>overrides.yaml</c> / <c>config/buildings/&lt;id&gt;.yaml</c> atomically; the watcher's usual loop (docs/viewer-and-editor.md, "Hot reload") swaps the rebuilt house in within a second.
/// Panel phases are <see cref="EditorMachines.PhotoBuildSpec"/>. This file: what the tool holds and the build; <c>PhotoTool.Edit.cs</c>: the live edits, their undo and the 3D picking.
/// </summary>
public sealed partial class PhotoTool : EditorPart
{
    public const int BuildTimeoutSeconds = 600;
    const int LogTailLines = 8;

    readonly StateMachine<PhotoBuildState> _fsm = new(EditorMachines.PhotoBuildSpec);
    readonly PhotoPanel _panel;
    readonly PhotoMarker _marker;
    readonly PhotoViewer _viewer = new();
    string _selSig = "";
    PhotoRun? _run;
    string? _runBuilding;
    JsonElement? _beforeBuild;
    readonly List<string> _log = new();

    public PhotoTool(IEditorHost host) : base(host)
    {
        _panel = new PhotoPanel(host, this);
        _marker = new PhotoMarker(host);
    }

    // ---- what the panel shows ----

    public PhotoBuildState Phase => _fsm.Current;
    public bool Locked => _fsm.IsAny(PhotoBuildState.Building, PhotoBuildState.Cancelling);
    public IReadOnlyList<BuildingRef> Buildings { get; private set; } = Array.Empty<BuildingRef>();
    public string? Current { get; private set; }
    public List<CaptureInfo> Captures { get; private set; } = new();
    public HashSet<int> TickedCaptures { get; } = new();
    public List<OwnPhoto> Own { get; private set; } = new();
    public bool ApiKey { get; private set; } = true;
    public string Stage { get; private set; } = "";
    public int StageIndex { get; private set; }
    public int StageCount { get; private set; }
    public double RunSeconds => _run?.Seconds ?? 0;
    public IReadOnlyList<string> LogTail => _log;
    public PhotoResult? Result { get; private set; }
    public bool ResultFromDisk { get; private set; }
    public List<string> Problems { get; } = new();
    public List<string> Invented { get; } = new();
    public string? Error { get; private set; }
    public bool KeyMissing { get; private set; }
    public BuildingForm? Form { get; private set; }
    public string? FormError { get; private set; }
    public bool FormLoading { get; private set; }
    public string? SelectedOpening { get; private set; }
    public int Version { get; private set; }
    public bool ViewerOpen => _viewer.Visible;
    public string Repo => Host.Watcher?.RepoDir ?? "";

    internal void Bump() { Version++; Host.MarkPanelDirty(); }

    public void BuildPage(VBoxContainer page) => _panel.Build(page);
    public void RefreshPanel() => _panel.Refresh();
    public void BuildViewer(Control root) => _viewer.Build(root);
    public void BuildWorld() => _marker.Build();
    public void CloseViewer() => _viewer.Close();
    public void ViewerKey(InputEventKey k) => _viewer.OnKey(k);

    public void OpenPictures(IReadOnlyList<(string Path, string Caption)> items, int at) => _viewer.Open(items, at);

    // ---- per frame ----

    public override void Update(double delta)
    {
        var active = Host.Tool == EditorTool.Photo;
        PumpRun(delta);
        PumpQueue(delta);
        if (active && !Locked) WatchSelection();
        _marker.Show(Form, SelectedOpening, active);
        if (active) _panel.UpdateTimer();
    }

    public override void ActiveChanged(bool on)
    {
        if (!on) _viewer.Close();
    }

    /// <summary>The editor is closing for good: a build still running is stopped.</summary>
    public void Shutdown() => _run?.Kill();

    void WatchSelection()
    {
        var sig = string.Join(",", Host.Selection);
        if (sig == _selSig) return;
        _selSig = sig;
        var list = new List<BuildingRef>();
        foreach (var pid in Host.Selection)
            if (Host.Map.Parcels.TryGetValue(pid, out var p))
                foreach (var b in p.BuildingIds) list.Add(new BuildingRef(b, pid));
        Buildings = list;
        var keep = list.Any(b => b.Id == Current) ? Current : DefaultBuilding(list);
        if (keep != Current) LoadBuilding(keep); else Bump();
    }

    /// <summary>The building to start with: the one that has captures, else the first.</summary>
    string? DefaultBuilding(IReadOnlyList<BuildingRef> list)
        => list.FirstOrDefault(b => PhotoFiles.Captures(Repo, b.Id).Count > 0)?.Id ?? list.FirstOrDefault()?.Id;

    public void SelectBuilding(string id)
    {
        if (Locked || id == Current) return;
        LoadBuilding(id);
    }

    void LoadBuilding(string? id)
    {
        if (_fsm.IsAny(PhotoBuildState.Review, PhotoBuildState.Failed)) _fsm.Enter(PhotoBuildState.Idle);
        Current = id;
        SelectedOpening = null;
        Form = null; FormError = null; FormLoading = false;
        Result = null; Problems.Clear(); Invented.Clear(); Error = null; KeyMissing = false;
        TickedCaptures.Clear();
        Captures = id == null ? new() : PhotoFiles.Captures(Repo, id);
        foreach (var c in Captures.TakeLast(Drycastle.ShooterCore.Editor.PhotoDefaults.Captures)) TickedCaptures.Add(c.N);
        Own = id == null ? new() : PhotoFiles.Own(Repo, id);
        if (Captures.Count == 0) foreach (var o in Own) o.Ticked = true;
        ApiKey = Repo == "" || PhotoFiles.HasApiKey(Repo);
        if (id != null)
        {
            LoadForm();
            var last = PhotoFiles.LastRun(Repo, id);
            if (last != null && _fsm.Is(PhotoBuildState.Idle))
            {
                Result = last with { Parcel = Buildings.FirstOrDefault(b => b.Id == id)?.Parcel ?? "" };
                ResultFromDisk = true;
                Problems.AddRange(last.Problems); Invented.AddRange(last.Invented);
                _fsm.Enter(PhotoBuildState.Review);
            }
        }
        Bump();
    }

    /// <summary>Read the building's files again through the worker (after a build, an undo, a failed edit).</summary>
    public void LoadForm()
    {
        var id = Current;
        if (id == null) return;
        var w = Host.Watcher;
        if (w == null) { FormError = "no building worker: start the viewer with --watch (the editor does)"; Bump(); return; }
        FormLoading = true;
        w.BuildingOp("state", new Dictionary<string, object?> { ["building"] = id }, (res, err) =>
        {
            if (id != Current) return;
            FormLoading = false;
            if (err != null) { FormError = err; Form = null; }
            else
            {
                FormError = null;
                Form = BuildingForm.Parse(res!.Value);
                if (SelectedOpening != null && Form.Detail?.Openings.All(o => o.Id != SelectedOpening) != false) SelectedOpening = null;
            }
            Bump();
        });
    }

    public void RecheckKey()
    {
        ApiKey = Repo == "" || PhotoFiles.HasApiKey(Repo);
        Host.Say(ApiKey ? "ANTHROPIC_API_KEY found" : "ANTHROPIC_API_KEY is still missing: add it to the repo-root .env", !ApiKey);
        Bump();
    }

    // ---- photos ----

    public void TickCapture(int n, bool on)
    {
        if (on) { TickedCaptures.Add(n); foreach (var o in Own) o.Ticked = false; } else TickedCaptures.Remove(n);
        Bump();
    }

    public void TickOwn(OwnPhoto o, bool on)
    {
        o.Ticked = on;
        if (on) TickedCaptures.Clear();
        Bump();
    }

    public void SetOwnPlace(OwnPhoto o, string? side, string? point)
    {
        o.Side = side; o.Point = point;
        PhotoFiles.SaveOwn(o);
        Bump();
    }

    public void AddOwnPhoto(string path)
    {
        if (Current == null) return;
        var o = PhotoFiles.AddOwn(Repo, Current, path, out var err);
        if (o == null) { Host.Say(err ?? "could not add the photo", true); return; }
        TickedCaptures.Clear();
        foreach (var other in Own) other.Ticked = false;
        Own.Add(o);
        Host.Say($"added {o.Name}: say where it was taken, then Build");
        Bump();
    }

    public void RemoveOwn(OwnPhoto o)
    {
        PhotoFiles.RemoveOwn(o);
        Own.Remove(o);
        Host.Say($"removed {o.Name} from the building's photos");
        Bump();
    }

    // ---- the build ----

    public string? BuildBlocker()
    {
        if (Current == null) return "Select a parcel with a house.";
        if (Locked) return "A build is running.";
        if (Host.Watcher == null || !Host.Watcher.BuildingOpsAvailable) return "Live buildings are not running: the viewer found no pipeline/config/overrides.yaml.";
        var own = Own.Where(o => o.Ticked).ToList();
        if (TickedCaptures.Count == 0 && own.Count == 0) return Captures.Count == 0 && Own.Count == 0 ? "No photos yet: add one, or take captures." : "Tick at least one photo.";
        if (own.Any(o => !o.HasPlace)) return "Say where each of your photos was taken.";
        if (!ApiKey) return "ANTHROPIC_API_KEY is missing.";
        return null;
    }

    public string BuildLabel()
    {
        var own = Own.Count(o => o.Ticked);
        return own > 0 ? $"Build from {own} of your photo{(own == 1 ? "" : "s")}" : $"Build from {TickedCaptures.Count} capture{(TickedCaptures.Count == 1 ? "" : "s")}";
    }

    public void Build()
    {
        if (BuildBlocker() is { } why) { Host.Say(why, true); return; }
        if (Form is { HasOverride: true } f)
        {
            var n = f.Detail?.Openings.Count ?? 0;
            Host.Confirm.Show($"Replace the model of {Current}?",
                $"{Current} already has an entry in overrides.yaml{(f.HasDetail ? $" and a detail file with {n} opening{(n == 1 ? "" : "s")}" : "")}. Building writes new ones over them, including adjustments made here. Ctrl+Z brings the old ones back.",
                "Replace", () => Launch(BuildArgs(true)));
        }
        else Launch(BuildArgs(false));
    }

    /// <summary>After "already has an entry": the user said replace.</summary>
    public void BuildReplacing()
    {
        if (BuildBlocker() is { } why) { Host.Say(why, true); return; }
        Host.Confirm.Show($"Replace the model of {Current}?", "The existing entry and detail file are written over. Ctrl+Z brings them back.", "Replace", () => Launch(BuildArgs(true)));
    }

    List<string> BuildArgs(bool replace)
    {
        var args = new List<string> { "auto", Current! };
        var own = Own.Where(o => o.Ticked).ToList();
        if (own.Count > 0)
            foreach (var o in own) { args.Add("--photo"); args.Add(o.Png); args.Add(o.Side != null ? "--side" : "--point"); args.Add(o.Side ?? o.Point!); }
        else { args.Add("--captures"); args.Add(string.Join(",", TickedCaptures.Order())); }
        if (replace) args.Add("--replace");
        return args;
    }

    /// <summary>Ask a question of the model again ("ends" or "walls") and carry on to the files: <c>auto --redo</c>.</summary>
    public void ReAsk(string stage)
    {
        if (Current == null || Locked) return;
        if (stage == "ends" && Result is { CanRedoEnds: false }) { Host.Say("the wall ends were marked by hand at capture: there is nothing to ask again", true); return; }
        if (!ApiKey) { Host.Say("ANTHROPIC_API_KEY is missing: add it to the repo-root .env", true); return; }
        Launch(new List<string> { "auto", Current, "--redo", stage, "--replace" });
    }

    void Launch(List<string> args)
    {
        var w = Host.Watcher;
        if (w == null || Current == null) return;
        ResetRun();
        _runBuilding = Current;
        w.BuildingOp("snapshot", new Dictionary<string, object?> { ["building"] = Current }, (res, _) => _beforeBuild = res?.GetProperty("snapshot"));
        try
        {
            _run = PhotoRun.Start(w.UvModuleStart("drycastle_pipeline.photos.facade_pipeline", args));
        }
        catch (Exception e)
        {
            Fail($"could not run uv ({e.Message}); is uv installed and on PATH? (DRYCASTLE_UV overrides)");
            return;
        }
        _fsm.Enter(PhotoBuildState.Building);
        Host.Say($"building {Current}: {string.Join(" ", args.Skip(2).Where(a => !a.StartsWith('/')))}");
        Bump();
    }

    void ResetRun()
    {
        Stage = ""; StageIndex = StageCount = 0;
        Result = null; ResultFromDisk = false; Error = null; KeyMissing = false;
        Problems.Clear(); Invented.Clear(); _log.Clear(); _beforeBuild = null;
    }

    void Fail(string message)
    {
        Error = message;
        _fsm.Enter(PhotoBuildState.Failed);
        Host.Say("build failed: " + message, true);
        Bump();
    }

    /// <summary>Esc or the Cancel button: ask, then stop the build process. The files are written at the very end of a build, so little is lost.</summary>
    public void CancelBuild()
    {
        if (!_fsm.Is(PhotoBuildState.Building)) return;
        Host.Confirm.Show("Stop the build?", "The model calls so far are not undone and cost what they cost; the building's files are written only at the end, so they stay as they are.", "Stop the build", () =>
        {
            if (!_fsm.Is(PhotoBuildState.Building)) return;
            _fsm.Enter(PhotoBuildState.Cancelling);
            _run?.Kill();
            Bump();
        });
    }

    /// <summary>"Back" from a failure or a review: the photo list and Build again.</summary>
    public void Back()
    {
        if (_fsm.IsAny(PhotoBuildState.Review, PhotoBuildState.Failed)) { _fsm.Enter(PhotoBuildState.Idle); Bump(); }
    }

    void PumpRun(double delta)
    {
        if (_run == null) return;
        _run.Tick(delta);
        var changed = false;
        while (_run.Lines.TryDequeue(out var line))
        {
            changed = true;
            if (line.StartsWith("stderr: ", StringComparison.Ordinal)) { Tail(line[8..]); continue; }
            var l = PhotoBuildLog.Parse(line);
            switch (l.Kind)
            {
                case PhotoLineKind.Stage: Stage = l.Stage; StageIndex = l.Index; StageCount = l.Count; break;
                case PhotoLineKind.Problem: Problems.Add(l.Text); break;
                case PhotoLineKind.Made: Invented.AddRange(l.Text.Split("; ", StringSplitOptions.RemoveEmptyEntries)); break;
                case PhotoLineKind.Result: Result = l.Result; break;
                case PhotoLineKind.Error: Error = l.Text; break;
                default: Tail(l.Text); break;
            }
        }
        if (_fsm.Is(PhotoBuildState.Building) && _run.Seconds > BuildTimeoutSeconds)
        {
            Error = $"the build took more than {BuildTimeoutSeconds / 60} minutes and was stopped";
            _run.Kill();
        }
        if (_run.Finished) FinishRun();
        else if (changed) Bump();
    }

    void Tail(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _log.Add(text.Length > 160 ? text[..160] + "..." : text);
        if (_log.Count > LogTailLines) _log.RemoveAt(0);
    }

    void FinishRun()
    {
        var code = _run!.ExitCode;
        _run.Dispose();
        _run = null;
        if (_fsm.Is(PhotoBuildState.Cancelling))
        {
            _fsm.Enter(PhotoBuildState.Idle);
            Host.Say("build stopped");
            LoadForm();
            Bump();
            return;
        }
        if (code == 0 && Result != null)
        {
            _fsm.Enter(PhotoBuildState.Review);
            Host.Say($"built {Result.Building}: {Result.Roof} roof, {Result.Openings} opening{(Result.Openings == 1 ? "" : "s")}; the viewer shows it in a moment (Ctrl+Z restores the old files)");
            PushBuildUndo(_runBuilding ?? Result.Building);
            if (Current == _runBuilding) LoadForm();
            Bump();
            return;
        }
        KeyMissing = code == 2;
        Fail(Error ?? (_log.Count > 0 ? _log[^1] : $"the build process ended with code {code} and no message"));
    }

    /// <summary>The build changed both files: its undo step restores what they were before it, and redo what it wrote.</summary>
    void PushBuildUndo(string building)
    {
        var before = _beforeBuild;
        _beforeBuild = null;
        var w = Host.Watcher;
        if (before == null || w == null) return;
        w.BuildingOp("snapshot", new Dictionary<string, object?> { ["building"] = building }, (res, _) =>
        {
            if (res?.GetProperty("snapshot") is not { } after) return;
            Host.Edits.History.Push(new UndoRecord($"photo build of {building}", new List<EntryChange>(), Replay: undo => Restore(building, undo ? before.Value : after)));
        });
    }

    // ---- hints and keys ----

    public string HintText()
    {
        if (Phase == PhotoBuildState.Building) return "building: Esc or Cancel stops it; the house changes in 3D when the files are written";
        if (SelectedOpening != null) return "opening: arrows nudge (Left/Right along the wall, Up/Down the sill, Shift x5), [ ] pick another, Ctrl+D duplicate, Del delete, Ctrl+Z undo, Esc deselect";
        if (Form?.Detail != null) return "click an opening in 3D to select it, or a row in the panel; every change shows in 3D at once, Ctrl+Z undoes, Esc back";
        return "select a parcel with a house, tick its photos, Build; Esc back";
    }

    /// <summary>Esc, one level: the enlarged picture, then the selected opening; false when this tool has nothing to step back from.</summary>
    public bool Escape()
    {
        if (_viewer.Visible) { _viewer.Close(); return true; }
        if (_fsm.Is(PhotoBuildState.Building)) { CancelBuild(); return true; }
        if (SelectedOpening != null) { SelectOpening(null); return true; }
        return false;
    }
}
