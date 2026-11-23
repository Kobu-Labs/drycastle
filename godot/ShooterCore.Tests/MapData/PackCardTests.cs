using Drycastle.MapLoader;
using Xunit;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using V4 = System.Numerics.Vector4;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.ShooterCore.Tests.MapData;

// #362: far trees as cards of their pack models, undergrowth that grows in, tiles built ahead of a moving focus. The pure parts only.
public class PackCardTests
{
    static string Shipped => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "pack_trees.json"));

    /// <summary>An upright quad in the plane z = <paramref name="z"/>, x in [x0, x1], y in [y0, y1], facing +Z.</summary>
    static PackSurface Wall(float x0, float x1, float y0, float y1, float z, int material = 0)
        => new(material, [new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z)], [V3.UnitZ, V3.UnitZ, V3.UnitZ, V3.UnitZ],
            [new(0, 1), new(1, 1), new(1, 0), new(0, 0)], [0, 1, 2, 0, 2, 3]);

    static PackModelMesh Model(params PackSurface[] surfaces) => new() { Name = "m", HeightM = 2f, Surfaces = surfaces };

    static readonly Dictionary<int, CardMaterial> Red = new() { [0] = new CardMaterial(null, new V4(1, 0, 0, 1), -1f), [1] = new CardMaterial(null, new V4(0, 0, 1, 1), -1f) };

    // ---- config ----

    [Fact]
    public void ShippedConfigTurnsTheCardsTheFarUndergrowthAndTheLeadOn()
    {
        var c = PackTreeConfig.Parse(Shipped);
        Assert.True(c.Cards.Enabled);
        Assert.Equal((128, 192f, 0f, 0.5f), (c.Cards.CellPx, c.Cards.TileM, c.Cards.FarM, c.Cards.Scissor));
        Assert.Equal((2f, 100f), (c.LeadS, c.LeadMaxM));
        var u = c.Undergrowth;
        Assert.True(u.FarM > u.NearM && u.FarFadeM < u.FarM && u.Grow is > 0 and <= 1 && u.SinkM >= 0);
        var forest = u.Mixes.Single(m => m.Mix == 5).Kinds;
        Assert.Equal(new[] { "bush", "sapling", "shrub" }, forest.Where(k => k.Far).Select(k => k.Name).Order());
        Assert.Contains(u.Mixes.Single(m => m.Mix == 9).Kinds, k => k.Far);
    }

    [Fact]
    public void EveryNewKeyIsReadAndBadOnesAreRejected()
    {
        var c = PackTreeConfig.Parse("""
            {"version":1,"lead_s":3.5,"lead_max_m":77,"cards":{"enabled":false,"cell_px":64,"tile_m":96,"far_m":800,"scissor":0.3},
             "undergrowth":{"far_m":99,"far_fade_m":11,"grow":0.6,"sink_m":1.5,"mixes":[{"mix":5,"name":"forest","kinds":[{"name":"sapling","per_100m2":1,"far":true,"models":[{"name":"a"}]}]}]}}
            """);
        Assert.Equal((3.5f, 77f), (c.LeadS, c.LeadMaxM));
        Assert.Equal((false, 64, 96f, 800f, 0.3f), (c.Cards.Enabled, c.Cards.CellPx, c.Cards.TileM, c.Cards.FarM, c.Cards.Scissor));
        Assert.Equal((99f, 11f, 0.6f, 1.5f), (c.Undergrowth.FarM, c.Undergrowth.FarFadeM, c.Undergrowth.Grow, c.Undergrowth.SinkM));
        Assert.True(c.Undergrowth.Mixes[0].Kinds[0].Far);
        foreach (var bad in new[] { """ "cards":{"cell_px":4} """, """ "cards":{"tile_m":0} """, """ "cards":{"scissor":1} """, """ "lead_s":-1 """, """ "undergrowth":{"grow":0} """, """ "undergrowth":{"far_m":-1} """ })
            Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("{\"version\":1," + bad + "}"));
    }

    // ---- the painter ----

    [Fact]
    public void TheFrontPictureShowsTheModelWhereItStandsAndTheSidePictureSeesAWallEdgeOn()
    {
        // a wall over the left half of the model's width, full height
        var m = Model(Wall(-1, 0, 0, 2, 0));
        var e = CardPainter.Measure(m)!.Value;
        Assert.Equal((1f, 0f, 2f), (e.Radius, e.Y0, e.Y1));
        var (rgba, normal) = CardPainter.Paint(m, Red, CardPainter.View.Front, e.Radius, e.Y0, e.Y1, 16, 32);
        byte A(int x, int y) => rgba[(y * 16 + x) * 4 + 3];
        Assert.Equal(255, A(2, 16)); Assert.Equal(255, A(7, 1));
        Assert.Equal(0, A(9, 16)); Assert.Equal(0, A(15, 30));
        Assert.Equal((255, 0, 0), (rgba[(16 * 16 + 2) * 4], rgba[(16 * 16 + 2) * 4 + 1], rgba[(16 * 16 + 2) * 4 + 2]));
        // the colour is spread into the clear pixels beside the wall (alpha stays 0), so filtering does not pull in black
        Assert.Equal(255, rgba[(16 * 16 + 9) * 4]);
        // the normal of the wall, +Z in the model's frame, packed
        Assert.Equal((128, 128, 255), (normal[(16 * 16 + 2) * 4], normal[(16 * 16 + 2) * 4 + 1], normal[(16 * 16 + 2) * 4 + 2]));

        var (side, _) = CardPainter.Paint(m, Red, CardPainter.View.Side, e.Radius, e.Y0, e.Y1, 16, 32);
        Assert.All(Enumerable.Range(0, 16 * 32), i => Assert.Equal(0, side[i * 4 + 3]));
    }

    [Fact]
    public void TheNearerSurfaceHidesTheOneBehindAndAScissoredTexelIsClear()
    {
        var m = Model(Wall(-1, 1, 0, 2, -0.5f, material: 0), Wall(-1, 1, 0, 1, 0.5f, material: 1)); // blue, in front, lower half only
        var (rgba, _) = CardPainter.Paint(m, Red, CardPainter.View.Front, 1f, 0f, 2f, 8, 16);
        Assert.Equal((0, 255), (rgba[(12 * 8 + 4) * 4], rgba[(12 * 8 + 4) * 4 + 2])); // lower: blue
        Assert.Equal((255, 0), (rgba[(3 * 8 + 4) * 4], rgba[(3 * 8 + 4) * 4 + 2])); // upper: the red wall behind

        // a texture whose left half is clear, on a scissored material: the left half of the wall is not painted
        var tex = new CardTexture(2, 1, [255, 255, 255, 0, 255, 255, 255, 255]);
        var cut = new Dictionary<int, CardMaterial> { [0] = new CardMaterial(tex, V4.One, 0.5f) };
        var (leaf, _) = CardPainter.Paint(Model(Wall(-1, 1, 0, 2, 0)), cut, CardPainter.View.Front, 1f, 0f, 2f, 8, 16);
        Assert.Equal((0, 255), (leaf[(8 * 8 + 1) * 4 + 3], leaf[(8 * 8 + 6) * 4 + 3]));
    }

    [Fact]
    public void TheCardQuadsAreLaidOutAsThePicturesArePainted()
    {
        // a quad's corner, projected as the painter does, lands on the picture corner whose UV the mesher gave it
        var card = new CardModel(2f, -0.5f, 6f, 4f, new CardRect(0.1f, 0.2f, 0.3f, 0.6f), new CardRect(0.4f, 0.2f, 0.6f, 0.6f), new CardRect(0.7f, 0.2f, 0.9f, 0.4f));
        var b = PackCardMesher.Build([card], [new PackPlacement(V3.Zero, 0f, 1f, 0)], V3.Zero, 7).Single();
        Assert.Equal((7, 12, 18), (b.Material, b.V.Count, b.I.Count));
        var views = new[] { (CardPainter.View.Front, card.Front), (CardPainter.View.Side, card.Side), (CardPainter.View.Top, card.Top) };
        for (var q = 0; q < 3; q++)
            for (var i = 0; i < 4; i++)
            {
                var (view, rect) = views[q];
                var px = CardPainter.Project(b.V[q * 4 + i], view, card.Radius, card.Y0, card.Y1, 1f, 1f);
                var uv = new V2(rect.U0 + px.X * (rect.U1 - rect.U0), rect.V0 + px.Y * (rect.V1 - rect.V0));
                Assert.True((uv - b.Uv[q * 4 + i]).Length() < 1e-5f, $"{view} corner {i}: {uv} vs {b.Uv[q * 4 + i]}");
            }
        Assert.All(b.V.Skip(8), v => Assert.Equal(4f, v.Y)); // the top card lies at the crown's height
    }

    [Fact]
    public void ACardIsPosedLikeItsModelAndMarkedAsACard()
    {
        var card = new CardModel(1f, 0f, 2f, 1f, default, default, default);
        var p = new PackPlacement(new V3(110, 5, -220), MathF.PI / 2, 3f, 0, Tint: new V3(0.4f, 0.8f, 0.2f));
        var centre = new V3(100, 0, -200);
        var b = PackCardMesher.Build([card, null], [p, p with { Model = 1 }, p with { Kind = PackKind.Object, Tint = default }], centre, 0).Single();
        Assert.Equal(24, b.V.Count); // the model without a card is skipped
        // the same pose as the full mesh: a point of the model lands where PackTileMesher puts it
        var full = PackTileMesher.Build([Model(Wall(-1, 1, 0, 2, 0))], [p], centre).Single();
        for (var i = 0; i < 4; i++) Assert.True((b.V[i] - full.V[i]).Length() < 1e-4f);
        Assert.All(b.Uv2.Take(12), uv => Assert.Equal(new V2(10, -20), uv));
        // the "normal" is the model's X axis in the world (a quarter turn about Y takes +X to -Z)
        Assert.All(b.N.Take(12), n => Assert.True((n - new V3(0, 0, -1)).Length() < 1e-5f));
        Assert.Equal(new V4(0.2f, 0.4f, 0.1f, 0.875f), b.Col[0]);
        Assert.Equal(new V4(0.5f, 0.5f, 0.5f, 0.375f), b.Col[12]);
        Assert.Empty(PackCardMesher.Build([null], [p], centre, 0));
    }

    [Fact]
    public void TheKindsAreFarEnoughApartForEightBitsAndTheShadersMargin()
    {
        var all = new[] { PackTileMesher.KindAlpha(PackKind.Tree, false), PackTileMesher.KindAlpha(PackKind.Tree, true), PackTileMesher.KindAlpha(PackKind.Object, false),
            PackTileMesher.KindAlpha(PackKind.Object, true), PackTileMesher.KindAlpha(PackKind.UndergrowthFar, false), PackTileMesher.KindAlpha(PackKind.Undergrowth, false) };
        for (var i = 0; i < all.Length; i++)
            for (var j = i + 1; j < all.Length; j++) Assert.True(MathF.Abs(all[i] - all[j]) >= 0.124f);
    }

    [Fact]
    public void TheAtlasHoldsEveryModelAndItsMipmaps()
    {
        Assert.Equal((1, 1, 128), CardPainter.Layout(1, 128));
        var (cols, rows, cell) = CardPainter.Layout(50, 128);
        Assert.True(cols * rows >= 50 && Math.Abs(cols * 3 * cell - rows * 2 * cell) <= 3 * cell);
        Assert.True(CardPainter.Layout(400, 1024).Cell < 1024); // halved until it fits a texture

        var models = new[] { Model(Wall(-1, 1, 0, 2, 0)), Model(), Model(Wall(-0.5f, 0.5f, 0, 1, 0)) };
        var atlas = CardPainter.Build(models, [0, 1, 2], Red, new PackCardConfig { CellPx = 32 });
        Assert.NotNull(atlas.Models[0]); Assert.Null(atlas.Models[1]); Assert.NotNull(atlas.Models[2]); // a model with no size has no card
        var bytes = CardPainter.MipSizes(atlas.W, atlas.H).Sum(s => s.W * s.H * 4);
        Assert.Equal((bytes, bytes), (atlas.Albedo.Length, atlas.Normal.Length));
        Assert.Equal((1, 1), CardPainter.MipSizes(atlas.W, atlas.H)[^1]);
        // the front picture of model 0 is filled inside its rect and clear in the pad around it
        var f = atlas.Models[0]!.Front;
        byte A(float u, float v) => atlas.Albedo[((int)(v * atlas.H) * atlas.W + (int)(u * atlas.W)) * 4 + 3];
        Assert.Equal(255, A(0.5f * (f.U0 + f.U1), 0.5f * (f.V0 + f.V1)));
        Assert.Equal(0, A(f.U0 - 2f / atlas.W, 0.5f * (f.V0 + f.V1)));
        Assert.True(atlas.Models[0]!.Front.U1 <= atlas.Models[0]!.Side.U0 && atlas.Models[0]!.Side.U1 <= atlas.Models[0]!.Top.U0);
    }

    [Fact]
    public void MipmapsKeepASparsePicturesCover()
    {
        // every fourth column opaque: a quarter passes the scissor; plain averaging would leave alpha 64 everywhere and nothing would pass
        const int w = 64, h = 64;
        var img = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x += 4) { var o = (y * w + x) * 4; img[o] = 200; img[o + 3] = 255; }
        var cell = new[] { (0, 0, w, h) };
        float Cover(byte[] all, int level)
        {
            var sizes = CardPainter.MipSizes(w, h);
            var off = sizes.Take(level).Sum(s => s.W * s.H * 4);
            var n = 0;
            for (var i = 0; i < sizes[level].W * sizes[level].H; i++) if (all[off + i * 4 + 3] >= 128) n++;
            return n / (float)(sizes[level].W * sizes[level].H);
        }
        var plain = CardPainter.Mipmaps(img, w, h, null, 0f);
        var kept = CardPainter.Mipmaps(img, w, h, cell, 0.5f);
        Assert.Equal(0f, Cover(plain, 2));
        Assert.True(Cover(kept, 2) >= 0.25f && Cover(kept, 3) >= 0.25f);
        // colour is averaged by alpha: the opaque columns' red, not dimmed by the clear ones
        Assert.InRange(kept[w * h * 4], 190, 200);
    }

    // ---- undergrowth grows in ----

    [Fact]
    public void UndergrowthGrowsOverTheBandAndIsWholeInside()
    {
        foreach (var t in new[] { 0f, 0.3f, 0.99f })
        {
            Assert.Equal(1f, PackTreeMath.Grow(0f, t, 0.35f));
            Assert.Equal(0f, PackTreeMath.Grow(1f, t, 0.35f));
            var last = 1f;
            for (var far = 0f; far <= 1f; far += 0.05f)
            {
                var g = PackTreeMath.Grow(far, t, 0.35f);
                Assert.True(g <= last + 1e-6f);
                last = g;
            }
        }
        // pieces start at different distances: where one is half grown another has not started
        Assert.True(PackTreeMath.Grow(0.6f, 0.9f, 0.35f) > 0.5f && PackTreeMath.Grow(0.6f, 0.1f, 0.35f) == 0f);
    }

    [Fact]
    public void AFarKindIsMarkedSoItTakesTheLongerRadius()
    {
        UndergrowthKind[] kinds = [new(0.05f, 1f, 1f, 0f, 1f, [1], [1f], [10]), new(0.05f, 1f, 1f, 0f, 1f, [2], [1f], [10], Far: true)];
        var mask = new ForestMask { Gx0 = 0, Gy0 = 0, W = 24, H = 24, Cells = Enumerable.Repeat(true, 24 * 24).ToArray() };
        var all = UndergrowthGen.Tile(new UndergrowthConfig(), kinds, 0, 0, 48, 48, 2f, mask, [], (_, _) => 0f, 1 << 30, out _);
        Assert.NotEmpty(all);
        Assert.All(all, p => Assert.Equal(p.Model == 2 ? PackKind.UndergrowthFar : PackKind.Undergrowth, p.Kind));
        var colour = new PackColourConfig();
        Assert.All(all, p => Assert.Equal(p.Kind, PackColourMath.Undergrowth(p, colour).Kind));
        // a kind switched off for a layer (no density) keeps the others where they were: the two layers of a tile add up to the one scatter
        var nearOnly = UndergrowthGen.Tile(new UndergrowthConfig(), [kinds[0], kinds[1] with { PerM2 = 0f }], 0, 0, 48, 48, 2f, mask, [], (_, _) => 0f, 1 << 30, out _);
        Assert.Equal(all.Where(p => p.Model == 1), nearOnly);
    }

    // ---- building ahead ----

    [Fact]
    public void TilesAreWantedAlongTheWayTheFocusIsHeading()
    {
        var here = new V2(100, 100);
        Assert.Equal(here, PackTreeMath.Lead(here, V2.Zero, 2f, 100f));
        Assert.Equal(new V2(150, 100), PackTreeMath.Lead(here, new V2(25, 0), 2f, 100f));
        Assert.Equal(new V2(200, 100), PackTreeMath.Lead(here, new V2(80, 0), 2f, 100f)); // an aircraft: capped
        Assert.Equal(here, PackTreeMath.Lead(here, new V2(25, 0), 0f, 100f));

        // a tile 150 m ahead is 150 m from the focus but on the way of a focus heading there
        Assert.Equal(150f, PackTreeMath.DistToBox(100, 100, 250, 76, 298, 124));
        Assert.Equal(50f, PackTreeMath.DistToBoxAlong(100, 100, 200, 100, 250, 76, 298, 124));
        Assert.Equal(0f, PackTreeMath.DistToBoxAlong(100, 100, 300, 100, 250, 76, 298, 124), 3);
        Assert.Equal(150f, PackTreeMath.DistToBoxAlong(100, 100, 100, 100, 250, 76, 298, 124));
    }

    [Fact]
    public void TheSpeedFollowsTheFocusAndATeleportIsNotASpeed()
    {
        var v = V2.Zero; var at = V2.Zero;
        for (var i = 0; i < 120; i++) { var next = at + new V2(25f / 60f, 0); v = PackTreeMath.Velocity(v, at, next, 1f / 60f); at = next; }
        Assert.InRange(v.X, 24f, 25.01f);
        Assert.Equal(V2.Zero, PackTreeMath.Velocity(v, at, at + new V2(500, 0), 1f / 60f)); // a respawn across the map
        Assert.Equal(v, PackTreeMath.Velocity(v, at, at, 0f));
    }

    // ---- the layer machine ----

    [Fact]
    public void ALayerIsBuiltHandedOverShownAndFreed()
    {
        var m = new StateMachine<PackLayerState>(PackLayerMachine.Spec);
        Assert.True(m.Is(PackLayerState.Idle));
        Assert.True(m.Enter(PackLayerState.Building) && m.Enter(PackLayerState.Handing) && m.Enter(PackLayerState.Ready) && m.Enter(PackLayerState.Idle));
        Assert.True(m.Enter(PackLayerState.Ready)); // nothing to make on this tile
        Assert.False(PackLayerMachine.Spec.Allows(PackLayerState.Ready, PackLayerState.Building)); // freed first, then asked for anew
        Assert.False(PackLayerMachine.Spec.Allows(PackLayerState.Idle, PackLayerState.Handing));
        Assert.Empty(PackLayerMachine.Spec.Unreachable);
    }
}
