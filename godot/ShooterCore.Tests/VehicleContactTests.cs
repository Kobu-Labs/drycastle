using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>#253: vehicles bump into each other. The overlap test and the bump, without a scene.</summary>
public class VehicleContactTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));

    /// <summary>A car-sized box at (x, 0, z) facing -Z.</summary>
    static ContactShape Car(float x, float z, float yaw = 0) => new(new Vector3(x, 0, z), yaw, 1.28f, 2.18f, 2.2f);

    static ContactShape Heli(float x, float y, float z) => new(new Vector3(x, y, z), 0, 1.3f, 4f, 2.95f, 4.5f, 3.4f, 0.3f);

    [Fact]
    public void TwoCarsApartDoNotTouch() => Assert.Null(VehicleContact.Test(Car(0, 0), Car(0, 5), false));

    [Fact]
    public void CarsNoseToTailTouchAlongTheirLengthAndPointFromFirstToSecond()
    {
        var hit = VehicleContact.Test(Car(0, 0), Car(0, 4), false);
        Assert.NotNull(hit);
        Assert.Equal(0.36f, hit!.Value.Depth, 3); // 2 * 2.18 - 4
        Assert.Equal(1f, hit.Value.Normal.Z, 3);
    }

    [Fact]
    public void CarsSideBySideTouchAcrossTheirWidth()
    {
        var hit = VehicleContact.Test(Car(0, 0), Car(2.4f, 0), false);
        Assert.NotNull(hit);
        Assert.Equal(2 * 1.28f - 2.4f, hit!.Value.Depth, 3);
        Assert.Equal(1f, hit.Value.Normal.X, 3);
    }

    [Fact]
    public void ATurnedCarIsATurnedBox()
    {
        // a car broadside across the other's nose reaches only its half width (1.28) along the line, not its half length: clear at 3.6, touching at 3.3
        Assert.Null(VehicleContact.Test(Car(0, 0), Car(0, 3.6f, MathF.PI / 2), false));
        Assert.NotNull(VehicleContact.Test(Car(0, 0), Car(0, 3.3f, MathF.PI / 2), false));
        // at 45 degrees it reaches (1.28 + 2.18) * 0.707 = 2.45 along the line, a little more than its own half length (2.18)
        Assert.NotNull(VehicleContact.Test(Car(0, 0), Car(0, 4.5f, MathF.PI / 4), false));
        Assert.Null(VehicleContact.Test(Car(0, 0), Car(0, 4.7f, MathF.PI / 4), false));
    }

    [Fact]
    public void GroundVehiclesNeverMeetVertically()
    {
        // a bicycle on a bus: deep overlap in the plane, so the way out is sideways, never up
        var bus = new ContactShape(Vector3.Zero, 0, 1.28f, 6.03f, 3.1f);
        var bike = new ContactShape(new Vector3(0, 0, 0.1f), 0, 0.3f, 0.85f, 1f);
        var hit = VehicleContact.Test(bus, bike, false);
        Assert.NotNull(hit);
        Assert.Equal(0f, hit!.Value.Normal.Y);
    }

    [Fact]
    public void AHelicopterSettingOnARoofIsPushedUp()
    {
        var bus = new ContactShape(Vector3.Zero, 0, 1.28f, 6.03f, 3.1f);
        var hit = VehicleContact.Test(bus, Heli(0, 3.0f, 0), true);
        Assert.NotNull(hit);
        Assert.Equal(1f, hit!.Value.Normal.Y, 3); // from the bus up to the helicopter
        Assert.Equal(0.1f, hit.Value.Depth, 3);
    }

    [Fact]
    public void AHelicopterAboveTheRoofHasNoContact()
    {
        var bus = new ContactShape(Vector3.Zero, 0, 1.28f, 6.03f, 3.1f);
        Assert.Null(VehicleContact.Test(bus, Heli(0, 4f, 0), true));
    }

    [Fact]
    public void TheRotorDiscReachesFurtherThanTheHull()
    {
        // a car 5.7 m from the mast: well past the hull, but inside the 4.5 m disc once the disc is as low as the car
        Assert.Null(VehicleContact.Test(Heli(0, 0, 0), Car(5.7f, 0), true)); // the disc is 3.4 m up, over the car
        var hit = VehicleContact.Test(Heli(0, -2.2f, 0), Car(5.7f, 0), true);
        Assert.NotNull(hit);
        Assert.Equal(1f, hit!.Value.Normal.X, 3); // pushed aside, from the helicopter towards the car
        Assert.Equal(4.5f - (5.7f - 1.28f), hit.Value.Depth, 3);
    }

    [Fact]
    public void TwoRotorDiscsTouchAcrossTheirRadii()
    {
        Assert.Null(VehicleContact.Test(Heli(0, 0, 0), Heli(9.2f, 0, 0), true));
        var hit = VehicleContact.Test(Heli(0, 0, 0), Heli(8f, 0, 0), true);
        Assert.NotNull(hit);
        Assert.Equal(1f, hit!.Value.Normal.X, 3);
    }

    // ---- bump

    static BumpBody Moving(float mass, float vx, float vz, bool flies = false) => new(mass, new Vector3(vx, 0, vz), flies);

    [Fact]
    public void AHeadOnBumpExchangesTheClosingSpeedAndMovesBothApart()
    {
        var hit = new VehicleContactHit(Vector3.UnitZ, 0.4f);
        var bump = VehicleContact.Bump(hit, Moving(1200, 0, 10), Moving(1200, 0, -10), 0.5f);
        Assert.Equal(-0.2f, bump.MoveA.Z, 4);
        Assert.Equal(0.2f, bump.MoveB.Z, 4);
        // equal masses, bounce 0.5: each goes from +-10 to -+5
        Assert.Equal(-15f, bump.DeltaVA.Z, 3);
        Assert.Equal(15f, bump.DeltaVB.Z, 3);
        Assert.Equal(15f, bump.SpeedLossA, 3);
    }

    [Fact]
    public void TheHeavyOneBarelyNoticesAndTheLightOneTakesTheSpeed()
    {
        var hit = new VehicleContactHit(Vector3.UnitZ, 0.4f);
        var bus = Moving(13000, 0, 10); var bike = Moving(230, 0, 0);
        var bump = VehicleContact.Bump(hit, bus, bike, 0.3f);
        Assert.True(bump.SpeedLossA < 0.5f);
        Assert.True(bump.SpeedLossB > 12f);
        Assert.True(MathF.Abs(bump.MoveA.Z) < MathF.Abs(bump.MoveB.Z) / 20); // the bike is shoved, the bus hardly moves
        // momentum is kept
        Assert.Equal(0f, 13000 * bump.DeltaVA.Z + 230 * bump.DeltaVB.Z, 1);
    }

    [Fact]
    public void VehiclesAlreadyMovingApartAreOnlyMoved()
    {
        var hit = new VehicleContactHit(Vector3.UnitZ, 0.4f);
        var bump = VehicleContact.Bump(hit, Moving(1200, 0, -3), Moving(1200, 0, 3), 0.3f);
        Assert.Equal(Vector3.Zero, bump.DeltaVA);
        Assert.Equal(Vector3.Zero, bump.DeltaVB);
        Assert.Equal(0.4f, bump.MoveB.Z - bump.MoveA.Z, 4);
    }

    [Fact]
    public void AGroundVehicleIsNotPushedUpButAHelicopterIs()
    {
        var hit = new VehicleContactHit(Vector3.UnitY, 0.3f); // from the bus up to the helicopter
        var bump = VehicleContact.Bump(hit, Moving(13000, 0, 0), new BumpBody(4000, new Vector3(0, -2, 0), true), 0.3f);
        Assert.Equal(Vector3.Zero, bump.MoveA);
        Assert.Equal(0.3f, bump.MoveB.Y, 4);
        Assert.Equal(Vector3.Zero, bump.DeltaVA);
        Assert.Equal(2.6f, bump.DeltaVB.Y, 3); // -2 m/s down comes back as +0.6
    }

    // ---- the shipped rules

    [Fact]
    public void ACarAtFullSpeedIntoAParkedOneHurtsBoth_AGentleNudgeHurtsNeither()
    {
        var car = Shipped.Vehicles.First(v => v.Id == "car");
        var hit = new VehicleContactHit(Vector3.UnitZ, 0.3f);
        var fast = VehicleContact.Bump(hit, Moving(car.Mass, 0, 30), Moving(car.Mass, 0, 0), Shipped.BumpRestitution);
        Assert.True(fast.SpeedLossA > Shipped.CrashMinSpeedLoss);
        Assert.True(fast.SpeedLossB > Shipped.CrashMinSpeedLoss);
        var nudge = VehicleContact.Bump(hit, Moving(car.Mass, 0, 3), Moving(car.Mass, 0, 0), Shipped.BumpRestitution);
        Assert.True(nudge.SpeedLossA < Shipped.CrashMinSpeedLoss);
        Assert.True(nudge.SpeedLossB < Shipped.CrashMinSpeedLoss);
    }

    [Fact]
    public void ABusRammingABicycleWrecksTheBicycleOnly()
    {
        var bus = Shipped.Vehicles.First(v => v.Id == "bus"); var bike = Shipped.Vehicles.First(v => v.Id == "bicycle");
        var hit = new VehicleContactHit(Vector3.UnitZ, 0.3f);
        var bump = VehicleContact.Bump(hit, Moving(bus.Mass, 0, 20), Moving(bike.Mass, 0, 0), Shipped.BumpRestitution);
        var busHurt = MathF.Max(0, bump.SpeedLossA - Shipped.CrashMinSpeedLoss) * Shipped.CrashDamagePerSpeed;
        var bikeHurt = MathF.Max(0, bump.SpeedLossB - Shipped.CrashMinSpeedLoss) * Shipped.CrashDamagePerSpeed;
        Assert.Equal(0f, busHurt);
        Assert.True(bikeHurt >= bike.Hp);
    }

    [Fact]
    public void BumpRestitutionIsValidated()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json");
        var text = File.ReadAllText(path).Replace("\"bump_restitution\": 0.3", "\"bump_restitution\": 1.5");
        var tmp = Path.Combine(Path.GetTempPath(), $"vehicles-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, text);
        try { Assert.Throws<DataFileException>(() => VehiclesFile.Load(tmp)); }
        finally { File.Delete(tmp); }
    }
}
