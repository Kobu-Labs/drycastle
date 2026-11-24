using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// Mass delete of zones: zones are marked (the tick of a list row, Ctrl+click on the map, X for the selected one, "Mark all") and
/// outlined in the danger colour; "Delete N marked zones" (Shift+Del) asks, then removes them all in one save = one undo step
/// (<see cref="Delete"/>). Marks are not saved and live until deleted, unmarked or the editor closes.
/// </summary>
public sealed class ZoneMarks
{
    public const string Outline = "outline_zone_marked";

    readonly IEditorHost _host;
    readonly ZonesTool _tool;
    readonly HashSet<string> _marked = new();
    int _version;
    object? _outlineFor;

    public ZoneMarks(IEditorHost host, ZonesTool tool)
    {
        _host = host;
        _tool = tool;
    }

    public int Count => _marked.Count;
    public IReadOnlySet<string> Ids => _marked;
    public bool Contains(string id) => _marked.Contains(id);

    /// <summary>The zones "Mark all" marks: those of the selected kind, or every zone when none is (not capped as the list is).</summary>
    public List<ZoneInfo> Listed() => _tool.SelectedKind is { } k ? _tool.List().Where(z => z.Kind == k).ToList() : _tool.List();

    /// <summary>Drop the marks of zones that no longer exist.</summary>
    internal void Prune()
    {
        if (_marked.RemoveWhere(id => _tool.Find(id) == null) > 0) _version++;
    }

    /// <summary>Mark or unmark zones for the mass delete. Nothing is saved.</summary>
    public void Mark(IEnumerable<string> ids, bool on, bool say = true)
    {
        _tool.List();
        var n = 0;
        foreach (var id in ids) if (on ? _tool.Find(id) != null && _marked.Add(id) : _marked.Remove(id)) n++;
        if (n == 0) return;
        _version++; _host.MarkPanelDirty();
        if (say) _host.Say($"{n} zone{(n == 1 ? "" : "s")} {(on ? "marked for deletion" : "unmarked")}: {_marked.Count} marked{(_marked.Count > 0 ? " (Shift+Del deletes them)" : "")}");
    }

    public bool Toggle(string id)
    {
        if (_tool.Find(id) is not { } z) { _host.Say($"zone {id} does not exist", true); return false; }
        var on = !_marked.Contains(id);
        Mark(new[] { id }, on, false);
        _host.Say($"{z.Label} {(on ? "marked for deletion" : "unmarked")}: {_marked.Count} marked{(_marked.Count > 0 ? " (Shift+Del deletes them)" : "")}");
        return true;
    }

    public void MarkListed() => Mark(Listed().Select(z => z.Id).ToList(), true);

    public void Clear() => Mark(_marked.ToList(), false);

    /// <summary>Shift+Del, the button: ask first, naming how many zones of which kinds go; accepting deletes them (one undo brings all back).</summary>
    public bool RequestDelete()
    {
        var zones = Marked();
        if (zones.Count == 0) { _host.Say("no zone is marked: tick zones in the list or Ctrl+click them on the map", true); return false; }
        var ids = zones.Select(z => z.Id).ToList();
        var kinds = string.Join(", ", zones.GroupBy(z => z.Kind).OrderBy(g => _tool.KindOrder(g.Key)).Select(g => $"{g.Count()} {_tool.KindLabel(g.Key)}"));
        var names = string.Join(", ", zones.OrderBy(z => z.Label, StringComparer.Ordinal).Take(8).Select(z => z.Label)) + (zones.Count > 8 ? $" and {zones.Count - 8} more" : "");
        var s = zones.Count == 1 ? "" : "s";
        _host.Confirm.Show($"Delete {zones.Count} marked zone{s}?", $"{kinds} ({zones.Sum(z => z.AreaM2):F0} m2 in all): {names}. They are removed; the ground is not changed. One undo (Ctrl+Z) brings them all back.",
            $"Delete {zones.Count} zone{s}", () => Delete(ids));
        return true;
    }

    List<ZoneInfo> Marked() => ExistingZones(_marked);

    List<ZoneInfo> ExistingZones(IEnumerable<string> ids)
    {
        _tool.List();
        return ids.Select(id => _tool.Find(id)).Where(z => z != null).Select(z => z!).ToList();
    }

    /// <summary>
    /// Delete several zones as one save and one undo step: a drawn one leaves edits.json `zones`, one that comes from a rule gets a
    /// cut (as <see cref="ZonesTool.EditRuleZone"/>). A rule zone whose outline cannot be cut is kept and named. The deleted ones lose their mark.
    /// </summary>
    public bool Delete(IReadOnlyCollection<string> ids)
    {
        var zones = ExistingZones(ids.Distinct());
        var kept = zones.Where(z => z.FromRule && ObjectEdits.RingProblemCode(ObjectEdits.Despiked(z.Points)) != ObjectEdits.RingOk).ToList();
        var todo = zones.Except(kept).ToList();
        if (todo.Count == 0) { _host.Say(kept.Count > 0 ? $"nothing deleted: the outline of {kept[0].Label}{(kept.Count > 1 ? $" and {kept.Count - 1} more" : "")} cannot be cut" : "nothing to delete: those zones no longer exist", true); return false; }
        var ok = _host.Edits.Mutate(root =>
        {
            var drawn = ShapeDoc.ShapesOf(root, ShapeKind.Zone);
            var n = 0;
            foreach (var z in todo)
            {
                if (!z.FromRule) { if (drawn != null && drawn.Remove(z.Id)) n++; continue; }
                ZonesTool.AddCut(root, z);
                n++;
            }
            if (drawn is { Count: 0 }) root.Remove(ShapeDoc.KeyOf(ShapeKind.Zone));
            if (n == 0) return (false, "nothing deleted: those zones no longer exist");
            return (true, $"deleted {n} zone{(n == 1 ? "" : "s")} (Ctrl+Z undoes)" + (kept.Count > 0 ? $"; {kept.Count} kept, their outline cannot be cut" : ""));
        });
        if (!ok) return false;
        Mark(todo.Select(z => z.Id).ToList(), false, false);
        if (_host.Shapes.SelectedZoneId != null && todo.Any(z => z.Id == _host.Shapes.SelectedZoneId)) _host.Shapes.SelectShape(ShapeKind.Zone, null);
        return true;
    }

    /// <summary>The outline of every marked zone in the danger colour, above the overlay; rebuilt when the marks or the file change.</summary>
    internal void UpdateOutline()
    {
        if (_host.Tool != EditorTool.Zones || _marked.Count == 0 || _host.Map.OriginEpsg5514 == null)
        {
            if (_outlineFor != null) { _outlineFor = null; _host.Outlines.Set(Outline, new(), EditorTheme.Danger, 0, 0); }
            return;
        }
        var key = (_host.Edits.Disk(), _version);
        if (_outlineFor is ValueTuple<Obj, int> was && ReferenceEquals(was.Item1, key.Item1) && was.Item2 == key.Item2) return;
        _outlineFor = key;
        var segs = new List<(Vector2, Vector2)>();
        foreach (var z in Marked()) segs.AddRange(_host.Picker.RingSegs(z.Points, true));
        _host.Outlines.Set(Outline, segs, EditorTheme.Danger, AreaSurfaces.Lift + 0.3f, 0.9f);
        if (_host.Outlines.Node(Outline)?.MaterialOverride is StandardMaterial3D m) { m.NoDepthTest = true; m.RenderPriority = 9; } // seen through obstacles, over the zone fill
    }
}
