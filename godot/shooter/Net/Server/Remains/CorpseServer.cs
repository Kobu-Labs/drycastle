using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Remains;

/// <summary>
/// Corpses on the server (ticket #130): a killed player leaves a corpse carrying the weapon in their primary position and its
/// rounds; a living player within reach takes it with a <see cref="EventKind.PickUpCorpse"/> event. The rules and the
/// cap live in <see cref="CorpseBook"/>; this spawns the corpse at death, flies the thrown ones and answers the pickup. A killed rider's corpse rides on in the seat (<see cref="RidingCorpses"/>).
/// </summary>
internal sealed class CorpseServer
{
    readonly ServerContext _ctx;
    readonly CorpseBook _book;
    readonly RidingCorpses _riding;
    readonly CarSeats _seats;
    readonly DeathPush _deathPush;
    readonly ItemsServer _gear;
    readonly WorldQuery _query;
    readonly Water _water;
    readonly WeaponsFile _weapons;
    readonly ItemsFile _items;
    readonly Action<ushort, ushort, Corpse> _harvesterOnCorpse;

    /// <summary>Where a rider was killed, kept from <c>PlayerKilled</c> (which frees the seat) to <c>PlayerDied</c> (which spawns the corpse): the vehicle, the seat, and whether the corpse stays seated (ADR 0026).</summary>
    readonly Dictionary<ushort, (ServerCar Car, int Seat, bool Attach)> _riderDeath = new();

    /// <param name="harvesterOnCorpse">A corpse was made: when the harvester killed its player the corpse begins its way into the machine (victim, corpse id, corpse).</param>
    public CorpseServer(ServerContext ctx, CorpseBook book, RidingCorpses riding, CarSeats seats, DeathPush deathPush, ItemsServer gear, WorldQuery query, Water water, WeaponsFile weapons, ItemsFile items,
        Action<ushort, ushort, Corpse> harvesterOnCorpse)
    {
        _ctx = ctx; _book = book; _riding = riding; _seats = seats; _deathPush = deathPush; _gear = gear; _query = query; _water = water; _weapons = weapons; _items = items; _harvesterOnCorpse = harvesterOnCorpse;
    }

    /// <summary>
    /// A seated player is killed (called from <c>PlayerKilled</c>, before the seat is freed). A pilot or driver of a vehicle still in one piece, with no
    /// push recorded, leaves a corpse that stays in the seat; anyone else (a gunner, a passenger, the rider of a destroyed vehicle, a victim of a blast)
    /// leaves one thrown with the vehicle's velocity, so it falls from a hovering or parked vehicle too (ADR 0026).
    /// </summary>
    public void RiderDies(ServerClient c)
    {
        var car = c.Car!; var seat = c.CarSeat;
        var attach = !car.Destroyed && car.Def.Seats[seat].Role == "driver" && !_deathPush.Has(c.Id);
        if (!attach && !_deathPush.Has(c.Id)) _deathPush.Record(c.Id, CarPose.Velocity(car), DeathFlight.None);
        _riderDeath[c.Id] = (car, seat, attach);
        _seats.Leave(c, false);
    }

    /// <summary>
    /// Called at a player's death, before the respawn: the corpse keeps the weapon the player had in the primary position, and every item they wore
    /// or carried in the bag (with the pickups already counted), which leave the player: they respawn bare headed with an empty bag (ADR 0018).
    /// </summary>
    public void Spawn(ushort victim)
    {
        var rider = _riderDeath.Remove(victim, out var rd) ? rd : ((ServerCar Car, int Seat, bool Attach)?)null;
        if (!_ctx.Players.TryGetValue(victim, out var c) || c.W == null) return;
        var weapon = c.W.HotbarWeapon(CorpseRules.PrimaryPos);
        var items = ItemRules.StripForCorpse(_items, c.Worn, c.Bag); // the standard items (the binoculars) are not left on the corpse
        var corpse = new Corpse(victim, c.Body.GlobalPosition.ToNumerics(), c.Body.Yaw, weapon, c.W.RoundsOf(weapon), items);
        var pushed = _deathPush.Take(victim, out var push);
        if (rider is { Attach: true } seated) corpse.Attach(CarPose.SeatPos(seated.Car, seated.Seat).ToNumerics());
        else if (rider != null) corpse.Throw(pushed ? push : NVec3.Zero); // out of a vehicle: falls, with the push it got
        else if (pushed) corpse.Launch(push);
        var id = _book.Add(corpse);
        if (rider is { Attach: true } s && id is { } cid) _riding.Seat(cid, corpse, s.Car, s.Seat);
        if (id is { } corpseId && items.Count > 0) _gear.BroadcastCorpseItems(corpseId); // after the corpse's own spawn event, on the same ordered channel
        if (id is { } hid) _harvesterOnCorpse(victim, hid, corpse); // a victim of the harvester goes into the machine
        _gear.SendEquipmentIfChanged(c);
    }

    /// <summary>Flies the thrown corpses one tick; they stop against the world (not the cars: a run-over body starts inside one).</summary>
    public void Step(float dt)
    {
        float? Cast(NVec3 from, NVec3 dir, float len)
        {
            var a = new Vector3(from.X, from.Y, from.Z);
            return _query.Blocked(MoverKind.Drop, a, a + new Vector3(dir.X, dir.Y, dir.Z) * len, out var hit) ? hit.Distance : null;
        }
        float? Surface(NVec3 p) { var y = _water.SurfaceY(p.X, p.Z); return float.IsNaN(y) ? null : y; } // a body that reaches the water floats there (ADR 0026)
        foreach (var body in _book.Bodies) body.Fly(dt, Cast, Surface);
        foreach (var d in _ctx.Match.Targets.All) if (!d.Alive) d.Body.Fly(dt, Cast); // a thrown dummy flies like a corpse
    }

    /// <summary>A living player on foot within reach takes the weapon of a corpse into their hotbar.</summary>
    public void OnPickUp(ServerClient c, byte[] payload)
    {
        if (!CorpseCodec.TryDecodePickUp(payload, out var corpse, out var pos)) return;
        if (!c.Joined || c.W == null || c.Car != null || !_ctx.IsAlive(c.Id)) return;
        var result = _book.TryPickUp(corpse, c.Body.GlobalPosition.ToNumerics(), c.W, _weapons, pos);
        if (result != PickUpResult.Ok)
        {
            GD.Print($"server: {c.Name} corpse pickup refused: {result}");
            return;
        }
        var hotbar = Enumerable.Range(0, c.W.HotbarCount).Select(c.W.HotbarWeapon).ToList();
        _ctx.SendEvent(c, EventKind.Hotbar, CorpseCodec.EncodeHotbar(hotbar));
        _ctx.SendAmmo(c);
        GD.Print($"server: {c.Name} took a corpse weapon into hotbar {pos}");
    }
}
