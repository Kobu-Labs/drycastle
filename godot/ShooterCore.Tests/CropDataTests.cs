using Drycastle.MapLoader;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class CropDataTests
{
    const string Json = """
    {"format_version":1,"axes":"map metres","chunk_size_m":250,
     "kinds":{"crop_wheat":{"role":"plant","synty":["SM_Prop_Plant_Wheat_Optimised_01","SM_Prop_Plant_Wheat_Optimised_02"]},
              "crop_rapeseed":{"role":"plant","asset":"pack:drycastle-generated-models/rapeseed_plant"},
              "art_tyre":{"role":"artifact","synty":["SM_Prop_Tyre_01"]}},
     "chunks":{"3_-1":{"crop_wheat":[[750.5,-100,12,0.5,1.0],[760,-90,12,1.0,0.8]],"art_tyre":[[751,-99,12,0,1]],"unknown":[[0,0,0,0,1]]}}}
    """;

    [Fact]
    public void ParsesKindsAndGroupsAndSkipsUnlistedKinds()
    {
        var doc = CropDoc.Parse(Json);
        Assert.Equal(3, doc.InstanceCount);
        Assert.True(doc.Kinds["crop_wheat"].IsPlant);
        Assert.Equal(2, doc.Kinds["crop_wheat"].Synty.Count);
        Assert.Equal("pack:drycastle-generated-models/rapeseed_plant", doc.Kinds["crop_rapeseed"].Asset);
        Assert.False(doc.Kinds["art_tyre"].IsPlant);
        Assert.DoesNotContain(doc.Groups, g => g.Kind == "unknown");
    }

    [Fact]
    public void RejectsUnknownFormatAndBadEntries()
    {
        Assert.Throws<FormatException>(() => CropDoc.Parse("""{"format_version":2}"""));
        Assert.Throws<FormatException>(() => CropDoc.Parse("""{"format_version":1,"kinds":{"a":{}},"chunks":{"0_0":{"a":[[1,2]]}}}"""));
    }

    [Fact]
    public void VariantIsStableAndInRange()
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            var inst = new ClutterInstance(i * 0.7f, i * 1.3f, 0, 0, 1);
            var v = CropSettings.Variant(inst, 4);
            Assert.InRange(v, 0, 3);
            Assert.Equal(v, CropSettings.Variant(inst, 4));
            seen.Add(v);
        }
        Assert.Equal(4, seen.Count);
        Assert.Equal(0, CropSettings.Variant(new ClutterInstance(5, 5, 0, 0, 1), 1));
    }

    [Fact]
    public void EveryShippedKindHasAPlaceholder()
    {
        var doc = CropDoc.Parse(Json);
        foreach (var (name, spec) in doc.Kinds) Assert.True(CropPlaceholder.For(name, spec).Height > 0);
        Assert.True(CropPlaceholder.For("art_unlisted", new CropKind("artifact", null, Array.Empty<string>())).Height > 0);
    }

    [Fact]
    public void PlantsThinOutOverTheBandAndNoneStandsAtTheRadius()
    {
        const float r = CropSettings.PlantRangeM, band = CropSettings.PlantFadeM;
        foreach (var t in new[] { 0f, 0.25f, 0.5f, 0.999f })
        {
            Assert.Equal(1f, CropSettings.Grow(r - band, t, r, band));        // whole where the band starts
            Assert.Equal(0f, CropSettings.Grow(r, t, r, band));               // gone at the radius
            Assert.Equal(0f, CropSettings.Grow(r + 30f, t, r, band));
            var last = 1f;
            for (var d = r - band; d <= r; d += 1f)
            {
                var g = CropSettings.Grow(d, t, r, band);
                Assert.True(g <= last);                                       // never grows going away
                last = g;
            }
        }
        // in the middle of the band some plants stand and some do not: the crop thins, it does not end
        var mid = r - band / 2f;
        Assert.Equal(1f, CropSettings.Grow(mid, 0.9f, r, band));
        Assert.Equal(0f, CropSettings.Grow(mid, 0.1f, r, band));
    }

    [Fact]
    public void FarTuftsComeInWhereThePlantsGoOut()
    {
        const float r = CropSettings.PlantRangeM, band = CropSettings.PlantFadeM;
        foreach (var t in new[] { 0f, 0.3f, 0.7f, 0.999f })
        {
            Assert.Equal(0f, CropSettings.FarGrow(r - band, t, r, band));     // no tuft among whole plants
            Assert.Equal(1f, CropSettings.FarGrow(r, t, r, band));            // every tuft stands where the last plant is gone
            Assert.Equal(1f, CropSettings.FarGrow(r + 200f, t, r, band));
            // never both gone: something stands at every distance
            for (var d = r - band; d <= r; d += 1f)
                Assert.True(CropSettings.Grow(d, t, r, band) + CropSettings.FarGrow(d, t, r, band) > 0f);
        }
    }

    [Fact]
    public void AboutOnePlantInFarEveryIsATuft()
    {
        var n = 0;
        for (var x = 0; x < 100; x++)
            for (var y = 0; y < 100; y++)
                if (CropSettings.IsFar(new ClutterInstance(700f + x * 0.8f, -300f + y * 0.8f, 0f, 0f, 1f))) n++;
        Assert.InRange(n, 10000 / CropSettings.FarEvery * 0.85, 10000 / CropSettings.FarEvery * 1.15);
    }
}
