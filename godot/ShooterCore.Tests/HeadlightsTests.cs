using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class HeadlightsTests
{
    static VehiclesFile Parse(string lights)
    {
        var json = "{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 1" + (lights == "" ? "" : ", \"lights\": " + lights) + " } ] }";
        return DataFile.Parse<VehiclesFile>(json, "test");
    }

    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));

    [Fact]
    public void LightsBlockParsesIntoOffsetsAndBeam()
    {
        var f = VehiclesFile.Validate(Parse("{ \"head\": [[-0.7, 0.6, -2.1], [0.7, 0.6, -2.1]], \"tail\": [[0, 0.5, 2]], \"beam_range\": 40, \"beam_angle\": 26 }"));
        var l = f.Vehicles[0].Lights!;
        Assert.Equal(2, l.Head.Count);
        Assert.Equal(new float[] { -0.7f, 0.6f, -2.1f }, l.Head[0]);
        Assert.Single(l.Tail);
        Assert.Equal(40, l.BeamRange);
        Assert.Equal(26, l.BeamAngle);
    }

    [Fact]
    public void NoLightsBlockMeansNoLamps() => Assert.Null(VehiclesFile.Validate(Parse("")).Vehicles[0].Lights);

    [Fact]
    public void TailIsOptionalButHeadlampsAreNot()
    {
        var f = VehiclesFile.Validate(Parse("{ \"head\": [[0, 0.9, -1]] }"));
        Assert.Empty(f.Vehicles[0].Lights!.Tail);
        Assert.Equal(30, f.Vehicles[0].Lights!.BeamRange); // default reach
    }

    [Theory]
    [InlineData("{ \"head\": [] }")]                                              // no headlamp
    [InlineData("{ \"head\": [[0,0,0],[0,0,0],[0,0,0],[0,0,0],[0,0,0]] }")]      // five headlamps
    [InlineData("{ \"head\": [[0, 0.6]] }")]                                     // two numbers, not [x, y, z]
    [InlineData("{ \"head\": [[0, 0.6, -1]], \"tail\": [[0,0,0],[0,0,0],[0,0,0],[0,0,0],[0,0,0]] }")]
    [InlineData("{ \"head\": [[0, 0.6, -1]], \"beam_range\": 0 }")]
    [InlineData("{ \"head\": [[0, 0.6, -1]], \"beam_angle\": 90 }")]
    public void BadLightsAreRejected(string lights) =>
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Parse(lights)));

    [Fact]
    public void ShippedVehiclesHaveLamps()
    {
        var f = Shipped();
        foreach (var id in new[] { "car", "motorcycle", "bicycle" })
        {
            var l = f.Vehicles.Single(v => v.Id == id).Lights;
            Assert.NotNull(l);
            Assert.NotEmpty(l!.Head);
            Assert.NotEmpty(l.Tail);
        }
        Assert.Equal(2, f.Vehicles.Single(v => v.Id == "car").Lights!.Head.Count); // a pair
    }

    [Theory]
    [InlineData(2.0, true)]    // small hours: dark
    [InlineData(12.0, false)]  // noon
    [InlineData(15.0, false)]  // afternoon
    [InlineData(18.0, true)]   // dusk
    [InlineData(22.0, true)]   // night
    public void DriverGettingInAtNightSwitchesLightsOn(double hour, bool expected) =>
        Assert.Equal(expected, Headlights.OnEnterAt(hour));

    [Fact]
    public void ToggleFlipsTheState()
    {
        Assert.True(Headlights.Toggle(false));
        Assert.False(Headlights.Toggle(true));
    }

    [Fact]
    public void KeyEdgeFiresOncePerPress()
    {
        var wasDown = false;
        // held for three ticks, released, pressed again
        var presses = new[] { true, true, true, false, true }.Select(d => Headlights.Edge(d, ref wasDown)).ToArray();
        Assert.Equal(new[] { true, false, false, false, true }, presses);
    }

    [Fact]
    public void HeadlightsButtonDoesNotCollideWithOtherButtons()
    {
        var others = Enum.GetValues<MoveButtons>().Where(b => b != MoveButtons.Headlights && b != MoveButtons.None).Aggregate(0, (acc, b) => acc | (int)b);
        Assert.Equal(0, (int)MoveButtons.Headlights & others);
        Assert.True((int)MoveButtons.Headlights <= ushort.MaxValue); // travels in the 16 bit input buttons
    }

    [Fact]
    public void LightsBitRidesInTheDamageByteWithoutChangingTheStage()
    {
        var pos = new Vector3(1, 0, 2);
        var on = VehicleRecord.Make(7, pos, 0.5f, 3, VehicleRecord.Pack(2, 5), stage: 2, lights: true);
        Assert.True(VehicleRecord.Lights(on));
        Assert.Equal(2, VehicleRecord.Stage(on));
        Assert.Equal(3, VehicleRecord.Driver(on));
        Assert.Equal(5, VehicleRecord.Colour(on));
        Assert.Equal(2, VehicleRecord.Kind(on));
        Assert.Equal(EntityState.InVehicle, on.State);

        var off = VehicleRecord.Make(7, pos, 0.5f, 0, VehicleRecord.Pack(2, 5), stage: 3);
        Assert.False(VehicleRecord.Lights(off));
        Assert.Equal(3, VehicleRecord.Stage(off));
        Assert.Equal(EntityState.Alive, off.State);
    }
}
