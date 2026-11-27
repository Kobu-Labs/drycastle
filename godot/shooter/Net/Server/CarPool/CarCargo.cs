using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// A vehicle's cargo volume on the server (vehicles.json cargo_volume, the Cartel van's bay, #495): whether a point is in it where the vehicle stands now, and the loads it holds
/// (<see cref="ServerCar.Hold"/>). The seam a loader (the Duffels of a Heist) plugs into: it moves its load, asks <see cref="Contains"/>, and on true <see cref="TryLoad"/>s it.
/// Which vehicle may take whose load is the loader's rule.
/// </summary>
internal static class CarCargo
{
    /// <summary>
    /// Whether the world point <paramref name="point"/> is inside <paramref name="car"/>'s cargo volume where it stands now (its yaw and its slope), grown by <paramref name="margin"/> metres
    /// on every side. False for a type without one, a destroyed or retired vehicle.
    /// </summary>
    public static bool Contains(ServerCar car, NVec3 point, float margin = 0)
    {
        if (car.Def.CargoVolume is not { } v || car.Destroyed) return false;
        var pose = CarPose.Present(car);
        return CargoVolume.Contains(v, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, point, margin);
    }

    /// <summary>The centre of <paramref name="car"/>'s cargo volume in the world now (a throw's aim, a marker), or null for a type without one.</summary>
    public static NVec3? Centre(ServerCar car)
    {
        if (car.Def.CargoVolume is not { } v) return null;
        var pose = CarPose.Present(car);
        return CargoVolume.Centre(v, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll);
    }

    /// <summary>One load goes into <paramref name="car"/>'s bay. False when it has none, is destroyed or full.</summary>
    public static bool TryLoad(ServerCar car) => !car.Destroyed && car.Hold is { } hold && hold.TryAdd();

    /// <summary>One load comes out of <paramref name="car"/>'s bay (taken back out). False when it is empty or has none.</summary>
    public static bool TryUnload(ServerCar car) => car.Hold is { } hold && hold.TryRemove();

    /// <summary>The loads in <paramref name="car"/>'s bay now (0 for a type without one).</summary>
    public static int Count(ServerCar car) => car.Hold?.Count ?? 0;

    /// <summary>How many loads <paramref name="car"/>'s bay takes (0 for a type without one).</summary>
    public static int Capacity(ServerCar car) => car.Hold?.Capacity ?? 0;
}
