using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The camouflage customiser's logic (ticket #141): parameter list, randomize, presets, share codes, per-slot wire.</summary>
public class CamoEditorTests
{
    static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    static CamoFile Camo => Data.Camo;

    [Fact]
    public void EveryParameterReadsWhatItWritesAndStaysInRange()
    {
        var parms = CamoEditor.Params(Camo);
        Assert.Equal(parms.Count, parms.Select(p => p.Key).Distinct().Count());
        foreach (var p in parms)
        {
            Assert.True(p.Min <= p.Max, p.Key);
            var written = p.Set(CamoSpec.Default, p.Max);
            Assert.Equal(p.Max, p.Get(written));
            Assert.Equal(p.Min, p.Get(p.Set(CamoSpec.Default, p.Min)));
            Assert.Null(written.Validate(Camo));
            // out-of-range writes are clamped, never produce an invalid spec
            Assert.Null(p.Set(CamoSpec.Default, int.MaxValue).Validate(Camo));
            Assert.Null(p.Set(CamoSpec.Default, int.MinValue).Validate(Camo));
        }
    }

    [Fact]
    public void ParametersCoverEveryField()
    {
        // the customiser must reach every field: a field nobody can tweak is a broken promise in the ticket
        var parms = CamoEditor.Params(Camo);
        var groups = parms.Select(p => p.Group).Distinct().ToList();
        Assert.Equal(4, groups.Count);
        Assert.Contains(parms, p => p.Key == "suit_base" && p.Kind == CamoParamKind.Colour);
        Assert.Contains(parms, p => p.Key == "tuft_layers" && p.Max == Camo.TuftMaxLayers);
        Assert.Contains(parms, p => p.Key == "face_colour_c" && p.Group == CamoGroup.FacePaint);

        var probe = CamoSpec.Default;
        foreach (var p in parms) probe = p.Set(probe, p.Get(probe) == p.Max ? p.Min : p.Max);
        Assert.NotEqual(CamoSpec.Default, probe);
    }

    [Fact]
    public void RandomizeAlwaysProducesAValidLook()
    {
        var rng = new Random(141);
        for (var i = 0; i < 200; i++)
            Assert.Null(CamoEditor.Randomize(Camo, rng).Validate(Camo));
    }

    [Fact]
    public void RandomizeStaysMutedAndNeverLeavesFoliageOrPaintHalfOn()
    {
        var rng = new Random(9);
        for (var i = 0; i < 200; i++)
        {
            var s = CamoEditor.Randomize(Camo, rng);
            // camouflage, not neon: no channel is far above the others
            foreach (var c in new[] { s.SuitBase, s.SuitSecondary })
                Assert.True(Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 110, $"{c}");
            // foliage is either off or visible somewhere
            Assert.Equal(s.TuftLayers > 0, s.TuftHead + s.TuftChest + s.TuftArms + s.TuftLegs > 0);
        }
    }

    [Fact]
    public void SwitchingFoliageOnAlwaysShowsSome()
    {
        var layers = CamoEditor.Params(Camo).Single(p => p.Key == "tuft_layers");
        var on = layers.Set(CamoSpec.Default, 1);
        Assert.Equal(CamoEditor.StarterTuftDensity, on.TuftChest);
        Assert.Equal(CamoEditor.StarterTuftDensity, on.TuftHead);
        // densities the player already chose are kept
        var chosen = CamoSpec.Default with { TuftArms = 40 };
        Assert.Equal(0, layers.Set(chosen, 2).TuftChest);
        Assert.Equal(40, layers.Set(chosen, 2).TuftArms);
        // changing the thickness of foliage that is already on leaves the densities alone
        Assert.Equal(on with { TuftLayers = 3 }, layers.Set(on, 3));
    }

    [Fact]
    public void InertControlsAreNamedWithAReason()
    {
        var plain = CamoSpec.Default;
        Assert.Equal("foliage_off", CamoEditor.Inert("tuft_size", plain, Camo));
        Assert.Equal("foliage_off", CamoEditor.Inert("vegetation", plain, Camo));
        Assert.Null(CamoEditor.Inert("tuft_layers", plain, Camo));
        Assert.Null(CamoEditor.Inert("tuft_size", plain with { TuftLayers = 1 }, Camo));
        Assert.Equal("face_off", CamoEditor.Inert("face_colour_a", plain, Camo));
        Assert.Null(CamoEditor.Inert("face_coverage", plain, Camo));
        Assert.Null(CamoEditor.Inert("face_pattern", plain with { FaceCoverage = 1 }, Camo));
        var bare = (byte)Camo.Bodies.ToList().FindIndex(b => b.Headgear.Count == 0);
        var helmet = (byte)Camo.Bodies.ToList().FindIndex(b => b.Headgear.Count > 0);
        Assert.Equal("no_headgear", CamoEditor.Inert("headgear", plain with { Body = bare }, Camo));
        Assert.Null(CamoEditor.Inert("headgear", plain with { Body = helmet }, Camo));
        Assert.Null(CamoEditor.Inert("suit_base", plain, Camo));
    }

    [Fact]
    public void ShareCodeRoundTripsAndIsBase64OfTheSpec()
    {
        var spec = CamoEditor.Randomize(Camo, new Random(7));
        var code = CamoEditor.ShareCode(spec);
        Assert.Equal(44, code.Length); // 33 bytes in base64
        Assert.Equal(spec, CamoEditor.ParseShareCode(code, Camo, out var reason));
        Assert.Null(reason);
        Assert.Equal(spec, CamoEditor.ParseShareCode("  " + code + "\n", Camo, out _)); // pasted with whitespace
    }

    [Theory]
    [InlineData("", "paste")]
    [InlineData("not base64!!", "not a share code")]
    [InlineData("AAAA", "different version")]
    public void ShareCodeRejectsBadTextWithAReason(string code, string reasonPart)
    {
        Assert.Null(CamoEditor.ParseShareCode(code, Camo, out var reason));
        Assert.Contains(reasonPart, reason);
    }

    [Fact]
    public void ShareCodeRejectsAnInvalidLookWithTheValidatorsReason()
    {
        var bad = CamoSpec.Default with { SuitPattern = (byte)Camo.SuitPatterns.Count };
        Assert.Null(CamoEditor.ParseShareCode(CamoEditor.ShareCode(bad), Camo, out var reason));
        Assert.Equal(bad.Validate(Camo), reason);
    }

    [Fact]
    public void PresetsReplaceOnlyTheirPart()
    {
        var draft = CamoSpec.Default with { FaceCoverage = 33, FacePattern = 0 };
        var suit = Camo.SuitPresets[0].Spec;
        var afterSuit = CamoEditor.WithSuitPreset(draft, suit);
        Assert.Equal(suit.SuitBase, afterSuit.SuitBase);
        Assert.Equal(suit.TuftLayers, afterSuit.TuftLayers);
        Assert.Equal(33, afterSuit.FaceCoverage); // face paint untouched

        var face = Camo.FacePresets[0].Spec;
        var afterFace = CamoEditor.WithFacePreset(draft, face);
        Assert.Equal(face.FaceCoverage, afterFace.FaceCoverage);
        Assert.Equal(draft.SuitBase, afterFace.SuitBase); // suit untouched
    }

    [Fact]
    public void ProfileCarriesOneLookPerSlotAndTheSaveNamesASlot()
    {
        var camos = Enumerable.Range(0, Loadout.SlotCount).Select(i => CamoEditor.Randomize(Camo, new Random(i))).ToArray();
        var profile = new ProfileMsg("bob", 0, 1, 2, new[] { Loadout.Empty(0) }, Array.Empty<Unlock>(), camos);
        var got = Assert.IsType<ProfileMsg>(Wire.DecodeReliable(Wire.Encode(profile)));
        Assert.Equal(camos, got.Camos);

        var save = new SaveCamoMsg(4, camos[1]);
        Assert.Equal(save, Wire.DecodeReliable(Wire.Encode(save)));

        var full = Wire.Encode(profile);
        Assert.Null(Wire.DecodeReliable(full.AsSpan(0, full.Length - 1))); // truncated camo block
    }
}
