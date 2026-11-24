using Drycastle.Shooter.Net.Bots;
using Drycastle.ShooterCore.Colliders;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Terrain;

/// <summary>
/// What a navmesh walker meets on its way (ADR 0028, the Walker row of <see cref="CollisionRules"/>: citizens, the Gang, a Raid's crew; bots are bodies and the physics stops them). The mesh holds terrain, buildings,
/// fences and placed solids, with the doors open and no trunks (<see cref="BotNav"/>); a trunk, a door that is closed now and a parked vehicle are asked here, at walk time, through <see cref="WorldQuery.Ahead"/>.
/// <para>
/// Cost: a walker asks one ray (<see cref="WalkGuard"/>) per <see cref="WalkGuard.LookAheadM"/> metres of straight run, so at a walk of 1.4 m/s about one ray every two seconds; a hundred walkers on the
/// move are about 1.5 rays a tick, 15 to 20 microseconds. The parked-vehicle test is analytic, over the vehicles, and only when the ray's start is near one.
/// </para>
/// </summary>
internal sealed class WalkerGround : IWalkerGround
{
    /// <summary>How high above the feet the walker's ray runs, metres: over a kerb or a stone, under a branch, at the height of a trunk, a door and a vehicle's box.</summary>
    public const float RayHeightM = 0.6f;

    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<WorldQuery> _query;

    public WalkerGround(Drycastle.MapLoader.MapLoader? map, Func<WorldQuery> query) { _map = map; _query = query; }

    public float ClearAhead(NVec3 from, NVec3 dir, float metres) =>
        GroundHug.Ray(_map, from, dir, metres, RayHeightM, out var a, out var b) ? _query().Ahead(MoverKind.Walker, a, b) : float.PositiveInfinity;

    public float GroundY(float x, float z, float fallback)
    {
        if (_map == null) return fallback;
        var y = _map.GroundHeight(x, z);
        return float.IsNaN(y) ? fallback : y;
    }
}
