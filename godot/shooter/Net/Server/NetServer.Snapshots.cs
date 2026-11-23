using Godot;
using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>The snapshots of the tick: every player, vehicle, dummy and rocket as entity records, and the world entities, delta-encoded against what each client last acknowledged.</summary>
public partial class NetServer
{
    /// <summary>The entity records of the snapshot being sent (reused every tick; <see cref="SendSnapshots"/>).</summary>
    readonly List<EntityRecord> _snapEnts = new();

    void SendSnapshots()
    {
        var ents = _snapEnts; // one list for every tick (the encoders read it inside this call only)
        ents.Clear();
        foreach (var c in _byId.Values) // every joined player, bots included
                ents.Add(EntityRecord.Make(c.Id, EntityType.Player, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z),
                    c.Body.Yaw, c.Body.Pitch, c.Body.Health, c.Car != null && _match.StateOf(c.Id) != EntityState.Dead ? EntityState.InVehicle : _match.StateOf(c.Id), c.Slot,
                    c.Car != null ? RiderRules.HitStance(c.Car.Def, c.CarSeat, c.Body.State.Stance) : BodyStanceOf(c)));
        foreach (var car in _cars) if (car.Seat.Count > 1) ents.Add(SeatsRecord.Make(car.Id, SeatOccupants(car))); // who sits in seats 1..4, for every client (ADR 0026)
        foreach (var car in _cars)
        {
            if (car.Retired) continue; // removed for good (RemoveRocketCarts): not sent, so every client drops it
            var stage = Vehicles == null ? 0 : VehicleDamage.Stage(car.Hp, car.Life.MaxHp, Vehicles.DamageStages);
            ents.Add(car.IsTrailer
                ? VehicleRecord.MakeTrailer(car.Id, N(car.GlobalPosition), car.State.Yaw, car.Tractor?.Id ?? (ushort)0, car.Colour, car.Cargo!.Loaded, stage, car.Destroyed) // a trailer: its tractor in the driver's field, its load in a health bit (protocol 67)
                : VehicleRecord.Make(car.Id, N(car.GlobalPosition), car.State.Yaw, car.Seat.Driver, car.Colour, stage, car.Destroyed, car.Lights, car.Life.WheelsOff, car.Braking));
        }
        foreach (var car in _cars) if (car.Def.Flak != null) ents.Add(FlakRecordOf(car)); // a flak truck's turret aim, for everyone's model
        foreach (var car in _cars) if (car.Flies) ents.Add(FlightRecord.Make(car.Id, car.Flight)); // a helicopter's velocity, tilt and rotor (ADR 0006)
        foreach (var car in _cars) if (car.Drives) ents.Add(DriveRecord.Make(car.Id, car.Drive)); // a driven vehicle's speeds, slope, steering and whether it floats (ADR 0010)
        foreach (var car in _cars) if (car.Def.DoorGun != null) ents.Add(DoorGunRecordOf(car)); // a door gun's aim against its airframe, for everyone's model
        ents.AddRange(TargetRecords()); // target dummies (#148)
        foreach (var r in _rockets)
            ents.Add(EntityRecord.Make(r.Id, EntityType.Projectile, N(r.Pos),
                MathF.Atan2(-r.Dir.X, -r.Dir.Z), MathF.Asin(Math.Clamp(r.Dir.Y, -1f, 1f)), r.Grenade != null ? r.Vel.Length() * GrenadeSpeedScale : 0, EntityState.Alive, r.Weapon)); // a grenade's speed rides the health byte (the rocket has none)
        // an empty tick (nothing changed for this client) sends no packet except the heartbeat, which carries the input ack (A-05)
        var heartbeat = _tick % Wire.EmptyTickHeartbeat == 0;
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            foreach (var pkt in c.Enc.Encode(_tick, c.Ack, c.SnapAck, ents))
                if (pkt.Length > SnapshotEncoder.HeaderBytes || heartbeat) Tx(c.Peer!, Wire.ChanData, pkt, 0);
        }
        var world = _match.Entities.Snapshot();
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            var at = c.Body.GlobalPosition; // the body, dead or spectating too: world interest follows where the player is (ADR 0025)
            foreach (var pkt in c.WEnc.Encode(_tick, c.WAck, world, new System.Numerics.Vector3(at.X, at.Y, at.Z))) Tx(c.Peer!, Wire.ChanData, pkt, 0);
        }
        AuditTick(world);
        LossLogStates(ents);
        _host.Flush();
    }
}
