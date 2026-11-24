using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// Aircraft on the server (ticket #125): the aircraft world entity, and the gunship turret of its owner. The streak adapters call <see cref="Spawn"/> from their
/// <see cref="IKillstreak.Activate"/> and wrap the result with <see cref="ControlFor"/>. Cannon rounds are resolved here: <see cref="GunshipRouting.Route"/> finds what the ray stops on,
/// and a round with a blast (every cannon round of the AC-130 and the helicopter, every shell) goes through <see cref="Burst"/>. Rockets go into the player rocket sim, so their splash and direct
/// hits are the same code. The FX of a tick (<see cref="Fx"/>: tracers, impacts, blasts, hit markers) are coalesced and sent once per tick (<see cref="FlushFx"/>, #159); damage is never held back by it.
/// </summary>
internal sealed class GunshipServer
{
    /// <summary>A rocket of a gunship: owner, weapons.json index, blast, where from and which way, its speed and how long it lives.</summary>
    public delegate void LaunchRocket(ushort owner, byte weapon, SplashDef? blast, NVec3 origin, NVec3 direction, float speed, float life);

    /// <summary>A shell in the air: its detonation (damage, blast, impact) at the tick its tracer arrives. Round resets clear them.</summary>
    sealed record PendingShell(ushort Owner, GunshipWeaponDef Def, byte Credit, GunshipRoute Route, NVec3 End);

    readonly ServerContext _ctx;
    readonly KillstreakFile _killstreaks;
    readonly WeaponsFile _weapons;
    readonly TargetBook _targets;
    readonly Fleet _fleet;
    readonly Func<WorldQuery> _query;
    readonly Func<VehiclesFile?> _vehicles;
    readonly DealDamage _deal;
    readonly Action<ServerCar, float, ushort, byte, float> _damageVehicle;
    readonly LaunchRocket _launchRocket;
    readonly ImpactFx _impacts;
    readonly Splash _splash;
    readonly DeathPush _deathPush;
    readonly Func<KillstreakService> _streaks;

    /// <summary>Shells in the air, by their detonation time (the server's clock).</summary>
    readonly TimedQueue<PendingShell> _shells = new();

    /// <summary>weapons.json index per gunship weapon (same order as the gunship's weapons), resolved once at start.</summary>
    readonly int[] _weaponIndex = Array.Empty<int>();

    /// <summary>weapons.json index per AC-130 weapon (same order as the section's weapons), resolved when that streak is built.</summary>
    int[] _ac130WeaponIndex = Array.Empty<int>();

    /// <summary>The gunner whose round is resolving right now (its hit markers go to <see cref="Fx"/>); null otherwise.</summary>
    ushort? _fxOwner;

    /// <summary>Registers the aircraft type and resolves the gunship's weapons.</summary>
    public GunshipServer(ServerContext ctx, KillstreakFile killstreaks, WeaponsFile weapons, TargetBook targets, Fleet fleet, Func<WorldQuery> query, Func<VehiclesFile?> vehicles, DealDamage deal,
        Action<ServerCar, float, ushort, byte, float> damageVehicle, LaunchRocket launchRocket, ImpactFx impacts, Splash splash, DeathPush deathPush, Func<KillstreakService> streaks)
    {
        _streaks = streaks;
        _ctx = ctx; _killstreaks = killstreaks; _weapons = weapons; _targets = targets; _fleet = fleet; _query = query; _vehicles = vehicles; _deal = deal;
        _damageVehicle = damageVehicle; _launchRocket = launchRocket; _impacts = impacts; _splash = splash; _deathPush = deathPush;
        Aircraft.Register(WorldEntityRegistry.Shared);
        var gun = killstreaks?.Gunship;
        if (gun == null || weapons == null) return;
        _weaponIndex = gun.Weapons.Select(w =>
        {
            var i = weapons.Weapons.FindIndex(x => x.Id == w.WeaponId);
            if (i < 0) throw new InvalidOperationException($"killstreaks.json: gunship weapon \"{w.Id}\" names unknown weapon \"{w.WeaponId}\"");
            return i;
        }).ToArray();
    }

    /// <summary>This tick's gunship FX; flushed once per tick.</summary>
    public GunshipFxBatch Fx { get; } = new();

    /// <summary>Resolves the AC-130's weapons (its section's order); called when the AC-130 streak is built.</summary>
    public void ResolveAc130Weapons(GunshipDef ac)
    {
        _ac130WeaponIndex = ac.Weapons.Select(w =>
        {
            var i = _weapons.Weapons.FindIndex(x => x.Id == w.WeaponId);
            if (i < 0) throw new InvalidOperationException($"killstreaks.json: ac130 weapon \"{w.Id}\" names unknown weapon \"{w.WeaponId}\"");
            return i;
        }).ToArray();
    }

    /// <summary>
    /// Spawns an aircraft of a kind from killstreaks.json circling <paramref name="centre"/> (its Y is the ground there).
    /// Returns its world entity id, or null when the kind is unknown or the entity cap is reached.
    /// </summary>
    public ushort? Spawn(string kindId, NVec3 centre, float phaseRad, bool clockwise, ushort owner, float? lifetimeS = null)
    {
        var kinds = _killstreaks?.AircraftKinds ?? Array.Empty<AircraftKindDef>();
        var index = -1;
        for (var i = 0; i < kinds.Count; i++) if (kinds[i].Id == kindId) index = i;
        if (index < 0) { GD.PrintErr($"server: aircraft kind \"{kindId}\" is not in killstreaks.json"); return null; }
        return _ctx.Match.Entities.Spawn(new Aircraft(kinds[index], index, centre, phaseRad, clockwise, owner, lifetimeS));
    }

    /// <summary>
    /// The gunship control of an aircraft's owner: its shots are resolved on the server and its status goes to the owner.
    /// Feed the owner's inputs to it with <c>KillstreakUse.TakeControl(entityId, i => control.OnInput(i, now), freezesBody: true)</c>:
    /// the gunship's owner stands still while steering it.
    /// <paramref name="credit"/> is the weapon byte the owner's kills are credited under (null: the turret weapon's own);
    /// <paramref name="weapons"/> limits the turret to these indices of killstreaks.json's gunship weapons (null: all);
    /// <paramref name="ac130"/> picks the AC-130's section (its own weapons and shells) instead of the gunship's.
    /// Rockets are not credited under <paramref name="credit"/>: a rocket is the rocket sim's, so use a cannon-only control for a streak.
    /// </summary>
    public GunshipControl ControlFor(ushort owner, Aircraft aircraft, byte? credit = null, IReadOnlyList<int>? weapons = null, bool ac130 = false)
    {
        var def = (ac130 ? _killstreaks?.Ac130 : _killstreaks?.Gunship) ?? throw new InvalidOperationException($"killstreaks.json has no {(ac130 ? "ac130" : "gunship")}");
        var index = ac130 ? _ac130WeaponIndex : _weaponIndex;
        return new GunshipControl(def, aircraft,
            shot => ResolveShot(owner, shot, def, index, credit),
            status => { if (_ctx.Players.TryGetValue(owner, out var c)) _ctx.SendEvent(c, EventKind.GunshipStatus, GunshipCodec.Encode(status)); },
            weapons: weapons);
    }

    /// <summary>The index of a weapon of killstreaks.json's gunship, by its id (-1 when there is none).</summary>
    public int WeaponIndex(string gunWeaponId) => _killstreaks?.Gunship?.Weapons.ToList().FindIndex(w => w.Id == gunWeaponId) ?? -1;

    /// <summary>The static walls a gunship round stops on, as every shot (<see cref="WorldQuery.Wall"/>: crowns and bushes too). Its vehicles are routed by <see cref="GunshipRouting"/>, which tests their hulls.</summary>
    public float WorldWall(Vector3 from, Vector3 dir, float max) => _query().Wall(from, dir, max);

    /// <summary>
    /// The live vehicles as the gunship routing sees them: the index is the position in the fleet. With <paramref name="targets"/> (the
    /// bodies a round can hit), a vehicle whose seats are in the open (an amphibious transport, ADR 0012) carries its riders, so a round reaches
    /// them through its hit volume. A flak truck's or a helicopter's gunner stays behind the volume for these rounds, as before.
    /// </summary>
    public List<GunshipVehicle> RoutedVehicles(IReadOnlyList<HitTarget>? targets = null)
    {
        var list = new List<GunshipVehicle>(_fleet.Count);
        for (var i = 0; i < _fleet.Count; i++)
        {
            var c = _fleet.Cars[i];
            if (c.Destroyed) continue; // no collision: a destroyed vehicle is shot through
            var riders = targets != null && c.Seat.Occupied ? RiderRules.Exposed(c.Def, c.Seat, targets, seatsOnly: true) : null;
            var (_, pitch, roll) = Fleet.AttitudeOf(c);
            var (tp, tr) = c.Def.TiltOf(pitch, roll);
            list.Add(new GunshipVehicle(i, c.HullFeet, c.State.Yaw, c.HalfWidth, c.HalfLength, c.Def.Height, riders, tp, tr, c.Def.Tilts ? c.Def.HullBoxes : null));
        }
        return list;
    }

    /// <summary>Hurts an aircraft. When it is shot down its streak ends (the owner is released from the gun), and the shooter is credited in the log. Returns true if it was shot down.</summary>
    public bool DamageAircraft(ushort id, Aircraft a, float amount, ushort attacker)
    {
        var kind = a.Def.Id;
        var owner = a.Owner;
        _ctx.Match.Entities.Damage(id, amount, attacker);
        if (_ctx.Match.Entities.StateOf(id) != null) return false; // still flying
        var ended = _streaks().EndForEntity(id, KillstreakEndReason.Destroyed);
        GD.Print($"server: #{attacker} shot down a {kind} (entity {id}, owner #{owner}, {ended} streak(s) ended)");
        return true;
    }

    /// <summary>A hit marker of a gunner whose round is resolving now rides the tick's gunship FX; false for anyone else (the caller sends it).</summary>
    public bool NoteHit(ushort attacker, bool head, bool kill)
    {
        if (attacker != _fxOwner) return false;
        Fx.Hit(attacker, head, kill);
        return true;
    }

    /// <summary>
    /// One round from a turret: a rocket joins the rocket sim; a cannon or shell is a trace that stops at what it hits
    /// (<see cref="GunshipRouting.Route"/>), and a round with a blast bursts there (<see cref="Burst"/>). The damage is applied
    /// now; what the clients see (tracer, impact, blast, hit marker) is recorded in <see cref="Fx"/> and sent once per tick
    /// (<see cref="FlushFx"/>). <paramref name="credit"/> is the kill-feed weapon when the turret credits its kills to a special
    /// weapon (the helicopter, the AC-130); null credits the weapons.json weapon of the round.
    /// </summary>
    void ResolveShot(ushort owner, GunshipShot shot, GunshipDef gun, int[] weaponIndex, byte? credit)
    {
        var def = gun.Weapons[shot.WeaponIndex];
        var weapon = weaponIndex[shot.WeaponIndex];
        var by = credit ?? (byte)weapon;
        if (def.Kind == GunshipWeaponKind.Rocket)
        {
            // the rocket flies at the gunship's own speed and lives out its whole range (the rpg's 40 m/s would burst it ~150 m out)
            _launchRocket(owner, (byte)weapon, def.Splash, shot.Origin, shot.Direction, def.ProjectileSpeedMps, RocketFlight.LifeFor(def.RangeM, def.ProjectileSpeedMps));
            return;
        }

        var wall = WorldWall(new Vector3(shot.Origin.X, shot.Origin.Y, shot.Origin.Z), new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z), def.RangeM);
        var targets = _targets.Live();
        var route = GunshipRouting.Route(def, shot.Origin, shot.Direction, wall, targets, owner, RoutedVehicles(targets), _vehicles()?.BulletDamageMultiplier ?? 0f);
        var end = shot.Origin + shot.Direction * route.Distance;
        var isShell = def.Kind == GunshipWeaponKind.Shell;
        // a cannon sky shot (no hit) is drawn only to GunshipFxLimits.MaxSkyTracerM; a shell's streak runs to where it bursts, so it is not cut short
        var drawn = route.Impact == GunshipImpact.None && !isShell ? shot.Origin + shot.Direction * MathF.Min(route.Distance, GunshipFxLimits.MaxSkyTracerM) : end;
        Fx.Tracer(owner, new GunshipTracer((byte)weapon, shot.Origin, drawn, def.TracerSpeedMps ?? 0f, def.TracerLengthM ?? 0f));

        var timing = isShell ? ShellFlight.Plan(shot.Origin, end, def.TracerSpeedMps ?? 0f, _ctx.Now, 1.0 / Wire.TickRate) : new ShellTiming(false, _ctx.Now);
        if (timing.Deferred)
        {
            // the shell bursts when its tracer arrives: the point and the route are fixed now (no homing); the damage waits
            _shells.Add(timing.DueAt, new PendingShell(owner, def, by, route, end));
            return;
        }
        Detonate(owner, def, by, route, end);
    }

    /// <summary>
    /// A round's damage and FX at its point: the blast (or the direct hit of a cannon round), the hit markers for the owner, and the impact.
    /// Runs at the fire for a cannon round, and at the tracer's arrival for a shell (<see cref="TickShells"/>). The victims are the live
    /// ones at this moment; the direct hit is the route's, from the fire.
    /// </summary>
    public void Detonate(ushort owner, GunshipWeaponDef def, byte credit, GunshipRoute route, NVec3 end)
    {
        var targets = _targets.Live();
        var kill = false;
        _fxOwner = owner;
        if (def.Splash != null && (def.Kind == GunshipWeaponKind.Shell || route.Impact != GunshipImpact.None))
            kill = Burst(owner, end, def, credit, route, targets);
        else if (route.Impact == GunshipImpact.Body)
            kill = _deal(owner, credit, route.TargetId, route.Damage, false, false) == DamageResult.Killed;
        else if (route.Impact == GunshipImpact.Vehicle && _vehicles() != null)
        {
            var car = _fleet.Cars[route.VehicleIndex];
            _damageVehicle(car, route.Damage, owner, credit, 0);
            kill = car.Destroyed;
        }
        _fxOwner = null;

        if (route.Impact != GunshipImpact.None)
            Fx.Impact(new GunshipImpactFx(
                _impacts.At(end, SurfaceClassifier.SizeFor(def.Kind, def.Damage, def.Splash?.Radius ?? 0), route.Impact == GunshipImpact.Body, route.Impact == GunshipImpact.Vehicle),
                kill));
    }

    /// <summary>The shells whose tracer has arrived by now detonate; called once a tick, before the tick's FX are sent.</summary>
    public void TickShells()
    {
        foreach (var s in _shells.TakeDue(_ctx.Now)) Detonate(s.Owner, s.Def, s.Credit, s.Route, s.End);
    }

    /// <summary>A round reset takes the shells in the air with it.</summary>
    public void RoundReset() => _shells.Clear();

    /// <summary>
    /// The blast of a turret round at <paramref name="at"/>: recorded for the clients (one blast per round, sent with the tick), then the
    /// damage to every body and vehicle in the radius (<see cref="GunshipRouting.Burst"/>; the hit point at full damage), with knockback
    /// for the bodies that live. The vehicle damage uses the explosive multiplier; the owner is not hurt by its own guns (self multiplier 0
    /// in killstreaks.json). Returns true when the round's direct hit (the body or vehicle it struck) was killed.
    /// </summary>
    bool Burst(ushort owner, NVec3 at, GunshipWeaponDef def, byte credit, GunshipRoute route, List<HitTarget> targets)
    {
        var splash = def.Splash ?? throw new InvalidOperationException($"gunship weapon \"{def.Id}\" has no blast");
        ushort? direct = route.Impact == GunshipImpact.Body ? route.TargetId : null;
        var directCar = route.Impact == GunshipImpact.Vehicle ? route.VehicleIndex : -1;
        Fx.Blast(owner, new GunshipBlast(at, splash.Radius));
        var hits = GunshipRouting.Burst(def, at, owner, direct, directCar, targets, RoutedVehicles(),
            _vehicles()?.RocketDamageMultiplier ?? 0f, id => _ctx.Players.TryGetValue(id, out var c) ? c.Perks.ExplosiveDamageTaken : 1f); // Flak Jacket
        var kill = false;
        foreach (var h in hits)
        {
            if (h.IsVehicle)
            {
                var car = _fleet.Cars[h.VehicleIndex];
                _damageVehicle(car, h.Damage, owner, credit, 0);
                if (h.Direct && car.Destroyed) kill = true;
                continue;
            }
            var t = targets.FirstOrDefault(x => x.Id == h.TargetId);
            var share = h.Direct ? 1f : _splash.Share(at, t); // walls, a bunker room's too, and a covered seat's hull
            if (share <= 0) continue;
            var k = ProjectileMath.Knockback(splash.KnockbackSpeed, at, HitMath.Centre(t), h.Fraction * share);
            _deathPush.Record(h.TargetId, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = _deal(owner, credit, h.TargetId, h.Damage * share, false, false);
            _deathPush.Clear(h.TargetId, res);
            if (res == DamageResult.Ignored) continue; // spawn protected or a pilot at the gun: neither damage nor knockback
            if (h.Direct && res == DamageResult.Killed) kill = true;
            if (res == DamageResult.Killed || !_ctx.Players.TryGetValue(h.TargetId, out var v)) continue;
            if (k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
        }
        return kill;
    }

    /// <summary>Sends the tick's gunship FX (tracers, impacts, blasts with hit flags): once per tick, after the rounds resolved.</summary>
    public void FlushFx()
    {
        foreach (var m in Fx.Flush()) _ctx.Broadcast((EventKind)m.Kind, m.Payload);
    }
}
