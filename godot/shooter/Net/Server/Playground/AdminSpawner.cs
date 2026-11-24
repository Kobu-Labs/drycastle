using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.Shooter.Net.Server.Playground;

/// <summary>
/// The vehicles a player asks for by hand: the sandbox panel (unlimited) and the admin's <c>/spawn</c> (capped per server run). One appears a few metres in front of the player, facing the
/// way they look, built as the map's cars are.
/// </summary>
internal sealed class AdminSpawner
{
    /// <summary>Vehicles spawned per server run (sandbox + admin). Only the admin's /spawn is capped; the sandbox is unlimited.</summary>
    const int AdminVehicleCap = 32;

    readonly ServerContext _ctx;
    readonly Func<VehiclesFile?> _vehicles;
    readonly Fleet _fleet;
    readonly Node3D _world;
    readonly Water _water;
    readonly Func<FlightBounds> _flightBounds;
    readonly Random _rng;
    int _spawned;

    public AdminSpawner(ServerContext ctx, Func<VehiclesFile?> vehicles, Fleet fleet, Node3D world, Water water, Func<FlightBounds> flightBounds, Random rng)
    {
        _ctx = ctx; _vehicles = vehicles; _fleet = fleet; _world = world; _water = water; _flightBounds = flightBounds; _rng = rng;
    }

    /// <summary>"/spawn vehicle": the named vehicle in front of the admin, alive only, at most <see cref="AdminVehicleCap"/> a run.</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        var id = args[0];
        var kind = _vehicles()?.Vehicles.FindIndex(v => v.Id == id) ?? -1;
        if (kind < 0) { _ctx.ChatTo(c, ChatLineKind.Error, $"Unknown vehicle '{id}'."); return; }
        if (_ctx.Match.Get(c.Id) is not { Life: PlayerLife.Alive }) { _ctx.ChatTo(c, ChatLineKind.Error, "You must be alive to spawn a vehicle."); return; }
        if (TrySpawn(c, (byte)kind, AdminVehicleCap) is { } why) { _ctx.ChatTo(c, ChatLineKind.Error, why); return; }
        _ctx.ChatTo(c, ChatLineKind.Info, $"Spawned a {id} in front of you.");
    }

    /// <summary>
    /// Spawns a vehicle in front of the player (the sandbox panel and the admin's /spawn). Returns why it did not, or null.
    /// <paramref name="cap"/> (null = none) bounds the vehicles spawned this run. <paramref name="kind"/> indexes vehicles.json.
    /// </summary>
    public string? TrySpawn(ServerClient c, byte kind, int? cap)
    {
        var vehicles = _vehicles();
        if (vehicles == null || kind >= vehicles.Vehicles.Count) return "Unknown vehicle.";
        if (c.Car != null) return "Leave the vehicle first.";
        if (cap is { } max && _spawned >= max) return $"The spawned vehicle limit ({max}) is reached until the server restarts.";
        _spawned++;
        var def = vehicles.Vehicles[kind];
        var yaw = c.Body.Yaw;
        var p = c.Body.GlobalPosition;
        var fwd = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var at = p + fwd * (def.HalfLength + 4f + (def.Flight?.RotorRadius ?? 0f)); // a helicopter stands further off: its rotor reaches past its nose
        var colour = VehicleColours.Pick(_rng, new List<byte>(), def.Variants);
        var car = new ServerCar
        {
            Name = $"{def.Id}{_fleet.Count}", Id = _fleet.NextId, Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, colour),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(at.X, at.Y + 0.02f, at.Z), OriginYaw = yaw,
            FlightWater = _water, FlightBounds = _flightBounds(),
        };
        _world.AddChild(car);
        _fleet.Add(car);
        GD.Print($"server: {c.Name} #{c.Id} spawned a {def.Id}");
        return null;
    }
}
