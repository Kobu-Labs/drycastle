using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Forest trees (#287): the collision solids built from trees.json's model block match the drawn mesh. The fixture
/// fixtures/tree_parity.json is written by pipeline/tests/test_tree_parity.py from <c>propmeshes.tree_mesh</c>: per sample tree
/// (forest conifer, forest broadleaf, standalone of both kinds) the vertices of each drawn piece (trunk, crown tiers).
/// </summary>
public class ForestTreeColliderTests
{
    static JsonElement Golden()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tree_parity.json"));
        return JsonDocument.Parse(json).RootElement;
    }

    static TreeInstance Instance(JsonElement t)
    {
        float F(string k) => t.GetProperty(k).GetSingle();
        return new TreeInstance(F("x"), F("y"), F("z"), F("scale"), F("rot"), t.GetProperty("shape").GetString() == "cone", 0, 0, t.GetProperty("forest").GetBoolean());
    }

    /// <summary>The golden model block and trees written as a trees.json (forest flag as the export writes it: 1 or absent).</summary>
    static string TreesJson(JsonElement golden)
    {
        var trees = golden.GetProperty("trees").EnumerateArray().Select(g =>
        {
            var t = g.GetProperty("tree");
            var forest = t.GetProperty("forest").GetBoolean() ? ",\"forest\":1" : "";
            return $"{{\"x\":{t.GetProperty("x")},\"y\":{t.GetProperty("y")},\"z\":{t.GetProperty("z")},\"scale\":{t.GetProperty("scale")},\"rot\":{t.GetProperty("rot")},\"shape\":{t.GetProperty("shape").GetRawText()}{forest}}}";
        });
        return $"{{\"model\":{golden.GetProperty("model").GetRawText()},\"trees\":[{string.Join(",", trees)}]}}";
    }

    [Fact]
    public void SolidsMatchTheDrawnPiecesVertexForVertex()
    {
        var golden = Golden();
        var trees = TreeColliders.Parse(TreesJson(golden), out var model); // the way the game reads a map
        Assert.NotNull(model.Forest);
        var samples = golden.GetProperty("trees").EnumerateArray().ToList();
        Assert.Equal(samples.Count, trees.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var name = samples[i].GetProperty("name").GetString();
            Assert.Equal(Instance(samples[i].GetProperty("tree")).Forest, trees[i].Forest);
            var parts = TreeColliders.Parts(trees[i], model);
            var pieces = samples[i].GetProperty("points").EnumerateArray().ToList();
            Assert.True(parts.Count == pieces.Count, $"{name}: {parts.Count} solids, {pieces.Count} drawn pieces");
            for (var k = 0; k < parts.Count; k++)
            {
                var drawn = pieces[k].EnumerateArray().Select(p => new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())).ToList();
                var solid = parts[k].Points();
                Assert.True(drawn.Count == solid.Length, $"{name} piece {k}: {solid.Length} solid points, {drawn.Count} drawn vertices");
                foreach (var v in drawn)
                    Assert.True(solid.Any(p => Vector3.Distance(p, v) < 2e-3f), $"{name} piece {k}: drawn vertex {v} is not a corner of the solid");
            }
        }
    }

    [Fact]
    public void ForestTreeHasATaperedSolidTrunkAndATallCrownAboveAClearStem()
    {
        var golden = Golden();
        var trees = TreeColliders.Parse(TreesJson(golden), out var model);
        var cone = trees[0]; // forest conifer, scale 2.1
        Assert.True(cone.Forest && cone.Cone);
        var parts = TreeColliders.Parts(cone, model);
        Assert.Equal(4, parts.Count); // trunk and three pyramids
        var trunk = parts[0];
        Assert.Equal((PartRole.Trunk, PartShape.Frustum), (trunk.Role, trunk.Shape));
        Assert.Equal(CollisionLayers.World, trunk.Layer);
        Assert.True(trunk.HalfTop < trunk.HalfU && trunk.HalfTop > 0);
        Assert.All(parts.Skip(1), p => Assert.Equal(CollisionLayers.ShotOnly, p.Layer));
        // a clear stem: the crown starts about 5 m above the ground and the tree is about 20 m tall
        Assert.InRange(parts[1].Z0 - cone.Z, 4f, 6f);
        Assert.InRange(parts.Max(p => p.Z1) - cone.Z, 15f, 25f);
        // under the crown a ray passes the trunk's height band between stems (no foliage solid there), and it meets the trunk at the foot
        Assert.DoesNotContain(parts.Skip(1), p => p.Contains(new Vector3(cone.X + 2.5f, cone.Y, cone.Z + 2f)));
        var hit = trunk.Ray(new Vector3(cone.X - 5, cone.Y, cone.Z + 1), new Vector3(1, 0, 0), 20);
        Assert.NotNull(hit);
    }

    [Fact]
    public void FrustumRayAndContainsFollowTheTaper()
    {
        var frustum = new SolidPart(PartRole.Trunk, PartShape.Frustum, 0, 0, 0, 4, 0.5f, 0.5f, 0, 0.25f);
        // half-width 0.5 at the foot, 0.25 at the top: 0.375 at height 2
        Assert.Equal(4.625f, frustum.Ray(new Vector3(-5, 0, 2), new Vector3(1, 0, 0), 100)!.Value, 4);
        Assert.Equal(4.5f, frustum.Ray(new Vector3(-5, 0, 0), new Vector3(1, 0, 0), 100)!.Value, 4);
        Assert.True(frustum.Contains(new Vector3(0.4f, 0, 1)));
        Assert.False(frustum.Contains(new Vector3(0.4f, 0, 3)));
        Assert.Null(frustum.Ray(new Vector3(-5, 0, 4.5f), new Vector3(1, 0, 0), 100));
        // yawed a quarter turn, a square taper is the same solid
        var turned = frustum with { Yaw = MathF.PI / 2 };
        Assert.Equal(4.625f, turned.Ray(new Vector3(-5, 0, 2), new Vector3(1, 0, 0), 100)!.Value, 4);
        Assert.Equal(new SolidShapeKeys.Key(2, 500, 4000, 250), SolidShapeKeys.Of(frustum));
    }

    [Fact]
    public void MapsWithoutTheForestBlockOrFlagKeepTheOldTree()
    {
        // a map before 0.11.0: no forest block, no flags
        var old = TreeColliders.Parse("""{"model":{"trunk_width_m":0.35,"trunk_height_m":1.8,"canopy_m":2.6},"trees":[{"x":1,"y":2,"z":3,"scale":1,"rot":0,"shape":"cone"}]}""", out var model);
        Assert.Null(model.Forest);
        Assert.False(old[0].Forest);
        Assert.Equal(3, TreeColliders.Parts(old[0], model).Count);
        // a forest flag with no model block (or a half-written one) is built as the plain tree, like the old drawn tree
        var flagged = new TreeInstance(1, 2, 3, 1, 0, true, 0, 0, true);
        Assert.Equal(PartShape.Box, TreeColliders.Parts(flagged, model)[0].Shape);
        TreeColliders.Parse("""{"model":{"forest":{"trunk_width_m":0.3}},"trees":[]}""", out var partial);
        Assert.Null(partial.Forest);
        // the flag alone changes the solids once the model has the block
        var withBlock = TreeColliders.Parse(TreesJson(Golden()), out var full);
        Assert.NotEqual(TreeColliders.Parts(withBlock[0] with { Forest = false }, full).Count, TreeColliders.Parts(withBlock[0], full).Count);
    }
}
