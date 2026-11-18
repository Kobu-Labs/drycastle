using System.Numerics;
using Drycastle.MapLoader;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Trees drawn as pack models are solid where the model's wood is, limb by limb, and their leaves stop nothing (#375): the leaning limb solid, the
/// solids of a tree from tree_limbs.json, and the choice of the model without the pack.
/// </summary>
public class TreeLimbsTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static IReadOnlyList<LimbModel> ShippedLimbs() => TreeLimbsFile.Parse(File.ReadAllText(Path.Combine(DataDir, "tree_limbs.json")));
    static PackTreeConfig ShippedConfig() => PackTreeConfig.Parse(File.ReadAllText(Path.Combine(DataDir, "pack_trees.json")));

    static readonly ForestModel Forest = new(0.3f, 4.4f, 0.55f, [new(2.4f, 1.25f, 5.4f), new(4.2f, 0.95f, 7.6f), new(6.2f, 0.62f, 9.5f)], [new(2.4f, 1.3f, 5.6f), new(5.6f, 0.85f, 9.2f)]);
    static readonly TreeModel Model = new(0.35f, 1.8f, 2.6f, Forest);

    // a trunk 10 m tall in its own metres: straight for 4 m, then leaning 2 m along u and 1 m along v
    static readonly LimbModel Leaning = new("nature", "leaning", 10f,
    [
        new Limb(0f, 4f, 0.5f, 0.4f, 0f, 0f, 0f, 0f),
        new Limb(4f, 8f, 0.4f, 0.2f, 0f, 0f, 2f, 1f),
    ]);

    [Fact]
    public void ALimbLeans()
    {
        var limb = new SolidPart(PartRole.Trunk, PartShape.Limb, 10, 20, 0, 4, 0.5f, 0.5f, 0, 0.25f, TopU: 3f, TopV: 0f);
        Assert.Equal(8, limb.Points().Length);
        Assert.True(limb.Contains(new Vector3(10, 20, 0.1f)));            // over the lower ring
        Assert.True(limb.Contains(new Vector3(13, 20, 3.9f)));            // under the upper ring
        Assert.True(limb.Contains(new Vector3(11.5f, 20, 2f)));           // on the leaning axis
        Assert.False(limb.Contains(new Vector3(10, 20, 3.9f)));           // straight above the foot: the limb has leant away
        Assert.False(limb.Contains(new Vector3(13, 20, 0.1f)));           // straight under the top
        Assert.Equal(CollisionLayers.World, limb.Layer);
        Assert.Equal("wood", limb.Group);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.9f)]
    [InlineData(-2.3f)]
    public void ALimbIsConvexWhateverItsYaw(float yaw)
    {
        var limb = new SolidPart(PartRole.Trunk, PartShape.Limb, 3, -7, 1, 5, 0.6f, 0.6f, yaw, 0.3f, TopU: 1.5f, TopV: -2f);
        var pts = limb.Points();
        foreach (var (n, d) in limb.Planes())
        {
            var on = 0;
            foreach (var p in pts)
            {
                var dist = (Vector3.Dot(n, p) - d) / n.Length();
                Assert.True(dist < 1e-3f, $"corner {p} is {dist} m outside a face");
                if (MathF.Abs(dist) < 1e-3f) on++;
            }
            Assert.Equal(4, on); // every face is a flat quad through four corners
        }
        // the top ring is where TopU, TopV say, in the yawed frame
        var (s, c) = MathF.SinCos(yaw);
        var top = (pts[4] + pts[5] + pts[6] + pts[7]) / 4;
        Assert.Equal(3 + c * 1.5f - s * -2f, top.X, 3);
        Assert.Equal(-7 + s * 1.5f + c * -2f, top.Y, 3);
        Assert.Equal(5f, top.Z, 3);
    }

    [Fact]
    public void ARayStopsOnALimbWhereItIs()
    {
        var limb = new SolidPart(PartRole.Trunk, PartShape.Limb, 0, 0, 0, 4, 0.5f, 0.5f, 0, 0.5f, TopU: 4f, TopV: 0f);
        // at mid height the limb is centred on u = 2: a ray along v through u = 2 hits, one through u = 0 passes
        Assert.NotNull(limb.Ray(new Vector3(2, -10, 2), new Vector3(0, 1, 0), 20));
        Assert.Null(limb.Ray(new Vector3(0, -10, 2), new Vector3(0, 1, 0), 20));
        Assert.Equal(9.5f, limb.Ray(new Vector3(2, -10, 2), new Vector3(0, 1, 0), 20)!.Value, 3);
    }

    [Fact]
    public void ATreeWithAModelIsItsLimbsAndNothingElse()
    {
        var plain = new TreeInstance(100, 200, 50, 1f, 0, false, 0, 0, Forest: true);
        Assert.Contains(TreeColliders.Parts(plain, Model), p => p.Role == PartRole.Crown);

        var model = Model with { Limbs = [Leaning] };
        var tree = plain with { Model = 0 };
        var parts = TreeColliders.Parts(tree, model);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => { Assert.Equal(PartRole.Trunk, p.Role); Assert.Equal(PartShape.Limb, p.Shape); });

        // scaled to the code-built height (9.2 m for a forest broadleaf of scale 1), at the nearest scale step
        var k = TreeLimbs.Scale(TreeColliders.CodeHeight(tree, model), Leaning.HeightM);
        Assert.InRange(k, 0.92f * 0.99f, 0.92f * 1.01f);
        Assert.Equal(50 - TreeLimbs.FootM, parts[0].Z0, 3);   // the foot starts below the ground
        Assert.Equal(50 + 4 * k, parts[0].Z1, 3);
        Assert.Equal(50 + 4 * k, parts[1].Z0, 3);             // the next limb starts where the first ends
        Assert.Equal(2 * k, parts[1].TopU, 3);
        Assert.Equal(1 * k, parts[1].TopV, 3);

        // the leaves stop nothing: where the code-built crown was there is no solid, the wood is where the model's is
        Assert.Null(TreeColliders.GroupAt([tree], model, new Vector3(101.2f, 200, 53)));
        Assert.Equal("wood", TreeColliders.GroupAt([tree], model, new Vector3(100, 200, 51)));
        Assert.Equal("wood", TreeColliders.GroupAt([tree], model, new Vector3(100 + 2 * k, 200 + 1 * k, 50 + 7.9f * k)));
        Assert.Equal("wood", new TreeColliders.Index([tree], model).GroupAt(new Vector3(100 + 2 * k, 200 + 1 * k, 50 + 7.9f * k)));

        // a model index the table does not have: the code-built tree
        Assert.Contains(TreeColliders.Parts(plain with { Model = 5 }, model), p => p.Role == PartRole.Crown);
    }

    [Fact]
    public void TheLimbsTurnWithTheTree()
    {
        var model = Model with { Limbs = [Leaning] };
        var tree = new TreeInstance(0, 0, 0, 1f, MathF.PI / 2, false, 0, 0, Forest: true, Model: 0);
        var k = TreeLimbs.Scale(TreeColliders.CodeHeight(tree, model), Leaning.HeightM);
        var top = TreeColliders.Parts(tree, model)[1].Points()[4..].Aggregate(Vector3.Zero, (a, p) => a + p) / 4;
        // a quarter turn takes u onto north and v onto west
        Assert.Equal(-1 * k, top.X, 3);
        Assert.Equal(2 * k, top.Y, 3);
    }

    [Fact]
    public void AModelWithoutLimbsIsNotSolid()
    {
        var model = Model with { Limbs = [new LimbModel("nature", "bush", 2f, [])] };
        Assert.Empty(TreeColliders.Parts(new TreeInstance(0, 0, 0, 1f, 0, false, 0, 0, Forest: true, Model: 0), model));
    }

    [Fact]
    public void TreesOfNearlyOneSizeShareTheirShapes()
    {
        var model = Model with { Limbs = [Leaning] };
        var a = TreeColliders.Parts(new TreeInstance(0, 0, 0, 1.000f, 0.3f, false, 0, 0, Forest: true, Model: 0), model);
        var b = TreeColliders.Parts(new TreeInstance(50, 9, 3, 1.002f, 2.1f, false, 0, 0, Forest: true, Model: 0), model);
        var c = TreeColliders.Parts(new TreeInstance(0, 0, 0, 1.5f, 0.3f, false, 0, 0, Forest: true, Model: 0), model);
        Assert.Equal(SolidShapeKeys.Of(a[1]), SolidShapeKeys.Of(b[1]));
        Assert.NotEqual(SolidShapeKeys.Of(a[1]), SolidShapeKeys.Of(c[1]));
        Assert.NotEqual(SolidShapeKeys.Of(a[0]), SolidShapeKeys.Of(a[1]));
    }

    [Fact]
    public void TheShippedTableHasEveryTreeModelOfTheConfig()
    {
        var limbs = ShippedLimbs();
        var have = limbs.Select(l => (l.Pack, l.Name)).ToHashSet();
        var cfg = ShippedConfig();
        var named = cfg.Shapes.Values.SelectMany(l => l)
            .Concat(cfg.Stands.Values.SelectMany(s => s.Cone.Concat(s.Box).Concat(s.Any)))
            .Where(e => e.Weight > 0).Select(e => (e.Pack, e.Name)).Distinct().ToList();
        Assert.NotEmpty(named);
        // a model missing here would be drawn by a client and not be solid: run scripts/tree_collider_audit.py --write
        Assert.Empty(named.Where(n => !have.Contains(n)));
        Assert.All(limbs, l => Assert.InRange(l.HeightM, 0.5f, 40f));
        Assert.Contains(limbs, l => l.Limbs.Count > 2); // forks and leaning trunks are followed
        foreach (var l in limbs.Where(l => l.Limbs.Count > 0))
        {
            Assert.Contains(l.Limbs, x => x.Z0 == 0); // a foot
            Assert.All(l.Limbs, x => Assert.InRange(x.Z1, 0f, l.HeightM));
        }
    }

    [Fact]
    public void TheModelIsChosenAsTheClientDrawsIt()
    {
        var limbs = ShippedLimbs();
        var cfg = ShippedConfig();
        var json = "{\"model\":{\"base_uv2\":true},\"trees\":[" + string.Join(",", Enumerable.Range(0, 400).Select(i =>
            $"{{\"x\":{i * 7.3f:0.0},\"y\":{i * -3.1f:0.0},\"z\":{i % 9},\"scale\":{0.5f + i % 30 * 0.1f:0.000},\"rot\":{i % 6},\"shape\":\"{(i % 2 == 0 ? "cone" : "box")}\"," +
            $"\"forest\":1,\"stand\":\"{new[] { "pine", "birch", "mixed", "thicket", "swamp" }[i % 5]}\"{(i % 3 == 0 ? ",\"edge\":true" : "")}}}")) + "]}";
        var trees = TreeColliders.Parse(json, out _);
        var before = trees.ToList();
        var model = PackTreeLimbs.Dress(trees, Model, json, cfg, limbs);
        Assert.Same(limbs, model.Limbs);

        // the client's choice: the same lists, resolved from the loaded pack (here the same names and heights)
        var ids = limbs.Select((l, i) => (l, i)).ToDictionary(x => (x.l.Pack, x.l.Name), x => x.i);
        var sets = PackModelSets.Build(cfg, e => ids.TryGetValue((e.Pack, e.Name), out var i) ? new PackCandidate(i, limbs[i].HeightM, e.Weight, e.Bulk) : null);
        var tags = PackTreeTags.Parse(json, trees.Count);
        for (var i = 0; i < trees.Count; i++)
        {
            Assert.Equal(before[i] with { Model = trees[i].Model }, trees[i]);
            Assert.InRange(trees[i].Model, 0, limbs.Count - 1);
            var drawn = PackTreeMath.Place(before[i], Model, tags[i], sets, cfg)!.Value;
            Assert.Equal(drawn.Model, trees[i].Model);
            // the pack model is scaled to the code-built height, with or without the limbs: the solids must not change what they are measured from
            Assert.Equal(PackTreeMath.CodeHeight(before[i], Model), PackTreeMath.CodeHeight(trees[i], model));
            Assert.InRange(TreeLimbs.Scale(PackTreeMath.CodeHeight(trees[i], model), limbs[trees[i].Model].HeightM) / drawn.Scale, 0.994f, 1.006f);
        }
        Assert.True(trees.Select(t => t.Model).Distinct().Count() > 8);
    }

    [Fact]
    public void ABadTableIsRefused()
    {
        Assert.Throws<DataFileException>(() => TreeLimbsFile.Parse("{\"models\":[]}"));
        Assert.Throws<DataFileException>(() => TreeLimbsFile.Parse("{\"version\":1,\"models\":[{\"name\":\"a\",\"height_m\":0,\"limbs\":[]}]}"));
        Assert.Throws<DataFileException>(() => TreeLimbsFile.Parse("{\"version\":1,\"models\":[{\"name\":\"a\",\"height_m\":5,\"limbs\":[[0,1,0.2]]}]}"));
        Assert.Throws<DataFileException>(() => TreeLimbsFile.Parse("{\"version\":1,\"models\":[{\"name\":\"a\",\"height_m\":5,\"limbs\":[[2,1,0.2,0.2,0,0,0,0]]}]}"));
        Assert.Single(TreeLimbsFile.Parse("{\"version\":1,\"models\":[{\"name\":\"a\",\"height_m\":5,\"limbs\":[[0,1,0.2,0.2,0,0,0,0]]}]}"));
    }
}
