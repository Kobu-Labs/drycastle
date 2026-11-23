using Godot;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>Server side of vehicles (#91, #92: car and motorcycle share it): spawning, enter/exit, driving from the driver's inputs, run-over, resets. A vehicle never despawns while abandoned (#294): it returns to its origin only when destroyed, or out of the world.</summary>
public partial class NetServer
{
    /// <summary>vehicles.json (run-over numbers, spawn count, car stats).</summary>
    public VehiclesFile? Vehicles;
    /// <summary>Spawn candidates of Kind road (with ground); cars prefer these, the other candidates fill up.</summary>
    /// <summary>Vehicle type of the car smoke arena (vehicles.json id).</summary>
    public string SmokeKind = "car";
    readonly List<ServerCar> _cars = new();
    const float RunOverCooldown = 0.8f;
    int _destroyedReturns, _vehicleHits, _vehicleBlasts, _blastHits, _chainHits;
    int _seatSwitches;
    int _carEnters, _carExits, _runOvers, _runOverKills, _carResets;

    FlightBounds? _mapFlightBounds;
    /// <summary>The flyable rectangle of the map (the box around its terrain, <see cref="FlightProbe.MapBounds"/>); a smoke arena has none.</summary>
    FlightBounds MapFlightBounds => _mapFlightBounds ??= FlightProbe.MapBounds(Map);

    /// <summary>A pilot whose inputs have not arrived for this many ticks is flown by the stabiliser (a hover), so a stalled client does not freeze the helicopter in the air.</summary>
    const uint FlightStarveTicks = 6;

    const float ClusterRadius = 120f;
    static float Dist2D(NVec3 a, NVec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Up to <paramref name="count"/> anchors (spawn candidates), each the one with the most not yet covered candidates within <paramref name="radius"/>.</summary>
    static List<NVec3> ClusterAnchors(IReadOnlyList<NVec3> spawns, int count, float radius)
    {
        var uncovered = spawns.ToList(); var anchors = new List<NVec3>();
        while (anchors.Count < count && uncovered.Count > 0)
        {
            // plain loops, no sort: the first candidate with the most uncovered neighbours wins, as with a stable OrderByDescending(...).First()
            var best = spawns[0];
            var bestScore = -1;
            foreach (var a in spawns)
            {
                var score = 0;
                foreach (var u in uncovered) if (Dist2D(a, u) <= radius) score++;
                if (score > bestScore) { best = a; bestScore = score; }
            }
            anchors.Add(best);
            uncovered.RemoveAll(u => Dist2D(best, u) <= radius);
        }
        return anchors;
    }

    /// <summary>
    /// The places of the types' <see cref="VehicleDef.FixedSpots"/> (vehicles.json), on the ground there: the heading is the spot's own or along the nearest
    /// road (<see cref="RoadHeading"/>), the height the highest ground under the footprint (the same probe the random deal uses, else the terrain's own
    /// height). A spot with no terrain under it is logged and left out. A vehicle that returns (<see cref="ServerCar.Reset"/>) comes back to its spot.
    /// </summary>
    List<(NVec3 Pos, float Yaw, int Kind)> PlanFixedSpots(PhysicsDirectSpaceState3D space)
    {
        var planned = new List<(NVec3 Pos, float Yaw, int Kind)>();
        if (Vehicles == null || Map == null) return planned;
        List<RoadLine>? roads = null;
        for (var kind = 0; kind < Vehicles.Vehicles.Count; kind++)
        {
            var def = Vehicles.Vehicles[kind];
            foreach (var spot in def.FixedSpots)
            {
                roads ??= RoadNetwork.Parse(Map.RoadsJson);
                var yaw = RoadHeading.YawFor(spot, roads);
                var terrain = Map.GroundHeight(spot.X, spot.Z);
                if (float.IsNaN(terrain)) { GD.PrintErr($"server: fixed spot of {def.Id} at ({spot.X:F1},{spot.Z:F1}) has no ground, left out"); continue; }
                var gy = ServerCar.RestHeight(space, new Vector3(spot.X, terrain, spot.Z), yaw, def.HalfWidth, def.HalfLength, terrain) ?? terrain;
                planned.Add((new NVec3(spot.X, gy, spot.Z), yaw, kind));
                GD.Print($"server: fixed spot {def.Id} at ({spot.X:F1},{gy:F1},{spot.Z:F1}) facing {yaw * 180f / MathF.PI:F0} deg");
                if (def.Flight != null) WarnIfAircraftDoesNotFit(def, new Vector3(spot.X, gy, spot.Z), yaw);
            }
        }
        if (GarageParking() is { } park) // the bus garage's own vehicle, nose to the doors (garage.json)
        {
            var def = Vehicles.Vehicles[park.Kind];
            var gy = ServerCar.RestHeight(space, new Vector3(park.Pos.X, park.Pos.Y, park.Pos.Z), park.Yaw, def.HalfWidth, def.HalfLength, park.Pos.Y) ?? park.Pos.Y;
            planned.Add((new NVec3(park.Pos.X, gy, park.Pos.Z), park.Yaw, park.Kind));
            GD.Print($"server: {def.Id} parked in the garage at ({park.Pos.X:F1},{gy:F1},{park.Pos.Z:F1})");
        }
        return planned;
    }

    /// <summary>
    /// A helicopter dealt at random only takes a place where its hull and rotor disc fit (<see cref="FlightProbe.Fits"/>); one at a fixed spot is put there
    /// whatever is around it, as placed by hand, so a spot that touches something solid (a hangar, a pole) is said in the log. It is still used.
    /// </summary>
    void WarnIfAircraftDoesNotFit(VehicleDef def, Vector3 at, float yaw)
    {
        var probe = FlightProbe.Make(def, Water, FlightBounds.None);
        World.AddChild(probe);
        if (!probe.Fits(at, yaw)) GD.PrintErr($"server: fixed spot of {def.Id} at ({at.X:F1},{at.Z:F1}): its hull or rotor disc touches something solid there (it stands there anyway)");
        probe.QueueFree();
    }

    /// <summary>Places the cars once the spawn candidates are validated: near the spawn zones, at least 12 m apart, random colours; the types' fixed spots stand last in the list.</summary>
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
            // cluster anchor (a random one), at least 12 m from every other vehicle, on real ground under the whole footprint
            var kinds = new List<int>();
            var garaged = GarageParking()?.Kind ?? -1; // one of that type stands in the garage instead of being dealt
            for (var kind = 0; kind < Vehicles!.Vehicles.Count; kind++)
                for (var n = kind == garaged ? 1 : 0; n < Vehicles.Vehicles[kind].SpawnCount; n++) kinds.Add(kind);
            var anchors = ClusterAnchors(Spawns, Math.Max(1, Math.Min(6, (kinds.Count + 2) / 3)), ClusterRadius);
            var fitProbes = new Dictionary<int, FlightProbe>();
            var fixedSpots = PlanFixedSpots(space); // the random deal keeps 12 m clear of them; they join the list after it, so the ids of the dealt vehicles do not move
            bool Place(int kind, IEnumerable<NVec3> cands)
            {
                var def0 = Vehicles.Vehicles[kind];
                foreach (var p in cands)
                {
                    if (spots.Any(s => NVec3.Distance(s.Pos, p) < 12f) || fixedSpots.Any(s => NVec3.Distance(s.Pos, p) < 12f)) continue;
                    var yaw = (float)(_rng.NextDouble() * Math.PI * 2);
                    if (ServerCar.RestHeight(space, new Vector3(p.X, p.Y, p.Z), yaw, def0.HalfWidth, def0.HalfLength, p.Y) is not { } gy) continue;
                    if (def0.Flight != null)
                    {
                        // a helicopter needs room for its rotor as well as flat ground for its skids
                        if (!fitProbes.TryGetValue(kind, out var fit)) { fitProbes[kind] = fit = FlightProbe.Make(def0, Water, FlightBounds.None); World.AddChild(fit); }
                        if (!fit.Fits(new Vector3(p.X, gy, p.Z), yaw)) continue;
                    }
                    spots.Add((new NVec3(p.X, gy, p.Z), yaw, kind));
                    return true;
                }
                return false;
            }
            for (var i = 0; i < kinds.Count; i++)
            {
                var kind = kinds[i];
                var def = Vehicles.Vehicles[kind];
                var anchor = anchors.Count > 0 ? anchors[i % anchors.Count] : Spawns[0];
                var near = Spawns.Where(s => Dist2D(s, anchor) <= 40f).OrderBy(_ => _rng.Next()).ToList();
                // amphibious: a shore candidate near its cluster first, so it can drive straight into the water
                var ok = (def.Medium == VehicleMedium.Amphibious && Place(kind, Spawns.Where(s => Dist2D(s, anchor) <= 90f && Water.ShoreDistance(s.X, s.Z) <= 40f).OrderBy(_ => _rng.Next())))
                    || Place(kind, near)
                    || Place(kind, Spawns.Where(s => Dist2D(s, anchor) <= 90f).OrderBy(s => Dist2D(s, anchor)))
                    || Place(kind, Spawns.OrderBy(_ => _rng.Next()));
                if (!ok) GD.PrintErr($"server: no spot for {def.Id}");
            }
            foreach (var fit in fitProbes.Values) fit.QueueFree();
            spots.AddRange(fixedSpots);
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
                FlightWater = Water, FlightBounds = MapFlightBounds,
            };
            World.AddChild(car);
            _cars.Add(car);
        }
        GD.Print($"server: {_cars.Count} vehicles spawned ({string.Join(", ", colours.Select(k => $"{Vehicles!.Vehicles[k.Key].Id} x{k.Value.Count} colours {string.Join(",", k.Value)}"))})");
    }

    void ResetCars()
    {
        foreach (var cl in _clients.Values) DetachFromCar(cl); // out of the seat for good: Car, and Body.InVehicle with it (a body left flagged cannot go prone)
        foreach (var car in _cars) { if (car.Retired) continue; car.Seat.Clear(); car.Reset(); RestoreVehicleGlass(car); }
        RestoreGlassForRound();
        RestoreLampsForRound();
        RestoreSignsForRound();
        _carResets += _cars.Count > 0 ? 1 : 0;
    }

    /// <summary>The vehicle the interact key would get a player at <paramref name="p"/> into: the nearest with a boardable seat within its enter radius, or null.</summary>
    ServerCar? EnterableCar(Vector3 p)
    {
        ServerCar? best = null; var bd = float.MaxValue;
        foreach (var car in _cars)
        {
            if (car.Destroyed || car.Seat.Count == 0 || !car.Seat.HasBoardable) continue; // a seat held by a corpse counts: it is kicked out (#330); a trailer has no seat at all
            // within the vehicle's enter radius of its footprint (#217), not of its centre
            if (!VehicleReach.CanEnter(N(car.GlobalPosition), car.State.Yaw, car.HalfWidth, car.HalfLength, N(p), Vehicles!.EnterRadiusOf(car.Def))) continue;
            var d = VehicleReach.DistanceToFootprint(N(car.GlobalPosition), car.State.Yaw, car.HalfWidth, car.HalfLength, N(p));
            if (d <= bd) { best = car; bd = d; }
        }
        return best;
    }

    bool TryEnterCar(Client c)
    {
        var p = c.Body.GlobalPosition;
        if (EnterableCar(p) is not { } best) return false;
        // the free seat nearest to the player (a roof gunner seat is a little further out than the car's centre)
        var seat = best.Seat.NearestFreeSeat(best.Def, N(best.GlobalPosition), best.State.Yaw, N(p), float.MaxValue, kickCorpses: true); // any free seat of a vehicle in reach: the nearest one
        var wasEmpty = !best.Seat.Occupied;
        if (seat < 0 || !best.Seat.TryBoard(c.Id, seat, out var kicked)) return false;
        if (kicked) KickSeatedCorpse(best, seat, N(p));
        c.Car = best; c.CarSeat = seat; c.Throttle = c.Steer = 0; c.Handbrake = false; c.Body.InVehicle = true; c.FlakSent = null; c.HeadOff = c.HeadPitch = 0;
        if (wasEmpty) best.Lights = Headlights.OnEnterAt(Clock.HourAt(_elapsed)); // night: on as the first rider gets in
        c.Body.Teleport(SeatPos(best, seat));
        _carEnters++;
        GD.Print($"server: {c.Name} entered {best.Def.Id} {best.Id} seat {seat} ({best.Def.Seats[seat].Role})");
        return true;
    }

    void ExitCar(Client c) => LeaveSeat(c, true);

    /// <summary>
    /// The one way a player stops being seated (exit, death, disconnect, round restart, a thrown-out survivor): the seat is freed, the lights go off when the
    /// vehicle is empty, and <see cref="Client.Car"/> and the body's <c>InVehicle</c> flag are cleared together. A body left flagged would keep prone blocked on
    /// the server only, so prediction and the server would disagree. Returns the vehicle the player left, null when they were not seated.
    /// </summary>
    ServerCar? DetachFromCar(Client c)
    {
        var car = c.Car;
        if (car == null) return null;
        car.Seat.Exit(c.Id);
        if (!car.Seat.Occupied) car.Lights = false; // off when empty
        c.Car = null;
        c.Body.InVehicle = false;
        return car;
    }

    /// <summary>The player leaves their seat (any seat); <paramref name="placeOnGround"/> puts the body on a free side of the car (not for death, the respawn moves it).</summary>
    void LeaveSeat(Client c, bool placeOnGround)
    {
        var seat = c.CarSeat;
        if (DetachFromCar(c) is not { } car) return;
        _carExits++;
        if (placeOnGround) c.Body.Teleport(FreeSide(c, car));
        GD.Print($"server: {c.Name} left {car.Def.Id} {car.Id} (seat {seat})");
    }

    /// <summary>A rider's request for another seat of the vehicle they sit in (<see cref="EventKind.SeatSwitch"/>). Anything invalid is ignored.</summary>
    void OnSeatSwitch(Client c, byte[] payload)
    {
        if (SeatCodec.DecodeSwitch(payload) is not { } seat) return;
        if (c.Car is not { Destroyed: false } car || _match.Get(c.Id)?.Life != PlayerLife.Alive) return;
        if (_streaks.ControlOf(c.Id) != null) return; // flying a streak's point of view: the seat is not theirs to change now
        SwitchSeat(c, car, seat);
    }

    /// <summary>
    /// The seated player moves to <paramref name="seat"/> without leaving the vehicle: the old seat is freed (a driver's seat then has no input, so the
    /// vehicle coasts to a stop; a gunner's turret keeps its aim) and the new one is set up as entering it does (the body on the seat, the gunner's ammo
    /// sent anew). False when the seat is refused.
    /// </summary>
    bool SwitchSeat(Client c, ServerCar car, int seat)
    {
        var from = c.CarSeat;
        if (seat >= 0 && seat < car.Seat.Count && car.Seat.HoldsCorpse(seat) && car.Seat.SeatOf(c.Id) == from) KickSeatedCorpse(car, seat, N(c.Body.GlobalPosition)); // a corpse sits there: out it goes (#330)
        if (car.Seat.SeatOf(c.Id) != from || !car.Seat.TrySwitch(c.Id, seat)) return false;
        c.CarSeat = seat; c.Throttle = c.Steer = 0; c.Handbrake = false; c.FlakSent = null; c.HeadOff = c.HeadPitch = 0;
        c.Body.Teleport(SeatPos(car, seat));
        _match.SetPosition(c.Id, N(c.Body.GlobalPosition));
        _seatSwitches++;
        GD.Print($"server: {c.Name} moved in {car.Def.Id} {car.Id} from seat {from} to seat {seat} ({car.Def.Seats[seat].Role})");
        return true;
    }

    /// <summary>The occupants of the car's seats, in seat order (the seats record's source).</summary>
    static ushort[] SeatOccupants(ServerCar car)
    {
        var o = new ushort[car.Seat.Count];
        for (var s = 0; s < o.Length; s++) o[s] = car.Seat.Occupant(s);
        return o;
    }

    /// <summary>Tells the riders who sits where when that has changed since the last send (an entry, an exit, a switch, a seat freed by a death or a drop).</summary>
    void SendSeatState(ServerCar car)
    {
        Span<ushort> now = stackalloc ushort[car.Seat.Count]; // every tick for every car: no heap array unless the seats changed
        for (var s = 0; s < now.Length; s++) now[s] = car.Seat.Occupant(s);
        if (car.SeatsSent != null && car.SeatsSent.AsSpan().SequenceEqual(now)) return;
        car.SeatsSent = now.ToArray();
        var msg = new EventMsg((byte)EventKind.SeatState, SeatCodec.EncodeState(car.Id, car.SeatsSent));
        foreach (var o in Occupants(car)) Send(o, msg);
    }

    /// <summary>World position of a seat of the car (its offset in the car's frame). A flak truck's gunner sits on the turret, so the seat turns with it.</summary>
    static Vector3 SeatPos(ServerCar car, int seat) => SeatPosAt(car, seat, PresentPose(car));

    /// <summary>
    /// A seat's world position with the car at pose <paramref name="pose"/> (the rewound pose of a shot, B-02, or the present one): the offset in the
    /// car's frame turned by the yaw, and by the attitude for a helicopter or a driven vehicle.
    /// </summary>
    static Vector3 SeatPosAt(ServerCar car, int seat, VehiclePose pose)
    {
        var offset = car.Def.Seats[seat].Offset;
        if (car.Def.Flak is { } flak && car.Def.Seats[seat].Role == "gunner") offset = FlakRules.TurretSeatOffset(flak, offset, pose.TurretYaw);
        var p = car.Flies || car.Drives // a helicopter's seats tilt with it; a driven vehicle's lie on the slope it stands on
            ? pose.Feet + FlightMath.Rotate(new System.Numerics.Vector3(offset[0], offset[1], offset[2]), pose.Yaw, pose.Pitch, pose.Roll)
            : VehicleSeat.SeatPosition(pose.Feet, pose.Yaw, offset);
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>The connected player in <paramref name="seat"/>, or null (empty, or the player has gone).</summary>
    Client? OccupantAt(ServerCar car, int seat)
    {
        if (seat < 0 || seat >= car.Seat.Count) return null; // a trailer has no driver
        var id = car.Seat.Occupant(seat);
        return id != 0 && _byId.TryGetValue(id, out var c) ? c : null;
    }

    List<Client> Occupants(ServerCar car)
    {
        var list = new List<Client>(car.Seat.Count);
        for (var s = 0; s < car.Seat.Count; s++) if (OccupantAt(car, s) is { } o) list.Add(o);
        return list;
    }

    /// <summary>A gunner's inputs: reload when asked, then one round when the trigger is held and the gun is ready.</summary>
    void GunnerInput(Client c, NetInput i)
    {
        if (c.Car!.Def.Flak is { } flak) { FlakGunnerInput(c, c.Car, flak, i); return; }
        if (c.Car.Def.DoorGun is { } doorGun) { DoorGunnerInput(c, c.Car, doorGun, i); return; }
        var gun = c.Car!.Gun!;
        if ((i.Buttons & (ushort)MoveButtons.Reload) != 0) gun.TryReload(_elapsed);
        if (gun.TryFire(_elapsed, (i.Buttons & (ushort)MoveButtons.Fire) != 0)) FireGun(c, c.Car, i, gun.Def);
    }

    /// <summary>
    /// One round of a car's gun from a gunner's seat: hitscan along the gunner's view with spread. The car's own riders are not targets,
    /// and the gunner gets the kill (no roadkill: that is for the driver's run-over).
    /// </summary>
    void FireGun(Client c, ServerCar car, NetInput i, VehicleGunDef def)
    {
        _vehicleView = ViewOf(VehicleBackOf(c), c.LifeStart); // the other vehicles, where the gunner's screen showed them (B-02)
        var eye = SeatPos(car, c.CarSeat) + Vector3.Up * 1.4f;
        var yaw = Quant.Yaw(i.Yaw); var pitch = Quant.Pitch(i.Pitch);
        var d = HitMath.SpreadDirection(yaw, RecoilState.ClampPitch(pitch), def.SpreadDegrees, _rng);
        var dir = new Vector3(d.X, d.Y, d.Z);
        List<GlassCross>? crossed = null;
        var wall = BulletHit(eye, dir, def.Range, out var shotCar, ref crossed, solid: car); // the gunner's own car stops the round as before: its glass is not shot out from the roof
        var targets = LiveTargets().Where(t => car.Seat.SeatOf(t.Id) < 0 && !IsArmouredRider(t)).ToList();
        // riders whose seat is in the open (an amphibious transport's) are reached through their hull, as by every weapon
        var (riders, pastHull) = ExposedGunnerBehind(shotCar == car ? null : shotCar, eye, dir, def.Range, targets, seatsOnly: true);
        var hit = GunnerCover.Raycast(N(eye), d, def.Range, wall, targets, c.Id, riders, pastHull);
        var end = eye + dir * (hit?.Distance ?? Math.Min(wall, def.Range));
        BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, SpecialWeapons.VehicleGun, N(eye), N(end))));
        if (hit is { } h) Deal(c.Id, SpecialWeapons.VehicleGun, h.Id, def.Damage, false, false);
        else if (shotCar != null && shotCar != car && Vehicles != null && wall <= def.Range)
            DamageVehicle(shotCar, def.Damage * Vehicles.BulletDamageMultiplier, c.Id, SpecialWeapons.VehicleGun, 0);
        if (hit == null && shotCar != car && wall <= def.Range) ShotGlass(shotCar, eye, dir, wall, def.Damage);
        DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, _ => def.Damage);
        ShotScenery(N(eye), N(dir), hit?.Distance ?? Math.Min(wall, def.Range)); // a pole lamp the round passed breaks (ADR 0037)
        _vehicleView = null;
    }

    /// <summary>Exit position: right, left, behind, in front of the car (first with ground and room for the capsule), else above the roof.</summary>
    Vector3 FreeSide(Client c, ServerCar car)
    {
        var yaw = car.State.Yaw;
        var fwd = car.Forward; var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
        var side = car.HalfWidth + 1f; var along = car.HalfLength + 1.05f; // 2.1 / 3.2 m for the car
        if (car.Flies && !car.Flight.Grounded)
        {
            // out of a helicopter in the air: through the door on the rider's own side, at the cabin floor, and from there it is a fall
            // (the fall damage rules decide what it costs; a low hover is free)
            var seatX = car.Def.Seats[c.CarSeat].Offset[0];
            var first = seatX < 0 ? -1f : 1f;
            foreach (var s in new[] { first, -first })
            {
                var p = car.GlobalPosition + right * (s * side) + Vector3.Up * car.Def.Seats[c.CarSeat].Offset[1];
                if (!c.Body.TestMove(new Transform3D(Basis.Identity, p), Vector3.Zero)) return p;
            }
            return car.GlobalPosition + right * (first * side) + Vector3.Up * car.Def.Seats[c.CarSeat].Offset[1];
        }
        foreach (var off in new[] { right * side, -right * side, -fwd * along, fwd * along })
        {
            var p = car.GlobalPosition + off;
            if (Query.Ground(MoverKind.Player, p, 1.5f, 2.5f) is not { } ground) continue;
            var g = ground.Position + Vector3.Up * 0.1f;
            if (!c.Body.TestMove(new Transform3D(Basis.Identity, g), Vector3.Zero)) return g;
        }
        return car.GlobalPosition + Vector3.Up * 2.5f;
    }

    void StepVehicles(float dt)
    {
        if (Vehicles == null || _cars.Count == 0) return;
        TrailerLatchTick(); // a loose trailer is latched by a tractor that reverses its hitch onto it (before the contacts and the steps: it follows its tractor from this tick on)
        var contactsT = Prof.Begin(Prof.S.VehicleContacts);
        ResolveVehicleContacts();
        Prof.End(Prof.S.VehicleContacts, contactsT);
        foreach (var car in _cars)
        {
            car.CrashSoundGap.Tick(dt);
            var def = car.Def;
            if (car.Retired) continue; // taken out of the world for good (RemoveRocketCarts)
            if (car.Destroyed)
            {
                if (car.Life.ReturnDue(_elapsed)) { car.Reset(); RestoreVehicleGlass(car); _destroyedReturns++; if (BlastSmoke && _destroyedReturns >= 2 && _finishAt == 0) _finishAt = _elapsed + 1.5; GD.Print($"server: {def.Id} {car.Id} returned after its destruction"); }
                continue;
            }
            if (car.IsTrailer) { StepTrailerCar(car, dt); continue; } // no seats, no driver: it follows its tractor, or rolls free
            // seats of players who have gone (disconnect, death) are freed; the driver is seat 0 and only the driver steers
            for (var s = 0; s < car.Seat.Count; s++) if (car.Seat.Occupant(s) != 0 && OccupantAt(car, s) == null) car.Seat.Clear(s);
            SendSeatState(car);
            if (car.Thrown != null) { car.Towing?.Unhitch(); StepThrownVehicle(car, dt); continue; } // flying on the Bigfoot's arc: no driving, no crash, no run-over (issue #264)
            var driver = OccupantAt(car, 0);
            var occupied = car.Seat.Occupied;
            if (occupied) car.Wake(); // somebody aboard: the vehicle is stepped every tick until it is empty and rests again
            car.PrevPos = car.Drives ? car.TickEndPos : car.GlobalPosition; // a driven vehicle has already moved this tick, on its driver's inputs
            var stepT = Prof.Begin(Prof.S.VehicleStep);
            if (car.Flies) StepFlyingVehicle(car, driver, dt);
            else if (car.Drives) StepDrivenVehicle(car, driver, dt);
            else if (driver != null) car.Step(def, driver.Throttle, driver.Steer, driver.Handbrake, dt, Water);
            else if (!car.Asleep || occupied) car.StepParked(dt, Water); // a parked car at rest is not stepped until disturbed (ParkedRules, T2-04)
            Prof.End(Prof.S.VehicleStep, stepT);
            var here = car.GlobalPosition; // read once: a native read each time, every car every tick
            car.TickEndPos = here;
            if (here.Y < KillY) { foreach (var o in Occupants(car)) LeaveSeat(o, false); car.Reset(false); continue; }
            // a wall takes speed from a driven car; a helicopter is hurt by what it hits whoever flies it, or nobody
            if (driver != null || car.Flies) Crash(car, car.LastImpact, 0);
            if (car.Life.DetonationDue(_elapsed)) { Detonate(car); continue; }
            for (var s = 0; s < car.Seat.Count; s++) // the riders are put on their seats (no list per car per tick)
            {
                if (OccupantAt(car, s) is not { } o) continue;
                var at = SeatPos(car, o.CarSeat);
                o.Body.Teleport(at);
                _match.SetPosition(o.Id, N(at));
                if (!RiderPose.FollowsAim(car.Def.Seats[o.CarSeat])) ApplyHeadLook(o, car); // the figure keeps facing along the hull as it turns (a gunner's follows the aim)
            }
            if (driver != null && car.Def.RunOver && !car.Afloat && (!car.Flies || car.Flight.Agl <= car.Def.Flight!.RunOverAgl)) RunOverCheck(car, driver); // road kill: on land only; a helicopter only skimming the ground
        }
    }

    /// <summary>
    /// Vehicles bump into each other (#253). Every pair of live vehicles that overlap (hit box, and a helicopter's rotor disc) is moved apart and
    /// exchanges its closing speed (<see cref="VehicleContact"/>); each loses speed by what the bump took from it, and a loss above the crash
    /// minimum hurts it by the crash rules, credited to the other vehicle's driver (nobody when that one is empty). Riders take nothing from the
    /// bump itself, only from the blast of a vehicle it destroys. The server alone resolves it: the pilot's client predicts against the world, not
    /// against other vehicles (the client sees them a snapshot late), and the reconciliation carries the bump to it.
    /// </summary>
    void ResolveVehicleContacts()
    {
        // each origin is read once here (a native call per read, and every pair asks): only a bump moves a vehicle inside this pass, so only those are read again
        if (_contactAt.Length < _cars.Count) _contactAt = new Vector3[_cars.Count];
        for (var i = 0; i < _cars.Count; i++) _contactAt[i] = _cars[i].GlobalPosition;
        ShoveCarsByVans(); // the Raid vans push through: a vehicle in a van's box is shoved out
        const float range2 = ContactBroadRange * ContactBroadRange;
        for (var i = 0; i < _cars.Count; i++)
        {
            var a = _cars[i];
            if (a.Destroyed || a.Thrown != null) continue; // a thrown vehicle bumps nothing, and is bumped by nothing
            for (var j = i + 1; j < _cars.Count; j++)
            {
                var b = _cars[j];
                if (b.Destroyed || b.Thrown != null) continue;
                if (a.Towing == b || b.Towing == a) continue; // a tractor and its own trailer overlap at a deep articulation by design: they are one body
                if (_contactAt[i].DistanceSquaredTo(_contactAt[j]) > range2) continue;
                var shapeA = a.ContactShape(); var shapeB = b.ContactShape();
                if (VehicleContact.Test(shapeA, shapeB, a.Flies || b.Flies) is not { } hit) continue;
                var bump = VehicleContact.Bump(hit, a.BumpBody(), b.BumpBody(), Vehicles!.BumpRestitution);
                a.ApplyBump(bump.MoveA, bump.DeltaVA);
                b.ApplyBump(bump.MoveB, bump.DeltaVB);
                _contactAt[i] = a.GlobalPosition; _contactAt[j] = b.GlobalPosition;
                BumpDamage(a, bump.SpeedLossA, b);
                BumpDamage(b, bump.SpeedLossB, a);
            }
        }
    }

    /// <summary>Each live vehicle's origin for the broad test of <see cref="ResolveVehicleContacts"/> (reused every tick).</summary>
    Vector3[] _contactAt = Array.Empty<Vector3>();

    /// <summary>Beyond this distance between two origins no pair of vehicles can touch (the bus is 12 m long, a rotor disc 9 m across).</summary>
    const float ContactBroadRange = 20f;

    void BumpDamage(ServerCar car, float speedLoss, ServerCar other) => Crash(car, speedLoss, OccupantAt(other, 0)?.Id ?? 0);

    /// <summary>
    /// The one crash rule (#417, #431): a collision that took <paramref name="speedLoss"/> m/s from <paramref name="car"/> in one tick, whatever it
    /// met (a wall, a tree, a ledge, the ground under a helicopter, another vehicle, a Raid van). Up to crash_min_speed_loss the hull takes
    /// nothing; the panes crack and shatter by their own two speeds (<see cref="CrashGlass"/>). A type with crash_damage off takes neither.
    /// From crash_sound_min_speed_loss it is heard (#410).
    /// </summary>
    void Crash(ServerCar car, float speedLoss, ushort attacker)
    {
        // heard by everyone, whatever the type takes from it (#410); one per vehicle in crash_sound_gap, so grinding along a wall is not a crash every tick
        if (Vehicles!.CrashSoundOf(speedLoss) is var sound and not CrashSound.None && car.CrashSoundGap.StartIfReady(Vehicles.CrashSoundGap))
            BroadcastEvent(EventKind.VehicleCrash, VehicleCrashCodec.Encode(car.Id, sound));
        if (!car.Def.CrashDamage) return;
        CrashGlass(car, speedLoss); // before the hull: a crash that dooms the vehicle still breaks its glass
        var damage = Vehicles!.CrashDamage(speedLoss);
        if (damage > 0) DamageVehicle(car, damage, attacker, SpecialWeapons.Vehicle, 0);
    }

    /// <summary>
    /// The tick of a flying vehicle (the utility helicopter, ADR 0006). With a pilot, the flight was already stepped in <see cref="SimulateClient"/>, once
    /// for each of the pilot's inputs (that is what lets the pilot's client predict it), so there is nothing to step here unless the inputs have
    /// stopped arriving. Without one the stabiliser flies: a hover while riders are aboard, then down (<see cref="FlightModel"/>). The hardest impact
    /// since the last tick goes to the crash damage rules, and skids in water cost hit points every second.
    /// </summary>
    void StepFlyingVehicle(ServerCar car, Client? pilot, float dt)
    {
        if (pilot == null)
        {
            // a helicopter resting on the ground is not stepped until disturbed (FlightRest); the flight tick still follows the server tick, as a step would leave it
            if (car.Asleep) car.FlightTick = _tick;
            else car.StepFlightUnpiloted(dt, _tick);
        }
        else if (_tick - car.FlightTick > FlightStarveTicks) car.StepFlight(FlightInput.Idle, dt, _tick);
        car.LastImpact = car.FlightImpact;
        car.FlightImpact = 0;
        if (car.Flight.InWater) DamageVehicle(car, car.Def.Flight!.WaterDamagePerSecond * dt, 0, SpecialWeapons.Vehicle, 0);
    }

    /// <summary>A driver whose inputs have not arrived for this many ticks is driven hands off (it coasts), so a stalled client does not freeze the vehicle.</summary>
    const uint DriveStarveTicks = 6;

    /// <summary>
    /// The tick of a driven vehicle (the amphibious transport, ADR 0010; the car, ADR 0014). With a driver, it was already stepped in <see cref="SimulateClient"/>, once
    /// for each of the driver's inputs (that is what lets the driver's client predict it), so there is nothing to step here unless the inputs have
    /// stopped arriving. With nobody at the wheel it is stepped hands off every tick: it coasts to a stop, on its wheels or on the water. The hardest
    /// impact since the last tick goes to the crash damage rules.
    /// </summary>
    void StepDrivenVehicle(ServerCar car, Client? driver, float dt)
    {
        if (driver != null && driver == _harvDriver)
        {
            // the harvester's own driver sends no inputs: the server steers it (NetServer.Harvester), one step a tick
            car.Wake();
            car.StepDrive(new DriveInput { Throttle = driver.Throttle, Steer = driver.Steer, Handbrake = driver.Handbrake }, dt);
        }
        else if (driver == null || _tick - car.DriveTick > DriveStarveTicks)
        {
            if (!car.Asleep) car.StepDriveUnpiloted(dt); // a parked one at rest is not stepped until disturbed (DriveRest)
        }
        car.LastImpact = car.DriveImpact;
        car.DriveImpact = 0;
    }

    /// <summary>
    /// Hurts a vehicle (bullet, rocket, blast or wall impact). At 0 HP it is scheduled to blow up <paramref name="delay"/> seconds from now
    /// (0 = in this tick's vehicle step). <paramref name="attacker"/> 0 means nobody: the blast is then credited to nobody (or the driver's own crash).
    /// </summary>
    void DamageVehicle(ServerCar car, float amount, ushort attacker, byte weapon, float delay)
    {
        car.Wake();
        if (car.Destroyed || amount <= 0 || Vehicles == null) return;
        if (_match.VehicleHitBlocked(attacker, SeatOccupants(car))) return; // a teammate aboard: no damage, no hit credit (#338)
        if (attacker != 0 && _byId.TryGetValue(attacker, out var attackerClient)) amount *= attackerClient.Perks.VehicleDamageDealt; // Tank Hunter
        if (attacker != 0) { car.LastAttacker = attacker; car.LastWeapon = weapon; }
        var before = VehicleDamage.Stage(car.Hp, car.Life.MaxHp, Vehicles.DamageStages);
        car.Life.Damage(amount, _elapsed, delay); // at 0 HP the blast is scheduled `delay` seconds from now (0 = this tick's vehicle step)
        _vehicleHits++;
        var stage = VehicleDamage.Stage(car.Hp, car.Life.MaxHp, Vehicles.DamageStages);
        if (stage != before) GD.Print($"server: {car.Def.Id} {car.Id} damage stage {before} -> {stage} (hp {car.Hp:F0}/{car.Life.MaxHp:F0})");
    }

    /// <summary>
    /// The vehicle blows up: broadcast the event, kill the driver, splash everyone alive and every other vehicle in range (those that run out of HP
    /// go off after the chain delay), then the vehicle is gone until it returns. The last attacker gets the credit.
    /// </summary>
    void Detonate(ServerCar car)
    {
        var at = car.GlobalPosition + Vector3.Up * 0.8f;
        var driver = OccupantAt(car, 0);
        var attacker = car.LastAttacker != 0 ? car.LastAttacker : driver?.Id ?? (ushort)0;
        var weapon = car.LastWeapon != 0 ? car.LastWeapon : SpecialWeapons.Vehicle;
        var carVelocity = CarVelocity(car); // before Destroy zeroes it: the riders' bodies fly on with it
        var loadedCart = car.Cargo?.Detonates(car.Def) ?? false; // before the destruction: a rocket cart that still carries its missile
        car.Destroy(car.Def.Returns ? _elapsed + Vehicles.DestroyedReturnTime : double.MaxValue); // a type that never returns (the Scud truck) is gone for the match
        _vehicleBlasts++;
        if (loadedCart) OnRocketCartDetonated(car, N(car.GlobalPosition), attacker); // it does not blow up like a vehicle: the Scud's blast is the hook's
        else VehicleBlast(car, at, attacker, weapon, carVelocity);
    }

    /// <summary>
    /// The ordinary vehicle explosion of <paramref name="car"/> (already destroyed, its life and its return set): the event for every client, the ground thrown up, the panes, then everyone aboard
    /// and everyone and every vehicle within the blast's reach. Credited to <paramref name="attacker"/>.
    /// </summary>
    void VehicleBlast(ServerCar car, Vector3 at, ushort attacker, byte weapon, NVec3 carVelocity)
    {
        var e = Vehicles!.Explosion;
        BroadcastEvent(EventKind.VehicleExplosion, WeaponCodec.Encode(new VehicleExplosionMsg(car.Id, attacker, N(at), e.Splash.Radius)));
        BroadcastImpact(N(car.GlobalPosition), ImpactSize.Plume); // the blast throws up dirt or road where the car sits (ticket #154)
        BlastGlass(at, e.Splash.Radius);
        GD.Print($"server: {car.Def.Id} {car.Id} exploded at ({at.X:F1},{at.Y:F1},{at.Z:F1}), credited to #{attacker}");

        ThrowRidingCorpses(car, carVelocity, N(at), e.Splash, 1f); // the dead pilots and drivers still in their seats are flung out (ADR 0026)
        foreach (var o in Occupants(car))
        {
            // a rider who dies is thrown with the vehicle's velocity plus the blast's knockback from its centre (no corpse stays seated in a wreck)
            var k = ProjectileMath.Knockback(e.Splash.KnockbackSpeed, N(at), N(o.Body.GlobalPosition) + NVec3.UnitY * 0.9f, 1f);
            PushOnDeath(o.Id, (k == NVec3.Zero ? NVec3.Zero : CorpseRules.ThrowFromBlast(k)) + carVelocity);
            var dealt = Deal(attacker, weapon, o.Id, e.DriverDamage, false, false); // everyone still aboard (a gunner too)
            ClearPush(o.Id, dealt);
            if (o.Car == car) LeaveSeat(o, true); // survived (spawn protection): thrown out, the seat is gone
        }
        foreach (var t in LiveTargets())
        {
            if (car.Seat.SeatOf(t.Id) >= 0) continue;
            var dist = HitMath.DistanceToBody(N(at), t);
            var dmg = VehicleDamage.BlastDamage(e, dist);
            if (dmg <= 0) continue;
            var share = SplashShare(N(at), t); // walls, a bunker room's too, and a covered seat's hull
            if (share <= 0) continue;
            dmg *= share;
            var self = t.Id == attacker;
            if (self) dmg = MathF.Max(1f, MathF.Round(dmg * e.Splash.SelfDamageMultiplier));
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(e.Splash.KnockbackSpeed, N(at), HitMath.Centre(t), dmg / e.Damage);
            PushOnDeath(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = Deal(attacker, weapon, t.Id, dmg, false, false);
            ClearPush(t.Id, res);
            if (res == DamageResult.Ignored) continue;
            _blastHits++;
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v) && k != NVec3.Zero) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z));
        }
        foreach (var other in _cars)
        {
            if (other == car || other.Destroyed) continue;
            var dist = HullDistanceNow(other, N(at));
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
                var dist = HullDistanceNow(car, N(pos));
                dmg = ProjectileMath.SplashDamage(w, dist, false);
            }
            DamageVehicle(car, dmg * Vehicles!.RocketDamageMultiplier, owner, weapon, 0);
        }
    }

    /// <summary>
    /// A vehicle that moves (<see cref="RunOver.Contacts"/>), driven, on land or skimming it: its row of the rule table runs over every live target it sweeps (<see cref="RunOverTargets"/>).
    /// Below the speed that hurts the damage is 0: the body is only pushed out of its way (<see cref="RunOverOne"/>).
    /// </summary>
    void RunOverCheck(ServerCar car, Client driver)
    {
        if (!RunOver.Contacts(car.State.Speed)) return;
        var dmg = RunOver.Damage(Vehicles!, car.State.Speed, Vehicles!.RunOverScale(car.Def));
        RunOverTargets(new RunOverSweep(RunOverMoverOf(car), N(car.PrevPos), N(car.GlobalPosition), car.State.Yaw, car.HalfWidth, car.HalfLength, N(car.Forward), car.State.Speed, dmg, driver.Id, car.HitCooldown));
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
        var needKill = maxHit >= Vehicles.RunOverMaxDamage; // a slow vehicle cannot kill a full-health player in one pass
        var ok = _carEnters >= 1 && _carExits >= 1 && _runOvers >= 1 && (_runOverKills >= 1 || !needKill) && _shotsFired == 0 && _cars.Count >= 3;
        GD.Print($"server: car smoke kind={SmokeKind} cars={_cars.Count} enters={_carEnters} exits={_carExits} runOvers={_runOvers} runOverKills={_runOverKills} shotsWhileDriving={_shotsFired}");
        GD.Print(ok ? "SHOOTER_CAR_SERVER_OK" : "SHOOTER_CAR_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }
}
