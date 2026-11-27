using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// Vehicles a Job puts down for its players and takes away again (the Cartel van of a Heist, #495; later the bus and ambulance jobs): never dealt, reserved for the Job's players
/// (<see cref="VehicleSeat.Reserve"/>: anyone else is refused a seat with a notice), removed for good when the Job ends (<see cref="Remove"/>) or the round resets. Its destruction is the
/// vehicle's own hook (<see cref="ServerCar.Exploded"/>); its bay is <see cref="CarCargo"/>. Generic: nothing here is cartel-specific.
/// </summary>
internal sealed class JobVehicles
{
    readonly Fleet _fleet;
    readonly Func<VehiclesFile?> _vehicles;
    readonly Node3D _world;
    readonly Action<ServerClient, bool> _leaveSeat;
    readonly List<ServerCar> _active = new();

    /// <param name="leaveSeat">A rider leaves the seat (CarSeats.Leave; true: put on the ground beside the vehicle).</param>
    public JobVehicles(Fleet fleet, Func<VehiclesFile?> vehicles, Node3D world, Action<ServerClient, bool> leaveSeat)
    {
        _fleet = fleet; _vehicles = vehicles; _world = world; _leaveSeat = leaveSeat;
    }

    /// <summary>The Job vehicles still in the world (not removed), destroyed ones included until they are removed.</summary>
    public IReadOnlyList<ServerCar> Active => _active;

    /// <summary>
    /// A vehicle of type <paramref name="typeId"/> (vehicles.json id, e.g. "cartel_van") standing at <paramref name="position"/> (on the ground under it: the highest ground under its footprint
    /// near that height) facing <paramref name="yaw"/>, reserved for <paramref name="riders"/> (null: anybody may get in). Its colour is the type's first. Null when the type does not exist.
    /// It is one more vehicle record; it never returns once destroyed when its type says so (the Cartel van: returns false).
    /// </summary>
    public ServerCar? Spawn(string typeId, NVec3 position, float yaw, IEnumerable<ushort>? riders)
    {
        var vehicles = _vehicles();
        var kind = vehicles?.Vehicles.FindIndex(v => v.Id == typeId) ?? -1;
        if (kind < 0) { GD.PrintErr($"server: no vehicle type {typeId}, no Job vehicle spawned"); return null; }
        var def = vehicles!.Vehicles[kind];
        var space = _world.GetWorld3D().DirectSpaceState;
        var y = ServerCar.RestHeight(space, new Vector3(position.X, position.Y, position.Z), yaw, def.HalfWidth, def.HalfLength, position.Y) ?? position.Y;
        var car = _fleet.Make(kind, def, 0, new Vector3(position.X, y + 0.02f, position.Z), yaw);
        if (riders != null) car.Seat.Reserve(riders);
        _active.Add(car);
        GD.Print($"server: Job vehicle {def.Id} {car.Id} at ({position.X:F1},{y:F1},{position.Z:F1}){(riders != null ? ", reserved" : "")}");
        return car;
    }

    /// <summary>No vehicle of the world stands within <paramref name="apartM"/> of the ground point <paramref name="at"/> (x, z): a Job vehicle put down there lands in no other. Destroyed and retired ones are not in the world.</summary>
    public bool Clear(NVec2 at, float apartM)
    {
        foreach (var car in _fleet.Cars)
        {
            if (car.Retired || car.Destroyed) continue;
            var p = car.GlobalPosition;
            if (new NVec2(p.X - at.X, p.Z - at.Y).LengthSquared() < apartM * apartM) return false;
        }
        return true;
    }

    /// <summary>Who may board <paramref name="car"/> from now on (a squad member joined or left); null lets anybody in. Riders already seated keep their seats.</summary>
    public static void SetRiders(ServerCar car, IEnumerable<ushort>? riders)
    {
        if (riders == null) car.Seat.Unreserve();
        else car.Seat.Reserve(riders);
    }

    /// <summary>Takes <paramref name="car"/> out of the world for good (the Job ended): its riders are put on the ground beside it, its loads are gone, clients drop it. Removing it twice does nothing.</summary>
    public void Remove(ServerCar car)
    {
        if (!_active.Remove(car)) return;
        foreach (var o in _fleet.Occupants(car)) _leaveSeat(o, true);
        car.Seat.Clear();
        car.Retire();
        GD.Print($"server: Job vehicle {car.Def.Id} {car.Id} removed");
    }

    /// <summary>A new round: every Job vehicle goes (the Jobs end with the round), after the vehicle loop has emptied the seats.</summary>
    public void RoundReset()
    {
        foreach (var car in _active.ToList()) Remove(car);
    }
}
