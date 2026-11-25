using System.Numerics;
using Drycastle.ShooterCore.Buildings;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings;

/// <summary>The doors of hollow houses: interiors.json, which door a player works, and the doors that do not close on a vehicle.</summary>
public class HouseDoorsTests
{
    /// <summary>A front door in a south wall at the origin and, 6 m east of it, a garage door.</summary>
    const string Sample = """
        {
          "version": 1, "axes": "x east, y north, z up",
          "houses": [ { "id": "w1", "rooms": [], "doors": [
            { "id": "d1", "kind": "hinged", "pos": [0, 0, 100], "yaw": 180, "width": 0.95, "height": 2.05, "colour": "#5a4636" },
            { "id": "g1", "kind": "garage", "pos": [6, 0, 100], "yaw": 180, "width": 2.6, "height": 2.2, "colour": "#6b5a48" } ] } ]
        }
        """;

    /// <summary>A map point [x east, y north, z up] as Godot axes.</summary>
    static Vector3 G(float x, float north, float up) => new(x, up, -north);

    [Fact]
    public void The_file_gives_the_doors_in_order()
    {
        var doors = HouseDoors.Parse(Sample);
        Assert.Equal(new[] { HouseDoorKind.Hinged, HouseDoorKind.Garage }, doors.All.Select(d => d.Kind));
        Assert.Equal(G(0, 0, 100), doors.All[0].Doorway.Pos);
        Assert.True(Vector3.Distance(doors.All[0].Doorway.Out, G(0, -1, 0)) < 1e-5f); // out is south
        Assert.Null(HouseDoors.FromJson(""));
        Assert.Null(HouseDoors.FromJsonOrNull("not json", _ => { }));
    }

    [Fact]
    public void A_player_works_the_nearest_door_in_reach_from_either_side()
    {
        var doors = HouseDoors.Parse(Sample);
        Assert.Equal(0, doors.Nearest(G(0, -1, 100)));
        Assert.Equal(0, doors.Nearest(G(0.5f, 1, 100)));
        Assert.Equal(1, doors.Nearest(G(7, -0.8f, 100)));
        Assert.Equal(-1, doors.Nearest(G(0, -2, 100))); // too far out
        Assert.Equal(-1, doors.Nearest(G(2.5f, 0, 100))); // beside both
        Assert.Equal(-1, doors.Nearest(G(0, 0, 104))); // on the roof
    }

    [Fact]
    public void A_door_opens_and_closes_alone_and_not_on_a_vehicle()
    {
        var doors = HouseDoors.Parse(Sample);
        Assert.True(doors.Use(1, vehicleInDoorway: true)); // opening is never blocked
        Assert.True(doors.IsOpen(1));
        Assert.False(doors.IsOpen(0));
        Assert.True(doors.All[1].Doorway.Holds(G(6, 0.5f, 100), 0, 1.03f, 1.93f));
        Assert.False(doors.Use(1, vehicleInDoorway: true));
        Assert.True(doors.IsOpen(1));
        Assert.True(doors.Use(1, vehicleInDoorway: false));
        Assert.False(doors.IsOpen(1));
    }

    [Fact]
    public void The_event_names_the_door_and_its_state()
    {
        Assert.Equal((300, true), HouseDoorCodec.Decode(HouseDoorCodec.Encode(300, true)));
        Assert.Equal((0, false), HouseDoorCodec.Decode(HouseDoorCodec.Encode(0, false)));
        Assert.Null(HouseDoorCodec.Decode(new byte[] { 0, 0, 2 }));
        Assert.Null(HouseDoorCodec.Decode(new byte[] { 1 }));
    }
}
