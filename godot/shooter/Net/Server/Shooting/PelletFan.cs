using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Scenery;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Shooting;

/// <summary>
/// Shotgun pellet fan (server side). One trigger pull draws a seeded fan of pellets; each pellet is traced against the
/// current positions and the world, damage is summed per target and dealt once per target, and the fan goes out as one
/// <see cref="EventKind.PelletFan"/> event so clients can redraw the same pellets for FX. The fan half-angle is the
/// pellet spread after stance, aim and movement (<see cref="HitMath.ShotSpread(float, WeaponDef, in MoveState, bool)"/>) and travels in the event.
/// </summary>
internal sealed class PelletFan
{
    readonly ServerContext _ctx;
    readonly ShotWorld _shot;
    readonly GlassServer _glass;
    readonly CarDamage _carDamage;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly SceneryServer _scenery;
    readonly WildlifeServer _wildlife;
    readonly VehiclesFile _vehicles;
    readonly Random _rng;
    readonly Action<NVec3, NVec3, float> _baleShot;

    public PelletFan(ServerContext ctx, ShotWorld shot, GlassServer glass, CarDamage carDamage, TargetBook targets, DealDamage deal, SceneryServer scenery, WildlifeServer wildlife, VehiclesFile vehicles, Random rng,
        Action<NVec3, NVec3, float> baleShot)
    {
        _ctx = ctx; _shot = shot; _glass = glass; _carDamage = carDamage; _targets = targets; _deal = deal; _scenery = scenery; _wildlife = wildlife; _vehicles = vehicles; _rng = rng; _baleShot = baleShot;
    }

    /// <param name="aimed">The targets the fan can hit (null: every live one). A rider firing from an armed seat passes everyone not aboard their vehicle.</param>
    /// <param name="seatCar">The vehicle the shooter rides, whose hull is not in the pellets' way (null on foot).</param>
    public void Fire(ServerClient c, Vector3 eye, float yaw, float pitch, WeaponDef w, byte weaponIdx, float cone, List<HitTarget>? aimed = null, ServerCar? seatCar = null)
    {
        var aim = HitMath.Forward(yaw, pitch);
        var seed = (uint)_rng.Next();
        var dirs = HitMath.PelletDirections(aim, cone, w.Pellets, seed);
        var range = w.Range > 0 ? w.Range : ShotServer.MaxRange;

        // world and vehicle stops per pellet (one physics ray each; player bodies are excluded)
        var targets = aimed ?? _targets.Live();
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
            walls.Add(_shot.BulletHit(eye, g, range, out var car, ref crossed, seatCar));
            crossedBy.Add(crossed);
            cars.Add(car);
            dirsG.Add(g);
            var (gunner, past) = _shot.ExposedGunnerBehind(car, eye, g, range, targets, skip: seatCar);
            exposed.Add(gunner);
            pastHull.Add(past);
        }

        var totals = HitMath.ResolvePelletsPastHulls(w, eye.ToNumerics(), dirs, range, walls, targets, c.Id, exposed, pastHull);
        _ctx.Broadcast(EventKind.PelletFan, WeaponCodec.Encode(new PelletFanMsg(c.Id, weaponIdx, eye.ToNumerics(), aim, seed, cone)));
        foreach (var (id, t) in totals)
        {
            // a wild animal's hit diagram: where the first pellet that hit it entered (taken before the hit can remove it)
            var shot = WildlifeRules.IsHitId(id) && t.First >= 0 ? _wildlife.HuntShotOf(targets, id, (eye + dirsG[t.First] * t.FirstDistance).ToNumerics(), t.FirstDistance, weaponIdx, t.FirstShapeHead) : null;
            _deal(c.Id, weaponIdx, id, t.Damage, t.Head, false, shot: shot, shotDistance: t.First >= 0 ? t.FirstDistance : null);
        }
        for (var i = 0; i < dirs.Length; i++) _baleShot(eye.ToNumerics(), dirs[i], Math.Min(walls[i], range)); // any bale a pellet passes catches fire (#331)

        // a pellet that hit no player damages the pane it crossed where it stopped (ADR 0007); the changes of a tick go out as one event per pane
        for (var i = 0; i < dirs.Length; i++)
        {
            var pelletHit = GunnerCover.Raycast(eye.ToNumerics(), dirs[i], range, walls[i], targets, c.Id, exposed[i], pastHull[i]);
            _glass.DamageCrossed(crossedBy[i], pelletHit?.Distance ?? float.PositiveInfinity, d => HitMath.PelletDamage(w, d, false));
            _scenery.Shot(eye.ToNumerics(), dirs[i], pelletHit?.Distance ?? Math.Min(walls[i], range)); // a pole lamp the pellet passed breaks (ADR 0037)
            if (walls[i] > range || pelletHit != null) continue;
            _glass.Shot(cars[i], eye, dirsG[i], walls[i], HitMath.PelletDamage(w, walls[i], false));
        }

        // pellets that stopped on a car (and no player was closer) damage the car, summed per car
        var carDamage = new Dictionary<ServerCar, float>();
        for (var i = 0; i < dirs.Length; i++)
        {
            if (cars[i] is not { } car || walls[i] > range) continue;
            if (GunnerCover.Raycast(eye.ToNumerics(), dirs[i], range, walls[i], targets, c.Id, exposed[i], pastHull[i]) != null) continue;
            carDamage[car] = carDamage.GetValueOrDefault(car) + HitMath.PelletDamage(w, walls[i], false) * _vehicles.BulletDamageMultiplier;
        }
        foreach (var (car, amount) in carDamage) _carDamage.Damage(car, amount, c.Id, weaponIdx, 0);
    }
}
