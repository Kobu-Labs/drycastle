using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The v2 ground vehicles (war jeep, quad bike), the seat framework, the vehicle gun and the roadkill feed (ticket #131).</summary>
public class VehicleTypesTests
{
    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Type(string id) => Shipped().Vehicles.Single(v => v.Id == id);

    // ---- the types ----

    [Fact]
    public void JeepHasADriverSeatAGunnerSeatAndAGun()
    {
        var j = Type("jeep");
        Assert.Equal(new[] { "driver", "gunner" }, j.Seats.Select(s => s.Role));
        Assert.NotNull(j.Gun);
        Assert.Equal(60, j.Gun!.Magazine);
        Assert.True(j.Gun.Reserve > 0);
        Assert.Equal(1, j.Variants); // one fixed olive drab, no colour variants
    }

    [Fact]
    public void QuadIsDriverOnlyWithNoGunAndNoModelFile()
    {
        var q = Type("quad");
        Assert.Single(q.Seats);
        Assert.Null(q.Gun);
        Assert.Equal("", q.Model);
        Assert.NotNull(q.Tint);
    }

    [Theory]
    [InlineData("jeep")]
    [InlineData("quad")]
    public void NewTypesReachTheirTopSpeedAndStopOnTheHandbrake(string id)
    {
        var def = Type(id); var s = new CarState();
        for (var i = 0; i < 30 * 20; i++) CarModel.Step(ref s, def, 1, 0, false, 1f / 30);
        Assert.Equal(def.TopSpeed, s.Speed, 3);
        for (var i = 0; i < 30 * 5; i++) CarModel.Step(ref s, def, 0, 0, true, 1f / 30);
        Assert.Equal(0f, s.Speed, 3);
    }

    [Fact]
    public void JeepTopSpeedIsAboutOneHundredAndTenKmhAndBelowTheCar()
    {
        var f = Shipped();
        Assert.InRange(f.Vehicles.First(v => v.Id == "jeep").TopSpeed * 3.6f, 100f, 120f);
        Assert.True(Type("jeep").TopSpeed < f.Vehicles[0].TopSpeed);
    }

    [Fact]
    public void QuadIsFasterThanABicycleButSlowerThanTheMotorcycle()
    {
        var f = Shipped(); var quad = Type("quad"); var moto = f.Vehicles[1]; var bike = f.Vehicles[2];
        Assert.InRange(quad.TopSpeed * 3.6f, 80f, 100f);
        Assert.True(bike.TopSpeed < quad.TopSpeed && quad.TopSpeed < moto.TopSpeed, "the quad sits between the bicycle and the motorcycle");
    }

    [Fact]
    public void JeepStopsFromOneHundredAndEightKmhWithinItsBrakingTarget()
    {
        // braking at 8 m/s2 from 30 m/s: v^2 / 2a = 56 m, plus the first ticks
        var def = Type("jeep"); var s = new CarState { Speed = 30 };
        var dt = 1f / 60; var distance = 0f;
        for (var i = 0; i < 60 * 30 && s.Speed > 0; i++) { distance += s.Speed * dt; CarModel.Step(ref s, def, -1, 0, false, dt); }
        Assert.InRange(distance, 52f, 62f);
    }

    [Fact]
    public void QuadStopsFromOneHundredKmhWithinItsBrakingTarget()
    {
        // 25 m/s at 11 m/s2: about 28 m
        var def = Type("quad"); var s = new CarState { Speed = 25 };
        var dt = 1f / 60; var distance = 0f;
        for (var i = 0; i < 60 * 30 && s.Speed > 0; i++) { distance += s.Speed * dt; CarModel.Step(ref s, def, -1, 0, false, dt); }
        Assert.InRange(distance, 26f, 32f);
    }

    [Fact]
    public void TurningRadiusIsTheLockAtWalkingPaceAndWidensWithSpeedForBothTypes()
    {
        foreach (var id in new[] { "jeep", "quad" })
        {
            var def = Type(id);
            Assert.Equal(def.TurnRadius, CarModel.TurnRadiusAt(def, 0), 3);
            Assert.True(CarModel.TurnRadiusAt(def, def.TopSpeed) > CarModel.TurnRadiusAt(def, 2), id);
        }
        Assert.True(CarModel.TurnRadiusAt(Type("jeep"), 20) > CarModel.TurnRadiusAt(Type("quad"), 20), "the jeep turns wider than the quad");
    }

    [Fact]
    public void HitPointsFollowTheBodyClass()
    {
        var f = Shipped();
        Assert.True(Type("quad").Hp < f.Vehicles[1].Hp, "the quad is flimsier than the motorcycle");
        Assert.True(Type("jeep").Hp > f.Vehicles[0].Hp, "the jeep takes more than the car");
    }

    // ---- run-over ----

    [Fact]
    public void RunOverMassScalesOnlyTheTypesThatSetIt()
    {
        var f = Shipped();
        Assert.Equal(1f, f.RunOverScale(f.Vehicles[0]));  // car: no run_over_mass, the v1 numbers
        Assert.Equal(1f, f.RunOverScale(f.Vehicles[1]));  // motorcycle
        Assert.Equal(1.25f, f.RunOverScale(Type("jeep")), 3);
        Assert.Equal(0.25f, f.RunOverScale(Type("quad")), 3);
    }

    [Fact]
    public void QuadRunOverIsLightAndTheJeepHeavier()
    {
        var f = Shipped();
        Assert.Equal(42.5f, RunOver.Damage(f, 25, f.RunOverScale(Type("quad"))), 3);
        Assert.Equal(50f, RunOver.Damage(f, 12, f.RunOverScale(Type("jeep"))), 3);
        Assert.Equal(f.RunOverMaxDamage, RunOver.Damage(f, 25, f.RunOverScale(f.Vehicles[0])));
        Assert.Equal(RunOver.Damage(f, 12), RunOver.Damage(f, 12, 1f), 3); // default scale is the v1 rule
    }

    // ---- seats ----

    [Fact]
    public void TwoSeatsTakeTwoPlayersAndTheDriverSeatComesFirst()
    {
        var s = new VehicleSeat(2);
        Assert.True(s.TryEnter(5));            // first free seat: the driver's
        Assert.Equal(5, s.Driver);
        Assert.True(s.TryEnter(6, 1));         // the gunner's
        Assert.False(s.TryEnter(7));           // full
        Assert.False(s.TryEnter(5, 1));        // already seated
        Assert.Equal(1, s.SeatOf(6));
        Assert.False(s.HasFree);
    }

    [Fact]
    public void ExitFreesOnlyThePlayersOwnSeat()
    {
        var s = new VehicleSeat(2);
        s.TryEnter(5, 0); s.TryEnter(6, 1);
        Assert.True(s.Exit(6));
        Assert.Equal(5, s.Driver);
        Assert.True(s.IsFree(1));
        Assert.False(s.Exit(6));
        s.Clear(0);
        Assert.False(s.Occupied);
    }

    [Fact]
    public void SeatPositionFollowsTheVehicleYawWithForwardMinusZ()
    {
        var car = new Vector3(10, 0, 10);
        var fwd = VehicleSeat.SeatPosition(car, 0, new float[] { 0, 0, -2 });
        Assert.Equal(new Vector3(10, 0, 8), fwd, new Vec3Comparer());
        var right = VehicleSeat.SeatPosition(car, 0, new float[] { 1, 0, 0 });
        Assert.Equal(new Vector3(11, 0, 10), right, new Vec3Comparer());
        // turned a quarter round to the left: forward is now -X
        var turned = VehicleSeat.SeatPosition(car, MathF.PI / 2, new float[] { 0, 0, -2 });
        Assert.Equal(new Vector3(8, 0, 10), turned, new Vec3Comparer());
        Assert.Equal(new Vector3(10, 1.25f, 10), VehicleSeat.SeatPosition(car, 0, new float[] { 0, 1.25f, 0 }), new Vec3Comparer());
    }

    [Fact]
    public void NearestFreeSeatPicksTheClosestFreeSeatWithinRange()
    {
        var jeep = Type("jeep"); var car = Vector3.Zero;
        var s = new VehicleSeat(2);
        Assert.Equal(0, s.NearestFreeSeat(jeep, car, 0, new Vector3(0, 0, 3.5f), 4f)); // an empty vehicle: the first in is the driver, wherever they stand
        // standing beside the car: the driver's seat (the centre) is no further than the roof, so the lower index wins
        Assert.Equal(0, s.NearestFreeSeat(jeep, car, 0, new Vector3(2, 0, 0), 4f));
        s.TryEnter(9, 0);
        Assert.Equal(1, s.NearestFreeSeat(jeep, car, 0, new Vector3(2, 0, 0), 4f));  // the driver is taken: the gunner
        Assert.Equal(-1, s.NearestFreeSeat(jeep, car, 0, new Vector3(20, 0, 0), 4f)); // too far from every free seat
        s.TryEnter(8, 1);
        Assert.Equal(-1, s.NearestFreeSeat(jeep, car, 0, new Vector3(2, 0, 0), 4f));  // full
    }

    // ---- the gun ----

    [Fact]
    public void GunFiresAtItsIntervalWhileTheTriggerIsHeld()
    {
        // 0.5 s is exact in float, so the interval edge is not hit by rounding
        var g = new VehicleGun(new VehicleGunDef { Magazine = 10, Reserve = 0, FireInterval = 0.5f });
        Assert.True(g.TryFire(0, true));
        Assert.False(g.TryFire(0.25, true));
        Assert.True(g.TryFire(0.5, true));
        Assert.False(g.TryFire(0.7, false));
        Assert.Equal(8, g.Magazine);
    }

    [Fact]
    public void EmptyMagazineReloadsFromTheReserveAndRunsOut()
    {
        var g = new VehicleGun(new VehicleGunDef { Magazine = 2, Reserve = 3, FireInterval = 0.5f, ReloadTime = 1f });
        Assert.True(g.TryFire(0, true));              // magazine 1
        Assert.True(g.TryFire(0.5, true));            // magazine 0
        Assert.False(g.TryFire(1.0, true));           // empty: starts a reload, done at 2.0
        Assert.True(g.Reloading);
        Assert.False(g.TryFire(1.5, true));           // still reloading
        Assert.False(g.TryFire(2.1, false));          // done: 2 in the magazine, 1 left in reserve
        Assert.Equal(2, g.Magazine);
        Assert.Equal(1, g.Reserve);
        Assert.True(g.TryFire(2.2, true));            // magazine 1
        Assert.True(g.TryFire(2.8, true));            // magazine 0, reserve still 1
        Assert.True(g.TryReload(3.0));                // the second reload starts, done at 4.0
        Assert.False(g.TryFire(3.5, true));           // still reloading
        Assert.False(g.TryFire(4.5, false));          // done: the last round moves in
        Assert.Equal(1, g.Magazine);
        Assert.Equal(0, g.Reserve);
        Assert.False(g.TryReload(10));                // nothing left to load
    }

    // ---- the feed and the streak ----

    [Fact]
    public void KillFeedCarriesTheRoadkillCause()
    {
        var k = new KillMsg(1, 2, SpecialWeapons.Vehicle, KillCause.Roadkill);
        Assert.Equal(k, MatchCodec.DecodeKill(MatchCodec.Encode(k)));
    }

    [Fact]
    public void AnOldFiveByteKillStillDecodesAsStandard()
    {
        Assert.Equal(new KillMsg(1, 2, 3), MatchCodec.DecodeKill(new byte[] { 1, 0, 2, 0, 3 }));
    }

    [Fact]
    public void ARoadkillCountsTowardTheStreakLikeAnyOtherKill()
    {
        double t = 0;
        var m = new MatchSim(new MatchConfig(), () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0), new Vector3(0, 0, 100) }, new Random(1));
        m.AddPlayer(1, "a"); m.AddPlayer(2, "b"); m.AddPlayer(3, "c");
        t += 3.01; m.Update();
        Assert.Equal(DamageResult.Killed, m.ApplyDamage(2, 1, SpecialWeapons.Vehicle, 999, cause: KillCause.Roadkill));
        Assert.Equal(DamageResult.Killed, m.ApplyDamage(3, 1, SpecialWeapons.VehicleGun, 999));
        Assert.Equal(2, m.Get(1)!.Kills);
    }

    // ---- validation ----

    [Fact]
    public void BadSeatsAndGunsAreRejected()
    {
        VehiclesFile Cfg(Action<VehicleDef> edit)
        {
            var v = new VehicleDef { Id = "x", TopSpeed = 10, Acceleration = 1, Brake = 1, TurnRadius = 1, MaxLateralAccel = 1, Hp = 10 };
            edit(v);
            return new VehiclesFile { Vehicles = { v } };
        }
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Cfg(v => v.Seats = new List<VehicleSeatDef> { new() { Role = "gunner" } })));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Cfg(v => v.Gun = new VehicleGunDef())));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Cfg(v => v.Tint = new float[] { 1, 0 })));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Cfg(v => { v.Tint = new float[] { 1, 0, 0 }; v.Paints.Add("A"); v.PaintMaterial = "B"; })));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Cfg(v => v.RunOverMass = 0)));
        var ok = Cfg(v =>
        {
            v.Seats = new List<VehicleSeatDef> { new() { Role = "driver" }, new() { Role = "gunner", Offset = new float[] { 0, 1, 0 } } };
            v.Gun = new VehicleGunDef();
        });
        Assert.Same(ok, VehiclesFile.Validate(ok));
    }

    /// <summary>Compares vectors to a small tolerance (the seat maths is float).</summary>
    sealed class Vec3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
