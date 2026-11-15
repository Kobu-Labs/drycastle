using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// A fixed route (<see cref="RaidRouteConfig"/>) resolved on a road graph: the waypoints snapped to the nearest road point, the roads driven through them in order
/// (<see cref="Path"/>, from the first to the last), and where the last waypoint is on the van's roads (<see cref="Last"/>: the place it drives on from).
/// </summary>
public sealed record ResolvedRoute(string Name, RoadPath Path, RoadPlace Last, Vector2[] Waypoints);

/// <summary>
/// The fixed routes of the Raid vans (GLOSSARY "Raid van"), pure. A route is only the way in and out: the van drives the roads through its waypoints, carries on from the last one in the
/// direction it was going to a stop on a road near the church (the main road the route continues on first), and leaves by the same roads backwards, to the first waypoint, where it is
/// gone. With no church (or <see cref="RaidsConfig.ParkCentre"/>) it parks at the end of its route.
/// </summary>
public static class RaidRoutes
{
    const int MaxChecks = 60;
    /// <summary>A stop candidate this close to the road from the route's end to the church counts as on the main road.</summary>
    const float MainRoadTolM = 3f;

    /// <summary>
    /// Resolves each route on <paramref name="routeRoads"/> (every road the owner may have drawn the route over: footpaths too, see <see cref="RaidsConfig.RouteMinRoadWidthM"/>): every waypoint snaps
    /// to the nearest road point (within <paramref name="snapMaxM"/>) and consecutive waypoints are joined by the shortest road path. The last waypoint must also snap onto <paramref name="vanRoads"/>,
    /// the roads the van drives on from there (the same graph when none is wider than the other). A route that cannot be resolved (a waypoint too far from any road, two waypoints with no
    /// road between them) is left out and <paramref name="log"/> gets one line for it.
    /// </summary>
    public static List<ResolvedRoute> Resolve(RoadGraph routeRoads, RoadGraph vanRoads, IEnumerable<RaidRouteConfig> routes, float snapMaxM, Action<string>? log = null)
    {
        var result = new List<ResolvedRoute>();
        foreach (var route in routes)
        {
            if (Resolve(routeRoads, vanRoads, route, snapMaxM, out var resolved, out var why)) result.Add(resolved!);
            else log?.Invoke($"Thieves route '{route.Name}' skipped: {why}");
        }
        return result;
    }

    static bool Resolve(RoadGraph g, RoadGraph vanRoads, RaidRouteConfig route, float snapMaxM, out ResolvedRoute? resolved, out string why)
    {
        resolved = null; why = "";
        if (g.IsEmpty) { why = "the map has no road graph"; return false; }
        var places = new List<RoadPlace>();
        for (var i = 0; i < route.Waypoints.Count; i++)
        {
            var w = route.Waypoints[i];
            var p = new Vector2(w[0], w[1]);
            if (g.Nearest(p, snapMaxM) is not { } near)
            {
                var far = g.Nearest(p, float.MaxValue);
                why = $"waypoint {i + 1} ({p.X:F0}, {p.Y:F0}) is {(far is { } f ? $"{f.Distance:F0} m" : "far")} from the nearest road (at most {snapMaxM:F0} m)";
                return false;
            }
            places.Add(near.Place);
        }
        RoadPath? path = null;
        for (var i = 0; i + 1 < places.Count; i++)
        {
            if (g.RouteBetween(places[i], places[i + 1]) is not { } leg) { why = $"no road path from waypoint {i + 1} to waypoint {i + 2}"; return false; }
            path = path == null ? leg.Path : path.Then(leg.Path);
        }
        if (path == null || path.IsEmpty) { why = "its waypoints snap to the same road point"; return false; }
        // from the last waypoint the van drives on over the van's roads (possibly narrower than the route's own)
        var end = path.End;
        if (vanRoads.Nearest(end, snapMaxM) is not { } last) { why = $"its last waypoint ({end.X:F0}, {end.Y:F0}) is not on a road the van may drive (none within {snapMaxM:F0} m)"; return false; }
        resolved = new ResolvedRoute(route.Name, path, last.Place, places.Select(g.PointAt).ToArray());
        return true;
    }

    /// <summary>The routes in the order the Raid tries them: plain random (<see cref="RaidsConfig.RoutePick"/> "random"), the first is the pick, the rest are the ones to try when its stop fails.</summary>
    public static List<ResolvedRoute> Order(IReadOnlyList<ResolvedRoute> routes, Random rng) => routes.OrderBy(_ => rng.Next()).ToList();

    /// <summary>
    /// The plan of one Raid over the fixed routes, or null when no route has a working stop (the caller then runs the automatic choice). <paramref name="centre"/> is where the van parks near
    /// (the church; null: at the end of its route), <paramref name="used"/> the stops of this event's vans (a stop within <see cref="VanConfig.UsedApartM"/> of one is refused; at the end
    /// of a route the van parks that far short of it). <paramref name="accept"/> is the door-point and navigation mesh check.
    /// </summary>
    public static RaidPlan? Plan(RoadGraph g, IReadOnlyList<ResolvedRoute> routes, Vector2? centre, IReadOnlyList<Vector2> used, VanConfig vc, RaidsConfig rc, Random rng, RaidPlanner.Accept accept)
    {
        foreach (var route in Order(routes, rng))
        {
            var plan = (centre is { } c ? ParkNearCentre(g, route, c, used, vc, rc, rng, accept) : null) ?? ParkAtEnd(g, route, used, vc, rc, accept);
            if (plan != null) return plan;
        }
        return null;
    }

    /// <summary>How far along the route's end and the way on the van is compared to tell that it would turn back over the road it just came by.</summary>
    const float RetraceProbeM = 15f;
    const float RetraceTolM = 3f;

    /// <summary>Whether the way on from the route's end goes back over the last stretch of the route (the van would turn back).</summary>
    static bool Retraces(RoadPath route, RoadPath ext)
    {
        var probe = MathF.Min(RetraceProbeM, MathF.Min(ext.Length, route.Length));
        if (probe < 1f) return false;
        return Vector2.Distance(ext.At(probe), route.At(route.Length - probe)) < RetraceTolM;
    }

    static bool Free(IReadOnlyList<Vector2> used, Vector2 p, float apartM)
    {
        foreach (var u in used) if (Vector2.Distance(u, p) < apartM) return false;
        return true;
    }

    /// <summary>
    /// A stop on a road within <see cref="RaidsConfig.ParkNearChurchM"/> of <paramref name="centre"/>, reached from the route's end driving on (never turning back over the end of the route): one at random on the main road
    /// (the road from the route's end to the centre), else at random on any road in reach, else the nearest working spot to the centre anywhere. Null when none works.
    /// </summary>
    static RaidPlan? ParkNearCentre(RoadGraph g, ResolvedRoute route, Vector2 centre, IReadOnlyList<Vector2> used, VanConfig vc, RaidsConfig rc, Random rng, RaidPlanner.Accept accept)
    {
        var d = g.DijkstraFrom(route.Last, null, 0);
        RoadPath? main = g.Nearest(centre, float.MaxValue) is { } near && g.RouteBetween(route.Last, null, 0, near.Place, d) is { } m && !Retraces(route.Path, m.Path) ? m.Path : null;
        var big = new Vector2(-1e9f, -1e9f); var bigMax = new Vector2(1e9f, 1e9f);
        var spots = g.PointsAlong(vc.StopStepM, big, bigMax, 0)
            .Where(rp => Free(used, rp.Ground, vc.UsedApartM))
            .Select(rp => (Point: rp, Dist: Vector2.Distance(rp.Ground, centre)))
            .ToList();
        var inReach = spots.Where(s => s.Dist <= rc.ParkNearChurchM).ToList();
        var onMain = inReach.Where(s => main != null && main.DistanceTo(s.Point.Ground) <= MainRoadTolM).OrderBy(_ => rng.Next());
        var elsewhere = inReach.Where(s => main == null || main.DistanceTo(s.Point.Ground) > MainRoadTolM).OrderBy(_ => rng.Next());
        var beyond = spots.Where(s => s.Dist > rc.ParkNearChurchM).OrderBy(s => s.Dist);
        var checks = 0;
        foreach (var (rp, _) in onMain.Concat(elsewhere).Concat(beyond))
        {
            if (checks >= MaxChecks) break;
            if (g.RouteBetween(route.Last, null, 0, rp.Place, d) is not { } ext || Retraces(route.Path, ext.Path)) continue;
            checks++;
            var full = route.Path.Then(ext.Path);
            var yaw = full.YawAt(full.Length);
            if (accept(rp.Ground, yaw) is not { } side) continue;
            return new RaidPlan(null, rp.Place, rp.Ground, ext.Forward, yaw, full, side, 0, false, full.Reversed(), route.Name);
        }
        return null;
    }

    /// <summary>
    /// The end of the route as the stop: the exact spot when it works, else the nearest working spot back along the route within <see cref="RaidsConfig.RouteStopSearchM"/>. A stop within
    /// <see cref="VanConfig.UsedApartM"/> of another van's is moved back along the route until it is that far short of it. Null when none works.
    /// </summary>
    static RaidPlan? ParkAtEnd(RoadGraph g, ResolvedRoute route, IReadOnlyList<Vector2> used, VanConfig vc, RaidsConfig rc, RaidPlanner.Accept accept)
    {
        var path = route.Path;
        var s = path.Length;
        while (s > 0 && !Free(used, path.At(s), vc.UsedApartM)) s -= 1f;
        if (s <= 0) return null;
        for (var back = s; back >= MathF.Max(1f, s - rc.RouteStopSearchM); back -= 1f)
        {
            var at = path.At(back);
            if (!Free(used, at, vc.UsedApartM)) continue;
            var yaw = path.YawAt(back);
            if (accept(at, yaw) is not { } side) continue;
            var inPath = path.Slice(0, back);
            if (inPath.IsEmpty) return null;
            var place = back >= path.Length - 0.01f ? route.Last : g.Nearest(at, 10f)?.Place ?? route.Last;
            return new RaidPlan(null, place, at, true, yaw, inPath, side, 0, false, inPath.Reversed(), route.Name);
        }
        return null;
    }
}
