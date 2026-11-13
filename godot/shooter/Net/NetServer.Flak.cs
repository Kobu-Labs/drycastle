using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The flak truck on the server (ADR 0005): the gunner's inputs aim the turret and fire its airburst shells (<see cref="FlakRules"/>).
/// A shell flies a straight line at a high speed, so its whole flight is worked out when it is fired: it bursts by an aircraft (the fuse), meets the
/// ground, or runs out of range. Its tracer is sent at once (the gunship's coalesced FX, <see cref="_gunFx"/>) and its damage lands when it arrives
/// (<see cref="DetonateFlak"/>). The aim goes to every client in the turret record of the snapshot (<see cref="FlakRecord"/>); the ammo goes to the gunner.
/// </summary>
public partial class NetServer
{
    /// <summary>A shell in the air: where it ends and when it is due (the server's clock). Cleared on a round reset.</summary>
    sealed record PendingFlak(ushort Owner, VehicleFlakDef Def, GunshipWeaponDef Shell, FlakPlan Plan, GunshipRoute Route);

    readonly TimedQueue<PendingFlak> _flakShells = new();
    readonly Dictionary<VehicleFlakDef, GunshipWeaponDef> _flakShellDefs = new();
    readonly Dictionary<VehicleFlakDef, int> _flakWeaponIndex = new();
    int _flakShots, _flakBursts, _flakHits;

    /// <summary>The ground shell as the gunship routing sees it: a shell with the flak's ground damage, blast and tracer (the burst against an aircraft is <see cref="FlakRules.BurstDamage"/>'s).</summary>
    GunshipWeaponDef FlakShell(VehicleFlakDef def)
    {
        if (_flakShellDefs.TryGetValue(def, out var s)) return s;
        return _flakShellDefs[def] = new GunshipWeaponDef
        {
            Id = "flak", Name = "Flak", Kind = GunshipWeaponKind.Shell, WeaponId = def.WeaponId, Damage = def.GroundDamage, RangeM = def.MaxRange,
            IntervalS = def.FireInterval, SpreadDeg = def.SpreadDegrees, Splash = def.GroundSplash, TracerSpeedMps = def.ShellSpeed, TracerLengthM = def.TracerLength,
        };
    }

    /// <summary>The weapons.json index of the flak's weapon (its shot sound and tracer index on the wire).</summary>
    int FlakWeapon(VehicleFlakDef def)
    {
        if (_flakWeaponIndex.TryGetValue(def, out var i)) return i;
        i = Weapons?.Weapons.FindIndex(w => w.Id == def.WeaponId) ?? -1;
        if (i < 0) throw new InvalidOperationException($"vehicles.json: flak names unknown weapon \"{def.WeaponId}\"");
        return _flakWeaponIndex[def] = i;
    }

    /// <summary>
    /// A ray, pellet or rocket segment stopped on <paramref name="hull"/>. When that vehicle's gunner sits in the open (vehicles.json exposed_gunner) and is
    /// alive, returns him as a target and the world distance along the ray with the hull left out, so the shot can reach him through the hull
    /// (<see cref="GunnerCover"/>). A rider behind a pane is reached by <see cref="BulletHit"/>, which lets bullets through glass.
    /// Otherwise no gunner (every rider stays behind the hull).
    /// </summary>
    /// <remarks>
    /// The riders are every rider of <paramref name="hull"/> in the open (<see cref="RiderRules.Exposed"/>): the one gunner of a flak truck or a
    /// utility helicopter, or everyone aboard an amphibious transport (ADR 0012). <paramref name="seatsOnly"/> is for the weapons that never
    /// reached a flak or helicopter gunner through the hull (the jeep's roof gun, the killstreak guns): they reach only riders whose seat says it
    /// is exposed. <paramref name="skip"/> is the shooter's own vehicle, which the ray passes through as well.
    /// </remarks>
    (ExposedRiders? Gunner, float PastHull) ExposedGunnerBehind(ServerCar? hull, Vector3 from, Vector3 dir, float max, IReadOnlyList<HitTarget> targets, bool seatsOnly = false, ServerCar? skip = null)
    {
        if (hull is not { Destroyed: false } || !hull.Seat.Occupied) return (null, float.PositiveInfinity);
        if (RiderRules.Exposed(hull.Def, hull.Seat, targets, seatsOnly) is not { } riders) return (null, float.PositiveInfinity);
        return (riders, NextStop(from, dir, max, hull, skip)); // the walls and the other hulls, the hull and the shooter's own left out (B-02)
    }

    /// <summary>The turret record of a flak vehicle for the snapshot: the world aim and the gunner's id.</summary>
    EntityRecord FlakRecordOf(ServerCar car)
    {
        var seat = car.Def.Seats.FindIndex(s => s.Role == "gunner");
        var gunner = seat >= 0 ? car.Seat.Occupant(seat) : (ushort)0;
        return FlakRecord.Make(car.Id, gunner, FlakRules.Wrap(car.State.Yaw + car.TurretLocalYaw), car.GunPitch);
    }

    /// <summary>
    /// The gunner's input: the view is the aim (the pitch limited to the barrels' elevation, the turret's yaw kept against the chassis so it turns
    /// with the truck once the gunner has left), Reload reloads, and Fire fires a round per interval while the magazine lasts.
    /// </summary>
    void FlakGunnerInput(Client c, ServerCar car, VehicleFlakDef def, NetInput i)
    {
        var yaw = Quant.Yaw(i.Yaw);
        var pitch = FlakRules.ClampPitch(def, Quant.Pitch(i.Pitch));
        car.TurretLocalYaw = FlakRules.Wrap(yaw - car.State.Yaw);
        car.GunPitch = pitch;
        var gun = car.Gun!;
        if ((i.Buttons & (ushort)MoveButtons.Reload) != 0) gun.TryReload(_elapsed);
        if (gun.TryFire(_elapsed, (i.Buttons & (ushort)MoveButtons.Fire) != 0)) FireFlak(c, car, def, yaw, pitch);
        var status = (gun.Magazine, gun.Reserve, gun.Reloading);
        if (c.FlakSent != status)
        {
            c.FlakSent = status;
            Send(c, new EventMsg((byte)EventKind.FlakStatus, FlakCodec.Encode(new FlakStatus((ushort)gun.Magazine, (ushort)gun.Reserve, gun.ReloadRemaining(_elapsed)))));
        }
    }

    /// <summary>One round: leave the next barrel, plan the whole flight, send the tracer, and land the damage now or when the shell arrives.</summary>
    void FireFlak(Client c, ServerCar car, VehicleFlakDef def, float yaw, float pitch)
    {
        var aim = HitMath.Forward(yaw, pitch);
        var origin = FlakRules.Muzzle(def, N(car.GlobalPosition), car.State.Yaw, yaw, aim, car.FlakBarrel++);
        var dir = HitMath.SpreadDirection(yaw, pitch, def.SpreadDegrees, _rng);
        var shell = FlakShell(def);
        var from = new Vector3(origin.X, origin.Y, origin.Z);
        var wall = WorldWall(from, new Vector3(dir.X, dir.Y, dir.Z), def.MaxRange);
        // the riders of the truck are not in the shell's way, nor is the truck itself
        var targets = LiveTargets().Where(t => car.Seat.SeatOf(t.Id) < 0).ToList();
        var vehicles = GunshipVehicles(targets).Where(v => _cars[v.Index] != car).ToList();
        var route = GunshipRouting.Route(shell, origin, dir, wall, targets, c.Id, vehicles, Vehicles?.BulletDamageMultiplier ?? 0f);
        var stop = route.Impact == GunshipImpact.None ? float.PositiveInfinity : route.Distance;
        var plan = FlakRules.Plan(def, origin, dir, stop, FlakTargets());
        _flakShots++;
        _gunFx.Tracer(c.Id, new GunshipTracer((byte)FlakWeapon(def), origin, plan.At, def.ShellSpeed, def.TracerLength));
        var pending = new PendingFlak(c.Id, def, shell, plan, route);
        if (plan.Seconds > 1.0 / Wire.TickRate) _flakShells.Add(_elapsed + plan.Seconds, pending);
        else DetonateFlakShell(pending);
    }

    /// <summary>The live aircraft as the fuse sees them: each one's position a number of seconds ahead along its orbit.</summary>
    List<FlakTarget> FlakTargets()
    {
        var list = new List<FlakTarget>();
        foreach (var (id, e) in _match.Entities.OfType(Aircraft.WireType))
            if (e is Aircraft a) list.Add(new FlakTarget(id, a.PositionAfter));
        return list;
    }

    /// <summary>The shells whose flight has ended by now burst; called once a tick, before the tick's FX are sent.</summary>
    void DetonateFlak()
    {
        foreach (var s in _flakShells.TakeDue(_elapsed)) DetonateFlakShell(s);
    }

    /// <summary>
    /// A shell arrives. An airburst hurts every aircraft within its reach, by its distance from the burst at this moment (the fuse planned the burst
    /// at the closest approach, the aircraft has since flown on by at most a tick); the shooter's hit marker lights, and a kill confirms. A shell
    /// that meets the ground goes through the gunship's blast (<see cref="DetonateRound"/>) with the flak's small one; one that ran out of range
    /// only puffs.
    /// </summary>
    void DetonateFlakShell(PendingFlak s)
    {
        if (s.Plan.End == FlakEnd.Ground)
        {
            DetonateRound(s.Owner, s.Shell, SpecialWeapons.Flak, s.Route, s.Plan.At);
            return;
        }
        _gunFx.Blast(s.Owner, new GunshipBlast(s.Plan.At, s.Def.PuffRadius));
        if (s.Plan.End != FlakEnd.Burst) return;
        _flakBursts++;
        foreach (var (id, e) in _match.Entities.OfType(Aircraft.WireType))
        {
            if (e is not Aircraft a) continue;
            var dmg = FlakRules.BurstDamage(s.Def, NVec3.Distance(a.Position, s.Plan.At));
            if (dmg <= 0) continue;
            _flakHits++;
            var shotDown = DamageAircraft(id, a, dmg, s.Owner);
            _gunFx.Hit(s.Owner, false, shotDown);
        }
    }

    /// <summary>Hurts an aircraft. When it is shot down its streak ends (the owner is released from the gun), and the shooter is credited in the log. Returns true if it was shot down.</summary>
    bool DamageAircraft(ushort id, Aircraft a, float amount, ushort attacker)
    {
        var kind = a.Def.Id;
        var owner = a.Owner;
        _match.Entities.Damage(id, amount, attacker);
        if (_match.Entities.StateOf(id) != null) return false; // still flying
        var ended = _streaks.EndForEntity(id, KillstreakEndReason.Destroyed);
        GD.Print($"server: #{attacker} shot down a {kind} (entity {id}, owner #{owner}, {ended} streak(s) ended)");
        return true;
    }
}
