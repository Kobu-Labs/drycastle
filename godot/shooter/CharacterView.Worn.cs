using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Worn Items (issue #266, ADR 0016): what a character wears in each equipment slot (<see cref="EquipSlot"/>), drawn from the item's model
/// (<see cref="ItemDef.Model"/>, authored in the slot's frame: metres, facing -Z). A headgear Item rides the head bone in every stance and pose
/// (the clips, the crouch squash, the prone roll, the ragdoll flail all move the bone) and replaces the Camouflage's helmet: the helmet and the
/// hair are hidden, and the foliage and face paint of the head are left out, because the item covers the head (see <see cref="HeadCovered"/>).
/// The same call serves remote players, corpses, the vehicle seat view and the menu preview (<see cref="CamoPreview.ShowWorn"/>).
/// </summary>
public sealed partial class CharacterView
{
    /// <summary>
    /// The middle of the head in the bind pose (rig units, +Z is the face). The four bodies of camo.json share one rig and one head bone, so one point serves
    /// them all (measured on the models: the head spans x -0.49..0.49, y 2.07..3.13, z -0.49..0.49, the Operator's hood a little more).
    /// </summary>
    static readonly Vector3 HeadCentre = new(0f, 2.6f, 0f);

    readonly ItemDef?[] _worn = new ItemDef?[Equipment.SlotCount];
    readonly Node?[] _wornNodes = new Node?[Equipment.SlotCount];

    /// <summary>A headgear Item is worn: it covers the head, so the body's helmet and hair, the head's foliage and the face paint are not drawn.</summary>
    bool HeadCovered => _worn[(int)EquipSlot.Headgear] != null;

    /// <summary>The Item worn in a slot, or null.</summary>
    public ItemDef? WornIn(EquipSlot slot) => _worn[(int)slot];

    /// <summary>
    /// Puts <paramref name="item"/> on in <paramref name="slot"/> (null takes it off), replacing what was there; the same Item again changes nothing.
    /// An Item that belongs to another slot, or has no model, is worn without being drawn (the helmet is still hidden for headgear). Safe to call before
    /// the model has loaded and across <see cref="SetCamo"/> changing the body: the Item is kept and drawn on whichever body is current.
    /// </summary>
    public void SetWorn(EquipSlot slot, ItemDef? item)
    {
        if (item != null && item.Slot != slot) item = null;
        var i = (int)slot;
        if (_worn[i]?.Id == item?.Id) return;
        _worn[i] = item;
        BuildWorn(slot);
        if (slot == EquipSlot.Headgear) ReapplyCamo(); // the helmet, hair, foliage and face paint depend on it
    }

    /// <summary>Draws the Item of one slot on the current body, replacing the node drawn before.</summary>
    void BuildWorn(EquipSlot slot)
    {
        var i = (int)slot;
        if (_wornNodes[i] is { } old && GodotObject.IsInstanceValid(old)) old.QueueFree();
        _wornNodes[i] = null;
        if (_worn[i] is not { Model: not "" } item || _skeleton == null) return;
        if (slot != EquipSlot.Headgear) return; // the only slot there is
        var head = _skeleton.FindBone("Head");
        if (head < 0 || !_bind.TryGetValue(head, out var bind)) return;
        var model = ModelLib.Instance(item.Model);
        if (model == null) return; // headless, or the file is missing: worn but not drawn
        // the model is in metres facing -Z; the rig is in rig units facing +Z, so turn it round and scale it up, then express that in the head bone's frame
        // (the same way the eyes are placed in BuildEyes)
        var inBind = new Transform3D(Basis.FromEuler(new Vector3(0, Mathf.Pi, 0)).Scaled(Vector3.One / Scale), HeadCentre);
        var holder = new Node3D { Name = "Item", Transform = bind.AffineInverse() * inBind };
        holder.AddChild(model);
        var carrier = new BoneAttachment3D { Name = "Worn_Headgear", BoneName = "Head" };
        carrier.AddChild(holder);
        _skeleton.AddChild(carrier);
        _wornNodes[i] = carrier;
    }

    /// <summary>A new body (<see cref="Build"/>) has none of the old body's nodes: draws every worn Item on it.</summary>
    void RebuildWorn()
    {
        Array.Clear(_wornNodes);
        for (var i = 0; i < _worn.Length; i++) BuildWorn((EquipSlot)i);
    }
}
