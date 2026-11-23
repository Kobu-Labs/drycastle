using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.BigfootEvent;

/// <summary>The server's side of <see cref="IBigfootWorld"/>: terrain and solids by the physics space, the players and vehicles by the tick's lists, and the hands that hurt, hold, throw and show.</summary>
internal sealed class BigfootEnv : IBigfootWorld
{
    readonly ServerContext _ctx;
    readonly BigfootFile _data;
    readonly BigfootTerrain _terrain;
    readonly Fleet _fleet;
    readonly DealDamage _deal;
    readonly Random _rng;
    readonly List<BigfootPlayer> _players;
    readonly List<BigfootVehicle> _vehicles;

    /// <param name="players">The living players and the throwable vehicles of this tick: the server refills them once a tick before the entities tick.</param>
    public BigfootEnv(ServerContext ctx, BigfootFile data, BigfootTerrain terrain, Fleet fleet, DealDamage deal, Random rng, List<BigfootPlayer> players, List<BigfootVehicle> vehicles)
    {
        _ctx = ctx; _data = data; _terrain = terrain; _fleet = fleet; _deal = deal; _rng = rng; _players = players; _vehicles = vehicles;
    }

    public IReadOnlyList<BigfootPlayer> Players => _players;
    public IReadOnlyList<BigfootVehicle> Vehicles => _vehicles;
    public float? GroundY(float x, float z, float nearY) => _terrain.Ground(x, z, nearY);
    public bool Blocked(NVec3 from, NVec3 to, float radius) => _terrain.Blocked(from, to, radius);

    public void SpawnTrunk(NVec3 from, NVec3 velocity) =>
        _ctx.Match.Entities.Spawn(new TrunkEntity(_data, this, from, velocity, _rng.Next()));

    public void Hurt(ushort player, float damage, BigfootHurt how)
    {
        if (!_ctx.Players.ContainsKey(player)) return;
        _deal(BigfootRules.TargetId, SpecialWeapons.Bigfoot, player, damage, false, false);
    }

    public void Knock(ushort player, NVec3 velocity)
    {
        if (_ctx.Players.TryGetValue(player, out var c) && c.Car == null) c.Body.AddImpulse(new Vector3(velocity.X, velocity.Y, velocity.Z));
    }

    public void Hold(ushort player, NVec3 at)
    {
        if (!_ctx.Players.TryGetValue(player, out var c) || c.Car != null) return;
        c.HeldUntilTick = _ctx.Tick + 1; // this tick's inputs and the next's are skipped: the Bigfoot puts the body here again every tick
        c.Body.Teleport(new Vector3(at.X, at.Y, at.Z));
        _ctx.Match.SetPosition(player, at);
    }

    public void Launch(ushort player, NVec3 velocity, float graceSeconds)
    {
        if (!_ctx.Players.TryGetValue(player, out var c)) return;
        c.HeldUntilTick = 0; // let go
        if (velocity == NVec3.Zero) return;
        c.LaunchGraceUntil = _ctx.Now + graceSeconds; c.LaunchedAt = _ctx.Now;
        c.Body.AddImpulse(new Vector3(velocity.X, velocity.Y, velocity.Z));
    }

    public void LiftVehicle(ushort vehicle, NVec3 hold, float seconds)
    {
        var car = _fleet.Find(vehicle);
        if (car == null || car.Destroyed || car.Thrown != null) return;
        car.Thrown = new VehicleThrow(ToN(car.GlobalPosition), car.State.Yaw, hold, seconds);
        car.Wake();
        car.HitCooldown.Clear();
    }

    public void DropVehicle(ushort vehicle)
    {
        if (_fleet.Find(vehicle) is { Destroyed: false, Thrown: { Released: false } lifted }) lifted.PutDown();
    }

    public void ThrowVehicle(ushort vehicle, NVec3 landing, float seconds, float spinRadPerS)
    {
        var car = _fleet.Find(vehicle);
        if (car is { Destroyed: false, Thrown: { Released: false } lifted }) { lifted.Release(landing, seconds, spinRadPerS); return; } // from over its head
        if (car == null || car.Destroyed || car.Thrown != null) return;
        car.Thrown = new VehicleThrow(ToN(car.GlobalPosition), car.State.Yaw, landing, seconds, spinRadPerS);
        car.Wake();
        car.HitCooldown.Clear();
    }

    public void Fx(BigfootFxKind kind, NVec3 at, ushort id = 0, byte extra = 0) =>
        _ctx.Broadcast(EventKind.BigfootFx, BigfootFxCodec.Encode(new BigfootFxMsg(kind, at, id, extra)));

    static NVec3 ToN(Vector3 v) => new(v.X, v.Y, v.Z);
}
