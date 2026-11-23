using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Corpses on the server (ticket #130): a killed player leaves a corpse carrying the weapon in their primary position and its
/// rounds; a living player within reach takes it with a <see cref="EventKind.PickUpCorpse"/> event. The rules and the
/// cap live in <see cref="CorpseBook"/>; this file only spawns the corpse at death and answers the pickup.
/// </summary>
public partial class NetServer
{
    CorpseBook _corpses = null!;
    /// <summary>The throw a killing blow gave its victim (a blast's knockback, a car's run-over), set before the damage and used by <see cref="SpawnCorpse"/>. The corpse flies off with it.</summary>
    readonly Dictionary<ushort, NVec3> _deathPush = new();

    /// <summary>Records the throw for a victim about to take damage; <see cref="ClearPush"/> drops it when the hit did not kill.</summary>
    void PushOnDeath(ushort victim, NVec3 velocity, DeathFlight flight = DeathFlight.Blast)
    {
        _deathPush[victim] = velocity;
        if (flight == DeathFlight.None) _deathFlight.Remove(victim); else _deathFlight[victim] = flight;
    }

    void ClearPush(ushort victim, DamageResult result) { if (result != DamageResult.Killed) { _deathPush.Remove(victim); _deathFlight.Remove(victim); } }

    /// <summary>What threw each victim about to die (<see cref="PushOnDeath"/>), sent with the Death event so the victim's own view can follow the flight (#416).</summary>
    readonly Dictionary<ushort, DeathFlight> _deathFlight = new();

    /// <summary>Where a rider was killed, kept from <c>PlayerKilled</c> (which frees the seat) to <c>PlayerDied</c> (which spawns the corpse): the vehicle, the seat, and whether the corpse stays seated (ADR 0026).</summary>
    readonly Dictionary<ushort, (ServerCar Car, int Seat, bool Attach)> _riderDeath = new();

    /// <summary>A pilot's or driver's corpse riding its vehicle: it follows the seat until the vehicle has rested or been destroyed.</summary>
    readonly List<(ushort Id, Corpse Body, ServerCar Car, int Seat)> _riding = new();

    /// <summary>A vehicle's velocity: what a corpse leaving it carries on (a helicopter's flight velocity, a car's, zero for one asleep).</summary>
    static NVec3 CarVelocity(ServerCar car) => car.Flies ? car.Flight.Velocity : car.Asleep ? NVec3.Zero : new NVec3(car.Velocity.X, car.Velocity.Y, car.Velocity.Z);

    /// <summary>At rest: slow, and a helicopter also on the ground (hovering is not rest).</summary>
    static bool CarAtRest(ServerCar car) => (!car.Flies || car.Flight.Grounded) && CarVelocity(car).Length() <= CorpseRules.RestSpeed;

    /// <summary>
    /// A seated player is killed (called from <c>PlayerKilled</c>, before the seat is freed). A pilot or driver of a vehicle still in one piece, with no
    /// push recorded, leaves a corpse that stays in the seat; anyone else (a gunner, a passenger, the rider of a destroyed vehicle, a victim of a blast)
    /// leaves one thrown with the vehicle's velocity, so it falls from a hovering or parked vehicle too (ADR 0026).
    /// </summary>
    void RiderDies(Client c)
    {
        var car = c.Car!; var seat = c.CarSeat;
        var attach = !car.Destroyed && car.Def.Seats[seat].Role == "driver" && !_deathPush.ContainsKey(c.Id);
        if (!attach && !_deathPush.ContainsKey(c.Id)) PushOnDeath(c.Id, CarVelocity(car), DeathFlight.None);
        _riderDeath[c.Id] = (car, seat, attach);
        LeaveSeat(c, false);
    }

    /// <summary>
    /// The vehicle blows up: every corpse still riding it is thrown with the vehicle's velocity (<paramref name="velocity"/>, read before the blast
    /// zeroes it) plus the blast knockback from <paramref name="at"/>.
    /// </summary>
    void ThrowRidingCorpses(ServerCar car, NVec3 velocity, NVec3 at, SplashDef splash, float fraction)
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
    void KickSeatedCorpse(ServerCar car, int seat, NVec3 boarder)
    {
        car.Seat.Unblock(seat);
        for (var i = _riding.Count - 1; i >= 0; i--)
        {
            var r = _riding[i];
            if (r.Car != car || r.Seat != seat) continue;
            _riding.RemoveAt(i);
            r.Body.Throw(CorpseRules.ThrowFromSeat(N(car.GlobalPosition), car.State.Yaw, boarder, CarVelocity(car)));
            GD.Print($"server: corpse {r.Id} kicked out of {car.Def.Id} {car.Id} (seat {seat})");
        }
    }

    /// <summary>Moves the seated corpses with their vehicles and drops each one whose vehicle has been at rest for a second (ADR 0026). Runs after the vehicles moved.</summary>
    void StepRidingCorpses(float dt)
    {
        for (var i = _riding.Count - 1; i >= 0; i--)
        {
            var (id, body, car, seat) = _riding[i];
            if (_corpses.Get(id) != body || !body.Seated || car.Destroyed) { car.Seat.Unblock(seat); _riding.RemoveAt(i); continue; } // gone, taken by the cap or the round end
            body.Follow(N(SeatPos(car, seat)));
            if (!body.TickSeated(dt, CarAtRest(car))) continue;
            car.Seat.Unblock(seat);
            _riding.RemoveAt(i);
            GD.Print($"server: corpse {id} dropped from {car.Def.Id} {car.Id} (seat {seat})");
        }
    }

    /// <summary>
    /// Called at a player's death, before the respawn: the corpse keeps the weapon the player had in the primary position, and every item they wore
    /// or carried in the bag (with the pickups already counted), which leave the player: they respawn bare headed with an empty bag (ADR 0018).
    /// </summary>
    void SpawnCorpse(ushort victim)
    {
        var rider = _riderDeath.Remove(victim, out var rd) ? rd : ((ServerCar Car, int Seat, bool Attach)?)null;
        if (!_byId.TryGetValue(victim, out var c) || c.W == null) return;
        var weapon = c.W.HotbarWeapon(CorpseRules.PrimaryPos);
        var items = ItemRules.StripForCorpse(Items, c.Worn, c.Bag); // the standard items (the binoculars) are not left on the corpse
        var corpse = new Corpse(victim, N(c.Body.GlobalPosition), c.Body.Yaw, weapon, c.W.RoundsOf(weapon), items);
        var pushed = _deathPush.Remove(victim, out var push);
        if (rider is { Attach: true } seated) corpse.Attach(N(SeatPos(seated.Car, seated.Seat)));
        else if (rider != null) corpse.Throw(pushed ? push : NVec3.Zero); // out of a vehicle: falls, with the push it got
        else if (pushed) corpse.Launch(push);
        var id = _corpses.Add(corpse);
        if (rider is { Attach: true } s && id is { } cid)
        {
            s.Car.Seat.Block(s.Seat); // nobody sits on the body
            _riding.Add((cid, corpse, s.Car, s.Seat));
        }
        if (id is { } corpseId && items.Count > 0) BroadcastCorpseItems(corpseId); // after the corpse's own spawn event, on the same ordered channel
        if (id is { } hid) HarvesterOnCorpse(victim, hid, corpse); // a victim of the harvester goes into the machine
        SendEquipmentIfChanged(c);
    }

    /// <summary>Flies the thrown corpses one tick; they stop against the world (not the cars: a run-over body starts inside one).</summary>
    void StepCorpses(float dt)
    {
        float? Cast(NVec3 from, NVec3 dir, float len)
        {
            var a = new Vector3(from.X, from.Y, from.Z);
            return Query.Blocked(MoverKind.Drop, a, a + new Vector3(dir.X, dir.Y, dir.Z) * len, out var hit) ? hit.Distance : null;
        }
        float? Surface(NVec3 p) { var y = Water.SurfaceY(p.X, p.Z); return float.IsNaN(y) ? null : y; } // a body that reaches the water floats there (ADR 0026)
        foreach (var body in _corpses.Bodies) body.Fly(dt, Cast, Surface);
        foreach (var d in _match.Targets.All) if (!d.Alive) d.Body.Fly(dt, Cast); // a thrown dummy flies like a corpse
    }

    void OnPickUpCorpse(Client c, byte[] payload)
    {
        if (!CorpseCodec.TryDecodePickUp(payload, out var corpse, out var pos)) return;
        if (!c.Joined || c.W == null || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) return;
        var result = _corpses.TryPickUp(corpse, N(c.Body.GlobalPosition), c.W, Weapons, pos);
        if (result != PickUpResult.Ok)
        {
            GD.Print($"server: {c.Name} corpse pickup refused: {result}");
            return;
        }
        var hotbar = Enumerable.Range(0, c.W.HotbarCount).Select(c.W.HotbarWeapon).ToList();
        Send(c, new EventMsg((byte)EventKind.Hotbar, CorpseCodec.EncodeHotbar(hotbar)));
        SendAmmo(c);
        GD.Print($"server: {c.Name} took a corpse weapon into hotbar {pos}");
    }
}
