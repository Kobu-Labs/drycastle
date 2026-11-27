using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// A vehicle's cargo volume on the server (vehicles.json cargo_volume, the Cartel van's bay, #495): whether a point is in it where the vehicle stands now, and how many loads it takes.
/// The seam a loader (the Duffels of a Heist) plugs into: it moves its load and asks <see cref="Contains"/>. The loader counts what went in (one count, the Heist's bay): the vehicle keeps
/// none, so a reset or a fall out of the world cannot empty a bay whose loads are still in it. Which vehicle may take whose load is the loader's rule.
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

    /// <summary>How many loads <paramref name="car"/>'s bay takes (0 for a type without one).</summary>
    public static int Capacity(ServerCar car) => car.Def.CargoVolume?.Capacity ?? 0;
}
