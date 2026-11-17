using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The Golf Mk2 (vehicles.json golf_mk2, scripts/make_golf_mk2_model.py): its shipped entry against the model's nodes, its 200 km/h handling on
/// the drive model, the fixed spot it stands at instead of the random deal, and the road heading that spot takes (<see cref="RoadHeading"/>).
/// </summary>
public class GolfMk2Tests
{
    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static VehiclesFile Load() => VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static readonly VehiclesFile Shipped = Load();
    static VehicleDef Golf => Shipped.Vehicles.Single(v => v.Id == "golf_mk2");

    // ---- data ----

    [Fact]
    public void It_is_a_four_seat_car_with_a_drive_block_and_is_never_dealt_at_random()
    {
        var g = Golf;
        Assert.Equal(0, g.SpawnCount);
        Assert.Equal(VehicleMedium.Land, g.Medium);
        Assert.NotNull(g.Drive);
        Assert.Equal(new[] { "driver", "passenger", "passenger", "passenger" }, g.Seats.Select(s => s.Role));
        Assert.Equal(1, g.Variants); // one fixed paint: black
        Assert.Equal(55.6f, g.TopSpeed, 2);
        Assert.InRange(g.TopSpeed * 3.6f, 199f, 201f);
        Assert.True(Shipped.Vehicles.Count <= 16);
    }

    [Fact]
    public void It_stands_at_one_fixed_spot_in_the_mechanic_garage_nose_to_the_road()
    {
        var spot = Assert.Single(Golf.FixedSpots);
        var site = MechanicGarageSite.GolfSpot;
        Assert.Equal(site.X, spot.X, 2);
        Assert.Equal(site.Z, spot.Z, 2);
        Assert.Equal(site.YawDeg, spot.YawDeg!.Value, 1);
        Assert.Equal(new[] { "utility_helicopter" }, Shipped.Vehicles.Where(v => v.Id != "golf_mk2" && v.FixedSpots.Count > 0).Select(v => v.Id)); // the heliport (HeliportTests)
    }

    [Fact]
    public void Its_wheelbase_and_track_are_the_models()
    {
        var n = AmphibiousModelTests.Nodes(Golf.Model);
        Assert.Equal(Golf.Drive!.Wheelbase, MathF.Abs(n["wheel_fl"].Z - n["wheel_rl"].Z), 2);
        Assert.Equal(Golf.Drive.Track, MathF.Abs(n["wheel_fl"].X - n["wheel_fr"].X), 2);
    }

    [Fact]
    public void Its_model_has_the_nodes_the_client_looks_for()
    {
        var n = AmphibiousModelTests.Nodes(Golf.Model);
        foreach (var name in new[] { "hull", "wheel_fl_steer", "wheel_fr_steer", "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr", "seat_driver", "light_head_l", "light_tail_r" })
            Assert.True(n.ContainsKey(name), $"the model has no node {name}");
        for (var i = 0; i < Golf.Glass.Count; i++) Assert.True(n.ContainsKey(NetClientPaneName(i)), $"the model has no node for pane {i}");
        Assert.Equal(n["wheel_fl_steer"], n["wheel_fl"]);
        foreach (var w in new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" }) Assert.InRange(n[w].Y, 0.30f, 0.45f);
    }

    static string NetClientPaneName(int index) => $"glass_{index}"; // NetClient.PaneNodeName (the client is not in the test project)

    [Fact]
    public void Its_seats_and_lamps_are_the_models_and_inside_its_footprint()
    {
        var g = Golf; var n = AmphibiousModelTests.Nodes(g.Model);
        var seats = new[] { "seat_driver", "seat_passenger_1", "seat_passenger_2", "seat_passenger_3" };
        for (var i = 0; i < seats.Length; i++)
        {
            Assert.True(Vector3.Distance(n[seats[i]], new Vector3(g.Seats[i].Offset[0], g.Seats[i].Offset[1], g.Seats[i].Offset[2])) < 0.006f, seats[i]);
            Assert.InRange(MathF.Abs(g.Seats[i].Offset[0]), 0.2f, g.HalfWidth);
            Assert.InRange(MathF.Abs(g.Seats[i].Offset[2]), 0f, g.HalfLength);
        }
        Assert.True(g.Seats[0].Offset[0] < 0, "left-hand drive: the driver sits on the left");
        var lights = g.Lights!;
        Assert.Equal(2, lights.Head.Count);
        Assert.Equal(2, lights.Tail.Count);
        var names = new[] { "light_head_l", "light_head_r", "light_tail_l", "light_tail_r" };
        var lamps = lights.Head.Concat(lights.Tail).ToList();
        for (var i = 0; i < names.Length; i++) Assert.True(Vector3.Distance(n[names[i]], new Vector3(lamps[i][0], lamps[i][1], lamps[i][2])) < 0.006f, names[i]);
    }

    [Fact]
    public void Its_hit_box_covers_the_model()
    {
        var g = Golf;
        Assert.InRange(g.HalfLength, 2.6f, 2.75f); // the body is 4.28 m x 1.25 long
        Assert.InRange(g.HalfWidth, 1.05f, 1.25f);
        Assert.InRange(g.Height, 1.75f, 1.85f);   // the roof is 1.42 m x 1.25
    }

    [Fact]
    public void The_fixed_spots_are_validated()
    {
        VehiclesFile With(Action<VehicleDef> change) { var f = Load(); change(f.Vehicles.Single(v => v.Id == "golf_mk2")); return f; }
        VehiclesFile.Validate(With(_ => { }));
        VehiclesFile.Validate(With(v => v.FixedSpots.Add(new VehicleSpotDef { X = 1, Z = 2, YawDeg = 90 })));
        VehiclesFile.Validate(With(v => v.SpawnCount = 0));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.SpawnCount = -1)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.FixedSpots.Add(new VehicleSpotDef { X = float.NaN }))));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.FixedSpots.Add(new VehicleSpotDef { YawDeg = float.PositiveInfinity }))));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => { for (var i = 0; i < 8; i++) v.FixedSpots.Add(new VehicleSpotDef { X = i }); })));
    }

    [Fact]
    public void A_spot_in_the_json_reads_its_yaw_override()
    {
        var f = DataFile.Parse<VehiclesFile>("{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 0, "
            + "\"fixed_spots\": [ { \"x\": 1.5, \"z\": -2 }, { \"x\": 3, \"z\": 4, \"yaw_deg\": 90 } ] } ] }", "test");
        var spots = f.Vehicles[0].FixedSpots;
        Assert.Equal(2, spots.Count);
        Assert.Equal((1.5f, -2f), (spots[0].X, spots[0].Z));
        Assert.Null(spots[0].YawDeg);
        Assert.Equal(90f, spots[1].YawDeg);
    }

    // ---- 200 km/h on the drive model ----

    sealed class Flat : IDriveWorld
    {
        public float GroundY(Vector3 from) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    static DriveInput Keys(float throttle = 0, float steer = 0) => new() { Throttle = throttle, Steer = steer };

    [Fact]
    public void Full_throttle_reaches_two_hundred_km_h_in_about_ten_seconds_and_no_more()
    {
        var g = Golf; var s = new DriveState(); var world = new Flat();
        var t = 0f; float? at100 = null, at190 = null;
        for (; t < 30f; t += Dt)
        {
            DriveModel.Step(ref s, g, Keys(1), Dt, world);
            at100 ??= s.Speed * 3.6f >= 100f ? t : null;
            at190 ??= s.Speed * 3.6f >= 190f ? t : null;
        }
        Assert.InRange(at100!.Value, 2f, 6f);
        Assert.InRange(at190!.Value, 6f, 16f);
        Assert.Equal(g.TopSpeed, s.Speed, 1);
        Assert.True(s.Speed <= g.TopSpeed + 1e-3f);
    }

    [Fact]
    public void Its_speed_travels_in_the_drive_record_and_a_wall_at_top_speed_is_a_crash_that_wrecks_it()
    {
        var g = Golf;
        var rec = DriveRecord.Make(VehicleRecord.FirstId, new DriveState { Speed = g.TopSpeed });
        Assert.Equal(g.TopSpeed, DriveRecord.Speed(rec), 1); // 1/128 m/s units in an int: 200 km/h is far inside the range
        var loss = g.TopSpeed - 0f;
        var damage = (loss - Shipped.CrashMinSpeedLoss) * Shipped.CrashDamagePerSpeed;
        Assert.True(damage >= g.Hp, "a full-speed wall is the end of it, as for the car");
        Assert.True(Shipped.Vehicles.Single(v => v.Id == "car").TopSpeed < g.TopSpeed);
    }

    [Fact]
    public void It_turns_in_a_circle_at_top_speed_that_is_wider_than_the_lock_and_not_wider_than_the_cars_yaw_rate_allows()
    {
        var g = Golf; var s = new DriveState { Speed = g.TopSpeed }; var world = new Flat();
        for (var i = 0; i < 60; i++) DriveModel.Step(ref s, g, Keys(1, 1), Dt, world);
        var yawRate = MathF.Abs(s.YawRate);
        var car = Shipped.Vehicles.Single(v => v.Id == "car");
        Assert.InRange(yawRate, 0.5f, 1.4f); // it is steerable at 200 km/h, and no twitchier than the car at its top speed
        Assert.True(CarModel.TurnRadiusAt(g, g.TopSpeed) > CarModel.TurnRadiusAt(g, 0) * 5);
        Assert.True(g.TurnRadius <= car.TurnRadius + 0.5f);
    }

    // ---- the heading of the fixed spot ----

    static RoadLine Road(string highway, params (float X, float Y)[] pts) => new(pts.Select(p => new Vector2(p.X, p.Y)).ToArray(), 5f, highway);

    /// <summary>Where a vehicle with this yaw faces, on the ground (x, z).</summary>
    static Vector2 Facing(float yaw) => new(-MathF.Sin(yaw), -MathF.Cos(yaw));

    [Theory]
    [InlineData(0f, -1f)]      // north is -z: yaw 0
    [InlineData(1f, 0f)]       // east: a right turn from north
    [InlineData(-1f, 0f)]      // west: a left turn
    [InlineData(0f, 1f)]       // south
    [InlineData(0.6f, -0.8f)]
    public void A_direction_on_the_ground_is_a_yaw_that_faces_that_way_in_the_games_convention(float dx, float dz)
    {
        var f = Facing(RoadHeading.YawOf(dx, dz));
        Assert.Equal(dx, f.X, 4);
        Assert.Equal(dz, f.Y, 4);
        Assert.Equal(0f, RoadHeading.YawOf(0, -1), 5);
        Assert.Equal(MathF.PI / 2, RoadHeading.YawOf(-1, 0), 5); // positive turns left
    }

    [Fact]
    public void It_faces_along_the_nearest_road_whichever_way_the_polyline_was_written()
    {
        // a road running north-north-east in roads.json axes (x east, y north), 10 m off the spot
        var up = new[] { Road("residential", (30, -50), (40, 50)) };
        var down = new[] { Road("residential", (40, 50), (30, -50)) };
        var a = RoadHeading.YawAt(up, 37, -7);
        var b = RoadHeading.YawAt(down, 37, -7);
        Assert.NotNull(a);
        Assert.Equal(a!.Value, b!.Value, 5);
        var f = Facing(a.Value);
        Assert.True(f.X > 0, "the way that heads east");
        Assert.True(f.Y < 0, "and here north too (-z)");
        Assert.Equal(10f / 100f, f.X / -f.Y, 3); // along the road: 10 m east per 100 m north
    }

    [Fact]
    public void A_road_running_north_to_south_is_taken_northwards()
    {
        var f = Facing(RoadHeading.YawAt(new[] { Road("tertiary", (0, 40), (0, -40)) }, 3, 0)!.Value);
        Assert.Equal(0f, f.X, 4);
        Assert.Equal(-1f, f.Y, 4);
        f = Facing(RoadHeading.YawAt(new[] { Road("tertiary", (0, -40), (0, 40)) }, 3, 0)!.Value);
        Assert.Equal(-1f, f.Y, 4);
    }

    [Fact]
    public void An_east_west_road_is_taken_eastwards_and_the_spot_z_is_the_negative_of_the_roads_y()
    {
        var road = new[] { Road("residential", (-50, 5), (50, 5)) }; // y 5 north = z -5
        Assert.NotNull(RoadHeading.YawAt(road, 0, -5));
        Assert.Null(RoadHeading.YawAt(road, 0, 5, 3f)); // z +5 is 10 m south of it
        var f = Facing(RoadHeading.YawAt(road, 0, -8)!.Value);
        Assert.Equal(1f, f.X, 4);
        Assert.Equal(0f, f.Y, 4);
    }

    [Fact]
    public void The_nearest_of_two_roads_decides_and_footpaths_do_not_count()
    {
        var far = Road("residential", (-50, 12), (50, 12));        // east-west, 12 m north of the spot
        var near = Road("residential", (4, -50), (4, 50));         // north-south, 4 m east of it
        var path = Road("footway", (-50, 1), (50, 1));             // east-west, 1 m away, but a footpath
        var f = Facing(RoadHeading.YawAt(new[] { far, near, path }, 0, 0)!.Value);
        Assert.Equal(0f, f.X, 4);
        Assert.Equal(-1f, f.Y, 4);
        Assert.Null(RoadHeading.YawAt(new[] { path }, 0, 0));
    }

    [Fact]
    public void With_no_road_close_by_the_yaw_is_zero_and_a_yaw_of_the_spot_wins()
    {
        var far = new[] { Road("residential", (100, -50), (100, 50)) };
        Assert.Null(RoadHeading.YawAt(far, 0, 0));
        Assert.Equal(0f, RoadHeading.YawFor(new VehicleSpotDef { X = 0, Z = 0 }, far));
        Assert.Equal(0f, RoadHeading.YawFor(new VehicleSpotDef { X = 0, Z = 0 }, Array.Empty<RoadLine>()));
        var near = new[] { Road("residential", (-50, 0), (50, 0)) };
        Assert.Equal(MathF.PI / 2, RoadHeading.YawFor(new VehicleSpotDef { X = 0, Z = 0, YawDeg = 90 }, near), 5);
        Assert.Equal(-MathF.PI / 2, RoadHeading.YawFor(new VehicleSpotDef { X = 0, Z = 0 }, near), 5); // east
    }

    [Fact]
    public void A_degenerate_road_is_skipped()
    {
        var roads = new[] { Road("residential", (3, 0), (3, 0)), Road("residential", (3, 0)) };
        Assert.Null(RoadHeading.YawAt(roads, 0, 0));
    }

    [Fact]
    public void The_village_road_near_the_spot_is_found_within_the_default_reach()
    {
        // the village's residential street that passes 10 m west of (37, -7): a south-south-west to north-north-east segment
        var street = new[] { Road("residential", (52.603f, 29.359f), (35.242f, -41.876f)) };
        var yaw = RoadHeading.YawAt(street, 37, -7);
        Assert.NotNull(yaw);
        var f = Facing(yaw!.Value);
        Assert.True(f.X > 0 && f.Y < 0); // east-north-east
        Assert.InRange(f.X / -f.Y, 0.2f, 0.3f);
    }
}
