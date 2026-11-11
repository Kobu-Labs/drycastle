using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class VehicleTests
{
    static VehiclesFile Cfg() => new() { Vehicles = { new VehicleDef { Id = "car", TopSpeed = 28, Acceleration = 9, Steering = 1.7f, Mass = 1200 } } };

    /// <summary>The shipped vehicles.json (copied next to the test binary).</summary>
    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));

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

    [Fact]
    public void ShippedFileHasCarMotorcycleAndBicycleWithTheAgreedSpawnCounts()
    {
        var f = Shipped();
        Assert.Equal(new[] { "car", "motorcycle", "bicycle" }, f.Vehicles.Select(v => v.Id));
        Assert.Equal(new[] { 8, 3, 3 }, f.Vehicles.Select(v => v.SpawnCount));
        Assert.All(f.Vehicles.Skip(1), v => { Assert.NotEqual("", v.Model); Assert.Equal(3, v.Variants); Assert.Contains(v.PaintMaterial, v.Paints); });
        Assert.Equal(VehicleColours.Palette.Length, f.Vehicles[0].Variants);
    }

    [Fact]
    public void BikesDifferFromTheCarAsSpecified()
    {
        var f = Shipped(); var car = f.Vehicles[0]; var moto = f.Vehicles[1]; var bike = f.Vehicles[2];
        Assert.True(moto.TopSpeed > car.TopSpeed && car.TopSpeed > bike.TopSpeed);
        Assert.True(moto.Acceleration > car.Acceleration && car.Acceleration > bike.Acceleration);
        Assert.True(moto.Steering > car.Steering);
        Assert.True(moto.Mass > bike.Mass);
        Assert.True(bike.TopSpeed > f.RunOverMinSpeed, "the bicycle must be able to run someone over");
        Assert.True(RunOver.Damage(f, moto.TopSpeed) >= f.RunOverMaxDamage, "the motorcycle kills at top speed");
        Assert.InRange(RunOver.Damage(f, bike.TopSpeed), 1f, 99f); // the bicycle hurts but does not one-shot
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryTypeReachesItsTopSpeedAndStopsOnTheHandbrake(int kind)
    {
        var def = Shipped().Vehicles[kind]; var s = new CarState();
        for (var i = 0; i < 30 * 20; i++) CarModel.Step(ref s, def, 1, 0, false, 1f / 30);
        Assert.Equal(def.TopSpeed, s.Speed, 3);
        for (var i = 0; i < 30 * 3; i++) CarModel.Step(ref s, def, 0, 0, true, 1f / 30);
        Assert.Equal(0f, s.Speed, 3);
    }

    [Fact]
    public void MotorcycleTurnsTighterThanTheCarAtTheSameSpeed()
    {
        var f = Shipped();
        float YawAfter(VehicleDef d) { var s = new CarState { Speed = 12 }; for (var i = 0; i < 15; i++) CarModel.Step(ref s, d, 0.4f, 1, false, 1f / 30); return -s.Yaw; }
        Assert.True(YawAfter(f.Vehicles[1]) > YawAfter(f.Vehicles[0]));
    }

    [Fact]
    public void RecordCarriesTypeAndColourInOneByte()
    {
        var r = VehicleRecord.Make(0x4010, new Vector3(1, 0, 2), 0.5f, 7, VehicleRecord.Pack(2, 2));
        Assert.Equal(2, VehicleRecord.Kind(r));
        Assert.Equal((byte)2, VehicleRecord.Colour(r));
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        foreach (var p in enc.Encode(1, 0, new[] { r })) Assert.True(dec.Apply(p, out _));
        Assert.Equal(2, VehicleRecord.Kind(dec.States[0x4010]));
        Assert.Equal((ushort)7, VehicleRecord.Driver(dec.States[0x4010]));
    }

    [Fact]
    public void ColourPickStaysBelowTheVariantCount()
    {
        var rng = new Random(9); var taken = new List<byte>();
        for (var i = 0; i < 3; i++) { var c = VehicleColours.Pick(rng, taken, 3); Assert.True(c < 3); Assert.DoesNotContain(c, taken); taken.Add(c); }
        Assert.True(VehicleColours.Pick(rng, taken, 3) < 3);
    }

    [Fact]
    public void PaintsWithoutMaterialAreRejected()
    {
        var f = Cfg(); f.Vehicles[0].Paints.Add("X");
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
    }

    // ---- damage stages and the blast (ticket #103) ----

    static readonly float[] Stages = { 0.66f, 0.33f, 0.12f };

    [Theory]
    [InlineData(300f, 0)]
    [InlineData(199f, 0)]    // 66.3 percent
    [InlineData(198f, 1)]    // exactly at the first threshold
    [InlineData(100f, 1)]   // 33.3 percent, just above the second threshold
    [InlineData(99f, 2)]
    [InlineData(36f, 3)]
    [InlineData(1f, 3)]
    [InlineData(0f, 3)]      // out of HP: still stage 3 until the blast removes it
    public void StageFollowsRemainingHp(float hp, int stage) => Assert.Equal(stage, VehicleDamage.Stage(hp, 300, Stages));

    [Fact]
    public void StageNeverGoesDownWhenHpOnlyDrops()
    {
        var last = 0;
        for (var hp = 300f; hp >= 0; hp -= 7) { var s = VehicleDamage.Stage(hp, 300, Stages); Assert.True(s >= last); last = s; }
        Assert.Equal(3, last);
    }

    [Fact]
    public void BlastFallsOffLinearlyAndStopsAtTheRadius()
    {
        var e = new VehicleExplosionDef { Damage = 110, Splash = new SplashDef { Radius = 9, FullDamageRadius = 3, EdgeDamage = 20 } };
        Assert.Equal(110f, VehicleDamage.BlastDamage(e, 0));
        Assert.Equal(110f, VehicleDamage.BlastDamage(e, 3));
        Assert.Equal(65f, VehicleDamage.BlastDamage(e, 6));
        Assert.Equal(0f, VehicleDamage.BlastDamage(e, 9));
        Assert.Equal(0f, VehicleDamage.BlastDamage(e, 20));
    }

    [Fact]
    public void DistanceToBoxIsZeroInsideAndMeasuredFromTheSurface()
    {
        var c = new Vector3(10, 0, 10);
        Assert.Equal(0f, VehicleDamage.DistanceToBox(c + new Vector3(0.5f, 0.8f, 1f), c, 0, 1.1f, 2.15f), 1e-4f);
        Assert.Equal(3f, VehicleDamage.DistanceToBox(c + new Vector3(4.1f, 0.5f, 0), c, 0, 1.1f, 2.15f), 1e-3f);   // beside the car
        Assert.Equal(2f, VehicleDamage.DistanceToBox(c + new Vector3(0, 0.5f, -4.15f), c, 0, 1.1f, 2.15f), 1e-3f); // in front (forward is -Z)
        Assert.Equal(2f, VehicleDamage.DistanceToBox(c + new Vector3(0, 0.5f, 4.15f), c, MathF.PI, 1.1f, 2.15f), 1e-3f); // turned round: that side is its front now
        Assert.Equal(2f, VehicleDamage.DistanceToBox(c + new Vector3(0, 3.5f, 0), c, 0, 1.1f, 2.15f), 1e-3f);     // above the roof (1.5 m)
    }

    [Fact]
    public void ShippedFileHasDamageStagesAndHpForEveryVehicle()
    {
        var f = Shipped();
        Assert.Equal(3, f.DamageStages.Count);
        Assert.All(f.Vehicles, v => { Assert.True(v.Hp > 0); Assert.Equal(3, v.Hood.Length); });
        Assert.True(f.Explosion.Damage > 0 && f.Explosion.Splash.Radius > 0);
    }

    [Fact]
    public void BadStagesAreRejected()
    {
        var f = Cfg(); f.DamageStages = new List<float> { 0.3f, 0.6f, 0.1f };
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        var g = Cfg(); g.Vehicles[0].Hp = 0;
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(g));
    }

    [Fact]
    public void VehicleRecordCarriesStageAndDestroyedState()
    {
        var r = VehicleRecord.Make(0x4001, Vector3.Zero, 0, 7, 3, stage: 2);
        Assert.Equal(2, VehicleRecord.Stage(r));
        Assert.Equal(EntityState.InVehicle, r.State);
        var dead = VehicleRecord.Make(0x4001, Vector3.Zero, 0, 0, 3, stage: 3, destroyed: true);
        Assert.Equal(EntityState.Dead, dead.State);
        Assert.Equal(3, VehicleRecord.Stage(dead));
    }

    [Fact]
    public void VehicleExplosionMessageRoundTrips()
    {
        var m = new VehicleExplosionMsg(0x4002, 5, new Vector3(1.5f, 2, -3), 9);
        Assert.Equal(m, WeaponCodec.DecodeVehicleExplosion(WeaponCodec.Encode(m)));
        Assert.Null(WeaponCodec.DecodeVehicleExplosion(new byte[5]));
    }

    [Fact]
    public void ExplosionKnockbackOverloadMatchesTheWeaponOne()
    {
        var w = new WeaponDef { Damage = 100, Splash = new SplashDef { Radius = 6, KnockbackSpeed = 4.5f } };
        var a = ProjectileMath.Knockback(w, new Vector3(0, 0, 0), new Vector3(2, 1, 0), 0.5f);
        var b = ProjectileMath.Knockback(4.5f, new Vector3(0, 0, 0), new Vector3(2, 1, 0), 0.5f);
        Assert.Equal(a, b);
    }
}
