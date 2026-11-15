using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Fixed seats (ADR 0026): how a rider's look is split between head and body, and who sits in a vehicle's seats on the wire.</summary>
public class RiderPoseTests
{
    static readonly VehicleSeatDef Passive = new() { Role = "passenger" };
    static readonly VehicleSeatDef Gunner = new() { Role = "gunner" };
    static readonly VehicleSeatDef Armed = new() { Role = "passenger", Armed = true };
    const float Deg = MathF.PI / 180f;

    // ---- the passive rider's look ----

    [Fact]
    public void WithoutTheHeadLookBitTheHeadFacesAheadWhateverTheYawIs()
    {
        var (off, pitch) = RiderPose.Passive(false, false, 1f, 2.5f, 0.4f); // the chase view's yaw is the camera's orbit
        Assert.Equal(0f, off);
        Assert.Equal(0f, pitch);
    }

    [Fact]
    public void ALookIsTheTurnAgainstTheHullAndNotTheWorldYaw()
    {
        var (off, pitch) = RiderPose.Passive(false, true, 3f, 3.5f, 0.2f);
        Assert.Equal(0.5f, off, 4);
        Assert.Equal(0.2f, pitch, 4);
    }

    [Fact]
    public void TheTurnWrapsTheShortWayRound()
    {
        var (off, _) = RiderPose.Passive(false, true, 3f, -3f, 0f); // 3 rad to -3 rad is a short way across the back
        Assert.True(MathF.Abs(off) < 0.5f);
    }

    [Fact]
    public void AHelicoptersPilotNeverTurnsTheirHeadWithTheYawButLooksUpAndDown()
    {
        var (off, pitch) = RiderPose.Passive(true, true, 1f, 2.5f, 0.3f);
        Assert.Equal(0f, off); // the yaw is the heading they steer to
        Assert.Equal(0.3f, pitch, 4);
    }

    [Fact]
    public void ThePitchIsHeldToTheNecksReach()
    {
        Assert.Equal(RiderPose.PitchUp, RiderPose.Passive(false, true, 0, 0, 1.5f).Pitch);
        Assert.Equal(RiderPose.PitchDown, RiderPose.Passive(false, true, 0, 0, -1.5f).Pitch);
    }

    // ---- the figure ----

    [Fact]
    public void APassiveRidersBodyStaysOnTheSeatWhateverTheLook()
    {
        foreach (var look in new[] { -170f, -60f, 0f, 45f, 90f, 179f })
            Assert.Equal(0f, RiderPose.Figure(Passive, 1f, 1f + look * Deg, 0f).BodyYaw);
    }

    [Fact]
    public void ALookWithinTheNecksReachTurnsTheHeadOnly()
    {
        var f = RiderPose.Figure(Passive, 0f, 40f * Deg, 0.1f);
        Assert.Equal(40f * Deg, f.HeadYaw, 4);
        Assert.Equal(0f, f.TorsoYaw);
        Assert.Equal(0.1f, f.Pitch, 4);
    }

    [Fact]
    public void ALookBeyondTheNeckTwistsTheUpperBodyAndTheTwoAddUpToTheLook()
    {
        var look = 160f * Deg;
        var f = RiderPose.Figure(Passive, 0f, look, 0f);
        Assert.Equal(RiderPose.HeadYawLimit, f.HeadYaw, 4);
        Assert.True(f.TorsoYaw > 0f && f.TorsoYaw <= RiderPose.TorsoLimit + 1e-4f);
        Assert.Equal(look, f.HeadYaw + f.TorsoYaw, 4);
    }

    [Fact]
    public void ARiderWhoWorksAGunTurnsTheWholeFigureToTheAim()
    {
        foreach (var seat in new[] { Gunner, Armed })
        {
            var f = RiderPose.Figure(seat, 0.5f, 0.5f + 90f * Deg, 0.6f);
            Assert.Equal(90f * Deg, f.BodyYaw, 4);
            Assert.Equal(0f, f.HeadYaw);
            Assert.Equal(0f, f.TorsoYaw);
            Assert.Equal(0.6f, f.Pitch, 4); // the weapon's pitch
        }
    }

    [Fact]
    public void OnlyGunSeatsFollowTheAim()
    {
        Assert.True(RiderPose.FollowsAim(Gunner));
        Assert.True(RiderPose.FollowsAim(Armed));
        Assert.False(RiderPose.FollowsAim(Passive));
        Assert.False(RiderPose.FollowsAim(new VehicleSeatDef { Role = "driver" }));
    }

    [Fact]
    public void TheHullTurningUnderASeatedRiderDoesNotTurnTheirBody()
    {
        // the snapshot's yaw is the hull's now plus the look: a hull that swings 100 degrees leaves a head facing ahead facing ahead
        var f = RiderPose.Figure(Passive, 1f + 100f * Deg, 1f + 100f * Deg, 0f);
        Assert.Equal(0f, f.BodyYaw);
        Assert.Equal(0f, f.HeadYaw, 4);
    }

    // ---- the seats record ----

    [Fact]
    public void TheSeatsRecordCarriesEveryOccupantBeyondTheDriver()
    {
        var r = SeatsRecord.Make(VehicleRecord.FirstId + 3, new ushort[] { 9, 0x1234, 77, 0xABCD });
        Assert.Equal(EntityType.Seats, r.Type);
        Assert.Equal(0x1234, SeatsRecord.Occupant(r, 1));
        Assert.Equal(77, SeatsRecord.Occupant(r, 2));
        Assert.Equal(0xABCD, SeatsRecord.Occupant(r, 3));
        Assert.Equal(0, SeatsRecord.Occupant(r, 0)); // the driver is in the vehicle's own record
    }

    [Fact]
    public void TheSeatsRecordSurvivesTheWire()
    {
        var enc = SeatsRecord.Make(VehicleRecord.FirstId, new ushort[] { 4, 65535, 0, 300 });
        Assert.Equal(65535, SeatsRecord.Occupant(enc, 1));
        Assert.Equal(0, SeatsRecord.Occupant(enc, 2));
        Assert.Equal(300, SeatsRecord.Occupant(enc, 3));
    }

    [Fact]
    public void AVehicleWithTwoSeatsLeavesTheOthersEmpty()
    {
        var r = SeatsRecord.Make(VehicleRecord.FirstId, new ushort[] { 4, 5 });
        Assert.Equal(5, SeatsRecord.Occupant(r, 1));
        Assert.Equal(0, SeatsRecord.Occupant(r, 2));
        Assert.Equal(0, SeatsRecord.Occupant(r, 3));
    }

    [Fact]
    public void TheRecordIdIsTheVehiclesIndexInItsOwnRangeAndRoundTrips()
    {
        var car = (ushort)(VehicleRecord.FirstId + 11);
        Assert.Equal(car, SeatsRecord.CarOf(SeatsRecord.IdOf(car)));
        Assert.True(SeatsRecord.IdOf(car) < VehicleRecord.FirstId);
    }

    [Fact]
    public void EveryShippedVehicleHasAFixedDriverSeat()
    {
        var all = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Vehicles;
        foreach (var v in all)
        {
            Assert.NotEmpty(v.Seats);
            Assert.Equal("driver", v.Seats[0].Role);
        }
    }

    [Fact]
    public void TheSeatsRecordRidesTheSnapshotWireAndIsOnlySentWhenASeatChanges()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var car = (ushort)(VehicleRecord.FirstId + 2);
        var seated = SeatsRecord.Make(car, new ushort[] { 5, 12, 0, 40000 });
        SnapshotHeader h = default; var committed = false;
        foreach (var p in enc.Encode(10, 1, Wire.NoTick, new[] { seated })) committed |= dec.Apply(p, out h);
        Assert.True(committed);
        var got = dec.States[SeatsRecord.IdOf(car)];
        Assert.Equal(EntityType.Seats, got.Type);
        Assert.Equal(12, SeatsRecord.Occupant(got, 1));
        Assert.Equal(40000, SeatsRecord.Occupant(got, 3));
        var same = enc.Encode(11, 2, 10, new[] { seated });
        Assert.Equal(SnapshotEncoder.HeaderBytes, Assert.Single(same).Length); // unchanged: header only
        foreach (var p in same) dec.Apply(p, out h);
        var left = SeatsRecord.Make(car, new ushort[] { 5, 0, 0, 40000 });
        foreach (var p in enc.Encode(12, 3, 11, new[] { left })) dec.Apply(p, out h);
        Assert.Equal(0, SeatsRecord.Occupant(dec.States[SeatsRecord.IdOf(car)], 1));
    }
}
