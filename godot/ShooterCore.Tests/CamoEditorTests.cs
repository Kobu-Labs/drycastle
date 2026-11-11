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
    public void ShareCodeRoundTripsAndIsBase64OfTheSpec()
    {
        var spec = CamoEditor.Randomize(Camo, new Random(7));
        var code = CamoEditor.ShareCode(spec);
        Assert.Equal(40, code.Length); // 30 bytes in base64
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
