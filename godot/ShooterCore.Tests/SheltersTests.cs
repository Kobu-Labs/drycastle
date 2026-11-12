using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class SheltersTests
{
    /// <summary>
    /// Two bunkers in local metres (x east, y north). A: a 4 x 3 m room at x 0..4, y 0..3, heights 100..102.4, with a second
    /// floor under it (97.6..100). B: a room at x 20..24. Godot ground axes are x east, z = -north, so A covers z -3..0.
    /// </summary>
    const string Json = """
    {
      "version": 1,
      "bunkers": [
        { "id": "wA", "parcel": "P-0001", "floors": [
          { "level": 0, "polygon": [[0,0],[4,0],[4,3],[0,3]], "z0": 100.0, "z1": 102.4 },
          { "level": -1, "polygon": [[0,0],[4,0],[4,3],[0,3]], "z0": 97.6, "z1": 100.0 } ] },
        { "id": "wB", "parcel": "P-0002", "floors": [
          { "level": 0, "polygon": [[20,0],[24,0],[24,3],[20,3]], "z0": 100.0, "z1": 102.4 } ] }
      ]
    }
    """;

    static readonly Vector3 InA = new(2, 101, -1.5f), UnderA = new(2, 99, -1.5f), InB = new(22, 101, -1.5f);
    static readonly Vector3 Open = new(10, 101, -1.5f), OnRoofA = new(2, 103, -1.5f);

    [Fact]
    public void RingsAreFlippedToGodotAxesAndBoundedInHeight()
    {
        var s = Shelters.Parse(Json);
        Assert.Equal(3, s.Rooms.Count);
        Assert.Equal(0, s.RoomAt(InA));
        Assert.Equal(1, s.RoomAt(UnderA));
        Assert.Equal(2, s.RoomAt(InB));
        Assert.Equal(-1, s.RoomAt(Open));
        Assert.Equal(-1, s.RoomAt(OnRoofA));
        Assert.Equal(-1, s.RoomAt(new Vector3(2, 101, 1.5f))); // north is -z: +z is south of the room
    }

    [Fact]
    public void ABlastOutsideDoesNotReachARoomNorTheOtherWayRound()
    {
        var s = Shelters.Parse(Json);
        Assert.False(s.Reaches(Open, InA));
        Assert.False(s.Reaches(OnRoofA, InA)); // the attack missile on the roof
        Assert.False(s.Reaches(InA, Open));
    }

    [Fact]
    public void ABlastReachesBodiesInItsOwnRoomOnly()
    {
        var s = Shelters.Parse(Json);
        Assert.True(s.Reaches(InA, new Vector3(3.5f, 100.9f, -2.5f)));
        Assert.False(s.Reaches(InA, UnderA)); // the floor below is another room
        Assert.False(s.Reaches(InA, InB));
        Assert.True(s.Reaches(Open, new Vector3(-30, 100, 40))); // in the open, nothing changes
    }

    [Fact]
    public void AMapWithoutBunkersSheltersNobody()
    {
        Assert.True(Shelters.Empty.Reaches(Open, InA));
        Assert.Equal(-1, Shelters.Empty.RoomAt(InA));
    }

    [Theory]
    [InlineData("{", "not JSON")]
    [InlineData("""{"version": 2, "bunkers": []}""", "version")]
    [InlineData("""{"version": 1}""", "bunkers")]
    [InlineData("""{"version": 1, "bunkers": [{"floors": []}]}""", "without id")]
    [InlineData("""{"version": 1, "bunkers": [{"id": "w1", "floors": [{"polygon": [[0,0],[1,0]], "z0": 0, "z1": 1}]}]}""", "polygon")]
    [InlineData("""{"version": 1, "bunkers": [{"id": "w1", "floors": [{"polygon": [[0,0],[1,0],[1,1]], "z0": 2, "z1": 1}]}]}""", "z1 <= z0")]
    public void ABadFileIsRefusedWithItsName(string json, string expected)
    {
        var e = Assert.Throws<DataFileException>(() => Shelters.Parse(json));
        Assert.Contains("bunkers.json", e.Message);
        Assert.Contains(expected, e.Message);
    }
}

public class LadderParseTests
{
    [Fact]
    public void LaddersAreParsedIntoGodotAxes()
    {
        const string json = """
            {"version":1,"bunkers":[{"id":"w1","parcel":"P-1",
              "floors":[{"level":0,"polygon":[[0,0],[4,0],[4,4],[0,4]],"z0":-0.15,"z1":2.3}],
              "ladders":[{"x":1.0,"y":2.0,"dx":0.0,"dy":1.0,"z0":-2.4,"z1":0.04}],"hatch":[]}]}
            """;
        var l = Shelters.Parse(json).Ladders;
        Assert.Equal(1, l.Count);
        Assert.Equal(new Vector2(1f, -2f), l[0].Rail); // y north becomes z = -north
        Assert.Equal(new Vector2(0f, -1f), l[0].Facing);
        Assert.Equal(-2.4f, l[0].Y0);
        Assert.Equal(0.04f, l[0].Y1);
    }

    [Fact]
    public void ABunkerWithoutLaddersOrABrokenOneIsHandled()
    {
        Assert.Equal(0, Shelters.Parse("""{"version":1,"bunkers":[{"id":"w1","floors":[{"polygon":[[0,0],[1,0],[1,1]],"z0":0,"z1":2}]}]}""").Ladders.Count);
        Assert.Throws<DataFileException>(() => Shelters.Parse("""{"version":1,"bunkers":[{"id":"w1","floors":[],"ladders":[{"x":0,"y":0,"dx":3,"dy":0,"z0":-2,"z1":0}]}]}"""));
        Assert.Equal(0, Shelters.FromJsonOrEmpty("not json", _ => { }).Ladders.Count);
        Assert.Same(Shelters.Empty, Shelters.FromJson(""));
    }
}
