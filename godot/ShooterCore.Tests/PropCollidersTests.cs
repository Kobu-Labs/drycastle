using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Shooter v2 (#158): tree trunk and crown solids from trees.json, the ray and surface lookups, object solids.</summary>
public class PropCollidersTests
{
    static readonly TreeModel Model = new(0.35f, 1.8f, 2.6f);

    const string TreesDoc = """
    {"format_version": 1, "chunk_size_m": 250,
     "model": {"trunk_width_m": 0.35, "trunk_height_m": 1.8, "canopy_m": 2.6},
     "trees": [
       {"x": 10.0, "y": 20.0, "z": 5.0, "scale": 1.0, "rot": 0.0, "shape": "box", "chunk": [0, 0]},
       {"x": 300.0, "y": 40.0, "z": 6.0, "scale": 0.9, "rot": 0.5, "shape": "cone", "chunk": [1, 0]}
     ]}
    """;

    [Fact]
    public void TreesJsonGivesModelNumbersAndChunks()
    {
        var trees = TreeColliders.Parse(TreesDoc, out var model);
        Assert.Equal(new TreeModel(0.35f, 1.8f, 2.6f), model);
        Assert.Equal(2, trees.Count);
        Assert.False(trees[0].Cone);
        Assert.True(trees[1].Cone);
        Assert.Equal((1, 0), (trees[1].ChunkX, trees[1].ChunkY));
        Assert.Empty(TreeColliders.Parse("", out _));
    }

    [Fact]
    public void BroadleafTrunkIsASolidBoxFromBelowTheGroundToTheTrunkTop()
    {
        var tree = new TreeInstance(10, 20, 5, 1.25f, 0.3f, false, 0, 0);
        var parts = TreeColliders.Parts(tree, Model);
        var trunk = parts[0];
        Assert.Equal(PartRole.Trunk, trunk.Role);
        Assert.Equal(PartShape.Box, trunk.Shape);
        Assert.Equal(CollisionLayers.World, trunk.Layer);
        Assert.Equal("wood", trunk.Group);
        Assert.Equal(5 - 0.2f, trunk.Z0, 4);
        Assert.Equal(5 + 1.8f * 1.25f, trunk.Z1, 4);
        Assert.Equal(0.35f * 1.25f / 2, trunk.HalfU, 4);
        Assert.Equal(0.3f, trunk.Yaw);
    }

    [Fact]
    public void BroadleafCrownIsTwoStackedBoxesAsDrawn()
    {
        var tree = new TreeInstance(0, 0, 0, 1f, 0f, false, 0, 0);
        var parts = TreeColliders.Parts(tree, Model);
        Assert.Equal(3, parts.Count);
        var lo = 1.8f - 0.3f;
        var big = parts[1];
        Assert.Equal(PartShape.Box, big.Shape);
        Assert.Equal(CollisionLayers.ShotOnly, big.Layer);
        Assert.Equal("foliage", big.Group);
        Assert.Equal(lo, big.Z0, 4);
        Assert.Equal(lo + 2.6f, big.Z1, 4);
        Assert.Equal(1.3f, big.HalfU, 4);
        var top = parts[2];
        Assert.Equal(2.6f * 0.3f, top.HalfU, 4);
        Assert.Equal(lo + 2.6f, top.Z0, 4);
        Assert.Equal(lo + 2.6f * 1.6f, top.Z1, 4);
    }

    [Fact]
    public void ConiferCrownIsTwoStackedPyramidsWithTheirBasesAsDrawn()
    {
        var tree = new TreeInstance(0, 0, 0, 1f, 0f, true, 0, 0);
        var parts = TreeColliders.Parts(tree, Model);
        Assert.Equal(3, parts.Count);
        Assert.Equal(PartShape.Pyramid, parts[1].Shape);
        Assert.Equal(1.8f * 0.5f, parts[1].Z0, 4);
        Assert.Equal(1.8f + 2.6f * 0.7f, parts[1].Z1, 4);
        Assert.Equal(2.6f * 0.55f, parts[1].HalfU, 4);
        Assert.Equal(5, parts[1].Points().Length);
        Assert.Equal(8, parts[0].Points().Length);
    }

    [Fact]
    public void RayStopsOnTheFaceOfABox()
    {
        var box = new SolidPart(PartRole.Trunk, PartShape.Box, 10, 0, 0, 2, 1, 1, 0);
        Assert.Equal(9f, box.Ray(new Vector3(0, 0, 1), new Vector3(1, 0, 0), 100)!.Value, 4);
        Assert.Null(box.Ray(new Vector3(0, 0, 1), new Vector3(1, 0, 0), 5)); // out of range
        Assert.Null(box.Ray(new Vector3(0, 0, 3), new Vector3(1, 0, 0), 100)); // over the top
        Assert.Equal(0f, box.Ray(new Vector3(10, 0, 1), new Vector3(1, 0, 0), 100)); // from inside
    }

    [Fact]
    public void RayUsesTheRotatedBoxAxes()
    {
        // a 2 m by 0.4 m slab along north; a ray from the west meets its narrow side
        var slab = new SolidPart(PartRole.Crown, PartShape.Box, 0, 0, 0, 2, 1f, 0.2f, MathF.PI / 2);
        Assert.Equal(4.8f, slab.Ray(new Vector3(-5, 0, 1), new Vector3(1, 0, 0), 100)!.Value, 4);
        // the same ray misses the slab's far end (it runs along the north axis, 1 m out)
        Assert.Null(slab.Ray(new Vector3(-5, 1.5f, 1), new Vector3(1, 0, 0), 100));
    }

    [Fact]
    public void RayStopsOnThePyramidSideAtItsCrossSection()
    {
        var pyramid = new SolidPart(PartRole.Crown, PartShape.Pyramid, 0, 0, 2, 4, 1, 1, 0);
        // at height 2.5 the cross-section half-width is 1 * (1 - 0.5 / 2) = 0.75
        Assert.Equal(4.25f, pyramid.Ray(new Vector3(-5, 0, 2.5f), new Vector3(1, 0, 0), 100)!.Value, 4);
        // near the apex it is narrow: 0.05 at height 3.9
        Assert.Equal(4.95f, pyramid.Ray(new Vector3(-5, 0, 3.9f), new Vector3(1, 0, 0), 100)!.Value, 3);
        Assert.Null(pyramid.Ray(new Vector3(-5, 0, 4.5f), new Vector3(1, 0, 0), 100)); // above the apex
        Assert.Null(pyramid.Ray(new Vector3(-5, 0, 1.5f), new Vector3(1, 0, 0), 100)); // below the base
    }

    [Fact]
    public void ContainsMatchesTheSolidAndItsTolerance()
    {
        var box = new SolidPart(PartRole.Trunk, PartShape.Box, 0, 0, 0, 2, 1, 1, 0);
        Assert.True(box.Contains(new Vector3(0.5f, 0.5f, 1)));
        Assert.False(box.Contains(new Vector3(1.02f, 0, 1)));
        Assert.True(box.Contains(new Vector3(1.02f, 0, 1), 0.05f));
        var pyramid = new SolidPart(PartRole.Crown, PartShape.Pyramid, 0, 0, 2, 4, 1, 1, 0);
        Assert.True(pyramid.Contains(new Vector3(0, 0, 3)));
        Assert.False(pyramid.Contains(new Vector3(0.9f, 0, 3)));
    }

    [Fact]
    public void ARayFromTheWestHitsTheTrunkBeforeTheCrown()
    {
        // a broadleaf tree at (10, 0): the ray at trunk height meets the trunk, the ray at crown height meets the crown
        var tree = new TreeInstance(10, 0, 0, 1f, 0f, false, 0, 0);
        var parts = TreeColliders.Parts(tree, Model);
        var trunkHit = parts.Select(p => p.Ray(new Vector3(0, 0, 0.8f), new Vector3(1, 0, 0), 100)).Where(t => t.HasValue).Select(t => t!.Value).Min();
        Assert.Equal(10f - 0.175f, trunkHit, 3);
        var crownOnly = parts.Where(p => p.Role == PartRole.Crown).Select(p => p.Ray(new Vector3(0, 0, 2.5f), new Vector3(1, 0, 0), 100)).Where(t => t.HasValue).Select(t => t!.Value).Min();
        Assert.Equal(10f - 1.3f, crownOnly, 3);
        // above the crown nothing is hit
        Assert.Empty(parts.Select(p => p.Ray(new Vector3(0, 0, 6), new Vector3(1, 0, 0), 100)).Where(t => t.HasValue));
    }

    [Fact]
    public void GroupAtFindsTrunkAndCrownOfANearbyTree()
    {
        var trees = TreeColliders.Parse(TreesDoc, out var model);
        Assert.Equal("wood", TreeColliders.GroupAt(trees, model, new Vector3(10, 20, 6)));      // in the trunk
        Assert.Equal("foliage", TreeColliders.GroupAt(trees, model, new Vector3(10, 20, 8)));   // in the crown
        Assert.Null(TreeColliders.GroupAt(trees, model, new Vector3(10, 20, 20)));              // far above
        Assert.Null(TreeColliders.GroupAt(trees, model, new Vector3(100, 100, 6)));             // no tree there
    }

    [Fact]
    public void LayersKeepMovementOnTheTrunkAndShotsOnTheCrown()
    {
        Assert.Equal(0u, CollisionLayers.ShotOnly & 1u);          // a player's move mask (layer 1) does not see crowns
        Assert.Equal(CollisionLayers.World, TreeColliders.Parts(new TreeInstance(0, 0, 0, 1, 0, false, 0, 0), Model)[0].Layer);
        Assert.NotEqual(0u, CollisionLayers.ShotOnly & CollisionLayers.Bullets);
        Assert.NotEqual(0u, CollisionLayers.ShotOnly & CollisionLayers.WorldShots);
        Assert.NotEqual(0u, CollisionLayers.Vehicles & CollisionLayers.Bullets);
    }

    [Fact]
    public void ObjectSolidsFollowTheCategory()
    {
        Assert.Empty(ObjectColliders.ForCategory("flower", 1, 1, 1));
        var bush = ObjectColliders.ForCategory("bush", 2, 1.5f, 2);
        Assert.Single(bush);
        Assert.Equal(CollisionLayers.ShotOnly, bush[0].Layer);
        var tree = ObjectColliders.ForCategory("tree", 3, 10, 3);
        Assert.Equal(2, tree.Count);
        Assert.Equal(CollisionLayers.World, tree[0].Layer);
        Assert.Equal(CollisionLayers.ShotOnly, tree[1].Layer);
        var wall = ObjectColliders.ForCategory("wall", 4, 2.5f, 0.3f);
        Assert.Single(wall);
        Assert.Equal(CollisionLayers.World, wall[0].Layer);
        Assert.Equal(2.5f, wall[0].Z1);
    }

    [Fact]
    public void TreeGridGivesTheSameSurfaceAsTheLinearScan()
    {
        // deterministic scatter of trees over 400 m, with a few sizes; the grid must answer every probe as the scan does
        var seed = 12345u;
        float Next(float max) { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24) * max; }
        var trees = new List<TreeInstance>();
        for (var i = 0; i < 600; i++)
            trees.Add(new TreeInstance(Next(400f), Next(400f), 0f, 0.6f + Next(1.2f), Next(6.28f), false, 0, 0));
        var grid = new TreeColliders.Index(trees, Model);
        var hits = 0;
        for (var i = 0; i < 4000; i++)
        {
            var near = trees[i % trees.Count]; // probes around trees, so most of them land on a trunk or a crown
            var p = new Vector3(near.X + Next(3f) - 1.5f, near.Y + Next(3f) - 1.5f, 0.9f);
            var linear = TreeColliders.GroupAt(trees, Model, p);
            Assert.Equal(linear, grid.GroupAt(p));
            if (linear != null) hits++;
        }
        Assert.True(hits > 100, $"the probes should land on trees often enough to mean something (got {hits})");
    public void TreesOfEqualScaleShareShapeKeysWhateverTheirPlaceAndYaw()
    {
        var a = TreeColliders.Parts(new TreeInstance(10, 20, 5, 1.1f, 0.2f, false, 0, 0), Model);
        var b = TreeColliders.Parts(new TreeInstance(-300, 7, 9, 1.1f, 2.5f, false, 3, 1), Model);
        for (var i = 0; i < a.Count; i++) Assert.Equal(SolidShapeKeys.Of(a[i]), SolidShapeKeys.Of(b[i]));

        var c = TreeColliders.Parts(new TreeInstance(10, 20, 5, 1.2f, 0.2f, false, 0, 0), Model);
        Assert.NotEqual(SolidShapeKeys.Of(a[0]), SolidShapeKeys.Of(c[0]));
    }

    [Fact]
    public void ConiferCrownKeysAreRelativeSoAPositionDoesNotSplitThem()
    {
        var a = TreeColliders.Parts(new TreeInstance(10, 20, 5, 0.9f, 0f, true, 0, 0), Model);
        var b = TreeColliders.Parts(new TreeInstance(50, 60, 12.5f, 0.9f, 1.3f, true, 0, 0), Model);
        Assert.Equal(PartShape.Pyramid, a[1].Shape);
        Assert.Equal(SolidShapeKeys.Of(a[1]), SolidShapeKeys.Of(b[1]));
        Assert.Equal(SolidShapeKeys.Of(a[2]), SolidShapeKeys.Of(b[2]));
    }

    [Fact]
    public void ShapeSizesQuantiseToMillimetres()
    {
        Assert.Equal(1000L, SolidShapeKeys.Quantise(1.0004f));
        Assert.Equal(1001L, SolidShapeKeys.Quantise(1.0006f));
        var box = new SolidPart(PartRole.Trunk, PartShape.Box, 0, 0, 0, 2, 0.5f, 0.25f, 0);
        Assert.Equal(new SolidShapeKeys.Key(0, 1000, 2000, 500), SolidShapeKeys.Of(box));
    }
}
