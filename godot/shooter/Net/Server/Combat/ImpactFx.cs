using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// Impact particles on the server (EventKind.ImpactFx, tickets #152 and #154). The server decides the surface of a hit (body,
/// vehicle, water, road, building, wood, foliage, else ground) and the burst size, so every client draws the same thing.
/// The surface comes from the map: the road polygons of roads.json (<see cref="RoadSurfaces"/>) and the parcel a point is in,
/// with its height above the ground (<see cref="ImpactGroups"/>). Only events call in here; nothing runs per tick.
/// </summary>
internal sealed class ImpactFx
{
    /// <summary>Minimum gap between the ground puffs of plain bullets, server seconds (keeps the impact traffic low).</summary>
    const double BulletPuffGap = 0.2;

    readonly ServerContext _ctx;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Water _water;
    RoadSurfaces? _roads;
    TreeColliders.Index? _trees;
    double _nextBulletPuff;

    public ImpactFx(ServerContext ctx, Drycastle.MapLoader.MapLoader? map, Water water)
    {
        _ctx = ctx; _map = map; _water = water;
    }

    RoadSurfaces Roads => _roads ??= _map != null && _map.RoadsJson.Length > 0 ? RoadSurfaces.Parse(_map.RoadsJson) : RoadSurfaces.Empty;

    /// <summary>The map's trees in a grid (T3-003); built with <see cref="Warm"/>, or on the first lookup.</summary>
    TreeColliders.Index Trees => _trees ??= new TreeColliders.Index(_map!.Trees, _map.TreeModel);

    /// <summary>
    /// Builds the road polygons and the tree grid at load (T3-003, T3-004), so the first impact of a match does not parse
    /// roads.json or index the trees on a tick.
    /// </summary>
    public void Warm()
    {
        if (_map == null) return;
        _ = Roads;
        _ = Trees;
    }

    /// <summary>The collider group of a point that is not a body or a vehicle (see <see cref="ImpactGroups"/>); null for the terrain.</summary>
    string? SurfaceGroup(NVec3 at)
    {
        var onRoad = Roads.Contains(at.X, -at.Z); // ground z is -north, map y is north
        if (_map == null) return ImpactGroups.For(onRoad, null, 0f);
        if (!onRoad && Trees.GroupAt(new NVec3(at.X, -at.Z, at.Y)) is { } tree) return tree; // trunk: wood, crown: foliage
        var parcel = _map.ParcelIndex?.At(at.X, at.Z);
        var above = at.Y - _map.GroundHeightOrMean(at.X, at.Z);
        return ImpactGroups.For(onRoad, parcel?.Kind, above);
    }

    /// <summary>An impact's surface and size, classified here (water wins over the map groups). Used alone or in a tick's gunship batch.</summary>
    public ImpactInfo At(NVec3 at, ImpactSize size, bool body = false, bool vehicle = false)
    {
        var surface = SurfaceClassifier.Classify(body, vehicle, _water.DepthAt(at) > 0, body || vehicle ? null : SurfaceGroup(at));
        return new ImpactInfo(at, surface, size);
    }

    /// <summary>Sends one impact to everyone (rockets, the missile, the helpers and the bullets' puffs).</summary>
    public void Broadcast(NVec3 at, ImpactSize size, bool body = false, bool vehicle = false) =>
        _ctx.Broadcast(EventKind.ImpactFx, ImpactCodec.Encode(At(at, size, body, vehicle)));

    /// <summary>A plain bullet that hit the ground or a wall (not a body, not a car): a small puff, at most one per <see cref="BulletPuffGap"/>.</summary>
    public void BulletPuff(NVec3 at)
    {
        if (_ctx.Now < _nextBulletPuff) return;
        _nextBulletPuff = _ctx.Now + BulletPuffGap;
        Broadcast(at, ImpactSize.Puff);
    }
}
