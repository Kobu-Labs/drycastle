using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>The hulls of the vehicles that stand parked, for what walks and drives past them (<see cref="WorldQuery.Ahead"/>).</summary>
internal sealed class ParkedHulls
{
    /// <summary>A vehicle slower than this stands parked: a walker goes round it (a moving one runs the walker over).</summary>
    const float ParkedSpeed = 0.5f;

    readonly Fleet _fleet;
    readonly Func<ServerCar, VehiclePose> _poseNow;

    /// <param name="poseNow">Where a vehicle stands now (its hull, yaw and attitude).</param>
    public ParkedHulls(Fleet fleet, Func<ServerCar, VehiclePose> poseNow) { _fleet = fleet; _poseNow = poseNow; }

    /// <summary>The nearest hull of a vehicle that stands parked along a ray (its hull: the upright box, or the boxes of its type's hull list); infinity when none.</summary>
    public float Distance(Vector3 from, Vector3 dir, float max)
    {
        var best = float.PositiveInfinity;
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Thrown != null || (car.Flies && !car.Flight.Grounded) || MathF.Abs(car.State.Speed) > ParkedSpeed) continue;
            var p = car.GlobalPosition;
            var reach = max + car.Def.BroadReach;
            if (MathF.Abs(p.X - from.X) > reach || MathF.Abs(p.Z - from.Z) > reach) continue;
            var pose = _poseNow(car);
            if (VehicleBox.Ray(new NVec3(from.X, from.Y, from.Z), new NVec3(dir.X, dir.Y, dir.Z), max, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, car.Def) is { } hit && hit.Distance < best) best = hit.Distance;
        }
        return best;
    }
}
