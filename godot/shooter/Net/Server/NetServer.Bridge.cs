using Godot;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>Temporary: the converted shot world, glass and pose rewind under their old names, until the partials that ask them are classes too.</summary>
public partial class NetServer
{
    WorldQuery Query => _shot.Query;
    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);
    float WorldDistance(Vector3 from, Vector3 dir, float max) => _shot.WorldDistance(from, dir, max);
    float WorldHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ServerCar? skip = null) => _shot.WorldHit(from, dir, max, out car, skip);
    float BulletHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ref List<GlassCross>? crossed, ServerCar? ignore = null, ServerCar? solid = null) => _shot.BulletHit(from, dir, max, out car, ref crossed, ignore, solid);
    (ExposedRiders? Gunner, float PastHull) ExposedGunnerBehind(ServerCar? hull, Vector3 from, Vector3 dir, float max, IReadOnlyList<HitTarget> targets, bool seatsOnly = false, ServerCar? skip = null) => _shot.ExposedGunnerBehind(hull, from, dir, max, targets, seatsOnly, skip);
    bool IsArmouredRider(HitTarget t) => _shot.IsArmouredRider(t);
    static Vector3? TargetFeet(List<HitTarget> targets, ushort id) => ShotWorld.FeetIn(targets, id);
    List<HitTarget> ShotTargets(ServerClient c) => _shot.TargetsFor(c);
    static VehiclePose PresentPose(ServerCar car) => CarPose.Present(car);
    static float HullDistanceNow(ServerCar car, NVec3 at) => CarPose.HullDistanceNow(car, at);
    static Vector3 SeatPos(ServerCar car, int seat) => CarPose.SeatPos(car, seat);
    static Vector3 SeatPosAt(ServerCar car, int seat, VehiclePose pose) => CarPose.SeatPosAt(car, seat, pose);
    static NVec3 CarVelocity(ServerCar car) => CarPose.Velocity(car);
    FlightBounds MapFlightBounds => _fleet.FlightBounds;
    const float RunOverCooldown = RunOverServer.Cooldown;
    void DamageVehicle(ServerCar car, float amount, ushort attacker, byte weapon, float delay) => _carDamage.Damage(car, amount, attacker, weapon, delay);
    void Crash(ServerCar car, float speedLoss, ushort attacker) => _carDamage.Crash(car, speedLoss, attacker);
    void LeaveSeat(ServerClient c, bool placeOnGround) => _seats.Leave(c, placeOnGround);
    ServerCar? DetachFromCar(ServerClient c) => _seats.Detach(c);
    void ExitCar(ServerClient c) => _seats.Exit(c);
    bool TryEnterCar(ServerClient c) => _seats.TryEnter(c);
    bool TowUseKey(ServerClient c, ServerCar car, bool useDown, bool useEdge, float dt) => TowServer.UseKey(c, car, useDown, useEdge, dt);
    void RunOverTargets(in RunOverSweep m, Func<ushort, bool>? spare = null) => _runOver.Sweep(m, spare);
    public ServerCar SpawnRocketCart(NVec3 position, float yaw, bool loaded = true) => _rocketCarts.Spawn(position, yaw, loaded);
    public int RemoveRocketCarts() => _rocketCarts.RemoveAll();
    public bool SetRocketCartLoaded(ServerCar cart, bool loaded) => RocketCarts.SetLoaded(cart, loaded);
    public List<ServerCar> LoadedRocketCartsNear(NVec3 point, float radius) => _rocketCarts.LoadedNear(point, radius);
    void SpawnCorpse(ushort victim) => _remains.Spawn(victim);
    void RiderDies(ServerClient c) => _remains.RiderDies(c);
    EntityRecord DoorGunRecordOf(ServerCar car) => VehicleGuns.DoorGunRecordOf(car);
    void GunnerInput(ServerClient c, NetInput i) => _guns.Input(c, i);
    void ResetCars() { _loop.RoundReset(); _scenery.RoundStart(); }
    void RocketVsVehicles(Vector3 pos, WeaponDef w, ushort owner, byte weapon, ServerCar? direct) => _carDamage.Rockets(pos, w, owner, weapon, direct);
    ServerCar? EnterableCar(Vector3 p) => _seats.EnterableCar(p);
    void ShotGlass(ServerCar? car, Vector3 from, Vector3 dir, float wall, float damage) => _glass.Shot(car, from, dir, wall, damage);
    void DamageCrossed(List<GlassCross>? crossed, float before, Func<float, float> damage) => _glass.DamageCrossed(crossed, before, damage);
    void BlastGlass(Vector3 at, float radius, float? centreDamage = null) => _glass.Blast(at, radius, centreDamage);
    void CrashGlass(ServerCar car, float speedLoss) => _glass.Crash(car, speedLoss);
    void FlushGlass() => _glass.Flush();
    void SendGlassOnJoin(ServerClient c) => _glass.SendOnJoin(c);
    void RestoreVehicleGlass(ServerCar car) => _glass.RestoreCar(car);
    void RestoreGlassForRound() => _glass.RestoreForRound();
}
