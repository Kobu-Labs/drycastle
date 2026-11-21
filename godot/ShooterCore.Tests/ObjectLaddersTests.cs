using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class ObjectLaddersTests
{
    const string Json = """{"version":1,"ladders":[{"asset":"a","at":[0,-1.5],"facing":[0,1],"bottom_m":0,"top_m":6}]}""";

    [Fact]
    public void LadderFollowsTheObjectsPositionTurnAndHeight()
    {
        var f = ObjectLaddersFile.Parse(Json);
        var l = f.Place("a", new Vector2(10, 20), 5f, 90f, 1f).Single();
        Assert.Equal(5f, l.Y0);
        Assert.Equal(11f, l.Y1);
        // turned 90 deg: the object's +z (0, 1) goes to (1, 0) on the ground, its -1.5 z to (-1.5, 0)
        Assert.Equal(8.5f, l.Rail.X, 3);
        Assert.Equal(20f, l.Rail.Y, 3);
        Assert.Equal(1f, l.Facing.X, 3);
    }

    [Fact]
    public void OtherAssetsGetNone() => Assert.Empty(ObjectLaddersFile.Parse(Json).Place("b", Vector2.Zero, 0, 0, 1));

    [Fact]
    public void BrokenFilesAreRefused()
    {
        Assert.Throws<DataFileException>(() => ObjectLaddersFile.Parse(Json.Replace("\"top_m\":6", "\"top_m\":0")));
        Assert.Throws<DataFileException>(() => ObjectLaddersFile.Parse(Json.Replace("[0,1]", "[0,3]")));
    }

    [Fact]
    public void ShippedFileParses() => ObjectLaddersFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/object_ladders.json")));
}
