using Godot;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>What a navmesh walker meets on its way (<see cref="WalkerGround"/>); here the parked vehicles it is asked about.</summary>
public partial class NetServer
{
    /// <summary>A vehicle slower than this stands parked: a walker goes round it (a moving one runs the walker over).</summary>
    const float ParkedSpeed = 0.5f;

    WalkerGround? _walkerGround;

    /// <summary>The geometry the walkers ask, shared by every citizen and member.</summary>
    IWalkerGround WalkerWorld => _walkerGround ??= new WalkerGround(Map, () => Query);

    /// <summary>The nearest hull of a vehicle that stands parked along a ray (its hull: the upright box, or the boxes of its type's hull list); infinity when none: <see cref="WorldQuery.Ahead"/> asks it.</summary>
    float ParkedHullDistance(Vector3 from, Vector3 dir, float max)
    {
        var best = float.PositiveInfinity;
        foreach (var car in _fleet.Cars)
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
