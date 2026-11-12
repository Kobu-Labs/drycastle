using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The vehicle seat switch: moving between seats, its wire format, and the schematic's layout.</summary>
public class SeatSwitchTests
{
    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Type(string id) => Shipped().Vehicles.Single(v => v.Id == id);

    // ---- the rule ----

    [Fact]
    public void ASeatedPlayerMovesToAFreeSeatAndTheVehicleStaysOccupied()
    {
        var s = new VehicleSeat(2);
        Assert.True(s.TryEnter(7, 0));
        Assert.True(s.TrySwitch(7, 1));
        Assert.True(s.IsFree(0));
        Assert.Equal(7, s.Occupant(1));
        Assert.Equal(1, s.SeatOf(7));
        Assert.True(s.Occupied);
        Assert.Equal(0, s.Driver); // the driver's seat is empty: the vehicle gets no input
    }

    [Fact]
    public void ASeatCanBeTakenBackAndForth()
    {
        var s = new VehicleSeat(2);
        s.TryEnter(7, 0);
        Assert.True(s.TrySwitch(7, 1));
        Assert.True(s.TrySwitch(7, 0));
        Assert.Equal(7, s.Driver);
    }

    [Fact]
    public void ARefusedSwitchChangesNothing()
    {
        var s = new VehicleSeat(3);
        s.TryEnter(7, 0);
        s.TryEnter(8, 1);
        Assert.False(s.TrySwitch(7, 1));   // another player's seat
        Assert.False(s.TrySwitch(7, 0));   // their own seat
        Assert.False(s.TrySwitch(7, 3));   // no such seat
        Assert.False(s.TrySwitch(7, -1));
        Assert.False(s.TrySwitch(9, 2));   // not seated in this vehicle
        Assert.False(s.TrySwitch(0, 2));
        Assert.Equal(7, s.Occupant(0));
        Assert.Equal(8, s.Occupant(1));
        Assert.True(s.IsFree(2));
    }

    // ---- the wire ----

    [Fact]
    public void ASwitchRequestRoundTripsAndRejectsABadPayload()
    {
        Assert.Equal(2, SeatCodec.DecodeSwitch(SeatCodec.EncodeSwitch(2)));
        Assert.Null(SeatCodec.DecodeSwitch(new byte[] { VehicleSeat.MaxSeats }));
        Assert.Null(SeatCodec.DecodeSwitch(ReadOnlySpan<byte>.Empty));
        Assert.Null(SeatCodec.DecodeSwitch(new byte[] { 0, 0 }));
    }

    [Fact]
    public void ASeatStateRoundTrips()
    {
        var back = SeatCodec.DecodeState(SeatCodec.EncodeState(0x4005, new ushort[] { 3, 0, 12 }));
        Assert.NotNull(back);
        Assert.Equal(0x4005, back!.CarId);
        Assert.Equal(new ushort[] { 3, 0, 12 }, back.Occupants);
    }

    [Fact]
    public void ASeatStateOfTheWrongSizeIsRejected()
    {
        var good = SeatCodec.EncodeState(1, new ushort[] { 1, 2 });
        Assert.Null(SeatCodec.DecodeState(good.AsSpan(0, good.Length - 1)));
        Assert.Null(SeatCodec.DecodeState(new byte[] { 1, 0, 0 }));                  // no seats
        Assert.Null(SeatCodec.DecodeState(new byte[] { 1, 0, VehicleSeat.MaxSeats + 1 })); // more than a vehicle has
    }

    // ---- the schematic ----

    [Fact]
    public void AVehicleWithoutASeatsListHasOneSeatNumberedOne()
    {
        var car = Type("car"); // no "seats" in vehicles.json: the one default driver seat
        var l = SeatSchematic.Layout(car, 184, 168, 12, 16);
        var seat = Assert.Single(l.Seats);
        Assert.Equal(1, seat.Number);
        Assert.Equal("driver", seat.Role);
    }

    [Fact]
    public void SeatsAreNumberedFromOneInTheOrderOfTheSeatsList()
    {
        var l = SeatSchematic.Layout(Type("amphibious"), 184, 168, 12, 16);
        Assert.Equal(new[] { 1, 2, 3, 4 }, l.Seats.Select(s => s.Number));
        Assert.Equal("driver", l.Seats[0].Role);
    }

    [Fact]
    public void TheBodyIsDrawnToScaleAndCentred()
    {
        var def = Type("flak");
        var l = SeatSchematic.Layout(def, 184, 168, 12, 16);
        Assert.Equal(184 / 2f, l.BodyLeft + l.BodyWidth / 2, 3);
        Assert.Equal(168 / 2f, l.BodyTop + l.BodyHeight / 2, 3);
        Assert.Equal(def.HalfWidth / def.HalfLength, l.BodyWidth / l.BodyHeight, 3); // the proportions of the vehicle
        Assert.InRange(l.BodyTop, 12 - 0.01, 168); // inside the padding
        Assert.True(l.BodyHeight <= 168 - 24 + 0.01 && l.BodyWidth <= 184 - 24 + 0.01);
    }

    [Fact]
    public void ForwardIsUpTheFlaksDriverIsAheadOfItsGunner()
    {
        var l = SeatSchematic.Layout(Type("flak"), 184, 168, 12, 16);
        Assert.True(l.Seats[0].Y < l.Seats[1].Y); // the cab is forward, the turret at the back
        Assert.True(l.Seats[0].X < l.Seats[1].X); // the driver sits left of the centre line (offset x -0.5), the turret on it
    }

    [Theory]
    [InlineData("car")]
    [InlineData("motorcycle")]
    [InlineData("bicycle")]
    [InlineData("jeep")]
    [InlineData("quad")]
    [InlineData("amphibious")]
    [InlineData("boat")]
    [InlineData("flak")]
    [InlineData("bus")]
    public void EveryShippedVehicleKeepsItsMarkersApartAndInsideTheBody(string id)
    {
        const float r = 16;
        var l = SeatSchematic.Layout(Type(id), 184, 168, 12, r);
        foreach (var s in l.Seats)
        {
            Assert.InRange(s.X, l.BodyLeft + Math.Min(r, l.BodyWidth / 2) - 0.01, l.BodyLeft + l.BodyWidth - Math.Min(r, l.BodyWidth / 2) + 0.01);
            Assert.InRange(s.Y, l.BodyTop + r - 0.01, l.BodyTop + l.BodyHeight - r + 0.01);
        }
        for (var i = 0; i < l.Seats.Count; i++)
            for (var j = i + 1; j < l.Seats.Count; j++)
            {
                var d = MathF.Sqrt(MathF.Pow(l.Seats[i].X - l.Seats[j].X, 2) + MathF.Pow(l.Seats[i].Y - l.Seats[j].Y, 2));
                Assert.True(d >= 2 * r, $"{id}: seats {i + 1} and {j + 1} overlap ({d:F1} px apart)");
            }
    }

    [Fact]
    public void TheJeepsRoofGunnerIsPushedClearOfItsDriver()
    {
        var jeep = Type("jeep"); // driver at the centre, gunner 0.25 m behind it: under a marker's width on the panel
        var l = SeatSchematic.Layout(jeep, 184, 168, 12, 16);
        Assert.True(l.Seats[0].Y < l.Seats[1].Y); // the order along the vehicle is kept: the driver in front
    }
}
