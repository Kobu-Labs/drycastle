using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The tactical map's waypoint: click to set, click again to clear, reached, the distance text and the sheet-to-ground inverse.</summary>
public class WaypointTests
{
    [Fact]
    public void ClickSetsThenClickingTheMarkerAgainClears()
    {
        var w = new Waypoint();
        Assert.True(w.Click(new Vector2(100, 200), 30f));
        Assert.Equal(new Vector2(100, 200), w.At);
        Assert.True(w.Click(new Vector2(300, 200), 30f));   // elsewhere: moves it
        Assert.Equal(new Vector2(300, 200), w.At);
        Assert.False(w.Click(new Vector2(310, 205), 30f));  // on the marker: takes it away
        Assert.Null(w.At);
    }

    [Fact]
    public void ReachedClearsOnlyWithinFiveMetres()
    {
        var w = new Waypoint();
        w.Set(new Vector2(0, 0));
        Assert.False(w.ClearIfReached(new Vector2(0, 5.5f)));
        Assert.NotNull(w.At);
        Assert.True(w.ClearIfReached(new Vector2(3, 3)));
        Assert.Null(w.At);
        Assert.False(w.ClearIfReached(new Vector2(0, 0))); // nothing set
    }

    [Theory]
    [InlineData(0f, "0m")]
    [InlineData(-4f, "0m")]
    [InlineData(499.6f, "500m")]
    [InlineData(999.6f, "1.0km")]
    [InlineData(1000f, "1.0km")]
    [InlineData(1249f, "1.2km")]
    [InlineData(12340f, "12.3km")]
    public void DistanceIsMetresThenKilometres(float metres, string expected) => Assert.Equal(expected, Waypoint.Distance(metres));

    [Fact]
    public void FromSheetInvertsToSheet()
    {
        var g = new Vector2(250f, 750f);
        var back = TacticalMapMath.FromSheet(TacticalMapMath.ToSheet(g, -500f, 1000f, 1000f), -500f, 1000f, 1000f);
        Assert.Equal(g.X, back.X, 3);
        Assert.Equal(g.Y, back.Y, 3);
        Assert.Equal(new Vector2(0f, 0f), TacticalMapMath.FromSheet(new Vector2(0f, 0f), 0f, 1000f, 1000f)); // north-west corner
    }
}
