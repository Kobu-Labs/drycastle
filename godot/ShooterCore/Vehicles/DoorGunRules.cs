using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where a door gun points: its mount's yaw and its pitch against the vehicle (radians, inside the arc), and that direction in the world.</summary>
public readonly record struct DoorGunAim(float Yaw, float Pitch, Vector3 Direction)
{
    /// <summary>The direction as a view yaw and pitch (Godot's sense), for a camera that looks along the gun.</summary>
    public float ViewYaw => MathF.Atan2(-Direction.X, -Direction.Z);
    public float ViewPitch => MathF.Asin(Math.Clamp(Direction.Y, -1f, 1f));
}

/// <summary>
/// The rules of a pintle-mounted gun (<see cref="VehicleDoorGunDef"/>, the utility helicopter's door gun), pure so the server, the gunner's
/// client and the tests do the same arithmetic. The gunner's view is what the gun is asked to point at; the gun points there as far as its
/// arc allows. The arc is in the vehicle's own frame, so it pitches and rolls with the airframe, and it is what keeps the rounds off the
/// vehicle's own hull, tail and rotor. The rounds themselves are the vehicle gun's (<see cref="VehicleGun"/>): hitscan from the muzzle.
/// </summary>
public static class DoorGunRules
{
    const float Deg2Rad = MathF.PI / 180f;

    /// <summary>
    /// The aim for a view (<paramref name="viewYaw"/>, <paramref name="viewPitch"/>, world) from a vehicle with the given attitude (a land
    /// vehicle passes pitch and roll 0): the view's direction in the vehicle's frame, as a yaw from the gun's rest direction and a pitch, each
    /// held inside the arc, and that clamped direction back in the world.
    /// </summary>
    public static DoorGunAim Aim(VehicleDoorGunDef def, float carYaw, float carPitch, float carRoll, float viewYaw, float viewPitch)
    {
        var local = FlightMath.Unrotate(HitMath.Forward(viewYaw, viewPitch), carYaw, carPitch, carRoll);
        var m = FlightMath.Rotate(local, -def.OutwardYawDeg * Deg2Rad, 0, 0);   // in the mount's frame: -Z is the rest direction
        var yaw = MathF.Atan2(-m.X, -m.Z);
        var pitch = MathF.Asin(Math.Clamp(m.Y, -1f, 1f));
        return FromLocal(def, carYaw, carPitch, carRoll, yaw, pitch);
    }

    /// <summary>The aim for a mount yaw and a pitch (held inside the arc): what every client poses the model with, and the world direction it points.</summary>
    public static DoorGunAim FromLocal(VehicleDoorGunDef def, float carYaw, float carPitch, float carRoll, float yaw, float pitch)
    {
        var arc = def.YawArcDeg * Deg2Rad;
        yaw = Math.Clamp(FlightMath.Wrap(yaw), -arc, arc);
        pitch = Math.Clamp(pitch, MinPitch(def, yaw), def.MaxPitchDeg * Deg2Rad);
        var inVehicle = FlightMath.Rotate(HitMath.Forward(yaw, pitch), def.OutwardYawDeg * Deg2Rad, 0, 0);
        return new DoorGunAim(yaw, pitch, Vector3.Normalize(FlightMath.Rotate(inVehicle, carYaw, carPitch, carRoll)));
    }

    /// <summary>
    /// The lowest the gun goes at a mount yaw (radians): <see cref="VehicleDoorGunDef.MinPitchDeg"/>, raised inside the guard (the riders in front
    /// of a ring gun) and coming down to it again over the blend, so the barrel rides up over them as it swings past and never drops into them.
    /// </summary>
    public static float MinPitch(VehicleDoorGunDef def, float yaw)
    {
        var low = def.MinPitchDeg * Deg2Rad;
        if (def.GuardArcDeg <= 0) return low;
        // the mount's yaw is from its rest direction; the guard is given in the vehicle's frame
        var off = MathF.Abs(FlightMath.Wrap(yaw + (def.OutwardYawDeg - def.GuardYawDeg) * Deg2Rad)) / Deg2Rad;
        var guard = def.GuardMinPitchDeg * Deg2Rad;
        if (off <= def.GuardArcDeg) return guard;
        if (def.GuardBlendDeg <= 0 || off >= def.GuardArcDeg + def.GuardBlendDeg) return low;
        return guard + (low - guard) * (off - def.GuardArcDeg) / def.GuardBlendDeg;
    }

    /// <summary>The mount's yaw axis at the height of the gun, in the world: the pintle's top, or the centre of a ring.</summary>
    public static Vector3 Pivot(VehicleDoorGunDef def, Vector3 carPos, float carYaw, float carPitch, float carRoll) =>
        carPos + FlightMath.Rotate(new Vector3(def.Pivot[0], def.Pivot[1], def.Pivot[2]), carYaw, carPitch, carRoll);

    /// <summary>
    /// The gun's own pivot in the world, where it pitches and the barrel starts from. On a pintle that is <see cref="Pivot"/>; on a ring
    /// (<see cref="VehicleDoorGunDef.RingRadius"/>) it rides the ring, out from the centre on the side the gun points.
    /// </summary>
    public static Vector3 GunPivot(VehicleDoorGunDef def, Vector3 carPos, float carYaw, float carPitch, float carRoll, in DoorGunAim aim)
    {
        var centre = new Vector3(def.Pivot[0], def.Pivot[1], def.Pivot[2]);
        if (def.RingRadius > 0) centre += FlightMath.Rotate(new Vector3(0, 0, -def.RingRadius), aim.Yaw + def.OutwardYawDeg * Deg2Rad, 0, 0);
        return carPos + FlightMath.Rotate(centre, carYaw, carPitch, carRoll);
    }

    /// <summary>The muzzle: the gun's pivot plus the barrel along the aim. The rounds leave here.</summary>
    public static Vector3 Muzzle(VehicleDoorGunDef def, Vector3 pivot, in DoorGunAim aim) => pivot + aim.Direction * def.MuzzleLength;

    /// <summary>The gunner's eye: <see cref="VehicleDoorGunDef.Eye"/> in the gun's frame (the gun turned by the aim, on the tilted vehicle), from the pivot.</summary>
    public static Vector3 Eye(VehicleDoorGunDef def, Vector3 pivot, float carYaw, float carPitch, float carRoll, in DoorGunAim aim)
    {
        var e = new Vector3(def.Eye[0], def.Eye[1], def.Eye[2]);
        var inMount = FlightMath.Rotate(e, aim.Yaw, aim.Pitch, 0);                                        // the gun's pitch, then the mount's yaw
        var inVehicle = FlightMath.Rotate(inMount, def.OutwardYawDeg * Deg2Rad, 0, 0);
        return pivot + FlightMath.Rotate(inVehicle, carYaw, carPitch, carRoll);
    }
}
