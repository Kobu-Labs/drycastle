using Godot;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client.Seating;

/// <summary>The seats of the vehicle the local player sits in: who is in each (0 = free) and which one is the player's (-1 until known).</summary>
internal sealed record SeatView(ushort CarId, ushort[] Occupants, int Mine);

/// <summary>
/// The vehicles of the world as this client draws them, by id, and the seat the local player sits in: the one place that knows which vehicle is which and who rides it.
/// The vehicle features (drawing, driving, flying, towing, the harvester, the Scud) read it and change only the <see cref="CarView"/> they own the part of.
/// </summary>
internal sealed class ClientCars
{
    /// <summary>The yaw the vehicle the local player rides is drawn at: where the chase camera ends when they get out.</summary>
    public float DrivenYaw;

    readonly ClientContext _ctx;
    readonly Dictionary<ushort, CarView> _byId = new();

    public ClientCars(ClientContext ctx)
    {
        _ctx = ctx;
        Sounds = new CarSounds(ctx, this);
    }

    public IReadOnlyDictionary<ushort, CarView> ById => _byId;
    public IEnumerable<CarView> All => _byId.Values;
    /// <summary>The same as <see cref="All"/>, as the concrete collection: a <c>foreach</c> over it allocates nothing (the per-frame prompts walk it).</summary>
    public Dictionary<ushort, CarView>.ValueCollection Values => _byId.Values;
    public int Count => _byId.Count;

    /// <summary>The vehicles' one-shots and long sounds, heard from inside by their riders.</summary>
    public CarSounds Sounds { get; }

    /// <summary>The seats of the vehicle the local player sits in, as the server last said; null on foot.</summary>
    public SeatView? Seats;

    /// <summary>Who sits in the other seats of each vehicle (seat 1 on), from the snapshot's seat records: an enemy on any seat keeps a vehicle off the map (#313).</summary>
    public readonly Dictionary<ushort, ushort[]> SeatOccupants = new();

    /// <summary>The cargo vehicles whose doors the server says are open (event CargoDoors, also a joiner's catch-up); every other vehicle's are shut. The renderer swings each model's doors to it.</summary>
    public readonly HashSet<ushort> CargoOpen = new();

    public bool TryGet(ushort id, out CarView car) => _byId.TryGetValue(id, out car!);
    public bool Has(ushort id) => _byId.ContainsKey(id);
    public void Add(CarView car) => _byId[car.Id] = car;

    /// <summary>A vehicle left the world: its sounds, model and solid box go. False when it was not known.</summary>
    public bool Remove(ushort id)
    {
        if (!_byId.Remove(id, out var c)) return false;
        c.Snd?.Release(); c.Engine?.Release(); c.Node.QueueFree(); c.Solid?.QueueFree();
        SeatOccupants.Remove(id);
        return true;
    }

    /// <summary>Leaving: every model goes (the probes are under the owned node and go with it).</summary>
    public void Clear()
    {
        foreach (var v in _byId.Values) if (GodotObject.IsInstanceValid(v.Node)) v.Node.QueueFree();
        _byId.Clear();
        CargoOpen.Clear();
    }

    public void ReleaseSounds()
    {
        foreach (var c in _byId.Values) { c.Snd?.Release(); c.Engine?.Release(); }
        Sounds.Release();
    }

    /// <summary>True while the local player rides this vehicle in any seat.</summary>
    public bool Rides(CarView v) => _ctx.Driving && (v.Driver == _ctx.MyId || (Seats is { Mine: >= 0 } st && st.CarId == v.Id));

    /// <summary>The seat the local player sits in, with its vehicle, once the server has said who sits where; null on foot or before that.</summary>
    public (CarView Car, VehicleDef Def, VehicleSeatDef Seat, int Index)? MySeat()
    {
        if (!_ctx.Driving || _ctx.Dead || Seats is not { Mine: >= 0 } s || _ctx.Vehicles == null || !TryGet(s.CarId, out var v) || v.Kind >= _ctx.Vehicles.Vehicles.Count) return null;
        var def = _ctx.Vehicles.Vehicles[v.Kind];
        return s.Mine < def.Seats.Count ? (v, def, def.Seats[s.Mine], s.Mine) : null;
    }

    /// <summary>The vehicle and occupants the schematic shows, or nothing when the local player is not seated (or the vehicle is not known yet).</summary>
    public (VehicleDef Def, ushort[] Occupants, int Mine)? SeatPanel()
    {
        if (!_ctx.Driving || Seats is not { } s || _ctx.Vehicles == null || !TryGet(s.CarId, out var v) || v.Kind >= _ctx.Vehicles.Vehicles.Count) return null;
        return (_ctx.Vehicles.Vehicles[v.Kind], s.Occupants, s.Mine);
    }

    readonly Dictionary<(int Tractor, int Trailer), TowGeometry> _towGeo = new();

    /// <summary>The geometry of a tractor type pulling a trailer type (built once).</summary>
    public TowGeometry GeometryOf(int tractorKind, int trailerKind)
    {
        if (_towGeo.TryGetValue((tractorKind, trailerKind), out var g)) return g;
        return _towGeo[(tractorKind, trailerKind)] = TowGeometry.Of(_ctx.Vehicles!.Vehicles[tractorKind], _ctx.Vehicles.Vehicles[trailerKind]);
    }

    /// <summary>The trailer hitched to <paramref name="tractor"/> as the records say, or null (a tractor pulls at most one).</summary>
    public CarView? TrailerOf(ushort tractor)
    {
        if (tractor == 0) return null;
        foreach (var c in _byId.Values) if (c.Trailer is { } t && t.TractorId == tractor && !c.Destroyed) return c;
        return null;
    }
}
