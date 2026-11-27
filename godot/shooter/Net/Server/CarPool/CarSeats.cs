using Godot;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// Who sits where: getting into a vehicle and out of it, changing seats, telling the riders who sits in which seat, and where a rider's body and head are put. The one way a player stops being
/// seated is <see cref="Leave"/> (exit, death, disconnect, round restart, a thrown-out survivor).
/// </summary>
internal sealed class CarSeats
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly RidingCorpses _riding;
    readonly ShotWorld _shot;
    readonly SmokeStats _smoke;
    readonly Func<VehiclesFile> _vehicles;
    readonly Func<double> _hour;
    readonly Func<ushort, bool> _flyingStreak;

    /// <param name="hour">The hour of the day now (the headlights come on at night as the first rider gets in).</param>
    /// <param name="flyingStreak">Whether the player flies a killstreak's point of view: the seat is not theirs to change then.</param>
    public CarSeats(ServerContext ctx, Fleet fleet, RidingCorpses riding, ShotWorld shot, SmokeStats smoke, Func<VehiclesFile> vehicles, Func<double> hour, Func<ushort, bool> flyingStreak)
    {
        _ctx = ctx; _fleet = fleet; _riding = riding; _shot = shot; _smoke = smoke; _vehicles = vehicles; _hour = hour; _flyingStreak = flyingStreak;
    }

    /// <summary>The vehicle the interact key would get a player at <paramref name="p"/> into: the nearest with a boardable seat within its enter radius, or null.</summary>
    public ServerCar? EnterableCar(Vector3 p)
    {
        ServerCar? best = null; var bd = float.MaxValue;
        var vehicles = _vehicles();
        foreach (var car in _fleet.Cars)
        {
            if (car.Destroyed || car.Seat.Count == 0 || !car.Seat.HasBoardable) continue; // a seat held by a corpse counts: it is kicked out (#330); a trailer has no seat at all
            // within the vehicle's enter radius of its footprint (#217), not of its centre
            if (!VehicleReach.CanEnter(car.GlobalPosition.ToNumerics(), car.State.Yaw, car.HalfWidth, car.HalfLength, p.ToNumerics(), vehicles.EnterRadiusOf(car.Def))) continue;
            var d = VehicleReach.DistanceToFootprint(car.GlobalPosition.ToNumerics(), car.State.Yaw, car.HalfWidth, car.HalfLength, p.ToNumerics());
            if (d <= bd) { best = car; bd = d; }
        }
        return best;
    }

    /// <summary>
    /// The player gets into the nearest vehicle in reach, in the free seat nearest to them. False when there is none. A vehicle reserved for other players (<see cref="VehicleSeat.Reserve"/>,
    /// a Heist's Cartel van) refuses them with a notice: the key was used, nobody gets in.
    /// </summary>
    public bool TryEnter(ServerClient c)
    {
        var p = c.Body.GlobalPosition;
        if (EnterableCar(p) is not { } best) return false;
        if (!best.Seat.Admits(c.Id))
        {
            _ctx.ChatTo(c, ChatLineKind.Error, VehicleSeat.RefusedText);
            GD.Print($"server: {c.Name} refused a seat in {best.Def.Id} {best.Id} (reserved for its crew)");
            return true;
        }
        // the free seat nearest to the player (a roof gunner seat is a little further out than the car's centre)
        var seat = best.Seat.NearestFreeSeat(best.Def, best.GlobalPosition.ToNumerics(), best.State.Yaw, p.ToNumerics(), float.MaxValue, kickCorpses: true); // any free seat of a vehicle in reach: the nearest one
        var wasEmpty = !best.Seat.Occupied;
        if (seat < 0 || !best.Seat.TryBoard(c.Id, seat, out var kicked)) return false;
        if (kicked) _riding.Kick(best, seat, p.ToNumerics());
        Sit(c, best, seat);
        if (wasEmpty) best.Lights = Headlights.OnEnterAt(_hour()); // night: on as the first rider gets in
        c.Body.Teleport(CarPose.SeatPos(best, seat));
        _smoke.CarEnters++;
        GD.Print($"server: {c.Name} entered {best.Def.Id} {best.Id} seat {seat} ({best.Def.Seats[seat].Role})");
        return true;
    }

    /// <summary>The player sits in <paramref name="seat"/> of <paramref name="car"/>: their controls and look start from nothing.</summary>
    void Sit(ServerClient c, ServerCar car, int seat)
    {
        c.Car = car; c.CarSeat = seat; c.Throttle = c.Steer = 0; c.Handbrake = false; c.FlakSent = null; c.HeadOff = c.HeadPitch = 0;
        c.Body.InVehicle = true;
    }

    public void Exit(ServerClient c) => Leave(c, true);

    /// <summary>
    /// The one way a player stops being seated: the seat is freed, the lights go off when the vehicle is empty, and <see cref="ServerClient.Car"/> and the body's <c>InVehicle</c> flag are cleared together.
    /// A body left flagged would keep prone blocked on the server only, so prediction and the server would disagree. Returns the vehicle the player left, null when they were not seated.
    /// </summary>
    public ServerCar? Detach(ServerClient c)
    {
        var car = c.Car;
        if (car == null) return null;
        car.Seat.Exit(c.Id);
        if (!car.Seat.Occupied) car.Lights = false; // off when empty
        c.Car = null;
        c.Body.InVehicle = false;
        return car;
    }

    /// <summary>The player leaves their seat (any seat); <paramref name="placeOnGround"/> puts the body on a free side of the car (not for death, the respawn moves it).</summary>
    public void Leave(ServerClient c, bool placeOnGround)
    {
        var seat = c.CarSeat;
        if (Detach(c) is not { } car) return;
        _smoke.CarExits++;
        if (placeOnGround) c.Body.Teleport(FreeSide(c, car));
        GD.Print($"server: {c.Name} left {car.Def.Id} {car.Id} (seat {seat})");
    }

    /// <summary>A rider's request for another seat of the vehicle they sit in (<see cref="EventKind.SeatSwitch"/>). Anything invalid is ignored.</summary>
    public void OnSwitch(ServerClient c, byte[] payload)
    {
        if (SeatCodec.DecodeSwitch(payload) is not { } seat) return;
        if (c.Car is not { Destroyed: false } car || !_ctx.IsAlive(c.Id)) return;
        if (_flyingStreak(c.Id)) return; // flying a streak's point of view: the seat is not theirs to change now
        Switch(c, car, seat);
    }

    /// <summary>
    /// The seated player moves to <paramref name="seat"/> without leaving the vehicle: the old seat is freed (a driver's seat then has no input, so the
    /// vehicle coasts to a stop; a gunner's turret keeps its aim) and the new one is set up as entering it does (the body on the seat, the gunner's ammo
    /// sent anew). False when the seat is refused.
    /// </summary>
    public bool Switch(ServerClient c, ServerCar car, int seat)
    {
        var from = c.CarSeat;
        if (seat >= 0 && seat < car.Seat.Count && car.Seat.HoldsCorpse(seat) && car.Seat.SeatOf(c.Id) == from) _riding.Kick(car, seat, c.Body.GlobalPosition.ToNumerics()); // a corpse sits there: out it goes (#330)
        if (car.Seat.SeatOf(c.Id) != from || !car.Seat.TrySwitch(c.Id, seat)) return false;
        c.CarSeat = seat; c.Throttle = c.Steer = 0; c.Handbrake = false; c.FlakSent = null; c.HeadOff = c.HeadPitch = 0;
        c.Body.Teleport(CarPose.SeatPos(car, seat));
        _ctx.Match.SetPosition(c.Id, c.Body.GlobalPosition.ToNumerics());
        GD.Print($"server: {c.Name} moved in {car.Def.Id} {car.Id} from seat {from} to seat {seat} ({car.Def.Seats[seat].Role})");
        return true;
    }

    /// <summary>Tells the riders who sits where when that has changed since the last send (an entry, an exit, a switch, a seat freed by a death or a drop).</summary>
    public void SendState(ServerCar car)
    {
        Span<ushort> now = stackalloc ushort[car.Seat.Count]; // every tick for every car: no heap array unless the seats changed
        for (var s = 0; s < now.Length; s++) now[s] = car.Seat.Occupant(s);
        if (car.SeatsSent != null && car.SeatsSent.AsSpan().SequenceEqual(now)) return;
        car.SeatsSent = now.ToArray();
        var msg = new EventMsg((byte)EventKind.SeatState, SeatCodec.EncodeState(car.Id, car.SeatsSent));
        foreach (var o in _fleet.Occupants(car)) _ctx.Send(o, msg);
    }

    /// <summary>A passive rider's body yaw and pitch for the snapshot: the hull's yaw now, turned by their look (<see cref="ServerClient.HeadOff"/>), at their look's pitch.</summary>
    public static void ApplyHeadLook(ServerClient c, ServerCar car)
    {
        c.Body.Yaw = Fleet.AttitudeOf(car).Yaw + c.HeadOff;
        c.Body.Pitch = c.HeadPitch;
    }

    /// <summary>Exit position: right, left, behind, in front of the car (first with ground and room for the capsule), else above the roof.</summary>
    Vector3 FreeSide(ServerClient c, ServerCar car)
    {
        var yaw = car.State.Yaw;
        var fwd = car.Forward; var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        var side = car.HalfWidth + 1f; var along = car.HalfLength + 1.05f; // 2.1 / 3.2 m for the car
        if (car.Flies && !car.Flight.Grounded)
        {
            // out of a helicopter in the air: through the door on the rider's own side, at the cabin floor, and from there it is a fall
            // (the fall damage rules decide what it costs; a low hover is free)
            var seatX = car.Def.Seats[c.CarSeat].Offset[0];
            var first = seatX < 0 ? -1f : 1f;
            foreach (var s in new[] { first, -first })
            {
                var p = car.GlobalPosition + right * (s * side) + Vector3.Up * car.Def.Seats[c.CarSeat].Offset[1];
                if (!c.Body.TestMove(new Transform3D(Basis.Identity, p), Vector3.Zero)) return p;
            }
            return car.GlobalPosition + right * (first * side) + Vector3.Up * car.Def.Seats[c.CarSeat].Offset[1];
        }
        foreach (var off in new[] { right * side, -right * side, -fwd * along, fwd * along })
        {
            var p = car.GlobalPosition + off;
            if (_shot.Query.Ground(MoverKind.Player, p, 1.5f, 2.5f) is not { } ground) continue;
            var g = ground.Position + Vector3.Up * 0.1f;
            if (!c.Body.TestMove(new Transform3D(Basis.Identity, g), Vector3.Zero)) return g;
        }
        return car.GlobalPosition + Vector3.Up * 2.5f;
    }
}
