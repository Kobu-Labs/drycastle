using Godot;
using Drycastle.Shooter.Buildings;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Houses;

/// <summary>
/// The doors of the hollow houses on the server (<see cref="HouseDoorsNode"/>, made with the map). Shut on a new server; a living player on foot in reach of one
/// (either side) opens or closes the nearest with a press of the interact key, which goes to the door before a seat of a vehicle in reach. The state is
/// <see cref="HouseDoors"/>; every client is told (<see cref="EventKind.HouseDoor"/>, a joiner each open one). A door does not close on a vehicle standing in it; a player
/// standing in it is moved to the nearer side. A map without interiors.json: nothing here does anything.
/// </summary>
internal sealed class HouseDoorServer
{
    readonly ServerContext _ctx;
    readonly bool _arenaMode;
    readonly IReadOnlyList<ServerCar> _cars;
    /// <summary>Per hollow house (grid index): its doors and their frame cells (<see cref="HouseFrames"/>), found at its first carve.</summary>
    readonly Dictionary<ushort, (int Door, int[] Frame)[]> _frames = new();

    public HouseDoorServer(ServerContext ctx, bool arenaMode, IReadOnlyList<ServerCar> cars)
    {
        _ctx = ctx; _arenaMode = arenaMode; _cars = cars;
    }

    HouseDoorsNode? Node => _arenaMode ? null : HouseDoorsNode.Current;

    /// <summary>Whether a player at <paramref name="at"/> could work a house's door with the interact key.</summary>
    public bool InReach(NVec3 at) => Node is { } node && node.Doors.Nearest(at) >= 0;

    /// <summary>A press of interact on foot: works the door the player is at. False when they are at none (the press is for something else).</summary>
    public bool Use(ServerClient c)
    {
        if (Node is not { } node) return false;
        var door = node.Doors.Nearest(c.Body.GlobalPosition.ToNumerics());
        if (door < 0) return false;
        var way = node.Doors.All[door].Doorway;
        var blocked = node.Doors.IsOpen(door) && _cars.Any(car => way.Holds(car.GlobalPosition.ToNumerics(), car.Rotation.Y, car.HalfWidth, car.HalfLength));
        if (!node.Doors.Use(door, blocked)) { _ctx.ChatTo(c, ChatLineKind.Error, "The door cannot close: a vehicle stands in the doorway."); return true; }
        var open = node.Doors.IsOpen(door);
        node.Show(door, open, animate: false); // the box first: nobody can walk into the doorway behind the push
        if (!open) PushOutOfDoorway(way);
        _ctx.Broadcast(EventKind.HouseDoor, HouseDoorCodec.Encode(door, open));
        return true;
    }

    /// <summary>The joiner's catch-up: doors are shut by default, so only open and blown ones are told.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (Node is not { } node) return;
        for (var door = 0; door < node.Doors.All.Count; door++)
            if (node.Doors.StateOf(door) is var s && s != HouseDoorState.Shut) _ctx.SendEvent(c, EventKind.HouseDoor, HouseDoorCodec.Encode(door, s));
    }

    /// <summary>A hollow house lost cells: each of its doors whose frame a carve took is blown out, for everyone (docs/house-destruction.md).</summary>
    public void HouseChanged(HouseCells house)
    {
        if (Node is not { } node) return;
        var g = house.Grid;
        if (!_frames.TryGetValue(g.Index, out var doors))
        {
            var mine = new List<(int, int[])>();
            for (var i = 0; i < node.Doors.All.Count; i++)
                if (node.Doors.All[i].House == g.Id && HouseFrames.FrameCells(g, HouseFrames.Rect(node.Doors.All[i].Doorway)) is { Length: > 0 } frame) mine.Add((i, frame));
            _frames[g.Index] = doors = mine.ToArray();
        }
        foreach (var (door, frame) in doors)
        {
            if (node.Doors.IsBlown(door) || !HouseFrames.Unframed(house, frame)) continue;
            node.Show(door, HouseDoorState.Blown, animate: false);
            _ctx.Broadcast(EventKind.HouseDoor, HouseDoorCodec.Encode(door, HouseDoorState.Blown));
        }
    }

    /// <summary>The houses are whole again (a new round, /houses restore): every blown door is hung again, shut, for everyone.</summary>
    public void RehangAll()
    {
        if (Node is not { } node) return;
        for (var door = 0; door < node.Doors.All.Count; door++)
        {
            if (!node.Doors.IsBlown(door)) continue;
            node.Show(door, HouseDoorState.Shut, animate: false);
            PushOutOfDoorway(node.Doors.All[door].Doorway);
            _ctx.Broadcast(EventKind.HouseDoor, HouseDoorCodec.Encode(door, HouseDoorState.Shut));
        }
    }

    /// <summary>A door never closes through a body: whoever stands in the doorway is moved to the nearer side (a ride in a vehicle is left alone).</summary>
    void PushOutOfDoorway(Doorway way)
    {
        foreach (var c in _ctx.Players.Values)
        {
            if (!c.Joined || c.Car != null || !_ctx.IsAlive(c.Id)) continue;
            if (way.PushOut(c.Body.GlobalPosition.ToNumerics(), MoveConsts.CapsuleRadius) is not { } moved) continue;
            c.Body.Teleport(moved.ToGodot());
            c.Inputs.Clear();
            _ctx.Match.SetPosition(c.Id, moved);
        }
    }
}
