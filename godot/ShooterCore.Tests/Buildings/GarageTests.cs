using System.Numerics;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Data;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings;

/// <summary>The bus garage: garage.json's doorway and parking place, who can work the doors, and the doors that do not close on a vehicle.</summary>
public class GarageTests
{
    /// <summary>The doorway in the east wall at the origin (outward is east), 3.6 m wide; the bus stands 7 m inside, nose to the door.</summary>
    const string Sample = """
        {
          "version": 1, "axes": "x east, y north, z up", "id": "w1", "parcel": "P-1",
          "doorway": { "pos": [0, 0, 100], "yaw": 90, "width": 3.6, "height": 3.5 },
          "bay": { "floor_z": 100, "ceiling_z": 104, "footprint": [[-14, -2.5], [0, -2.5], [0, 2.5], [-14, 2.5]] },
          "parking": { "vehicle": "bus", "pos": [-7.2, 0, 100], "yaw": 90 }
        }
        """;

    static readonly Garage Shipped = Garage.Parse(Sample);

    /// <summary>A map point [x east, y north, z up] as Godot axes.</summary>
    static Vector3 G(float x, float north, float up) => new(x, up, -north);

    const float East = -MathF.PI / 2; // the Godot yaw of something that faces east

    [Fact]
    public void The_file_gives_the_doorway_and_the_parking_place()
    {
        Assert.Equal(G(0, 0, 100), Shipped.Doorway.Pos);
        Assert.Equal(1f, Shipped.Doorway.Out.X, 4); // out is east
        Assert.Equal("bus", Shipped.ParkVehicle);
        Assert.Equal(G(-7.2f, 0, 100), Shipped.ParkPos);
        Assert.Equal(East, Shipped.ParkYaw, 4);
        Assert.Null(Garage.FromJson(""));
    }

    [Fact]
    public void A_broken_file_is_refused()
    {
        Assert.Throws<DataFileException>(() => Garage.Parse("""{ "version": 1, "parking": { "vehicle": "bus", "pos": [0, 0, 0], "yaw": 0 } }"""));
        Assert.Throws<DataFileException>(() => Garage.Parse("""{ "version": 1, "doorway": { "pos": [0, 0, 0], "yaw": 0, "width": 3, "height": 3 } }"""));
        Assert.Throws<DataFileException>(() => Garage.Parse("""{ "version": 1, "doorway": { "pos": [0, 0, 0], "yaw": 0, "width": 0, "height": 3 }, "parking": { "vehicle": "bus", "pos": [0, 0, 0], "yaw": 0 } }"""));
        Assert.Throws<DataFileException>(() => Garage.Parse("""{ "version": 1, "doorway": { "pos": [0, 0, 0], "yaw": 0, "width": 3, "height": 3 }, "parking": { "vehicle": "", "pos": [0, 0, 0], "yaw": 0 } }"""));
        var errors = new List<string>();
        Assert.Null(Garage.FromJsonOrNull("""{ "version": 1 }""", errors.Add));
        Assert.Single(errors);
    }

    [Fact]
    public void The_doors_are_worked_from_either_side_close_to_the_doorway()
    {
        Assert.True(Shipped.InReach(G(0.8f, 0, 100))); // in the yard
        Assert.True(Shipped.InReach(G(-0.8f, 1.5f, 100))); // inside, between the doors and the bus
        Assert.True(Shipped.InReach(G(0.5f, 2.2f, 100))); // just beside the doorway
        Assert.False(Shipped.InReach(G(2f, 0, 100))); // too far out
        Assert.False(Shipped.InReach(G(-3f, 2f, 100))); // beside the bus: the key is the bus's
        Assert.False(Shipped.InReach(G(0.5f, 3f, 100))); // along the wall
        Assert.False(Shipped.InReach(G(0.5f, 0, 105))); // on the roof
    }

    [Fact]
    public void A_vehicle_in_the_doorway_is_seen_and_a_parked_one_is_not()
    {
        const float hw = 1.28f, hl = 6.03f;
        Assert.False(Shipped.InDoorway(Shipped.ParkPos, Shipped.ParkYaw, hw, hl)); // parked: the nose is a metre behind the doors
        Assert.True(Shipped.InDoorway(G(-3f, 0, 100), East, hw, hl)); // driving out
        Assert.True(Shipped.InDoorway(G(5.5f, 0, 100), East, hw, hl)); // the tail still in it
        Assert.False(Shipped.InDoorway(G(7f, 0, 100), East, hw, hl)); // out in the yard
        Assert.True(Shipped.InDoorway(G(1f, 0, 100), 0, hw, hl)); // across the doorway, outside against the wall
        Assert.False(Shipped.InDoorway(G(2f, 0, 100), 0, hw, hl)); // across, a little further out
    }

    [Fact]
    public void The_doors_toggle_and_do_not_close_on_a_vehicle()
    {
        var doors = new GarageDoors();
        Assert.False(doors.IsOpen);
        Assert.True(doors.Use(vehicleInDoorway: true)); // opening is never blocked
        Assert.True(doors.IsOpen);
        Assert.False(doors.Use(vehicleInDoorway: true));
        Assert.True(doors.IsOpen);
        Assert.True(doors.Use(vehicleInDoorway: false));
        Assert.False(doors.IsOpen);
    }

    [Fact]
    public void The_event_is_one_byte()
    {
        Assert.True(GarageDoorCodec.Decode(GarageDoorCodec.Encode(true)));
        Assert.False(GarageDoorCodec.Decode(GarageDoorCodec.Encode(false)));
        Assert.Null(GarageDoorCodec.Decode(new byte[] { 2 }));
    }
}
