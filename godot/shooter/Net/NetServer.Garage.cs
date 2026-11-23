using Godot;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The bus garage on the server (garage.json next to the map): its double doors and its parked vehicle. The doors are shut on a new server; a living player on foot in
/// reach of the doorway (<see cref="Garage.InReach"/>, either side) opens or closes them with a press of the interact key, which goes to the doors before a seat of a
/// vehicle in reach. The state is <see cref="GarageDoors"/>; the box is <see cref="GarageDoorNode"/> (on the World layer, so players, bullets and vehicles stop at it), and
/// every client is told (<see cref="EventKind.GarageDoor"/>, also a joiner while they are open). They do not close on a vehicle standing in the doorway; a player standing
/// there is moved to the nearer side. The type named by the file stands in the bay at the start (<see cref="GarageParking"/>). No garage.json: nothing here does anything.
/// </summary>
public partial class NetServer
{
    LazyInit<Garage> _garage;
    readonly GarageDoors _garageDoors = new();

    Garage? EnsureGarage()
    {
        if (!_garage.Begin()) return _garage.Value;
        if (ArenaMode || string.IsNullOrEmpty(MapDir)) return null;
        var path = Path.Combine(MapDir, "garage.json");
        if (!File.Exists(path)) { GD.Print($"server: no {path}; no bus garage"); return null; }
        if (Garage.FromJsonOrNull(File.ReadAllText(path), m => GD.PrintErr($"server: {m}; no bus garage")) is { } garage) _garage.Ready(garage);
        return _garage.Value;
    }

    /// <summary>A press of interact on foot: works the garage doors when the player is at the doorway. False when they are not (the press is for something else).</summary>
    bool GarageUse(Client c)
    {
        if (EnsureGarage() is not { } garage || GarageDoorNode.Current == null) return false;
        var p = c.Body.GlobalPosition;
        if (!garage.InReach(new NVec3(p.X, p.Y, p.Z))) return false;
        var blocked = _garageDoors.IsOpen && _cars.Any(car => garage.InDoorway(N(car.GlobalPosition), car.Rotation.Y, car.HalfWidth, car.HalfLength));
        if (!_garageDoors.Use(blocked)) { ChatTo(c, ChatLineKind.Error, "The garage doors cannot close: a vehicle stands in the doorway."); return true; }
        var open = _garageDoors.IsOpen;
        GarageDoorNode.Current.SetOpen(open, animate: false); // the box first: nobody can walk into the doorway behind the push
        if (!open) PushOutOfGarageDoorway(garage);
        BroadcastEvent(EventKind.GarageDoor, GarageDoorCodec.Encode(open));
        GD.Print($"server: garage doors {(open ? "opened" : "closed")} by {c.Name} #{c.Id}");
        return true;
    }

    /// <summary>The joiner's catch-up: the doors are shut by default, so only open doors are told.</summary>
    void SendGarageDoorOnJoin(Client c)
    {
        if (_garageDoors.IsOpen) Send(c, new EventMsg((byte)EventKind.GarageDoor, GarageDoorCodec.Encode(true)));
    }

    /// <summary>A door never closes through a body: whoever stands in the doorway is moved to the nearer side (a ride in a vehicle is left alone).</summary>
    void PushOutOfGarageDoorway(Garage garage)
    {
        foreach (var c in _byId.Values)
        {
            if (!c.Joined || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) continue;
            var p = c.Body.GlobalPosition;
            if (garage.Doorway.PushOut(new NVec3(p.X, p.Y, p.Z), MoveConsts.CapsuleRadius) is not { } moved) continue;
            c.Body.Teleport(new Vector3(moved.X, moved.Y, moved.Z));
            c.Inputs.Clear();
            _match.SetPosition(c.Id, moved);
        }
    }

    /// <summary>The garage's parking place and the index of the type that stands there (vehicles.json), or null without a garage or when no type has that id.</summary>
    (NVec3 Pos, float Yaw, int Kind)? GarageParking()
    {
        if (EnsureGarage() is not { } garage || Vehicles == null) return null;
        var kind = Vehicles.Vehicles.FindIndex(v => v.Id == garage.ParkVehicle);
        if (kind < 0) { GD.PrintErr($"server: garage.json parks '{garage.ParkVehicle}', which is not in vehicles.json; the garage stays empty"); return null; }
        return (garage.ParkPos, garage.ParkYaw, kind);
    }
}
