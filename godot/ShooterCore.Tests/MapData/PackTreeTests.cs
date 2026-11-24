using Drycastle.MapLoader.Trees;
using Xunit;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Tests.MapData;

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
        Assert.Contains(cfg.For(false), e => e.Bulk);
        Assert.All(all.Where(e => e.Name.StartsWith("SM_Tree_PolyPine") || e.Name.StartsWith("SM_Tree_Round") || e.Name == "SM_Tree_TallRound_01" || e.Name.StartsWith("SM_Tree_0")), e => Assert.True(e.Bulk, e.Name));
        Assert.All(all.Where(e => e.Name.StartsWith("SM_Tree_Birch") || e.Name.StartsWith("SM_Tree_Pine_")), e => Assert.False(e.Bulk, e.Name));
        Assert.InRange(cfg.BulkShare, 0.5f, 1f);
    }

    // ---- stands and edge trees ----

    static PackTreeEntry E(string name, float weight = 1f, bool bulk = false, string pack = "nature") => new() { Name = name, Weight = weight, Bulk = bulk, Pack = pack };

    // ids by name: the name's last digit is the id and the height is 5 m, except names that are not in the "pack"
    static PackCandidate? Resolve(PackTreeEntry e) => e.Name.StartsWith("gone") ? null : new PackCandidate(int.Parse(e.Name[^1..]), 5f, e.Weight, e.Bulk);

    static PackTreeConfig StandConfig() => new()
    {
        BulkShare = 0.9f, EdgeShare = 0.5f,
        Shapes = new() { ["cone"] = [E("c1", 1, true)], ["box"] = [E("b2", 1, true)] },
        Stands = new()
        {
            ["pine"] = new() { Any = [E("p3", 1, true), E("gone4")], BulkShare = 0.6f },
            ["swamp"] = new() { Cone = [E("s5")], Edge = false },
            ["edge"] = new() { Any = [E("e6", 1, true)] },
            ["bare"] = new() { Any = [E("gone7")] }, // nothing of it loads
        },
    };

    [Fact]
    public void StandsPickTheirOwnModelsAndFallBackToTheShapeLists()
    {
        var sets = PackModelSets.Build(StandConfig(), Resolve);
        Assert.Equal([1], sets.Default.Cone.Select(c => c.ModelId));
        Assert.Equal([2], sets.Default.Box.Select(c => c.ModelId));
        var pine = sets.For(new PackTreeTag("pine", false), 0.5f, 0.9);
        Assert.Equal([3], pine.Cone.Select(c => c.ModelId)); // a model that cannot load is skipped
        Assert.Equal([3], pine.Box.Select(c => c.ModelId)); // `any` serves both shapes
        Assert.Equal(0.6f, pine.BulkShare); // its own share
        Assert.Equal(0.9f, sets.Default.BulkShare);
        // a stand with one shape's list only: the other shape takes it too; no list that loads: today's shape lists
        Assert.Equal([5], sets.For(new PackTreeTag("swamp", false), 0.5f, 0.9).Box.Select(c => c.ModelId));
        Assert.Same(sets.Default.Cone, sets.For(new PackTreeTag("bare", false), 0.5f, 0.9).Cone);
        // unknown, absent ("mixed") and null stands are the default
        Assert.Same(sets.Default, sets.For(new PackTreeTag("mixed", false), 0.5f, 0.9));
        Assert.Same(sets.Default, sets.For(new PackTreeTag("moor", false), 0.5f, 0.9));
        Assert.Same(sets.Default, sets.For(new PackTreeTag(null!, false), 0.5f, 0.9));
        Assert.Same(sets.Default, sets.For(PackTreeTag.Plain, 0.5f, 0.9));
    }

    [Fact]
    public void EdgeTreesTakeTheEdgeModelsByShareUnlessTheirStandKeepsThem()
    {
        var sets = PackModelSets.Build(StandConfig(), Resolve);
        var edge = new PackTreeTag("pine", true);
        Assert.Equal([6], sets.For(edge, 0.5f, 0.49).Cone.Select(c => c.ModelId));
        Assert.Equal([3], sets.For(edge, 0.5f, 0.51).Cone.Select(c => c.ModelId)); // the rest keep their stand's
        Assert.Equal([3], sets.For(new PackTreeTag("pine", false), 0.5f, 0.0).Cone.Select(c => c.ModelId)); // not at the edge
        Assert.Equal([1], sets.For(new PackTreeTag("mixed", false), 0.5f, 0.0).Cone.Select(c => c.ModelId));
        Assert.Equal([6], sets.For(new PackTreeTag("mixed", true), 1f, 0.99).Cone.Select(c => c.ModelId)); // share 1: all of them
        Assert.Equal([5], sets.For(new PackTreeTag("swamp", true), 1f, 0.0).Cone.Select(c => c.ModelId)); // edge: false on the swamp
        // no edge stand in the config: nothing to take
        var cfg = StandConfig(); cfg.Stands.Remove("edge");
        Assert.Equal([3], PackModelSets.Build(cfg, Resolve).For(edge, 1f, 0.0).Cone.Select(c => c.ModelId));
        // the edge number is a position hash of its own, spread over [0, 1) and not the model roll
        var seen = Enumerable.Range(0, 2000).Select(i => PackTreeMath.EdgeRoll(i * 2.3f, i * 0.7f - 40f)).ToList();
        Assert.All(seen, r => Assert.InRange(r, 0.0, 1.0));
        Assert.InRange(seen.Count(r => r < 0.5), 900, 1100);
        Assert.NotEqual(PackTreeMath.Roll(12.5f, 3.5f), PackTreeMath.EdgeRoll(12.5f, 3.5f));
    }

    [Fact]
    public void PlacementUsesTheStandsModelsAndItsBulkShare()
    {
        var cfg = StandConfig();
        var sets = PackModelSets.Build(cfg, Resolve);
        var t = new TreeInstance(201.4f, -1677.2f, 150f, 1.3f, 0.7f, true, 0, 0);
        Assert.Equal(1, PackTreeMath.Place(t, Model, PackTreeTag.Plain, sets, cfg)!.Value.Model);
        Assert.Equal(3, PackTreeMath.Place(t, Model, new PackTreeTag("pine", false), sets, cfg)!.Value.Model);
        Assert.Equal(PackTreeMath.Place(t, Model, new PackTreeTag("pine", true), sets, cfg), PackTreeMath.Place(t, Model, new PackTreeTag("pine", true), sets, cfg));
        // the old entry point is the default set
        var one = new[] { C(1, 5f) };
        Assert.Equal(PackTreeMath.Place(t, Model, one, one, cfg), PackTreeMath.Place(t, Model, PackTreeTag.Plain, sets, cfg));
    }

    [Fact]
    public void TagsAreReadInFileOrderWithDefaultsForWhatIsMissing()
    {
        const string json = """{"trees":[{"x":1},{"x":2,"stand":"swamp","edge":true},{"x":3,"stand":"","edge":0},{"x":4,"stand":"pine","edge":1},{"x":5,"stand":7}]}""";
        var tags = PackTreeTags.Parse(json, 5);
        Assert.Equal([PackTreeTag.Plain, new("swamp", true), PackTreeTag.Plain, new("pine", true), PackTreeTag.Plain], tags);
        // fewer records than trees: padded; more: cut; unreadable or empty: all plain
        Assert.Equal(7, PackTreeTags.Parse(json, 7).Length);
        Assert.Equal(PackTreeTag.Plain, PackTreeTags.Parse(json, 7)[6]);
        Assert.Equal(2, PackTreeTags.Parse(json, 2).Length);
        Assert.All(PackTreeTags.Parse("not json", 3), t => Assert.Equal(PackTreeTag.Plain, t));
        Assert.All(PackTreeTags.Parse("""{"model":{}}""", 3), t => Assert.Equal(PackTreeTag.Plain, t));
        Assert.Empty(PackTreeTags.Parse("", 0));
    }

    [Fact]
    public void ConfigRejectsBadStandsAndEdgeShare()
    {
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"edge_share":1.5}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"stands":{"pine":{"bulk_share":2}}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"stands":{"pine":{"any":[{"name":"a","weight":-1}]}}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"stands":{"pine":{"any":[{"name":"a","pack":""}]}}}"""));
        var ok = PackTreeConfig.Parse("""{"version":1,"stands":{"swamp":{"edge":false,"cone":[{"pack":"swamp","name":"a"}]}}}""");
        Assert.Equal("swamp", ok.Stands["swamp"].Cone.Single().Pack);
        Assert.Equal("nature", new PackTreeEntry().Pack);
        Assert.Contains("swamp", ok.Packs());
        Assert.Contains("nature", ok.Packs());
    }

    [Fact]
    public void ShippedStandsCoverEveryStandAndEveryNumberTheyAddIsRead()
    {
        var c = Shipped();
        // the number settings of the stands are read (an unread one stays at the class default or null, see DataFile's snake-case policy)
        Assert.True(c.EdgeShare > 0 && c.EdgeShare != new PackTreeConfig().EdgeShare);
        foreach (var stand in new[] { "pine", "birch", "thicket", "swamp", "edge" })
        {
            Assert.True(c.Stands.TryGetValue(stand, out var s), stand);
            Assert.NotNull(s!.BulkShare);
            Assert.True(s.BulkShare > 0 && s.BulkShare != c.BulkShare, stand + " bulk_share");
            Assert.NotEmpty(s.For(true)); Assert.NotEmpty(s.For(false));
            Assert.All(s.For(true).Concat(s.For(false)), e => Assert.True(e.Weight > 0, e.Name));
        }
        Assert.False(c.Stands["swamp"].Edge); Assert.False(c.Stands["thicket"].Edge); Assert.True(c.Stands["pine"].Edge);
        // pine: the pine families; birch: birches and slender round trees; swamp: dead trees, willows, bare swamp trees, and one swamp-pack LOD
        Assert.All(c.Stands["pine"].Any, e => Assert.Matches("^SM_Tree_(Poly)?Pine", e.Name));
        Assert.All(c.Stands["swamp"].Any.Where(e => e.Pack == "swamp"), e => Assert.Matches("_LOD[12]$", e.Name)); // 28-30k triangles at full detail: only an LOD is affordable
        Assert.Contains(c.Stands["thicket"].Any, e => e.Name.Contains("Small") || e.Name.StartsWith("SM_Plant_Bush"));
        Assert.All(c.Stands["edge"].Any, e => Assert.Matches("^SM_Tree_(Round|Generic|0)", e.Name));
    }

    [Fact]
    public void ShippedJsonHasOnlyKeysTheStandsConfigReads()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "pack_trees.json")));
        foreach (var stand in doc.RootElement.GetProperty("stands").EnumerateObject())
        {
            Assert.All(stand.Value.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "bulk_share", "edge", "cone", "box", "any" }));
            foreach (var list in stand.Value.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Array))
                foreach (var e in list.Value.EnumerateArray())
                    Assert.All(e.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "pack", "name", "weight", "bulk" }));
        }
    }

    // ---- the cut ----

    [Fact]
    public void FarShareRampsAndItsEndsDecideTheSwap()
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
        // far 0 is always the pack tree and far 1 always the baked one, whatever the tree's threshold
        for (var i = 0; i < 500; i++)
        {
            float x = i * 7.31f - 900f, z = i * 3.77f + 40f;
            Assert.InRange(PackTreeMath.Threshold(x, z), 0f, 0.99999f);
            Assert.False(PackTreeMath.IsBaked(0f, x, z));
            Assert.True(PackTreeMath.IsBaked(1f, x, z));
        }
    }

    [Fact]
    public void TreesSwapWholeOneByOneAcrossTheBand()
    {
        // 4000 trees on a grid: each is baked or pack, never both (one predicate), the baked share grows with the distance, and the share baked at far f is about f
        var pts = Enumerable.Range(0, 4000).Select(i => (X: (i % 80) * 2.37f + 0.13f, Z: (i / 80) * 2.91f + 0.07f)).ToArray();
        foreach (var far in new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
        {
            var baked = pts.Count(p => PackTreeMath.IsBaked(far, p.X, p.Z)) / (double)pts.Length;
            Assert.InRange(baked, far - 0.05, far + 0.05);
        }
        // a tree that is baked at far f stays baked at every larger far: it swaps once, at its own distance
        foreach (var p in pts.Take(300))
        {
            var swapped = false;
            for (var f = 0f; f <= 1f; f += 0.02f)
            {
                var b = PackTreeMath.IsBaked(f, p.X, p.Z);
                Assert.False(swapped && !b);
                swapped |= b;
            }
            Assert.True(swapped);
        }
    }

    [Fact]
    public void ThresholdIsStableAgainstFloatNoiseInTheBase()
    {
        // the baked mesh and the pack tile compute the same base through different matrices: rounding of 1e-4 m must not move a (centimetre-placed) tree to another cell
        for (var i = 0; i < 2000; i++)
        {
            float x = i * 0.37f - 300f, z = i * 0.53f + 12f;
            x = MathF.Round(x, 2); z = MathF.Round(z, 2);
            Assert.Equal(PackTreeMath.Threshold(x, z), PackTreeMath.Threshold(x + 1e-4f, z - 1e-4f));
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
