using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.BusGarage;

/// <summary>
/// The bus garage on the server (garage.json next to the map): its double doors and its parked vehicle. The doors are shut on a new server; a living player on foot in
/// reach of the doorway (<see cref="Garage.InReach"/>, either side) opens or closes them with a press of the interact key, which goes to the doors before a seat of a
/// vehicle in reach. The state is <see cref="GarageDoors"/>; the box is <see cref="GarageDoorNode"/> (on the World layer, so players, bullets and vehicles stop at it), and
/// every client is told (<see cref="EventKind.GarageDoor"/>, also a joiner while they are open). They do not close on a vehicle standing in the doorway; a player standing
/// there is moved to the nearer side. The type named by the file stands in the bay at the start (<see cref="Parking"/>). No garage.json: nothing here does anything.
/// </summary>
internal sealed class GarageServer
{
    readonly ServerContext _ctx;
    readonly bool _arenaMode;
    readonly string _mapDir;
    readonly IReadOnlyList<ServerCar> _cars;
    readonly Func<VehiclesFile?> _vehicles;
    readonly GarageDoors _doors = new();

    LazyInit<Garage> _garage;

    public GarageServer(ServerContext ctx, bool arenaMode, string mapDir, IReadOnlyList<ServerCar> cars, Func<VehiclesFile?> vehicles)
    {
        _ctx = ctx; _arenaMode = arenaMode; _mapDir = mapDir; _cars = cars; _vehicles = vehicles;
    }

    Garage? Ensure()
    {
        if (!_garage.Begin()) return _garage.Value;
        if (_arenaMode || string.IsNullOrEmpty(_mapDir)) return null;
        var path = Path.Combine(_mapDir, "garage.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}; no bus garage"); return null; }
        if (Garage.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"server: {m}; no bus garage")) is { } garage) _garage.Ready(garage);
        return _garage.Value;
    }

    /// <summary>Whether a player at <paramref name="at"/> could work the doors with the interact key (the doors exist and are in reach).</summary>
    public bool InReach(NVec3 at) => Ensure() is { } garage && GarageDoorNode.Current != null && garage.InReach(at);

    /// <summary>A press of interact on foot: works the garage doors when the player is at the doorway. False when they are not (the press is for something else).</summary>
    public bool Use(ServerClient c)
    {
        if (Ensure() is not { } garage || GarageDoorNode.Current == null) return false;
        if (!garage.InReach(c.Body.GlobalPosition.ToNumerics())) return false;
        var blocked = _doors.IsOpen && _cars.Any(car => garage.InDoorway(car.GlobalPosition.ToNumerics(), car.Rotation.Y, car.HalfWidth, car.HalfLength));
        if (!_doors.Use(blocked)) { _ctx.ChatTo(c, ChatLineKind.Error, "The garage doors cannot close: a vehicle stands in the doorway."); return true; }
        var open = _doors.IsOpen;
        GarageDoorNode.Current.SetOpen(open, animate: false); // the box first: nobody can walk into the doorway behind the push
        if (!open) PushOutOfDoorway(garage);
        _ctx.Broadcast(EventKind.GarageDoor, GarageDoorCodec.Encode(open));
        GD.Print($"server: garage doors {(open ? "opened" : "closed")} by {c.Name} #{c.Id}");
        return true;
    }

    /// <summary>The joiner's catch-up: the doors are shut by default, so only open doors are told.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_doors.IsOpen) _ctx.SendEvent(c, EventKind.GarageDoor, GarageDoorCodec.Encode(true));
    }

    /// <summary>A door never closes through a body: whoever stands in the doorway is moved to the nearer side (a ride in a vehicle is left alone).</summary>
    void PushOutOfDoorway(Garage garage)
    {
        foreach (var c in _ctx.Players.Values)
        {
            if (!c.Joined || c.Car != null || !_ctx.IsAlive(c.Id)) continue;
            if (garage.Doorway.PushOut(c.Body.GlobalPosition.ToNumerics(), MoveConsts.CapsuleRadius) is not { } moved) continue;
            c.Body.Teleport(moved.ToGodot());
            c.Inputs.Clear();
            _ctx.Match.SetPosition(c.Id, moved);
        }
    }

    /// <summary>The garage's parking place and the index of the type that stands there (vehicles.json), or null without a garage or when no type has that id.</summary>
    public (NVec3 Pos, float Yaw, int Kind)? Parking()
    {
        if (Ensure() is not { } garage || _vehicles() is not { } vehicles) return null;
        var kind = vehicles.Vehicles.FindIndex(v => v.Id == garage.ParkVehicle);
        if (kind < 0) { GD.PrintErr($"server: garage.json parks '{garage.ParkVehicle}', which is not in vehicles.json; the garage stays empty"); return null; }
        return (garage.ParkPos, garage.ParkYaw, kind);
    }
}
