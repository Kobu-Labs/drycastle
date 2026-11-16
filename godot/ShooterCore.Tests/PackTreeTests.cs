using Drycastle.MapLoader;
using Xunit;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;

namespace Drycastle.ShooterCore.Tests;

public class PackTreeTests
{
    static readonly TreeModel Model = new(0.35f, 1.8f, 2.6f);

    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    static PackTreeConfig Shipped() => PackTreeConfig.Parse(File.ReadAllText(Path.Combine(DataDir, "pack_trees.json")));

    [Fact]
    public void ShippedConfigParsesAndHasBothShapes()
    {
        var c = Shipped();
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
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"max_in_flight":0}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"scale_min":2,"scale_max":1}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"bulk_share":1.5}"""));
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
    public void TilesAreFlooredAndNearByTheirBox()
    {
        Assert.Equal(-1, PackTreeMath.TileOf(-0.1f, 32f));
        Assert.Equal(2, PackTreeMath.TileOf(64f, 32f));
        var trees = new[] { new TreeInstance(5, 5, 0, 1, 0, true, 0, 0), new TreeInstance(40, -5, 0, 1, 0, false, 0, 0), new TreeInstance(6, 6, 0, 1, 0, true, 0, 0) };
        var index = PackTreeMath.TileIndex(trees, 32f);
        Assert.Equal(new[] { 0, 2 }, index[(0, 0)]);
        Assert.Equal(new[] { 1 }, index[(1, -1)]);
        var tiles = PackTreeMath.TilesNear(0f, 0f, 40f, 32f).ToList();
        Assert.Contains(tiles, t => t.Tx == 0 && t.Ty == 0 && t.Dist == 0f);
        Assert.All(tiles, t => Assert.True(t.Dist <= 40f));
        Assert.DoesNotContain(tiles, t => t.Tx == 5 && t.Ty == 5);
        // a tile whose centre is out of reach but whose edge is within it is wanted: its trees may stand inside the radius
        Assert.Contains(tiles, t => t.Tx == 1 && t.Ty == 0 && Math.Abs(t.Dist - 32f) < 1e-3f);
        Assert.Equal(0f, PackTreeMath.DistToBox(1, 1, 0, 0, 2, 2));
        Assert.Equal(5f, PackTreeMath.DistToBox(5, 0, 0, 0, 0, 0), 3);
    }

    // ---- model choice by size ----

    static PackCandidate C(int id, float height, float weight = 1f, bool bulk = true) => new(id, height, weight, bulk);

    [Fact]
    public void ChoiceKeepsTheScaleWithinTheSpanWhenAModelFits()
    {
        var list = new[] { C(0, 3.2f), C(1, 6.7f), C(2, 8.6f) };
        // a 13 m tree: scales 4.06, 1.94, 1.51 with the span [0.5, 4]: the 3.2 m model is out
        for (var i = 0; i < 500; i++) Assert.NotEqual(0, PackTreeMath.Choose(list, 13f, i / 500.0, 0.5f, 4f, 1f));
        // a 6 m tree: scales 1.9, 0.9, 0.7, all fit, and all are chosen over the rolls
        var seen = Enumerable.Range(0, 500).Select(i => PackTreeMath.Choose(list, 6f, i / 500.0, 0.5f, 4f, 1f)).Distinct().Order().ToList();
        Assert.Equal(new[] { 0, 1, 2 }, seen);
    }

    [Fact]
    public void ChoiceFallsBackToTheNearestModelOutsideTheSpan()
    {
        var list = new[] { C(0, 3.2f), C(1, 6.7f), C(2, 8.6f) };
        // a 40 m tree needs scale 12.5, 6, 4.65: none fits, the tallest is the nearest
        for (var i = 0; i < 50; i++) Assert.Equal(2, PackTreeMath.Choose(list, 40f, i / 50.0, 0.5f, 4f, 1f));
        // a 0.5 m tree needs scale 0.16, 0.07, 0.06: the shortest is the nearest
        for (var i = 0; i < 50; i++) Assert.Equal(0, PackTreeMath.Choose(list, 0.5f, i / 50.0, 0.5f, 4f, 1f));
    }

    [Fact]
    public void ChoiceTakesBulkModelsByShareAndFallsBackWithinTheKind()
    {
        // two bulk models (a short one and a tall one) and one occasional model
        var list = new[] { C(0, 4f, 1f, true), C(1, 8f, 1f, true), C(2, 6f, 1f, false) };
        var counts = new int[3];
        for (var i = 0; i < 1000; i++) counts[PackTreeMath.Choose(list, 6f, i / 1000.0, 0.5f, 4f, 0.75f)]++;
        Assert.Equal(new[] { 375, 375, 250 }, counts);
        // a 20 m tree: scales 5, 2.5, 3.3; no bulk model but the tall one fits, the occasional one does, for its share only
        counts = new int[3];
        for (var i = 0; i < 1000; i++) counts[PackTreeMath.Choose(list, 20f, i / 1000.0, 0.5f, 4f, 0.75f)]++;
        Assert.Equal(new[] { 0, 750, 250 }, counts);
        // a 40 m tree: no bulk model fits (scale 10, 5), so the tall bulk one stands in; the occasional one (6.7) is out too and stands in for its share
        counts = new int[3];
        for (var i = 0; i < 1000; i++) counts[PackTreeMath.Choose(list, 40f, i / 1000.0, 0.5f, 4f, 0.75f)]++;
        Assert.Equal(new[] { 0, 750, 250 }, counts);
        // only one kind in the list: all trees take it, whatever the share
        Assert.Equal(0, PackTreeMath.Choose(new[] { C(2, 6f, 1f, false) }, 6f, 0.1, 0.5f, 4f, 0.9f));
        Assert.Equal(0, PackTreeMath.Choose(new[] { C(0, 6f, 1f, true) }, 6f, 0.99, 0.5f, 4f, 0.1f));
        Assert.Equal(-1, PackTreeMath.Choose(new[] { C(0, 6f, 0f) }, 6f, 0.5, 0.5f, 4f, 0.5f));
    }

    [Fact]
    public void PlacementIsDeterministicAndScalesToTheCodeHeight()
    {
        var cfg = Shipped();
        var cands = new[] { C(0, 3.2f, 1f, true), C(1, 6.7f, 1f, true), C(2, 8.6f, 1f, false) };
        var t = new TreeInstance(201.4f, -1677.2f, 150f, 1.3f, 0.7f, true, 0, 0);
        var a = PackTreeMath.Place(t, Model, cands, cands, cfg)!.Value;
        var b = PackTreeMath.Place(t, Model, cands, cands, cfg)!.Value;
        Assert.Equal(a, b);
        Assert.Equal(new V3(201.4f, 150f, 1677.2f), a.Base); // Godot axes: x, up, -y
        Assert.Equal(PackTreeMath.CodeHeight(t, Model) / cands[a.Model].HeightM, a.Scale, 4);
        Assert.Equal(0.7f, a.Rot);
        Assert.Null(PackTreeMath.Place(t, Model, Array.Empty<PackCandidate>(), Array.Empty<PackCandidate>(), cfg));
    }

    [Fact]
    public void ShippedMixMarksTheBulkModelsAndHasBothKinds()
    {
        var cfg = Shipped();
        var all = cfg.For(true).Concat(cfg.For(false)).ToList();
        Assert.Contains(cfg.For(true), e => e.Bulk);
        Assert.Contains(cfg.For(true), e => !e.Bulk);
        Assert.Contains(cfg.For(false), e => e.Bulk);
        Assert.Contains(cfg.For(false), e => !e.Bulk);
        Assert.All(all.Where(e => e.Name.StartsWith("SM_Tree_PolyPine") || e.Name.StartsWith("SM_Tree_Round") || e.Name == "SM_Tree_TallRound_01" || e.Name.StartsWith("SM_Tree_0")), e => Assert.True(e.Bulk, e.Name));
        Assert.All(all.Where(e => e.Name.StartsWith("SM_Tree_Birch") || e.Name.StartsWith("SM_Tree_Pine_")), e => Assert.False(e.Bulk, e.Name));
        Assert.InRange(cfg.BulkShare, 0.5f, 1f);
    }

    // ---- the cut ----

    [Fact]
    public void PackAndBakedSharesAddUpToOneAtEveryDistance()
    {
        // radius 70, band 14: all baked outside 70, all pack inside 56, linear between
        Assert.Equal(0f, PackTreeMath.FarShare(10f, 70f, 14f));
        Assert.Equal(0f, PackTreeMath.FarShare(56f, 70f, 14f));
        Assert.Equal(0.5f, PackTreeMath.FarShare(63f, 70f, 14f), 4);
        Assert.Equal(1f, PackTreeMath.FarShare(70f, 70f, 14f));
        Assert.Equal(1f, PackTreeMath.FarShare(400f, 70f, 14f));
        // a hard switch (band 0) cuts at the radius; a negative radius is off: everything is baked
        Assert.Equal(0f, PackTreeMath.FarShare(69.9f, 70f, 0f));
        Assert.Equal(1f, PackTreeMath.FarShare(70f, 70f, 0f));
        Assert.Equal(1f, PackTreeMath.FarShare(0f, -1f, 0f));
        // the pack tree is drawn exactly when its base is inside the radius (its share is above 0)
        for (var i = 0; i < 240; i++) Assert.Equal(i * 0.5f < 70f, 1f - PackTreeMath.FarShare(i * 0.5f, 70f, 14f) > 0f);
        // the dither: a pixel with noise n shows the pack tree when n < 1 - far and the baked one otherwise: the two cover every pixel once, in the shares 1 - far and far
        for (var d = 40f; d < 90f; d += 1.3f)
        {
            var far = PackTreeMath.FarShare(d, 70f, 14f);
            var packPixels = Enumerable.Range(0, 1000).Count(k => k / 1000.0 < 1.0 - far);
            Assert.InRange(packPixels, (1 - far) * 1000 - 1, (1 - far) * 1000 + 1);
        }
    }

    [Fact]
    public void CutRadiusStopsAtTheNearestUnbuiltTile()
    {
        Assert.Equal(70f, PackTreeMath.CutRadius(70f, Array.Empty<float>()));
        Assert.Equal(31.5f, PackTreeMath.CutRadius(70f, new[] { 55f, 31.5f, 60f }));
        Assert.Equal(0f, PackTreeMath.CutRadius(70f, new[] { 0f }));
        Assert.Equal(70f, PackTreeMath.CutRadius(70f, new[] { 90f })); // never past the near radius
    }

    [Theory]
    [InlineData("""{"model":{"base_uv2":true},"trees":[]}""", true)]
    [InlineData("""{"model":{"base_uv2":false},"trees":[]}""", false)]
    [InlineData("""{"model":{"trunk_width_m":0.35},"trees":[]}""", false)]
    [InlineData("""{"trees":[]}""", false)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    public void BaseUv2FlagIsReadFromTheModelBlock(string json, bool expected) => Assert.Equal(expected, PackTreeSettings.ReadBaseUv2(json));

    // ---- the tile mesh ----

    // Two models: A one surface (material 0, a triangle), B two surfaces (material 0 a triangle, material 1 a quad).
    static PackModelMesh[] Models()
    {
        PackSurface Tri(int mat) => new(mat,
            [new V3(0, 0, 0), new V3(1, 0, 0), new V3(0, 2, 0)], [V3.UnitZ, V3.UnitZ, V3.UnitZ], [new V2(0, 0), new V2(1, 0), new V2(0, 1)], [0, 1, 2]);
        PackSurface Quad(int mat) => new(mat,
            [new V3(0, 0, 0), new V3(1, 0, 0), new V3(1, 1, 0), new V3(0, 1, 0)], [V3.UnitZ, V3.UnitZ, V3.UnitZ, V3.UnitZ], [new V2(0, 0), new V2(1, 0), new V2(1, 1), new V2(0, 1)], [0, 1, 2, 0, 2, 3]);
        return
        [
            new PackModelMesh { Name = "A", HeightM = 2, Surfaces = [Tri(0)] },
            new PackModelMesh { Name = "B", HeightM = 2, Surfaces = [Tri(0), Quad(1)] },
        ];
    }

    [Fact]
    public void TileCostsOneBucketPerMaterialWhateverTheModels()
    {
        var models = Models();
        var trees = new[]
        {
            new PackPlacement(new V3(10, 5, -20), 0f, 1f, 0), new PackPlacement(new V3(12, 5, -22), 1f, 2f, 0), new PackPlacement(new V3(14, 6, -20), 2f, 1f, 0),
        };
        var onlyA = PackTileMesher.Build(models, trees, V3.Zero);
        Assert.Single(onlyA); // three trees of one single-material model: one draw
        Assert.Equal(0, onlyA[0].Material);
        Assert.Equal(9, onlyA[0].V.Count);
        Assert.Equal(9, onlyA[0].I.Count);

        var mixed = PackTileMesher.Build(models, trees.Append(new PackPlacement(new V3(16, 5, -20), 0f, 1f, 1)).ToArray(), V3.Zero);
        Assert.Equal(new[] { 0, 1 }, mixed.Select(b => b.Material).ToArray()); // a model with two materials adds one more bucket, not one per tree
        Assert.Equal(12, mixed[0].V.Count); // 3 + 1 triangles
        Assert.Equal(4, mixed[1].V.Count);
        Assert.Equal(6, mixed[1].I.Count);
        Assert.All(mixed, b => { Assert.Equal(b.V.Count, b.N.Count); Assert.Equal(b.V.Count, b.Uv.Count); Assert.Equal(b.V.Count, b.Uv2.Count); Assert.All(b.I, i => Assert.InRange(i, 0, b.V.Count - 1)); });
        Assert.Empty(PackTileMesher.Build(models, Array.Empty<PackPlacement>(), V3.Zero));
    }

    [Fact]
    public void TileVerticesAreScaledTurnedMovedAndCarryTheirTreesBase()
    {
        var models = Models();
        var centre = new V3(48, 0, -24);
        var tree = new PackPlacement(new V3(50, 7, -20), MathF.PI / 2, 3f, 0);
        var b = PackTileMesher.Build(models, [tree], centre).Single();
        // vertex (1, 0, 0) turned a quarter about +Y goes to (0, 0, -1) (Godot's Basis.Rotated(Up, pi/2)), times 3, plus the base relative to the centre (2, 7, 4)
        AssertNear(new V3(2, 7, 1), b.V[1]);
        AssertNear(new V3(2, 13, 4), b.V[2]); // (0, 2, 0): up stays up
        AssertNear(new V3(1, 0, 0), b.N[0]); // the normal (0, 0, 1) turns to (1, 0, 0)
        Assert.All(b.Uv2, uv => Assert.Equal(new V2(2, 4), uv)); // the base in the tile's frame, the same for every vertex of the tree
        Assert.Equal(new V2(1, 0), b.Uv[1]);
        // a second tree: its own base, indices offset by the first one's vertices
        var other = new PackPlacement(new V3(40, 7, -30), 0f, 1f, 0);
        var two = PackTileMesher.Build(models, [tree, other], centre).Single();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, two.I.ToArray());
        Assert.Equal(new V2(-8, -6), two.Uv2[3]);
        // deterministic: the same input gives the same mesh
        Assert.Equal(two.V, PackTileMesher.Build(models, [tree, other], centre).Single().V);
    }

    static void AssertNear(V3 expected, V3 actual) => Assert.True((expected - actual).Length() < 1e-4f, $"{expected} != {actual}");
}
