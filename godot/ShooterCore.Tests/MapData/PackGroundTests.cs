using Drycastle.MapLoader;
using Xunit;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Tests.MapData;

public class PackGroundTests
{
    static PackTreeConfig Shipped() => PackTreeConfig.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "pack_trees.json")));

    [Fact]
    public void ShippedConfigMapsEveryGroundKindAndHasUndergrowth()
    {
        var c = Shipped();
        foreach (var kind in new[] { "log", "stump", "boulder", "rootplate" })
        {
            Assert.True(c.GroundObjects.Kinds.TryGetValue(kind, out var k), kind);
            Assert.NotEmpty(k!.Models);
            Assert.All(k.Models, e => Assert.StartsWith("SM_", e.Name));
        }
        Assert.All(c.GroundObjects.Kinds["log"].Models.Where(e => !e.Lay), e => Assert.StartsWith("SM_Tree_Log_", e.Name)); // models that already lie
        Assert.All(c.GroundObjects.Kinds["log"].Models.Where(e => e.Lay), e => Assert.Contains("Dead", e.Name)); // dead trees laid down
        Assert.All(c.GroundObjects.Kinds["boulder"].Models, e => Assert.StartsWith("SM_Rock_", e.Name));
        Assert.Equal(new[] { 5, 9 }, c.Undergrowth.Mixes.Select(m => m.Mix)); // forest and swamp
        Assert.All(c.Undergrowth.Mixes, m => { Assert.NotEmpty(m.Kinds); Assert.All(m.Kinds, k => Assert.NotEmpty(k.Models)); });
        Assert.Equal(60000, c.Undergrowth.TrisPerTile);
    }

    [Fact]
    public void ShippedUndergrowthHasUnderstoryKindsThinAtTrunksAndASwampSet()
    {
        var u = Shipped().Undergrowth;
        var forest = u.Mixes.Single(m => m.Mix == 5).Kinds.ToDictionary(k => k.Name);
        // the old kinds stay, the understory kinds are new
        Assert.All(new[] { "fern", "bush", "mushroom", "twig", "rock", "grass" }, n => Assert.True(forest.ContainsKey(n), n));
        var sapling = forest["sapling"]; var shrub = forest["shrub"];
        Assert.Equal(0.4f, sapling.Per100M2);
        Assert.Equal(0.6f, shrub.Per100M2);
        Assert.InRange(sapling.ScaleMin, 0.25f, 0.25f); Assert.InRange(sapling.ScaleMax, 0.5f, 0.5f);
        Assert.All(new[] { sapling, shrub }, k => { Assert.True(k.Foot < 1f, k.Name + " must thin at trunks"); Assert.True(k.Clump > 0f, k.Name + " is clumped"); });
        Assert.All(sapling.Models, e => Assert.Matches("PolyPine|Round", e.Name));
        var swamp = u.Mixes.Single(m => m.Mix == 9);
        Assert.All(new[] { "reeds", "swamp_grass", "moss_mound", "root", "log" }, n => Assert.Contains(swamp.Kinds, k => k.Name == n));
        Assert.All(swamp.Kinds.Where(k => k.Name is "reeds" or "swamp_grass" or "moss_mound").SelectMany(k => k.Models), e => Assert.Equal("swamp", e.Pack));
        Assert.DoesNotContain(swamp.Kinds, k => k.Name.Contains("lil", StringComparison.OrdinalIgnoreCase)); // no lily pads: they need water under them
    }

    [Fact]
    public void EveryNumberTheShippedUndergrowthSetsIsReadNotLeftAtItsDefault()
    {
        // the snake-case policy once made per_100m2 per100_m2 and the file's value was never read (unmapped members are skipped): every number must differ from the class default
        var u = Shipped().Undergrowth;
        var d = new UndergrowthKindConfig();
        Assert.All(u.Mixes, m => Assert.InRange(m.Mix, 1, 15));
        foreach (var k in u.Mixes.SelectMany(m => m.Kinds))
        {
            Assert.True(k.Per100M2 > 0 && k.Per100M2 != d.Per100M2, k.Name + " per_100m2");
            Assert.True(k.ScaleMin > 0 && k.ScaleMax > 0 && (k.ScaleMin != d.ScaleMin || k.ScaleMax != d.ScaleMax), k.Name + " scale");
            Assert.True(k.Clump > 0 && k.Clump != d.Clump, k.Name + " clump");
            Assert.True(k.Foot > 0 && k.Foot != d.Foot, k.Name + " foot");
        }
    }

    [Fact]
    public void ShippedUndergrowthJsonHasOnlyKeysTheConfigReads()
    {
        // a key the class does not have is skipped without a word, so a typo is a setting that silently does nothing
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "pack_trees.json")));
        var u = doc.RootElement.GetProperty("undergrowth");
        Assert.All(u.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "enabled", "near_m", "fade_m", "ahead_m", "far_m", "far_fade_m", "grow", "sink_m", "tris_per_tile", "clump_m", "foot_m", "trunk_clear_m", "mixes" }));
        foreach (var mix in u.GetProperty("mixes").EnumerateArray())
        {
            Assert.All(mix.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "mix", "name", "kinds" }));
            foreach (var kind in mix.GetProperty("kinds").EnumerateArray())
            {
                Assert.All(kind.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "name", "per_100m2", "scale_min", "scale_max", "clump", "foot", "far", "models" }));
                foreach (var model in kind.GetProperty("models").EnumerateArray())
                    Assert.All(model.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "pack", "name", "weight" }));
            }
        }
    }

    [Fact]
    public void ConfigRejectsBadGroundAndUndergrowthNumbers()
    {
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"ground_objects":{"kinds":{"log":{"radius_m":0}}}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"undergrowth":{"tris_per_tile":-1}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"undergrowth":{"mixes":[{"mix":5,"kinds":[{"name":"a","clump":2}]}]}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"undergrowth":{"mixes":[{"mix":5},{"mix":5}]}}""")); // one set per mix
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"undergrowth":{"mixes":[{"mix":16}]}}""")); // the zone byte's nibble
    }

    // ---- ground_objects.json and the kind mapping ----

    const string File1 = """
        {"format_version":1,"model":{"base_uv2":true,"kinds":{"log":{"radius_m":0.4},"stump":{}}},
         "objects":[{"kind":"log","x":10,"y":20,"z":3,"rot":1.5,"scale":1.2,"len":4,"chunk":[0,0]},
                    {"kind":"stump","x":-5,"y":6,"z":1,"rot":0,"scale":1,"chunk":[0,0]},{"x":1},{"kind":"boulder"}]}
        """;

    [Fact]
    public void GroundObjectFileIsReadAndAMissingOrWrongFileIsNone()
    {
        var f = GroundObjectFile.Parse(File1)!;
        Assert.True(f.BaseUv2);
        Assert.Equal(2, f.Objects.Count); // the two malformed entries are skipped
        Assert.Equal(new PackGroundObject("log", 10, 20, 3, 1.5f, 1.2f, 4), f.Objects[0]);
        Assert.Equal(1f, f.Objects[1].Scale); // default
        Assert.Equal(0.4f, f.RadiusM["log"]);
        Assert.False(f.RadiusM.ContainsKey("stump"));
        Assert.Null(GroundObjectFile.Parse(""));
        Assert.Null(GroundObjectFile.Parse("not json"));
        Assert.Null(GroundObjectFile.Parse("""{"format_version":2,"objects":[]}"""));
        Assert.False(GroundObjectFile.Parse("""{"format_version":1,"model":{},"objects":[]}""")!.BaseUv2);
    }

    [Fact]
    public void KindMappingPicksByPositionHashAndWeightAndIsEmptySafe()
    {
        var list = new[] { new GroundCandidate(7, 1f), new GroundCandidate(9, 3f) };
        Assert.Equal(GroundObjectMath.Choose(list, 12.3f, -4.5f), GroundObjectMath.Choose(list, 12.3f, -4.5f));
        var counts = new int[2];
        for (var i = 0; i < 4000; i++) counts[GroundObjectMath.Choose(list, i * 1.7f, i * 0.3f - 99f)]++;
        Assert.InRange(counts[1], 2800, 3200); // three quarters
        Assert.Equal(-1, GroundObjectMath.Choose(Array.Empty<GroundCandidate>(), 1, 1));
    }

    // a model 0.6 x 0.5 across and 3 m long (Z), like SM_Tree_Log_01
    static PackModelMesh Log() => new()
    {
        Name = "log",
        HeightM = 0.5f,
        Surfaces = [new PackSurface(0, [new V3(-0.3f, -0.25f, 0), new V3(0.3f, 0.25f, 3f), new V3(0, 0, 1)], [V3.UnitY, V3.UnitY, V3.UnitY], [V2.Zero, V2.Zero, V2.Zero], [0, 1, 2])],
    };

    [Fact]
    public void LogIsScaledToTheCodeBuiltRadiusAndStretchedToItsLength()
    {
        var o = new PackGroundObject("log", 10, 20, 3, 0.5f, 1.5f, 4.5f);
        var p = GroundObjectMath.Place(o, Log(), 2, radiusM: 0.3f, yawDeg: 90f, stretchToLen: true);
        // radius 0.3 * 1.5 = 0.45 over the model's 0.3 (half of 0.6): scale 1.5; length 3 * 1.5 = 4.5 already: no stretch
        Assert.Equal(1.5f, p.Scale, 4);
        Assert.Equal(1f, p.Stretch, 4);
        // Godot axes; the model's Z runs 0..3, so the middle of its length (4.5 m after the scale) is moved onto the object's point, back along the heading
        var heading = new V3(MathF.Cos(0.5f), 0, -MathF.Sin(0.5f));
        AssertNear(new V3(10, 3, -20) - heading * 2.25f, p.Base);
        Assert.Equal(PackKind.Object, p.Kind);
        Assert.Equal(0.5f + MathF.PI / 2, p.Rot, 4);
        Assert.Equal(2, p.Model);
        // a longer log is stretched along the model's Z only: 6 m = 3 * 1.5 * 4/3
        var long6 = GroundObjectMath.Place(o with { Len = 6f }, Log(), 2, 0.3f, 0, true);
        Assert.Equal(1.5f, long6.Scale, 4);
        Assert.Equal(6f / 4.5f, long6.Stretch, 4);
        // the model's end reaches exactly len along its axis
        var b = PackTileMesher.Build([Log()], [long6 with { Model = 0, Rot = 0 }], V3.Zero).Single();
        Assert.Equal(6f, b.V.Max(v => v.Z) - b.V.Min(v => v.Z) - 0f, 3);
        // without a length (or for another kind) there is no stretch
        Assert.Equal(1f, GroundObjectMath.Place(o with { Len = 0 }, Log(), 0, 0.3f, 0, true).Stretch);
        Assert.Equal(1f, GroundObjectMath.Place(o, Log(), 0, 0.3f, 0, false).Stretch);
    }

    static void AssertNear(V3 expected, V3 actual) => Assert.True((expected - actual).Length() < 1e-4f, $"{expected} != {actual}");

    [Fact]
    public void RoundObjectsTakeTheWiderHorizontalSide()
    {
        // stump-like model 0.8 wide (X), 0.4 deep (Z), 0.8 tall: radius 0.4; code-built radius 0.35 at scale 2 = 0.7
        var m = new PackModelMesh
        {
            Name = "stump", HeightM = 0.8f,
            Surfaces = [new PackSurface(0, [new V3(-0.4f, 0, -0.2f), new V3(0.4f, 0.8f, 0.2f), new V3(0, 0, 0)], [V3.UnitY, V3.UnitY, V3.UnitY], [V2.Zero, V2.Zero, V2.Zero], [0, 1, 2])],
        };
        var p = GroundObjectMath.Place(new PackGroundObject("stump", 0, 0, 0, 0, 2f, 0), m, 0, 0.35f, 0, false);
        Assert.Equal(0.7f / 0.4f, p.Scale, 4);
    }

    // ---- undergrowth ----

    static UndergrowthConfig Cfg(int tris = 60000) => new() { ClumpM = 6f, FootM = 4f, TrunkClearM = 0.4f, TrisPerTile = tris };
    static UndergrowthKind[] Kinds() =>
    [
        new(0.04f, 0.8f, 1.2f, 0.5f, 2f, [10, 11], [1f, 1f], [100, 200]),
        new(0.02f, 1f, 1.5f, 0f, 1f, [12], [1f], [50]),
    ];
    static ForestMask All(float x0, float y0, float x1, float y1, float cell)
    {
        int gx0 = (int)MathF.Floor(x0 / cell), gy0 = (int)MathF.Floor(y0 / cell), w = (int)MathF.Ceiling(x1 / cell) - gx0, h = (int)MathF.Ceiling(y1 / cell) - gy0;
        return new ForestMask { Gx0 = gx0, Gy0 = gy0, W = w, H = h, Cells = Enumerable.Repeat(true, w * h).ToArray() };
    }
    static List<PackPlacement> Run(float x0, float y0, float x1, float y1, int budget, out int tris, TreeFoot[]? trees = null, ForestMask? mask = null, Func<float, float, float>? ground = null)
        => UndergrowthGen.Tile(Cfg(budget), Kinds(), x0, y0, x1, y1, 2f, mask ?? All(x0, y0, x1, y1, 2f), trees ?? [], ground ?? ((_, _) => 5f), budget, out tris);

    [Fact]
    public void ScatterIsDeterministicInsideTheTileOnTheGroundAndPlentiful()
    {
        var a = Run(96, -48, 144, 0, 60000, out var ta);
        var b = Run(96, -48, 144, 0, 60000, out var tb);
        Assert.Equal(a, b);
        Assert.Equal(ta, tb);
        // 2304 m2 at 0.06 per m2: about 140, clumping moves it, not by a factor
        Assert.InRange(a.Count, 70, 280);
        Assert.All(a, p => { Assert.InRange(p.Base.X, 96f, 144f); Assert.InRange(-p.Base.Z, -48f, 0f); Assert.Equal(5f, p.Base.Y); Assert.Contains(p.Model, new[] { 10, 11, 12 }); });
        Assert.Equal(a.Sum(p => p.Model switch { 10 => 100, 11 => 200, _ => 50 }), ta);
        Assert.Equal(a.Count, a.Select(p => p.Base).Distinct().Count());
    }

    [Fact]
    public void TilesAgreeWhereTheyMeetWhateverTheirBox()
    {
        // the same cells make the same pieces: two halves are the whole (hash of the global cell, not of the tile)
        var whole = Run(0, 0, 48, 48, 1 << 30, out _);
        var left = Run(0, 0, 24, 48, 1 << 30, out _);
        var right = Run(24, 0, 48, 48, 1 << 30, out _);
        Assert.Equal(whole.Count, left.Count + right.Count);
        Assert.Equal(whole.OrderBy(p => p.Base.X).ThenBy(p => p.Base.Z), left.Concat(right).OrderBy(p => p.Base.X).ThenBy(p => p.Base.Z));
    }

    [Fact]
    public void OnlyForestCellsOnlyWhereThereIsGroundAndNotInTrunks()
    {
        Assert.Empty(Run(0, 0, 48, 48, 1 << 30, out _, mask: new ForestMask { Gx0 = 0, Gy0 = 0, W = 24, H = 24, Cells = new bool[576] }));
        Assert.Empty(Run(0, 0, 48, 48, 1 << 30, out _, ground: (_, _) => float.NaN));
        var half = Run(0, 0, 48, 48, 1 << 30, out _, ground: (x, _) => x < 24 ? 1f : float.NaN);
        Assert.NotEmpty(half);
        Assert.All(half, p => Assert.InRange(p.Base.X, 0f, 24f));
        // a mask with only the cell (3, 4) (map 6..8 x 8..10)
        var one = new bool[576]; one[4 * 24 + 3] = true;
        var some = Run(0, 0, 48, 48, 1 << 30, out _, mask: new ForestMask { Gx0 = 0, Gy0 = 0, W = 24, H = 24, Cells = one });
        Assert.All(some, p => { Assert.InRange(p.Base.X, 6f, 8f); Assert.InRange(-p.Base.Z, 8f, 10f); });
        // trunks: a fat tree (scale 10 -> 4 m clear) in the middle of the tile is left alone
        var trees = new[] { new TreeFoot(24, 24, 10f) };
        var near = Run(0, 0, 48, 48, 1 << 30, out _, trees);
        Assert.DoesNotContain(near, p => (p.Base.X - 24) * (p.Base.X - 24) + (-p.Base.Z - 24) * (-p.Base.Z - 24) < 16f);
    }

    [Fact]
    public void DensityRisesAtTreeFeetAndFollowsTheClumpFormula()
    {
        Assert.Equal(1f, UndergrowthGen.FootFactor(4f, 4f, 3f));
        Assert.Equal(1f, UndergrowthGen.FootFactor(9f, 4f, 3f));
        Assert.Equal(3f, UndergrowthGen.FootFactor(0f, 4f, 3f), 4);
        Assert.InRange(UndergrowthGen.FootFactor(2f, 4f, 3f), 1.5f, 2.5f);
        Assert.Equal(1f, UndergrowthGen.ClumpFactor(0.5, 0.9f), 4);
        Assert.Equal(0f, UndergrowthGen.ClumpFactor(0.1, 1f)); // a gap
        Assert.Equal(3f, UndergrowthGen.ClumpFactor(1.0, 1f), 4);
        Assert.Equal(1f, UndergrowthGen.ClumpFactor(0.9, 0f)); // no clump: even
        // more pieces within 3 m of a tree than within 3 m of the same spots without it (foot 8, no clump)
        UndergrowthKind[] one = [new(0.02f, 1f, 1f, 0f, 8f, [10], [1f], [10])];
        var spots = Enumerable.Range(0, 16).Select(i => new TreeFoot(6 + 12 * (i % 4), 6 + 12 * (i / 4), 1f)).ToArray();
        List<PackPlacement> Grow(TreeFoot[] trees) => UndergrowthGen.Tile(Cfg(), one, 0, 0, 48, 48, 2f, All(0, 0, 48, 48, 2f), trees, (_, _) => 0f, 1 << 30, out _);
        int Around(IEnumerable<PackPlacement> l) => l.Count(p => spots.Any(t => (p.Base.X - t.X) * (p.Base.X - t.X) + (-p.Base.Z - t.Y) * (-p.Base.Z - t.Y) < 9f));
        Assert.True(Around(Grow(spots)) > 2 * Around(Grow([])));
    }

    [Fact]
    public void EachMixGrowsItsOwnKindsOnItsOwnCellsUnderOneBudget()
    {
        // forest on the left half of the tile (kind models 10, 11), swamp on the right (model 20): each stays on its cells
        bool[] Half(bool left) { var c = new bool[24 * 24]; for (var y = 0; y < 24; y++) for (var x = 0; x < 24; x++) c[y * 24 + x] = (x < 12) == left; return c; }
        var forest = new UndergrowthSet(new ForestMask { Gx0 = 0, Gy0 = 0, W = 24, H = 24, Cells = Half(true) }, Kinds(), 50000);
        UndergrowthKind[] swampKinds = [new(0.05f, 1f, 1f, 0f, 1f, [20], [1f], [30])];
        var swamp = new UndergrowthSet(new ForestMask { Gx0 = 0, Gy0 = 0, W = 24, H = 24, Cells = Half(false) }, swampKinds, 90000);
        var both = UndergrowthGen.Tile(Cfg(), [forest, swamp], 0, 0, 48, 48, 2f, [], (_, _) => 0f, 1 << 30, out var tris);
        Assert.All(both.Where(p => p.Model == 20), p => Assert.InRange(p.Base.X, 24f, 48f));
        Assert.All(both.Where(p => p.Model != 20), p => Assert.InRange(p.Base.X, 0f, 24f));
        Assert.Contains(both, p => p.Model == 20);
        Assert.Contains(both, p => p.Model != 20);
        // a mix with no cell in the tile (the swamp on a map that has none) adds nothing and changes nothing
        var none = new UndergrowthSet(new ForestMask { Gx0 = 0, Gy0 = 0, W = 24, H = 24, Cells = new bool[576] }, swampKinds, 90000);
        Assert.Equal(UndergrowthGen.Tile(Cfg(), [forest], 0, 0, 48, 48, 2f, [], (_, _) => 0f, 1 << 30, out _),
            UndergrowthGen.Tile(Cfg(), [forest, none], 0, 0, 48, 48, 2f, [], (_, _) => 0f, 1 << 30, out _));
        // one budget for both: thinned to it together
        var thin = UndergrowthGen.Tile(Cfg(), [forest, swamp], 0, 0, 48, 48, 2f, [], (_, _) => 0f, tris / 2, out var thinTris);
        Assert.InRange(thinTris, tris / 2 - 200, tris / 2);
        Assert.Contains(thin, p => p.Model == 20);
    }

    [Fact]
    public void OverTheTriangleBudgetTheScatterIsThinnedToIt()
    {
        var full = Run(0, 0, 48, 48, 1 << 30, out var fullTris);
        Assert.True(fullTris > 6000);
        var thin = Run(0, 0, 48, 48, 6000, out var thinTris);
        Assert.InRange(thinTris, 5800, 6000); // fills the budget to within the biggest piece
        Assert.True(thin.Count < full.Count);
        Assert.All(thin, p => Assert.Contains(p, full)); // a subset: nothing new appears
        Assert.Equal(thin, Run(0, 0, 48, 48, 6000, out _)); // and the same one every time
        // thinning is by position hash, not by kind: the kinds keep about their ratio
        var ratioFull = full.Count(p => p.Model == 12) / (double)full.Count;
        var ratioThin = thin.Count(p => p.Model == 12) / (double)thin.Count;
        Assert.InRange(Math.Abs(ratioFull - ratioThin), 0.0, 0.12);
        Assert.Empty(Run(0, 0, 48, 48, 0, out var none));
        Assert.Equal(0, none);
        // under the budget nothing is removed
        Assert.Equal(full.Count, Run(0, 0, 48, 48, fullTris, out _).Count);
    }
}
