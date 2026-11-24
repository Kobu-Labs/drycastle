using Drycastle.Shooter.Net.Server.Diagnostics;
using Godot;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Net.Server.BusGarage;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// The deal of the vehicles at the start, once the spawn candidates are validated: near the spawn zones, at least 12 m apart, random colours; the types' fixed spots stand last in the list.
/// A smoke arena has its cars at fixed places instead. A vehicle never despawns while abandoned (#294): it returns to its origin only when destroyed, or out of the world.
/// </summary>
internal sealed class CarDealer
{
    const float ClusterRadius = 120f;

    readonly Fleet _fleet;
    readonly VehiclesFile _vehicles;
    readonly Node3D _world;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Water _water;
    readonly List<NVec3> _spawns;
    readonly Random _rng;
    readonly GarageServer _garage;
    readonly bool _blastSmoke;
    readonly bool _carSmoke;
    readonly string _smokeKind;

    /// <param name="spawns">The players' spawn candidates: the dealt vehicles take the room next to them away (the list is shortened here).</param>
    /// <param name="smokeKind">The vehicle type of the car smoke arena (vehicles.json id).</param>
    public CarDealer(Fleet fleet, VehiclesFile vehicles, Node3D world, Drycastle.MapLoader.MapLoader? map, Water water, List<NVec3> spawns, Random rng, GarageServer garage, bool blastSmoke, bool carSmoke, string smokeKind)
    {
        _fleet = fleet; _vehicles = vehicles; _world = world; _map = map; _water = water; _spawns = spawns; _rng = rng; _garage = garage;
        _blastSmoke = blastSmoke; _carSmoke = carSmoke; _smokeKind = smokeKind;
    }

    /// <summary>Places the cars: a smoke arena's at fixed spots, else dealt over the map.</summary>
    public void Deal()
    {
        var spots = new List<(NVec3 Pos, float Yaw, int Kind)>();
        if (_blastSmoke || _carSmoke) AddSmokeArenaSpots(spots);
        else DealSpots(_world.GetWorld3D().DirectSpaceState, spots);
        CreateCars(spots);
    }

    /// <summary>The smoke arenas' cars, at fixed spots.</summary>
    void AddSmokeArenaSpots(List<(NVec3 Pos, float Yaw, int Kind)> spots)
    {
        var kind = _vehicles.Vehicles.FindIndex(v => v.Id == _smokeKind);
        var c = new NVec3(SmokeArena.X, 0, SmokeArena.Z);
        if (_blastSmoke)
        {
            // car 0 is shot to pieces, car 1 stands 6 m beside it (chain damage, survives), car 2 is far away (rocket target, never in a blast)
            foreach (var o in new[] { new NVec3(0, 0, -30), new NVec3(6, 0, -30), new NVec3(0, 0, -70) }) spots.Add((c + o, 0, kind));
            return;
        }
        if (kind < 0) throw new InvalidOperationException($"unknown --car-kind {_smokeKind}");
        foreach (var o in new[] { new NVec3(-40, 0, 60), new NVec3(-80, 0, 60), new NVec3(-40, 0, 100) }) spots.Add((c + o, 0, kind));
    }

    /// <summary>
    /// The deal on the real map: vehicles stand around the player spawn areas. The spawn candidates are clustered greedily (most uncovered candidates within ClusterRadius),
    /// the vehicles of vehicles.json (cars first) are dealt round-robin over the clusters and each takes a free candidate within 40 m of its
    /// cluster anchor (a random one), at least 12 m from every other vehicle, on real ground under the whole footprint; then the fixed spots join, and spawn candidates next to a vehicle are dropped.
    /// </summary>
    void DealSpots(PhysicsDirectSpaceState3D space, List<(NVec3 Pos, float Yaw, int Kind)> spots)
    {
        var kinds = new List<int>();
        var garaged = _garage.Parking()?.Kind ?? -1; // one of that type stands in the garage instead of being dealt
        for (var kind = 0; kind < _vehicles.Vehicles.Count; kind++)
            for (var n = kind == garaged ? 1 : 0; n < _vehicles.Vehicles[kind].SpawnCount; n++) kinds.Add(kind);
        var anchors = ClusterAnchors(_spawns, Math.Max(1, Math.Min(6, (kinds.Count + 2) / 3)), ClusterRadius);
        var fitProbes = new Dictionary<int, FlightProbe>();
        var fixedSpots = PlanFixedSpots(space); // the random deal keeps 12 m clear of them; they join the list after it, so the ids of the dealt vehicles do not move
        bool Place(int kind, IEnumerable<NVec3> cands)
        {
            var def0 = _vehicles.Vehicles[kind];
            foreach (var p in cands)
            {
                if (spots.Any(s => NVec3.Distance(s.Pos, p) < 12f) || fixedSpots.Any(s => NVec3.Distance(s.Pos, p) < 12f)) continue;
                var yaw = (float)(_rng.NextDouble() * Math.PI * 2);
                if (ServerCar.RestHeight(space, new Vector3(p.X, p.Y, p.Z), yaw, def0.HalfWidth, def0.HalfLength, p.Y) is not { } gy) continue;
                if (def0.Flight != null)
                {
                    // a helicopter needs room for its rotor as well as flat ground for its skids
                    if (!fitProbes.TryGetValue(kind, out var fit)) { fitProbes[kind] = fit = FlightProbe.Make(def0, _water, FlightBounds.None); _world.AddChild(fit); }
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
            var def = _vehicles.Vehicles[kind];
            var anchor = anchors.Count > 0 ? anchors[i % anchors.Count] : _spawns[0];
            var near = _spawns.Where(s => CarPose.Flat(s, anchor) <= 40f).OrderBy(_ => _rng.Next()).ToList();
            // amphibious: a shore candidate near its cluster first, so it can drive straight into the water
            var ok = (def.Medium == VehicleMedium.Amphibious && Place(kind, _spawns.Where(s => CarPose.Flat(s, anchor) <= 90f && _water.ShoreDistance(s.X, s.Z) <= 40f).OrderBy(_ => _rng.Next())))
                || Place(kind, near)
                || Place(kind, _spawns.Where(s => CarPose.Flat(s, anchor) <= 90f).OrderBy(s => CarPose.Flat(s, anchor)))
                || Place(kind, _spawns.OrderBy(_ => _rng.Next()));
            if (!ok) GD.PrintErr($"server: no spot for {def.Id}");
        }
        foreach (var fit in fitProbes.Values) fit.QueueFree();
        spots.AddRange(fixedSpots);
        // players must not respawn inside a vehicle: drop the spawn candidates next to one (keep a healthy pool)
        var free = _spawns.Where(s => spots.All(v => NVec3.Distance(v.Pos, s) > 4f)).ToList();
        if (free.Count >= 20) { _spawns.Clear(); _spawns.AddRange(free); }
        foreach (var (pos, _, kind) in spots)
        {
            var anchorD = anchors.Select(a => CarPose.Flat(a, pos)).DefaultIfEmpty(float.MaxValue).Min();
            GD.Print($"server: vehicle spot {_vehicles.Vehicles[kind].Id} at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}), {anchorD:F0} m from the nearest spawn-cluster anchor, nearest player spawn {_spawns.Select(s => CarPose.Flat(s, pos)).DefaultIfEmpty(-1).Min():F0} m");
        }
    }

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
                foreach (var u in uncovered) if (CarPose.Flat(a, u) <= radius) score++;
                if (score > bestScore) { best = a; bestScore = score; }
            }
            anchors.Add(best);
            uncovered.RemoveAll(u => CarPose.Flat(best, u) <= radius);
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
        if (_map == null) return planned;
        List<RoadLine>? roads = null;
        for (var kind = 0; kind < _vehicles.Vehicles.Count; kind++)
        {
            var def = _vehicles.Vehicles[kind];
            foreach (var spot in def.FixedSpots)
            {
                roads ??= RoadNetwork.Parse(_map.RoadsJson);
                var yaw = RoadHeading.YawFor(spot, roads);
                var terrain = _map.GroundHeight(spot.X, spot.Z);
                if (float.IsNaN(terrain)) { GD.PrintErr($"server: fixed spot of {def.Id} at ({spot.X:F1},{spot.Z:F1}) has no ground, left out"); continue; }
                var gy = ServerCar.RestHeight(space, new Vector3(spot.X, terrain, spot.Z), yaw, def.HalfWidth, def.HalfLength, terrain) ?? terrain;
                planned.Add((new NVec3(spot.X, gy, spot.Z), yaw, kind));
                GD.Print($"server: fixed spot {def.Id} at ({spot.X:F1},{gy:F1},{spot.Z:F1}) facing {yaw * 180f / MathF.PI:F0} deg");
                if (def.Flight != null) WarnIfAircraftDoesNotFit(def, new Vector3(spot.X, gy, spot.Z), yaw);
            }
        }
        if (_garage.Parking() is { } park) // the bus garage's own vehicle, nose to the doors (garage.json)
        {
            var def = _vehicles.Vehicles[park.Kind];
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
        var probe = FlightProbe.Make(def, _water, FlightBounds.None);
        _world.AddChild(probe);
        if (!probe.Fits(at, yaw)) GD.PrintErr($"server: fixed spot of {def.Id} at ({at.X:F1},{at.Z:F1}): its hull or rotor disc touches something solid there (it stands there anyway)");
        probe.QueueFree();
    }

    /// <summary>One <see cref="ServerCar"/> per spot, in the list's order (the ids follow it), each in a colour of its type not yet taken.</summary>
    void CreateCars(List<(NVec3 Pos, float Yaw, int Kind)> spots)
    {
        var colours = new Dictionary<int, List<byte>>();
        foreach (var (pos, yaw, kind) in spots)
        {
            var def = _vehicles.Vehicles[kind];
            if (!colours.TryGetValue(kind, out var taken)) colours[kind] = taken = new List<byte>();
            var colour = VehicleColours.Pick(_rng, taken, def.Variants);
            taken.Add(colour);
            _fleet.Make(kind, def, colour, new Vector3(pos.X, pos.Y + 0.02f, pos.Z), yaw);
        }
        GD.Print($"server: {_fleet.Count} vehicles spawned ({string.Join(", ", colours.Select(k => $"{_vehicles.Vehicles[k.Key].Id} x{k.Value.Count} colours {string.Join(",", k.Value)}"))})");
    }
}
