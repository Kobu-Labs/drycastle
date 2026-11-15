using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Shotgun pellet fan (server side). One trigger pull draws a seeded fan of pellets; each pellet is traced against the
/// current positions and the world, damage is summed per target and dealt once per target, and the fan goes out as one
/// <see cref="EventKind.PelletFan"/> event so clients can redraw the same pellets for FX. The fan half-angle is the
/// pellet spread after stance, aim and movement (<see cref="HitMath.ShotSpread(float, WeaponDef, in MoveState, bool)"/>) and travels in the event.
/// </summary>
public partial class NetServer
{
    /// <param name="aimed">The targets the fan can hit (null: every live one). A rider firing from an armed seat passes everyone not aboard their vehicle.</param>
    /// <param name="seatCar">The vehicle the shooter rides, whose hull is not in the pellets' way (null on foot).</param>
    void FirePellets(Client c, Vector3 eye, float yaw, float pitch, WeaponDef w, byte weaponIdx, float cone, List<HitTarget>? aimed = null, ServerCar? seatCar = null)
    {
        var aim = HitMath.Forward(yaw, pitch);
        var seed = (uint)_rng.Next();
        var dirs = HitMath.PelletDirections(aim, cone, w.Pellets, seed);
        var range = w.Range > 0 ? w.Range : MaxShotRange;

        // world and vehicle stops per pellet (one physics ray each; player bodies are excluded)
        var targets = aimed ?? LiveTargets();
        var walls = new List<float>(dirs.Length);
        var cars = new List<ServerCar?>(dirs.Length);
        var dirsG = new List<Vector3>(dirs.Length);
        var crossedBy = new List<List<GlassCross>?>(dirs.Length); // the panes each pellet went through (null: none)
        var exposed = new List<ExposedRiders?>(dirs.Length); // a pellet that stopped on a vehicle with riders in the open may reach one of them (GunnerCover)
        var pastHull = new List<float>(dirs.Length);
        foreach (var d in dirs)
        {
            var g = new Vector3(d.X, d.Y, d.Z);
            List<GlassCross>? crossed = null;
            walls.Add(BulletHit(eye, g, range, out var car, ref crossed, seatCar));
            crossedBy.Add(crossed);
            cars.Add(car);
            dirsG.Add(g);
            var (gunner, past) = ExposedGunnerBehind(car, eye, g, range, targets, skip: seatCar);
            exposed.Add(gunner);
            pastHull.Add(past);
        }

        var totals = HitMath.ResolvePelletsPastHulls(w, N(eye), dirs, range, walls, targets, c.Id, exposed, pastHull);
        BroadcastEvent(EventKind.PelletFan, WeaponCodec.Encode(new PelletFanMsg(c.Id, weaponIdx, N(eye), aim, seed, cone)));
        foreach (var (id, t) in totals) Deal(c.Id, weaponIdx, id, t.Damage, t.Head, false);

        // a pellet that hit no player damages the pane it crossed where it stopped (ADR 0007); the changes of a tick go out as one event per pane
        for (var i = 0; i < dirs.Length; i++)
        {
            var pelletHit = GunnerCover.Raycast(N(eye), dirs[i], range, walls[i], targets, c.Id, exposed[i], pastHull[i]);
            DamageCrossed(crossedBy[i], pelletHit?.Distance ?? float.PositiveInfinity, d => HitMath.PelletDamage(w, d, false));
            if (walls[i] > range || pelletHit != null) continue;
            ShotGlass(cars[i], eye, dirsG[i], walls[i], HitMath.PelletDamage(w, walls[i], false));
        }

        // pellets that stopped on a car (and no player was closer) damage the car, summed per car
        if (Vehicles == null) return;
        var carDamage = new Dictionary<ServerCar, float>();
        for (var i = 0; i < dirs.Length; i++)
        {
            if (cars[i] is not { } car || walls[i] > range) continue;
            if (GunnerCover.Raycast(N(eye), dirs[i], range, walls[i], targets, c.Id, exposed[i], pastHull[i]) != null) continue;
            carDamage[car] = carDamage.GetValueOrDefault(car) + HitMath.PelletDamage(w, walls[i], false) * Vehicles.BulletDamageMultiplier;
        }
        foreach (var (car, amount) in carDamage) DamageVehicle(car, amount, c.Id, weaponIdx, 0);
    }
}
