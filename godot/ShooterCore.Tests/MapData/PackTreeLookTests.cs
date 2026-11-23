using Drycastle.MapLoader;
using Xunit;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Tests.MapData;

// #287 look of the pack trees: per-tree colour, the undergrowth's own radius, whole fallen trees. The pure parts only.
public class PackTreeLookTests
{
    static string Shipped => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "pack_trees.json"));

    // ---- config: every number is read (DataFile's snake-case policy skips what it cannot map) ----

    [Fact]
    public void ShippedConfigHasTheNewSettingsAndTheRadii()
    {
        var c = PackTreeConfig.Parse(Shipped);
        Assert.Equal((220f, 40f, 40f, 6), (c.NearM, c.FadeM, c.AheadM, c.MaxInFlight));
        Assert.Equal((64f, 20f, 16f), (c.Undergrowth.NearM, c.Undergrowth.FadeM, c.Undergrowth.AheadM));
        Assert.True(c.Undergrowth.NearM < c.NearM && c.Undergrowth.FadeM < c.Undergrowth.NearM);
        var k = c.Colour;
        Assert.True(k.Enabled && k.Saturation > 0 && k.Gain > 0 && k.HueShift > 0 && k.Value > 0 && k.UndergrowthScale is > 0 and < 1);
        // the foliage takes the baked tree's colour: its fallback, the undergrowth greens and the shading span are read
        Assert.True(PackColourMath.TryHex(k.TreeColour, out _) && k.UndergrowthColours.Count >= 2 && k.LeafRef != 0.2f + 1 && k.ShadeMin < 1 && k.ShadeMax > 1);
        Assert.True(k.Accents.Count >= 3 && k.Accents.All(a => a.Tint.Length == 3 && a.Weight > 0));
        var f = c.GroundObjects.Fallen;
        Assert.Equal((1.25f, 0.5f, 2.6f, 3), (f.StretchMax, f.ScaleMin, f.ScaleMax, f.MaxPieces));
        var log = c.GroundObjects.Kinds["log"].Models;
        Assert.Contains(log, e => !e.Lay); Assert.Contains(log, e => e.Lay);
        Assert.Contains(log, e => e.Lay && e.Pack == "swamp");
    }

    [Fact]
    public void EveryNewKeyIsReadNotLeftAtItsDefault()
    {
        // each value differs from the class default, so a key that silently maps to nothing shows
        var c = PackTreeConfig.Parse("""
            {"version":1,"near_m":111,"fade_m":22,"ahead_m":33,"max_in_flight":5,
             "colour":{"enabled":false,"saturation":0.7,"gain":0.8,"hue_shift":0.31,"value":0.27,"accent_share":0.41,"undergrowth_scale":0.23,"leaf_lo":0.02,"leaf_hi":0.5,
                       "accents":[{"name":"a","tint":[0.5,0.6,0.7],"weight":2.5}]},
             "ground_objects":{"fallen":{"enabled":false,"stretch_max":1.4,"scale_min":0.6,"scale_max":3.1,"sink":0.2,"max_pieces":2,"piece_weight":0.3},
                               "kinds":{"log":{"models":[{"name":"x","lay":true}]}}},
             "undergrowth":{"near_m":44,"fade_m":9,"ahead_m":7}}
            """);
        Assert.Equal((111f, 22f, 33f, 5), (c.NearM, c.FadeM, c.AheadM, c.MaxInFlight));
        var k = c.Colour;
        Assert.Equal((false, 0.7f, 0.8f, 0.31f, 0.27f), (k.Enabled, k.Saturation, k.Gain, k.HueShift, k.Value));
        Assert.Equal((0.41f, 0.23f, 0.02f, 0.5f), (k.AccentShare, k.UndergrowthScale, k.LeafLo, k.LeafHi));
        Assert.Equal(new[] { 0.5f, 0.6f, 0.7f }, k.Accents.Single().Tint); Assert.Equal(2.5f, k.Accents.Single().Weight);
        var f = c.GroundObjects.Fallen;
        Assert.Equal((false, 1.4f, 0.6f, 3.1f, 0.2f, 2, 0.3f), (f.Enabled, f.StretchMax, f.ScaleMin, f.ScaleMax, f.Sink, f.MaxPieces, f.PieceWeight));
        Assert.True(c.GroundObjects.Kinds["log"].Models.Single().Lay);
        Assert.Equal((44f, 9f, 7f), (c.Undergrowth.NearM, c.Undergrowth.FadeM, c.Undergrowth.AheadM));
    }

    [Fact]
    public void ShippedJsonHasOnlyKeysTheNewSectionsRead()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Shipped);
        var root = doc.RootElement;
        Assert.All(root.GetProperty("colour").EnumerateObject(), p => Assert.Contains(p.Name, new[] { "enabled", "saturation", "gain", "hue_shift", "value", "accent_share", "undergrowth_scale", "leaf_lo", "leaf_hi", "accents", "tree_colour", "undergrowth_colours", "leaf_ref", "shade_min", "shade_max" }));
        foreach (var a in root.GetProperty("colour").GetProperty("accents").EnumerateArray()) Assert.All(a.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "name", "tint", "weight" }));
        var ground = root.GetProperty("ground_objects");
        Assert.All(ground.GetProperty("fallen").EnumerateObject(), p => Assert.Contains(p.Name, new[] { "enabled", "stretch_max", "scale_min", "scale_max", "sink", "max_pieces", "piece_weight" }));
        foreach (var m in ground.GetProperty("kinds").GetProperty("log").GetProperty("models").EnumerateArray())
            Assert.All(m.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "pack", "name", "weight", "lay" }));
    }

    [Fact]
    public void ConfigRejectsBadColourFallenAndUndergrowthRadius()
    {
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"colour":{"accents":[{"name":"a","tint":[1,1]}]}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"colour":{"accents":[{"name":"a","tint":[1,1,3]}]}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"colour":{"accent_share":2}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"colour":{"leaf_lo":0.5,"leaf_hi":0.2}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"ground_objects":{"fallen":{"stretch_max":0.9}}}"""));
        Assert.Throws<DataFileException>(() => PackTreeConfig.Parse("""{"version":1,"undergrowth":{"near_m":0}}"""));
    }

    // ---- colour ----

    [Fact]
    public void TintIsAPositionHashThatVariesWithinTheConfiguredAmounts()
    {
        var c = PackTreeConfig.Parse(Shipped).Colour;
        Assert.Equal(PackColourMath.Tint(12.34f, -56.78f, c), PackColourMath.Tint(12.34f, -56.78f, c));
        var tints = Enumerable.Range(0, 3000).Select(i => PackColourMath.Tint(i * 1.7f, i * 0.9f - 400f, c)).ToList();
        Assert.All(tints, t => { Assert.InRange(t.X, 0f, 2f); Assert.InRange(t.Y, 0f, 2f); Assert.InRange(t.Z, 0f, 2f); });
        Assert.True(tints.Select(t => MathF.Round(t.X, 3)).Distinct().Count() > 50); // not a handful of presets
        // ordinary trees stay within hue_shift and value of 1 (an accent is the clear departure): most of them
        var ordinary = tints.Count(t => MathF.Abs(t.X - t.Z) <= 2 * c.HueShift * (1 + c.Value) + 1e-3f && t.Y >= 1 - c.Value - 1e-3f && t.Y <= 1 + c.Value + 1e-3f);
        Assert.InRange(ordinary / 3000.0, 1 - c.AccentShare - 0.05, 1.0);
        // the shares of the red/blue tilt: both directions
        Assert.Contains(tints, t => t.X > t.Z + c.HueShift); Assert.Contains(tints, t => t.Z > t.X + c.HueShift);
    }

    [Fact]
    public void UndergrowthVariesMoreGentlyAndOffMeansNoTint()
    {
        var c = PackTreeConfig.Parse(Shipped).Colour;
        float Dev(float amount) => Enumerable.Range(0, 2000).Average(i => (PackColourMath.Tint(i * 2.3f, i * 0.7f, c, amount) - V3.One).Length());
        Assert.True(Dev(c.UndergrowthScale) < 0.75f * Dev(1f));
        Assert.Equal(V3.One, PackColourMath.Tint(5, 5, c, 0f));
        Assert.Equal(V3.One, PackColourMath.Tint(5, 5, new PackColourConfig { Enabled = false }));
        var piece = PackColourMath.Undergrowth(new PackPlacement(new V3(10, 2, -20), 0, 1, 3), c);
        Assert.Equal(PackKind.Undergrowth, piece.Kind);
        Assert.Contains(c.UndergrowthColours, h => PackColourMath.TryHex(h, out var g) && (g * PackColourMath.Tint(10, 20, c, c.UndergrowthScale) - piece.Tint).Length() < 1e-5f); // one of the greens, varied; map y is minus Godot z
    }

    [Fact]
    public void TreePlacementCarriesItsTintAndTheMesherWritesItWithItsKind()
    {
        var cfg = PackTreeConfig.Parse(Shipped);
        var cands = new[] { new PackCandidate(0, 5f, 1f, true) };
        var t = new TreeInstance(30.5f, 12.25f, 7f, 1f, 0, true, 0, 0);
        var p = PackTreeMath.Place(t, new TreeModel(0.35f, 1.8f, 2.6f), cands, cands, cfg)!.Value;
        PackColourMath.TryHex(cfg.Colour.TreeColour, out var green);
        Assert.Equal(green * PackColourMath.Tint(30.5f, 12.25f, cfg.Colour), p.Tint); // no colour in the record: the config's green
        PackColourMath.TryHex("#285e33", out var baked);
        Assert.Equal(baked * PackColourMath.Tint(30.5f, 12.25f, cfg.Colour), PackColourMath.Foliage(baked, 30.5f, 12.25f, cfg.Colour)); // the baked tree's own colour
        var model = new PackModelMesh { Name = "m", HeightM = 1, Surfaces = [new PackSurface(0, [V3.Zero, V3.UnitX, V3.UnitY], [V3.UnitZ, V3.UnitZ, V3.UnitZ], [V2.Zero, V2.Zero, V2.Zero], [0, 1, 2])] };
        var b = PackTileMesher.Build([model], [p, p with { Kind = PackKind.Object, Tint = default }, p with { Kind = PackKind.Undergrowth }], V3.Zero).Single();
        Assert.Equal(9, b.Col.Count);
        Assert.Equal(new System.Numerics.Vector4(p.Tint * 0.5f, 1f), b.Col[0]); // tree: tint halved, alpha 1
        Assert.Equal(new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 0.5f), b.Col[3]); // object: no tint (stored 0.5 = 1), alpha 0.5
        Assert.Equal(0f, b.Col[6].W); // undergrowth: alpha 0, which the shader cuts on its own radius
    }

    // ---- fallen trees ----

    // 4 m tall dead tree: a trunk of radius 0.05 (4 verts a slice) and a branch far to the side, base at y = 0
    static PackModelMesh Dead(float trunk = 0.05f, float height = 4f)
    {
        var v = new List<V3>();
        for (var i = 0; i <= 8; i++)
        {
            var y = i * height / 8;
            v.Add(new V3(trunk, y, 0)); v.Add(new V3(-trunk, y, 0)); v.Add(new V3(0, y, trunk)); v.Add(new V3(0, y, -trunk));
        }
        v.Add(new V3(1.2f, height * 0.3f, 0)); v.Add(new V3(1.2f, height * 0.9f, 0));
        var n = v.Select(_ => V3.UnitY).ToArray();
        return new PackModelMesh { Name = "dead", HeightM = height, Surfaces = [new PackSurface(0, v.ToArray(), n, v.Select(_ => V2.Zero).ToArray(), [0, 1, 2])] };
    }

    // a log model 0.5 across and 3 m long (Z -1.5..1.5)
    static PackModelMesh LogModel() => new()
    {
        Name = "log", HeightM = 0.5f,
        Surfaces = [new PackSurface(0, [new V3(-0.25f, -0.25f, -1.5f), new V3(0.25f, 0.25f, 1.5f), V3.Zero], [V3.UnitY, V3.UnitY, V3.UnitY], [V2.Zero, V2.Zero, V2.Zero], [0, 1, 2])],
    };

    [Fact]
    public void TrunkRadiusReadsTheTrunkNotTheBranches()
    {
        Assert.Equal(0.05f, Dead().TrunkRadiusM, 3);
        Assert.Equal(0.3f, Dead(0.3f).TrunkRadiusM, 3);
    }

    static readonly FallenConfig Fallen = new();
    static PackGroundObject Rec(float len = 6f, float scale = 1f, float rot = 0f) => new("log", 100f, 50f, 7f, rot, scale, len);

    [Fact]
    public void AnUprightModelLiesAlongTheLogWithItsBaseAtTheThickEnd()
    {
        var o = Rec();
        var box = GroundObjectMath.BoxOf(o, new GroundModel());
        var fit = GroundObjectMath.FitLaid(Dead(), 6f, box, Fallen)!.Value;
        Assert.Equal(6f / 4f, fit.Cross, 4); // uniform: the height 4 m becomes the length 6 m
        Assert.Equal(1f, fit.Stretch, 4);
        Assert.Equal(0.1f * 1.5f * (0.5f - Fallen.Sink), fit.Lift, 4); // axis rides over the ground by the radius less the sink: 12% of the diameter is buried
        var p = GroundObjectMath.PlaceLog(o, [new GroundCandidate(0, 1f, true)], [Dead()], 0.225f, 90f, Fallen, box).Single();
        Assert.Equal(PackKind.Object, p.Kind);
        var b = PackTileMesher.Build([Dead()], [p], V3.Zero).Single();
        // base end at -u (x - 3), top end at +u (x + 3): the log's midpoint is the record's point; u = +x at rot 0 (Godot z is -y, so the log lies along x)
        var trunk = b.V.Take(36).ToList();
        Assert.Equal(97f, trunk.Min(v => v.X), 3); Assert.Equal(103f, trunk.Max(v => v.X), 3);
        Assert.All(trunk, v => Assert.InRange(v.Z, -50.2f, -49.8f)); // within the trunk's own width of the record's y (-50 in Godot axes)
        // resting on the ground (z 7): the trunk's lowest point sinks by the share of its diameter, its top is inside the collider box
        var low = trunk.Min(v => v.Y); var high = trunk.Max(v => v.Y);
        Assert.InRange(low, 7f - 0.02f, 7f + 0.01f); // sunk by at most 12% of the 0.15 m diameter, the ring's corners being what is lowest
        Assert.True(high - 7f <= box.Top);
        Assert.True(trunk.Max(v => MathF.Abs(v.Z + 50f)) <= box.HalfWidth);
    }

    [Fact]
    public void ALogRotatedByRotLiesAlongItsHeading()
    {
        var o = Rec(rot: MathF.PI / 2); // u = +y (north), Godot -z
        var box = GroundObjectMath.BoxOf(o, new GroundModel());
        var p = GroundObjectMath.PlaceLog(o, [new GroundCandidate(0, 1f, true)], [Dead()], 0.225f, 90f, Fallen, box).Single();
        var trunk = PackTileMesher.Build([Dead()], [p], V3.Zero).Single().V.Take(36).ToList();
        Assert.Equal(-53f, trunk.Min(v => v.Z), 3); Assert.Equal(-47f, trunk.Max(v => v.Z), 3); // the base end is the south one (-u): Godot z = -y, so -47
        Assert.True(trunk.First().Z > trunk.Last().Z - 1e-3f); // the first vertex is at the base (y = 0 of the model), south
    }

    [Fact]
    public void AModelThatIsOutsideTheScaleSpanOrTooThickForTheBoxIsNotUsed()
    {
        var o = Rec(); var box = GroundObjectMath.BoxOf(o, new GroundModel());
        Assert.Null(GroundObjectMath.FitLaid(Dead(height: 1.5f), 6f, box, Fallen)); // scale 4 > 2.6
        Assert.Null(GroundObjectMath.FitLaid(Dead(height: 20f), 6f, box, Fallen)); // scale 0.3 < 0.5
        Assert.Null(GroundObjectMath.FitLaid(Dead(trunk: 0.3f), 6f, box, Fallen)); // 0.9 m thick against a box 0.32 m wide
        // a little too thick is thinned across, within the stretch limit; the length stays
        var edge = GroundObjectMath.FitLaid(Dead(trunk: 0.12f), 6f, box, Fallen)!.Value;
        Assert.InRange(edge.Stretch, 1f, Fallen.StretchMax + 1e-4f);
        Assert.True(2 * 0.12f * edge.Cross <= box.Top / (1 - Fallen.Sink) + 1e-4f);
        Assert.Equal(6f / 4f, edge.Cross * edge.Stretch, 4);
    }

    [Fact]
    public void LogModelsAreStretchedAtMostTheLimitOrLaidEndToEndAndTheChoiceFollowsTheHash()
    {
        var logs = new[] { new GroundCandidate(0, 1f) };
        var model = LogModel();
        var box = GroundObjectMath.BoxOf(Rec(), new GroundModel());
        // radius 0.25 at scale 1 is the model's own: model length 3 m. A 3.6 m log: one piece, stretch 1.2 (inside 1.25)
        var one = GroundObjectMath.PlaceLog(Rec(3.6f), logs, [model], 0.25f, 90f, Fallen, box);
        Assert.Single(one); Assert.Equal(1.2f, one[0].Stretch, 4);
        // a 6 m log: one piece would be 2x; two pieces of 3 m, centred 1.5 m either side, the thick (base) ends toward -u
        var two = GroundObjectMath.PlaceLog(Rec(6f), logs, [model], 0.25f, 90f, Fallen, box);
        Assert.Equal(2, two.Count);
        Assert.All(two, p => Assert.Equal(1f, p.Stretch, 4));
        Assert.Equal(98.5f, two[0].Base.X, 3); Assert.Equal(101.5f, two[1].Base.X, 3); // along x at rot 0, each piece centred on a quarter of the length (the model's middle on it)
        // 9 m: three pieces; 20 m: nothing fits within the limit, so the nearest is stretched anyway (the baked log must not vanish)
        Assert.Equal(3, GroundObjectMath.PlaceLog(Rec(9f), logs, [model], 0.25f, 90f, Fallen, box).Count);
        var far = GroundObjectMath.PlaceLog(Rec(20f), logs, [model], 0.25f, 90f, Fallen, box);
        Assert.Equal(3, far.Count); Assert.All(far, p => Assert.InRange(p.Stretch, 2f, 2.5f));
        // laid trees and log models share the choice by weight and position hash: both appear over many records, deterministically
        var mixed = new[] { new GroundCandidate(0, 1f), new GroundCandidate(1, 1f, true) };
        var picks = Enumerable.Range(0, 400).Select(i => GroundObjectMath.PlaceLog(Rec(6f) with { X = i * 3.1f, Y = i * 1.3f }, mixed, [model, Dead()], 0.25f, 90f, Fallen, box)).ToList();
        var laid = picks.Count(l => l.Count == 1 && l[0].Pitch != 0);
        Assert.InRange(laid, 100, 300);
        Assert.Equal(picks[7][0], GroundObjectMath.PlaceLog(Rec(6f) with { X = 7 * 3.1f, Y = 7 * 1.3f }, mixed, [model, Dead()], 0.25f, 90f, Fallen, box)[0]);
        // fallen.enabled = false: the first slice, one log model stretched to the length, no laid model
        var off = GroundObjectMath.PlaceLog(Rec(6f), mixed, [model, Dead()], 0.25f, 90f, new FallenConfig { Enabled = false }, box).Single();
        Assert.Equal(0, off.Pitch); Assert.Equal(2f, off.Stretch, 3);
    }

    [Fact]
    public void BoxOfIsTheColliderOfTheLog()
    {
        var o = Rec(scale: 1f);
        var part = GroundObjectColliders.LogBox(new GroundObject(GroundKind.Log, o.X, o.Y, o.Z, o.Rot, o.Scale, o.Len, 0, 0), new GroundModel());
        var box = GroundObjectMath.BoxOf(o, new GroundModel());
        Assert.Equal(part.HalfV, box.HalfWidth); Assert.Equal(part.Z1 - 7f, box.Top, 4);
        Assert.InRange(box.Top, 0.2f, 0.4f); // a 0.45 m log: knee height at most
    }
}
