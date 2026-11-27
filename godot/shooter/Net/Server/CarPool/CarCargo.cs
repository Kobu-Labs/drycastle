using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// A vehicle's cargo volume on the server (vehicles.json cargo_volume, #495): where its centre is now and how many loads it takes. What loads into it is the cargo server's (<see cref="CargoServer"/>:
/// the <see cref="Drycastle.ShooterCore.Cartel.VehicleCargo"/> of every cargo vehicle, which counts its loads).
/// </summary>
internal static class CarCargo
{
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
