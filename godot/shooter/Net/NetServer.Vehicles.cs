using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>Server side of vehicles (#91, #92: car, motorcycle, bicycle share it): spawning, enter/exit, driving from the driver's inputs, run-over, abandon return, resets.</summary>
public partial class NetServer
{
    /// <summary>vehicles.json (run-over numbers, abandon time, spawn count, car stats).</summary>
    public VehiclesFile? Vehicles;
    /// <summary>Spawn candidates of Kind road (with ground); cars prefer these, the other candidates fill up.</summary>
    public List<NVec3> RoadSpawns = new();
    /// <summary>Vehicle type of the car smoke arena (vehicles.json id).</summary>
    public string SmokeKind = "car";
    readonly List<ServerCar> _cars = new();
    const float EnterRange = 3f, RunOverCooldown = 0.8f;
    int _destroyedReturns, _vehicleHits, _vehicleBlasts, _blastHits, _chainHits;
    int _carEnters, _carExits, _runOvers, _runOverKills, _carReturns, _carResets;
    bool _carWasAway;

    const float ClusterRadius = 120f;
    static float Dist2D(NVec3 a, NVec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Up to <paramref name="count"/> anchors (spawn candidates), each the one with the most not yet covered candidates within <paramref name="radius"/>.</summary>
    static List<NVec3> ClusterAnchors(IReadOnlyList<NVec3> spawns, int count, float radius)
    {
        var uncovered = spawns.ToList(); var anchors = new List<NVec3>();
        while (anchors.Count < count && uncovered.Count > 0)
        {
            var best = spawns.OrderByDescending(a => uncovered.Count(u => Dist2D(a, u) <= radius)).First();
            anchors.Add(best);
            uncovered.RemoveAll(u => Dist2D(best, u) <= radius);
        }
        return anchors;
    }

    /// <summary>Places the cars once the spawn candidates are validated: road candidates first (not footpaths), at least 12 m apart, random colours.</summary>
    void SpawnCars()
    {
        var space = World.GetWorld3D().DirectSpaceState;
        var spots = new List<(NVec3 Pos, float Yaw, int Kind)>();
        if (BlastSmoke)
        {
            var kind = Vehicles!.Vehicles.FindIndex(v => v.Id == SmokeKind);
            var c = new NVec3(ArenaX, 0, ArenaZ);
            // car 0 is shot to pieces, car 1 stands 6 m beside it (chain damage, survives), car 2 is far away (rocket target, never in a blast)
            foreach (var o in new[] { new NVec3(0, 0, -30), new NVec3(6, 0, -30), new NVec3(0, 0, -70) }) spots.Add((c + o, 0, kind));
        }
        else if (CarSmoke)
        {
            var kind = Vehicles!.Vehicles.FindIndex(v => v.Id == SmokeKind);
            if (kind < 0) throw new InvalidOperationException($"unknown --car-kind {SmokeKind}");
            var c = new NVec3(ArenaX, 0, ArenaZ);
            foreach (var o in new[] { new NVec3(-40, 0, 60), new NVec3(-80, 0, 60), new NVec3(-40, 0, 100) }) spots.Add((c + o, 0, kind));
        }
        else
        {
            // vehicles stand around the player spawn areas: the spawn candidates are clustered greedily (most uncovered candidates within ClusterRadius),
            // the vehicles of vehicles.json (cars first) are dealt round-robin over the clusters and each takes a free candidate within 40 m of its
            // cluster anchor (road candidates first), at least 12 m from every other vehicle, on real ground under the whole footprint
            var kinds = new List<int>();
            for (var kind = 0; kind < Vehicles!.Vehicles.Count; kind++)
                for (var n = 0; n < Vehicles.Vehicles[kind].SpawnCount; n++) kinds.Add(kind);
            var anchors = ClusterAnchors(Spawns, Math.Max(1, Math.Min(6, (kinds.Count + 2) / 3)), ClusterRadius);
            var roadRank = new Dictionary<NVec3, int>();
            for (var i = 0; i < RoadSpawns.Count; i++) roadRank.TryAdd(RoadSpawns[i], i);
            int Rank(NVec3 s) => roadRank.TryGetValue(s, out var r) ? r : RoadSpawns.Count + 1;
            bool Place(int kind, IEnumerable<NVec3> cands)
            {
                var def0 = Vehicles.Vehicles[kind];
                foreach (var p in cands)
                {
                    if (spots.Any(s => NVec3.Distance(s.Pos, p) < 12f)) continue;
                    var yaw = (float)(_rng.NextDouble() * Math.PI * 2);
                    if (ServerCar.RestHeight(space, new Vector3(p.X, p.Y, p.Z), yaw, def0.HalfWidth, def0.HalfLength, p.Y) is not { } gy) continue;
                    spots.Add((new NVec3(p.X, gy, p.Z), yaw, kind));
                    return true;
                }
                return false;
            }
            for (var i = 0; i < kinds.Count; i++)
            {
                var anchor = anchors.Count > 0 ? anchors[i % anchors.Count] : Spawns[0];
                var near = Spawns.Where(s => Dist2D(s, anchor) <= 40f).OrderBy(Rank).ThenBy(_ => _rng.Next()).ToList();
                var ok = Place(kinds[i], near)
                    || Place(kinds[i], Spawns.Where(s => Dist2D(s, anchor) <= 90f).OrderBy(s => Dist2D(s, anchor)))
                    || Place(kinds[i], Spawns.OrderBy(Rank).ThenBy(_ => _rng.Next()));
                if (!ok) GD.PrintErr($"server: no spot for {Vehicles.Vehicles[kinds[i]].Id}");
            }
            // players must not respawn inside a vehicle: drop the spawn candidates next to one (keep a healthy pool)
            var free = Spawns.Where(s => spots.All(v => NVec3.Distance(v.Pos, s) > 4f)).ToList();
            if (free.Count >= 20) { Spawns.Clear(); Spawns.AddRange(free); }
            foreach (var (pos, _, kind) in spots)
            {
                var anchorD = anchors.Select(a => Dist2D(a, pos)).DefaultIfEmpty(float.MaxValue).Min();
                GD.Print($"server: vehicle spot {Vehicles.Vehicles[kind].Id} at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}), {anchorD:F0} m from the nearest spawn-cluster anchor, nearest player spawn {Spawns.Select(s => Dist2D(s, pos)).DefaultIfEmpty(-1).Min():F0} m");
            }
        }
        var colours = new Dictionary<int, List<byte>>();
        foreach (var (pos, yaw, kind) in spots)
        {
            var def = Vehicles!.Vehicles[kind];
            if (!colours.TryGetValue(kind, out var taken)) colours[kind] = taken = new List<byte>();
            var colour = VehicleColours.Pick(_rng, taken, def.Variants);
            taken.Add(colour);
            var car = new ServerCar
            {
                Name = $"{def.Id}{_cars.Count}", Id = (ushort)(VehicleRecord.FirstId + _cars.Count), Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, colour),
                HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(pos.X, pos.Y + 0.02f, pos.Z), OriginYaw = yaw,
            };
            World.AddChild(car);
            foreach (var cl in _clients.Values.Where(x => x.Joined)) car.AddCollisionExceptionWith(cl.Body);
            _cars.Add(car);
        }
        GD.Print($"server: {_cars.Count} vehicles spawned ({string.Join(", ", colours.Select(k => $"{Vehicles!.Vehicles[k.Key].Id} x{k.Value.Count} colours {string.Join(",", k.Value)}"))})");
    }

    void ResetCars()
    {
        foreach (var cl in _clients.Values) cl.Car = null;
        foreach (var car in _cars) { car.Seat.Clear(); car.Reset(); }
        _carResets += _cars.Count > 0 ? 1 : 0;
    }

    bool TryEnterCar(Client c)
    {
        ServerCar? best = null; var bd = EnterRange;
        var p = c.Body.GlobalPosition;
        foreach (var car in _cars)
        {
            if (car.Destroyed || car.Seat.Occupied || Mathf.Abs(car.GlobalPosition.Y - p.Y) > 2.5f) continue;
            var d = new Vector2(car.GlobalPosition.X - p.X, car.GlobalPosition.Z - p.Z).Length();
            if (d <= bd) { best = car; bd = d; }
        }
        if (best == null || !best.Seat.TryEnter(c.Id)) return false;
        c.Car = best; c.Throttle = c.Steer = 0; c.Handbrake = false;
        c.Body.Teleport(best.GlobalPosition);
        _carEnters++;
        GD.Print($"server: {c.Name} entered {best.Def.Id} {best.Id}");
        return true;
    }

    void ExitCar(Client c) => EjectDriver(c, true);

    /// <summary>The driver leaves the seat; <paramref name="placeOnGround"/> puts the body on a free side of the car (not for death, the respawn moves it).</summary>
    void EjectDriver(Client c, bool placeOnGround)
    {
        var car = c.Car;
        if (car == null) return;
        car.Seat.Exit(c.Id);
        c.Car = null;
        _carExits++;
        if (placeOnGround) c.Body.Teleport(FreeSide(c, car));
        GD.Print($"server: {c.Name} left {car.Def.Id} {car.Id}");
    }

    /// <summary>Exit position: right, left, behind, in front of the car (first with ground and room for the capsule), else above the roof.</summary>
    Vector3 FreeSide(Client c, ServerCar car)
    {
        var yaw = car.State.Yaw;
        var fwd = car.Forward; var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        var space = World.GetWorld3D().DirectSpaceState;
        var side = car.HalfWidth + 1f; var along = car.HalfLength + 1.05f; // 2.1 / 3.2 m for the car
        foreach (var off in new[] { right * side, -right * side, -fwd * along, fwd * along })
        {
            var p = car.GlobalPosition + off;
            var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 1.5f, p + Vector3.Down * 2.5f);
            q.CollisionMask = 1;
            var r = space.IntersectRay(q);
            if (r.Count == 0) continue;
            var g = r["position"].AsVector3() + Vector3.Up * 0.1f;
            if (!c.Body.TestMove(new Transform3D(Basis.Identity, g), Vector3.Zero)) return g;
        }
        return car.GlobalPosition + Vector3.Up * 2.5f;
    }

    void StepVehicles(float dt)
    {
        if (Vehicles == null || _cars.Count == 0) return;
        foreach (var car in _cars)
        {
            var def = car.Def;
            if (car.Destroyed)
            {
                if (_elapsed >= car.ReturnAt) { car.Reset(); _destroyedReturns++; if (BlastSmoke && _destroyedReturns >= 2 && _finishAt == 0) _finishAt = _elapsed + 1.5; GD.Print($"server: {def.Id} {car.Id} returned after its destruction"); }
                continue;
            }
            Client? driver = car.Seat.Occupied && _byId.TryGetValue(car.Seat.Driver, out var d) ? d : null;
            if (car.Seat.Occupied && driver == null) car.Seat.Clear();
            if (driver != null) car.Step(def, driver.Throttle, driver.Steer, driver.Handbrake, dt);
            else car.Step(def, 0, 0, false, dt);
            if (car.GlobalPosition.Y < KillY) { if (driver != null) EjectDriver(driver, false); car.Reset(false); }
            if (driver != null && car.LastImpact > Vehicles.CrashMinSpeedLoss)
                DamageVehicle(car, (car.LastImpact - Vehicles.CrashMinSpeedLoss) * Vehicles.CrashDamagePerSpeed, 0, SpecialWeapons.Vehicle, 0);
            if (car.DetonateAt >= 0 && _elapsed >= car.DetonateAt) { Detonate(car); continue; }
            if (driver != null)
            {
                driver.Body.Teleport(car.GlobalPosition);
                _match.SetPosition(driver.Id, N(car.GlobalPosition));
                RunOverCheck(car, driver);
            }
            else if (car.Abandon.Update(false, car.AtOrigin, dt, Vehicles.AbandonReturnTime))
            {
                car.Reset(false); _carReturns++;
                GD.Print($"server: {car.Def.Id} {car.Id} abandoned, returned to its origin");
            }
            else if (!car.AtOrigin && car.GlobalPosition.DistanceTo(car.OriginPos) > 20) _carWasAway = true;
            if (driver != null && car.GlobalPosition.DistanceTo(car.OriginPos) > 20) _carWasAway = true;
        }
    }

    /// <summary>
    /// Hurts a vehicle (bullet, rocket, blast or wall impact). At 0 HP it is scheduled to blow up <paramref name="delay"/> seconds from now
    /// (0 = in this tick's vehicle step). <paramref name="attacker"/> 0 means nobody: the blast is then credited to nobody (or the driver's own crash).
    /// </summary>
    void DamageVehicle(ServerCar car, float amount, ushort attacker, byte weapon, float delay)
    {
        if (car.Destroyed || amount <= 0 || Vehicles == null) return;
        if (attacker != 0 && _byId.TryGetValue(attacker, out var attackerClient)) amount *= attackerClient.Perks.VehicleDamageDealt; // Tank Hunter
        if (attacker != 0) { car.LastAttacker = attacker; car.LastWeapon = weapon; }
        var before = VehicleDamage.Stage(car.Hp, car.Def.Hp, Vehicles.DamageStages);
        car.Hp = MathF.Max(0, car.Hp - amount);
        _vehicleHits++;
        var stage = VehicleDamage.Stage(car.Hp, car.Def.Hp, Vehicles.DamageStages);
        if (stage != before) GD.Print($"server: {car.Def.Id} {car.Id} damage stage {before} -> {stage} (hp {car.Hp:F0}/{car.Def.Hp:F0})");
        if (car.Hp <= 0 && car.DetonateAt < 0) car.DetonateAt = _elapsed + delay;
    }

    /// <summary>
    /// The vehicle blows up: broadcast the event, kill the driver, splash everyone alive and every other vehicle in range (those that run out of HP
    /// go off after the chain delay), then the vehicle is gone until it returns. The last attacker gets the credit.
    /// </summary>
    void Detonate(ServerCar car)
    {
        var e = Vehicles!.Explosion;
        var at = car.GlobalPosition + Vector3.Up * 0.8f;
        var driver = car.Seat.Occupied && _byId.TryGetValue(car.Seat.Driver, out var d) ? d : null;
        var attacker = car.LastAttacker != 0 ? car.LastAttacker : driver?.Id ?? (ushort)0;
        var weapon = car.LastWeapon != 0 ? car.LastWeapon : SpecialWeapons.Vehicle;
        car.Destroy();
        car.ReturnAt = _elapsed + Vehicles.DestroyedReturnTime;
        _vehicleBlasts++;
        BroadcastEvent(EventKind.VehicleExplosion, WeaponCodec.Encode(new VehicleExplosionMsg(car.Id, attacker, N(at), e.Splash.Radius)));
        GD.Print($"server: {car.Def.Id} {car.Id} exploded at ({at.X:F1},{at.Y:F1},{at.Z:F1}), credited to #{attacker}");

        if (driver != null)
        {
            Deal(attacker, weapon, driver.Id, e.DriverDamage, false, false);
            if (driver.Car == car) EjectDriver(driver, true); // survived (spawn protection): thrown out, the seat is gone
        }
        foreach (var t in LiveTargets())
        {
            if (driver != null && t.Id == driver.Id) continue;
            var dist = ProjectileMath.DistanceToCapsule(N(at), t.Feet, HitMath.Height(t.Crouching), MoveConsts.CapsuleRadius);
            var dmg = VehicleDamage.BlastDamage(e, dist);
            if (dmg <= 0) continue;
            var self = t.Id == attacker;
            if (self) dmg = MathF.Max(1f, MathF.Round(dmg * e.Splash.SelfDamageMultiplier));
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var res = Deal(attacker, weapon, t.Id, dmg, false, false);
            if (res == DamageResult.Ignored) continue;
            _blastHits++;
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v))
            {
                var centre = t.Feet + new NVec3(0, HitMath.Height(t.Crouching) / 2, 0);
                var k = ProjectileMath.Knockback(e.Splash.KnockbackSpeed, N(at), centre, dmg / e.Damage);
                if (k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
            }
        }
        foreach (var other in _cars)
        {
            if (other == car || other.Destroyed) continue;
            var dist = VehicleDamage.DistanceToBox(N(at), N(other.GlobalPosition), other.State.Yaw, other.HalfWidth, other.HalfLength);
            var dmg = VehicleDamage.BlastDamage(e, dist) * e.VehicleDamageMultiplier;
            if (dmg <= 0) continue;
            _chainHits++;
            DamageVehicle(other, dmg, attacker, weapon, e.ChainDelay);
        }
    }

    /// <summary>Vehicle damage of a weapon (rocket direct hit or splash) by the vehicles.json multiplier.</summary>
    void RocketVsVehicles(Vector3 pos, WeaponDef w, ushort owner, byte weapon, ServerCar? direct)
    {
        foreach (var car in _cars)
        {
            if (car.Destroyed) continue;
            float dmg;
            if (car == direct) dmg = w.Damage;
            else
            {
                var dist = VehicleDamage.DistanceToBox(N(pos), N(car.GlobalPosition), car.State.Yaw, car.HalfWidth, car.HalfLength);
                dmg = ProjectileMath.SplashDamage(w, dist, false);
            }
            DamageVehicle(car, dmg * Vehicles!.RocketDamageMultiplier, owner, weapon, 0);
        }
    }

    void RunOverCheck(ServerCar car, Client driver)
    {
        var dmg = RunOver.Damage(Vehicles!, car.State.Speed);
        if (dmg <= 0) return;
        var fwd = car.Forward;
        var right = new Vector3(Mathf.Cos(car.State.Yaw), 0, -Mathf.Sin(car.State.Yaw));
        foreach (var t in _byId.Values)
        {
            if (t == driver || t.Car != null || _match.Get(t.Id)?.Life != PlayerLife.Alive) continue;
            var d = t.Body.GlobalPosition - car.GlobalPosition;
            if (d.Y < -0.6f || d.Y > 2f) continue;
            var lx = d.Dot(right); var lz = d.Dot(fwd);
            if (Mathf.Abs(lx) > car.HalfWidth + MoveConsts.CapsuleRadius || Mathf.Abs(lz) > car.HalfLength + MoveConsts.CapsuleRadius) continue;
            if (car.HitCooldown.TryGetValue(t.Id, out var until) && _elapsed < until) continue;
            car.HitCooldown[t.Id] = _elapsed + RunOverCooldown;
            var r = Deal(driver.Id, SpecialWeapons.Vehicle, t.Id, dmg, false, false);
            if (r == DamageResult.Ignored) continue;
            _runOvers++;
            if (r == DamageResult.Killed) _runOverKills++;
            GD.Print($"server: {car.Def.Id} {car.Id} ran over #{t.Id} at {car.State.Speed:F1} m/s for {dmg:F0}");
        }
    }

    void FinishBlast()
    {
        var ok = _vehicleHits >= 20 && _vehicleBlasts >= 2 && _blastHits >= 2 && _chainHits >= 1 && _destroyedReturns >= 2 && _cars.Count == 3;
        GD.Print($"server: blast smoke vehicleHits={_vehicleHits} blasts={_vehicleBlasts} blastHits(players)={_blastHits} chainHits={_chainHits} returns={_destroyedReturns} kills={_kills}");
        GD.Print(ok ? "SHOOTER_BLAST_SERVER_OK" : "SHOOTER_BLAST_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }

    void FinishCar()
    {
        var maxHit = RunOver.Damage(Vehicles!, Vehicles!.Vehicles.First(v => v.Id == SmokeKind).TopSpeed);
        var needKill = maxHit >= Vehicles.RunOverMaxDamage; // a bicycle at 11 m/s cannot kill a full-health player in one pass
        var ok = _carEnters >= 1 && _carExits >= 1 && _runOvers >= 1 && (_runOverKills >= 1 || !needKill) && _carReturns >= 1 && _carWasAway && _shotsFired == 0 && _cars.Count >= 3;
        GD.Print($"server: car smoke kind={SmokeKind} cars={_cars.Count} enters={_carEnters} exits={_carExits} runOvers={_runOvers} runOverKills={_runOverKills} returns={_carReturns} shotsWhileDriving={_shotsFired}");
        GD.Print(ok ? "SHOOTER_CAR_SERVER_OK" : "SHOOTER_CAR_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }
}
