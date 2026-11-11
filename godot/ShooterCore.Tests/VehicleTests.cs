using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class VehicleTests
{
    static VehiclesFile Cfg() => new() { Vehicles = { new VehicleDef { Id = "car", TopSpeed = 28, Acceleration = 9, Steering = 1.7f, Mass = 1200 } } };

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(8f, 0f)]      // at the threshold: nothing
    [InlineData(9f, 10f)]     // 10 per m/s over 8
    [InlineData(13.5f, 55f)]
    [InlineData(28f, 100f)]   // capped
    [InlineData(-20f, 100f)]  // reversing counts too
    public void RunOverDamageFollowsTheRule(float speed, float expected) => Assert.Equal(expected, RunOver.Damage(Cfg(), speed), 3);

    [Fact]
    public void OneDriverOnly()
    {
        var s = new VehicleSeat();
        Assert.True(s.TryEnter(3));
        Assert.False(s.TryEnter(4));
        Assert.Equal(3, s.Driver);
        Assert.False(s.Exit(4));
        Assert.True(s.Exit(3));
        Assert.False(s.Occupied);
        Assert.False(s.Exit(3));
        Assert.True(s.TryEnter(4));
        s.Clear();
        Assert.False(s.Occupied);
        Assert.False(s.TryEnter(0));
    }

    [Fact]
    public void AbandonTimerReturnsOnceAfterTheTime()
    {
        var t = new AbandonTimer();
        for (var i = 0; i < 29; i++) Assert.False(t.Update(false, false, 1f, 30f));
        Assert.True(t.Update(false, false, 1f, 30f));
        Assert.False(t.Update(false, false, 1f, 30f)); // counting again from zero
    }

    [Fact]
    public void AbandonTimerResetsWhenOccupiedOrHome()
    {
        var t = new AbandonTimer();
        t.Update(false, false, 20f, 30f);
        Assert.Equal(20f, t.Empty);
        t.Update(true, false, 1f, 30f);
        Assert.Equal(0f, t.Empty);
        Assert.False(t.Update(false, true, 100f, 30f)); // already at its origin: nothing to return
    }

    [Fact]
    public void ColourPickPrefersUntakenAndStaysInPalette()
    {
        var rng = new Random(5);
        var taken = new List<byte>();
        for (var i = 0; i < VehicleColours.Palette.Length; i++)
        {
            var c = VehicleColours.Pick(rng, taken);
            Assert.DoesNotContain(c, taken);
            taken.Add(c);
        }
        Assert.True(VehicleColours.Pick(rng, taken) < VehicleColours.Palette.Length); // all taken: any
    }

    [Fact]
    public void VehicleRecordCarriesDriverAndColourThroughTheWire()
    {
        var r = VehicleRecord.Make(0x4001, new Vector3(1, 2, 3), 1f, 1234, 5);
        Assert.Equal(EntityState.InVehicle, r.State);
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        foreach (var p in enc.Encode(1, 0, new[] { r })) Assert.True(dec.Apply(p, out _));
        var got = dec.States[0x4001];
        Assert.Equal(EntityType.Vehicle, got.Type);
        Assert.Equal((ushort)1234, VehicleRecord.Driver(got));
        Assert.Equal((byte)5, VehicleRecord.Colour(got));
        Assert.Equal(EntityState.Alive, VehicleRecord.Make(0x4001, Vector3.Zero, 0, 0, 0).State);
    }

    [Fact]
    public void CarAcceleratesToTopSpeedAndNoFurther()
    {
        var def = Cfg().Vehicles[0]; var s = new CarState();
        for (var i = 0; i < 30 * 10; i++) CarModel.Step(ref s, def, 1, 0, false, 1f / 30);
        Assert.Equal(28f, s.Speed, 3);
        Assert.Equal(0f, s.Yaw, 3);
    }

    [Fact]
    public void BrakeAndHandbrakeStopTheCar_ReverseIsSlower()
    {
        var def = Cfg().Vehicles[0];
        var s = new CarState { Speed = 20 };
        for (var i = 0; i < 30; i++) CarModel.Step(ref s, def, 0, 0, true, 1f / 30);
        Assert.Equal(0f, s.Speed, 3);
        for (var i = 0; i < 30 * 10; i++) CarModel.Step(ref s, def, -1, 0, false, 1f / 30);
        Assert.Equal(-28f * CarModel.ReverseTopFraction, s.Speed, 3);
    }

    [Fact]
    public void SteeringNeedsSpeedAndTurnsTheRightWay()
    {
        var def = Cfg().Vehicles[0];
        var still = new CarState();
        CarModel.Step(ref still, def, 0, 1, false, 0.1f);
        Assert.Equal(0f, still.Yaw);
        var s = new CarState { Speed = 15 };
        for (var i = 0; i < 10; i++) CarModel.Step(ref s, def, 1, 1, false, 1f / 30); // steer right: yaw decreases (positive yaw turns left)
        Assert.True(s.Yaw < -0.1f);
        var l = new CarState { Speed = 15 };
        for (var i = 0; i < 10; i++) CarModel.Step(ref l, def, 1, -1, false, 1f / 30);
        Assert.True(l.Yaw > 0.1f);
    }
}
