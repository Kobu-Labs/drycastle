using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Fence tool (Editor 6): the target (a parcel, several parcels or a property group), the pending fence draft per
/// target, and Apply. The gates are <see cref="FenceGates"/>, the sidebar page <see cref="FencePanel"/>. Every control
/// calls one of the public methods below, so the headless smoke drives the same code without mouse events.
/// </summary>
public sealed class FenceTool : EditorPart
{
    EditTarget? _target;
    bool _scopeGroup = true;
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public FenceGates Gates { get; }
    public FencePanel Panel { get; }

    public FenceTool(IEditorHost host) : base(host)
    {
        Gates = new FenceGates(host, this);
        Panel = new FencePanel(host, this);
    }

    // ---- the parts' hooks ----

    /// <summary>A gate being dragged follows the mouse.</summary>
    public override void Update(double delta) => Gates.UpdateDrag();

    public void BuildHeader(VBoxContainer outer) => Panel.BuildHeader(outer);
    public void BuildPage(VBoxContainer page) => Panel.BuildPage(page);

    public void BuildFooter(VBoxContainer outer)
    {
        Panel.BuildFooter(outer);
        Gates.BuildWorld();
    }

    /// <summary>Bring the target, every control and the gate markers in line with the selection (called when the panel is dirty).</summary>
    public void RefreshPanel()
    {
        UpdateTarget();
        Panel.Refresh();
        Gates.RebuildMarkers();
        Host.MarkAllOutlines();
    }

    // ---- target and pending state ----

    public EditTarget? Target => _target;
    public int PendingCount => _pending.Count;
    public bool TargetDirty => _target != null && _pending.ContainsKey(_target.Key);
    public bool GroupScope => _scopeGroup;
    /// <summary>The first few targets with unsaved changes, for the footer and the discard question.</summary>
    public string PendingNames => string.Join(", ", _pending.Keys.Take(4));

    /// <summary>The target for the current selection (see the panel header), or null when nothing is selected.</summary>
    EditTarget? ComputeTarget()
    {
        var sel = Host.Selection.Where(Host.Map.Parcels.ContainsKey).ToList();
        if (sel.Count == 0) return null;
        var groups = sel.Select(Host.Group).Distinct().ToList();
        if (groups.Count == 1 && groups[0] != null)
        {
            var gid = groups[0]!;
            var members = Host.Map.Parcels.Values.Where(p => p.Group == gid).Select(p => p.Id).ToList();
            if (members.Count == sel.Count || (sel.Count == 1 && _scopeGroup)) return EditTarget.ForGroup(gid, members);
        }
        return EditTarget.ForParcels(sel);
    }

    void UpdateTarget()
    {
        var t = ComputeTarget();
        if (t?.Key != _target?.Key)
        {
            if (Gates.Armed) Gates.Cancel();
            Gates.Deselect();
        }
        _target = t;
    }

    Pending PendingFor()
    {
        if (!_pending.TryGetValue(_target!.Key, out var p)) _pending[_target.Key] = p = new Pending { Target = _target };
        return p;
    }

    /// <summary>The fence as the panel shows it: the pending draft, else what is on disk.</summary>
    public FenceDraft Current
        => _target == null ? new FenceDraft()
            : _pending.GetValueOrDefault(_target.Key)?.Fence ?? EditModel.FenceFromDisk(Host.Edits.Disk(), _target);

    internal FenceDraft? InheritedGroupFence()
        => _target == null || _target.IsGroup ? null : EditModel.GroupFenceOf(Host.Edits.Disk(), _target.Parcels[0]);

    void AfterEdit()
    {
        Host.MarkPanelDirty();
        Host.MarkAllOutlines();
    }

    internal bool Edit(Action<FenceDraft> change, bool needCustom = false)
    {
        if (_target == null) { Host.Say("select a parcel first", true); return false; }
        var p = PendingFor();
        var d = p.Fence ??= EditModel.FenceFromDisk(Host.Edits.Disk(), _target);
        if (needCustom && d.EffectiveMode != "custom") { d.SetMode("custom"); Host.Say("fence set to custom so your change shows"); }
        change(d);
        AfterEdit();
        return true;
    }

    // ---- fence edits ----

    /// <summary>Remove the fence entry: the rules decide again (a group member inherits the group's fence).</summary>
    public bool Reset()
    {
        Gates.Deselect();
        return Edit(d => d.Reset());
    }

    public bool SetMode(string mode)
    {
        if (!EditSchema.FenceModes.Contains(mode)) { Host.Say($"unknown fence mode {mode}", true); return false; }
        Gates.Deselect();
        return Edit(d => d.SetMode(mode));
    }

    /// <summary>Set the type (null = default); only in mode custom.</summary>
    public bool SetType(string? type)
    {
        if (type != null && !EditSchema.FenceTypeNames.Contains(type)) { Host.Say($"unknown fence type {type}", true); return false; }
        return Edit(d => d.Type = type, true);
    }

    public bool SetHeight(double? metres) => Edit(d => d.Height = metres is { } v ? Math.Max(0.05, v) : null, true);
    public bool SetThickness(double? metres) => Edit(d => d.Thickness = metres is { } v ? Math.Max(0.01, v) : null, true);

    public bool SetColour(string? colour)
    {
        if (colour != null && !EditSchema.ColourRx.IsMatch(colour)) { Host.Say($"colour must look like #rrggbb, got {colour}", true); return false; }
        return Edit(d => d.Colour = colour?.ToLowerInvariant(), true);
    }

    public void SetGroupScope(bool group)
    {
        _scopeGroup = group;
        Host.MarkPanelDirty();
    }

    // ---- apply and revert ----

    /// <summary>Write all pending targets in one atomic save (the file is reloaded first); true when saved.</summary>
    public bool Apply()
    {
        if (_pending.Count == 0) { Host.Say("nothing to apply"); return false; }
        var items = _pending.Values.ToList();
        var ok = Host.Edits.Mutate(root =>
        {
            var errors = EditModel.Apply(root, items);
            return errors.Count > 0 ? (false, "not saved: " + ObjectEdits.Summarise(errors)) : (true, items.Count == 1 ? items[0].Target.Header : $"{items.Count} targets");
        }, "apply " + (items.Count == 1 ? items[0].Target.Header : $"{items.Count} targets"));
        if (!ok) return false;
        Discard();
        return true;
    }

    /// <summary>Drop every pending change without a word (the caller has asked the user, or Apply has just written them).</summary>
    public void Discard()
    {
        _pending.Clear();
        AfterEdit();
    }

    /// <summary>Discard the pending changes of the current target.</summary>
    public void Revert()
    {
        if (_target != null && _pending.Remove(_target.Key)) { Host.Say($"reverted {_target.Header}"); AfterEdit(); }
    }

    public void RevertAll()
    {
        if (_pending.Count == 0) return;
        _pending.Clear();
        Host.Say("reverted all unsaved changes");
        AfterEdit();
    }
}
