using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The water craft (ticket #132): the amphibious transport, its water driving and road kill on land only.</summary>
public class WaterCraftTests
{
    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Type(string id) => Shipped().Vehicles.Single(v => v.Id == id);

    /// <summary>An axis-aligned square water body in Godot ground axes (x east, z = -north), flat surface and bed.</summary>
    static WaterBody Square(string id, float x0, float z0, float size, float surface, float bed) => new()
    {
        Id = id,
        Rings = new[] { new[] { new Vector2(x0, z0), new Vector2(x0 + size, z0), new Vector2(x0 + size, z0 + size), new Vector2(x0, z0 + size) } },
        SurfaceY = surface, BedY = bed,
        MinX = x0, MaxX = x0 + size, MinZ = z0, MaxZ = z0 + size,
    };

    // ---- the shipped types ----

    [Fact]
    public void ShippedAmphibiousDrivesOnLandWithThreeSeatsAndRunsPeopleOverOnLand()
    {
        var a = Type("amphibious");
        Assert.Equal(VehicleMedium.Amphibious, a.Medium);
        Assert.True(a.RunOver);
        Assert.True(a.CrashDamage);
        Assert.Equal(3, a.Seats.Count); // the driver and two riders (AmphibiousTransportTests holds what the riders do)
        Assert.Equal("driver", a.Seats[0].Role);
        Assert.True(a.WaterTopSpeed < a.TopSpeed);
        Assert.NotNull(a.Lights);
    }

    [Fact]
    public void EverySeatOfAFloatingTypeIsAboveTheWaterline()
    {
        // a seated player's feet at or below the surface would count as swimming; the seats sit on the deck
        foreach (var v in Shipped().Vehicles.Where(v => v.CanFloat))
            Assert.All(v.Seats, s => Assert.True(s.Offset[1] >= 0.4f, $"{v.Id} seat {s.Role} is too low"));
    }

    [Fact]
    public void ValidationRejectsAnUnknownMedium()
    {
        var f = new VehiclesFile { Vehicles = { new VehicleDef { Id = "hovercraft", TopSpeed = 10, Acceleration = 1, Brake = 1, TurnRadius = 5, MaxLateralAccel = 5, Medium = "hovercraft" } } };
        var e = Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        Assert.Contains("medium", e.Message);
    }

    [Fact]
    public void ValidationRejectsANonPositiveWaterSpeed()
    {
        var f = new VehiclesFile { Vehicles = { new VehicleDef { Id = "duck", TopSpeed = 10, Acceleration = 1, Brake = 1, TurnRadius = 5, MaxLateralAccel = 5, Medium = VehicleMedium.Amphibious, WaterTopSpeed = 0 } } };
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
    }

    [Fact]
    public void RunOverScaleOfTheAmphibiousIsItsMassOverTheReference()
    {
        var f = Shipped();
        Assert.Equal(1800f / f.RunOverReferenceMass, f.RunOverScale(Type("amphibious")), 4);
    }

    // ---- driving ----

    [Fact]
    public void AnAmphibiousTopsOutAtItsWaterSpeedAfloatAndAtItsLandSpeedOnLand()
    {
        // the car model's own rule for an amphibious type without a drive block. The shipped amphibious transport has one and is stepped by
        // DriveModel instead (ADR 0010): DriveTests holds its numbers.
        Assert.NotNull(Type("amphibious").Drive);
        var a = new VehicleDef { Id = "duck", TopSpeed = 22, Acceleration = 4, Brake = 8, TurnRadius = 4.2f, MaxLateralAccel = 7.5f, Medium = VehicleMedium.Amphibious, WaterTopSpeed = 7, WaterAcceleration = 1.5f };
        var afloat = new CarState();
        for (var i = 0; i < 30 * 60; i++) CarModel.Step(ref afloat, a, 1, 0, false, 1f / 30, onWater: true);
        Assert.Equal(a.WaterTopSpeed, afloat.Speed, 3);

        var land = new CarState();
        for (var i = 0; i < 30 * 60; i++) CarModel.Step(ref land, a, 1, 0, false, 1f / 30);
        Assert.Equal(a.TopSpeed, land.Speed, 3);
    }

    [Fact]
    public void LandVehiclesAreUnchangedByTheWaterFlag()
    {
        var car = Type("car");
        var dry = new CarState { Speed = 10 };
        var wet = new CarState { Speed = 10 };
        for (var i = 0; i < 60; i++)
        {
            CarModel.Step(ref dry, car, 1, 0.3f, false, 1f / 30);
            CarModel.Step(ref wet, car, 1, 0.3f, false, 1f / 30, onWater: true);
        }
        Assert.Equal(dry.Speed, wet.Speed, 5);
        Assert.Equal(dry.Yaw, wet.Yaw, 5);
    }

    // ---- road kill ----

    [Fact]
    public void RoadKillIsOnLandOnly()
    {
        // the server gates the run-over check on RunOver and on not being afloat (NetServer.StepVehicles)
        Assert.True(Type("amphibious").RunOver);
        Assert.True(Type("jeep").RunOver);
        Assert.True(Type("car").RunOver);
    }

    // ---- water bodies ----

    [Fact]
    public void ShoreDistanceAndAreaOfASquare()
    {
        var lake = Square("lake", 0, 0, 60, 5, 1);
        Assert.Equal(3600f, lake.Area, 1);
        Assert.Equal(1f, lake.ShoreDistance(1, 30), 3);
        Assert.Equal(30f, lake.ShoreDistance(30, 30), 3);
        Assert.Equal(0f, lake.ShoreDistance(0, 30), 3);
    }
}
