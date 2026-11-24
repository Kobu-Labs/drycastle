using Godot;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// Every vehicle of the map, in the order they were made: the one place that knows which cars exist. A vehicle's id is
/// <see cref="VehicleRecord.FirstId"/> plus its index, so <see cref="Find"/> is a lookup, not a search. Who sits in a seat is answered here too (the seats
/// keep ids, the players are the server's).
/// </summary>
internal sealed class Fleet
{
    readonly List<ServerCar> _cars = new();
    readonly IReadOnlyDictionary<ushort, ServerClient> _players;
    readonly Node3D _world;
    readonly Water _water;
    readonly Drycastle.MapLoader.MapLoader? _map;
    FlightBounds? _bounds;

    public Fleet(IReadOnlyDictionary<ushort, ServerClient> players, Node3D world, Water water, Drycastle.MapLoader.MapLoader? map)
    {
        _players = players; _world = world; _water = water; _map = map;
    }

    /// <summary>The flyable rectangle of the map (the box around its terrain, <see cref="FlightProbe.MapBounds"/>); a smoke arena has none.</summary>
    public FlightBounds FlightBounds => _bounds ??= FlightProbe.MapBounds(_map);

    /// <summary>Beyond this distance between two origins no pair of vehicles can touch (the bus is 12 m long, a rotor disc 9 m across).</summary>
    public const float ContactBroadRange = 20f;

    public int Count => _cars.Count;

    public IReadOnlyList<ServerCar> Cars => _cars;

    /// <summary>The next vehicle's id: the ids follow the order the vehicles are added in.</summary>
    public ushort NextId => (ushort)(VehicleRecord.FirstId + _cars.Count);

    public void Add(ServerCar car) => _cars.Add(car);

    /// <summary>A new vehicle of type <paramref name="kind"/> standing at <paramref name="origin"/> facing <paramref name="yaw"/> (where it returns to), in the world and at the end of the list: its id is the next one.</summary>
    public ServerCar Make(int kind, VehicleDef def, byte colour, Vector3 origin, float yaw)
    {
        var car = new ServerCar
        {
            Name = $"{def.Id}{Count}", Id = NextId, Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, colour),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = origin, OriginYaw = yaw,
            FlightWater = _water, FlightBounds = FlightBounds,
        };
        _world.AddChild(car);
        _cars.Add(car);
        return car;
    }

    /// <summary>The vehicle with this id, or null.</summary>
    public ServerCar? Find(int id) => id - VehicleRecord.FirstId is var i && i >= 0 && i < _cars.Count && _cars[i].Id == id ? _cars[i] : null;

    /// <summary>The connected player in <paramref name="seat"/>, or null (empty, or the player has gone).</summary>
    public ServerClient? OccupantAt(ServerCar car, int seat)
    {
        if (seat < 0 || seat >= car.Seat.Count) return null; // a trailer has no driver
        var id = car.Seat.Occupant(seat);
        return id != 0 && _players.TryGetValue(id, out var c) ? c : null;
    }

    /// <summary>The connected players in the car's seats, in seat order.</summary>
    public List<ServerClient> Occupants(ServerCar car)
    {
        var list = new List<ServerClient>(car.Seat.Count);
        for (var s = 0; s < car.Seat.Count; s++) if (OccupantAt(car, s) is { } o) list.Add(o);
        return list;
    }

    /// <summary>The attitude a door gun's arc and a rider turn with: the helicopter's; a land vehicle's yaw alone.</summary>
    public static (float Yaw, float Pitch, float Roll) AttitudeOf(ServerCar car) =>
        car.Flies ? (car.Flight.Yaw, car.Flight.Pitch, car.Flight.Roll)
        : car.Drives ? (car.Drive.Yaw, car.Drive.Pitch, car.Drive.Roll) // a driven vehicle lies on the slope it stands on
        : car.TrailerTilt is { } t ? (car.State.Yaw, t.Pitch, t.Roll) // a trailer lies on the ground it stands on (#482)
        : (car.State.Yaw, 0f, 0f);

    /// <summary>The occupants of the car's seats as ids, in seat order (0 for a free seat).</summary>
    public static ushort[] SeatOccupants(ServerCar car)
    {
        var o = new ushort[car.Seat.Count];
        for (var s = 0; s < o.Length; s++) o[s] = car.Seat.Occupant(s);
        return o;
    }
}
