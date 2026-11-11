using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Shotgun pellet fan (server side). One trigger pull draws a seeded fan of pellets; each pellet is traced against the
/// current positions and the world, damage is summed per target and dealt once per target, and the fan goes out as one
/// <see cref="EventKind.PelletFan"/> event so clients can redraw the same pellets for FX.
/// </summary>
public partial class NetServer
{
    void FirePellets(Client c, Vector3 eye, float yaw, float pitch, WeaponDef w, byte weaponIdx)
    {
        var aim = HitMath.Forward(yaw, pitch);
        var seed = (uint)_rng.Next();
        var dirs = HitMath.PelletDirections(aim, w.PelletSpread, w.Pellets, seed);
        var range = w.Range > 0 ? w.Range : MaxShotRange;

        // world and vehicle stops per pellet (one physics ray each; player bodies are excluded)
        var walls = new List<float>(dirs.Length);
        var cars = new List<ServerCar?>(dirs.Length);
        var dirsG = new List<Vector3>(dirs.Length);
        foreach (var d in dirs)
        {
            var g = new Vector3(d.X, d.Y, d.Z);
            walls.Add(WorldHit(eye, g, range, out var car));
            cars.Add(car);
            dirsG.Add(g);
        }

        var targets = LiveTargets();
        var totals = HitMath.ResolvePellets(w, N(eye), dirs, range, walls, targets, c.Id);
        BroadcastEvent(EventKind.PelletFan, WeaponCodec.Encode(new PelletFanMsg(c.Id, weaponIdx, N(eye), aim, seed)));
        foreach (var (id, t) in totals) Deal(c.Id, weaponIdx, id, t.Damage, t.Head, false);

        // pellets that stopped on a car (and no player was closer) damage the car, summed per car
        if (Vehicles == null) return;
        var carDamage = new Dictionary<ServerCar, float>();
        for (var i = 0; i < dirs.Length; i++)
        {
            if (cars[i] is not { } car || walls[i] > range) continue;
            if (HitMath.Raycast(N(eye), dirs[i], range, walls[i], targets, c.Id) != null) continue;
            carDamage[car] = carDamage.GetValueOrDefault(car) + HitMath.PelletDamage(w, walls[i], false) * Vehicles.BulletDamageMultiplier;
        }
        foreach (var (car, amount) in carDamage) DamageVehicle(car, amount, c.Id, weaponIdx, 0);
    }
}
