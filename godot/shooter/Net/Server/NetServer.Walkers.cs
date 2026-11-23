using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// What a navmesh walker meets on its way (ADR 0028, the Walker row of <see cref="CollisionRules"/>: citizens, the Gang, a Raid's crew; bots are bodies and the physics stops them). The mesh holds terrain, buildings,
/// fences and placed solids, with the doors open and no trunks (<see cref="BotNav"/>); a trunk, a door that is closed now and a parked vehicle are asked here, at walk time, through <see cref="WorldQuery.Ahead"/>.
/// <para>
/// Cost: a walker asks one ray (<see cref="WalkGuard"/>) per <see cref="WalkGuard.LookAheadM"/> metres of straight run, so at a walk of 1.4 m/s about one ray every two seconds; a hundred walkers on the
/// move are about 1.5 rays a tick, 15 to 20 microseconds. The parked-vehicle test is analytic, over the vehicles, and only when the ray's start is near one.
/// </para>
/// </summary>
public partial class NetServer
{
    /// <summary>How high above the feet the walker's ray runs, metres: over a kerb or a stone, under a branch, at the height of a trunk, a door and a vehicle's box.</summary>
    const float WalkRayHeightM = 0.6f;

    /// <summary>A vehicle slower than this stands parked: a walker goes round it (a moving one runs the walker over).</summary>
    const float ParkedSpeed = 0.5f;

    WalkerGround? _walkerGround;

    /// <summary>The geometry the walkers ask, shared by every citizen and member.</summary>
    IWalkerGround WalkerWorld => _walkerGround ??= new WalkerGround(this);

    sealed class WalkerGround : IWalkerGround
    {
        readonly NetServer _s;
        public WalkerGround(NetServer server) => _s = server;

        public float ClearAhead(NVec3 from, NVec3 dir, float metres) =>
            _s.GroundHugRay(from, dir, metres, WalkRayHeightM, out var a, out var b) ? _s.Query.Ahead(MoverKind.Walker, a, b) : float.PositiveInfinity;

        public float GroundY(float x, float z, float fallback)
        {
            if (_s.Map == null) return fallback;
            var y = _s.Map.GroundHeight(x, z);
            return float.IsNaN(y) ? fallback : y;
        }
    }

    /// <summary>
    /// The ends of one ray at <paramref name="height"/> above the ground, along the ground: it runs from the ground height under the start to the ground height under the end, raised where the heightmap
    /// between bulges above that line, so a rise or a hollow is never mistaken for a wall. False without a map (nothing to follow: no ray).
    /// </summary>
    bool GroundHugRay(NVec3 from, NVec3 dir, float maxM, float height, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        if (Map == null) return false;
        float G(float t) { var y = Map.GroundHeight(from.X + dir.X * t, from.Z + dir.Z * t); return float.IsNaN(y) ? from.Y : y; }
        var (y0, y1) = (from.Y, G(maxM));
        var raise = 0f;
        for (var k = 1; k < 4; k++) raise = MathF.Max(raise, G(maxM * k / 4) - (y0 + (y1 - y0) * k / 4));
        a = new Vector3(from.X, y0 + height + raise, from.Z);
        b = new Vector3(from.X + dir.X * maxM, y1 + height + raise, from.Z + dir.Z * maxM);
        return true;
    }

    /// <summary>The nearest hull of a vehicle that stands parked along a ray (its hull: the upright box, or the boxes of its type's hull list); infinity when none: <see cref="WorldQuery.Ahead"/> asks it.</summary>
    float ParkedHullDistance(Vector3 from, Vector3 dir, float max)
    {
        var best = float.PositiveInfinity;
        foreach (var car in _cars)
        {
            if (car.Destroyed || car.Thrown != null || (car.Flies && !car.Flight.Grounded) || MathF.Abs(car.State.Speed) > ParkedSpeed) continue;
            var p = car.GlobalPosition;
            var reach = max + car.Def.BroadReach;
            if (MathF.Abs(p.X - from.X) > reach || MathF.Abs(p.Z - from.Z) > reach) continue;
            var pose = PresentPose(car);
            if (VehicleBox.Ray(N(from), N(dir), max, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, car.Def) is { } hit && hit.Distance < best) best = hit.Distance;
        }
        return best;
    }
}
