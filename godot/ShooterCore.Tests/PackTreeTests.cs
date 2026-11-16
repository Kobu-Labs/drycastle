using System.Numerics;
using Drycastle.MapLoader;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class PackTreeTests
{
    static readonly TreeModel Model = new(0.35f, 1.8f, 2.6f);

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    [Fact]
    public void ShippedConfigParsesAndHasBothShapes()
    {
        var c = PackTreeConfig.Parse(File.ReadAllText(Path.Combine(DataDir, "pack_trees.json")));
        Assert.True(c.NearM > 0 && c.TileM > 0);
        Assert.NotEmpty(c.For(cone: true));
        Assert.NotEmpty(c.For(cone: false));
        Assert.All(c.For(true).Concat(c.For(false)), e => Assert.StartsWith("SM_Tree_", e.Name));
    }

    [Fact]
    public void ConfigRejectsBadNumbers()
    {
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"near_m":0}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"tile_m":-1}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"shapes":{"cone":[{"name":"a","weight":-1}]}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"near_m":50}""")); // no version
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData(" False ", false)]
    public void EnvSwitch(string? value, bool on) => Assert.Equal(on, PackTreeSettings.ParseEnabled(value));

    [Fact]
    public void PickFollowsWeightsAndSkipsZero()
    {
        var w = new[] { 1f, 0f, 3f };
        Assert.Equal(0, PackTreeMath.Pick(w, 0.0));
        Assert.Equal(0, PackTreeMath.Pick(w, 0.24));
        Assert.Equal(2, PackTreeMath.Pick(w, 0.25));
        Assert.Equal(2, PackTreeMath.Pick(w, 0.999));
        Assert.Equal(-1, PackTreeMath.Pick(new[] { 0f, 0f }, 0.5));
        Assert.Equal(-1, PackTreeMath.Pick(Array.Empty<float>(), 0.5));
    }

    [Fact]
    public void ChoiceIsDeterministicAndSpreadsOverModels()
    {
        Assert.Equal(PackTreeMath.Roll(201.4f, -1677.2f), PackTreeMath.Roll(201.4f, -1677.2f));
        var counts = new int[4];
        for (var i = 0; i < 4000; i++)
        {
            var r = PackTreeMath.Roll(i * 3.7f, i * 1.3f - 500f);
            Assert.InRange(r, 0.0, 1.0);
            counts[PackTreeMath.Pick(new[] { 1f, 1f, 1f, 1f }, r)]++;
        }
        Assert.All(counts, c => Assert.InRange(c, 800, 1200)); // about a quarter each
    }

    [Fact]
    public void ScaleMatchesTheCodeBuiltTreeHeight()
    {
        // cone: trunk 1.8 + 1.5 * canopy 2.6 = 5.7 m at scale 1; box: 1.8 - 0.3 + 1.6 * 2.6 = 5.66 m
        var cone = new TreeInstance(0, 0, 10f, 1f, 0, true, 0, 0);
        var box = new TreeInstance(0, 0, 10f, 1f, 0, false, 0, 0);
        Assert.Equal(5.7f, PackTreeMath.CodeHeight(cone, Model), 3);
        Assert.Equal(5.66f, PackTreeMath.CodeHeight(box, Model), 3);
        Assert.Equal(5.7f / 12f, PackTreeMath.ScaleFor(cone, Model, 12f), 4);
        var big = cone with { Scale = 2f };
        Assert.Equal(2f * 5.7f / 12f, PackTreeMath.ScaleFor(big, Model, 12f), 4);
        Assert.Equal(1f, PackTreeMath.ScaleFor(cone, Model, 0f));
    }

    [Fact]
    public void TilesAreFlooredAndWithinRadius()
    {
        Assert.Equal(-1, PackTreeMath.TileOf(-0.1f, 32f));
        Assert.Equal(2, PackTreeMath.TileOf(64f, 32f));
        var trees = new[] { new TreeInstance(5, 5, 0, 1, 0, true, 0, 0), new TreeInstance(40, -5, 0, 1, 0, false, 0, 0), new TreeInstance(6, 6, 0, 1, 0, true, 0, 0) };
        var index = PackTreeMath.TileIndex(trees, 32f);
        Assert.Equal(new[] { 0, 2 }, index[(0, 0)]);
        Assert.Equal(new[] { 1 }, index[(1, -1)]);
        var tiles = PackTreeMath.TilesWithin(0f, 0f, 40f, 32f).ToList();
        Assert.Contains(tiles, t => t.Tx == 0 && t.Ty == 0);
        Assert.All(tiles, t => Assert.True(t.Dist <= 40f));
        Assert.DoesNotContain(tiles, t => t.Tx == 5 && t.Ty == 5);
        Assert.Equal(0f, PackTreeMath.DistToBox(1, 1, 0, 0, 2, 2));
        Assert.Equal(5f, PackTreeMath.DistToBox(5, 0, 0, 0, 0, 0), 3);
    }

    // The baked tree, as pipeline propmeshes.tree_mesh makes it (ported: box_tris and pyramid_tris, same order and winding).
    static List<(Vector3, Vector3, Vector3)> BakedTree(TreeInstance t)
    {
        var s = t.Scale;
        float trunkW = Model.TrunkWidthM * s, trunkH = Model.TrunkHeightM * s, canopy = Model.CanopyM * s;
        var (ux, uy) = (MathF.Cos(t.Rot), MathF.Sin(t.Rot));
        var tris = new List<(Vector3, Vector3, Vector3)>();
        void Box(float half, float z0, float z1, bool bottom)
        {
            var p0 = new Vector2(t.X - ux * half, t.Y - uy * half); var p1 = new Vector2(t.X + ux * half, t.Y + uy * half);
            var d = Vector2.Normalize(p1 - p0);
            var v = new Vector2(-d.Y, d.X) * half; // width / 2 with width = 2 * half
            Vector2[] ring = [p0 - v, p1 - v, p1 + v, p0 + v];
            Vector3 P(Vector2 p, float z) => new(p.X, p.Y, z);
            for (var k = 0; k < 4; k++)
            {
                Vector2 a = ring[k], b = ring[(k + 1) % 4];
                tris.Add((P(a, z0), P(b, z0), P(b, z1)));
                tris.Add((P(a, z0), P(b, z1), P(a, z1)));
            }
            tris.Add((P(ring[0], z1), P(ring[1], z1), P(ring[2], z1)));
            tris.Add((P(ring[0], z1), P(ring[2], z1), P(ring[3], z1)));
            if (bottom)
            {
                tris.Add((P(ring[0], z0), P(ring[2], z0), P(ring[1], z0)));
                tris.Add((P(ring[0], z0), P(ring[3], z0), P(ring[2], z0)));
            }
        }
        void Pyramid(float half, float z0, float z1)
        {
            var (c, sn) = (MathF.Cos(t.Rot), MathF.Sin(t.Rot));
            var ring = new[] { (-half, -half), (half, -half), (half, half), (-half, half) }
                .Select(q => new Vector3(t.X + c * q.Item1 - sn * q.Item2, t.Y + sn * q.Item1 + c * q.Item2, z0)).ToArray();
            var apex = new Vector3(t.X, t.Y, z1);
            for (var k = 0; k < 4; k++) tris.Add((ring[k], ring[(k + 1) % 4], apex));
            tris.Add((ring[0], ring[2], ring[1]));
            tris.Add((ring[0], ring[3], ring[2]));
        }
        Box(trunkW / 2, t.Z - 0.2f, t.Z + trunkH, false);
        if (t.Cone)
        {
            Pyramid(canopy * 0.55f, t.Z + trunkH * 0.5f, t.Z + trunkH + canopy * 0.7f);
            Pyramid(canopy * 0.38f, t.Z + trunkH + canopy * 0.35f, t.Z + trunkH + canopy * 1.5f);
        }
        else
        {
            var lo = t.Z + trunkH - 0.3f * s;
            Box(canopy / 2, lo, lo + canopy, true); // half > 0.4 * canopy: with a bottom
            Box(canopy * 0.3f, lo + canopy, lo + canopy * 1.6f, false);
        }
        return tris;
    }

    [Fact]
    public void EveryTriangleOfABakedTreeFindsItsTree()
    {
        var trees = new[]
        {
            new TreeInstance(100.3f, -40.7f, 187.708f, 2.692f, 1.389f, false, 0, 0),
            new TreeInstance(101.2f, -41.9f, 187.9f, 1.615f, 0.679f, true, 0, 0), // overlaps the first
            new TreeInstance(120f, -40f, 190f, 0.8f, 5.9f, true, 0, 0),
            new TreeInstance(100.3f, -52f, 186f, 1.2f, 3.1f, false, 0, 0),
        };
        var owner = new TreeTriangleOwner(trees, Model);
        for (var i = 0; i < trees.Length; i++)
        {
            var tris = BakedTree(trees[i]);
            Assert.Equal(trees[i].Cone ? 22 : 32, tris.Count);
            foreach (var (a, b, c) in tris) Assert.Equal(i, owner.Find(a, b, c));
        }
        // and in a different order, as a chunk mesh holds them
        foreach (var i in new[] { 3, 1, 0, 2 })
            foreach (var (a, b, c) in BakedTree(trees[i])) Assert.Equal(i, owner.Find(a, b, c));
    }

    [Fact]
    public void FenceTrianglesBelongToNoTree()
    {
        var trees = new[] { new TreeInstance(100f, -40f, 187f, 1.5f, 0.3f, true, 0, 0) };
        var owner = new TreeTriangleOwner(trees, Model);
        // a fence panel running through the tree's footprint, and one far away
        var panel = new[] { new Vector3(98f, -40f, 187f), new Vector3(102f, -40f, 187f), new Vector3(102f, -40f, 188.2f) };
        Assert.Equal(-1, owner.Find(panel[0], panel[1], panel[2]));
        Assert.Equal(-1, owner.Find(new Vector3(500, 500, 0), new Vector3(501, 500, 0), new Vector3(501, 500, 1)));
    }
}
