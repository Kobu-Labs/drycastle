using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// Session undo/redo of every save the editor made since it opened (objects, group operations, Apply of fence). Each save records the before and after content of what it touched: `groups.&lt;ID&gt;`, `regions.&lt;ID&gt;` and `cables.&lt;ID&gt;`
/// as whole entries, a parcel at key level (`parcels.&lt;ID&gt;.fence` and each object by id), with "absent" as a value.
/// Undo reloads the file from disk, writes the before-values back (absent deletes), validates and saves; the watcher
/// (or the instant objects path) applies the effect. Only the keys this operation touched are overwritten; everything
/// else in the file, including other keys of the same parcel changed by another writer, stays as it is. Redo mirrors that with the after-entries. A new edit clears the redo stack. The
/// stacks live in memory only and hold at most <see cref="UndoCap"/> operations. Sketch changes are steps on the same stacks.
/// </summary>
public partial class EditorMode
{
    public const int UndoCap = 200;

    /// <summary>One touched entry: kind is "parcels", "groups", "regions" or "cables"; Before/After null means absent.</summary>
    /// <summary>
    /// Key is null for the whole entry (groups, regions, cables). For a parcel it names the part that changed: "fence",
    /// or "objects/&lt;object id&gt;", so undo leaves the parcel's other keys (and other writers' edits) alone.
    /// </summary>
    public sealed record EntryChange(string Kind, string Id, Obj? Before, Obj? After, string? Key = null);
    /// <summary>A step that changed the sketch (EditorMode.Sketch.cs) carries its two states and no file changes: nothing is written.</summary>
    public sealed record UndoRecord(string Description, List<EntryChange> Changes, SketchDoc? SketchBefore = null, SketchDoc? SketchAfter = null);

    readonly List<UndoRecord> _undo = new(), _redo = new();

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    static Obj? Entry(Obj root, string kind, string id) => ((Obj)root[kind]!).GetValueOrDefault(id) as Obj;

    /// <summary>The entries (parcels: fence and objects by id) whose content differs between two documents.</summary>
    static List<EntryChange> DiffEntries(Obj before, Obj after)
    {
        var list = new List<EntryChange>();
        foreach (var kind in new[] { "parcels", "groups", "regions", "cables" })
        {
            var a = before.GetValueOrDefault(kind) as Obj ?? EditsFile.NewObject(); var b = after.GetValueOrDefault(kind) as Obj ?? EditsFile.NewObject();
            foreach (var id in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                var x = a.GetValueOrDefault(id) as Obj; var y = b.GetValueOrDefault(id) as Obj;
                if (kind == "parcels") { DiffParcel(list, id, x, y); continue; }
                Add(list, kind, id, null, x, y);
            }
        }
        return list;
    }

    static void Add(List<EntryChange> list, string kind, string id, string? key, Obj? x, Obj? y)
    {
        var tx = x == null ? null : EditsFile.Dump(x); var ty = y == null ? null : EditsFile.Dump(y);
        if (tx == ty) return;
        list.Add(new EntryChange(kind, id, x == null ? null : ObjectEdits.CloneObj(x), y == null ? null : ObjectEdits.CloneObj(y), key));
    }

    static Dictionary<string, Obj> ObjectsById(Obj? parcel)
    {
        var d = new Dictionary<string, Obj>();
        if (parcel?.GetValueOrDefault("objects") is List<object?> l)
            foreach (var o in l) if (o is Obj ob && ob.GetValueOrDefault("id") is string s) d[s] = ob;
        return d;
    }

    static void DiffParcel(List<EntryChange> list, string id, Obj? x, Obj? y)
    {
        foreach (var key in new[] { "fence" })
            Add(list, "parcels", id, key, x?.GetValueOrDefault(key) as Obj, y?.GetValueOrDefault(key) as Obj);
        var ox = ObjectsById(x); var oy = ObjectsById(y);
        foreach (var oid in ox.Keys.Union(oy.Keys).OrderBy(k => k, StringComparer.Ordinal))
            Add(list, "parcels", id, "objects/" + oid, ox.GetValueOrDefault(oid), oy.GetValueOrDefault(oid));
    }

    /// <summary>Write one recorded value (null = absent) into the document at its key.</summary>
    static void ApplyChange(Obj root, EntryChange c, Obj? target)
    {
        if (root.GetValueOrDefault(c.Kind) is not Obj map) root[c.Kind] = map = EditsFile.NewObject(); // regions may be absent
        if (c.Key == null)
        {
            if (target == null) map.Remove(c.Id); else map[c.Id] = ObjectEdits.CloneObj(target);
            return;
        }
        var entry = map.GetValueOrDefault(c.Id) as Obj;
        if (c.Key.StartsWith("objects/", StringComparison.Ordinal))
        {
            var oid = c.Key["objects/".Length..];
            var objs = entry?.GetValueOrDefault("objects") as List<object?>;
            var idx = objs == null ? -1 : objs.FindIndex(o => o is Obj ob && ob.GetValueOrDefault("id") as string == oid);
            if (target == null)
            {
                if (idx >= 0) objs!.RemoveAt(idx);
            }
            else
            {
                if (entry == null) map[c.Id] = entry = EditsFile.NewObject();
                if (objs == null) entry["objects"] = objs = new List<object?>();
                if (idx >= 0) objs[idx] = ObjectEdits.CloneObj(target); else objs.Add(ObjectEdits.CloneObj(target));
            }
            if (entry != null && objs != null && objs.Count == 0) entry.Remove("objects");
        }
        else if (target == null) entry?.Remove(c.Key);
        else
        {
            if (entry == null) map[c.Id] = entry = EditsFile.NewObject();
            entry[c.Key] = ObjectEdits.CloneObj(target);
        }
        if (entry != null && entry.Count == 0) map.Remove(c.Id);
    }

    void PushUndo(UndoRecord rec)
    {
        _undo.Add(rec);
        if (_undo.Count > UndoCap) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo() => Step(_undo, _redo, true);
    public bool Redo() => Step(_redo, _undo, false);

    bool Step(List<UndoRecord> from, List<UndoRecord> to, bool undo)
    {
        var word = undo ? "undo" : "redo";
        if (from.Count == 0) { Say($"nothing to {word}"); return false; }
        var rec = from[^1];
        if (rec.SketchBefore != null && rec.SketchAfter != null)
        {
            from.RemoveAt(from.Count - 1);
            to.Add(rec);
            if (to.Count > UndoCap) to.RemoveAt(0);
            _skDraft.Clear();
            ApplySketch(undo ? rec.SketchBefore : rec.SketchAfter);
            Say($"{word}: {rec.Description}");
            return true;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var root = EditsFile.Load(EditsPath);
            foreach (var c in rec.Changes)
            {
                ApplyChange(root, c, undo ? c.Before : c.After);
            }
            var errors = ObjectEdits.ValidateDocument(root);
            if (errors.Count > 0) { Say($"cannot {word}: the file changed since ({ObjectEdits.Summarise(errors)})", true); return false; }
            EditsFile.Save(EditsPath, root);
            SaveCount++;
            from.RemoveAt(from.Count - 1);
            to.Add(rec);
            if (to.Count > UndoCap) to.RemoveAt(0);
            AfterSave(sw);
            Say($"{word}: {rec.Description}");
            return true;
        }
        catch (Exception e)
        {
            Say($"cannot {word}: {e.Message}", true);
            return false;
        }
    }
}
