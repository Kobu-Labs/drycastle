using Drycastle.Viewer.Edits;
using System;
using System.Collections.Generic;
using System.IO;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The edits document as the editor sees it: the last good read of the file (<see cref="Disk"/>), the one way the editor
/// writes it (<see cref="Mutate"/>) and undo/redo of those writes (<see cref="History"/>).
/// </summary>
public sealed class EditStore
{
    const string LockedMessage = "not saved: edits.json is being written by another program (the MCP server?), try again in a moment";

    readonly IEditorHost _host;
    readonly Action _prepareValidation;
    readonly Action<SketchDoc> _restoreSketch;
    Obj? _disk;
    (DateTime, long) _seen;

    /// <param name="prepareValidation">Runs before every write: tells the validator what the map's catalogs hold.</param>
    /// <param name="restoreSketch">Replays a sketch step of the history (it has no file changes).</param>
    public EditStore(IEditorHost host, Action prepareValidation, Action<SketchDoc> restoreSketch)
        => (_host, _prepareValidation, _restoreSketch) = (host, prepareValidation, restoreSketch);

    public UndoHistory History { get; } = new();

    /// <summary>Number of times edits.json was written by this editor (group operations and Apply).</summary>
    public int SaveCount { get; private set; }
    /// <summary>Wall time of the last save from reading the file to the objects being instanced (when instant), milliseconds.</summary>
    public double LastSaveMs { get; private set; }
    /// <summary>True when the last save was applied in process (objects only, no parcel-run).</summary>
    public bool LastSaveInstant { get; private set; }

    /// <summary>The file as last read (a new object on every reload, so its identity keys caches).</summary>
    public Obj Disk()
    {
        if (_disk != null) return _disk;
        try { _disk = EditsFile.Load(_host.EditsPath); } catch (Exception) { _disk = EditsFile.Load("/nonexistent"); }
        return _disk;
    }

    /// <summary>Read the file again; throws when it is half-written or invalid (the last good read stays then).</summary>
    public Obj Reload() => _disk = EditsFile.Load(_host.EditsPath);

    /// <summary>Remember the file's current state, for <see cref="ChangedOnDisk"/>.</summary>
    public void Remember() => _seen = Stat();

    /// <summary>Whether the file changed since the last call (or <see cref="Remember"/>): another writer saved it.</summary>
    public bool ChangedOnDisk()
    {
        var st = Stat();
        if (st == _seen) return false;
        _seen = st;
        return true;
    }

    (DateTime, long) Stat()
    {
        try
        {
            var fi = new FileInfo(_host.EditsPath);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException) { return (DateTime.MinValue, -1); }
    }

    /// <summary>
    /// Reload the file from disk, apply the operation to that fresh content (another writer may have
    /// changed it), validate the result (the Python validator's rules), write it back atomically and refresh
    /// the group data. The touched `parcels.&lt;ID&gt;` / `groups.&lt;ID&gt;` entries (before and after) go on the undo
    /// stack under <paramref name="description"/> (default: the operation's message). Returns whether it was saved.
    /// </summary>
    public bool Mutate(Func<Obj, (bool ok, string message)> op, string? description = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _prepareValidation();
            using var wlock = EditsLock.TryAcquire(_host.EditsPath); // same flock as the MCP server's writes, held from the load to the save
            if (wlock == null) { _host.Say(LockedMessage, true); return false; }
            var root = EditsFile.Load(_host.EditsPath);
            var before = ObjectEdits.CloneObj(root);
            var (ok, msg) = op(root);
            if (!ok) { _host.Say(msg, true); _host.ReloadGroups(); return false; }
            var errors = ObjectEdits.ValidateDocument(root);
            if (errors.Count > 0) { _host.Say("not saved: " + ObjectEdits.Summarise(errors), true); _host.ReloadGroups(); return false; }
            if (EditsFile.Dump(before) == EditsFile.Dump(root)) { _host.Say("no changes to save"); return true; } // the file already says this
            var changes = UndoHistory.Diff(before, root);
            EditsFile.Save(_host.EditsPath, root);
            SaveCount++;
            if (changes.Count > 0) History.Push(new UndoRecord(description ?? msg, changes));
            AfterSave(sw);
            _host.Say($"saved ({msg})");
            return true;
        }
        catch (Exception e)
        {
            _host.Say($"not saved: {e.Message}", true);
            return false;
        }
    }

    public bool Undo() => Step(true);
    public bool Redo() => Step(false);

    /// <summary>Write the before-values (undo) or after-values (redo) of the newest record back into a fresh read of the file.</summary>
    bool Step(bool undo)
    {
        var word = undo ? "undo" : "redo";
        if (History.Peek(undo) is not { } rec) { _host.Say($"nothing to {word}"); return false; }
        if (rec.SketchBefore != null && rec.SketchAfter != null)
        {
            History.Moved(undo);
            _restoreSketch(undo ? rec.SketchBefore : rec.SketchAfter);
            _host.Say($"{word}: {rec.Description}");
            return true;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _prepareValidation();
            using var wlock = EditsLock.TryAcquire(_host.EditsPath);
            if (wlock == null) { _host.Say($"cannot {word}: edits.json is being written by another program (the MCP server?), try again in a moment", true); return false; }
            var root = EditsFile.Load(_host.EditsPath);
            foreach (var c in rec.Changes)
                UndoHistory.Apply(root, c, undo ? c.Before : c.After);
            var errors = ObjectEdits.ValidateDocument(root);
            if (errors.Count > 0) { _host.Say($"cannot {word}: the file changed since ({ObjectEdits.Summarise(errors)})", true); return false; }
            EditsFile.Save(_host.EditsPath, root);
            SaveCount++;
            History.Moved(undo);
            AfterSave(sw);
            _host.Say($"{word}: {rec.Description}");
            return true;
        }
        catch (Exception e)
        {
            _host.Say($"cannot {word}: {e.Message}", true);
            return false;
        }
    }

    /// <summary>Everything that follows a write: remember the file state, refresh groups, let the watcher do objects instantly.</summary>
    void AfterSave(System.Diagnostics.Stopwatch sw)
    {
        Remember();
        _host.ReloadGroups();
        var instant0 = _host.Watcher?.InstantCount ?? 0;
        _host.Watcher?.NotifySaved();
        LastSaveInstant = (_host.Watcher?.InstantCount ?? 0) > instant0;
        LastSaveMs = sw.Elapsed.TotalMilliseconds;
        _host.MarkPanelDirty();
    }
}
