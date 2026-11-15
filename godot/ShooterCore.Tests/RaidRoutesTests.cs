using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The fixed routes of the Raid vans (docs/events/thieves/03, "Fixed routes"): resolving waypoints on a road graph, the random pick, the stop near the church, the way out, the short-stop rule.</summary>
public class RaidRoutesTests
{
    // x east, y = world z (south). A main street x = 0 from y = 0 to y = 400 (nodes 0..4, "the church" lies at (0, 380)), a side road off it at y = 300 to (60, 300) (node 9),
    // a second way in x = 100 from y = 0 to y = 200 (nodes 7, 8, 5) that joins the street at node 2 (0, 200), and an island that nothing leads to (nodes 10, 11).
    static Vector2[] Line(params (float X, float Y)[] p) => p.Select(q => new Vector2(q.X, q.Y)).ToArray();

    static RoadGraph Town() => RoadGraph.Make(
        new[] { new Vector2(0, 0), new Vector2(0, 100), new Vector2(0, 200), new Vector2(0, 300), new Vector2(0, 400), new Vector2(100, 200), new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(60, 300), new Vector2(500, 500), new Vector2(500, 600) },
        new (int, int, Vector2[])[]
        {
            (0, 1, Line((0, 0), (0, 100))),
            (1, 2, Line((0, 100), (0, 200))),
            (2, 3, Line((0, 200), (0, 300))),
            (3, 4, Line((0, 300), (0, 400))),
            (7, 8, Line((100, 0), (100, 100))),
            (8, 5, Line((100, 100), (100, 200))),
            (5, 2, Line((100, 200), (0, 200))),
            (3, 9, Line((0, 300), (60, 300))),
            (10, 11, Line((500, 500), (500, 600))),
        });

    static RaidRouteConfig Route(string name, params (float X, float Y)[] w) => new() { Name = name, Waypoints = w.Select(p => new[] { p.X, p.Y }).ToList() };
    static readonly RaidRouteConfig R1 = Route("one", (0, 0), (0, 100), (0, 200));
    static readonly RaidRouteConfig R2 = Route("two", (100, 0), (100, 100), (100, 200));
    static readonly Vector2 Church = new(0, 380);
    static readonly VanConfig Van = new() { UsedApartM = 60, StopStepM = 20 };
    static readonly RaidsConfig Raids = new() { ParkNearChurchM = 100, RouteStopSearchM = 40, RouteSnapMaxM = 30 };

    static List<ResolvedRoute> Resolved(RoadGraph g, params RaidRouteConfig[] routes) => RaidRoutes.Resolve(g, g, routes, 30);

    // ---- resolving ----

    [Fact]
    public void WaypointsSnapToTheRoadsAndAreJoinedByTheShortestRoadPath()
    {
        var g = Town();
        var one = Resolved(g, Route("snapped", (4, 0), (-3, 100), (0, 200)))[0]; // a few metres beside the street
        Assert.Equal(200f, one.Path.Length, 0.1f);
        Assert.Equal(new Vector2(0, 0), one.Path.Start);
        Assert.Equal(new Vector2(0, 200), one.Path.End);
        Assert.Equal(new Vector2(0, 100), one.Waypoints[1]); // the snapped point, not the sketched one
        // a waypoint off the street's line: the path goes by the roads (via node 2), not straight
        var two = Resolved(g, R2)[0];
        Assert.Equal(200f, two.Path.Length, 0.1f);
        var bent = Resolved(g, Route("bent", (100, 100), (0, 100)))[0];
        Assert.Equal(100f + 100f + 100f, bent.Path.Length, 0.1f); // 8 -> 5 -> 2 -> 1, never the 100 m straight across
    }

    [Fact]
    public void ARouteTheMapCannotDriveIsSkippedWithOneLogLine()
    {
        var g = Town();
        var log = new List<string>();
        var routes = RaidRoutes.Resolve(g, g, new[] { Route("far", (0, 0), (300, 300)), R1, Route("island", (0, 0), (500, 550)) }, 30, log.Add);
        Assert.Equal(new[] { "one" }, routes.Select(r => r.Name));
        Assert.Equal(2, log.Count);
        Assert.Contains("'far'", log[0]); Assert.Contains("waypoint 2", log[0]);
        Assert.Contains("'island'", log[1]); Assert.Contains("no road path", log[1]);
        Assert.Empty(RaidRoutes.Resolve(RoadGraph.Empty, RoadGraph.Empty, new[] { R1 }, 30));
    }

    [Fact]
    public void TheRouteIsDrivenOnWiderRoadsThanTheVanUsesAfterwards()
    {
        // the last waypoint must lie on the van's roads: here a graph with only the main street
        var routeRoads = Town();
        var vanRoads = RoadGraph.Make(new[] { new Vector2(0, 0), new Vector2(0, 400) }, new (int, int, Vector2[])[] { (0, 1, Line((0, 0), (0, 400))) });
        Assert.Single(RaidRoutes.Resolve(routeRoads, vanRoads, new[] { R1 }, 30));
        Assert.Empty(RaidRoutes.Resolve(routeRoads, vanRoads, new[] { R2 }, 30)); // ends at (100, 200): 100 m from the van's roads
    }

    // ---- the pick ----

    [Fact]
    public void EachRaidPicksARouteAtRandomAndTheSameSeedPicksTheSame()
    {
        var routes = Resolved(Town(), R1, R2);
        var picks = Enumerable.Range(0, 40).Select(s => RaidRoutes.Order(routes, new Random(s))[0].Name).ToList();
        Assert.Contains("one", picks); Assert.Contains("two", picks);
        Assert.Equal(picks, Enumerable.Range(0, 40).Select(s => RaidRoutes.Order(routes, new Random(s))[0].Name).ToList());
        Assert.Equal(2, RaidRoutes.Order(routes, new Random(1)).Count); // the other one is the second try
    }

    // ---- the stop near the church ----

    static RaidPlan? PlanOn(RoadGraph g, List<ResolvedRoute> routes, int seed, IReadOnlyList<Vector2>? used = null, RaidPlanner.Accept? accept = null, Vector2? centre = null) =>
        RaidRoutes.Plan(g, routes, centre ?? Church, used ?? Array.Empty<Vector2>(), Van, Raids, new Random(seed), accept ?? ((_, _) => 1));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void TheVanDrivesTheRoutesRoadsThenOnToAStopWithinRangeOfTheChurchAndLeavesBackwards(int seed)
    {
        var g = Town();
        var routes = Resolved(g, R1, R2);
        var plan = PlanOn(g, routes, seed)!;
        Assert.NotNull(plan);
        Assert.False(plan.Fallback);
        Assert.Null(plan.Entry);
        var route = routes.First(r => r.Name == plan.RouteName);
        Assert.Equal(route.Path.Start, plan.In.Start); // appears at the first waypoint
        Assert.True(Vector2.Distance(plan.StopAt, Church) <= 100f + 0.01f, "within 100 m of the church");
        Assert.Equal(plan.StopAt, plan.In.End);
        Assert.True(plan.In.Length > route.Path.Length, "on past the route's last waypoint");
        foreach (var w in route.Waypoints) Assert.True(plan.In.DistanceTo(w) < 0.01f, "through every waypoint");
        // only on roads: every point is on a street line, a way in or the side road (never a straight line off them)
        Assert.All(plan.In.Points, p => Assert.True(OnTown(p), $"{p} is off the roads"));
        // never turns back over the route's end: on the street from y = 200 it only goes on towards the church
        Assert.True(plan.StopAt.Y > 200f || plan.StopAt.X > 0f);
        // the way out is the same way backwards
        Assert.Equal(plan.In.Length, plan.Out!.Length, 0.01f);
        Assert.Equal(plan.StopAt, plan.Out.Start);
        Assert.Equal(route.Path.Start, plan.Out.End);
    }

    static bool OnTown(Vector2 p) =>
        (MathF.Abs(p.X) < 0.01f && p.Y >= -0.01f && p.Y <= 400.01f) ||
        (MathF.Abs(p.X - 100) < 0.01f && p.Y >= -0.01f && p.Y <= 200.01f) ||
        (MathF.Abs(p.Y - 200) < 0.01f && p.X >= -0.01f && p.X <= 100.01f) ||
        (MathF.Abs(p.Y - 300) < 0.01f && p.X >= -0.01f && p.X <= 60.01f);

    [Fact]
    public void TheMainRoadIsPreferredThenAnyRoadInRangeThenTheNearestWorkingSpotThenTheRoutesEnd()
    {
        var g = Town();
        var routes = Resolved(g, R1);
        for (var seed = 0; seed < 20; seed++)
        {
            var plan = PlanOn(g, routes, seed)!;
            Assert.True(MathF.Abs(plan.StopAt.X) < 0.01f && plan.StopAt.Y > 280f - 0.01f, $"seed {seed}: {plan.StopAt} is on the main road"); // the street the route continues on
        }
        // the main road refused: the side road in range
        var side = PlanOn(g, routes, 1, accept: (at, _) => MathF.Abs(at.X) < 0.5f ? null : 1)!;
        Assert.Equal(300f, side.StopAt.Y, 0.01f);
        Assert.True(side.StopAt.X > 0);
        // nothing in range works (the church's ring is refused): the nearest spot to the church outside it
        var beyond = PlanOn(g, routes, 1, accept: (at, _) => Vector2.Distance(at, Church) <= 100f ? null : 1)!;
        Assert.True(Vector2.Distance(beyond.StopAt, Church) > 100f);
        Assert.True(beyond.StopAt.Y > 200f, "the nearest to the church, not the route's end");
        // nothing but the route itself works: it parks on the route (the way in is a part of the route then)
        var onRoute = PlanOn(g, routes, 1, accept: (at, _) => at.Y <= 200f ? 1 : null)!;
        Assert.True(onRoute.StopAt.Y <= 200f);
        // nothing works anywhere: no plan, the caller runs the automatic choice
        Assert.Null(PlanOn(g, routes, 1, accept: (_, _) => null));
    }

    [Fact]
    public void AStopWithinUsedApartOfAnotherVansIsNotTaken()
    {
        var g = Town();
        var routes = Resolved(g, R1);
        var used = new[] { new Vector2(0, 330) };
        for (var seed = 0; seed < 20; seed++)
        {
            var plan = PlanOn(g, routes, seed, used)!;
            Assert.True(Vector2.Distance(plan.StopAt, used[0]) >= 60f, $"seed {seed}: {plan.StopAt}");
            Assert.True(Vector2.Distance(plan.StopAt, Church) <= 100f + 0.01f);
        }
    }

    [Fact]
    public void AMapWithNoChurchParksAtTheEndOfTheRouteOrShortOfItAndShortOfAnotherVan()
    {
        var g = Town();
        var routes = Resolved(g, R1);
        var end = RaidRoutes.Plan(g, routes, null, Array.Empty<Vector2>(), Van, Raids, new Random(0), (_, _) => 1)!;
        Assert.Equal(new Vector2(0, 200), end.StopAt);
        Assert.Equal(end.In.End, end.StopAt);
        Assert.Equal(200f, end.In.Length, 0.1f);
        Assert.Equal(end.In.Length, end.Out!.Length, 0.01f);
        // the exact spot fails: the nearest working spot back along the route within 40 m
        var back = RaidRoutes.Plan(g, routes, null, Array.Empty<Vector2>(), Van, Raids, new Random(0), (at, _) => at.Y <= 170f ? 1 : null)!;
        Assert.InRange(back.StopAt.Y, 160f, 170f);
        Assert.Equal(back.StopAt, back.In.End);
        // too far back (more than 40 m): no stop on this route
        Assert.Null(RaidRoutes.Plan(g, routes, null, Array.Empty<Vector2>(), Van, Raids, new Random(0), (at, _) => at.Y <= 100f ? 1 : null));
        // another van stands at the end: this one parks used_apart_m short of it, on the route
        var short1 = RaidRoutes.Plan(g, routes, null, new[] { new Vector2(0, 200) }, Van, Raids, new Random(0), (_, _) => 1)!;
        Assert.InRange(Vector2.Distance(short1.StopAt, new Vector2(0, 200)), 60f, 62f);
        Assert.Equal(140f, short1.StopAt.Y, 1.5f);
        Assert.Equal(0f, short1.StopAt.X, 0.01f);
    }

    [Fact]
    public void WithNoUsableRouteThereIsNoPlan()
    {
        var g = Town();
        Assert.Null(RaidRoutes.Plan(g, new List<ResolvedRoute>(), Church, Array.Empty<Vector2>(), Van, Raids, new Random(0), (_, _) => 1));
    }

    // ---- the graph and path helpers ----

    [Fact]
    public void RouteBetweenPlacesGoesDirectOnOneEdgeOtherwiseViaTheNodesAndKnowsWhichWayItEnds()
    {
        var g = Town();
        var a = g.Nearest(new Vector2(0, 30), 1)!.Value.Place; var b = g.Nearest(new Vector2(0, 70), 1)!.Value.Place;
        var direct = g.RouteBetween(a, b)!.Value;
        Assert.Equal(40f, direct.Path.Length, 0.01f);
        Assert.True(direct.Forward);
        Assert.False(g.RouteBetween(b, a)!.Value.Forward);
        var far = g.Nearest(new Vector2(0, 350), 1)!.Value.Place;
        var via = g.RouteBetween(a, far)!.Value;
        Assert.Equal(320f, via.Path.Length, 0.01f);
        Assert.True(Vector2.Distance(new Vector2(0, 30), via.Path.Start) < 0.01f);
        Assert.True(Vector2.Distance(new Vector2(0, 350), via.Path.End) < 0.01f);
        Assert.Null(g.RouteBetween(a, g.Nearest(new Vector2(500, 550), 1)!.Value.Place)); // the island
    }

    [Fact]
    public void ADriverThatMayNotTurnBackOnlyReachesWhatIsAhead()
    {
        var g = Town();
        var at = g.Nearest(new Vector2(0, 150), 1)!.Value.Place; // on 1-2, heading To (south, y up)
        var ahead = g.Nearest(new Vector2(0, 250), 1)!.Value.Place; var behind = g.Nearest(new Vector2(0, 50), 1)!.Value.Place;
        var d = g.DijkstraFrom(at, true, float.PositiveInfinity);
        Assert.NotNull(g.RouteBetween(at, true, float.PositiveInfinity, ahead, d));
        Assert.Null(g.RouteBetween(at, true, float.PositiveInfinity, behind, d)); // the edge behind it
        var sameEdgeBehind = g.Nearest(new Vector2(0, 120), 1)!.Value.Place;
        Assert.Null(g.RouteBetween(at, true, float.PositiveInfinity, sameEdgeBehind, d)); // the same edge, behind it: not by driving on and turning round at the node
        var free = g.DijkstraFrom(at, true, 0);
        Assert.Equal(100f, g.RouteBetween(at, true, 0, behind, free)!.Value.Path.Length, 0.01f);
    }

    [Fact]
    public void ARoadPathCanBeSlicedReversedAndMeasured()
    {
        var p = new RoadPath(new[] { new Vector2(0, 0), new Vector2(0, 10), new Vector2(10, 10) });
        var s = p.Slice(5, 15);
        Assert.Equal(new Vector2(0, 5), s.Start);
        Assert.Equal(new Vector2(5, 10), s.End);
        Assert.Equal(10f, s.Length, 0.001f);
        Assert.Equal(p.Length, p.Reversed().Length, 0.001f);
        Assert.Equal(p.End, p.Reversed().Start);
        Assert.Equal(3f, p.DistanceTo(new Vector2(3, 7)), 0.001f); // beside the first leg
        Assert.Equal(30f, p.Then(new RoadPath(new[] { new Vector2(10, 10), new Vector2(10, 20) })).Length, 0.001f);
        Assert.True(p.Slice(4, 4).IsEmpty);
    }

    // ---- the data and the village ----

    [Fact]
    public void TheShippedDataHasTheTwoVillageRoutesAndIsChecked()
    {
        var raids = LoadThieves().Raids;
        Assert.Equal("random", raids.RoutePick);
        Assert.Equal(2, raids.Routes.Count);
        Assert.Equal(new[] { 3, 5 }, raids.Routes.Select(r => r.Waypoints.Count));
        Assert.Equal(100f, raids.ParkNearChurchM);
        Assert.Equal(30f, raids.RouteSnapMaxM);
        Assert.Equal(40f, raids.RouteStopSearchM);
        Assert.Null(raids.ParkCentre);
        Assert.Throws<DataFileException>(() => ThievesFile.Parse(Mutate("\"route_pick\": \"random\"", "\"route_pick\": \"sequence\"")));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse(Mutate("[[191.61, 1663.37], [104.11, 890.37], [16.11, 122.37]]", "[[191.61, 1663.37]]")));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse(Mutate("\"route_snap_max_m\": 30", "\"route_snap_max_m\": 0")));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse(Mutate("\"route_pick\": \"random\"", "\"route_pick\": \"random\", \"park_centre\": [1]")));
    }

    static ThievesFile LoadThieves() => ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json")));

    static string Mutate(string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json"));
        Assert.Contains(from, text);
        return text.Replace(from, to);
    }

    /// <summary>The village map's roads (the fixture is the whole roads.json) and the two routes the owner sketched: both resolve, join near the church and the plan stays on the roads.</summary>
    [Fact]
    public void TheVillageRoutesResolveOnTheRealRoadsAndReachTheChurch()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "roads_village.json"));
        var vanRoads = RoadGraph.Parse(json, 3);
        var routeRoads = RoadGraph.Parse(json, 0);
        var raids = LoadThieves().Raids;
        var log = new List<string>();
        var routes = RaidRoutes.Resolve(routeRoads, vanRoads, raids.Routes, raids.RouteSnapMaxM, log.Add);
        Assert.Empty(log);
        Assert.Equal(2, routes.Count);
        Assert.Equal(1551f, routes[0].Path.Length, 15f); // A B C
        Assert.Equal(1283f, routes[1].Path.Length, 15f); // D E F G H (G to H over a 1.5 m footpath)
        foreach (var r in routes) Assert.True(Vector2.Distance(r.Path.End, new Vector2(12, 100)) < 30f, "both routes end on the tertiary road, C and H about 40 m apart");
        var church = new Vector2(-221.335f, -276.138f); // church.json doorway, local north flipped to z
        var inRange = vanRoads.PointsAlong(Van.StopStepM, new Vector2(-1e9f), new Vector2(1e9f), 0).Count(p => Vector2.Distance(p.Ground, church) <= 100f);
        Assert.True(inRange >= 20, $"{inRange} stop candidates within 100 m of the church");
        foreach (var route in routes)
            for (var seed = 0; seed < 10; seed++)
            {
                var plan = RaidRoutes.Plan(vanRoads, new List<ResolvedRoute> { route }, church, Array.Empty<Vector2>(), Van, raids, new Random(seed), (_, _) => 1)!;
                Assert.NotNull(plan);
                Assert.True(Vector2.Distance(plan.StopAt, church) <= 100f + 0.01f);
                Assert.Equal(route.Path.Start, plan.In.Start);
                Assert.True(plan.In.Length > route.Path.Length);
                Assert.Equal(plan.In.Length, plan.Out!.Length, 0.01f);
            }
    }
}
