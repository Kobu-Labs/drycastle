using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The radar unit on the server (#371, GLOSSARY "Radar unit", ADR 0037). A trailer with one operator seat: while it is hitched its dish turns (<see cref="RadarSweep"/>, the machine <c>RadarUnit.Sweep</c>) and
/// stamps the enemies it passes, whole map. What it finds goes to the operator alone (<see cref="EventKind.RadarSweep"/>); nobody else is told a position. The filter is the UAV's (<see cref="UavContacts"/>,
/// <see cref="RadarRules"/>): Ghost hides a player or the vehicle he sits in, and in team deathmatch the operator's teammates are shown live instead of swept.
/// </summary>
public partial class NetServer
{
    /// <summary>The radar message goes out every this many ticks (30 Hz ticks: ten a second); the client carries the arm on between them.</summary>
    const int RadarSendTicks = 3;

    readonly List<RadarBlip> _radarEnemies = new(), _radarFriendlies = new();
    readonly List<RadarContact> _radarSwept = new();

    /// <summary>
    /// One tick of a radar unit, after it moved: the dish turns while it is hitched and stops when it is not; with an operator in the seat the sweep stamps the enemies it passes and the operator is sent the sweep
    /// ten times a second. With nobody in the seat nothing is stamped, and the next operator starts with a blank screen.
    /// </summary>
    void StepRadarUnit(ServerCar car, float dt)
    {
        if (car.Radar is not { } radar) return;
        radar.Power(car.Tow!.Hitched);
        if (OccupantAt(car, 0) is not { } op) { radar.Clear(); radar.Tick(dt, 0, 0, Array.Empty<RadarBlip>()); return; }
        RadarContactsFor(op);
        var at = car.GlobalPosition;
        radar.Tick(dt, at.X, at.Z, _radarEnemies);
        if (!radar.Sweeping || (_tick + car.Id) % RadarSendTicks != 0) return;
        _radarSwept.Clear();
        radar.Collect(_radarSwept);
        Send(op, new EventMsg((byte)EventKind.RadarSweep, RadarCodec.Encode(new RadarSweepMsg(car.Id, radar.Angle, radar.Revolution, _radarSwept, _radarFriendlies))));
    }

    /// <summary>Fills <see cref="_radarEnemies"/> and <see cref="_radarFriendlies"/> for the operator <paramref name="op"/>: the UAV's living players and dummies, and the vehicles with somebody aboard.</summary>
    void RadarContactsFor(Client op)
    {
        var vehicles = new List<RadarVehicle>();
        foreach (var other in _cars)
        {
            if (other.Destroyed || other.Retired || !other.Seat.Occupied) continue;
            var riders = new List<ushort>(other.Seat.Count);
            for (var s = 0; s < other.Seat.Count; s++) if (other.Seat.Occupant(s) != 0) riders.Add(other.Seat.Occupant(s));
            var p = other.GlobalPosition;
            vehicles.Add(new RadarVehicle(other.Id, p.X, p.Z, riders));
        }
        RadarRules.Split(UavContacts(), vehicles, op.Id, _radarEnemies, _radarFriendlies);
    }
}
