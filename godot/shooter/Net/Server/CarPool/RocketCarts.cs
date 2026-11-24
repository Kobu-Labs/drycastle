using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>The rocket carts the Scud event brings (a trailer with a load): where they stand, whether they carry their missile, and what is within reach of a launcher truck.</summary>
internal sealed class RocketCarts
{
    readonly Fleet _fleet;
    readonly Func<WorldQuery> _query;
    readonly VehiclesFile _vehicles;

    public RocketCarts(Fleet fleet, Func<WorldQuery> query, VehiclesFile vehicles) { _fleet = fleet; _query = query; _vehicles = vehicles; }

    /// <summary>
    /// Spawns a rocket cart standing at <paramref name="position"/> (its origin on the ground there: the ground under it is found, the given height is only where to look from), facing <paramref name="yaw"/>
    /// (Godot's: 0 faces -Z, positive turns left), loaded unless told otherwise. It is a vehicle like any other (in the list, in the snapshot, hit, run over by) and returns to this place, loaded, when it is destroyed.
    /// </summary>
    public ServerCar Spawn(NVec3 position, float yaw, bool loaded = true)
    {
        var kind = _vehicles.Vehicles.FindIndex(v => v.Trailer is { HasCargo: true });
        if (kind < 0) throw new InvalidOperationException("vehicles.json has no rocket cart (a trailer with a loaded_hp)");
        var ground = _query().GroundY(MoverKind.GroundVehicle, new Vector3(position.X, position.Y + 2f, position.Z), 3f, 12f) ?? position.Y;
        var car = _fleet.Make(kind, _vehicles.Vehicles[kind], 0, new Vector3(position.X, ground + 0.02f, position.Z), yaw);
        car.SetLoaded(loaded);
        GD.Print($"server: rocket cart {car.Id} spawned at ({position.X:F1},{ground:F1},{position.Z:F1}), {(loaded ? "loaded" : "empty")}");
        return car;
    }

    /// <summary>Removes every rocket cart from the world for good, hitched or not, loaded or not (<see cref="ServerCar.Retire"/>: clients drop them from the next snapshot). Returns how many there were.</summary>
    public int RemoveAll()
    {
        var n = 0;
        foreach (var car in _fleet.Cars)
        {
            if (car.Cargo == null || car.Retired) continue;
            car.Retire();
            n++;
        }
        if (n > 0) GD.Print($"server: {n} rocket cart(s) removed");
        return n;
    }

    /// <summary>
    /// Puts the missile on a rocket cart or takes it off (<see cref="ServerCar.SetLoaded"/>): its hit points follow, keeping their share, and every client sees the missile appear or go. False when the cart is
    /// gone (destroyed or removed) or already as asked.
    /// </summary>
    public static bool SetLoaded(ServerCar cart, bool loaded) => !cart.Destroyed && cart.SetLoaded(loaded);

    /// <summary>The loaded, live rocket carts within <paramref name="radius"/> metres of <paramref name="point"/> (straight-line distance from the cart's origin), nearest first.</summary>
    public List<ServerCar> LoadedNear(NVec3 point, float radius)
    {
        var found = new List<(float D, ServerCar Car)>();
        foreach (var car in _fleet.Cars)
        {
            if (car.Cargo is not { Loaded: true } || car.Destroyed) continue;
            var d = NVec3.Distance(car.GlobalPosition.ToNumerics(), point);
            if (d <= radius) found.Add((d, car));
        }
        found.Sort((a, b) => a.D.CompareTo(b.D));
        return found.Select(f => f.Car).ToList();
    }
}
