using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// The flak truck on the server (ADR 0005): the gunner's inputs aim the turret and fire its airburst shells (<see cref="FlakRules"/>).
/// A shell flies a straight line at a high speed, so its whole flight is worked out when it is fired: it bursts by an aircraft (the fuse), meets the
/// ground, or runs out of range. Its tracer is sent at once (the gunship's coalesced FX, <see cref="GunshipServer.Fx"/>) and its damage lands when it arrives
/// (<see cref="Tick"/>). The aim goes to every client in the turret record of the snapshot (<see cref="RecordOf"/>); the ammo goes to the gunner.
/// </summary>
internal sealed class FlakServer
{
    /// <summary>A shell in the air: where it ends and when it is due (the server's clock). Cleared on a round reset.</summary>
    sealed record PendingFlak(ushort Owner, VehicleFlakDef Def, GunshipWeaponDef Shell, FlakPlan Plan, GunshipRoute Route);

    readonly ServerContext _ctx;
    readonly WeaponsFile? _weapons;
    readonly GunshipServer _gunships;
    readonly TargetBook _targets;
    readonly Fleet _fleet;
    readonly Func<VehiclesFile?> _vehicles;
    readonly Random _rng;
    readonly Action<NVec3, float, BlastSource> _carve;
    readonly TimedQueue<PendingFlak> _shells = new();
    readonly Dictionary<VehicleFlakDef, GunshipWeaponDef> _shellDefs = new();
    readonly Dictionary<VehicleFlakDef, int> _weaponIndex = new();

    public FlakServer(ServerContext ctx, WeaponsFile? weapons, GunshipServer gunships, TargetBook targets, Fleet fleet, Func<VehiclesFile?> vehicles, Random rng, Action<NVec3, float, BlastSource> carve)
    {
        _carve = carve;
        _ctx = ctx; _weapons = weapons; _gunships = gunships; _targets = targets; _fleet = fleet; _vehicles = vehicles; _rng = rng;
    }

    /// <summary>A flak naming an unknown weapon fails here, at start, not on the first shot.</summary>
    public void CheckWeapons(VehiclesFile vehicles)
    {
        if (_weapons != null) foreach (var v in vehicles.Vehicles) if (v.Flak is { } fk) WeaponOf(fk);
    }

    /// <summary>A round reset takes the shells in the air with it.</summary>
    public void RoundReset() => _shells.Clear();

    /// <summary>The ground shell as the gunship routing sees it: a shell with the flak's ground damage, blast and tracer (the burst against an aircraft is <see cref="FlakRules.BurstDamage"/>'s).</summary>
    GunshipWeaponDef ShellOf(VehicleFlakDef def)
    {
        if (_shellDefs.TryGetValue(def, out var s)) return s;
        return _shellDefs[def] = new GunshipWeaponDef
        {
            Id = "flak", Name = "Flak", Kind = GunshipWeaponKind.Shell, WeaponId = def.WeaponId, Damage = def.GroundDamage, RangeM = def.MaxRange,
            IntervalS = def.FireInterval, SpreadDeg = def.SpreadDegrees, Splash = def.GroundSplash, TracerSpeedMps = def.ShellSpeed, TracerLengthM = def.TracerLength,
        };
    }

    /// <summary>The weapons.json index of the flak's weapon (its shot sound and tracer index on the wire).</summary>
    int WeaponOf(VehicleFlakDef def)
    {
        if (_weaponIndex.TryGetValue(def, out var i)) return i;
        i = _weapons?.Weapons.FindIndex(w => w.Id == def.WeaponId) ?? -1;
        if (i < 0) throw new InvalidOperationException($"vehicles.json: flak names unknown weapon \"{def.WeaponId}\"");
        return _weaponIndex[def] = i;
    }

    /// <summary>The turret record of a flak vehicle for the snapshot: the world aim and the gunner's id.</summary>
    public static EntityRecord RecordOf(ServerCar car)
    {
        var seat = car.Def.Seats.FindIndex(s => s.Role == "gunner");
        var gunner = seat >= 0 ? car.Seat.Occupant(seat) : (ushort)0;
        return FlakRecord.Make(car.Id, gunner, FlakRules.Wrap(car.State.Yaw + car.TurretLocalYaw), car.GunPitch);
    }

    /// <summary>
    /// The gunner's input: the view is the aim (the pitch limited to the barrels' elevation, the turret's yaw kept against the chassis so it turns
    /// with the truck once the gunner has left), Reload reloads, and Fire fires a round per interval while the magazine lasts.
    /// </summary>
    public void OnGunnerInput(ServerClient c, ServerCar car, VehicleFlakDef def, NetInput i)
    {
        var yaw = Quant.Yaw(i.Yaw);
        var pitch = FlakRules.ClampPitch(def, Quant.Pitch(i.Pitch));
        car.TurretLocalYaw = FlakRules.Wrap(yaw - car.State.Yaw);
        car.GunPitch = pitch;
        var gun = car.Gun!;
        if ((i.Buttons & (ushort)MoveButtons.Reload) != 0) gun.TryReload(_ctx.Now);
        if (gun.TryFire(_ctx.Now, (i.Buttons & (ushort)MoveButtons.Fire) != 0)) Fire(c, car, def, yaw, pitch);
        var status = (gun.Magazine, gun.Reserve, gun.Reloading);
        if (c.FlakSent != status)
        {
            c.FlakSent = status;
            _ctx.SendEvent(c, EventKind.FlakStatus, FlakCodec.Encode(new FlakStatus((ushort)gun.Magazine, (ushort)gun.Reserve, gun.ReloadRemaining(_ctx.Now))));
        }
    }

    /// <summary>One round: leave the next barrel, plan the whole flight, send the tracer, and land the damage now or when the shell arrives.</summary>
    void Fire(ServerClient c, ServerCar car, VehicleFlakDef def, float yaw, float pitch)
    {
        var aim = HitMath.Forward(yaw, pitch);
        var origin = FlakRules.Muzzle(def, car.GlobalPosition.ToNumerics(), car.State.Yaw, yaw, aim, car.FlakBarrel++);
        var dir = HitMath.SpreadDirection(yaw, pitch, def.SpreadDegrees, _rng);
        var shell = ShellOf(def);
        var from = new Vector3(origin.X, origin.Y, origin.Z);
        var wall = _gunships.WorldWall(from, new Vector3(dir.X, dir.Y, dir.Z), def.MaxRange);
        // the riders of the truck are not in the shell's way, nor is the truck itself
        var targets = _targets.Live().Where(t => car.Seat.SeatOf(t.Id) < 0).ToList();
        var vehicles = _gunships.RoutedVehicles(targets).Where(v => _fleet.Cars[v.Index] != car).ToList();
        var route = GunshipRouting.Route(shell, origin, dir, wall, targets, c.Id, vehicles, _vehicles()?.BulletDamageMultiplier ?? 0f);
        var stop = route.Impact == GunshipImpact.None ? float.PositiveInfinity : route.Distance;
        var plan = FlakRules.Plan(def, origin, dir, stop, FuseTargets());
        _gunships.Fx.Tracer(c.Id, new GunshipTracer((byte)WeaponOf(def), origin, plan.At, def.ShellSpeed, def.TracerLength));
        var pending = new PendingFlak(c.Id, def, shell, plan, route);
        if (plan.Seconds > 1.0 / Wire.TickRate) _shells.Add(_ctx.Now + plan.Seconds, pending);
        else Detonate(pending);
    }

    /// <summary>The live aircraft as the fuse sees them: each one's position a number of seconds ahead along its orbit.</summary>
    List<FlakTarget> FuseTargets()
    {
        var list = new List<FlakTarget>();
        foreach (var (id, e) in _ctx.Match.Entities.OfType(Aircraft.WireType))
            if (e is Aircraft a) list.Add(new FlakTarget(id, a.PositionAfter));
        return list;
    }

    /// <summary>The shells whose flight has ended by now burst; called once a tick, before the tick's FX are sent.</summary>
    public void Tick()
    {
        foreach (var s in _shells.TakeDue(_ctx.Now)) Detonate(s);
    }

    /// <summary>
    /// A shell arrives. An airburst hurts every aircraft within its reach, by its distance from the burst at this moment (the fuse planned the burst
    /// at the closest approach, the aircraft has since flown on by at most a tick); the shooter's hit marker lights, and a kill confirms. A shell
    /// that meets the ground goes through the gunship's blast (<see cref="GunshipServer.Detonate"/>) with the flak's small one; one that ran out of range
    /// only puffs.
    /// </summary>
    void Detonate(PendingFlak s)
    {
        if (s.Plan.End == FlakEnd.Ground)
        {
            _gunships.Detonate(s.Owner, s.Shell, SpecialWeapons.Flak, s.Route, s.Plan.At);
            return;
        }
        _gunships.Fx.Blast(s.Owner, new GunshipBlast(s.Plan.At, s.Def.PuffRadius));
        _carve(s.Plan.At, s.Def.PuffRadius, BlastSource.Flak); // an airburst by a house's roof (house_destruction.json flak: 0 by default)
        if (s.Plan.End != FlakEnd.Burst) return;
        foreach (var (id, e) in _ctx.Match.Entities.OfType(Aircraft.WireType))
        {
            if (e is not Aircraft a) continue;
            var dmg = FlakRules.BurstDamage(s.Def, NVec3.Distance(a.Position, s.Plan.At));
            if (dmg <= 0) continue;
            var shotDown = _gunships.DamageAircraft(id, a, dmg, s.Owner);
            _gunships.Fx.Hit(s.Owner, false, shotDown);
        }
    }
}
