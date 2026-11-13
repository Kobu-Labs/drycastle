using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The tactical map (M): grid cell size, grid references, the world-to-sheet transform, heading, scale bar and layout.</summary>
public class TacticalMapMathTests
{
    [Theory]
    [InlineData(300f, 50f)]     // 6 cells: the smallest size is the floor
    [InlineData(800f, 50f)]     // 16 cells
    [InlineData(1000f, 50f)]    // exactly 20 cells
    [InlineData(1001f, 100f)]   // 21 at 50 m, so 100 m: 11 cells
    [InlineData(1500f, 100f)]
    [InlineData(2000f, 100f)]
    [InlineData(3000f, 200f)]
    [InlineData(4000f, 200f)]
    [InlineData(9000f, 500f)]
    public void CellSizeKeepsTheGridToAtMostTwentyCellsASide(float extent, float expected)
    {
        Assert.Equal(expected, TacticalMapMath.CellSize(extent));
    }

    [Fact]
    public void CellSizeGivesTenToTwentyCellsAcrossTheUsualMapRange()
    {
        for (var extent = 600f; extent <= 4000f; extent += 25f)
        {
            var cells = TacticalMapMath.Cells(extent, TacticalMapMath.CellSize(extent));
            Assert.InRange(cells, 8, TacticalMapMath.MaxCells); // the lower end is reached just after a size step
        }
    }

    [Fact]
    public void CellsRoundUpSoTheEdgeIsCovered()
    {
        Assert.Equal(15, TacticalMapMath.Cells(1500f, 100f));
        Assert.Equal(16, TacticalMapMath.Cells(1501f, 100f));
        Assert.Equal(1, TacticalMapMath.Cells(0f, 100f));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(2, "C")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void ColumnsAreLettersThatContinueAfterZ(int column, string expected)
    {
        Assert.Equal(expected, TacticalMapMath.ColumnLabel(column));
    }

    [Fact]
    public void RowsCountFromOne()
    {
        Assert.Equal("1", TacticalMapMath.RowLabel(0));
        Assert.Equal("7", TacticalMapMath.RowLabel(6));
        Assert.Equal("C7", TacticalMapMath.Reference(2, 6));
    }

    [Fact]
    public void ToMapMeasuresEastingFromTheWestEdgeAndNorthingFromTheSouthEdge()
    {
        // west edge at x = -500, south edge at z = 300 (north is -z)
        var m = TacticalMapMath.ToMap(new Vector2(-500f, 300f), -500f, 300f);
        Assert.Equal(Vector2.Zero, m);                                      // the south-west corner
        var ne = TacticalMapMath.ToMap(new Vector2(500f, -700f), -500f, 300f);
        Assert.Equal(new Vector2(1000f, 1000f), ne);                        // the north-east corner of a 1000 m map
        var north = TacticalMapMath.ToMap(new Vector2(0f, 0f), -500f, 300f);
        Assert.Equal(300f, north.Y);                                        // 300 m north of the south edge
    }

    [Fact]
    public void ToGroundIsTheInverseOfToMap()
    {
        var g = new Vector2(123.5f, -87.25f);
        var back = TacticalMapMath.ToGround(TacticalMapMath.ToMap(g, -500f, 300f), -500f, 300f);
        Assert.Equal(g.X, back.X, 3);
        Assert.Equal(g.Y, back.Y, 3);
    }

    [Fact]
    public void ToSheetPutsNorthWestAtTheTopLeft()
    {
        // map 1000 m, west edge x = 0, south edge z = 1000 (so the north edge is z = 0)
        Assert.Equal(new Vector2(0f, 0f), TacticalMapMath.ToSheet(new Vector2(0f, 0f), 0f, 1000f, 1000f));        // north-west
        Assert.Equal(new Vector2(1f, 1f), TacticalMapMath.ToSheet(new Vector2(1000f, 1000f), 0f, 1000f, 1000f));  // south-east
        Assert.Equal(new Vector2(0.25f, 0.75f), TacticalMapMath.ToSheet(new Vector2(250f, 750f), 0f, 1000f, 1000f));
        Assert.Equal(-0.1f, TacticalMapMath.ToSheet(new Vector2(-100f, 500f), 0f, 1000f, 1000f).X, 5);          // off the map is not clamped
    }

    [Fact]
    public void CellOfFindsTheSquareAndClampsOntoTheGrid()
    {
        // 1000 m map, 100 m cells: ten columns, ten rows
        Assert.Equal((0, 0), TacticalMapMath.CellOf(new Vector2(1f, 1f), 1000f, 100f));
        Assert.Equal((2, 6), TacticalMapMath.CellOf(new Vector2(250f, 650f), 1000f, 100f)); // C7
        Assert.Equal((2, 6), TacticalMapMath.CellOf(new Vector2(200f, 600f), 1000f, 100f)); // a line belongs to the cell to its north-east
        Assert.Equal((9, 9), TacticalMapMath.CellOf(new Vector2(1000f, 1000f), 1000f, 100f)); // the far edge is still in the last cell
        Assert.Equal((0, 9), TacticalMapMath.CellOf(new Vector2(-30f, 5000f), 1000f, 100f));  // off the map
        Assert.Equal("C7", TacticalMapMath.Reference(2, 6));
    }

    [Fact]
    public void ReferenceOfNamesTheCellOfAGroundPointOnTheSheet()
    {
        // 1000 m sheet, west edge x = -500, south edge z = 500 (north is -z): 50 m cells, so ground (-250, 100) is 250 m east and 400 m north of the corner
        Assert.Equal(50f, TacticalMapMath.CellSize(1000f));
        Assert.Equal("F9", TacticalMapMath.ReferenceOf(new Vector2(-250f, 100f), -500f, 500f, 1000f));
        Assert.Equal("A1", TacticalMapMath.ReferenceOf(new Vector2(-500f, 500f), -500f, 500f, 1000f)); // the south-west corner
    }

    [Fact]
    public void ThePartialLastCellStillHasAReference()
    {
        // 1050 m with 100 m cells: eleven columns, the last one 50 m wide
        Assert.Equal((10, 0), TacticalMapMath.CellOf(new Vector2(1040f, 10f), 1050f, 100f));
    }

    [Theory]
    [InlineData(0f, 0)]                      // facing north (-z)
    [InlineData(-MathF.PI / 2f, 90)]         // yaw -90 deg: forward = +x, east
    [InlineData(MathF.PI, 180)]              // south
    [InlineData(MathF.PI / 2f, 270)]         // west
    [InlineData(-MathF.PI / 4f, 45)]         // north-east
    [InlineData(MathF.PI * 2f, 0)]           // a full turn is north again
    [InlineData(-MathF.PI * 2f - 0.1f, 6)]   // beyond a full turn, still wrapped
    public void HeadingIsClockwiseDegreesFromNorth(float yaw, int expected)
    {
        Assert.Equal(expected, TacticalMapMath.HeadingDegrees(yaw));
    }

    [Fact]
    public void HeadingMatchesTheForwardVectorOfTheGameConvention()
    {
        // forward = (-sin yaw, -cos yaw) in ground x/z; the heading is the bearing of that vector (x east, -z north)
        for (var yaw = -7f; yaw <= 7f; yaw += 0.37f)
        {
            var fx = -MathF.Sin(yaw);
            var fnorth = MathF.Cos(yaw);
            var bearing = MathF.Atan2(fx, fnorth); // clockwise from north
            Assert.Equal(MinimapView.Wrap(bearing), MinimapView.Wrap(TacticalMapMath.HeadingRadians(yaw)), 4);
        }
    }

    [Fact]
    public void HeadingDegreesNeverReaches360()
    {
        for (var yaw = -10f; yaw <= 10f; yaw += 0.001f)
            Assert.InRange(TacticalMapMath.HeadingDegrees(yaw), 0, 359);
        Assert.Equal(0, TacticalMapMath.HeadingDegrees(0.0001f)); // 359.994 rounds up to 360, which is 0
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(22, 0)]
    [InlineData(23, 1)]
    [InlineData(90, 2)]
    [InlineData(180, 4)]
    [InlineData(270, 6)]
    [InlineData(338, 0)]
    [InlineData(337, 7)]
    public void OctantNamesTheNearestOfEightPoints(int degrees, int expected)
    {
        Assert.Equal(expected, TacticalMapMath.Octant(degrees));
    }

    [Theory]
    [InlineData(1f, 300f, 200f)]    // 1 px per metre, up to 300 px: 200 m
    [InlineData(1f, 500f, 500f)]
    [InlineData(1f, 99f, 50f)]
    [InlineData(2f, 300f, 100f)]    // 150 m fit: 100 m
    [InlineData(0.5f, 300f, 500f)]  // 600 m fit: 500 m
    [InlineData(0.1f, 300f, 2000f)] // 3000 m fit: 2000 m
    [InlineData(10f, 30f, 2f)]      // 3 m fit: 2 m
    public void ScaleBarIsTheLongestRoundLengthThatFits(float pxPerMetre, float maxPixels, float expected)
    {
        Assert.Equal(expected, TacticalMapMath.ScaleBarMetres(pxPerMetre, maxPixels), 3);
    }

    [Fact]
    public void ScaleBarHandlesNothingToDraw()
    {
        Assert.Equal(0f, TacticalMapMath.ScaleBarMetres(0f, 300f));
        Assert.Equal(0f, TacticalMapMath.ScaleBarMetres(1f, 0f));
    }

    [Theory]
    [InlineData(1920f, 1080f)]
    [InlineData(2560f, 1080f)]   // 21:9 at 1080 p
    [InlineData(1280f, 720f)]
    [InlineData(3840f, 2160f)]
    [InlineData(1707f, 720f * 1.5f)]
    public void TheMapIsSquareAndCentredWithRoomForBothPanels(float w, float h)
    {
        const float margin = 32f, gutter = 28f, panel = 260f, gap = 16f;
        var side = TacticalMapLayout.Side(w, h, margin, gutter, panel, gap);
        var o = TacticalMapLayout.Origin(w, h, side);
        Assert.True(side > 0f);
        Assert.True(side <= h - 2 * margin - 2 * gutter + 0.01f);               // fits the height
        Assert.True(o.X - gutter - gap >= margin + panel - 0.01f);              // the left panel fits beside the sheet
        Assert.True(w - (o.X + side) - gutter - gap >= margin + panel - 0.01f); // so does the right one
        Assert.Equal(o.X, w - (o.X + side), 3);                                 // centred on x
        Assert.Equal(o.Y, h - (o.Y + side), 3);                                 // and on y
    }

    [Fact]
    public void ANarrowScreenShrinksTheMapInsteadOfCoveringThePanels()
    {
        var wide = TacticalMapLayout.Side(1920f, 1080f, 32f, 28f, 260f, 16f);
        var narrow = TacticalMapLayout.Side(1200f, 1080f, 32f, 28f, 260f, 16f);
        Assert.True(narrow < wide);
        Assert.Equal(160f, TacticalMapLayout.Side(100f, 100f, 32f, 28f, 260f, 16f)); // never under the minimum
    }
}
