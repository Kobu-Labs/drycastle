using Drycastle.MapLoader.Core;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// Property groups (a set of parcels with one fence round its outer edge): who is in which group (read from the edits file on disk),
/// the outer perimeter of a group, and the operations on the selection (group, merge, add, take out, dissolve) as edits.json writes.
/// </summary>
public sealed class ParcelGroups
{
    readonly IEditorHost _host;
    // members of each property group and its outer perimeter; both are rebuilt in Reload only
    readonly Dictionary<string, List<ParcelInfo>> _members = new();
    readonly Dictionary<string, List<(Vector2, Vector2)>> _perimeters = new();

    public ParcelGroups(IEditorHost host) => _host = host;

    /// <summary>The property group of a parcel (null: none).</summary>
    public string? GroupOf(string parcel) => _host.Map.Parcels.TryGetValue(parcel, out var p) ? p.Group : null;

    public IReadOnlyList<ParcelInfo> Members(string group) => _members.TryGetValue(group, out var l) ? l : Array.Empty<ParcelInfo>();

    /// <summary>Outer perimeter of a property group, computed once until the groups are reloaded.</summary>
    public List<(Vector2, Vector2)> Perimeter(string group)
    {
        if (!_perimeters.TryGetValue(group, out var segs)) _perimeters[group] = segs = ParcelOutline.GroupPerimeter(Members(group));
        return segs;
    }

    /// <summary>The parcel itself, or all members of its property group (a group is shown and picked as one shape).</summary>
    public List<string> Unit(string parcelId)
    {
        var g = GroupOf(parcelId);
        if (g == null) return new List<string> { parcelId };
        return Members(g).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Read the groups again from the file on disk (after every save, and when another writer changed it); fills ParcelInfo.Group.</summary>
    public void Reload()
    {
        Dictionary<string, string> member;
        try { member = EditsFile.MembershipOf(_host.Edits.Reload()); }
        catch (Exception) { return; } // half-written or invalid: keep what we have
        var map = _host.Map;
        foreach (var p in map.Parcels.Values) p.Group = member.GetValueOrDefault(p.Id);
        _members.Clear();
        _perimeters.Clear();
        foreach (var p in map.Parcels.Values)
            if (p.Group is { } g)
            {
                if (!_members.TryGetValue(g, out var l)) _members[g] = l = new();
                l.Add(p);
            }
        _host.Outlines.MarkAll();
        _host.MarkPanelDirty();
    }

    // ---- operations on the selection ----

    public void GroupSelected()
    {
        var selection = _host.Selection;
        if (selection.Count < 2) { _host.Say("select 2 or more parcels to group (Shift+click adds)", true); return; }
        var groups = selection.Select(GroupOf).Where(g => g != null).Distinct().ToList();
        if (groups.Count == 1 && selection.All(p => GroupOf(p) == groups[0]))
        {
            _host.Say($"already one group: {groups[0]}", true);
            return;
        }
        if (groups.Count >= 1) { MergeGroups(); return; } // marked parcels touch a group: grouping them makes one new group of everything
        var sel = selection.ToList();
        _host.Edits.Mutate(root =>
        {
            var (ok, msg, gid) = EditsFile.CreateGroup(root, sel);
            return (ok, ok ? $"created {gid}" : msg);
        });
    }

    /// <summary>Merge the groups of the marked parcels, and marked ungrouped parcels, into one new group. Undoable.</summary>
    public void MergeGroups()
    {
        var selection = _host.Selection;
        var gids = selection.Select(GroupOf).Where(g => g != null).Select(g => g!).Distinct().ToList();
        var extra = selection.Where(p => GroupOf(p) == null).ToList();
        if (gids.Count < 2 && !(gids.Count == 1 && extra.Count > 0)) { _host.Say("mark parcels from 2 or more groups, or a group and a loose parcel, to merge", true); return; }
        _host.Edits.Mutate(root =>
        {
            var (ok, msg, _) = EditsFile.MergeGroups(root, gids, extra);
            return (ok, msg);
        });
    }

    public void AddSelectedToGroup()
    {
        var selection = _host.Selection;
        var groups = selection.Select(GroupOf).Where(g => g != null).Distinct().ToList();
        if (groups.Count != 1) { _host.Say(groups.Count == 0 ? "select a grouped parcel too, to say which group" : "selection spans several groups", true); return; }
        var add = selection.Where(p => GroupOf(p) == null).ToList();
        if (add.Count == 0) { _host.Say("nothing to add: every selected parcel is already in a group", true); return; }
        var gid = groups[0]!;
        _host.Edits.Mutate(root =>
        {
            var (ok, msg) = EditsFile.AddToGroup(root, gid, add);
            return (ok, msg);
        });
    }

    public void RemoveSelectedFromGroup()
    {
        var sel = _host.Selection.Where(p => GroupOf(p) != null).ToList();
        if (sel.Count == 0) { _host.Say("none of the selected parcels is in a group", true); return; }
        _host.Edits.Mutate(root => EditsFile.RemoveFromGroups(root, sel));
    }

    public void Dissolve()
    {
        var groups = _host.Selection.Select(GroupOf).Where(g => g != null).Distinct().ToList();
        if (groups.Count != 1) { _host.Say(groups.Count == 0 ? "no group in the selection" : "selection spans several groups", true); return; }
        var gid = groups[0]!;
        _host.Edits.Mutate(root => EditsFile.Dissolve(root, gid));
    }
}
