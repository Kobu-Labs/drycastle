using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The dish target on the server (docs/events/thieves/06): the dishes are the placed objects of the map with the generated dish model (<see cref="DishSource.AssetId"/>), found once from the
/// loader's object nodes (a map with none has no dish targets). The set of dishes taken is the source's; every change goes to everyone as <see cref="EventKind.DishTaken"/> (the whole set), and a
/// joiner gets it when it is not empty (<see cref="SendDishesOnJoin"/>). A round reset empties it (the registry's reset), which broadcasts the empty set.
/// </summary>
public partial class NetServer
{
    DishSource? _dishes;

    /// <summary>The dishes of the map as the pure source sees them: object nodes, the ground and the navigation mesh.</summary>
    sealed class ServerDishes : IDishWorld
    {
        readonly NetServer _s;
        List<DishSite>? _sites;
        public ServerDishes(NetServer server) => _s = server;

        public IReadOnlyList<DishSite> Sites => _sites ?? Find();

        // cached once the map is there and its nodes are in the tree; until then (no map yet) the list is empty and asked again
        List<DishSite> Find()
        {
            var list = new List<DishSite>();
            if (_s.Map is not { } map) return list;
            var ready = true;
            foreach (var (id, node) in map.ObjectNodes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (map.ObjectAsset(id) != DishSource.AssetId || !GodotObject.IsInstanceValid(node)) continue;
                if (!node.IsInsideTree()) { ready = false; continue; }
                var x = node.GlobalTransform.Basis.X; // the turn from the axis, as MapLoader.FirstObjectOfAsset does
                list.Add(new DishSite(id, N(node.GlobalPosition), Mathf.RadToDeg(MathF.Atan2(-x.Z, x.X))));
            }
            if (ready) _sites = list;
            return list;
        }

        public float GroundAt(float x, float z) => _s.Map == null ? float.NaN : _s.Map.GroundHeight(x, z);

        public bool TrySnap(NVec3 p, out NVec3 snapped)
        {
            snapped = default;
            if (_s._nav == null) return false;
            snapped = N(_s._nav.Snap(new Vector3(p.X, p.Y, p.Z)));
            return true;
        }
    }

    /// <summary>The dish source (made and registered by <see cref="InitLoot"/>): its changes are sent to everyone.</summary>
    DishSource MakeDishSource()
    {
        _dishes = new DishSource(ThievesData.Loot.Dish, new ServerDishes(this));
        _dishes.TakenChanged += () => BroadcastEvent(EventKind.DishTaken, DishCodec.Encode(_dishes.TakenObjectIds));
        return _dishes;
    }

    /// <summary>Join: the dishes taken so far, when there are any (an empty set is what a client starts with).</summary>
    void SendDishesOnJoin(Client c)
    {
        if (_dishes is { TakenCount: > 0 } d) Send(c, new EventMsg((byte)EventKind.DishTaken, DishCodec.Encode(d.TakenObjectIds)));
    }
}
