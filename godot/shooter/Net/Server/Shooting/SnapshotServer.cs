using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Shooting;

/// <summary>The snapshots of the tick: every player, vehicle, dummy and rocket as entity records, and the world entities, delta-encoded against what each client last acknowledged.</summary>
internal sealed class SnapshotServer
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly Sitters _sitters;
    readonly DummyServer _dummies;
    readonly RocketSim _rockets;
    readonly VehiclesFile _vehicles;
    readonly Action<IReadOnlyList<WorldEntry>> _auditTick;
    readonly Action<List<EntityRecord>> _logStates;

    /// <summary>The entity records of the snapshot being sent (reused every tick).</summary>
    readonly List<EntityRecord> _ents = new();

    /// <param name="auditTick">The traffic audit's look at the world entities of the tick (a perf run only).</param>
    /// <param name="logStates">The loss test's log of the entity states sent (a loss test only).</param>
    public SnapshotServer(ServerContext ctx, Fleet fleet, Sitters sitters, DummyServer dummies, RocketSim rockets, VehiclesFile vehicles, Action<IReadOnlyList<WorldEntry>> auditTick, Action<List<EntityRecord>> logStates)
    {
        _ctx = ctx; _fleet = fleet; _sitters = sitters; _dummies = dummies; _rockets = rockets; _vehicles = vehicles; _auditTick = auditTick; _logStates = logStates;
    }

    /// <summary>Sends every joined client its snapshot of the tick: the entities, then the world entities near it. An empty tick sends no packet except the heartbeat, which carries the input ack (A-05).</summary>
    public void Send()
    {
        var ents = _ents; // one list for every tick (the encoders read it inside this call only)
        ents.Clear();
        AddPlayers(ents);
        AddVehicles(ents);
        ents.AddRange(_dummies.Records()); // target dummies (#148)
        _rockets.AddRecords(ents);
        var tick = _ctx.Tick;
        var heartbeat = tick % Wire.EmptyTickHeartbeat == 0;
        foreach (var c in _ctx.Connections)
        {
            if (!c.Joined) continue;
            foreach (var pkt in c.Enc.Encode(tick, c.Ack, c.SnapAck, ents))
                if (pkt.Length > SnapshotEncoder.HeaderBytes || heartbeat) _ctx.SendUnreliable(c, Wire.ChanData, pkt);
        }
        var world = _ctx.Match.Entities.Snapshot();
        foreach (var c in _ctx.Connections)
        {
            if (!c.Joined) continue;
            var at = c.Body.GlobalPosition; // the body, dead or spectating too: world interest follows where the player is (ADR 0025)
            foreach (var pkt in c.WEnc.Encode(tick, c.WAck, world, new NVec3(at.X, at.Y, at.Z))) _ctx.SendUnreliable(c, Wire.ChanData, pkt);
        }
        _auditTick(world);
        _logStates(ents);
    }

    /// <summary>Every joined player, bots included.</summary>
    void AddPlayers(List<EntityRecord> ents)
    {
        foreach (var c in _ctx.Roster)
            ents.Add(EntityRecord.Make(c.Id, EntityType.Player, new NVec3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z),
                c.Body.Yaw, c.Body.Pitch, c.Body.Health, c.Car != null && _ctx.Match.StateOf(c.Id) != EntityState.Dead ? EntityState.InVehicle : _ctx.Match.StateOf(c.Id), c.Slot,
                c.Car != null ? RiderRules.HitStance(c.Car.Def, c.CarSeat, c.Body.State.Stance) : _sitters.StanceOf(c)));
    }

    /// <summary>The vehicles, then what only some of them carry: the flak truck's turret aim, a helicopter's flight, a driven vehicle's drive, a door gun's aim.</summary>
    void AddVehicles(List<EntityRecord> ents)
    {
        foreach (var car in _fleet.Cars) if (car.Seat.Count > 1) ents.Add(SeatsRecord.Make(car.Id, Fleet.SeatOccupants(car))); // who sits in seats 1..4, for every client (ADR 0026)
        foreach (var car in _fleet.Cars)
        {
            if (car.Retired) continue; // removed for good (RocketCarts.RemoveAll): not sent, so every client drops it
            var stage = VehicleDamage.Stage(car.Hp, car.Life.MaxHp, _vehicles.DamageStages);
            ents.Add(car.IsTrailer
                ? VehicleRecord.MakeTrailer(car.Id, car.GlobalPosition.ToNumerics(), car.State.Yaw, car.Tractor?.Id ?? (ushort)0, car.Colour, car.Cargo!.Loaded, stage, car.Destroyed) // a trailer: its tractor in the driver's field, its load in a health bit (protocol 67)
                : VehicleRecord.Make(car.Id, car.GlobalPosition.ToNumerics(), car.State.Yaw, car.Seat.Driver, car.Colour, stage, car.Destroyed, car.Lights, car.Life.WheelsOff, car.Braking));
        }
        foreach (var car in _fleet.Cars) if (car.Def.Flak != null) ents.Add(FlakServer.RecordOf(car)); // a flak truck's turret aim, for everyone's model
        foreach (var car in _fleet.Cars) if (car.Flies) ents.Add(FlightRecord.Make(car.Id, car.Flight)); // a helicopter's velocity, tilt and rotor (ADR 0006)
        foreach (var car in _fleet.Cars) if (car.Drives) ents.Add(DriveRecord.Make(car.Id, car.Drive)); // a driven vehicle's speeds, slope, steering and whether it floats (ADR 0010)
        foreach (var car in _fleet.Cars) if (car.Def.DoorGun != null) ents.Add(VehicleGuns.DoorGunRecordOf(car)); // a door gun's aim against its airframe, for everyone's model
    }
}
