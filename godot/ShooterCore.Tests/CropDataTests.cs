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
}
