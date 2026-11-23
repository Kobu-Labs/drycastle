using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>The cars the tire target steals from (<see cref="TireLootConfig.VehicleIds"/>): the server's vehicles as the pure <see cref="TireSource"/> sees them. The wheel bits live in the car's <see cref="VehicleLifecycle"/> and travel in the vehicle record.</summary>
internal sealed class TireCars : ITireCars
{
    readonly Fleet _fleet;
    readonly TireLootConfig _cfg;

    public TireCars(Fleet fleet, TireLootConfig cfg) { _fleet = fleet; _cfg = cfg; }

    TireCar? Of(ServerCar car)
    {
        if (!_cfg.VehicleIds.Contains(car.Def.Id)) return null;
        var speed = MathF.Abs(car.Drives ? car.Drive.Speed : car.State.Speed);
        var atRest = !car.Destroyed && !car.Life.Doomed && car.Thrown == null && !car.Seat.Occupied && speed <= _cfg.RestSpeedMps;
        return new TireCar(car.Id, car.GlobalPosition.ToNumerics(), car.State.Yaw, atRest, car.Life.WheelsOff);
    }

    public IEnumerable<TireCar> Cars()
    {
        foreach (var car in _fleet.Cars)
            if (Of(car) is { } t) yield return t;
    }

    public bool TryGet(int carId, out TireCar car)
    {
        car = default;
        if (_fleet.Find(carId) is not { } c || Of(c) is not { } t) return false;
        car = t;
        return true;
    }

    public void SetWheel(int carId, int wheel, bool off)
    {
        if (_fleet.Find(carId) is not { } c) return;
        if (off) c.Life.TakeWheel(wheel); else c.Life.ReturnWheel(wheel);
    }

    public void RestoreAll()
    {
        foreach (var car in _fleet.Cars)
            for (var w = 0; w < VehicleLifecycle.Wheels; w++) car.Life.ReturnWheel(w);
    }
}
