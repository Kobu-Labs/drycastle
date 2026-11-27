using Godot;
using System;
using System.Linq;

namespace Drycastle.Viewer.Editor;

/// <summary>The pages of the Select tool (what is selected) and the Group tool (mark parcels, group, merge, add, take out, dissolve; see <see cref="ParcelGroups"/>).</summary>
public sealed class ParcelPanel
{
    readonly IEditorHost _host;
    readonly ParcelGroups _groups;

    Label _selLabel = null!, _groupLabel = null!, _groupCount = null!;
    Button _btnCopy = null!, _btnGroup = null!, _btnMerge = null!, _btnAdd = null!, _btnRemove = null!, _btnDissolve = null!;

    public ParcelPanel(IEditorHost host, ParcelGroups groups) => (_host, _groups) = (host, groups);

    /// <summary>The Group button is enabled (Enter groups the marked parcels then).</summary>
    public bool CanGroup => !_btnGroup.Disabled;

    /// <summary>Texts shown in the panel (for the smoke test).</summary>
    public string SelectionText => _selLabel.Text;
    public string GroupText => _groupLabel.Text;

    public void BuildSelect(VBoxContainer page)
    {
        _selLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        page.AddChild(_selLabel);
        _btnCopy = Ui.FocusBtn("Copy parcel ID", CopySelection);
        _btnCopy.TooltipText = "Copy the group ID of the selection, or the parcel ID where a parcel is in no group (space-separated)";
        page.AddChild(_btnCopy);
    }

    void CopySelection()
    {
        var map = _host.Map;
        var ids = string.Join(" ", _host.Selection.Where(map.Parcels.ContainsKey).Select(id => map.Parcels[id].Group ?? id).Distinct());
        if (ids.Length == 0) return;
        DisplayServer.ClipboardSet(ids);
        Ui.FlashCopied(_btnCopy);
        _host.Say("copied: " + ids);
    }

    public void BuildGroup(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Marking parcels", ThemeTypeVariation = "Header" });
        page.AddChild(new Label
        {
            Text = "Click parcels in the 3D view to mark them (click a marked parcel again to unmark). Then group them: a group is one property with one fence around its outer edge.",
            ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _groupCount = new Label { ThemeTypeVariation = "Header" };
        page.AddChild(_groupCount);
        _groupLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        page.AddChild(_groupLabel);
        _btnGroup = Ui.Btn("Group the marked parcels  (Enter)", _groups.GroupSelected);
        _btnGroup.ThemeTypeVariation = "PrimaryButton";
        page.AddChild(_btnGroup);
        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Existing groups", ThemeTypeVariation = "Header" });
        _btnMerge = Ui.Btn("Merge marked groups and parcels into one group", _groups.MergeGroups);
        _btnMerge.TooltipText = "Mark parcels from two or more groups, or a group and loose parcels (also G). Everything goes into one new group; the fence they share is kept, else the first group's. Ctrl+Z undoes it.";
        _btnAdd = Ui.Btn("Add the marked parcels to the group", _groups.AddSelectedToGroup);
        _btnRemove = Ui.Btn("Take the marked parcels out of their group", _groups.RemoveSelectedFromGroup);
        _btnDissolve = Ui.Btn("Dissolve the group", _groups.Dissolve);
        foreach (var b in new[] { _btnMerge, _btnAdd, _btnRemove, _btnDissolve }) page.AddChild(b);
        page.AddChild(new Label { Text = "Clear the marks with Esc.", ThemeTypeVariation = "Dim" });
    }

    public void Refresh()
    {
        var map = _host.Map;
        var sel = _host.Selection.Where(map.Parcels.ContainsKey).Select(id => map.Parcels[id]).ToList();
        var wholeGroup = sel.Count > 0 && sel[0].Group is { } wg && sel.All(p => p.Group == wg)
            && sel.Count == map.Parcels.Values.Count(p => p.Group == wg) ? wg : null;
        if (sel.Count == 0) _selLabel.Text = "Nothing selected.\nClick a parcel; Shift+click adds.";
        else if (wholeGroup != null)
            _selLabel.Text = $"{wholeGroup}  (property group)\n{sel.Count} parcels\narea: {sel.Sum(p => p.AreaM2):F0} m2\nDissolve the group to edit its parts.";
        else if (sel.Count == 1)
        {
            var p = sel[0];
            _selLabel.Text = $"{p.Id}\nkind: {p.Kind}\narea: {p.AreaM2:F0} m2\ncadastral: {p.CadastralRef}\ngroup: {p.Group ?? "none"}";
        }
        else
        {
            var lines = sel.Take(12).Select(p => $"{p.Id}  {p.Kind}  {p.AreaM2:F0} m2" + (p.Group != null ? $"  [{p.Group}]" : ""));
            _selLabel.Text = $"{sel.Count} parcels selected\n" + string.Join("\n", lines) + (sel.Count > 12 ? $"\n... +{sel.Count - 12} more" : "");
        }
        _btnCopy.Visible = sel.Count > 0;
        var copyIds = sel.Select(p => p.Group ?? p.Id).Distinct().ToList();
        var copyText = copyIds.Count > 1 ? $"Copy {copyIds.Count} IDs" : sel.Count > 0 && sel[0].Group != null ? "Copy group ID" : "Copy parcel ID";
        if (Ui.Flashing(_btnCopy)) _btnCopy.SetMeta("flash_text", copyText); else _btnCopy.Text = copyText;
        var gids = sel.Select(p => p.Group).Where(g => g != null).Distinct().ToList();
        if (gids.Count == 0) _groupLabel.Text = "Selection is not in a group.";
        else
        {
            var lines = gids.Select(g => $"{g}: {string.Join(", ", map.Parcels.Values.Where(p => p.Group == g).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal))}");
            _groupLabel.Text = string.Join("\n", lines);
        }
        _groupCount.Text = sel.Count == 0 ? "Nothing marked yet" : $"{sel.Count} parcel{(sel.Count == 1 ? "" : "s")} marked";
        _btnGroup.Disabled = sel.Count < 2;
        _btnMerge.Disabled = !(gids.Count >= 2 || (gids.Count == 1 && sel.Any(p => p.Group == null)));
        _btnAdd.Disabled = gids.Count != 1 || sel.All(p => p.Group != null);
        _btnRemove.Disabled = gids.Count == 0;
        _btnDissolve.Disabled = gids.Count != 1;
    }
}
