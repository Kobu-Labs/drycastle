using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The road graph and the stop choice of the Raid vans (docs/events/thieves/03): shortest paths, where roads cross the map edge, stop candidates, the plan of a Raid.</summary>
public class RoadGraphTests
{
    // A square of 100 m with a diagonal and a road leaving east:
    //   3 --- 2        node 0 (0,0) 1 (100,0) 2 (100,100) 3 (0,100), 4 (200,0) outside the map
    //   |   / |        edges 0-1, 1-2, 2-3, 3-0, 0-2 (diagonal), 1-4 (out of the map)
    //   0 --- 1 --- 4
    static RoadGraph Square() => RoadGraph.Make(
        new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100), new Vector2(200, 0) },
        new (int, int, Vector2[])[]
        {
            (0, 1, new[] { new Vector2(0, 0), new Vector2(100, 0) }),
            (1, 2, new[] { new Vector2(100, 0), new Vector2(100, 100) }),
            (2, 3, new[] { new Vector2(100, 100), new Vector2(0, 100) }),
            (3, 0, new[] { new Vector2(0, 100), new Vector2(0, 0) }),
            (0, 2, new[] { new Vector2(0, 0), new Vector2(100, 100) }),
            (1, 4, new[] { new Vector2(100, 0), new Vector2(200, 0) }),
        });

    static readonly Vector2 Min = new(-10, -10), Max = new(150, 150);

    // ---- parsing ----

    [Fact]
    public void ParseFlipsTheMapsNorthToWorldZAndLeavesOutFootpaths()
    {
        const string json = """
        {"nodes":[{"id":"A","x":0,"y":0},{"id":"B","x":100,"y":50},{"id":"C","x":0,"y":50}],
         "edges":[{"from":"A","to":"B","polyline":[[0,0],[100,50]],"width":5},
                  {"from":"A","to":"C","polyline":[[0,0],[0,50]],"width":1.5},
                  {"from":"A","to":"X","polyline":[[0,0],[0,50]],"width":5}]}
        """;
        var g = RoadGraph.Parse(json, 3);
        Assert.Equal(1, g.EdgeCount); // the footpath is out, the edge to an unknown node too
        Assert.Equal(new Vector2(100, -50), g.NodeAt(1)); // map y north = world -z
        Assert.Equal(2, RoadGraph.Parse(json, 0).EdgeCount);
        Assert.True(RoadGraph.Parse("", 3).IsEmpty);
        Assert.True(RoadGraph.Parse("{}", 3).IsEmpty);
    }

    /// <summary>The village map's roads (a copy of its roads.json cut to what the graph reads) and the extent the server uses: the box round the parcels, world axes.</summary>
    [Fact]
    public void TheVillageRoadsGiveEntryPointsAndAPlanFromTheMapEdgeToAStopAndOut()
    {
        var g = RoadGraph.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "roads_village.json")), 3);
        Assert.True(g.EdgeCount > 200, "footpaths are cut, the rest stays");
        var min = new Vector2(-521.4f, -1242.1f); var max = new Vector2(1465.7f, 278.8f); // x east, z south: the map's y north flipped
        var entries = g.EntryPoints(min, max);
        Assert.True(entries.Count >= 10, $"{entries.Count} entry points");
        Assert.All(entries, e =>
        {
            Assert.True(MathF.Abs(e.Border.X - min.X) < 0.01f || MathF.Abs(e.Border.X - max.X) < 0.01f || MathF.Abs(e.Border.Y - min.Y) < 0.01f || MathF.Abs(e.Border.Y - max.Y) < 0.01f, "on the border");
            Assert.True(e.Lead.Length >= 2);
        });
        var cars = new[] { new Vector2(300, -400), new Vector2(305, -396), new Vector2(310, -402) };
        var cfg = new VanConfig();
        var plan = RaidPlanner.Plan(g, min, max, cars, Array.Empty<Vector2>(), Array.Empty<Vector2>(), cfg, new Random(3), (_, _) => 1);
        Assert.NotNull(plan);
        Assert.False(plan!.Fallback);
        Assert.Equal(plan.StopAt, plan.In.End);
        Assert.True(plan.In.Length > 30f);
        var exit = RaidPlanner.Exit(g, plan, min, max, cfg);
        Assert.Equal(plan.StopAt, exit.Start);
        Assert.True(MathF.Abs(exit.End.X - min.X) < 0.01f || MathF.Abs(exit.End.X - max.X) < 0.01f || MathF.Abs(exit.End.Y - min.Y) < 0.01f || MathF.Abs(exit.End.Y - max.Y) < 0.01f, "the way out ends at the map edge");
    }

    // ---- shortest paths ----

    [Fact]
    public void TheShortestPathTakesTheDiagonalAndNoPathMeansNull()
    {
        var g = Square();
        var p = g.ShortestPath(0, 2)!;
        Assert.Equal(141.42f, p.Length, 0.1f);
        Assert.Equal(new Vector2(0, 0), p.Start);
        Assert.Equal(new Vector2(100, 100), p.End);
        Assert.Equal(200f, g.ShortestPath(1, 3)!.Length, 0.1f);
        Assert.Equal(0f, g.ShortestPath(2, 2)!.Length);
        var loose = RoadGraph.Make(new[] { Vector2.Zero, new Vector2(10, 0), new Vector2(50, 50), new Vector2(60, 50) },
            new (int, int, Vector2[])[] { (0, 1, new[] { Vector2.Zero, new Vector2(10, 0) }), (2, 3, new[] { new Vector2(50, 50), new Vector2(60, 50) }) });
        Assert.Null(loose.ShortestPath(0, 3)); // two islands
    }

    [Fact]
    public void ParallelEdgesGiveTheShorterAndAWindingEdgeCountsItsPolyline()
    {
        var g = RoadGraph.Make(new[] { Vector2.Zero, new Vector2(100, 0) }, new (int, int, Vector2[])[]
        {
            (0, 1, new[] { Vector2.Zero, new Vector2(50, 80), new Vector2(100, 0) }),   // a long way round
            (0, 1, new[] { Vector2.Zero, new Vector2(100, 0) }),
        });
        Assert.Equal(100f, g.ShortestPath(0, 1)!.Length, 0.01f);
    }

    [Fact]
    public void APathSamplesByDistanceAndFacesAlongItsLegs()
    {
        var path = new RoadPath(new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, -50) });
        Assert.Equal(150f, path.Length);
        Assert.Equal(new Vector2(50, 0), path.At(50));
        Assert.Equal(new Vector2(100, -25), path.At(125));
        Assert.Equal(new Vector2(100, -50), path.At(999)); // clamped
        Assert.Equal(-MathF.PI / 2, path.YawAt(10), 0.01f); // east: Godot yaw -90 degrees (0 faces -Z)
        Assert.Equal(0f, path.YawAt(120), 0.01f);            // north (-z)
        Assert.Equal(0f, path.YawAt(150), 0.01f);            // the last leg's heading at the very end
    }

    // ---- the map edge ----

    [Fact]
    public void AnEntryIsWhereARoadCrossesTheMapEdgeGoingInAndLeadsToTheNodeInside()
    {
        var g = Square();
        var entries = g.EntryPoints(Min, Max);
        var e = Assert.Single(entries);
        Assert.Equal(new Vector2(150, 0), e.Border);
        Assert.Equal(1, e.InsideNode);
        Assert.Equal(new[] { new Vector2(150, 0), new Vector2(100, 0) }, e.Lead);
        Assert.Empty(g.EntryPoints(new Vector2(-500, -500), new Vector2(500, 500))); // everything inside: no road crosses
    }

    [Fact]
    public void AnEdgeThatLeavesTheMapAgainOrHasBothEndsOutsideIsNoEntry()
    {
        // both ends outside, the road runs through the map: a van would drive out of it again, so it is no way in
        var through = RoadGraph.Make(new[] { new Vector2(-100, 50), new Vector2(300, 50) }, new (int, int, Vector2[])[] { (0, 1, new[] { new Vector2(-100, 50), new Vector2(50, 50), new Vector2(300, 50) }) });
        Assert.Empty(through.EntryPoints(Min, Max));
        // a road that dips out and comes back is an entry at the last crossing, and its lead keeps to the inside
        var dip = RoadGraph.Make(new[] { new Vector2(50, 50), new Vector2(60, 50) }, new (int, int, Vector2[])[] { (0, 1, new[] { new Vector2(50, 50), new Vector2(200, 50), new Vector2(60, 50) }) });
        var e = dip.EntryPoints(Min, Max);
        Assert.Equal(2, e.Count); // one for each way along it
        Assert.All(e, x => Assert.Equal(new Vector2(150, 50), x.Border));
    }

    [Fact]
    public void AnEntryWorksFromEitherEndOfTheEdge()
    {
        // the same road drawn the other way round (from inside to outside) is the same entry
        var g = RoadGraph.Make(new[] { new Vector2(200, 0), new Vector2(100, 0) }, new (int, int, Vector2[])[] { (0, 1, new[] { new Vector2(200, 0), new Vector2(100, 0) }) });
        var e = Assert.Single(g.EntryPoints(Min, Max));
        Assert.Equal(new Vector2(150, 0), e.Border);
        Assert.Equal(1, e.InsideNode);
    }

    // ---- places and routes ----

    [Fact]
    public void StopCandidatesAreSpacedAlongTheRoadsAndStayInsideTheInset()
    {
        var g = Square();
        var pts = g.PointsAlong(20, Min, Max, 30).ToList();
        Assert.NotEmpty(pts);
        Assert.All(pts, p => { Assert.InRange(p.Ground.X, Min.X + 30, Max.X - 30); Assert.InRange(p.Ground.Y, Min.Y + 30, Max.Y - 30); });
        Assert.DoesNotContain(pts, p => p.Ground.X > 120 && p.Ground.Y == 0); // the stretch outside the inset
        var nearest = g.Nearest(new Vector2(40, 7), 20)!.Value;
        Assert.Equal(7f, nearest.Distance, 0.01f);
        Assert.Equal(new Vector2(40, 0), g.PointAt(nearest.Place));
        Assert.Null(g.Nearest(new Vector2(1000, 1000), 20));
    }

    [Fact]
    public void TheWayInEndsAtTheStopFacingTheWayItWasDriven()
    {
        var g = Square();
        var entry = g.EntryPoints(Min, Max)[0];
        // a stop 30 m along edge 2 (100,100 -> 0,100) from its From end: reached via node 2 going From to To
        var (path, forward) = g.RouteIn(entry, new RoadPlace(2, 30))!.Value;
        Assert.True(forward);
        Assert.Equal(entry.Border, path.Start);
        Assert.Equal(new Vector2(70, 100), path.End);
        Assert.Equal(50f + 100f + 30f, path.Length, 0.1f); // border to node 1, up edge 1, 30 m along edge 2
        // a stop 20 m from edge 3's To end (node 0) is reached from node 0: driving To to From
        var (p2, f2) = g.RouteIn(entry, new RoadPlace(3, 80))!.Value;
        Assert.False(f2);
        Assert.Equal(0f, p2.End.X, 0.01f);
        Assert.Equal(20f, p2.End.Y, 0.01f);
        Assert.Equal(50f + 100f + 20f, p2.Length, 0.1f); // border to node 1, along edge 0 to node 0, 20 m up edge 3
    }

    [Fact]
    public void TheWayOutDrivesOnAndOnlyTurnsRoundWhenThatIsMuchShorter()
    {
        var g = Square();
        var entries = g.EntryPoints(Min, Max);
        // stopped on edge 0 (0,0 -> 100,0) near its To end, facing east (forward): the way out is straight on through node 1 to the map edge
        var ahead = g.RouteOut(new RoadPlace(0, 80), true, entries, 150)!;
        Assert.Equal(new Vector2(80, 0), ahead.Start);
        Assert.Equal(new Vector2(150, 0), ahead.End);
        Assert.Equal(70f, ahead.Length, 0.1f);
        // stopped on edge 0 near its From end, facing west (not forward): ahead is node 0, the way out goes round the square; with no penalty it would turn round
        var round = g.RouteOut(new RoadPlace(0, 10), false, entries, 150)!;
        Assert.Equal(new Vector2(150, 0), round.End);
        Assert.True(round.Length > 150f, "drives on round the square");
        var turn = g.RouteOut(new RoadPlace(0, 10), false, entries, 0)!;
        Assert.Equal(140f, turn.Length, 0.1f); // no penalty: back east, 90 m to the node and 50 m out
        Assert.Null(g.RouteOut(new RoadPlace(0, 10), true, new List<RoadEntry>(), 150));
    }

    // ---- the stop choice ----

    static VanConfig Cfg(Action<VanConfig>? tune = null)
    {
        var c = new VanConfig { StopRadiusM = 30, StopStepM = 10, StopInsetM = 5, UsedApartM = 40 };
        tune?.Invoke(c);
        return c;
    }

    static readonly RaidPlanner.Accept Anywhere = (_, _) => 1;

    [Fact]
    public void TheStopHasTheMostTargetsWithinTheRadius()
    {
        var g = Square();
        // three cars hug the corner (100,100); one stands by node 0
        var targets = new[] { new Vector2(95, 95), new Vector2(105, 98), new Vector2(98, 105), new Vector2(2, 2) };
        var plan = RaidPlanner.Plan(g, Min, Max, targets, Array.Empty<Vector2>(), Array.Empty<Vector2>(), Cfg(), new Random(1), Anywhere)!;
        Assert.Equal(3, plan.Targets);
        Assert.InRange(Vector2.Distance(plan.StopAt, new Vector2(100, 100)), 0f, 30f);
        Assert.False(plan.Fallback);
        Assert.Equal(1, plan.Entry!.InsideNode);
        Assert.Equal(plan.StopAt, plan.In.End);
        Assert.Equal(1, plan.DoorSide);
    }

    [Fact]
    public void AStopUsedEarlierIsNotUsedAgainAndWithNoTargetsTheNearestToAPlayerWins()
    {
        var g = Square();
        var targets = new[] { new Vector2(95, 95), new Vector2(105, 98) };
        var used = new[] { new Vector2(100, 100) };
        var second = RaidPlanner.Plan(g, Min, Max, targets, used, Array.Empty<Vector2>(), Cfg(), new Random(1), Anywhere)!;
        Assert.True(Vector2.Distance(second.StopAt, used[0]) >= 40f); // the corner is taken: the next best spot elsewhere
        var near = RaidPlanner.Plan(g, Min, Max, Array.Empty<Vector2>(), Array.Empty<Vector2>(), new[] { new Vector2(10, 95) }, Cfg(), new Random(1), Anywhere)!;
        Assert.Equal(0, near.Targets);
        Assert.InRange(Vector2.Distance(near.StopAt, new Vector2(10, 95)), 0f, 12f);
    }

    [Fact]
    public void ASpotTheCrewCannotStepOutOnIsSkippedAndNoSpotMeansNoPlan()
    {
        var g = Square();
        var targets = new[] { new Vector2(95, 95) };
        // refuses everything near the corner: the plan stops somewhere else, and says which side the door is on
        var plan = RaidPlanner.Plan(g, Min, Max, targets, Array.Empty<Vector2>(), Array.Empty<Vector2>(), Cfg(), new Random(1), (at, _) => Vector2.Distance(at, new Vector2(100, 100)) < 25 ? null : -1)!;
        Assert.True(Vector2.Distance(plan.StopAt, new Vector2(100, 100)) >= 25f);
        Assert.Equal(-1, plan.DoorSide);
        Assert.Null(RaidPlanner.Plan(g, Min, Max, targets, Array.Empty<Vector2>(), Array.Empty<Vector2>(), Cfg(), new Random(1), (_, _) => null));
    }

    [Fact]
    public void NoRoadsOrNoEntryPointMeansNoPlanAndTheFallbackParksNearThePlayer()
    {
        Assert.Null(RaidPlanner.Plan(RoadGraph.Empty, Min, Max, Array.Empty<Vector2>(), Array.Empty<Vector2>(), Array.Empty<Vector2>(), Cfg(), new Random(1), Anywhere));
        var inside = new Vector2(500, 500);
        Assert.Null(RaidPlanner.Plan(Square(), new Vector2(-500, -500), inside, Array.Empty<Vector2>(), Array.Empty<Vector2>(), Array.Empty<Vector2>(), Cfg(), new Random(1), Anywhere)); // no road crosses this extent
        var fb = RaidPlanner.Fallback(Square(), new Vector2(50, 60), 25f, new Random(1), Anywhere)!;
        Assert.True(fb.Fallback);
        Assert.True(fb.In.IsEmpty);
        Assert.Null(fb.Entry);
        Assert.InRange(Vector2.Distance(fb.StopAt, new Vector2(50, 60)), 25f, 60f); // at least 25 m from the admin, on a road
        Assert.Null(RaidPlanner.Fallback(RoadGraph.Empty, Vector2.Zero, 25f, new Random(1), Anywhere));
    }

    [Fact]
    public void TheExitGoesOutOverTheRoadsOrStraightOnWhenThereIsNone()
    {
        var g = Square();
        var plan = RaidPlanner.Plan(g, Min, Max, new[] { new Vector2(95, 95) }, Array.Empty<Vector2>(), Array.Empty<Vector2>(), Cfg(), new Random(1), Anywhere)!;
        var exit = RaidPlanner.Exit(g, plan, Min, Max, Cfg());
        Assert.Equal(plan.StopAt, exit.Start);
        Assert.Equal(new Vector2(150, 0), exit.End);
        var noWay = RaidPlanner.Exit(g, plan, new Vector2(-500, -500), new Vector2(500, 500), Cfg()); // no entry on this extent: straight on
        Assert.Equal(150f, noWay.Length, 0.01f);
        Assert.Equal(plan.StopAt, noWay.Start);
    }
}
