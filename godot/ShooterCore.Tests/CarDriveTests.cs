using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The standard car under the drive model (ADR 0014): its shipped `drive` block is the car's handling on <see cref="DriveModel"/>, and the
/// numbers must stay as close to the planar <see cref="CarModel"/> it replaced as the model allows. The tests run both on the same keys on a
/// flat road and compare: launch and top speed, the brake and the coast, the circle at speed, the steering's rate, that the tyres hold the car
/// to its heading, a wall, the parked rest, and that the driver's prediction and the server step to the same state.
/// </summary>
public class CarDriveTests
{
    readonly Xunit.Abstractions.ITestOutputHelper _out;
    public CarDriveTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Car => Shipped.Vehicles.Single(v => v.Id == "car");

    /// <summary>A flat road, with an optional wall across z (the car faces -Z).</summary>
    sealed class Road : IDriveWorld
    {
        public float? WallZ;
        public float GroundY(Vector3 from) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;

        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion)
        {
            if (WallZ is { } w && motion.Z < 0 && from.Z >= w && from.Z + motion.Z < w)
                return new DriveMove(from + motion * Math.Clamp((w - from.Z) / motion.Z, 0f, 1f), true, Vector3.UnitZ);
            return new DriveMove(from + motion, false, Vector3.Zero);
        }
    }

    /// <summary>A road that rises and falls: a kerb of 0.2 m across z = -20 and a dip of 0.25 m across z = 30, so the wheels ride over both.</summary>
    sealed class Kerbs : IDriveWorld
    {
        public float GroundY(Vector3 from)
        {
            var g = 0f;
            if (from.Z < -20f && from.Z > -21f) g = 0.2f;
            if (from.Z < 30f && from.Z > 29f) g = -0.25f;
            return g <= from.Y + 1f ? g : float.NegativeInfinity;
        }
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    static DriveInput Keys(float throttle = 0, float steer = 0, bool handbrake = false) => new() { Throttle = throttle, Steer = steer, Handbrake = handbrake };

    static float Deg(float rad) => rad * 180f / MathF.PI;

    // ---- data ----

    [Fact]
    public void The_shipped_car_is_a_land_vehicle_with_a_drive_block_and_its_wheels_and_body_are_the_models()
    {
        var c = Car;
        Assert.Equal(VehicleMedium.Land, c.Medium);
        Assert.NotNull(c.Drive);
        Assert.False(c.CanFloat);
        Assert.Equal(10f, c.HighSpeedRadiusFactor); // the car's speed-dependent steering is the handling overhaul's (#134), shared by both models
        Assert.True(c.Drive!.Land.MaxClimbDeg <= 50f + 1e-3f, "it climbs no steeper than the server's floor angle allowed");
        Assert.Equal(VehicleMedium.Amphibious, Shipped.Vehicles.Single(v => v.Id == "amphibious").Medium);
        Assert.All(Shipped.Vehicles.Where(v => v.Id is not ("car" or "amphibious")), v => Assert.Null(v.Drive));
    }

    [Fact]
    public void A_land_drive_block_is_validated_without_the_water_numbers()
    {
        static VehiclesFile With(Action<VehicleDef> change)
        {
            var f = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
            change(f.Vehicles.Single(v => v.Id == "car"));
            return f;
        }
        VehiclesFile.Validate(With(_ => { }));
        // a car never floats, so the water numbers are not read and not checked
        VehiclesFile.Validate(With(v => v.Drive!.Water.CoastDrag = 5f));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Medium = VehicleMedium.Boat)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Land.Grip = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.ThrottleRise = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Camera.TurnFollow = 2)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Body.Damping = 0)));
    }

    // ---- the feel, against the car model it replaced ----

    [Fact]
    public void Full_throttle_reaches_the_car_models_speed_at_each_moment_and_stops_at_its_top_speed()
    {
        var c = new CarState();
        var d = new DriveState();
        var road = new Road();
        var top = Car.TopSpeed;
        _out.WriteLine("  t(s)   carModel  driveModel   (m/s)");
        var worst = 0f;
        for (var tick = 1; tick <= 25 * Wire.TickRate; tick++)
        {
            CarModel.Step(ref c, Car, 1, 0, false, Dt);
            DriveModel.Step(ref d, Car, Keys(1), Dt, road);
            if (tick % (2 * Wire.TickRate) == 0) _out.WriteLine($"  {tick * Dt,4:F0}   {c.Speed,8:F2}   {d.Speed,8:F2}");
            if (tick * Dt >= 0.5f) worst = MathF.Max(worst, MathF.Abs(c.Speed - d.Speed) / top);
            Assert.True(d.Speed <= top + 1e-3f, $"it passed its top speed at tick {tick}");
        }
        _out.WriteLine($"worst gap {worst:P1} of top speed after 0.5 s");
        Assert.True(worst < 0.04f, $"the speed drifts {worst:P1} from the car model");
        Assert.True(d.Speed > 0.97f * top, $"it reaches {d.Speed:F2} m/s, not its top speed {top}");
    }

    [Fact]
    public void Braking_from_top_speed_stops_in_the_car_models_distance_and_coasting_in_its_too()
    {
        var road = new Road();
        float Stop(float throttle, bool drive)
        {
            var c = new CarState { Speed = Car.TopSpeed };
            var d = new DriveState { Speed = Car.TopSpeed };
            var x = 0f;
            for (var i = 0; i < 60 * Wire.TickRate && MathF.Abs(drive ? d.Speed : c.Speed) > 0.05f; i++)
            {
                if (drive) { var p = d.Position.Z; DriveModel.Step(ref d, Car, Keys(throttle), Dt, road); x += MathF.Abs(d.Position.Z - p); }
                else { CarModel.Step(ref c, Car, throttle, 0, false, Dt); x += MathF.Abs(c.Speed) * Dt; }
            }
            return x;
        }
        var carBrake = Stop(-1, false); var driveBrake = Stop(-1, true);
        var carCoast = Stop(0, false); var driveCoast = Stop(0, true);
        _out.WriteLine($"brake: car model {carBrake:F1} m, drive model {driveBrake:F1} m; coast: car model {carCoast:F0} m, drive model {driveCoast:F0} m");
        Assert.InRange(driveBrake / carBrake, 0.95f, 1.05f);
        Assert.InRange(driveCoast / carCoast, 0.95f, 1.05f);
    }

    [Fact]
    public void The_steering_rate_at_a_given_speed_is_the_car_models_and_the_circle_is_its_speed_dependent_radius()
    {
        foreach (var speed in new[] { 5f, 15f, 40f })
        {
            // the speed is held at each tick (the throttle's share is not what is measured): the wheel alone sets the yaw rate
            var c = new CarState { Speed = speed };
            var d = new DriveState { Speed = speed };
            var road = new Road();
            for (var i = 0; i < 2 * Wire.TickRate; i++)
            {
                c.Speed = speed; d.Speed = speed;
                CarModel.Step(ref c, Car, 0, 1, false, Dt);
                DriveModel.Step(ref d, Car, Keys(0, 1), Dt, road);
            }
            var want = speed / CarModel.TurnRadiusAt(Car, speed);
            _out.WriteLine($"speed {speed}: yaw rate car {Deg(c.YawRate):F1}, drive {Deg(d.YawRate):F1} deg/s (circle {speed / want:F1} m)");
            Assert.InRange(d.YawRate / c.YawRate, 0.95f, 1.05f);
            Assert.InRange(MathF.Abs(d.YawRate) / want, 0.95f, 1.05f);
        }
    }

    [Fact]
    public void The_steering_takes_about_as_long_to_lock_as_the_car_model()
    {
        // a full lock from a standstill: time to reach 90% of the target yaw rate (the car model slews linearly, the drive model eases)
        float Time90(bool drive)
        {
            var c = new CarState { Speed = 10 };
            var d = new DriveState { Speed = 10 };
            var target = -10f / CarModel.TurnRadiusAt(Car, 10); // a right turn (steer +1) is a negative yaw
            for (var i = 1; i <= Wire.TickRate; i++)
            {
                c.Speed = 10; d.Speed = 10;
                if (drive) DriveModel.Step(ref d, Car, Keys(0, 1), Dt, new Road());
                else CarModel.Step(ref c, Car, 0, 1, false, Dt);
                var rate = drive ? d.YawRate : c.YawRate;
                if (MathF.Abs(rate) >= 0.9f * MathF.Abs(target)) return i * Dt;
            }
            return float.PositiveInfinity;
        }
        var car = Time90(false); var drive = Time90(true);
        _out.WriteLine($"90% of the lock: car model {car:F3} s, drive model {drive:F3} s");
        Assert.InRange(drive, car * 0.5f, car * 2f);
    }

    [Fact]
    public void The_tyres_hold_it_to_its_heading_at_speed_and_on_the_handbrake()
    {
        var road = new Road();
        var d = new DriveState { Speed = Car.TopSpeed };
        var worstSlip = 0f;
        for (var i = 0; i < 4 * Wire.TickRate; i++)
        {
            DriveModel.Step(ref d, Car, Keys(0.3f, i < 2 * Wire.TickRate ? 1 : -1), Dt, road);
            worstSlip = MathF.Max(worstSlip, MathF.Abs(d.Slip));
        }
        Assert.True(worstSlip < 0.05f, $"the car slid {worstSlip} m/s sideways");
        var h = new DriveState { Speed = 20 };
        var worstHand = 0f;
        for (var i = 0; i < 2 * Wire.TickRate; i++)
        {
            DriveModel.Step(ref h, Car, Keys(0, 1, handbrake: true), Dt, road);
            worstHand = MathF.Max(worstHand, MathF.Abs(h.Slip));
        }
        Assert.True(worstHand < 0.05f, $"the handbrake let it slide {worstHand} m/s");
        Assert.True(MathF.Abs(h.Speed) < 20f - 5f, "the handbrake slows it");
    }

    [Fact]
    public void A_wall_takes_the_speed_into_it_and_reports_the_impact()
    {
        var road = new Road { WallZ = -60 };
        var d = new DriveState { Position = new Vector3(0, 0, -20), Speed = 20 };
        var impact = 0f;
        for (var i = 0; i < 3 * Wire.TickRate; i++)
        {
            DriveModel.Step(ref d, Car, Keys(1), Dt, road);
            impact = MathF.Max(impact, d.Impact);
        }
        Assert.True(d.Position.Z > -60.5f, $"it went through the wall to {d.Position.Z}");
        Assert.True(impact > 0, "no impact was reported");
        Assert.True(MathF.Abs(d.Speed) < 5f, $"it still carries {d.Speed} m/s into the wall");
    }

    [Fact]
    public void It_drives_over_a_kerb_and_a_dip_on_its_wheels_without_a_wall()
    {
        var world = new Kerbs();
        var d = new DriveState { Position = new Vector3(0, 0, -10), Speed = 10 };
        var airborne = false;
        for (var i = 0; i < 4 * Wire.TickRate; i++)
        {
            DriveModel.Step(ref d, Car, Keys(0.5f), Dt, world);
            airborne |= d.Airborne;
        }
        Assert.False(airborne, "a kerb of 0.2 m is driven over, not jumped");
        Assert.True(d.Position.Z < -30, $"it stopped at {d.Position.Z}");
    }

    [Fact]
    public void A_parked_car_at_rest_is_a_fixed_point_of_the_hands_off_step()
    {
        var road = new Road();
        var d = new DriveState { Position = new Vector3(3, 0, 4), Yaw = 0.5f };
        DriveModel.Settle(ref d, Car, road);
        for (var i = 0; i < Wire.TickRate; i++)
        {
            var before = d;
            DriveModel.Step(ref d, Car, DriveInput.None, Dt, road);
            Assert.True(DriveRest.IsAtRest(before, d), $"it moved at rest on tick {i}");
        }
    }

    // ---- the prediction, against the server ----

    [Fact]
    public void Prediction_and_the_server_step_to_the_same_state_for_the_car_from_the_same_inputs()
    {
        var world = new Kerbs();
        var server = new DriveState { Position = new Vector3(0, 0, -5) };
        var client = new DrivePrediction(Car, world, server);
        ushort seq = 0;
        var rng = new Random(258);
        var t = 0f;
        for (var i = 0; i < 40 * Wire.TickRate; i++)
        {
            // a driver who takes the lock, the throttle and the handbrake now and then, as people do
            if (i % 45 == 0) t = (float)rng.NextDouble() * 2 - 1;
            var keys = Keys(i % 300 < 230 ? 1f : -0.6f, t * 0.8f, i % 97 < 6);
            var ni = new NetInput { Seq = ++seq, MoveX = Quant.Axis(keys.Steer), MoveY = Quant.Axis(keys.Throttle), Buttons = (ushort)(keys.Handbrake ? MoveButtons.Jump : MoveButtons.None) };
            DriveModel.Step(ref server, Car, DriveInput.FromNet(ni), Dt, world);
            client.Step(seq, DriveInput.FromNet(ni), Dt);
            Assert.Equal(server.Position, client.State.Position);
            Assert.Equal(server.Speed, client.State.Speed);
            if (seq % 3 == 0)
            {
                // a snapshot acknowledges the input: on a flat-enough course nothing is corrected, through the snapshot's quantisation
                var vehicle = VehicleRecord.Make(VehicleRecord.FirstId, server.Position, server.Yaw, 1, 0);
                Assert.False(client.Reconcile(seq, DriveRecord.State(vehicle, DriveRecord.Make(VehicleRecord.FirstId, server)), Dt), $"corrected at input {seq}");
            }
        }
        Assert.Equal(0, client.Corrections);
        Assert.True(client.MaxError < 0.02f, $"largest error {client.MaxError}");
    }

    [Fact]
    public void A_server_that_disagrees_about_a_car_is_taken_and_the_inputs_on_the_way_are_replayed()
    {
        var world = new Road();
        var start = new DriveState { Speed = 30 };
        var client = new DrivePrediction(Car, world, start);
        var server = start;
        var inputs = new List<DriveInput>();
        for (ushort seq = 1; seq <= 10; seq++)
        {
            var keys = Keys(1, seq < 5 ? 1 : 0);
            inputs.Add(keys);
            client.Step(seq, keys, Dt);
        }
        // a bump with another vehicle the client cannot see: it takes 4 m/s off the speed
        for (var i = 0; i < 4; i++) DriveModel.Step(ref server, Car, inputs[i], Dt, world);
        server.Speed -= 4;
        Assert.True(client.Reconcile(4, server, Dt));
        var expect = server;
        for (var i = 4; i < 10; i++) DriveModel.Step(ref expect, Car, inputs[i], Dt, world);
        Assert.True(Vector3.Distance(expect.Position, client.State.Position) < 0.02f);
        Assert.Equal(expect.Speed, client.State.Speed, 2);
    }
}
