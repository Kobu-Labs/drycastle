using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class HouseGridTests
{
    [Fact]
    public void IndexOf_and_Coords_are_inverse()
    {
        var g = TestHouses.Box(7, 6, 9);
        Assert.Equal(3 + 7 * (2 + 6 * 5), g.IndexOf(3, 2, 5));
        Assert.Equal((3, 2, 5), g.Coords(g.IndexOf(3, 2, 5)));
    }

    [Fact]
    public void ToGrid_rotates_by_yaw_and_ToMap_inverts_it()
    {
        var g = TestHouses.Box(8, 8, 8, origin: new Vector3(10, 20, 3), yaw: MathF.PI / 2);
        // grid +x points north at yaw 90 degrees: one metre north of the corner is 4 cells along +x
        var p = g.ToGrid(new Vector3(10, 21, 3.5f));
        Assert.Equal(4, p.X, 4); Assert.Equal(0, p.Y, 4); Assert.Equal(2, p.Z, 4);
        var m = new Vector3(12.3f, 18.7f, 5.1f);
        var back = g.ToMap(g.ToGrid(m));
        Assert.True(Vector3.Distance(m, back) < 1e-4f);
    }

    [Fact]
    public void Voxel_file_round_trips()
    {
        var a = TestHouses.Box(12, 10, 16, id: "b001", origin: new Vector3(1.5f, -2, 0.25f), yaw: 0.3f);
        var b = TestHouses.Box(6, 7, 8, door: false, id: "b002");
        using var ms = new MemoryStream();
        HouseVoxelFile.Write(ms, [a, b]);
        var bytes = ms.ToArray();
        Assert.Equal("HVOX"u8.ToArray(), bytes[..4]);
        var read = HouseVoxelFile.Read(new MemoryStream(bytes));
        Assert.Equal(2, read.Count);
        foreach (var (src, got, i) in new[] { (a, read[0], 0), (b, read[1], 1) })
        {
            Assert.Equal(i, got.Index);
            Assert.Equal(src.Id, got.Id);
            Assert.Equal(src.Origin, got.Origin);
            Assert.Equal(src.Yaw, got.Yaw);
            Assert.Equal(src.Cell, got.Cell);
            Assert.Equal((src.Nx, src.Ny, src.Nz), (got.Nx, got.Ny, got.Nz));
            Assert.Equal(src.Palette.ToArray(), got.Palette.ToArray());
            Assert.Equal(src.Cells.ToArray(), got.Cells.ToArray());
        }
        // deterministic: same input, same bytes
        using var ms2 = new MemoryStream();
        HouseVoxelFile.Write(ms2, read);
        Assert.Equal(bytes, ms2.ToArray());
    }

    [Fact]
    public void Voxel_file_rejects_bad_data()
    {
        using var ms = new MemoryStream();
        HouseVoxelFile.Write(ms, [TestHouses.Box(6, 6, 6)]);
        var bytes = ms.ToArray();
        Assert.Throws<InvalidDataException>(() => HouseVoxelFile.Read(new MemoryStream(bytes[..^1])));
        var bad = (byte[])bytes.Clone(); bad[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => HouseVoxelFile.Read(new MemoryStream(bad)));
        Assert.Throws<ArgumentException>(() => HouseVoxelFile.Write(new MemoryStream(), [TestHouses.Box(6, 6, 6, id: "b"), TestHouses.Box(6, 6, 6, id: "a")]));
    }

    [Fact]
    public void Box_has_foundation_floor_walls_door_ceiling_roof_and_air_attic()
    {
        var g = TestHouses.Box(20, 16, 24, storey: 11);
        CellMaterial M(int x, int y, int z) => g.Material(g.IndexOf(x, y, z));
        Assert.Equal(CellMaterial.Air, M(0, 5, 5));             // margin
        Assert.Equal(CellMaterial.Foundation, M(5, 5, 0));
        Assert.Equal(CellMaterial.Floor, M(5, 5, 1));
        Assert.Equal(CellMaterial.Wall, M(1, 5, 5));
        Assert.Equal(CellMaterial.Wall, M(5, 14, 5));
        Assert.Equal(CellMaterial.Air, M(5, 5, 5));             // room
        Assert.Equal(CellMaterial.Air, M(10, 1, 3));            // door through the y = 1 wall
        Assert.Equal(CellMaterial.Wall, M(10, 1, 11));          // lintel
        Assert.Equal(CellMaterial.Ceiling, M(5, 5, 12));
        Assert.Equal(CellMaterial.Roof, M(5, 1, 13));           // eave skin
        Assert.Equal(CellMaterial.Roof, M(5, 3, 15));
        Assert.Equal(CellMaterial.Air, M(5, 8, 14));            // attic
        Assert.Equal(CellMaterial.Wall, M(1, 8, 14));           // gable end
        Assert.Equal(CellMaterial.Air, M(5, 8, 23));            // top margin
        for (var i = 0; i < g.Count; i++) Assert.NotEqual(CellMaterial.Partition, g.Material(i));
    }

    [Fact]
    public void Cells_remove_is_real_and_restore_clears_it()
    {
        var c = new HouseCells(TestHouses.Box(8, 8, 8));
        var wall = c.Grid.IndexOf(1, 3, 3);
        var air = c.Grid.IndexOf(0, 0, 0);
        Assert.True(c.Solid(wall));
        Assert.False(c.Solid(air));
        Assert.False(c.Damaged);
        c.Remove(air);
        Assert.False(c.Damaged);
        c.Remove(wall);
        Assert.False(c.Solid(wall));
        Assert.True(c.Damaged);
        c.Restore();
        Assert.True(c.Solid(wall));
        Assert.False(c.Damaged);
    }
}
