using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>What a Gunman's round stops on (<see cref="GangRound"/>; docs/cartel.md "Chase cars"): a rider's round passes its own car and every crewed car, a round shot on foot passes none.</summary>
public class GangRoundTests
{
    static readonly VehicleDef Car = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Vehicles.Vehicles.Single(v => v.Id == new ChaseData().VehicleType);

    const ushort Rear = 1, Front = 2, Van = 3;
    static readonly Vector3 Eye = new(0, 1.2f, 0);
    static readonly Vector3 Ahead = new(0, 0, -1);

    /// <summary>The two chase cars (the rear one, the front one 12 m before it) and the van 40 m before the rear one, on a line down -Z, all facing along it.</summary>
    static RoundHull[] Line() => new[]
    {
        new RoundHull(Rear, new VehiclePose(Vector3.Zero, 0, 0, 0, 0), Car),
        new RoundHull(Front, new VehiclePose(new Vector3(0, 0, -12f), 0, 0, 0, 0), Car),
        new RoundHull(Van, new VehiclePose(new Vector3(0, 0, -40f), 0, 0, 0, 0), Car),
    };

    [Fact]
    public void ARidersRoundPassesItsOwnCarAndItsSisterCarThenStopsOnTheVan()
    {
        var stop = GangRound.Stop(Eye, Ahead, 200f, float.PositiveInfinity, Line(), Rear, new ushort[] { Rear, Front });
        Assert.Equal(Van, stop.Hull);
        Assert.InRange(stop.Distance, 40f - Car.HalfLength - 0.1f, 40f + Car.HalfLength);
    }

    [Fact]
    public void ARoundFromOneCarThatCarriesNoCrewMeetsTheSisterCar()
    {
        var stop = GangRound.Stop(Eye, Ahead, 200f, float.PositiveInfinity, Line(), Rear, new ushort[] { Rear });
        Assert.Equal(Front, stop.Hull); // the front car is not crewed: it is a car like any
    }

    [Fact]
    public void ARoundShotOnFootPassesNoCar()
    {
        var hulls = Line();
        var stop = GangRound.Stop(new Vector3(0, 1.2f, 20f), Ahead, 200f, float.PositiveInfinity, hulls, null, new ushort[] { Rear, Front });
        Assert.Equal(Rear, stop.Hull); // even a crewed car is a car to a Gunman on the ground
    }

    [Fact]
    public void AWallNearerThanTheHullStopsTheRoundAndNoHullIsNamed()
    {
        var stop = GangRound.Stop(Eye, Ahead, 200f, 25f, Line(), Rear, new ushort[] { Rear, Front });
        Assert.Equal((25f, null), (stop.Distance, stop.Hull));
    }

    [Fact]
    public void NothingInReachIsAnInfiniteStop()
    {
        var stop = GangRound.Stop(Eye, Ahead, 20f, float.PositiveInfinity, Line(), Rear, new ushort[] { Rear, Front });
        Assert.Equal((float.PositiveInfinity, null), (stop.Distance, stop.Hull)); // the van is 40 m off
    }
}
