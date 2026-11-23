using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// A pilot's or driver's corpse riding its vehicle (ADR 0026): it follows its seat until the vehicle has rested for a second or been destroyed, is flung out when the vehicle blows up,
/// and is kicked out of the seat by whoever boards it (#330). The seat is blocked for the others while the body is in it.
/// </summary>
internal sealed class RidingCorpses
{
    readonly CorpseBook _book;
    readonly List<(ushort Id, Corpse Body, ServerCar Car, int Seat)> _riding = new();

    public RidingCorpses(CorpseBook book) => _book = book;

    /// <summary>At rest: slow, and a helicopter also on the ground (hovering is not rest).</summary>
    static bool AtRest(ServerCar car) => (!car.Flies || car.Flight.Grounded) && CarPose.Velocity(car).Length() <= CorpseRules.RestSpeed;

    /// <summary>The corpse <paramref name="id"/> sits in <paramref name="seat"/>: nobody sits on the body.</summary>
    public void Seat(ushort id, Corpse body, ServerCar car, int seat)
    {
        car.Seat.Block(seat);
        _riding.Add((id, body, car, seat));
    }

    /// <summary>
    /// The vehicle blows up: every corpse still riding it is thrown with the vehicle's velocity (<paramref name="velocity"/>, read before the blast
    /// zeroes it) plus the blast knockback from <paramref name="at"/>.
    /// </summary>
    public void Throw(ServerCar car, NVec3 velocity, NVec3 at, SplashDef splash, float fraction)
    {
        for (var i = _riding.Count - 1; i >= 0; i--)
        {
            var r = _riding[i];
            if (r.Car != car) continue;
            r.Car.Seat.Unblock(r.Seat);
            _riding.RemoveAt(i);
            var k = ProjectileMath.Knockback(splash.KnockbackSpeed, at, r.Body.Position, fraction);
            r.Body.Throw((k == NVec3.Zero ? NVec3.Zero : CorpseRules.ThrowFromBlast(k)) + velocity);
        }
    }

    /// <summary>
    /// A player boards a seat a corpse holds (#330): the corpse leaves the riding list and is flung out of the door on the boarder's side (<see cref="CorpseRules.ThrowFromSeat"/>),
    /// so it flies and lands Thrown like any other, keeping its items. The seat is unblocked by the caller (<see cref="VehicleSeat.TryBoard"/>) or here.
    /// </summary>
    public void Kick(ServerCar car, int seat, NVec3 boarder)
    {
        car.Seat.Unblock(seat);
        for (var i = _riding.Count - 1; i >= 0; i--)
        {
            var r = _riding[i];
            if (r.Car != car || r.Seat != seat) continue;
            _riding.RemoveAt(i);
            r.Body.Throw(CorpseRules.ThrowFromSeat(car.GlobalPosition.ToNumerics(), car.State.Yaw, boarder, CarPose.Velocity(car)));
            GD.Print($"server: corpse {r.Id} kicked out of {car.Def.Id} {car.Id} (seat {seat})");
        }
    }

    /// <summary>Moves the seated corpses with their vehicles and drops each one whose vehicle has been at rest for a second (ADR 0026). Runs after the vehicles moved.</summary>
    public void Step(float dt)
    {
        for (var i = _riding.Count - 1; i >= 0; i--)
        {
            var (id, body, car, seat) = _riding[i];
            if (_book.Get(id) != body || !body.Seated || car.Destroyed) { car.Seat.Unblock(seat); _riding.RemoveAt(i); continue; } // gone, taken by the cap or the round end
            body.Follow(CarPose.SeatPos(car, seat).ToNumerics());
            if (!body.TickSeated(dt, AtRest(car))) continue;
            car.Seat.Unblock(seat);
            _riding.RemoveAt(i);
            GD.Print($"server: corpse {id} dropped from {car.Def.Id} {car.Id} (seat {seat})");
        }
    }
}
