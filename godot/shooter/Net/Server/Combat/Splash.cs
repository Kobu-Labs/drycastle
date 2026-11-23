using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// The splash rule of ADR 0028 (the Splash row of <see cref="CollisionRules"/>): a blast hurts a target only along a clear line of sight, and a
/// rider in a covered seat takes half of it through the vehicle's hull. Asked by every blast that splashes players (rockets and flak shells, the
/// attack missile, the gunship, a vehicle blowing up), once for each target the radius reaches, so only after the damage came out above zero.
/// </summary>
internal sealed class Splash
{
    /// <summary>The line of sight starts this far above the blast, so a blast that sits on the ground does not clip the slope it stands on.</summary>
    const float RayLiftM = 0.1f;

    /// <summary>Share of the splash a covered seat's rider takes through the hull; an exposed seat takes all of it.</summary>
    const float CoveredSeatSplash = 0.5f;

    readonly ServerContext _ctx;
    readonly Func<Shelters> _shelters;
    readonly Func<WorldQuery> _query;
    readonly Func<ServerCar, VehiclePose> _poseOf;

    /// <param name="poseOf">Where a vehicle stands for the shot being resolved (the lag compensation's view).</param>
    public Splash(ServerContext ctx, Func<Shelters> shelters, Func<WorldQuery> query, Func<ServerCar, VehiclePose> poseOf)
    {
        _ctx = ctx; _shelters = shelters; _query = query; _poseOf = poseOf;
    }

    /// <summary>
    /// The share of a blast at <paramref name="blast"/> that reaches <paramref name="t"/>: 0 when a bunker room's walls (<see cref="Shelters"/>) or
    /// any wall a shot stops on (<see cref="WorldQuery.Wall"/>: terrain, building, fence, placed solid, trunk, crown, bush, closed door) is between them,
    /// <see cref="CoveredSeatSplash"/> for a rider of a covered seat when the blast is outside their vehicle's hull, 1 otherwise. A vehicle's hull does not
    /// shield anyone else. One ray per target.
    /// </summary>
    public float Share(NVec3 blast, HitTarget t)
    {
        var centre = HitMath.Centre(t);
        if (!_shelters().Reaches(blast, centre)) return 0f;
        var from = new Vector3(blast.X, blast.Y + RayLiftM, blast.Z);
        var d = new Vector3(centre.X, centre.Y, centre.Z) - from;
        var len = d.Length();
        if (len < 0.05f) return 1f;
        var dir = d / len;
        if (_query().Wall(from, dir, len) < len - 0.05f) return 0f;
        if (!_ctx.Players.TryGetValue(t.Id, out var rider) || rider.Car is not { Destroyed: false } car || car.Def.SeatExposed(rider.CarSeat)) return 1f;
        var pose = _poseOf(car);
        var into = VehicleBox.Ray(new NVec3(from.X, from.Y, from.Z), new NVec3(dir.X, dir.Y, dir.Z), len, pose.Feet, pose.Yaw, pose.Pitch, pose.Roll, car.Def);
        return into != null ? CoveredSeatSplash : 1f;
    }
}
