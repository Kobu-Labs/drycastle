using Drycastle.MapLoader;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ClutterDataTests
{
    const string Json = """
    {"format_version":1,"axes":"map metres","chunk_size_m":250,
     "kinds":{"tuft":{"mesh":"tuft"},"pebble":{"mesh":"pebble_a"}},
     "chunks":{"3_-1":{"tuft":[[750.5,-100,12,0.5,1.0],[760,-90,12,1.0,0.8]],"pebble":[[751,-99,12,0,1]],"unknown":[[0,0,0,0,1]]},
               "0_0":{"tuft":[[1,2,3,4,5]]}}}
    """;

    [Fact]
    public void ParsesGroupsAndSkipsUnlistedKinds()
    {
        var doc = ClutterDoc.Parse(Json);
        Assert.Equal(4, doc.InstanceCount);
        Assert.Equal("pebble_a", doc.KindMesh["pebble"]);
        var g = Assert.Single(doc.Groups, g => g.ChunkX == 3 && g.ChunkY == -1 && g.Kind == "tuft");
        Assert.Equal(new ClutterInstance(750.5f, -100f, 12f, 0.5f, 1f), g.Instances[0]);
        Assert.DoesNotContain(doc.Groups, g => g.Kind == "unknown");
    }

    [Fact]
    public void RejectsUnknownFormatAndBadEntries()
    {
        Assert.Throws<FormatException>(() => ClutterDoc.Parse("""{"format_version":2}"""));
        Assert.Throws<FormatException>(() => ClutterDoc.Parse("""{"format_version":1,"kinds":{"a":{}},"chunks":{"x":{"a":[]}}}"""));
        Assert.Throws<FormatException>(() => ClutterDoc.Parse("""{"format_version":1,"kinds":{"a":{}},"chunks":{"0_0":{"a":[[1,2]]}}}"""));
    }

    [Fact]
    public void CellsGroupByPositionAndKeepEveryInstance()
    {
        var list = new List<ClutterInstance>
        {
            new(1, 1, 0, 0, 1), new(47, 47, 0, 0, 1), new(49, 1, 0, 0, 1), new(-1, -1, 0, 0, 1),
        };
        var cells = ClutterSettings.Cells(list).ToList();
        Assert.Equal(3, cells.Count);
        Assert.Equal(4, cells.Sum(c => c.Count));
        Assert.Contains(cells, c => c.Count == 2); // (1,1) and (47,47)
    }

    [Theory]
    [InlineData(null, 1f)]
    [InlineData("", 1f)]
    [InlineData("0", 0f)]
    [InlineData("off", 0f)]
    [InlineData("0.5", 0.5f)]
    [InlineData("99", 4f)]
    [InlineData("junk", 1f)]
    public void ParseScale(string? value, float expected) => Assert.Equal(expected, ClutterSettings.ParseScale(value));

    [Fact]
    public void TintIsTheOptionalSixthNumber()
    {
        var doc = ClutterDoc.Parse("""{"format_version":1,"kinds":{"tuft":{"mesh":"tuft","tint":true}},"chunks":{"0_0":{"tuft":[[1,2,3,0,1,40],[1,2,3,0,1],[1,2,3,0,1,999]]}}}""");
        var l = doc.Groups[0].Instances;
        Assert.Equal(40, l[0].Tint);
        Assert.Equal(ClutterTint.Neutral, l[1].Tint);
        Assert.Equal(255, l[2].Tint);
    }

    [Fact]
    public void TintFactorsRunFromFreshThroughWhiteToDry()
    {
        Assert.Equal((1f, 1f, 1f), ClutterTint.Rgb(ClutterTint.Neutral));
        var fresh = ClutterTint.Rgb(0);
        var dry = ClutterTint.Rgb(255);
        Assert.Equal(0.80f, fresh.R, 4);
        Assert.Equal(0.55f, dry.B, 4);
        Assert.True(ClutterTint.Rgb(64).G < 1f && ClutterTint.Rgb(64).G > fresh.G);
    }

    [Fact]
    public void EveryKindOfTheVarietyHasARangeBetweenAMatAndTheTallestFlowers()
    {
        foreach (var kind in new[] { "tall_grass", "straw", "clover", "reed", "weed", "dandelion", "daisy", "red_clover", "buttercup", "poppy", "cornflower", "yarrow", "tulip", "sunflower", "spike", "rose" })
            Assert.InRange(ClutterSettings.RangeOf(kind), 40f, 80f);
        Assert.True(ClutterSettings.RangeOf("clover") < ClutterSettings.RangeOf("tall_grass"));
        Assert.True(ClutterSettings.RangeOf("daisy") < ClutterSettings.RangeOf("sunflower"));
    }

    [Fact]
    public void SmallThingsVanishSoonerThanTufts()
        => Assert.True(ClutterSettings.RangeOf("pebble") < ClutterSettings.RangeOf("tuft"));
}
