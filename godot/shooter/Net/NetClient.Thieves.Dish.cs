using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The satellite dishes taken by the Thieves on the client (docs/events/thieves/06): the server sends the whole set of taken dishes (<see cref="EventKind.DishTaken"/>, object ids) when it changes and to
/// a joiner; the client hides the object node of each and shows every other dish of the map. A dish carried or lying is drawn by <see cref="LootProp"/>.
/// </summary>
public partial class NetClient
{
    /// <summary>The object ids of the dishes taken, as the server last said.</summary>
    readonly HashSet<string> _dishesTaken = new();

    void OnDishEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.DishTaken || DishCodec.Decode(payload) is not { } ids) return;
        _dishesTaken.Clear();
        foreach (var id in ids) _dishesTaken.Add(id);
        ApplyDishes();
    }

    /// <summary>Hides the taken dishes' nodes and shows the others (every placed object of the dish asset).</summary>
    void ApplyDishes()
    {
        if (Map == null) return;
        foreach (var (id, node) in Map.ObjectNodes)
        {
            if (Map.ObjectAsset(id) != DishSource.AssetId || !GodotObject.IsInstanceValid(node)) continue;
            node.Visible = !_dishesTaken.Contains(id);
        }
    }
}
