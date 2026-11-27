using Drycastle.Viewer.Edits;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor;

/// <summary>One touched entry: kind is "parcels", "objects", "groups", "ground_patches", "zones", "zone_cuts", "cables" or "ground_types"; Before/After null means absent.</summary>
/// <remarks>
/// Key is null for the whole entry (objects by id, groups, ground patches, zones, cables). For a parcel it names the part that changed:
/// "fence", so undo leaves the parcel's other keys (and other writers' edits) alone.
/// </remarks>
public sealed record EntryChange(string Kind, string Id, Obj? Before, Obj? After, string? Key = null);

/// <summary>
/// A step that changed the sketch (the Sketch tool) carries its two states and no file changes: nothing is written. A step with <c>Replay</c> (the Photo to building tool: a
/// building's overrides and detail files, written through the building worker) is replayed by that function (true: undo, false: redo; it returns false when it could not start).
/// </summary>
public sealed record UndoRecord(string Description, List<EntryChange> Changes, SketchDoc? SketchBefore = null, SketchDoc? SketchAfter = null, Func<bool, bool>? Replay = null);

/// <summary>
/// Session undo/redo of every save the editor made since it opened (objects, group operations, Apply of fence). Each save records the before and after content of what it touched: `groups.&lt;ID&gt;`, `ground_patches.&lt;ID&gt;`, `zones.&lt;ID&gt;`, `zone_cuts.&lt;ID&gt;`, `cables.&lt;ID&gt;`
/// and each object (by id, in the top-level `objects` list) as whole entries, a parcel at key level (`parcels.&lt;ID&gt;.fence`), with "absent" as a value.
/// Undo (see <see cref="EditStore"/>) reloads the file from disk, writes the before-values back (absent deletes), validates and saves. Only the keys an operation touched
/// are overwritten; everything else in the file, including other keys of the same parcel changed by another writer, stays as it is. Redo mirrors that with the after-entries.
/// A new edit clears the redo stack. The stacks live in memory only and hold at most <see cref="Cap"/> operations. Sketch changes are steps on the same stacks.
/// </summary>
public sealed class UndoHistory
{
    public const int Cap = 200;

    readonly List<UndoRecord> _undo = new(), _redo = new();

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>A new edit: it goes on the undo stack and the redo stack is dropped.</summary>
    public void Push(UndoRecord rec)
    {
        _undo.Add(rec);
        if (_undo.Count > Cap) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>The record the next undo (or redo) would replay; null when the stack is empty.</summary>
    public UndoRecord? Peek(bool undo) => (undo ? _undo : _redo) is { Count: > 0 } s ? s[^1] : null;

    /// <summary>The replayed record moves to the other stack.</summary>
    public void Moved(bool undo)
    {
        var (from, to) = undo ? (_undo, _redo) : (_redo, _undo);
        var rec = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(rec);
        if (to.Count > Cap) to.RemoveAt(0);
    }

    /// <summary>The entries (parcels: the fence; every object by id) whose content differs between two documents.</summary>
    public static List<EntryChange> Diff(Obj before, Obj after)
    {
        var list = new List<EntryChange>();
        var ox = ObjectsById(before); var oy = ObjectsById(after);
        foreach (var oid in ox.Keys.Union(oy.Keys).OrderBy(k => k, StringComparer.Ordinal))
            Add(list, "objects", oid, null, ox.GetValueOrDefault(oid), oy.GetValueOrDefault(oid));
        foreach (var kind in new[] { "parcels", "groups", "ground_patches", "zones", "zone_cuts", "cables", "ground_types" })
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

    static Dictionary<string, Obj> ObjectsById(Obj root)
    {
        var d = new Dictionary<string, Obj>();
        foreach (var (_, o) in ObjectEdits.All(root)) if (o.GetValueOrDefault("id") is string s) d[s] = o;
        return d;
    }

    static void DiffParcel(List<EntryChange> list, string id, Obj? x, Obj? y)
    {
        foreach (var key in new[] { "fence" })
            Add(list, "parcels", id, key, x?.GetValueOrDefault(key) as Obj, y?.GetValueOrDefault(key) as Obj);
    }

    /// <summary>Write one recorded value (null = absent) into the document at its key.</summary>
    public static void Apply(Obj root, EntryChange c, Obj? target)
    {
        if (c.Kind == "objects") { ApplyObject(root, c.Id, target); return; }
        if (root.GetValueOrDefault(c.Kind) is not Obj map) root[c.Kind] = map = EditsFile.NewObject(); // ground_patches and zones may be absent
        if (c.Key == null)
        {
            if (target == null) map.Remove(c.Id); else map[c.Id] = ObjectEdits.CloneObj(target);
            return;
        }
        var entry = map.GetValueOrDefault(c.Id) as Obj;
        if (target == null) entry?.Remove(c.Key);
        else
        {
            if (entry == null) map[c.Id] = entry = EditsFile.NewObject();
            entry[c.Key] = ObjectEdits.CloneObj(target);
        }
        if (entry != null && entry.Count == 0) map.Remove(c.Id);
    }

    /// <summary>Write one object (null = absent) into the top-level list: replace the one with its id, append a new one, drop a removed one.</summary>
    static void ApplyObject(Obj root, string oid, Obj? target)
    {
        var list = ObjectEdits.Items(root);
        var idx = list == null ? -1 : list.FindIndex(o => o is Obj ob && ob.GetValueOrDefault("id") as string == oid);
        if (target == null)
        {
            if (idx >= 0) { list!.RemoveAt(idx); if (list.Count == 0) root.Remove("objects"); }
            return;
        }
        if (list == null) root["objects"] = list = new List<object?>();
        if (idx >= 0) list[idx] = ObjectEdits.CloneObj(target); else list.Add(ObjectEdits.CloneObj(target));
    }
}
