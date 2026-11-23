using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The splash rule of ADR 0028 (the Splash row of <see cref="CollisionRules"/>): a blast hurts a target only along a clear line of sight, and a
/// rider in a covered seat takes half of it through the vehicle's hull. Asked by every blast that splashes players (rockets and flak shells, the
/// attack missile, the gunship, a vehicle blowing up), once for each target the radius reaches, so only after the damage came out above zero.
/// </summary>
public partial class NetServer
{
    /// <summary>The line of sight starts this far above the blast, so a blast that sits on the ground does not clip the slope it stands on.</summary>
    const float SplashRayLiftM = 0.1f;

    /// <summary>Share of the splash a covered seat's rider takes through the hull; an exposed seat takes all of it.</summary>
    const float CoveredSeatSplash = 0.5f;

    /// <summary>
    /// The share of a blast at <paramref name="blast"/> that reaches <paramref name="t"/>: 0 when a bunker room's walls (<see cref="MapShelters"/>) or
    /// any wall a shot stops on (<see cref="WorldQuery.Wall"/>: terrain, building, fence, placed solid, trunk, crown, bush, closed door) is between them,
    /// <see cref="CoveredSeatSplash"/> for a rider of a covered seat when the blast is outside their vehicle's hull, 1 otherwise. A vehicle's hull does not
    /// shield anyone else. One ray per target.
    /// </summary>
    float SplashShare(NVec3 blast, HitTarget t)
    {
        var centre = HitMath.Centre(t);
        if (!MapShelters.Reaches(blast, centre)) return 0f;
        var from = new Vector3(blast.X, blast.Y + SplashRayLiftM, blast.Z);
        var d = new Vector3(centre.X, centre.Y, centre.Z) - from;
        var len = d.Length();
        if (len < 0.05f) return 1f;
        var dir = d / len;
        if (Query.Wall(from, dir, len) < len - 0.05f) return 0f;
        if (!_byId.TryGetValue(t.Id, out var rider) || rider.Car is not { Destroyed: false } car || car.Def.SeatExposed(rider.CarSeat)) return 1f;
        var pose = PoseOf(car);
        var into = VehicleBox.Ray(new NVec3(from.X, from.Y, from.Z), new NVec3(dir.X, dir.Y, dir.Z), len, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, car.Def);
        return into != null ? CoveredSeatSplash : 1f;
    }
}
