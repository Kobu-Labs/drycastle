using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #154: the road polygon lookup from roads.json and the collider group of a hit.</summary>
public class RoadSurfacesTests
{
    // one straight 10 m road from (0,0) to (100,0) and a bent 4 m lane from (0,50) to (20,50) to (20,80)
    const string Roads = """
    {"version": 1, "nodes": [], "edges": [
      {"id": "a", "highway": "primary", "width": 10.0, "polyline": [[0, 0], [100, 0]]},
      {"id": "b", "highway": "path", "width": 4.0, "polyline": [[0, 50], [20, 50], [20, 80]]},
      {"id": "c", "highway": "x", "polyline": [[500, 500], [600, 500]]},
      {"id": "d", "highway": "x", "width": 0, "polyline": [[700, 700], [800, 700]]}
    ]}
    """;

    [Fact]
    public void PointInsideTheRoadWidthIsOnTheRoad()
    {
        var roads = RoadSurfaces.Parse(Roads);
        Assert.True(roads.Contains(50f, 4.9f));   // half width is 5 m
        Assert.True(roads.Contains(50f, -4.9f));
    }

    [Fact]
    public void PointBeyondTheHalfWidthIsNotOnTheRoad()
    {
        var roads = RoadSurfaces.Parse(Roads);
        Assert.False(roads.Contains(50f, 5.5f));
        Assert.False(roads.Contains(50f, 40f));
    }

    [Fact]
    public void PointsPastTheEndOfASegmentUseTheEndCap()
    {
        var roads = RoadSurfaces.Parse(Roads);
        Assert.True(roads.Contains(104f, 0f));    // 4 m past the end, inside the round cap of the 5 m half width
        Assert.False(roads.Contains(106f, 0f));
    }

    [Fact]
    public void EveryPolylineSegmentCounts()
    {
        var roads = RoadSurfaces.Parse(Roads);
        Assert.True(roads.Contains(20f, 65f));    // on the second leg of the bent lane
        Assert.True(roads.Contains(10f, 50f));    // on the first leg
        Assert.False(roads.Contains(10f, 60f));   // between the lane and nothing
    }

    [Fact]
    public void EdgesWithoutAPositiveWidthAreSkipped()
    {
        var roads = RoadSurfaces.Parse(Roads);
        Assert.Equal(3, roads.Count);             // a (one segment) and b (two); c has no width, d a zero width
        Assert.False(roads.Contains(550f, 500f));
        Assert.False(roads.Contains(750f, 700f));
    }

    [Fact]
    public void EmptyOrMissingDataIsEmpty()
    {
        Assert.Equal(0, RoadSurfaces.Parse("").Count);
        Assert.Equal(0, RoadSurfaces.Parse("{}").Count);
        Assert.False(RoadSurfaces.Empty.Contains(0f, 0f));
    }

    [Theory]
    [InlineData(true, "building_plot", 3f, "road")]     // on a road wins over any parcel
    [InlineData(false, "building_plot", 3f, "building")]
    [InlineData(false, "building_plot", 0.2f, null)]    // ground-level hit in a building plot is the terrain
    [InlineData(false, "forest", 6f, "foliage")]
    [InlineData(false, "garden", 1.2f, "wood")]         // a fence
    [InlineData(false, null, 5f, null)]                 // no parcel
    public void GroupFollowsRoadParcelAndHeight(bool onRoad, string? kind, float above, string? expected)
    {
        Assert.Equal(expected, ImpactGroups.For(onRoad, kind, above));
    }

    [Fact]
    public void GroupsClassifyToTheirSurfaces()
    {
        Assert.Equal(SurfaceKind.Wood, SurfaceClassifier.Classify(false, false, false, "wood"));
        Assert.Equal(SurfaceKind.Foliage, SurfaceClassifier.Classify(false, false, false, "foliage"));
        Assert.Equal(SurfaceKind.Road, SurfaceClassifier.Classify(false, false, false, ImpactGroups.For(true, null, 0f)));
    }
}
