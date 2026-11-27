using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>
/// The `cartel_van` type (the Gang Warfare SM_Veh_Van_01, #495) and the generic pieces it brought: the cargo volume (a bay in the vehicle frame and its load count) and the reserved
/// riders of a vehicle. The generic model checks are StandardizedVehicleTests and VehicleContractTests.
/// </summary>
public class CartelVanTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Van => Shipped.Vehicles.Single(v => v.Id == "cartel_van");

    [Fact]
    public void It_is_never_dealt_never_returns_and_takes_more_than_the_road_vehicles()
    {
        Assert.Equal(0, Van.SpawnCount);
        Assert.Empty(Van.FixedSpots);
        Assert.False(Van.Returns);
        Assert.NotNull(Van.Drive); // ADR 0036
        foreach (var id in new[] { "car", "golf_mk2", "truck", "humvee", "amphibious", "bus" }) Assert.True(Van.Hp > Shipped.Vehicles.Single(v => v.Id == id).Hp, id); // high hit points (docs/cartel.md)
        Assert.Equal("pack:synty-vehicles/cartel_van_white", Van.Model);
    }

    [Fact]
    public void A_driver_and_a_passenger_sit_on_the_cab_bench_behind_glass()
    {
        Assert.Equal(new[] { "driver", "passenger" }, Van.Seats.Select(s => s.Role));
        Assert.All(Enumerable.Range(0, Van.Seats.Count), i => Assert.Equal(SeatCover.Glass, Van.CoverOf(i)));
        Assert.True(Van.Seats[0].Offset[0] < 0, "the driver sits on the left (-x), behind the wheel");
        Assert.Equal(-Van.Seats[0].Offset[0], Van.Seats[1].Offset[0], 3);
        Assert.Null(Van.Gun);
    }

    [Fact]
    public void The_cargo_volume_is_the_bay_behind_the_cab_inside_the_hit_box()
    {
        var c = Van.CargoVolume!;
        Assert.Null(c.Problem(Van));
        Assert.True(c.Capacity >= 1);
        var front = c.Centre[2] - c.Size[2] / 2;
        Assert.True(Van.Seats.All(s => s.Offset[2] < front), "the bay starts behind the seats (+z is the rear)");
        Assert.True(c.Centre[2] + c.Size[2] / 2 < Van.HalfLength, "the bay ends inside the rear doors");
    }

    [Fact]
    public void A_point_is_in_the_cargo_where_the_vehicle_stands_turned_and_tilted()
    {
        var v = new VehicleCargoVolumeDef { Centre = new[] { 0f, 1.3f, 1f }, Size = new[] { 1.5f, 1.1f, 2.3f }, Capacity = 4 };
        var feet = new Vector3(100, 5, -40);
        Assert.True(CargoVolume.Contains(v, feet, 0, 0, 0, feet + new Vector3(0, 1.3f, 1f)));
        Assert.False(CargoVolume.Contains(v, feet, 0, 0, 0, feet + new Vector3(0, 1.3f, -1f)), "the cab is not the bay");
        // turned a quarter left (yaw +pi/2): the vehicle's +z (its rear) points to world +x
        var yaw = MathF.PI / 2;
        Assert.True(CargoVolume.Contains(v, feet, yaw, 0, 0, feet + new Vector3(1f, 1.3f, 0)));
        Assert.False(CargoVolume.Contains(v, feet, yaw, 0, 0, feet + new Vector3(0, 1.3f, 1f)));
        Assert.Equal(feet + new Vector3(1f, 1.3f, 0), CargoVolume.Centre(v, feet, yaw, 0, 0), new Vec3Near(1e-4f));
        // nose up on a slope: the bay's centre is lower than on the flat
        var pitch = 0.3f;
        var tilted = CargoVolume.Centre(v, feet, 0, pitch, 0);
        Assert.True(CargoVolume.Contains(v, feet, 0, pitch, 0, tilted));
        Assert.True(tilted.Y < feet.Y + 1.3f);
        // the margin grows the box on every side: just behind the doors counts with it, not without
        var behind = feet + new Vector3(0, 1.3f, 1f + 2.3f / 2 + 0.2f);
        Assert.False(CargoVolume.Contains(v, feet, 0, 0, 0, behind));
        Assert.True(CargoVolume.Contains(v, feet, 0, 0, 0, behind, margin: 0.3f));
    }

    [Fact]
    public void A_cargo_volume_outside_the_hit_box_or_without_capacity_is_refused()
    {
        string Json(string cargo) => "{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 0, "
            + "\"half_width\": 1, \"half_length\": 2.5, \"height\": 2.2, \"cargo_volume\": " + cargo + " } ] }";
        Assert.NotNull(VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(Json("{ \"centre\": [0, 1.2, 1], \"size\": [1.6, 1.0, 2.0], \"capacity\": 3 }"), "vehicles.json")).Vehicles[0].CargoVolume);
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(Json("{ \"centre\": [0, 1.2, 1.8], \"size\": [1.6, 1.0, 2.0], \"capacity\": 3 }"), "vehicles.json")));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(Json("{ \"centre\": [0, 1.9, 1], \"size\": [1.6, 1.0, 2.0], \"capacity\": 3 }"), "vehicles.json")));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(Json("{ \"centre\": [0, 1.2, 1], \"size\": [1.6, 1.0, 2.0], \"capacity\": 0 }"), "vehicles.json")));
    }

    [Fact]
    public void The_load_counts_up_to_the_capacity_and_empties()
    {
        var hold = new CargoLoad(2);
        Assert.False(hold.TryRemove());
        Assert.True(hold.TryAdd());
        Assert.True(hold.TryAdd());
        Assert.True(hold.Full);
        Assert.False(hold.TryAdd());
        Assert.Equal(2, hold.Count);
        Assert.True(hold.TryRemove());
        Assert.Equal(1, hold.Count);
        hold.Clear();
        Assert.Equal(0, hold.Count);
    }

    [Fact]
    public void A_reserved_vehicle_seats_only_its_riders()
    {
        var seats = new VehicleSeat(2);
        Assert.True(seats.Admits(7));
        seats.Reserve(new ushort[] { 3, 4 });
        Assert.True(seats.IsReserved);
        Assert.False(seats.Admits(7));
        Assert.False(seats.TryEnter(7));
        Assert.False(seats.TryBoard(7, 0, out _));
        Assert.True(seats.TryBoard(3, 0, out _));
        Assert.True(seats.TryEnter(4));
        Assert.Equal(new ushort[] { 3, 4 }, new[] { seats.Occupant(0), seats.Occupant(1) });
    }

    [Fact]
    public void A_new_reservation_keeps_who_sits_and_survives_the_seat_clearing()
    {
        var seats = new VehicleSeat(2);
        seats.Reserve(new ushort[] { 3 });
        Assert.True(seats.TryEnter(3));
        seats.Reserve(new ushort[] { 4 }); // the crew changed: the rider in it stays seated
        Assert.Equal(0, seats.SeatOf(3));
        Assert.True(seats.TrySwitch(3, 1));
        seats.Clear(); // a round's seat clearing does not lift the reservation
        Assert.False(seats.Admits(3));
        Assert.True(seats.Admits(4));
        seats.Reserve(Array.Empty<ushort>());
        Assert.False(seats.Admits(4)); // an empty crew: nobody
        seats.Unreserve();
        Assert.False(seats.IsReserved);
        Assert.True(seats.TryEnter(9));
    }

    sealed class Vec3Near : IEqualityComparer<Vector3>
    {
        readonly float _eps;
        public Vec3Near(float eps) => _eps = eps;
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= _eps;
        public int GetHashCode(Vector3 v) => 0;
    }
}
