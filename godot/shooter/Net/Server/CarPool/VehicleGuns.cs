using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Net.Server.Scenery;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// The guns a rider works from a vehicle's gunner seat: the car's roof gun (<see cref="VehicleGun"/>), the flak truck's (<see cref="FlakServer"/>) and the utility helicopter's door gun (<see cref="DoorGunRules"/>). The gunner's
/// view is what the gun is asked to point at; a door gun points there as far as its arc allows, and a round goes where the gun points: hitscan from the muzzle with the gun's spread, so its kills are ordinary kills
/// (no roadkill: that is for the driver's run-over). The car's own riders are not targets.
/// </summary>
internal sealed class VehicleGuns
{
    readonly ServerContext _ctx;
    readonly PoseRewind _rewind;
    readonly ShotWorld _shot;
    readonly GlassServer _glass;
    readonly SceneryServer _scenery;
    readonly FlakServer _flak;
    readonly TargetBook _targets;
    readonly ImpactFx _impacts;
    readonly DealDamage _deal;
    readonly CarDamage _damage;
    readonly VehiclesFile _vehicles;
    readonly Random _rng;

    public VehicleGuns(ServerContext ctx, PoseRewind rewind, ShotWorld shot, GlassServer glass, SceneryServer scenery, FlakServer flak, TargetBook targets, ImpactFx impacts, DealDamage deal, CarDamage damage, VehiclesFile vehicles, Random rng)
    {
        _ctx = ctx; _rewind = rewind; _shot = shot; _glass = glass; _scenery = scenery; _flak = flak; _targets = targets; _impacts = impacts; _deal = deal; _damage = damage; _vehicles = vehicles; _rng = rng;
    }

    /// <summary>A gunner's inputs: reload when asked, then one round when the trigger is held and the gun is ready.</summary>
    public void Input(ServerClient c, NetInput i)
    {
        if (c.Car!.Def.Flak is { } flak) { _flak.OnGunnerInput(c, c.Car, flak, i); return; }
        if (c.Car.Def.DoorGun is { } doorGun) { DoorGunInput(c, c.Car, doorGun, i); return; }
        var gun = c.Car!.Gun!;
        if ((i.Buttons & (ushort)MoveButtons.Reload) != 0) gun.TryReload(_ctx.Now);
        if (gun.TryFire(_ctx.Now, (i.Buttons & (ushort)MoveButtons.Fire) != 0)) FireRoofGun(c, c.Car, i, gun.Def);
    }

    /// <summary>The door gun's record for the snapshot: the mount's yaw and the gun's pitch against the vehicle, and the gunner's id. With nobody at it, the gun stays where it was left.</summary>
    public static EntityRecord DoorGunRecordOf(ServerCar car)
    {
        var seat = car.Def.Seats.FindIndex(s => s.Role == "gunner");
        var gunner = seat >= 0 ? car.Seat.Occupant(seat) : (ushort)0;
        return FlakRecord.Make(car.Id, gunner, car.TurretLocalYaw, car.GunPitch);
    }

    /// <summary>One round of a car's gun from a gunner's seat: hitscan along the gunner's view with spread. The gunner gets the kill.</summary>
    void FireRoofGun(ServerClient c, ServerCar car, NetInput i, VehicleGunDef def)
    {
        _rewind.Look(c); // the other vehicles, where the gunner's screen showed them (B-02)
        var eye = CarPose.SeatPos(car, c.CarSeat) + Vector3.Up * 1.4f;
        var yaw = Quant.Yaw(i.Yaw); var pitch = Quant.Pitch(i.Pitch);
        var d = HitMath.SpreadDirection(yaw, RecoilState.ClampPitch(pitch), def.SpreadDegrees, _rng);
        var dir = new Vector3(d.X, d.Y, d.Z);
        List<GlassCross>? crossed = null;
        var wall = _shot.BulletHit(eye, dir, def.Range, out var shotCar, ref crossed, solid: car); // the gunner's own car stops the round as before: its glass is not shot out from the roof
        var targets = _targets.Live().Where(t => car.Seat.SeatOf(t.Id) < 0 && !_shot.IsArmouredRider(t)).ToList();
        // riders whose seat is in the open (an amphibious transport's) are reached through their hull, as by every weapon
        var (riders, pastHull) = _shot.ExposedGunnerBehind(shotCar == car ? null : shotCar, eye, dir, def.Range, targets, seatsOnly: true);
        var hit = GunnerCover.Raycast(eye.ToNumerics(), d, def.Range, wall, targets, c.Id, riders, pastHull);
        var end = eye + dir * (hit?.Distance ?? Math.Min(wall, def.Range));
        _ctx.Broadcast(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, SpecialWeapons.VehicleGun, eye.ToNumerics(), end.ToNumerics())));
        if (hit is { } h) _deal(c.Id, SpecialWeapons.VehicleGun, h.Id, def.Damage, false, false);
        else if (shotCar != null && shotCar != car && wall <= def.Range)
            _damage.Damage(shotCar, def.Damage * _vehicles.BulletDamageMultiplier, c.Id, SpecialWeapons.VehicleGun, 0);
        if (hit == null && shotCar != car && wall <= def.Range) _glass.Shot(shotCar, eye, dir, wall, def.Damage);
        _glass.DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, _ => def.Damage);
        _scenery.Shot(eye.ToNumerics(), dir.ToNumerics(), hit?.Distance ?? Math.Min(wall, def.Range)); // a pole lamp the round passed breaks (ADR 0037)
        _rewind.LookNow();
    }

    /// <summary>The gunner's input: the view aims the gun inside its arc, Reload reloads, Fire fires a round per interval while the magazine lasts.</summary>
    void DoorGunInput(ServerClient c, ServerCar car, VehicleDoorGunDef def, NetInput i)
    {
        var (yaw, pitch, roll) = Fleet.AttitudeOf(car);
        var aim = DoorGunRules.Aim(def, yaw, pitch, roll, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        car.TurretLocalYaw = aim.Yaw;
        car.GunPitch = aim.Pitch;
        var gun = car.Gun!;
        if ((i.Buttons & (ushort)MoveButtons.Reload) != 0) gun.TryReload(_ctx.Now);
        if (gun.TryFire(_ctx.Now, (i.Buttons & (ushort)MoveButtons.Fire) != 0)) FireDoorGun(c, car, def, gun.Def, aim);
        var status = (gun.Magazine, gun.Reserve, gun.Reloading);
        if (c.FlakSent != status)
        {
            c.FlakSent = status;
            _ctx.SendEvent(c, EventKind.FlakStatus, FlakCodec.Encode(new FlakStatus((ushort)gun.Magazine, (ushort)gun.Reserve, gun.ReloadRemaining(_ctx.Now))));
        }
    }

    /// <summary>
    /// One round: from the muzzle along the gun with the gun's spread. The helicopter's own hull does not stop it (the arc keeps the gun clear of
    /// the airframe; the hit box is a plain box around it) and its riders are not targets. Everything else is a rifle's shot: a body takes the
    /// damage, a vehicle the damage times the bullet multiplier, and a gunner sitting in the open on another vehicle is hit through its hull.
    /// </summary>
    void FireDoorGun(ServerClient c, ServerCar car, VehicleDoorGunDef def, VehicleGunDef gun, DoorGunAim aim)
    {
        _rewind.Look(c); // the other vehicles, where the gunner's screen showed them (B-02)
        var (yaw, pitch, roll) = Fleet.AttitudeOf(car);
        var muzzle = DoorGunRules.Muzzle(def, DoorGunRules.GunPivot(def, car.GlobalPosition.ToNumerics(), yaw, pitch, roll, aim), aim);
        var d = HitMath.SpreadDirection(aim.ViewYaw, aim.ViewPitch, gun.SpreadDegrees, _rng);
        var from = new Vector3(muzzle.X, muzzle.Y, muzzle.Z);
        var dir = new Vector3(d.X, d.Y, d.Z);
        // the world without the gunner's own vehicle
        List<GlassCross>? crossed = null;
        var wall = _shot.BulletHit(from, dir, gun.Range, out var shotCar, ref crossed, car);
        var targets = _targets.Live().Where(t => car.Seat.SeatOf(t.Id) < 0 && !_shot.IsArmouredRider(t)).ToList();
        var (exposedGunner, pastHull) = _shot.ExposedGunnerBehind(shotCar, from, dir, gun.Range, targets);
        var hit = GunnerCover.Raycast(muzzle, d, gun.Range, wall, targets, c.Id, exposedGunner, pastHull);
        var end = from + dir * (hit?.Distance ?? Math.Min(wall, gun.Range));
        _ctx.Broadcast(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, SpecialWeapons.VehicleGun, muzzle, end.ToNumerics())));
        if (hit is { } h) _deal(c.Id, SpecialWeapons.VehicleGun, h.Id, gun.Damage, false, false);
        else if (shotCar != null && wall <= gun.Range)
            _damage.Damage(shotCar, gun.Damage * _vehicles.BulletDamageMultiplier, c.Id, SpecialWeapons.VehicleGun, 0);
        else if (shotCar == null && wall <= gun.Range) _impacts.BulletPuff(end.ToNumerics()); // dust where the round meets the ground or a wall
        if (hit == null && wall <= gun.Range) _glass.Shot(shotCar, from, dir, wall, gun.Damage);
        _glass.DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, _ => gun.Damage);
        _scenery.Shot(from.ToNumerics(), dir.ToNumerics(), hit?.Distance ?? Math.Min(wall, gun.Range)); // a pole lamp the round passed breaks (ADR 0037)
        _rewind.LookNow();
    }
}
