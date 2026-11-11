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
    int _carEnters, _carExits, _runOvers, _runOverKills, _carReturns, _carResets;
    bool _carWasAway;

    /// <summary>Places the cars once the spawn candidates are validated: road candidates first (not footpaths), at least 12 m apart, random colours.</summary>
    void SpawnCars()
    {
        var space = World.GetWorld3D().DirectSpaceState;
        float? Ground(NVec3 p)
        {
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(p.X, p.Y + 2f, p.Z), new Vector3(p.X, p.Y - 5f, p.Z));
            q.CollisionMask = 1;
            var r = space.IntersectRay(q);
            return r.Count == 0 ? null : r["position"].AsVector3().Y;
        }
        var spots = new List<(NVec3 Pos, float Yaw, int Kind)>();
        if (CarSmoke)
        {
            var kind = Vehicles!.Vehicles.FindIndex(v => v.Id == SmokeKind);
            if (kind < 0) throw new InvalidOperationException($"unknown --car-kind {SmokeKind}");
            var c = new NVec3(ArenaX, 0, ArenaZ);
            foreach (var o in new[] { new NVec3(-40, 0, 60), new NVec3(-80, 0, 60), new NVec3(-40, 0, 100) }) spots.Add((c + o, 0, kind));
        }
        else
        {
            // one shared pool (road candidates first, then the rest), 12 m apart; each type takes its spawn_count from it in vehicles.json order
            var pool = RoadSpawns.OrderBy(_ => _rng.Next()).Concat(Spawns.Where(s => !RoadSpawns.Contains(s)).OrderBy(_ => _rng.Next())).ToList();
            var pi = 0;
            for (var kind = 0; kind < Vehicles!.Vehicles.Count; kind++)
            {
                var want = Vehicles.Vehicles[kind].SpawnCount; var got = 0;
                for (; pi < pool.Count && got < want; pi++)
                {
                    var p = pool[pi];
                    if (spots.Any(s => NVec3.Distance(s.Pos, p) < 12f)) continue;
                    if (Ground(p) is not { } gy) continue; // no ground under the candidate: skip
                    spots.Add((new NVec3(p.X, gy, p.Z), (float)(_rng.NextDouble() * Math.PI * 2), kind));
                    got++;
                }
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
                HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(pos.X, pos.Y + 0.05f, pos.Z), OriginYaw = yaw,
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
            if (car.Seat.Occupied || Mathf.Abs(car.GlobalPosition.Y - p.Y) > 2.5f) continue;
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
            Client? driver = car.Seat.Occupied && _byId.TryGetValue(car.Seat.Driver, out var d) ? d : null;
            if (car.Seat.Occupied && driver == null) car.Seat.Clear();
            if (driver != null) car.Step(def, driver.Throttle, driver.Steer, driver.Handbrake, dt);
            else car.Step(def, 0, 0, false, dt);
            if (car.GlobalPosition.Y < KillY) { if (driver != null) EjectDriver(driver, false); car.Reset(); }
            if (driver != null)
            {
                driver.Body.Teleport(car.GlobalPosition);
                _match.SetPosition(driver.Id, N(car.GlobalPosition));
                RunOverCheck(car, driver);
            }
            else if (car.Abandon.Update(false, car.AtOrigin, dt, Vehicles.AbandonReturnTime))
            {
                car.Reset(); _carReturns++;
                GD.Print($"server: {car.Def.Id} {car.Id} abandoned, returned to its origin");
            }
            else if (!car.AtOrigin && car.GlobalPosition.DistanceTo(car.OriginPos) > 20) _carWasAway = true;
            if (driver != null && car.GlobalPosition.DistanceTo(car.OriginPos) > 20) _carWasAway = true;
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
