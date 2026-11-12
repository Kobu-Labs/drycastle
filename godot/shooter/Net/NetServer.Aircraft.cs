using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Aircraft on the server (ticket #125): the aircraft world entity, and the gunship turret of its owner. The streak tickets
/// call <see cref="SpawnAircraft"/> from their <see cref="IKillstreak.Activate"/> and wrap the result with
/// <see cref="GunshipFor"/>. Cannon rounds are resolved here: <see cref="GunshipRouting.Route"/> finds what the ray stops on,
/// and a round with a blast (every cannon round of the AC-130 and the helicopter, every shell) goes through
/// <see cref="GunshipBurst"/>. Rockets go into the player rocket sim, so their splash and direct hits are the same code.
/// </summary>
public partial class NetServer
{
    /// <summary>This tick's gunship FX, flushed once per tick (#159). Damage is never held back by it.</summary>
    readonly GunshipFxBatch _gunFx = new();
    /// <summary>The gunner whose round is resolving right now (its hit markers go to _gunFx); null otherwise.</summary>
    ushort? _gunFxOwner;

    /// <summary>Shells in the air, by their detonation time (the server's clock). Cleared on a round reset.</summary>
    readonly TimedQueue<PendingShell> _shells = new();

    /// <summary>weapons.json index per gunship weapon (same order as the gunship's weapons), resolved once at start.</summary>
    int[] _gunshipWeaponIndex = Array.Empty<int>();

    /// <summary>Registers the aircraft type and resolves the gunship's weapons; called from <see cref="_Ready"/>.</summary>
    void InitAircraft()
    {
        Aircraft.Register(WorldEntityRegistry.Shared);
        var gun = Killstreaks?.Gunship;
        if (gun == null || Weapons == null) return;
        _gunshipWeaponIndex = gun.Weapons.Select(w =>
        {
            var i = Weapons.Weapons.FindIndex(x => x.Id == w.WeaponId);
            if (i < 0) throw new InvalidOperationException($"killstreaks.json: gunship weapon \"{w.Id}\" names unknown weapon \"{w.WeaponId}\"");
            return i;
        }).ToArray();
    }

    /// <summary>
    /// Spawns an aircraft of a kind from killstreaks.json circling <paramref name="centre"/> (its Y is the ground there).
    /// Returns its world entity id, or null when the kind is unknown or the entity cap is reached.
    /// </summary>
    public ushort? SpawnAircraft(string kindId, NVec3 centre, float phaseRad, bool clockwise, ushort owner, float? lifetimeS = null)
    {
        var kinds = Killstreaks?.AircraftKinds ?? Array.Empty<AircraftKindDef>();
        var index = -1;
        for (var i = 0; i < kinds.Count; i++) if (kinds[i].Id == kindId) index = i;
        if (index < 0) { GD.PrintErr($"server: aircraft kind \"{kindId}\" is not in killstreaks.json"); return null; }
        return _match.Entities.Spawn(new Aircraft(kinds[index], index, centre, phaseRad, clockwise, owner, lifetimeS));
    }

    /// <summary>
    /// The gunship control of an aircraft's owner: its shots are resolved on the server and its status goes to the owner.
    /// Feed the owner's inputs to it with <c>KillstreakUse.TakeControl(entityId, i => control.OnInput(i, _elapsed), freezesBody: true)</c>:
    /// the gunship's owner stands still while steering it.
    /// <paramref name="credit"/> is the weapon byte the owner's kills are credited under (null: the turret weapon's own);
    /// <paramref name="weapons"/> limits the turret to these indices of killstreaks.json's gunship weapons (null: all);
    /// <paramref name="ac130"/> picks the AC-130's section (its own weapons and shells) instead of the gunship's.
    /// Rockets are not credited under <paramref name="credit"/>: a rocket is the rocket sim's, so use a cannon-only control for a streak.
    /// </summary>
    public GunshipControl GunshipFor(ushort owner, Aircraft aircraft, byte? credit = null, IReadOnlyList<int>? weapons = null, bool ac130 = false)
    {
        var def = (ac130 ? Killstreaks?.Ac130 : Killstreaks?.Gunship) ?? throw new InvalidOperationException($"killstreaks.json has no {(ac130 ? "ac130" : "gunship")}");
        var index = ac130 ? _ac130WeaponIndex : _gunshipWeaponIndex;
        return new GunshipControl(def, aircraft,
            shot => ResolveGunshipShot(owner, shot, def, index, credit),
            status => SendGunshipStatus(owner, status),
            weapons: weapons);
    }

    /// <summary>The index of a weapon of killstreaks.json's gunship, by its id (-1 when there is none).</summary>
    public int GunshipWeaponIndex(string gunWeaponId) => Killstreaks?.Gunship?.Weapons.ToList().FindIndex(w => w.Id == gunWeaponId) ?? -1;

    void SendGunshipStatus(ushort owner, GunshipStatus s)
    {
        if (_byId.TryGetValue(owner, out var c)) Send(c, new EventMsg((byte)EventKind.GunshipStatus, GunshipCodec.Encode(s)));
    }

    /// <summary>The world blocks at the blocking geometry only (layer 1; vehicles and player bodies are not walls for a gunship round).</summary>
    float WorldWall(Vector3 from, Vector3 dir, float max)
    {
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * max);
        q.CollisionMask = 1;
        q.Exclude = PlayerRids();
        var r = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return r.Count == 0 ? float.PositiveInfinity : from.DistanceTo(r["position"].AsVector3());
    }

    /// <summary>The live vehicles as the gunship routing sees them: the index is the position in <see cref="_cars"/>.</summary>
    List<GunshipVehicle> GunshipVehicles()
    {
        var list = new List<GunshipVehicle>(_cars.Count);
        for (var i = 0; i < _cars.Count; i++)
        {
            var c = _cars[i];
            if (c.Destroyed) continue; // no collision: a destroyed vehicle is shot through
            list.Add(new GunshipVehicle(i, N(c.GlobalPosition), c.State.Yaw, c.HalfWidth, c.HalfLength, c.Def.Height));
        }
        return list;
    }

    /// <summary>
    /// One round from a turret: a rocket joins the rocket sim; a cannon or shell is a trace that stops at what it hits
    /// (<see cref="GunshipRouting.Route"/>), and a round with a blast bursts there (<see cref="GunshipBurst"/>). The damage is applied
    /// now; what the clients see (tracer, impact, blast, hit marker) is recorded in <see cref="_gunFx"/> and sent once per tick
    /// (<see cref="FlushGunshipFx"/>). <paramref name="credit"/> is the kill-feed weapon when the turret credits its kills to a special
    /// weapon (the helicopter, the AC-130); null credits the weapons.json weapon of the round.
    /// </summary>
    void ResolveGunshipShot(ushort owner, GunshipShot shot, GunshipDef gun, int[] weaponIndex, byte? credit)
    {
        var def = gun.Weapons[shot.WeaponIndex];
        var weapon = weaponIndex[shot.WeaponIndex];
        var w = Weapons.Weapons[weapon];
        var by = credit ?? (byte)weapon;
        if (def.Kind == GunshipWeaponKind.Rocket)
        {
            // the rocket flies at the gunship's own speed and lives out its whole range (the bazooka's 40 m/s would burst it ~150 m out)
            var rocket = new Rocket
            {
                Id = _nextRocket, Owner = owner, Weapon = (byte)weapon, Blast = def.Splash,
                Pos = new Vector3(shot.Origin.X, shot.Origin.Y, shot.Origin.Z),
                Dir = new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z),
                Speed = def.ProjectileSpeedMps, Life = RocketFlight.LifeFor(def.RangeM, def.ProjectileSpeedMps),
            };
            _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
            _rockets.Add(rocket);
            _rocketsFired++;
            BroadcastEvent(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, owner, rocket.Weapon, shot.Origin, shot.Direction * rocket.Speed)));
            return;
        }

        var wall = WorldWall(new Vector3(shot.Origin.X, shot.Origin.Y, shot.Origin.Z), new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z), def.RangeM);
        var targets = LiveTargets();
        var route = GunshipRouting.Route(def, shot.Origin, shot.Direction, wall, targets, owner, GunshipVehicles(), Vehicles?.BulletDamageMultiplier ?? 0f);
        var end = shot.Origin + shot.Direction * route.Distance;
        var isShell = def.Kind == GunshipWeaponKind.Shell;
        // a cannon sky shot (no hit) is drawn only to GunshipFxLimits.MaxSkyTracerM; a shell's streak runs to where it bursts, so it is not cut short
        var drawn = route.Impact == GunshipImpact.None && !isShell ? shot.Origin + shot.Direction * MathF.Min(route.Distance, GunshipFxLimits.MaxSkyTracerM) : end;
        _gunFx.Tracer(owner, new GunshipTracer((byte)weapon, shot.Origin, drawn, def.TracerSpeedMps ?? 0f, def.TracerLengthM ?? 0f));

        var from = new NVec3(shot.Origin.X, shot.Origin.Y, shot.Origin.Z);
        var timing = isShell ? ShellFlight.Plan(from, end, def.TracerSpeedMps ?? 0f, _elapsed, 1.0 / Wire.TickRate) : new ShellTiming(false, _elapsed);
        if (timing.Deferred)
        {
            // the shell bursts when its tracer arrives: the point and the route are fixed now (no homing); the damage waits
            _shells.Add(timing.DueAt, new PendingShell(owner, def, by, route, end));
            return;
        }
        DetonateRound(owner, def, by, route, end);
    }

    /// <summary>
    /// A round's damage and FX at its point: the blast (or the direct hit of a cannon round), the hit markers for the owner, and the impact.
    /// Runs at the fire for a cannon round, and at the tracer's arrival for a shell (<see cref="DetonateShells"/>). The victims are the live
    /// ones at this moment; the direct hit is the route's, from the fire.
    /// </summary>
    void DetonateRound(ushort owner, GunshipWeaponDef def, byte credit, GunshipRoute route, NVec3 end)
    {
        var targets = LiveTargets();
        var kill = false;
        _gunFxOwner = owner;
        if (def.Splash != null && (def.Kind == GunshipWeaponKind.Shell || route.Impact != GunshipImpact.None))
            kill = GunshipBurst(owner, end, def, credit, route, targets);
        else if (route.Impact == GunshipImpact.Body)
            kill = Deal(owner, credit, route.TargetId, route.Damage, false, false) == DamageResult.Killed;
        else if (route.Impact == GunshipImpact.Vehicle && Vehicles != null)
        {
            var car = _cars[route.VehicleIndex];
            DamageVehicle(car, route.Damage, owner, credit, 0);
            kill = car.Destroyed;
        }
        _gunFxOwner = null;

        if (route.Impact != GunshipImpact.None)
            _gunFx.Impact(new GunshipImpactFx(
                ImpactAt(end, SurfaceClassifier.SizeFor(def.Kind, def.Damage, def.Splash?.Radius ?? 0), route.Impact == GunshipImpact.Body, route.Impact == GunshipImpact.Vehicle),
                kill));
    }

    /// <summary>A shell in the air: its detonation (damage, blast, impact) at the tick its tracer arrives. Round resets clear them.</summary>
    sealed record PendingShell(ushort Owner, GunshipWeaponDef Def, byte Credit, GunshipRoute Route, NVec3 End);

    /// <summary>The shells whose tracer has arrived by now detonate; called once a tick, before the tick's FX are sent.</summary>
    void DetonateShells()
    {
        foreach (var s in _shells.TakeDue(_elapsed)) DetonateRound(s.Owner, s.Def, s.Credit, s.Route, s.End);
    }

    /// <summary>
    /// The blast of a turret round at <paramref name="at"/>: recorded for the clients (one blast per round, sent with the tick), then the
    /// damage to every body and vehicle in the radius (<see cref="GunshipRouting.Burst"/>; the hit point at full damage), with knockback
    /// for the bodies that live. The vehicle damage uses the explosive multiplier; the owner is not hurt by its own guns (self multiplier 0
    /// in killstreaks.json). Returns true when the round's direct hit (the body or vehicle it struck) was killed.
    /// </summary>
    bool GunshipBurst(ushort owner, NVec3 at, GunshipWeaponDef def, byte credit, GunshipRoute route, List<HitTarget> targets)
    {
        var splash = def.Splash ?? throw new InvalidOperationException($"gunship weapon \"{def.Id}\" has no blast");
        ushort? direct = route.Impact == GunshipImpact.Body ? route.TargetId : null;
        var directCar = route.Impact == GunshipImpact.Vehicle ? route.VehicleIndex : -1;
        _gunFx.Blast(owner, new GunshipBlast(at, splash.Radius));
        var hits = GunshipRouting.Burst(def, at, owner, direct, directCar, targets, GunshipVehicles(),
            Vehicles?.RocketDamageMultiplier ?? 0f, id => _byId.TryGetValue(id, out var c) ? c.Perks.ExplosiveDamageTaken : 1f); // Flak Jacket
        var kill = false;
        foreach (var h in hits)
        {
            if (h.IsVehicle)
            {
                var car = _cars[h.VehicleIndex];
                DamageVehicle(car, h.Damage, owner, credit, 0);
                if (h.Direct && car.Destroyed) kill = true;
                continue;
            }
            var t = targets.FirstOrDefault(x => x.Id == h.TargetId);
            if (!h.Direct && !BlastReaches(at, t)) continue; // a bunker room's walls are between them
            var k = ProjectileMath.Knockback(splash.KnockbackSpeed, at, HitMath.Centre(t), h.Fraction);
            PushOnDeath(h.TargetId, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = Deal(owner, credit, h.TargetId, h.Damage, false, false);
            ClearPush(h.TargetId, res);
            if (res == DamageResult.Ignored) continue; // spawn protected or a pilot at the gun: neither damage nor knockback
            if (h.Direct && res == DamageResult.Killed) kill = true;
            if (res == DamageResult.Killed || !_byId.TryGetValue(h.TargetId, out var v)) continue;
            if (k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
        }
        return kill;
    }

    /// <summary>Sends the tick's gunship FX (tracers, impacts, blasts with hit flags): once per tick, after the rounds resolved.</summary>
    void FlushGunshipFx()
    {
        foreach (var m in _gunFx.Flush()) BroadcastEvent((EventKind)m.Kind, m.Payload);
    }
}
