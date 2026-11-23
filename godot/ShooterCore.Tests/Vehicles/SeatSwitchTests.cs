using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

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

    [Fact]
    public void AFiveSeatVehicleSwitchesToItsLastSeatAndBack()
    {
        var s = new VehicleSeat(5);
        for (ushort p = 1; p <= 4; p++) s.TryEnter(p, p - 1);
        Assert.True(s.TrySwitch(1, 4));    // the driver to the fifth seat (key 5)
        Assert.True(s.IsFree(0));
        Assert.Equal(1, s.Occupant(4));
        Assert.False(s.TrySwitch(2, 4));   // taken
        Assert.False(s.TrySwitch(2, 5));   // no sixth seat
        Assert.True(s.TrySwitch(1, 0));
        Assert.True(s.IsFree(4));
    }

    [Fact]
    public void ADeadRidersCorpseBlocksTheFifthSeatAsAnyOther()
    {
        var s = new VehicleSeat(5);
        s.TryEnter(1, 0);
        s.Block(4);
        Assert.False(s.TrySwitch(1, 4));
        Assert.True(s.HoldsCorpse(4));
        Assert.False(s.IsFree(4));
        s.Unblock(4);
        Assert.True(s.TrySwitch(1, 4));
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
    public void AFiveSeatRequestAndStateRoundTrip()
    {
        Assert.Equal(4, SeatCodec.DecodeSwitch(SeatCodec.EncodeSwitch(4)));
        Assert.Null(SeatCodec.DecodeSwitch(SeatCodec.EncodeSwitch(5)));
        var back = SeatCodec.DecodeState(SeatCodec.EncodeState(0x4001, new ushort[] { 3, 0, 12, 40000, 7 }));
        Assert.NotNull(back);
        Assert.Equal(new ushort[] { 3, 0, 12, 40000, 7 }, back!.Occupants);
        Assert.Null(SeatCodec.DecodeState(SeatCodec.EncodeState(1, new ushort[] { 1, 2, 3, 4, 5, 6 }))); // six
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
        var car = Type("quad"); // no "seats" in vehicles.json: the one default driver seat
        var l = SeatSchematic.Layout(car, 184, 168, 12, 16);
        var seat = Assert.Single(l.Seats);
        Assert.Equal(1, seat.Number);
        Assert.Equal("driver", seat.Role);
    }

    [Fact]
    public void SeatsAreNumberedFromOneInTheOrderOfTheSeatsList()
    {
        var l = SeatSchematic.Layout(Type("amphibious"), 184, 168, 12, 16);
        Assert.Equal(new[] { 1, 2, 3 }, l.Seats.Select(s => s.Number));
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
    [InlineData("truck")]
    [InlineData("quad")]
    [InlineData("amphibious")]
    [InlineData("flak")]
    [InlineData("bus")]
    [InlineData("humvee")]
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
    public void AFiveSeatVehicleGetsFiveNumberedMarkersThatDoNotOverlap()
    {
        const float r = 16;
        var def = new VehicleDef { Id = "x", HalfWidth = 1.1f, HalfLength = 2.5f };
        def.Seats = new List<VehicleSeatDef>
        {
            new() { Role = "driver", Offset = new float[] { -0.5f, 1, -0.6f } },
            new() { Role = "passenger", Offset = new float[] { 0.5f, 1, -0.6f } },
            new() { Role = "passenger", Offset = new float[] { -0.5f, 1, 0.5f } },
            new() { Role = "passenger", Offset = new float[] { 0.5f, 1, 0.5f } },
            new() { Role = "gunner", Offset = new float[] { 0, 1.6f, 0.5f } }, // the roof gunner sits on top of the rear seats
        };
        var l = SeatSchematic.Layout(def, 184, 168, 12, r);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, l.Seats.Select(s => s.Number));
        for (var i = 0; i < 5; i++)
            for (var j = i + 1; j < 5; j++)
            {
                var d = MathF.Sqrt(MathF.Pow(l.Seats[i].X - l.Seats[j].X, 2) + MathF.Pow(l.Seats[i].Y - l.Seats[j].Y, 2));
                Assert.True(d >= 2 * r, $"seats {i + 1} and {j + 1} overlap ({d:F1} px apart)");
            }
    }

    [Fact]
    public void ARoofGunnerIsPushedClearOfItsDriver()
    {
        // driver at the centre, gunner 0.25 m behind it: under a marker's width on the panel (the old jeep's seats, gone with its gun, #325)
        var def = new VehicleDef { Id = "x", HalfWidth = 1.28f, HalfLength = 2.3f, Height = 2.2f };
        def.Seats = new List<VehicleSeatDef> { new() { Role = "driver", Offset = new float[] { 0, 0, 0 } }, new() { Role = "gunner", Offset = new float[] { 0, 1.25f, 0.25f } } };
        var l = SeatSchematic.Layout(def, 184, 168, 12, 16);
        Assert.True(l.Seats[0].Y < l.Seats[1].Y); // the order along the vehicle is kept: the driver in front
    }

    // ---- the outlines (#334) ----

    static VehicleOutlinesFile Outlines() => VehicleOutlinesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicle_outlines.json"));
    // the panel's drawing area (SeatSchematicView.W x H, Pad, Radius)
    const float PanelW = 184, PanelH = 216, PanelPad = 12, PanelR = 16;

    static IEnumerable<VehicleDef> Seated() => Shipped().Vehicles.Where(v => v.Seats.Count > 0);

    [Fact]
    public void EveryShippedVehicleWithSeatsHasAnOutlineOfAUsablePointCount()
    {
        var o = Outlines();
        foreach (var v in Seated())
        {
            var poly = o.For(v.Id);
            Assert.True(poly != null, $"{v.Id}: no outline (python3 scripts/vehicles/make_vehicle_outlines.py)");
            Assert.InRange(poly!.Count, 20, 60);
        }
    }

    [Fact]
    public void EverySeatLiesInsideItsVehiclesOutlineOrWithinAStandingRidersMargin()
    {
        var o = Outlines();
        foreach (var v in Seated())
            for (var i = 0; i < v.Seats.Count; i++)
            {
                var s = v.Seats[i].Offset;
                var poly = o.For(v.Id)!;
                var ok = OutlineGeometry.Contains(poly, s[0], s[2]) || OutlineGeometry.EdgeDistance(poly, s[0], s[2]) <= 0.3f;
                Assert.True(ok, $"{v.Id}: seat {i + 1} at x {s[0]}, z {s[2]} is outside the outline");
            }
    }

    [Fact]
    public void AnOutlineFitsThePanelAndSeatsKeepTheirRealPlaceWhenNothingTouches()
    {
        var car = Type("car");
        var poly = Outlines().For("car")!;
        var l = SeatSchematic.Layout(car, PanelW, PanelH, PanelPad, PanelR, poly);
        Assert.NotNull(l.Outline);
        Assert.Equal(poly.Count, l.Outline!.Count);
        Assert.All(l.Outline, p => { Assert.InRange(p.X, PanelPad - 0.01f, PanelW - PanelPad + 0.01f); Assert.InRange(p.Y, PanelPad - 0.01f, PanelH - PanelPad + 0.01f); });
        // the front seats sit 0.75 m apart; the rear pair is closer than a marker pair and is nudged, which moves the front ones by a few pixels at most
        var scale = Math.Min((PanelW - 2 * PanelPad) / (poly.Max(p => p[0]) - poly.Min(p => p[0])), (PanelH - 2 * PanelPad) / (poly.Max(p => p[1]) - poly.Min(p => p[1])));
        Assert.InRange(l.Seats[1].X - l.Seats[0].X, (car.Seats[1].Offset[0] - car.Seats[0].Offset[0]) * scale - 4, (car.Seats[1].Offset[0] - car.Seats[0].Offset[0]) * scale + 4);
        Assert.InRange(Math.Abs(l.Seats[0].Y - l.Seats[1].Y), 0, 6); // still abreast
        Assert.True(l.Seats[0].Y < l.Seats[2].Y); // the rear seats stay behind the front ones
    }

    [Fact]
    public void WithoutAnOutlineTheHitBoxIsDrawnAsBefore()
    {
        var def = Type("car");
        var a = SeatSchematic.Layout(def, 184, 168, 12, 16);
        Assert.Null(a.Outline);
        Assert.Equal(a.Seats, SeatSchematic.Layout(def, 184, 168, 12, 16, null).Seats);
        Assert.Null(SeatSchematic.Layout(def, 184, 168, 12, 16, new List<float[]>()).Outline); // an empty outline is no outline
        Assert.Null(new VehicleOutlinesFile().For("nothing"));
    }

    [Fact]
    public void EveryOutlinedVehicleKeepsItsMarkersApartInThePanel()
    {
        var o = Outlines();
        foreach (var v in Seated())
        {
            var l = SeatSchematic.Layout(v, PanelW, PanelH, PanelPad, PanelR, o.For(v.Id));
            for (var i = 0; i < l.Seats.Count; i++)
            {
                Assert.InRange(l.Seats[i].X, l.BodyLeft - 0.01f, l.BodyLeft + l.BodyWidth + 0.01f);
                Assert.InRange(l.Seats[i].Y, l.BodyTop - 0.01f, l.BodyTop + l.BodyHeight + 0.01f);
                for (var j = i + 1; j < l.Seats.Count; j++)
                {
                    var d = MathF.Sqrt(MathF.Pow(l.Seats[i].X - l.Seats[j].X, 2) + MathF.Pow(l.Seats[i].Y - l.Seats[j].Y, 2));
                    Assert.True(d >= 2 * PanelR, $"{v.Id}: seats {i + 1} and {j + 1} overlap ({d:F1} px apart)");
                }
            }
        }
    }

    [Fact]
    public void ThePointInPolygonAndEdgeDistanceHelpersAgree()
    {
        var sq = new List<float[]> { new[] { -1f, -1f }, new[] { 1f, -1f }, new[] { 1f, 1f }, new[] { -1f, 1f } };
        Assert.True(OutlineGeometry.Contains(sq, 0, 0));
        Assert.False(OutlineGeometry.Contains(sq, 1.5f, 0));
        Assert.Equal(0.5f, OutlineGeometry.EdgeDistance(sq, 1.5f, 0), 3);
        Assert.Equal(1f, OutlineGeometry.EdgeDistance(sq, 0, 0), 3);
    }
}
