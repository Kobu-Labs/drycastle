using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The <c>loot.tire</c> sub-section of thieves.json. Tires are taken off the cars listed in <see cref="VehicleIds"/> (the ones drawn with the Kenney sedan, whose wheels are nodes
/// <c>wheel-front-left</c>, <c>wheel-front-right</c>, <c>wheel-back-left</c>, <c>wheel-back-right</c>): the wheel centres are <see cref="WheelSideM"/> either side of the car's middle and
/// <see cref="WheelEndM"/> ahead of and behind it (the sedan's wheels at the game's scale of 1.7), the Thief kneels <see cref="WorkSideM"/> out from the middle beside the wheel. A car counts as at rest
/// when nobody sits in it and it moves slower than <see cref="RestSpeedMps"/>.
/// </summary>
public sealed class TireLootConfig : LootKindConfig
{
    public TireLootConfig() { StealSeconds = 6f; }

    public List<string> VehicleIds { get; set; } = new() { "car" };
    public float WheelSideM { get; set; } = 0.72f;
    public float WheelEndM { get; set; } = 1.12f;
    public float WorkSideM { get; set; } = 1.9f;
    public float RestSpeedMps { get; set; } = 0.5f;
    public override void Validate(string file, string kind)
    {
        base.Validate(file, kind);
        if (!(WheelSideM > 0) || !(WheelEndM > 0) || !(WorkSideM > WheelSideM) || !(RestSpeedMps >= 0)) throw new DataFileException($"{file}: loot.tire wheel_side_m and wheel_end_m must be positive, work_side_m beyond wheel_side_m, rest_speed_mps not negative");
    }
}

/// <summary>A car as the tire target sees it: its id (the vehicle record's), where it stands, its yaw (Godot convention), whether it is at rest and empty, and the wheels taken.</summary>
public readonly record struct TireCar(int Id, Vector3 Position, float Yaw, bool AtRest, byte WheelsOff);

/// <summary>The cars the tires are taken from (the server's adapter over its vehicles). Only cars of the types in <see cref="TireLootConfig.VehicleIds"/> are listed.</summary>
public interface ITireCars
{
    IEnumerable<TireCar> Cars();
    bool TryGet(int carId, out TireCar car);
    /// <summary>Sets or clears one wheel's bit (<see cref="VehicleLifecycle.TakeWheel"/>).</summary>
    void SetWheel(int carId, int wheel, bool off);
    /// <summary>A new round: every wheel back (the cars' own reset does it too; this makes the source complete by itself).</summary>
    void RestoreAll();
}

/// <summary>
/// The tire target (docs/events/thieves/04), the first kind of Loot: one target per wheel of a car at rest. Id = car id * 4 + wheel (0 front left, 1 front right, 2 back left, 3 back right). Taking it
/// sets the wheel's bit (<see cref="ITireCars.SetWheel"/>: Health bits 2-5 of the vehicle record, so everyone's client hides the wheel); returning or a new round clears it. The car drives exactly as
/// before. A car that is driven off, entered or destroyed is no longer a target (the Thief gives up).
/// </summary>
public sealed class TireSource : IStealSource
{
    readonly TireLootConfig _cfg;
    readonly ITireCars _cars;

    public TireSource(TireLootConfig cfg, ITireCars cars) { _cfg = cfg; _cars = cars; }

    public LootKind Kind => LootKind.Tire;

    public static int IdOf(int carId, int wheel) => carId * 4 + wheel;
    public static int CarOf(int id) => id >> 2;
    public static int WheelOf(int id) => id & 3;

    /// <summary>The wheel's centre in the world: wheel 0 and 1 are the front (the car faces -Z at yaw 0), 0 and 2 the left.</summary>
    public static Vector3 WheelAt(TireCar car, int wheel, float x, float z, float y)
    {
        var lx = (wheel & 1) == 0 ? -x : x;
        var lz = wheel < 2 ? -z : z;
        var right = new Vector3(MathF.Cos(car.Yaw), 0, -MathF.Sin(car.Yaw));
        var back = new Vector3(MathF.Sin(car.Yaw), 0, MathF.Cos(car.Yaw));
        return car.Position + right * lx + back * lz + new Vector3(0, y, 0);
    }

    StealTarget TargetOf(TireCar car, int wheel)
    {
        var at = WheelAt(car, wheel, _cfg.WheelSideM, _cfg.WheelEndM, 0);
        var work = WheelAt(car, wheel, _cfg.WorkSideM, _cfg.WheelEndM, 0);
        return new StealTarget(LootKind.Tire, IdOf(car.Id, wheel), at, work);
    }

    public void FreeNear(Vector3 near, float radius, List<StealTarget> into)
    {
        foreach (var car in _cars.Cars())
        {
            if (!car.AtRest) continue;
            for (var w = 0; w < 4; w++)
            {
                if ((car.WheelsOff & (1 << w)) != 0) continue;
                var t = TargetOf(car, w);
                var dx = t.Position.X - near.X; var dz = t.Position.Z - near.Z;
                if (dx * dx + dz * dz <= radius * radius) into.Add(t);
            }
        }
    }

    public void Places(List<Vector2> into)
    {
        foreach (var car in _cars.Cars())
        {
            if (!car.AtRest) continue;
            for (var w = 0; w < 4; w++)
            {
                if ((car.WheelsOff & (1 << w)) != 0) continue;
                var at = WheelAt(car, w, _cfg.WheelSideM, _cfg.WheelEndM, 0);
                into.Add(new Vector2(at.X, at.Z));
            }
        }
    }

    public bool TryFind(int id, out StealTarget target)
    {
        target = default;
        if (!_cars.TryGet(CarOf(id), out var car) || !car.AtRest || (car.WheelsOff & (1 << WheelOf(id))) != 0) return false;
        target = TargetOf(car, WheelOf(id));
        return true;
    }

    public void Take(int id) => _cars.SetWheel(CarOf(id), WheelOf(id), true);

    public void Return(int id) => _cars.SetWheel(CarOf(id), WheelOf(id), false);

    public void Reset() => _cars.RestoreAll();
}
