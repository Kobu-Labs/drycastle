using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Impact particles on the server (EventKind.ImpactFx, tickets #152 and #154). The server decides the surface of a hit (body,
/// vehicle, water, road, building, wood, foliage, else ground) and the burst size, so every client draws the same thing.
/// The surface comes from the map: the road polygons of roads.json (<see cref="RoadSurfaces"/>) and the parcel a point is in,
/// with its height above the ground (<see cref="ImpactGroups"/>). Only events call in here; nothing runs per tick.
/// </summary>
public partial class NetServer
{
    /// <summary>The map this server runs on (the parcels, the ground height and roads.json); null in tests.</summary>
    public Drycastle.MapLoader.MapLoader? Map;

    /// <summary>Minimum gap between the ground puffs of plain bullets, server seconds (keeps the impact traffic low).</summary>
    const double BulletPuffGap = 0.2;

    RoadSurfaces? _roads;
    TreeColliders.Index? _mapTrees;
    double _nextBulletPuff;

    RoadSurfaces Roads => _roads ??= Map != null && Map.RoadsJson.Length > 0 ? RoadSurfaces.Parse(Map.RoadsJson) : RoadSurfaces.Empty;

    /// <summary>The map's trees in a grid (T3-003); built with <see cref="WarmSurfaceIndexes"/>, or on the first lookup.</summary>
    TreeColliders.Index MapTrees => _mapTrees ??= new TreeColliders.Index(Map!.Trees, Map.TreeModel);

    /// <summary>
    /// Builds the road polygons and the tree grid at load (T3-003, T3-004), so the first impact of a match does not parse
    /// roads.json or index the trees on a tick.
    /// </summary>
    void WarmSurfaceIndexes()
    {
        if (Map == null) return;
        _ = Roads;
        _ = MapTrees;
    }

    /// <summary>The collider group of a point that is not a body or a vehicle (see <see cref="ImpactGroups"/>); null for the terrain.</summary>
    string? SurfaceGroup(NVec3 at)
    {
        var onRoad = Roads.Contains(at.X, -at.Z); // ground z is -north, map y is north
        if (Map == null) return ImpactGroups.For(onRoad, null, 0f);
        if (!onRoad && MapTrees.GroupAt(new NVec3(at.X, -at.Z, at.Y)) is { } tree) return tree; // trunk: wood, crown: foliage
        var parcel = Map.ParcelIndex?.At(at.X, at.Z);
        var above = at.Y - Map.GroundHeightOrMean(at.X, at.Z);
        return ImpactGroups.For(onRoad, parcel?.Kind, above);
    }

    /// <summary>An impact's surface and size, classified here (water wins over the map groups). Used alone or in a tick's gunship batch.</summary>
    ImpactInfo ImpactAt(NVec3 at, ImpactSize size, bool body = false, bool vehicle = false)
    {
        var surface = SurfaceClassifier.Classify(body, vehicle, Water.DepthAt(at) > 0, body || vehicle ? null : SurfaceGroup(at));
        return new ImpactInfo(at, surface, size);
    }

    /// <summary>Sends one impact to everyone (rockets, the missile, the helpers and the bullets' puffs).</summary>
    void BroadcastImpact(NVec3 at, ImpactSize size, bool body = false, bool vehicle = false) =>
        BroadcastEvent(EventKind.ImpactFx, ImpactCodec.Encode(ImpactAt(at, size, body, vehicle)));

    /// <summary>A plain bullet that hit the ground or a wall (not a body, not a car): a small puff, at most one per <see cref="BulletPuffGap"/>.</summary>
    void BulletPuff(NVec3 at)
    {
        if (_elapsed < _nextBulletPuff) return;
        _nextBulletPuff = _elapsed + BulletPuffGap;
        BroadcastImpact(at, ImpactSize.Puff);
    }
}
