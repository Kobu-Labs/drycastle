using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server.Cartel;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// Where a van of the wave comes in, stops and leaves (GLOSSARY "Raid"; ADR 0021; docs/events/thieves/03): the road graph of the map's roads.json cut to the roads a van may use, the fixed routes
/// (<c>raids.routes</c>) resolved on it, and the plan of one van (<see cref="RaidRoutes"/>, <see cref="RaidPlanner"/>). Nothing is read as a graph before the first Raid. Everything it needs from the
/// world is a function: the ground height, the navigation mesh, the church.
/// </summary>
internal sealed class RaidLauncher
{
    readonly ServerContext _ctx;
    readonly ThievesFile _data;
    readonly ThievesEvent _wave;
    readonly RaidRoster _roster;
    readonly Drycastle.MapLoader.MapLoader? _map;
    readonly Func<BotNav?> _nav;
    readonly Func<FlightBounds> _bounds;
    readonly Func<NVec2?> _parkCentre;
    readonly Random _rng;
    readonly OpenGround? _open;
    RoadGraph? _roads;
    RoadGraph? _routeRoads;
    List<ResolvedRoute>? _routes;

    /// <summary>The route the first van of the wave took: the others follow it (null: the automatic way in, or no wave yet).</summary>
    ResolvedRoute? _waveRoute;

    public RaidLauncher(ServerContext ctx, ThievesFile data, ThievesEvent wave, RaidRoster roster, Drycastle.MapLoader.MapLoader? map, Func<BotNav?> nav, Func<FlightBounds> bounds,
        Func<NVec2?> churchDoorway, Random rng, OpenGround? open = null)
    {
        _ctx = ctx; _data = data; _wave = wave; _roster = roster; _map = map; _nav = nav; _bounds = bounds; _rng = rng; _open = open;
        _parkCentre = () => data.Raids.ParkCentre is { } c ? new NVec2(c[0], c[1]) : churchDoorway(); // where a van parks near: raids.park_centre, else the church's doorway, else null (the end of its route)
    }

    /// <summary>The admin who started the event: a van with no way in is parked near them.</summary>
    public ushort Admin { get; set; }

    public bool NoRoads => Roads.IsEmpty;

    /// <summary>The roads a van may use (at least <see cref="VanConfig.MinRoadWidthM"/> wide), read once; empty without a map. A Heist puts its Cartel van on them too.</summary>
    public RoadGraph Roads => _roads ??= _map != null ? RoadGraph.Parse(_map.RoadsJson, _data.Van.MinRoadWidthM) : RoadGraph.Empty;

    /// <summary>The roads the fixed routes are driven over: every road wide enough (<c>raids.route_min_road_width_m</c>, 0: footpaths too).</summary>
    RoadGraph RouteRoads => _routeRoads ??= _map != null ? RoadGraph.Parse(_map.RoadsJson, _data.Raids.RouteMinRoadWidthM) : RoadGraph.Empty;

    /// <summary>The fixed routes resolved on the road graph, once: the ones this map cannot drive are logged and left out. Empty: the automatic choice.</summary>
    List<ResolvedRoute> RouteList => _routes ??= RaidRoutes.Resolve(RouteRoads, Roads, _data.Raids.Routes, _data.Raids.RouteSnapMaxM, s => GD.Print($"server: {s}"));

    /// <summary>The map's extent as the Raids see it (the terrain's box, as for a helicopter: where a road crosses it, a van comes in). Infinite on both sides without a map: no road crosses it.</summary>
    (NVec2 Min, NVec2 Max) Extent()
    {
        var b = _bounds();
        return (new NVec2(b.MinX, b.MinZ), new NVec2(b.MaxX, b.MaxZ));
    }

    float Ground(float x, float z) => _map == null ? float.NaN : _map.GroundHeight(x, z);

    /// <summary>Humans in the match (the crew grows with them); at least one.</summary>
    int Humans() => Math.Max(1, _ctx.Players.Values.Count(c => !c.IsBot));

    /// <summary>A point to bring a fallback van near: the admin who started the event, else any human, else the map's middle.</summary>
    NVec2 NearPoint()
    {
        if (Admin != 0 && _ctx.Players.TryGetValue(Admin, out var admin)) return new NVec2(admin.Body.GlobalPosition.X, admin.Body.GlobalPosition.Z);
        foreach (var c in _ctx.Players.Values)
            if (!c.IsBot) return new NVec2(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Z);
        var (min, max) = Extent();
        return float.IsFinite(min.X) && float.IsFinite(max.X) ? (min + max) / 2 : NVec2.Zero;
    }

    /// <summary>Whether a van can stop at a road spot: the crew steps out on the navigation mesh beside a side door. Returns the side (1 right, -1 left) that reaches the mesh, or null.</summary>
    int? AcceptStop(NVec2 at, float yaw)
    {
        if (_nav() is not { } nav) return null;
        var y = Ground(at.X, at.Y);
        if (float.IsNaN(y)) return null;
        var vc = _data.Van;
        var fwd = new NVec2(-MathF.Sin(yaw), -MathF.Cos(yaw));
        var right = new NVec2(MathF.Cos(yaw), -MathF.Sin(yaw));
        foreach (var side in new[] { 1, -1 })
        {
            var door = at + right * (side * (vc.HalfWidthM + vc.DoorOutM)) + fwd * vc.DoorAlongM;
            var snap = nav.Snap(new Vector3(door.X, y, door.Y));
            if (MathF.Abs(snap.X - door.X) <= 3f && MathF.Abs(snap.Z - door.Y) <= 3f && MathF.Abs(snap.Y - y) <= 4f) return side;
        }
        return null;
    }

    /// <summary>Where a crew that flees heads for: the nearest point of the map's edge (a point far to the east without a map).</summary>
    NVec3 FleePoint(NVec3 from)
    {
        var (min, max) = Extent();
        return float.IsFinite(min.X) && float.IsFinite(max.X) ? GangPlacement.NearestEdge(from, min, max) : from + new NVec3(1000, 0, 0);
    }

    /// <summary>
    /// The next van of the wave is due (<see cref="ThievesEvent.Tick"/>). The first picks one of the fixed routes (<see cref="RaidRoutes"/>), every other van of the wave drives the same one in a line and
    /// parks at a stop of its own near the church (not within <c>used_apart_m</c> of the others'), so they split in the village; all leave the same way back. With no usable route the old automatic
    /// choice: a road in from the map edge to the stop with the most targets (<paramref name="places"/>: where Loot can be stolen). When the first van finds no way in at all it is put parked at a road point near the admin instead (fallback); a later
    /// van that finds no stop of its own, or no room in the world, is dropped: the wave is one van smaller (<see cref="ThievesEvent.Shrink"/>) and the log says so.
    /// </summary>
    public void Launch(Func<List<NVec2>> places)
    {
        var nav = _nav();
        if (nav == null) return;
        var vc = _data.Van;
        var roads = Roads;
        var (min, max) = Extent();
        var n = _wave.Launched; var of = _wave.Total;
        var first = n == 1;
        if (first) _waveRoute = null;
        var players = _ctx.Players.Values.Where(c => !c.IsBot && _ctx.IsAlive(c.Id)).Select(c => new NVec2(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Z)).ToList();
        RaidPlan? plan = null;
        var routes = first ? RouteList : _waveRoute != null ? new List<ResolvedRoute> { _waveRoute } : new List<ResolvedRoute>();
        if (routes.Count > 0)
        {
            plan = RaidRoutes.Plan(roads, routes, _parkCentre(), _roster.UsedStops, vc, _data.Raids, _rng, AcceptStop);
            if (plan == null) GD.Print($"server: Thieves van {n}/{of}: no {(first ? "fixed route has" : "stop left on the wave's route")} a stop the crew can step out at{(first ? ", the automatic way in is used" : "")}");
            else if (first) _waveRoute = routes.FirstOrDefault(r => r.Name == plan.RouteName);
        }
        else if (first && _data.Raids.Routes.Count > 0) GD.Print($"server: Thieves van {n}/{of}: no fixed route is usable on this map, the automatic way in is used");
        if (plan == null && (first || _waveRoute == null)) plan = RaidPlanner.Plan(roads, min, max, places(), _roster.UsedStops, players, vc, _rng, AcceptStop);
        if (plan == null && first)
        {
            GD.Print($"server: Thieves van {n}/{of}: no way in from the map edge ({(roads.IsEmpty ? "no road graph" : "no entry point or no reachable stop")}), the van is parked near the admin instead (fallback)");
            plan = RaidPlanner.Fallback(roads, NearPoint(), 25f, _rng, AcceptStop);
        }
        if (plan == null) { Shrink($"no stop of its own was found{(first ? " (no road with the navigation mesh beside it)" : "")}"); return; }
        var exit = plan.Out ?? RaidPlanner.Exit(roads, plan, min, max, vc);
        var start0 = plan.In.IsEmpty ? plan.StopAt : plan.In.Start;
        var y = _map?.GroundHeightOrMean(start0.X, start0.Y) ?? 0f; // the terrain is a ragged patch inside its box, so the edge may lie past it: the mean height then, and the van takes the ground's as soon as it has one
        var start = new NVec3(start0.X, y, start0.Y);
        var crew = _data.Raids.CrewFor(_data.Gang.Raid, Humans());
        var raid = _roster.Launch(plan, exit, start, crew, p => nav.Snap(new Vector3(p.X, p.Y, p.Z)).ToNumerics(), FleePoint, Ground);
        if (raid == null) { Shrink("the world has no room for it"); return; }
        GD.Print($"server: Thieves van {n}/{of}: {(plan.Fallback ? "parked (fallback)" : plan.RouteName != null ? $"on route '{plan.RouteName}' from ({start0.X:F0},{start0.Y:F0}), {plan.In.Length:F0} m of road" : $"in from ({start0.X:F0},{start0.Y:F0}), {plan.In.Length:F0} m of road")} to the stop at ({plan.StopAt.X:F0},{plan.StopAt.Y:F0}) with {plan.Targets} targets near, crew of {crew.Size}");
    }

    /// <summary>
    /// A Heist's Assault (docs/cartel.md "Assaults"): a van of the Rival crew in from the map's edge over the roads, as a Raid's (ADR 0021), to the road stop nearest <paramref name="target"/> that is
    /// not within used_apart_m of <paramref name="used"/> (the Heist's earlier stops and its Cartel van) and lets the crew step out on the navigation mesh, and from there straight across open ground (a field,
    /// a meadow: never a building, trunk, fence or water, <see cref="AssaultTail"/>) to park off_road.park_short_m short of the target when that brings it closer; with no way in, parked on the road spot
    /// nearest it (and driving on over open ground from there). The van stays until the Job removes it; <paramref name="gang"/> (the Rival crew's, empty) gets out in <paramref name="order"/> and heads for <paramref name="post"/> (snapped onto the
    /// mesh). Null with no navigation mesh, no road, no stop or no room in the world.
    /// </summary>
    public Raid? LaunchAssault(NVec2 target, IReadOnlyList<NVec2> used, IReadOnlyList<GangMemberKind> order, ThiefGang gang, NVec3 post)
    {
        if (_nav() is not { } nav || Roads.IsEmpty) return null;
        var vc = _data.Van;
        var (min, max) = Extent();
        var plan = RaidPlanner.Plan(Roads, min, max, Array.Empty<NVec2>(), used, new[] { target }, vc, _rng, AcceptStop)
            ?? RaidPlanner.Fallback(Roads, target, vc.HalfLengthM * 4, _rng, AcceptStop);
        if (plan == null) { GD.Print($"server: Assault to ({target.X:F0},{target.Y:F0}): no road stop the crew can step out at"); return null; }
        var onRoad = plan.StopAt;
        if (_open is { } open) plan = AssaultTail.Extend(Roads, plan, target, used, vc, open.Numbers, (a, b) => open.Line(a, b, vc.HalfWidthM), AcceptStop);
        var overOpen = plan.StopAt != onRoad;
        var exit = plan.Out ?? RaidPlanner.Exit(Roads, plan, min, max, vc);
        var start0 = plan.In.IsEmpty ? plan.StopAt : plan.In.Start;
        var start = new NVec3(start0.X, _map?.GroundHeightOrMean(start0.X, start0.Y) ?? 0f, start0.Y);
        var stays = vc.Copy();
        stays.StaySeconds = float.PositiveInfinity; // it never drives off: the Job removes it
        var snapped = nav.Snap(new Vector3(post.X, post.Y, post.Z)).ToNumerics();
        var raid = _roster.Launch(plan, exit, start, order, gang, stays, p => nav.Snap(new Vector3(p.X, p.Y, p.Z)).ToNumerics(), FleePoint, Ground, snapped);
        if (raid != null) GD.Print($"server: Assault van {(plan.Fallback && !overOpen ? "parked (fallback)" : $"in from ({start0.X:F0},{start0.Y:F0}), {plan.In.Length:F0} m of way")} to the stop at ({plan.StopAt.X:F0},{plan.StopAt.Y:F0}){(overOpen ? $", the last {NVec2.Distance(onRoad, plan.StopAt):F0} m over open ground from the road at ({onRoad.X:F0},{onRoad.Y:F0})" : "")}, crew of {order.Count}");
        return raid;
    }

    /// <summary>The van that was due could not be launched: the wave loses it (the event is won with the vans it has).</summary>
    void Shrink(string why)
    {
        GD.PrintErr($"server: Thieves van {_wave.Launched}/{_wave.Total}: {why}, the wave is {_wave.Total - 1} van(s) instead of {_wave.Total}");
        _wave.Shrink();
    }
}
