using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Waters;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// What the amphibious transport shows beyond where it is: the body leans over its wheels (<see cref="DriveBody"/>), the wheels roll with the speed
/// and the front pair steer, the propeller turns in the water, a wake trails from the stern and spray flies from the bow in proportion to the
/// speed, and it goes in with a splash. Its riders are drawn on their seats, on the body as it is drawn (so they lean and bob with it, and stay
/// aboard the vehicle its driver's client draws in the present), seated or standing as the seat says, facing where they aim. All of it is the
/// client's own: nothing here is on the wire beyond the drive record and the players' own records.
/// </summary>
public partial class NetClient
{

    /// <summary>The frame's body, wheels, propeller and water effects of one driven vehicle, as it was posed by <see cref="PoseDrive"/>.</summary>
    void StepDriveFx(CarView v, VehicleDef def, Vector3 p, float dt)
    {
        if (v.Drive == null || !_drives.TryGetValue(v.Id, out var view) || !view.Drawn) return;
        var crossed = v.Drive.Step(def, view.Shown, view.Pose, Sfx.ListenerDistance(p), dt);
        if (crossed == 0) return;
        // going in: a big splash at the bow; coming out: a smaller one where the stern leaves the water
        var at = crossed > 0 ? v.Drive.Bow : v.Drive.Stern;
        var surface = Water.SurfaceY(at.X, at.Z);
        if (float.IsNaN(surface)) surface = Water.SurfaceY(p.X, p.Z);
        if (float.IsNaN(surface)) surface = p.Y + def.Drive!.Water.Draft;
        WaterFx.Splash(Ctx.Owned, new Vector3(at.X, surface, at.Z), crossed > 0 ? 2.6f : 1.3f);
    }

    /// <summary>The node riders stand on: the body of a driven vehicle (it leans over its wheels), else the vehicle itself.</summary>
    static Node3D SeatAnchor(CarView v) => v.Drive?.Hull is { } hull && GodotObject.IsInstanceValid(hull) ? hull : v.Node;

    /// <summary>The vehicle and seat each seated player is in this frame, rebuilt by <see cref="SeatRiders"/>.</summary>
    readonly Dictionary<ushort, (CarView Car, int Seat)> _rideOf = new();

    void OnSeatsState(EntityRecord rec)
    {
        var carId = SeatsRecord.CarOf(rec.Id);
        if (!Ctx.Cars.SeatOccupants.TryGetValue(carId, out var o)) Ctx.Cars.SeatOccupants[carId] = o = new ushort[VehicleSeat.MaxSeats - 1];
        for (var s = 1; s < VehicleSeat.MaxSeats; s++) o[s - 1] = SeatsRecord.Occupant(rec, s);
    }

    /// <summary>
    /// Puts every remote rider of every vehicle on their seat as the vehicle is drawn this frame (ADR 0026). The server says who sits where (the driver
    /// in the vehicle's record, the other seats in its seats record), so a rider is drawn on the seat they are told to be on, not on one guessed from where
    /// their body is. They are drawn a tenth of a second in the past, and the vehicle its driver's client draws is in the present, so drawn from the snapshot
    /// they would trail behind it; drawn on the seat of the drawn hull they lean, bob and tilt with it. The figure is fixed there: it faces the way the hull
    /// does, and what the rider looks at (the snapshot's view, taken against the hull) moves the head and, past the neck's reach, the upper body; a rider who
    /// works a gun turns the whole figure to the aim (<see cref="RiderPose.Figure"/>), with the weapon pitched to it.
    /// </summary>
    void SeatRiders()
    {
        if (Vehicles == null) return;
        _rideOf.Clear();
        foreach (var v in Ctx.Cars.All)
        {
            if (v.Destroyed || v.Kind >= Vehicles.Vehicles.Count) continue;
            if (v.Driver != 0) _rideOf[v.Driver] = (v, 0);
            if (!Ctx.Cars.SeatOccupants.TryGetValue(v.Id, out var others)) continue;
            for (var k = 0; k < others.Length; k++) if (others[k] != 0) _rideOf[others[k]] = (v, k + 1);
        }
        foreach (var (id, r) in Ctx.Remotes)
        {
            if (r.State != EntityState.InVehicle || !_rideOf.TryGetValue(id, out var ride) || ride.Seat >= Vehicles.Vehicles[ride.Car.Kind].Seats.Count)
            {
                if (r.RideSeat >= 0) { r.RideSeat = -1; r.RideStanding = false; r.View?.SetAimPitch(0); r.View?.SetLook(0, 0, 0); }
                continue;
            }
            var v = ride.Car;
            r.RideCar = v.Id; r.RideSeat = ride.Seat;
            var seat = Vehicles.Vehicles[v.Kind].Seats[ride.Seat];
            var anchor = SeatAnchor(v);
            var figure = RiderPose.Figure(seat, v.SnapYaw, r.ShownYaw, r.ShownPitch);
            r.Node.GlobalPosition = anchor.GlobalTransform * new Vector3(seat.Offset[0], seat.Offset[1], seat.Offset[2]);
            r.Node.GlobalBasis = anchor.GlobalBasis.Orthonormalized() * new Basis(Vector3.Up, figure.BodyYaw);
            r.RideStanding = seat.Standing;
            r.View?.SetAimPitch(RiderPose.FollowsAim(seat) ? figure.Pitch : 0f);
            r.View?.SetLook(figure.TorsoYaw, figure.HeadYaw, figure.Pitch);
        }
    }
}
