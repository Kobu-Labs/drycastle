using System.Buffers.Binary;
using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.ShooterCore.Tests.Waters;

/// <summary>water.json version 2: the depth grid of a body, its bilinear lookup in Godot axes, and the version 1 fallback.</summary>
public class WaterBedTests
{
    /// <summary>
    /// A version 2 body: an 8 m by 4 m outline (local x east 0..8, y north 0..4), surface 10 m, on a 2 m grid of 4 by 2 cells.
    /// Depths in cm, south row first: row 0 = 0, 100, 250, 0 and row 1 = 0, 300, 150, 0. Lowest bed 10 - 3 = 7 m.
    /// </summary>
    static string GridJson() => $$"""
    {
      "version": 2,
      "bodies": [
        {
          "id": "R-0001",
          "polygon": { "type": "Polygon", "coordinates": [[[0,0],[8,0],[8,4],[0,4],[0,0]]] },
          "surface_y": 10.0, "bed_y": 7.0, "max_depth_m": 3.0,
          "depth": { "origin": [0, 0], "cell_m": 2.0, "cols": 4, "rows": 2, "cm": "{{Encode(0, 100, 250, 0, 0, 300, 150, 0)}}" },
          "flow": []
        },
        {
          "id": "P-0002",
          "polygon": { "type": "Polygon", "coordinates": [[[100,0],[110,0],[110,10],[100,10],[100,0]]] },
          "surface_y": 10.0, "bed_y": 9.2
        }
      ]
    }
    """;

    /// <summary>Little-endian uint16 centimetres, base64: the encoding of terrain/water.py's DepthGrid.encode.</summary>
    static string Encode(params int[] cm)
    {
        var bytes = new byte[cm.Length * 2];
        for (var k = 0; k < cm.Length; k++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * k), (ushort)cm[k]);
        return Convert.ToBase64String(bytes);
    }

    static MoveState Feet(float x, float feetY, float z)
    {
        var s = new MoveState { Position = new Vector3(x, feetY, z), OnFloor = false, FallPeakY = feetY };
        return s;
    }

    [Fact]
    public void Version2ReadsTheDepthGridPerBody()
    {
        var w = Water.Parse(GridJson());
        var body = w.Bodies[0];
        Assert.NotNull(body.Grid);
        Assert.Equal(4, body.Grid!.Cols);
        Assert.Equal(2, body.Grid.Rows);
        Assert.Equal(3f, body.Grid.MaxDepth, 3);
        Assert.Equal(3f, body.Depth, 3); // the deepest point: surface minus the lowest bed
        Assert.Null(w.Bodies[1].Grid);   // a body without a grid keeps the flat bed
    }

    [Fact]
    public void DepthVariesOverTheBodyAndIsBilinearBetweenCells()
    {
        var w = Water.Parse(GridJson());
        // local (x, y) -> Godot (x, -y): a cell centre is exact
        Assert.Equal(2.5f, w.Depth(5, -1), 3);    // cell (2, 0)
        Assert.Equal(1.0f, w.Depth(3, -1), 3);    // cell (1, 0)
        Assert.Equal(3.0f, w.Depth(3, -3), 3);    // cell (1, 1): the pool
        Assert.Equal(1.75f, w.Depth(4, -1), 3);   // halfway between (1, 0) and (2, 0)
        Assert.NotEqual(w.Depth(3, -1), w.Depth(5, -1));
    }

    [Fact]
    public void DepthAtFeetAndTheSampleUseTheGrid()
    {
        var w = Water.Parse(GridJson());
        Assert.Equal(2.5f, w.DepthAt(new Vector3(5, 0, -1)), 3);
        Assert.Equal(1.0f, w.DepthAt(new Vector3(3, 9.5f, -1)), 3);   // feet under the surface
        Assert.Equal(0f, w.DepthAt(new Vector3(3, 11f, -1)), 3);      // feet in the air above the surface

        var s = Feet(3, 0, -1);
        w.Sample(ref s);
        Assert.Equal(1.0f, s.WaterDepth, 3);
        Assert.Equal(10f, s.WaterSurfaceY, 3);

        s = Feet(5, 0, -1);
        w.Sample(ref s);
        Assert.Equal(2.5f, s.WaterDepth, 3);
    }

    [Fact]
    public void GridIsZeroBeyondItsCellsAndTheDryGroundIsDry()
    {
        var w = Water.Parse(GridJson());
        Assert.Equal(0f, w.Depth(20, -2), 3);            // not over any body
        Assert.Equal(0f, w.Bodies[0].DepthAt(-30, 2), 3); // beyond the grid
        Assert.Equal(0f, w.Bodies[0].DepthAt(-100, -100), 3);
    }

    [Fact]
    public void Version1IsAFlatBedAndKeepsItsSemantics()
    {
        const string v1 = """
        {
          "version": 1,
          "bodies": [
            { "id": "P-0001", "polygon": { "type": "Polygon", "coordinates": [[[0,0],[40,0],[40,40],[0,40],[0,0]]] }, "surface_y": 10.0, "bed_y": 8.0 }
          ]
        }
        """;
        var w = Water.Parse(v1);
        var body = w.Bodies[0];
        Assert.Null(body.Grid);
        Assert.Equal(2f, body.Depth, 3);
        Assert.Equal(2f, w.Depth(20, -20), 3);
        Assert.Equal(2f, w.Depth(1, -39), 3);
        Assert.Equal(2f, w.DepthAt(new Vector3(20, 0, -20)), 3);
    }

    [Fact]
    public void Version2RejectsAGridWhoseBytesDoNotMatchItsSize()
    {
        var bad = GridJson().Replace(Encode(0, 100, 250, 0, 0, 300, 150, 0), Encode(0, 100), StringComparison.Ordinal);
        Assert.Throws<DataFileException>(() => Water.Parse(bad));
    }

    [Fact]
    public void Version3IsRejected()
    {
        Assert.Throws<DataFileException>(() => Water.Parse("""{"version": 3, "bodies": []}"""));
    }
}
