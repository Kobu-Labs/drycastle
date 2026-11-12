using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Where an armed seat's rider aims: the view held inside the seat's arc, as a world yaw and pitch (Godot's sense) and a direction.</summary>
public readonly record struct SeatAim(float ViewYaw, float ViewPitch, Vector3 Direction);

/// <summary>
/// The rules of an armed seat (<see cref="VehicleSeatDef.Armed"/>, ADR 0008), pure so the server, the rider's client and the tests do the same
/// arithmetic. The rider fights with their own loadout from the seat: the view is the aim, held inside the seat's arc
/// (<see cref="VehicleSeatAimDef"/>) in the vehicle's own frame, so the arc turns and tilts with the vehicle; the shots leave the seated eye; and
/// the vehicle's speed widens the spread as the rider's own speed does on foot.
/// </summary>
public static class SeatAimRules
{
    const float Deg2Rad = MathF.PI / 180f;

    /// <summary>
    /// The aim for a view (<paramref name="viewYaw"/>, <paramref name="viewPitch"/>, world) from a seat of a vehicle with the given attitude: the
    /// view's direction in the vehicle's frame, its yaw held within the arc and its pitch within the limits, and that direction back in the
    /// world. A seat without an arc (<paramref name="aim"/> null) aims anywhere.
    /// </summary>
    public static SeatAim Aim(VehicleSeatAimDef? aim, float carYaw, float carPitch, float carRoll, float viewYaw, float viewPitch)
    {
        var world = HitMath.Forward(viewYaw, viewPitch);
        if (aim == null) return new SeatAim(viewYaw, viewPitch, world);
        var local = FlightMath.Unrotate(world, carYaw, carPitch, carRoll);
        var yaw = MathF.Atan2(-local.X, -local.Z);
        var pitch = MathF.Asin(Math.Clamp(local.Y, -1f, 1f));
        var centre = aim.YawDeg * Deg2Rad; var arc = aim.ArcDeg * Deg2Rad;
        var held = centre + Math.Clamp(FlightMath.Wrap(yaw - centre), -arc, arc);
        var heldPitch = Math.Clamp(pitch, aim.MinPitchDeg * Deg2Rad, aim.MaxPitchDeg * Deg2Rad);
        // inside the arc the view is returned as it came, bit for bit: only an aim that had to be held is rebuilt
        if (MathF.Abs(FlightMath.Wrap(held - yaw)) < 1e-6f && MathF.Abs(heldPitch - pitch) < 1e-6f) return new SeatAim(viewYaw, viewPitch, world);
        var d = Vector3.Normalize(FlightMath.Rotate(HitMath.Forward(held, heldPitch), carYaw, carPitch, carRoll));
        return new SeatAim(MathF.Atan2(-d.X, -d.Z), MathF.Asin(Math.Clamp(d.Y, -1f, 1f)), d);
    }

    /// <summary>The eye of a seat's rider above the seat (the rider's feet): a seated rider's is the crouch's, a standing one's the stand's.</summary>
    public static float EyeHeight(VehicleSeatDef seat) => MovementStep.EyeHeight(seat.RiderStance);

    /// <summary>
    /// The spread half-angle of a shot from an armed seat (<see cref="HitMath.ShotSpread(float, WeaponDef, Stance, bool, float, bool)"/> for a rider):
    /// the weapon's own spread, plus its movement spread in proportion to the vehicle's speed (all of it at the seat's
    /// <see cref="VehicleSeatAimDef.SpreadFullSpeed"/>, none at a standstill), then the aiming multiplier. A seat gives no crouch bonus: the rider
    /// shoots as steadily as standing still. The server and the rider's crosshair both call this.
    /// </summary>
    public static float ShotSpread(float degrees, WeaponDef w, bool aiming, float vehicleSpeed, VehicleSeatDef seat)
    {
        var full = seat.Aim?.SpreadFullSpeed ?? new VehicleSeatAimDef().SpreadFullSpeed;
        var asOnFoot = MoveConsts.SprintSpeed * Math.Clamp(MathF.Abs(vehicleSpeed) / full, 0f, 1f);
        return HitMath.ShotSpread(degrees, w, Stance.Stand, aiming, asOnFoot, onFloor: true);
    }
}
