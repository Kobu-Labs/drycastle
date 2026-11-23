using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The seat switch on the client: while seated in any vehicle the number keys (the weapon slot keys, the same bindings) take that seat, a small
/// top-down schematic in the bottom right shows the seats (<see cref="Hud.SetSeats"/>), and the camera follows the seat the player sits in (<see cref="RiderCamera"/>). The server
/// says who sits where (<see cref="EventKind.SeatState"/>, sent to the riders when it changes; the seats record of the snapshot for every vehicle) and moves the player on a request
/// (<see cref="EventKind.SeatSwitch"/>). A single player can drive, go to the turret and drive again. The riders of other players are drawn on their seats here (ADR 0026).
/// </summary>
internal sealed class SeatsClient
{
    readonly ClientContext _ctx;

    /// <summary>The vehicle and seat each seated player is in this frame, rebuilt by <see cref="PlaceRiders"/>.</summary>
    readonly Dictionary<ushort, (CarView Car, int Seat)> _rideOf = new();

    string _armedPrompt = "";
    double _armedPromptAt = -10;

    public SeatsClient(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The seat state the server sends to the riders when it changes.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.SeatState && SeatCodec.DecodeState(payload) is { } s)
            _ctx.Cars.Seats = new SeatView(s.CarId, s.Occupants, Array.IndexOf(s.Occupants, _ctx.MyId));
    }

    /// <summary>The seats record of a vehicle: who sits in each seat but the driver's.</summary>
    public void OnRecord(EntityRecord rec)
    {
        var carId = SeatsRecord.CarOf(rec.Id);
        if (!_ctx.Cars.SeatOccupants.TryGetValue(carId, out var o)) _ctx.Cars.SeatOccupants[carId] = o = new ushort[VehicleSeat.MaxSeats - 1];
        for (var s = 1; s < VehicleSeat.MaxSeats; s++) o[s - 1] = SeatsRecord.Occupant(rec, s);
    }

    /// <summary>
    /// A number key while seated: asks the server for that seat. Not while a streak's point of view is flown (the weapon keys are its own), nor for the
    /// seat the player already has or one that is taken. The keys are the weapon slot actions, so a rebind moves both. True when the key was used.
    /// </summary>
    public bool HandleInput(InputEvent e)
    {
        if (!_ctx.View.Mode.SeatKeysLive || _ctx.Cars.Seats is not { Mine: >= 0 } s || !_ctx.Connected()) return false;
        if (Input.MouseMode != Input.MouseModeEnum.Captured || e is not InputEventKey { Echo: false }) return false;
        for (var n = 1; n <= s.Occupants.Length; n++)
        {
            if (!e.IsActionPressed($"slot_{n}")) continue;
            if (n - 1 != s.Mine && s.Occupants[n - 1] == 0) _ctx.Send(EventKind.SeatSwitch, SeatCodec.EncodeSwitch(n - 1));
            return true; // the digit is the seat's while seated: it never changes the weapon
        }
        return false;
    }

    /// <summary>The speed of the vehicle the local player rides, m/s: what widens an armed rider's spread.</summary>
    public static float RideSpeed(CarView v, DriveState? drawn) => drawn is { } d ? MathF.Abs(d.Speed) : v.Kmh / 3.6f;

    /// <summary>The armed rider's prompt line: the keys as they are bound (the weapon change is the mouse wheel, which has no binding). Rebuilt once a second in case one was rebound.</summary>
    public string ArmedPrompt()
    {
        if (_ctx.Now - _armedPromptAt < 1.0) return _armedPrompt;
        _armedPromptAt = _ctx.Now;
        return _armedPrompt = Strings.F("hud.armedseat.prompt", InputBindings.Name("fire"), InputBindings.Name("reload"), InputBindings.Name("interact"));
    }

    /// <summary>
    /// Puts every remote rider of every vehicle on their seat as the vehicle is drawn this frame (ADR 0026). The server says who sits where (the driver
    /// in the vehicle's record, the other seats in its seats record), so a rider is drawn on the seat they are told to be on, not on one guessed from where
    /// their body is. They are drawn a tenth of a second in the past, and the vehicle its driver's client draws is in the present, so drawn from the snapshot
    /// they would trail behind it; drawn on the seat of the drawn hull they lean, bob and tilt with it. The figure is fixed there: it faces the way the hull
    /// does, and what the rider looks at (the snapshot's view, taken against the hull) moves the head and, past the neck's reach, the upper body; a rider who
    /// works a gun turns the whole figure to the aim (<see cref="RiderPose.Figure"/>), with the weapon pitched to it.
    /// </summary>
    public void PlaceRiders()
    {
        if (_ctx.Vehicles == null) return;
        _rideOf.Clear();
        foreach (var v in _ctx.Cars.All)
        {
            if (v.Destroyed || v.Kind >= _ctx.Vehicles.Vehicles.Count) continue;
            if (v.Driver != 0) _rideOf[v.Driver] = (v, 0);
            if (!_ctx.Cars.SeatOccupants.TryGetValue(v.Id, out var others)) continue;
            for (var k = 0; k < others.Length; k++) if (others[k] != 0) _rideOf[others[k]] = (v, k + 1);
        }
        foreach (var (id, r) in _ctx.Remotes)
        {
            if (r.State != EntityState.InVehicle || !_rideOf.TryGetValue(id, out var ride) || ride.Seat >= _ctx.Vehicles.Vehicles[ride.Car.Kind].Seats.Count)
            {
                if (r.RideSeat >= 0) { r.RideSeat = -1; r.RideStanding = false; r.View?.SetAimPitch(0); r.View?.SetLook(0, 0, 0); }
                continue;
            }
            var v = ride.Car;
            r.RideCar = v.Id; r.RideSeat = ride.Seat;
            var seat = _ctx.Vehicles.Vehicles[v.Kind].Seats[ride.Seat];
            var anchor = v.SeatAnchor;
            var figure = RiderPose.Figure(seat, v.SnapYaw, r.ShownYaw, r.ShownPitch);
            r.Node.GlobalPosition = anchor.GlobalTransform * new Vector3(seat.Offset[0], seat.Offset[1], seat.Offset[2]);
            r.Node.GlobalBasis = anchor.GlobalBasis.Orthonormalized() * new Basis(Vector3.Up, figure.BodyYaw);
            r.RideStanding = seat.Standing;
            r.View?.SetAimPitch(RiderPose.FollowsAim(seat) ? figure.Pitch : 0f);
            r.View?.SetLook(figure.TorsoYaw, figure.HeadYaw, figure.Pitch);
        }
    }
}
