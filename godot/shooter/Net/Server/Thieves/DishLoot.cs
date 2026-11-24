using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The dish target on the server (docs/events/thieves/06): the dishes are the placed objects of the map with the generated dish model (<see cref="DishSource.AssetId"/>), found once from the
/// loader's object nodes (a map with none has no dish targets). The set of dishes taken is the source's; every change goes to everyone as <see cref="EventKind.DishTaken"/> (the whole set), and a
/// joiner gets it when it is not empty (<see cref="SendOnJoin"/>). A round reset empties it (the registry's reset), which broadcasts the empty set. This class is the source's world: object nodes, the ground and the mesh.
/// </summary>
internal sealed class DishLoot : IDishWorld
{
    readonly ServerContext _ctx;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<BotNav?> _nav;
    List<DishSite>? _sites;

    public DishLoot(ServerContext ctx, DishLootConfig cfg, Drycastle.MapLoader.MapLoader? map, Func<BotNav?> nav)
    {
        _ctx = ctx; _map = map; _nav = nav;
        Source = new DishSource(cfg, this);
        Source.TakenChanged += () => ctx.Broadcast(EventKind.DishTaken, DishCodec.Encode(Source.TakenObjectIds));
    }

    public DishSource Source { get; }

    /// <summary>Join: the dishes taken so far, when there are any (an empty set is what a client starts with).</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (Source is { TakenCount: > 0 } d) _ctx.SendEvent(c, EventKind.DishTaken, DishCodec.Encode(d.TakenObjectIds));
    }

    public IReadOnlyList<DishSite> Sites => _sites ?? Find();

    // cached once the map is there and its nodes are in the tree; until then (no map yet) the list is empty and asked again
    List<DishSite> Find()
    {
        var list = new List<DishSite>();
        if (_map is not { } map) return list;
        var ready = true;
        foreach (var (id, node) in map.ObjectNodes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (map.ObjectAsset(id) != DishSource.AssetId || !GodotObject.IsInstanceValid(node)) continue;
            if (!node.IsInsideTree()) { ready = false; continue; }
            var x = node.GlobalTransform.Basis.X; // the turn from the axis, as MapLoader.FirstObjectOfAsset does
            list.Add(new DishSite(id, node.GlobalPosition.ToNumerics(), Mathf.RadToDeg(MathF.Atan2(-x.Z, x.X))));
        }
        if (ready) _sites = list;
        return list;
    }

    public float GroundAt(float x, float z) => _map == null ? float.NaN : _map.GroundHeight(x, z);

    public bool TrySnap(NVec3 p, out NVec3 snapped)
    {
        snapped = default;
        if (_nav() is not { } nav) return false;
        snapped = nav.Snap(new Vector3(p.X, p.Y, p.Z)).ToNumerics();
        return true;
    }
}
