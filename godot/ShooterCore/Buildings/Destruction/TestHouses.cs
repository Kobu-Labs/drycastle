using System.Numerics;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Fixture houses for the tests and the client's stand-in glb (until the pipeline writes real ones).</summary>
public static class TestHouses
{
    public const byte PAir = 0, PFoundation = 1, PFloor = 2, PWall = 3, PCeiling = 4, PRoof = 5, PPartition = 6;

    static readonly (CellMaterial, byte, byte, byte)[] BoxPalette =
    [
        (CellMaterial.Air, 0, 0, 0),
        (CellMaterial.Foundation, 110, 110, 110),
        (CellMaterial.Floor, 150, 110, 70),
        (CellMaterial.Wall, 225, 215, 190),
        (CellMaterial.Ceiling, 240, 240, 235),
        (CellMaterial.Roof, 160, 60, 45),
        (CellMaterial.Partition, 205, 200, 185),
    ];

    /// <summary>
    /// A one-storey box house filling an nx x ny x nz grid, laid out as the voxeliser does (Contract 1): one air cell of margin on each side and at the top;
    /// z = 0 the foundation across the footprint, z = 1 the floor slab, the outer walls one cell thick from z = 2 up to the ceiling slab at
    /// z = 1 + <paramref name="storey"/> (clipped to fit), then a gable roof skin along x (rising one cell per cell inward from the long sides, closed by
    /// gable walls at the x ends; the attic is air; a roof cut off by the grid's top is closed flat). One door through the wall at y = 1, centred in x,
    /// 4 cells wide and 8 high (clipped). The smallest box is 5 x 5 x 5.
    /// </summary>
    public static HouseGrid Box(int nx, int ny, int nz, int storey = 11, bool door = true, ushort index = 0, string id = "test_box",
        Vector3 origin = default, float yaw = 0, float cell = 0.25f)
    {
        if (nx < 5 || ny < 5 || nz < 5) throw new ArgumentException("TestHouses.Box needs at least 5 x 5 x 5");
        var cells = new byte[nx * ny * nz];
        int x0 = 1, x1 = nx - 2, y0 = 1, y1 = ny - 2, top = nz - 2;
        var ceil = Math.Min(1 + Math.Max(storey, 1), top);
        void Set(int x, int y, int z, byte p) => cells[x + nx * (y + ny * z)] = p;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                Set(x, y, 0, PFoundation);
                Set(x, y, 1, PFloor);
                if (ceil > 1) Set(x, y, ceil, PCeiling);
                if (x == x0 || x == x1 || y == y0 || y == y1)
                    for (var z = 2; z < ceil; z++) Set(x, y, z, PWall);
            }
        if (door && ceil > 2)
        {
            var w = Math.Min(4, x1 - x0 - 1);
            var dx = x0 + 1 + (x1 - x0 - 1 - w) / 2;
            for (var x = dx; x < dx + w; x++)
                for (var z = 2; z < Math.Min(2 + 8, ceil); z++) Set(x, y0, z, PAir);
        }
        // gable roof skin over the ceiling, one cell up per cell inward from y0 and y1
        for (var k = 0; ceil + 1 + k <= top; k++)
        {
            int z = ceil + 1 + k, ya = y0 + k, yb = y1 - k;
            if (ya > yb) break;
            var closeFlat = z == top || ya + 1 >= yb;
            for (var x = x0; x <= x1; x++)
                for (var y = ya; y <= yb; y++)
                {
                    var skin = y == ya || y == yb || closeFlat;
                    if (skin) Set(x, y, z, PRoof);
                    else if (x == x0 || x == x1) Set(x, y, z, PWall);
                }
            if (closeFlat) break;
        }
        return new HouseGrid(index, id, origin, yaw, cell, nx, ny, nz, BoxPalette, cells);
    }
}
