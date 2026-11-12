using Suchohrad.MapLoader;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

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
    public void SmallThingsVanishSoonerThanTufts()
        => Assert.True(ClutterSettings.RangeOf("pebble") < ClutterSettings.RangeOf("tuft"));
}
