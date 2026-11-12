using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The utility helicopter's door gun on the server (<see cref="DoorGunRules"/>). The gunner's view is what the gun is asked to point at; the gun
/// points there as far as its arc allows, and a round goes where the gun points: hitscan from the muzzle by the vehicle gun's rules (the jeep's
/// magazine, reserve, reload and spread, <see cref="VehicleGun"/>), so its kills are ordinary kills. The aim goes to every client in a turret
/// record, as the flak's does, here as the mount's yaw and the gun's pitch against the airframe; the gunner's ammo goes to the gunner.
/// </summary>
public partial class NetServer
{
    int _doorGunShots;

    /// <summary>The attitude a door gun's arc turns with: the helicopter's; a land vehicle's yaw alone.</summary>
    static (float Yaw, float Pitch, float Roll) AttitudeOf(ServerCar car) =>
        car.Flies ? (car.Flight.Yaw, car.Flight.Pitch, car.Flight.Roll) : (car.State.Yaw, 0f, 0f);

    /// <summary>The door gun's record for the snapshot: the mount's yaw and the gun's pitch against the vehicle, and the gunner's id. With nobody at it, the gun stays where it was left.</summary>
    EntityRecord DoorGunRecordOf(ServerCar car)
    {
        var seat = car.Def.Seats.FindIndex(s => s.Role == "gunner");
        var gunner = seat >= 0 ? car.Seat.Occupant(seat) : (ushort)0;
        return FlakRecord.Make(car.Id, gunner, car.TurretLocalYaw, car.GunPitch);
    }

    /// <summary>The gunner's input: the view aims the gun inside its arc, Reload reloads, Fire fires a round per interval while the magazine lasts.</summary>
    void DoorGunnerInput(Client c, ServerCar car, VehicleDoorGunDef def, NetInput i)
    {
        var (yaw, pitch, roll) = AttitudeOf(car);
        var aim = DoorGunRules.Aim(def, yaw, pitch, roll, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        car.TurretLocalYaw = aim.Yaw;
        car.GunPitch = aim.Pitch;
        var gun = car.Gun!;
        if ((i.Buttons & (ushort)MoveButtons.Reload) != 0) gun.TryReload(_elapsed);
        if (gun.TryFire(_elapsed, (i.Buttons & (ushort)MoveButtons.Fire) != 0)) FireDoorGun(c, car, def, gun.Def, aim);
        var status = (gun.Magazine, gun.Reserve, gun.Reloading);
        if (c.FlakSent != status)
        {
            c.FlakSent = status;
            Send(c, new EventMsg((byte)EventKind.FlakStatus, FlakCodec.Encode(new FlakStatus((ushort)gun.Magazine, (ushort)gun.Reserve, gun.ReloadRemaining(_elapsed)))));
        }
    }

    /// <summary>
    /// One round: from the muzzle along the gun with the gun's spread. The helicopter's own hull does not stop it (the arc keeps the gun clear of
    /// the airframe; the hit box is a plain box around it) and its riders are not targets. Everything else is a rifle's shot: a body takes the
    /// damage, a vehicle the damage times the bullet multiplier, and a gunner sitting in the open on another vehicle is hit through its hull.
    /// </summary>
    void FireDoorGun(Client c, ServerCar car, VehicleDoorGunDef def, VehicleGunDef gun, DoorGunAim aim)
    {
        var (yaw, pitch, roll) = AttitudeOf(car);
        var muzzle = DoorGunRules.Muzzle(def, DoorGunRules.Pivot(def, N(car.GlobalPosition), yaw, pitch, roll), aim);
        var d = HitMath.SpreadDirection(aim.ViewYaw, aim.ViewPitch, gun.SpreadDegrees, _rng);
        var from = new Vector3(muzzle.X, muzzle.Y, muzzle.Z);
        var dir = new Vector3(d.X, d.Y, d.Z);
        // the world without the gunner's own vehicle
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * gun.Range);
        q.CollisionMask = CollisionLayers.Bullets;
        q.Exclude = new Godot.Collections.Array<Rid>(PlayerRids()) { car.GetRid() };
        var r = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var wall = r.Count == 0 ? float.PositiveInfinity : from.DistanceTo(r["position"].AsVector3());
        var shotCar = r.Count == 0 ? null : r["collider"].As<GodotObject>() as ServerCar;
        var targets = LiveTargets().Where(t => car.Seat.SeatOf(t.Id) < 0).ToList();
        var (exposedGunner, pastHull) = ExposedGunnerBehind(shotCar, from, dir, gun.Range, targets);
        var hit = GunnerCover.Raycast(muzzle, d, gun.Range, wall, targets, c.Id, exposedGunner, pastHull);
        var end = from + dir * (hit?.Distance ?? Math.Min(wall, gun.Range));
        _doorGunShots++;
        BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, SpecialWeapons.VehicleGun, muzzle, N(end))));
        if (hit is { } h) Deal(c.Id, SpecialWeapons.VehicleGun, h.Id, gun.Damage, false, false);
        else if (shotCar != null && Vehicles != null && wall <= gun.Range)
            DamageVehicle(shotCar, gun.Damage * Vehicles.BulletDamageMultiplier, c.Id, SpecialWeapons.VehicleGun, 0);
        else if (shotCar == null && wall <= gun.Range) BulletPuff(N(end)); // dust where the round meets the ground or a wall
        if (hit == null && wall <= gun.Range) ShotGlass(shotCar, from, dir, wall, gun.Damage);
    }
}
