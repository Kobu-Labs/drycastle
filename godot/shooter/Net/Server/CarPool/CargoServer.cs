using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// The cargo vehicles on the server (GLOSSARY "Cargo volume"; docs/cartel.md "Cargo vehicles"; vehicles.json <c>cargo_volume</c>): every live vehicle whose type has one gets a <see cref="VehicleCargo"/>, added to
/// the <see cref="DuffelBook"/> when the vehicle appears and removed when it is retired. The interact key of a player on foot at the access point works the doors (<see cref="Use"/>; the doors win the key
/// there, <see cref="DoorsInReach"/>), they stay as left, everybody is told (<see cref="EventKind.CargoDoors"/>, a joiner too: <see cref="SendOnJoin"/>). A vehicle destroyed, retired or reset (<see cref="ServerCar.LifeEnded"/>)
/// shuts its doors and its loaded Duffels are destroyed. Which party a volume takes (the Cartel van: its Job's) and who counts what loaded is the loader's (<see cref="VehicleCargo.Restrict"/>).
/// </summary>
internal sealed class CargoServer
{
    readonly ServerContext _ctx;
    readonly DuffelBook _book;
    readonly float _margin;
    readonly Dictionary<ushort, (ServerCar Car, VehicleCargo Bay)> _bays = new();

    public CargoServer(ServerContext ctx, Fleet fleet, DuffelBook book, DuffelsFile data)
    {
        _ctx = ctx; _book = book; _margin = (float)data.CargoMarginM;
        foreach (var car in fleet.Cars) Add(car);
        fleet.Made += Add;
    }

    /// <summary>The volume of a vehicle, or null for a type without one (or a vehicle taken out for good).</summary>
    public VehicleCargo? BayOf(ServerCar car) => _bays.TryGetValue(car.Id, out var b) ? b.Bay : null;

    void Add(ServerCar car)
    {
        if (car.Def.CargoVolume is not { } def || _bays.ContainsKey(car.Id)) return;
        var bay = new VehicleCargo(car.Id, def, () => car.Destroyed || car.Retired ? null : CarPose.Present(car), _margin);
        _bays[car.Id] = (car, bay);
        _book.AddVolume(bay);
        car.LifeEnded += OnLifeEnded;
    }

    /// <summary>The vehicle's life ended or started over: its doors shut, what it held is destroyed, and a vehicle gone for good drops its volume.</summary>
    void OnLifeEnded(ServerCar car)
    {
        if (!_bays.TryGetValue(car.Id, out var b)) return;
        var lost = _book.RemoveLoadedIn(b.Bay);
        if (b.Bay.Doors.Close()) _ctx.Broadcast(EventKind.CargoDoors, CargoDoorsCodec.Encode(car.Id, false));
        if (lost > 0) GD.Print($"server: {car.Def.Id} {car.Id} lost {lost} loaded duffel(s)");
        if (!car.Retired) return;
        _book.RemoveVolume(b.Bay);
        _bays.Remove(car.Id);
        car.LifeEnded -= OnLifeEnded;
    }

    /// <summary>The bay whose doors a player standing at <paramref name="feet"/> may work (the nearest access point within reach), or null.</summary>
    VehicleCargo? DoorsAt(NVec3 feet)
    {
        VehicleCargo? best = null; var bd = float.PositiveInfinity;
        foreach (var (_, bay) in _bays.Values)
            if (bay.DoorsDistance(feet) is { } d && d < bd) { best = bay; bd = d; }
        return best;
    }

    /// <summary>A cargo vehicle's doors are in reach of the player's interact key (its access point): they own the key before the vehicle's seat.</summary>
    public bool DoorsInReach(ServerClient c) => DoorsAt(c.Body.GlobalPosition.ToNumerics()) != null;

    /// <summary>The interact key's press on foot: opens or shuts the doors of the vehicle at whose access point the player stands. True when it did (the key is used up).</summary>
    public bool Use(ServerClient c)
    {
        if (DoorsAt(c.Body.GlobalPosition.ToNumerics()) is not { } bay) return false;
        var open = bay.Doors.Toggle();
        _ctx.Broadcast(EventKind.CargoDoors, CargoDoorsCodec.Encode(bay.Vehicle, open));
        GD.Print($"server: {c.Name} #{c.Id} {(open ? "opened" : "shut")} the cargo doors of vehicle {bay.Vehicle}");
        return true;
    }

    /// <summary>The joiner's catch-up: the doors are shut by default, so only open ones are told.</summary>
    public void SendOnJoin(ServerClient c)
    {
        foreach (var (_, bay) in _bays.Values)
            if (bay.HasDoors && bay.Doors.IsOpen) _ctx.SendEvent(c, EventKind.CargoDoors, CargoDoorsCodec.Encode(bay.Vehicle, true));
    }
}
