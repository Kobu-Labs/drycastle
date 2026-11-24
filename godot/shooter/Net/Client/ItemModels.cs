using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Items;

namespace Drycastle.Shooter.Net.Client;

/// <summary>An item's own model, as the characters wear it and as it lies on the ground.</summary>
internal static class ItemModels
{
    /// <summary>An item's model: its own file, or for a model of the paid Synty packs (<see cref="ItemDef.Atlas"/>) the pack's model wearing that atlas; null when the file is not there.</summary>
    public static Node3D? Instance(ItemDef item)
    {
        const string synty = "synty/glb/";
        if (item.Atlas == "") return ModelLib.Instance(item.Model);
        return SyntyLib.Instance(item.Model[synty.Length..^".glb".Length], item.Atlas);
    }

    /// <summary>The box round every mesh under <paramref name="n"/> in the frame of the model's root (the root's own transform is left out when <paramref name="root"/>), or null when there is no mesh.</summary>
    public static Aabb? Bounds(Node3D n, Transform3D parent, bool root)
    {
        var here = root ? Transform3D.Identity : parent * n.Transform;
        Aabb? box = n is MeshInstance3D { Mesh: not null } mi ? here * mi.GetAabb() : null;
        foreach (var c in n.GetChildren())
        {
            if (c is not Node3D child || Bounds(child, here, false) is not { } b) continue;
            box = box is { } have ? have.Merge(b) : b;
        }
        return box;
    }
}
