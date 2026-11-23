using Godot;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>Where a vehicle stands, and what that means for its seats and its velocity: the pure arithmetic every vehicle feature asks, at the present pose or at a rewound one (ADR 0013, B-02).</summary>
internal static class CarPose
{
    /// <summary>The vehicle as it stands now: its feet, yaw and attitude, and the turret's yaw (the same numbers the hit box and the seats read).</summary>
    public static VehiclePose Present(ServerCar car)
    {
        var (_, pitch, roll) = Fleet.AttitudeOf(car);
        return new VehiclePose(car.HullFeet, car.State.Yaw, pitch, roll, car.TurretLocalYaw);
    }

    /// <summary>The distance between two points seen from above.</summary>
    public static float Flat(NVec3 a, NVec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Distance from a point to a vehicle's hull where it stands now (its upright box, or its type's tilted boxes).</summary>
    public static float HullDistanceNow(ServerCar car, NVec3 at)
    {
        var (yaw, pitch, roll) = Fleet.AttitudeOf(car);
        return HullDistance.Of(at, car.HullFeet, yaw, pitch, roll, car.Def);
    }

    /// <summary>World position of a seat of the car (its offset in the car's frame). A flak truck's gunner sits on the turret, so the seat turns with it.</summary>
    public static Vector3 SeatPos(ServerCar car, int seat) => SeatPosAt(car, seat, Present(car));

    /// <summary>
    /// A seat's world position with the car at pose <paramref name="pose"/> (the rewound pose of a shot, B-02, or the present one): the offset in the
    /// car's frame turned by the yaw, and by the attitude for a helicopter or a driven vehicle.
    /// </summary>
    public static Vector3 SeatPosAt(ServerCar car, int seat, VehiclePose pose)
    {
        var offset = car.Def.Seats[seat].Offset;
        if (car.Def.Flak is { } flak && car.Def.Seats[seat].Role == "gunner") offset = FlakRules.TurretSeatOffset(flak, offset, pose.TurretYaw);
        var p = car.Flies || car.Drives // a helicopter's seats tilt with it; a driven vehicle's lie on the slope it stands on
            ? pose.Feet + FlightMath.Rotate(new NVec3(offset[0], offset[1], offset[2]), pose.Yaw, pose.Pitch, pose.Roll)
            : VehicleSeat.SeatPosition(pose.Feet, pose.Yaw, offset);
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>A vehicle's velocity: what a corpse leaving it carries on (a helicopter's flight velocity, a car's, zero for one asleep).</summary>
    public static NVec3 Velocity(ServerCar car) => car.Flies ? car.Flight.Velocity : car.Asleep ? NVec3.Zero : new NVec3(car.Velocity.X, car.Velocity.Y, car.Velocity.Z);
}
