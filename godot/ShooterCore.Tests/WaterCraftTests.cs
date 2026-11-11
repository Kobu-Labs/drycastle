using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>The water craft (ticket #132): the boat and the amphibious transport, their water driving, road kill on land only, and water spawn placement.</summary>
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
    public void ShippedBoatIsWaterOnlyWithNoRunOverAndNoBeachingDamage()
    {
        var b = Type("boat");
        Assert.Equal(VehicleMedium.Boat, b.Medium);
        Assert.False(b.RunOver);
        Assert.False(b.CrashDamage);
        Assert.True(b.PlaningSpeed > 0);
        Assert.Null(b.Gun);
        Assert.Equal(new[] { "driver", "passenger" }, b.Seats.Select(s => s.Role));
        Assert.NotNull(b.Lights);
    }

    [Fact]
    public void ShippedAmphibiousDrivesOnLandWithFourSeatsAndRunsPeopleOverOnLand()
    {
        var a = Type("amphibious");
        Assert.Equal(VehicleMedium.Amphibious, a.Medium);
        Assert.True(a.RunOver);
        Assert.True(a.CrashDamage);
        Assert.Equal(4, a.Seats.Count);
        Assert.Equal("driver", a.Seats[0].Role);
        Assert.All(a.Seats.Skip(1), s => Assert.Equal("passenger", s.Role)); // passengers never shoot in v2
        Assert.Null(a.Gun);
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
    public void ValidationRejectsABoatWithAGun()
    {
        var f = new VehiclesFile
        {
            Vehicles =
            {
                new VehicleDef
                {
                    Id = "gunboat", TopSpeed = 10, Acceleration = 1, Brake = 1, TurnRadius = 5, MaxLateralAccel = 5, Medium = VehicleMedium.Boat,
                    Seats = { new VehicleSeatDef { Role = "driver" }, new VehicleSeatDef { Role = "gunner", Offset = new float[] { 0, 1, 0 } } },
                    Gun = new VehicleGunDef(),
                },
            },
        };
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
    public void ABoatOnLandHasNoDriveAndCoastsToAStop()
    {
        var b = Type("boat");
        var s = new CarState { Speed = 10 };
        float prev = s.Speed;
        for (var i = 0; i < 30; i++) // one second at full throttle
        {
            CarModel.Step(ref s, b, 1, 0, false, 1f / 30);
            Assert.True(s.Speed <= prev + 1e-4f, "a boat on land must never speed up");
            prev = s.Speed;
        }
        Assert.InRange(s.Speed, 4.9f, 5.1f); // 10 m/s less 5 m/s² for one second
        for (var i = 0; i < 30 * 3 && s.Speed > 0; i++) CarModel.Step(ref s, b, 0, 0, false, 1f / 30);
        Assert.Equal(0f, s.Speed);
    }

    [Fact]
    public void ABoatAfloatAcceleratesHalfAsFastBelowPlaningThenFullSpeedAbove()
    {
        var b = Type("boat");
        var s = new CarState();
        for (var i = 0; i < 30; i++) CarModel.Step(ref s, b, 1, 0, false, 1f / 30, onWater: true);
        Assert.InRange(s.Speed, 1.4f, 1.6f); // 3 m/s² at half rate below the 7 m/s planing speed

        var planing = new CarState { Speed = b.PlaningSpeed + 1 };
        for (var i = 0; i < 30; i++) CarModel.Step(ref planing, b, 1, 0, false, 1f / 30, onWater: true);
        Assert.InRange(planing.Speed, b.PlaningSpeed + 3.9f, b.PlaningSpeed + 4.1f); // full 3 m/s² once on the plane
    }

    [Fact]
    public void AnAmphibiousTopsOutAtItsWaterSpeedAfloatAndAtItsLandSpeedOnLand()
    {
        var a = Type("amphibious");
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
    public void RoadKillIsOnLandOnlyAndNotForABoat()
    {
        // the server gates the run-over check on RunOver and on not being afloat (NetServer.StepVehicles)
        Assert.False(Type("boat").RunOver);
        Assert.True(Type("amphibious").RunOver);
        Assert.True(Type("jeep").RunOver);
        Assert.True(Type("car").RunOver);
    }

    // ---- water spawn placement ----

    [Fact]
    public void BoatCandidatesLieInTheShoreBandOfADeepEnoughBody()
    {
        var water = new Water(new[] { Square("lake", 0, 0, 60, 5, 1) }); // 60 m square, 4 m deep
        var spots = WaterSpawns.ShoreCandidates(water);
        Assert.NotEmpty(spots);
        foreach (var s in spots)
        {
            var d = water.ShoreDistance(s.X, s.Z);
            Assert.InRange(d, WaterSpawns.ShoreMinDistance - 1e-3f, WaterSpawns.ShoreMaxDistance + 1e-3f);
            Assert.True(water.BodyAt(s.X, s.Z) != null);
            Assert.Equal(5f, s.SurfaceY); // the origin rides the surface
        }
        Assert.DoesNotContain(spots, s => Math.Abs(s.X - 30) < 1 && Math.Abs(s.Z - 30) < 1); // the middle is 30 m from the shore
    }

    [Fact]
    public void ShallowWaterGivesNoBoatCandidates()
    {
        var water = new Water(new[] { Square("puddle", 0, 0, 60, 5, 4) }); // 1 m deep, under the minimum
        Assert.Empty(WaterSpawns.ShoreCandidates(water));
    }

    [Fact]
    public void TooLittleShoreWaterFallsBackToTheMiddleOfTheLargestBody()
    {
        // a pond too narrow for any shore-band candidate (its widest point is 3 m from the bank)
        var pond = Square("pond", 0, 0, 6, 5, 1);
        var onlyPond = new Water(new[] { pond });
        Assert.Empty(WaterSpawns.ShoreCandidates(onlyPond));
        var fallback = WaterSpawns.FallbackCandidates(onlyPond);
        Assert.NotEmpty(fallback);
        Assert.All(fallback, s => Assert.True(s.X >= 0 && s.X <= 6 && s.Z >= 0 && s.Z <= 6));
        Assert.True(pond.ShoreDistance(fallback[0].X, fallback[0].Z) >= 1.5f - 1e-3f); // the furthest grid point from the bank first (1.5 m on a 3 m grid in a 6 m pond)

        // with a larger lake present, the fallback is that lake, not the pond
        var water = new Water(new[] { pond, Square("lake", 100, 0, 20, 5, 1) });
        var big = WaterSpawns.FallbackCandidates(water);
        Assert.NotEmpty(big);
        Assert.All(big, s => Assert.True(s.X >= 100 && s.X <= 120, "the largest body is the lake"));
        Assert.All(big, s => Assert.Equal(5f, s.SurfaceY));
        var first = water.Bodies[1].ShoreDistance(big[0].X, big[0].Z);
        Assert.All(big, s => Assert.True(water.Bodies[1].ShoreDistance(s.X, s.Z) <= first + 1e-3f));
    }

    [Fact]
    public void NoWaterGivesNoBoatSpots()
    {
        Assert.Empty(WaterSpawns.ShoreCandidates(Water.Empty));
        Assert.Empty(WaterSpawns.FallbackCandidates(Water.Empty));
    }

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
