using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;

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

    public Fleet(IReadOnlyDictionary<ushort, ServerClient> players) => _players = players;

    /// <summary>Beyond this distance between two origins no pair of vehicles can touch (the bus is 12 m long, a rotor disc 9 m across).</summary>
    public const float ContactBroadRange = 20f;

    public int Count => _cars.Count;

    public IReadOnlyList<ServerCar> Cars => _cars;

    /// <summary>The next vehicle's id: the ids follow the order the vehicles are added in.</summary>
    public ushort NextId => (ushort)(VehicleRecord.FirstId + _cars.Count);

    public void Add(ServerCar car) => _cars.Add(car);

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

    /// <summary>The occupants of the car's seats as ids, in seat order (0 for a free seat).</summary>
    public static ushort[] SeatOccupants(ServerCar car)
    {
        var o = new ushort[car.Seat.Count];
        for (var s = 0; s < o.Length; s++) o[s] = car.Seat.Occupant(s);
        return o;
    }
}
