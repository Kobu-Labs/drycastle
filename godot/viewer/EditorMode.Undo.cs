using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// Session undo/redo of every save the editor made since it opened (objects, group operations, Apply of fence and
/// trees). Each save records the before and after content of the top-level entries it touched: `parcels.&lt;ID&gt;` and
/// `groups.&lt;ID&gt;`, with "absent" as a value. Undo reloads the file from disk, writes the before-entries back
/// (absent deletes the entry), validates and saves; the watcher (or the instant objects path) applies the effect.
/// Entries somebody else changed in between are overwritten only when this operation touched them; everything else
/// in the file stays as it is. Redo mirrors that with the after-entries. A new edit clears the redo stack. The
/// stacks live in memory only and hold at most <see cref="UndoCap"/> operations.
/// </summary>
public partial class EditorMode
{
    public const int UndoCap = 200;

    /// <summary>One touched entry: kind is "parcels" or "groups"; Before/After null means absent.</summary>
    public sealed record EntryChange(string Kind, string Id, Obj? Before, Obj? After);
    public sealed record UndoRecord(string Description, List<EntryChange> Changes);

    readonly List<UndoRecord> _undo = new(), _redo = new();

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    static Obj? Entry(Obj root, string kind, string id) => ((Obj)root[kind]!).GetValueOrDefault(id) as Obj;

    /// <summary>The parcel and group entries whose content differs between two documents.</summary>
    static List<EntryChange> DiffEntries(Obj before, Obj after)
    {
        var list = new List<EntryChange>();
        foreach (var kind in new[] { "parcels", "groups" })
        {
            var a = (Obj)before[kind]!; var b = (Obj)after[kind]!;
            foreach (var id in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                var x = a.GetValueOrDefault(id) as Obj; var y = b.GetValueOrDefault(id) as Obj;
                var tx = x == null ? null : EditsFile.Dump(x); var ty = y == null ? null : EditsFile.Dump(y);
                if (tx != ty) list.Add(new EntryChange(kind, id, x == null ? null : ObjectEdits.CloneObj(x), y == null ? null : ObjectEdits.CloneObj(y)));
            }
        }
        return list;
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var root = EditsFile.Load(EditsPath);
            foreach (var c in rec.Changes)
            {
                var target = undo ? c.Before : c.After;
                var map = (Obj)root[c.Kind]!;
                if (target == null) map.Remove(c.Id); else map[c.Id] = ObjectEdits.CloneObj(target);
            }
            var errors = ObjectEdits.ValidateDocument(root);
            if (errors.Count > 0) { Say($"cannot {word}: the file changed since ({errors[0]})", true); return false; }
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
