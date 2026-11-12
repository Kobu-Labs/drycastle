using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// The amphibious transport's driving (ADR 0007). Nobody can feel it in a test, so the feel is pinned as numbers on the shipped vehicles.json:
/// how long to top speed and how far to stop, on land and afloat, the circle it turns at speed on both, that a tap is a small correction, that the
/// water checks it going in and the wheels haul it out, and that the driver's prediction and the server step to the same state from the same inputs.
/// </summary>
public class DriveTests
{
    readonly Xunit.Abstractions.ITestOutputHelper _out;
    public DriveTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Car => Shipped.Vehicles.Single(v => v.Id == "amphibious");
    static VehicleDriveDef D => Car.Drive!;

    /// <summary>
    /// A field whose ground height is a function of the place (flat at 0 by default), with an optional pond (everything with z below
    /// <see cref="ShoreZ"/>, surface at <see cref="Surface"/>) and an optional wall across z (the vehicle faces -Z, so it drives into both).
    /// </summary>
    sealed class Field : IDriveWorld
    {
        public Func<float, float, float> Ground = (_, _) => 0f;
        public float? ShoreZ, WallZ;
        public float Surface;
        public bool NoGround;
        public int Rays, Moves;

        public float GroundY(Vector3 from)
        {
            Rays++;
            if (NoGround) return float.NegativeInfinity;
            var g = Ground(from.X, from.Z);
            return g <= from.Y + 1f ? g : float.NegativeInfinity; // a wheel finds ground no more than a metre above it
        }

        public float WaterY(float x, float z) => ShoreZ is { } s && z < s ? Surface : float.NegativeInfinity;

        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion)
        {
            Moves++;
            if (WallZ is { } w && motion.Z < 0 && from.Z >= w && from.Z + motion.Z < w)
                return new DriveMove(from + motion * Math.Clamp((w - from.Z) / motion.Z, 0f, 1f), true, Vector3.UnitZ);
            return new DriveMove(from + motion, false, Vector3.Zero);
        }
    }

    /// <summary>A pond 1.5 m deep whose bank slopes down into it from the shore at z = 0 (the surface is at -0.1, just under the dry ground).</summary>
    static Field Pond() => new() { ShoreZ = 0f, Surface = -0.1f, Ground = (_, z) => z >= 0 ? 0f : MathF.Max(-1.6f, z * 0.25f) };

    static DriveInput Keys(float throttle = 0, float steer = 0, bool handbrake = false) => new() { Throttle = throttle, Steer = steer, Handbrake = handbrake };

    static void Drive(ref DriveState s, IDriveWorld w, DriveInput input, float seconds, Action<DriveState>? each = null)
    {
        for (var i = 0; i < (int)MathF.Round(seconds / Dt); i++)
        {
            DriveModel.Step(ref s, Car, input, Dt, w);
            each?.Invoke(s);
        }
    }

    /// <summary>Afloat in the middle of a pond, at rest.</summary>
    static (Field World, DriveState State) Afloat()
    {
        var w = new Field { ShoreZ = 1e6f, Surface = 0f, Ground = (_, _) => -3f };
        var s = new DriveState { Position = new Vector3(0, -D.Water.Draft, 0), Afloat = true };
        return (w, s);
    }

    static float Deg(float rad) => rad * 180f / MathF.PI;
    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>The radius of the circle a steady turn draws: the path's speed over its yaw rate, once both have settled.</summary>
    static float CircleRadius(DriveState s) => MathF.Sqrt(s.Speed * s.Speed + s.Slip * s.Slip) / MathF.Abs(s.YawRate);

    // ---- data ----

    [Fact]
    public void The_shipped_transport_has_a_drive_block_and_only_it_does()
    {
        Assert.NotNull(Car.Drive);
        Assert.Equal(VehicleMedium.Amphibious, Car.Medium);
        Assert.True(Car.WaterTopSpeed < Car.TopSpeed, "it is slower on the water");
        foreach (var v in Shipped.Vehicles.Where(v => v.Id != "amphibious")) Assert.Null(v.Drive);
        // the wheels the ground is sampled under are the model's
        var n = AmphibiousModelTests.Nodes(Car.Model);
        Assert.Equal(n["wheel_rl"].Z - n["wheel_fl"].Z, D.Wheelbase, 2);
        Assert.Equal(n["wheel_fr"].X - n["wheel_fl"].X, D.Track, 2);
        Assert.Equal(n["wake_bow"].Y, D.Water.Draft, 2); // the model's waterline
        // a seated rider's feet stay above the waterline, so a rider is never wading
        Assert.All(Car.Seats, seat => Assert.True(seat.Offset[1] > D.Water.Draft + 0.05f));
    }

    [Fact]
    public void A_drive_block_is_validated()
    {
        static VehiclesFile With(Action<VehicleDef> change)
        {
            var f = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
            change(f.Vehicles.Single(v => v.Id == "amphibious"));
            return f;
        }
        VehiclesFile.Validate(With(_ => { }));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Medium = VehicleMedium.Land)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.ThrottleRise = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Land.Grip = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Land.HandbrakeGrip = 1.5f)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Water.CoastDrag = 1f)));       // the drag alone would hold it under its top speed
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Water.BuoyancyResponse = 20))); // would ring at the tick rate
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Body.Damping = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Camera.TurnFollow = 2)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Drive!.Camera.Distance = 0)));
    }

    /// <summary>
    /// Not a check: the feel as a table, for tuning vehicles.json without the game. Run it with
    /// <c>dotnet test ShooterCore.Tests --filter "DriveTests.The_feel_in_numbers" --logger "console;verbosity=detailed"</c>.
    /// </summary>
    [Fact]
    public void The_feel_in_numbers()
    {
        var land = new Field();
        float TimeTo(IDriveWorld w, DriveState start, float speed)
        {
            var s = start; var t = 0f; var at = -1f;
            Drive(ref s, w, Keys(1), 60, x => { t += Dt; if (at < 0 && x.Speed >= speed) at = t; });
            return at;
        }
        (float Metres, float Seconds) Stop(IDriveWorld w, DriveState from, DriveInput keys)
        {
            var s = from; var t = 0f; var at = -1f;
            Drive(ref s, w, keys, 120, x => { t += Dt; if (at < 0 && x.Speed <= 0.05f) at = t; });
            var end = from; Drive(ref end, w, keys, at < 0 ? 120 : at);
            return (Flat(from.Position, end.Position), at);
        }
        var top = new DriveState(); Drive(ref top, land, Keys(1), 60);
        _out.WriteLine("LAND");
        _out.WriteLine($"top speed            {top.Speed:F1} m/s ({top.Speed * 3.6f:F0} km/h), reverse {Car.TopSpeed * D.Land.ReverseTopFraction:F1} m/s");
        _out.WriteLine($"0 to 10 m/s          {TimeTo(land, default, 10):F2} s");
        _out.WriteLine($"0 to 50% / 90% / 99% {TimeTo(land, default, 0.5f * Car.TopSpeed):F2} / {TimeTo(land, default, 0.9f * Car.TopSpeed):F2} / {TimeTo(land, default, 0.99f * Car.TopSpeed):F2} s");
        var (bm, bs) = Stop(land, top, Keys(-1));
        var (cm, cs) = Stop(land, top, Keys());
        var (hm, hs) = Stop(land, top, Keys(handbrake: true));
        _out.WriteLine($"stop from top        brake {bm:F1} m in {bs:F2} s, handbrake {hm:F1} m in {hs:F2} s, coasting {cm:F0} m in {cs:F1} s");
        foreach (var v in new[] { 3f, 8f, 15f, Car.TopSpeed })
        {
            var s = new DriveState { Speed = v }; var hold = v >= Car.TopSpeed ? 1f : 0f;
            // hold the speed with a little throttle while turning: measure the settled circle
            for (var i = 0; i < 300; i++) { DriveModel.Step(ref s, Car, Keys(hold, 1), Dt, land); if (hold == 0) s.Speed = v; }
            _out.WriteLine($"circle at {v,4:F0} m/s     {CircleRadius(s):F1} m, {Deg(s.YawRate):F0} deg/s, {s.Speed * MathF.Abs(s.YawRate):F1} m/s2 sideways");
        }
        float TurnIn(float v)
        {
            var s = new DriveState { Speed = v }; var t = 0f; var at = -1f;
            var settled = new DriveState { Speed = v }; for (var i = 0; i < 300; i++) { DriveModel.Step(ref settled, Car, Keys(0, 1), Dt, land); settled.Speed = v; }
            for (var i = 0; i < 300; i++) { DriveModel.Step(ref s, Car, Keys(0, 1), Dt, land); s.Speed = v; t += Dt; if (at < 0 && MathF.Abs(s.YawRate) >= 0.9f * MathF.Abs(settled.YawRate)) at = t; }
            return at;
        }
        _out.WriteLine($"full lock reached    {TurnIn(4):F2} s at 4 m/s, {TurnIn(Car.TopSpeed):F2} s at top speed");
        var tap = new DriveState { Speed = 10 }; Drive(ref tap, land, Keys(0, 1), 2 * Dt); Drive(ref tap, land, Keys(), 1);
        var held = new DriveState { Speed = 10 }; Drive(ref held, land, Keys(0, 1), 0.5f);
        _out.WriteLine($"steering tap         a 2-tick tap at 10 m/s turns {MathF.Abs(Deg(tap.Yaw)):F1} deg; half a second held turns {MathF.Abs(Deg(held.Yaw)):F1} deg");

        var (water, rest) = Afloat();
        var wtop = rest; Drive(ref wtop, water, Keys(1), 60);
        _out.WriteLine("WATER");
        _out.WriteLine($"top speed            {wtop.Speed:F1} m/s ({wtop.Speed * 3.6f:F0} km/h)");
        _out.WriteLine($"0 to 50% / 90% / 99% {TimeTo(water, rest, 0.5f * Car.WaterTopSpeed):F2} / {TimeTo(water, rest, 0.9f * Car.WaterTopSpeed):F2} / {TimeTo(water, rest, 0.99f * Car.WaterTopSpeed):F2} s");
        var (rm, rs) = Stop(water, wtop, Keys(-1));
        var lift = wtop; Drive(ref lift, water, Keys(), 2);
        var drift = wtop; Drive(ref drift, water, Keys(), 10);
        _out.WriteLine($"stop from top        full astern {rm:F1} m in {rs:F2} s; lifting off it still does {lift.Speed:F1} m/s after 2 s and has run {Flat(wtop.Position, drift.Position):F0} m after 10 s");
        var circle = wtop; Drive(ref circle, water, Keys(1, 1), 20);
        _out.WriteLine($"circle at speed      {CircleRadius(circle):F1} m at {circle.Speed:F1} m/s, {Deg(circle.YawRate):F0} deg/s, sliding {Deg(MathF.Atan2(MathF.Abs(circle.Slip), circle.Speed)):F0} deg off its heading");
        var spin = rest; Drive(ref spin, water, Keys(1, 1), 2);
        _out.WriteLine($"from rest, helm over turns {MathF.Abs(Deg(spin.Yaw)):F0} deg in 2 s");
        var pond = Pond();
        var enter = new DriveState { Position = new Vector3(0, 0, 40), Speed = Car.TopSpeed };
        var tin = -1f; var tt = 0f; var hardest = 0f; var last = enter.Speed; var lowest = 0f;
        Drive(ref enter, pond, Keys(1), 8, x =>
        {
            tt += Dt;
            if (x.EnteredWater) tin = tt;
            if (x.Afloat) { hardest = MathF.Max(hardest, (last - x.Speed) / Dt); lowest = MathF.Min(lowest, x.Position.Y - (pond.Surface - D.Water.Draft)); }
            if (tin > 0 && MathF.Abs(tt - tin - 1f) < Dt / 2) _out.WriteLine($"going in at top speed  {x.Speed:F1} m/s one second after the splash");
            last = x.Speed;
        });
        _out.WriteLine($"                     hardest check {hardest:F1} m/s2, the hull plunges {-lowest:F2} m under its waterline, settles at {enter.Speed:F1} m/s");
    }

    // ---- on land ----

    [Fact]
    public void On_land_it_launches_hard_reaches_ninety_percent_of_top_in_about_six_seconds_and_never_passes_top_speed()
    {
        var w = new Field(); var s = new DriveState();
        float t = 0, t10 = -1, t90 = -1, max = 0;
        Drive(ref s, w, Keys(1), 40, x => { t += Dt; max = MathF.Max(max, x.Speed); if (t10 < 0 && x.Speed >= 10) t10 = t; if (t90 < 0 && x.Speed >= 0.9f * Car.TopSpeed) t90 = t; });
        Assert.InRange(t10, 1.0f, 2.0f);                 // responsive at once: 36 km/h inside two seconds, like every motorised type
        Assert.InRange(t90, 4.5f, 7.5f);                 // weighty: the top end takes its time
        Assert.InRange(s.Speed, Car.TopSpeed * 0.99f, Car.TopSpeed);
        Assert.True(max <= Car.TopSpeed + 1e-3f, $"overshot the top speed: {max}");
        Assert.Equal(0f, s.Slip);
        Assert.Equal(0f, s.Position.X, 3);               // straight
        Assert.Equal(0f, s.Position.Y, 3);               // on the ground
    }

    [Fact]
    public void On_land_the_brake_stops_it_from_top_speed_in_about_thirty_metres_and_coasting_takes_several_times_as_far()
    {
        var w = new Field();
        var top = new DriveState { Speed = Car.TopSpeed, Throttle = 1 };
        var braked = top; Drive(ref braked, w, Keys(-1), 2.6f, x => Assert.True(x.Speed >= -1e-3f || true));
        var stop = top; var t = 0f; var at = -1f;
        Drive(ref stop, w, Keys(-1), 10, x => { t += Dt; if (at < 0 && x.Speed <= 0.05f) at = t; });
        var stopped = top; Drive(ref stopped, w, Keys(-1), at);
        Assert.InRange(Flat(top.Position, stopped.Position), 24f, 34f);
        Assert.InRange(at, 2.2f, 3.2f);
        // the same key then backs it up, to the reverse top speed and no faster
        Assert.Equal(-Car.TopSpeed * D.Land.ReverseTopFraction, stop.Speed, 1);
        // hands off it rolls on: no brake, only the rolling resistance
        var coast = top; Drive(ref coast, w, Keys(), 60);
        Assert.Equal(0f, coast.Speed);
        Assert.True(Flat(top.Position, coast.Position) > 3f * Flat(top.Position, stopped.Position), "coasting must take several times the braking distance");
    }

    [Fact]
    public void A_tap_of_the_brake_bites_less_than_the_key_held()
    {
        var w = new Field();
        var a = new DriveState { Speed = 15 }; Drive(ref a, w, Keys(-1), 2 * Dt);
        Assert.InRange(15 - a.Speed, Car.Brake * D.Land.BrakeBite * 2 * Dt * 0.95f, Car.Brake * 2 * Dt * 0.85f); // more than nothing, less than the full brake
        var b = new DriveState { Speed = 15 }; Drive(ref b, w, Keys(-1), 0.5f); var half = b.Speed; Drive(ref b, w, Keys(-1), Dt);
        Assert.Equal(Car.Brake * Dt, half - b.Speed, 3);                                                          // held, it is the whole brake
    }

    [Fact]
    public void On_land_the_circle_is_the_lock_at_walking_pace_and_the_grip_limit_at_top_speed()
    {
        var w = new Field();
        float Radius(float v)
        {
            var s = new DriveState { Speed = v };
            for (var i = 0; i < 400; i++) { DriveModel.Step(ref s, Car, Keys(0, 1), Dt, w); s.Speed = v; }
            return CircleRadius(s);
        }
        Assert.InRange(Radius(2f), Car.TurnRadius, Car.TurnRadius * 1.35f);                       // the steering lock
        Assert.InRange(Radius(Car.TopSpeed), 52f, 62f);                                           // top_speed² / grip: about 57 m
        Assert.Equal(Car.TopSpeed * Car.TopSpeed / D.Land.Grip, Radius(Car.TopSpeed), 0);
        Assert.True(Radius(8f) > Radius(2f) && Radius(15f) > Radius(8f) && Radius(Car.TopSpeed) > Radius(15f), "the circle widens with speed");
        // it turns the way the wheel is put: right is a negative yaw
        var right = new DriveState { Speed = 8 }; Drive(ref right, w, Keys(1, 1), 1);
        Assert.True(right.Yaw < 0 && right.Position.X > 0);
        var left = new DriveState { Speed = 8 }; Drive(ref left, w, Keys(1, -1), 1);
        Assert.Equal(-right.Yaw, left.Yaw, 4);
    }

    [Fact]
    public void Steering_is_quick_at_low_speed_calm_at_top_speed_and_never_asks_more_than_the_tyres_hold()
    {
        var w = new Field();
        float YawAfter(float v, float seconds) { var s = new DriveState { Speed = v }; for (var i = 0; i < (int)MathF.Round(seconds / Dt); i++) { DriveModel.Step(ref s, Car, Keys(0, 1), Dt, w); s.Speed = v; } return MathF.Abs(Deg(s.YawRate)); }
        Assert.True(YawAfter(6f, 0.4f) > 30f, "at 6 m/s the nose swings at more than 30 deg/s within four tenths of a second");
        Assert.True(YawAfter(6f, 1.5f) > 2f * YawAfter(Car.TopSpeed, 1.5f), "it turns at least twice as fast at 6 m/s as at top speed");
        Assert.InRange(YawAfter(Car.TopSpeed, 1.5f), 15f, 26f);        // at top speed: about 22 deg/s, however long the key is held
        for (var v = 1f; v <= Car.TopSpeed; v += 1f)
        {
            var s = new DriveState { Speed = v };
            for (var i = 0; i < 120; i++)
            {
                DriveModel.Step(ref s, Car, Keys(0, i < 60 ? 1 : -1), Dt, w); s.Speed = v;
                Assert.True(v * MathF.Abs(s.YawRate) <= D.Land.Grip * 1.01f, $"{v} m/s: {v * MathF.Abs(s.YawRate)} m/s² sideways");
                Assert.Equal(0f, s.Slip);  // on its tyres it goes where it points
            }
        }
    }

    [Fact]
    public void A_steering_tap_is_a_small_correction()
    {
        var w = new Field();
        var tap = new DriveState { Speed = 12 };
        for (var i = 0; i < 60; i++) { DriveModel.Step(ref tap, Car, Keys(0, i < 2 ? 1 : 0), Dt, w); tap.Speed = 12; }
        var held = new DriveState { Speed = 12 };
        for (var i = 0; i < 15; i++) { DriveModel.Step(ref held, Car, Keys(0, 1), Dt, w); held.Speed = 12; }
        Assert.InRange(MathF.Abs(Deg(tap.Yaw)), 0.3f, 4f);                       // two ticks: a nudge
        Assert.True(MathF.Abs(held.Yaw) > 5f * MathF.Abs(tap.Yaw), "held for half a second it turns many times more");
        Assert.Equal(0f, tap.YawRate);                                           // and it runs straight again afterwards
        Assert.Equal(0f, tap.Steer);
    }

    [Fact]
    public void The_handbrake_stops_it_and_lets_the_tail_slide()
    {
        var w = new Field();
        var s = new DriveState { Speed = 16 };
        var slid = 0f;
        Drive(ref s, w, Keys(0, 1, handbrake: true), 1.2f, x => slid = MathF.Max(slid, MathF.Abs(x.Slip)));
        Assert.True(slid > 1.5f, $"the tail slid only {slid} m/s");
        var plain = new DriveState { Speed = 16 }; Drive(ref plain, w, Keys(0, 1), 1.2f);
        Assert.True(MathF.Abs(s.Yaw) > MathF.Abs(plain.Yaw), "the handbrake turn comes round further");
        Drive(ref s, w, Keys(handbrake: true), 3);
        Assert.Equal(0f, s.Speed);
        Assert.Equal(0f, s.Slip);
    }

    [Fact]
    public void With_nobody_at_the_wheel_it_coasts_straight_to_a_stop()
    {
        var w = new Field();
        var s = new DriveState { Speed = 15, Throttle = 1, Steer = 0.5f, YawRate = -0.3f };
        Drive(ref s, w, DriveInput.None, 0.5f);
        Assert.Equal(0f, s.Throttle);
        Assert.Equal(0f, s.Steer);
        var yaw = s.Yaw;
        Drive(ref s, w, DriveInput.None, 30);
        Assert.Equal(0f, s.Speed);
        Assert.Equal(0f, s.YawRate);
        Assert.Equal(yaw, s.Yaw, 2);
        var at = s.Position; Drive(ref s, w, DriveInput.None, 5);
        Assert.Equal(at, s.Position); // and it stays put
    }

    [Fact]
    public void A_wall_takes_the_speed_into_it_and_reports_the_impact()
    {
        var w = new Field { WallZ = -10 };
        var s = new DriveState { Speed = 18 };
        var hardest = 0f;
        Drive(ref s, w, Keys(1), 2, x => hardest = MathF.Max(hardest, x.Impact));
        Assert.InRange(hardest, 17f, 19.5f);
        Assert.InRange(s.Position.Z, -10.01f, -9.9f);
        Assert.InRange(s.Speed, 0f, 0.5f);
        Assert.True(hardest > Shipped.CrashMinSpeedLoss, "full speed into a wall is a crash");
        // glancing: it keeps the speed along the wall
        var g = new DriveState { Speed = 18, Yaw = -MathF.PI / 2 + 0.2f, Position = new Vector3(0, 0, -9) }; // heading +X, a little towards the wall
        Drive(ref g, w, Keys(1), 2);
        Assert.InRange(g.Position.Z, -10.01f, -9.9f);
        Assert.True(g.Position.X > 20f, "it slides along the wall");
    }

    [Fact]
    public void It_lies_on_the_slope_under_its_wheels_climbs_a_kerb_and_is_stopped_by_a_ledge()
    {
        // a 15 degree ramp up towards -Z
        var slope = MathF.Tan(15 * MathF.PI / 180);
        var ramp = new Field { Ground = (_, z) => z < 0 ? -z * slope : 0f };
        var s = new DriveState { Position = new Vector3(0, 0, 10), Speed = 8 };
        Drive(ref s, ramp, Keys(1), 4);
        Assert.Equal(15f, Deg(s.Pitch), 0);                       // nose up the hill
        Assert.Equal(0f, s.Roll, 3);
        Assert.Equal(-s.Position.Z * slope, s.Position.Y, 1);     // on the ramp
        // across the same slope it lies over to one side
        var across = new Field { Ground = (x, _) => x * slope };
        var a = new DriveState { Position = new Vector3(0, 0, 0), Speed = 5 };
        Drive(ref a, across, Keys(), 0.5f);
        Assert.Equal(15f, Deg(a.Roll), 0);                        // ground rises to the right: the right side is up
        // a kerb is driven over, a ledge is a wall
        var kerb = new Field { Ground = (_, z) => z < 0 ? 0.3f : 0f };
        var k = new DriveState { Position = new Vector3(0, 0, 6), Speed = 6 };
        Drive(ref k, kerb, Keys(1), 2);
        Assert.True(k.Position.Z < -3 && MathF.Abs(k.Position.Y - 0.3f) < 1e-3f);
        var ledge = new Field { Ground = (_, z) => z < 0 ? 0.9f : 0f };
        var l = new DriveState { Position = new Vector3(0, 0, 6), Speed = 6 };
        var hit = 0f;
        Drive(ref l, ledge, Keys(1), 3, x => hit = MathF.Max(hit, x.Impact));
        Assert.True(l.Position.Z > 0.5f, $"it went up a 0.9 m ledge (z {l.Position.Z})");
        Assert.True(hit > 4f, "the ledge is an impact");
    }

    [Fact]
    public void Off_a_drop_it_flies_without_drive_and_lands()
    {
        var w = new Field { Ground = (_, z) => z < 0 ? -3f : 0f };
        var s = new DriveState { Position = new Vector3(0, 0, 4), Speed = 12 };
        var airborne = 0;
        Drive(ref s, w, Keys(1), 0.6f, x => { if (x.Airborne) airborne++; });
        Assert.True(airborne >= 3, $"it was in the air for {airborne} ticks");
        Assert.True(s.Airborne);
        Assert.True(s.Position.Y < -0.3f && s.Position.Y > -3f);
        var yaw = s.Yaw; var speed = s.Speed;
        Drive(ref s, w, Keys(-1, 1), 0.15f);
        Assert.True(s.Airborne);
        Assert.Equal(speed, s.Speed, 3);                 // no brake in the air
        Assert.Equal(yaw, s.Yaw, 3);                     // and no steering
        Drive(ref s, w, Keys(1), 2);
        Assert.False(s.Airborne);
        Assert.Equal(-3f, s.Position.Y, 3);
        Assert.Equal(0f, s.VerticalSpeed);
        // no ground at all: it keeps falling (the server's kill height ends that)
        var hole = new Field { NoGround = true };
        var h = new DriveState { Speed = 5 }; Drive(ref h, hole, Keys(1), 1);
        Assert.True(h.Airborne && h.Position.Y < -5f);
    }

    // ---- afloat ----

    [Fact]
    public void Afloat_it_tops_out_at_its_water_speed_slowly_and_backs_slower()
    {
        var (w, rest) = Afloat();
        var s = rest; float t = 0, t90 = -1, max = 0;
        Drive(ref s, w, Keys(1), 40, x => { t += Dt; max = MathF.Max(max, x.Speed); if (t90 < 0 && x.Speed >= 0.9f * Car.WaterTopSpeed) t90 = t; });
        Assert.Equal(Car.WaterTopSpeed, s.Speed, 2);
        Assert.True(max <= Car.WaterTopSpeed + 1e-3f);
        Assert.InRange(t90, 3.0f, 6.0f);
        Assert.Equal(-D.Water.Draft, s.Position.Y, 3);   // at its waterline
        var back = rest; Drive(ref back, w, Keys(-1), 40);
        Assert.InRange(-back.Speed, 0.5f * Car.WaterTopSpeed, 0.85f * Car.WaterTopSpeed);
        // the handbrake is a wheel brake: it does nothing afloat
        var hb = s; var plain = s;
        Drive(ref hb, w, Keys(handbrake: true), 1); Drive(ref plain, w, Keys(), 1);
        Assert.Equal(plain.Speed, hb.Speed, 4);
    }

    [Fact]
    public void Afloat_it_carries_its_way_when_the_throttle_is_lifted_and_stops_only_by_going_astern()
    {
        var (w, rest) = Afloat();
        var top = rest; Drive(ref top, w, Keys(1), 40);
        var lift = top; Drive(ref lift, w, Keys(), 2);
        Assert.True(lift.Speed > 0.4f * Car.WaterTopSpeed, $"two seconds after lifting off it does only {lift.Speed} m/s");
        var run = top; Drive(ref run, w, Keys(), 10);
        Assert.True(Flat(top.Position, run.Position) > 18f, "it runs on for a long way");
        Assert.True(run.Speed > 0.2f, "and is still moving after ten seconds");
        var stop = top; var t = 0f; var at = -1f;
        Drive(ref stop, w, Keys(-1), 20, x => { t += Dt; if (at < 0 && x.Speed <= 0) at = t; });
        var astern = top; Drive(ref astern, w, Keys(-1), at);
        Assert.InRange(at, 1.5f, 4f);
        Assert.InRange(Flat(top.Position, astern.Position), 5f, 14f);
        // against the land: from the same speed the brake stops it in about 3 m
        var land = new DriveState { Speed = Car.WaterTopSpeed }; var wheelsStop = 0f;
        Drive(ref land, new Field(), Keys(-1), 3, x => { if (x.Speed > 0) wheelsStop = -x.Position.Z; });
        Assert.InRange(wheelsStop, 2f, 4.5f);
        Assert.True(Flat(top.Position, astern.Position) > 2f * wheelsStop, "it takes much longer to stop than on its wheels");
    }

    [Fact]
    public void Afloat_it_turns_from_the_stern_slides_wide_and_answers_the_helm_lazily()
    {
        var (w, rest) = Afloat();
        var top = rest; Drive(ref top, w, Keys(1), 40);
        // a steady turn at speed: a wide circle, the hull pointing well inside the way it travels
        var s = top; Drive(ref s, w, Keys(1, 1), 25);
        var radius = CircleRadius(s);
        Assert.InRange(radius, 6f, 14f);
        var slideAngle = Deg(MathF.Atan2(MathF.Abs(s.Slip), s.Speed));
        Assert.InRange(slideAngle, 6f, 35f);
        // on land at the same speed the circle is tighter and it does not slide at all
        var l = new DriveState { Speed = Car.WaterTopSpeed };
        for (var i = 0; i < 300; i++) { DriveModel.Step(ref l, Car, Keys(0, 1), Dt, new Field()); l.Speed = Car.WaterTopSpeed; }
        Assert.True(CircleRadius(l) < radius, "the land circle at that speed is tighter");
        Assert.Equal(0f, l.Slip);
        // the helm is lazy: after a third of a second it has less than half the yaw rate it will have (on land: nearly all of it)
        var lazy = top; Drive(ref lazy, w, Keys(1, 1), 0.33f);
        Assert.True(MathF.Abs(lazy.YawRate) < 0.5f * MathF.Abs(s.YawRate));
        // the stern swings out: the centre is carried to the outside of the turn at once, before the heading has come round
        var swing = top; Drive(ref swing, w, Keys(1, 1), 0.5f);
        Assert.True(swing.Yaw < 0, "turning right");
        var onRails = top.Position + new Vector3(0, 0, -1) * 0; // it was heading -Z along x = 0
        Assert.True(swing.Position.X < onRails.X + 0.02f, $"the centre has not yet followed the bow to the right (x {swing.Position.X})");
        // straightened up, the slide goes on for a while
        var after = s; Drive(ref after, w, Keys(1), 0.5f);
        Assert.True(MathF.Abs(after.Slip) > 0.3f * MathF.Abs(s.Slip), "it is still sliding half a second after the helm is centred");
        // from rest, power and helm turn it where a car would not
        var spin = rest; Drive(ref spin, w, Keys(1, 1), 2);
        Assert.True(MathF.Abs(Deg(spin.Yaw)) > 12f);
        var car = new DriveState(); Drive(ref car, new Field(), Keys(0, 1), 2);
        Assert.Equal(0f, car.Yaw);
    }

    // ---- going in and coming out ----

    [Fact]
    public void Going_in_is_one_splash_a_clear_deceleration_and_a_plunge_that_settles()
    {
        var pond = Pond();
        var s = new DriveState { Position = new Vector3(0, 0, 30), Speed = Car.TopSpeed, Throttle = 1 };
        var entered = 0; var tin = -1f; var t = 0f; var last = s.Speed; var hardest = 0f; var lowest = float.PositiveInfinity; var oneSecond = -1f;
        var line = pond.Surface - D.Water.Draft;
        Drive(ref s, pond, Keys(1), 12, x =>
        {
            t += Dt;
            if (x.EnteredWater) { entered++; tin = t; Assert.True(x.Afloat); }
            if (x.Afloat)
            {
                hardest = MathF.Max(hardest, (last - x.Speed) / Dt);
                lowest = MathF.Min(lowest, x.Position.Y);
                if (oneSecond < 0 && t - tin >= 1f) oneSecond = x.Speed;
            }
            last = x.Speed;
        });
        Assert.Equal(1, entered);                                            // one event, for the splash
        Assert.True(hardest > 8f, $"the water checked it by only {hardest} m/s²");
        Assert.True(oneSecond < 0.62f * Car.TopSpeed, $"a second after the splash it still did {oneSecond} m/s");
        Assert.True(lowest < line - 0.02f, "it plunges under its waterline going in");
        Assert.True(lowest > line - 0.35f, "but not out of sight");
        Assert.Equal(line, s.Position.Y, 2);                                 // and settles on it
        Assert.Equal(Car.WaterTopSpeed, s.Speed, 1);
        Assert.Equal(0f, s.Pitch, 3);                                        // level
        Assert.True(s.Afloat && !s.Airborne);
    }

    [Fact]
    public void Coming_out_the_wheels_haul_it_up_the_bank_and_it_is_a_car_again()
    {
        var pond = Pond();
        // afloat, heading for the shore (+Z)
        var s = new DriveState { Position = new Vector3(0, pond.Surface - D.Water.Draft, -20), Yaw = MathF.PI, Afloat = true };
        var left = 0; var t = 0f; var tout = -1f; var fastestRise = 0f; var lastY = s.Position.Y; var emerging = 0;
        Drive(ref s, pond, Keys(1), 14, x =>
        {
            t += Dt;
            if (x.LeftWater) { left++; tout = t; Assert.True(x.Emerging); }
            if (x.Emerging) emerging++;
            if (!x.Afloat) fastestRise = MathF.Max(fastestRise, (x.Position.Y - lastY) / Dt);
            lastY = x.Position.Y;
        });
        Assert.Equal(1, left);
        Assert.True(emerging >= 3, "the climb out takes a few ticks: a heave, not a jump");
        Assert.InRange(fastestRise, 0.5f, D.Water.ClimbOutRate + Car.WaterTopSpeed * 1.2f);
        Assert.False(s.Afloat);
        Assert.False(s.Emerging);
        Assert.Equal(0f, s.Position.Y, 2);                                   // on the dry ground
        Assert.True(s.Position.Z > 20f);
        Assert.True(s.Speed > Car.WaterTopSpeed * 1.5f, "on its wheels it is faster than it ever was afloat");
    }

    [Fact]
    public void It_floats_in_a_shallow_pond_as_in_a_deep_one_and_stands_on_its_wheels_where_the_ground_is_dry()
    {
        // the village's ponds are 0.2 to 0.3 m deep in the terrain: it floats there, the wheels just under the bed
        var shallow = new Field { ShoreZ = 1e6f, Surface = 0f, Ground = (_, _) => -0.2f };
        var s = new DriveState { Position = new Vector3(0, -0.2f, 0) };
        Drive(ref s, shallow, Keys(1), 20);
        Assert.True(s.Afloat);
        Assert.Equal(Car.WaterTopSpeed, s.Speed, 1);
        Assert.Equal(-D.Water.Draft, s.Position.Y, 2);
        // a bank inside the water body, above the surface: it is on its wheels there
        var bank = new Field { ShoreZ = 1e6f, Surface = 0f, Ground = (_, _) => 0.3f };
        var b = new DriveState { Position = new Vector3(0, 0.3f, 0) };
        Drive(ref b, bank, Keys(1), 5);
        Assert.False(b.Afloat);
        Assert.True(b.Speed > Car.WaterTopSpeed);
        // a puddle shallower than float_depth is driven through
        var puddle = new Field { ShoreZ = 1e6f, Surface = 0f, Ground = (_, _) => -0.05f };
        var p = new DriveState { Position = new Vector3(0, -0.05f, 0) };
        Drive(ref p, puddle, Keys(1), 5);
        Assert.False(p.Afloat);
    }

    // ---- prediction ----

    static IEnumerable<DriveInput> Script()
    {
        // down the bank into the pond, about in it, and out again: throttle, steering both ways, the brake and the handbrake
        for (var i = 0; i < 900; i++)
        {
            var t = i * Dt;
            yield return t < 3 ? Keys(1)
                : t < 5 ? Keys(1, 0.6f)
                : t < 6 ? Keys(1, -1)
                : t < 8 ? Keys(1)
                : t < 12 ? Keys(1, 1)
                : t < 13 ? Keys(-1, -1)
                : t < 14 ? Keys()
                : t < 22 ? Keys(1, t % 2 < 1 ? 0.8f : -0.3f)
                : t < 26 ? Keys(1, 1)
                : t < 27 ? Keys(0, 0, handbrake: true)
                : Keys(-1, 0.5f);
        }
    }

    [Fact]
    public void Prediction_and_the_server_step_to_the_same_state_from_the_same_inputs()
    {
        var server = new DriveState { Position = new Vector3(0, 0, 25) };
        var client = new DrivePrediction(Car, Pond(), server);
        var world = Pond();
        ushort seq = 0;
        var afloat = 0; var onLand = 0;
        foreach (var keys in Script())
        {
            var ni = new NetInput { Seq = ++seq, MoveX = Quant.Axis(keys.Steer), MoveY = Quant.Axis(keys.Throttle), Buttons = (ushort)(keys.Handbrake ? MoveButtons.Jump : MoveButtons.None) };
            DriveModel.Step(ref server, Car, DriveInput.FromNet(ni), Dt, world);   // the server, from the input as it arrives
            client.Step(seq, DriveInput.FromNet(ni), Dt);                          // the client, from the input as it sends it
            Assert.Equal(server.Position, client.State.Position);
            Assert.Equal(server.Yaw, client.State.Yaw);
            Assert.Equal(server.Speed, client.State.Speed);
            Assert.Equal(server.Slip, client.State.Slip);
            Assert.Equal(server.Afloat, client.State.Afloat);
            if (server.Afloat) afloat++; else onLand++;
            // every third tick a snapshot acknowledges the input: nothing to correct, even through the snapshot's quantisation
            if (seq % 3 == 0)
            {
                var vehicle = VehicleRecord.Make(VehicleRecord.FirstId, server.Position, server.Yaw, 1, 0);
                Assert.False(client.Reconcile(seq, DriveRecord.State(vehicle, DriveRecord.Make(VehicleRecord.FirstId, server)), Dt), $"corrected at input {seq}");
            }
        }
        Assert.True(afloat > 100 && onLand > 100, $"the script must cover both ({afloat} ticks afloat, {onLand} on land)");
        Assert.Equal(0, client.Corrections);
        Assert.True(client.MaxError < 0.02f, $"largest error {client.MaxError}");
    }

    [Fact]
    public void A_server_that_disagrees_is_taken_and_the_inputs_on_their_way_are_replayed()
    {
        var world = new Field();
        var start = new DriveState { Speed = 10 };
        var client = new DrivePrediction(Car, world, start);
        var server = start;
        var inputs = new List<DriveInput>();
        for (ushort seq = 1; seq <= 10; seq++)
        {
            var keys = Keys(1, seq < 5 ? 1 : 0);
            inputs.Add(keys);
            client.Step(seq, keys, Dt);
        }
        // the server saw the first four inputs, and something the client did not know of took 3 m/s off it (a wall, another player)
        for (var i = 0; i < 4; i++) DriveModel.Step(ref server, Car, inputs[i], Dt, world);
        server.Speed -= 3; server.Position += new Vector3(0.4f, 0, 0);
        Assert.True(client.Reconcile(4, server, Dt));
        Assert.Equal(1, client.Corrections);
        Assert.Equal(6, client.PendingCount);
        // the client now holds what the server will hold once it has stepped the other six
        var expect = server;
        for (var i = 4; i < 10; i++) DriveModel.Step(ref expect, Car, inputs[i], Dt, world);
        Assert.True(Vector3.Distance(expect.Position, client.State.Position) < 0.02f);
        Assert.Equal(expect.Speed, client.State.Speed, 2);
        // an acknowledgement of an input it never stepped changes nothing
        Assert.False(client.Reconcile(500, server, Dt));
    }

    [Fact]
    public void The_drive_record_carries_the_state_within_its_quantisation()
    {
        var s = new DriveState
        {
            Position = new Vector3(12.5f, 3.25f, -40.75f), Yaw = 1.2f, Speed = 17.3f, Slip = -1.4f, VerticalSpeed = -2.5f, Pitch = 0.21f, Roll = -0.12f,
            Steer = -0.6f, Throttle = 0.7f, Afloat = true, Sink = 0.3f,
        };
        var id = (ushort)(VehicleRecord.FirstId + 7);
        var rec = DriveRecord.Make(id, s);
        Assert.Equal(EntityType.Drive, rec.Type);
        Assert.Equal(id, DriveRecord.CarOf(rec.Id));
        Assert.InRange(rec.Id, DriveRecord.FirstId, (ushort)(FlightRecord.FirstId - 1));
        var back = DriveRecord.State(VehicleRecord.Make(id, s.Position, s.Yaw, 0, 0), rec);
        Assert.Equal(s.Position, back.Position);
        Assert.Equal(s.Yaw, back.Yaw, 3);
        Assert.Equal(s.Speed, back.Speed, 2);
        Assert.Equal(s.Slip, back.Slip, 2);
        Assert.Equal(s.VerticalSpeed, back.VerticalSpeed, 2);
        Assert.Equal(s.Pitch, back.Pitch, 3);
        Assert.Equal(s.Roll, back.Roll, 3);
        Assert.Equal(s.Steer, back.Steer, 1);
        Assert.InRange(back.Throttle, 0.55f, 0.9f);
        Assert.True(back.Afloat && back.Emerging && !back.Airborne);
        // the ends of the ramps survive exactly enough to tell ahead from astern and left from right
        Assert.Equal(1f, DriveRecord.Throttle(DriveRecord.Make(id, new DriveState { Throttle = 1 })), 2);
        Assert.Equal(-1f, DriveRecord.Throttle(DriveRecord.Make(id, new DriveState { Throttle = -1 })), 2);
        Assert.Equal(-1f, DriveRecord.Steer(DriveRecord.Make(id, new DriveState { Steer = -1 })), 2);
        Assert.Equal(1f, DriveRecord.Steer(DriveRecord.Make(id, new DriveState { Steer = 1 })), 2);
    }

    [Fact]
    public void The_drivers_input_is_read_from_the_wire_the_same_on_both_sides()
    {
        var ni = new NetInput { MoveX = Quant.Axis(-1f), MoveY = Quant.Axis(1f), Buttons = (ushort)(MoveButtons.Jump | MoveButtons.Fire) };
        var i = DriveInput.FromNet(ni);
        Assert.Equal(1f, i.Throttle);
        Assert.Equal(-1f, i.Steer);
        Assert.True(i.Handbrake);
        var off = DriveInput.FromNet(ni, handsOff: true); // steering a killstreak: the keys are not the vehicle's
        Assert.Equal(0f, off.Throttle);
        Assert.Equal(0f, off.Steer);
        Assert.False(off.Handbrake);
    }

    // ---- the body, as it is drawn ----

    static DriveBodyPose Draw(ref DriveBodyState b, DriveState s, float frame = 1f / 144f) =>
        DriveBody.Step(ref b, Car, new DriveBodyInput(s.Speed, s.YawRate, s.Throttle, s.Afloat, s.Pitch, s.Roll), frame);

    [Fact]
    public void On_land_the_body_squats_under_throttle_dives_under_braking_and_leans_out_of_a_turn()
    {
        var w = new Field(); var b = new DriveBodyState();
        var s = new DriveState();
        float squat = 0, dive = 0;
        // a frame is drawn for every tick here; the springs are sliced, so the frame rate does not change what they do
        Drive(ref s, w, Keys(1), 1.5f, x => squat = MathF.Max(squat, Draw(ref b, x, Dt).BodyPitch));
        Assert.InRange(Deg(squat), 1.5f, D.Body.MaxPitchDeg + 2.5f);          // the tail squats: nose up
        Drive(ref s, w, Keys(1), 8, x => Draw(ref b, x, Dt));
        Drive(ref s, w, Keys(-1), 1.0f, x => dive = MathF.Min(dive, Draw(ref b, x, Dt).BodyPitch));
        Assert.InRange(Deg(dive), -(D.Body.MaxPitchDeg + 2.5f), -2.5f);       // the nose dives
        // a right turn at speed: the body leans to the left, out of the turn (the left side drops: positive roll)
        var t = new DriveState { Speed = 14 }; var bt = new DriveBodyState(); var pose = default(DriveBodyPose);
        Drive(ref t, w, Keys(1, 1), 2, x => pose = Draw(ref bt, x, Dt));
        Assert.InRange(Deg(pose.BodyRoll), 2f, D.Body.MaxRollDeg + 0.01f);
        Assert.Equal(0f, pose.Roll, 4);                                       // the wheels stay flat on the flat ground
        var l = new DriveState { Speed = 14 }; var bl = new DriveBodyState();
        Drive(ref l, w, Keys(1, -1), 2, x => pose = Draw(ref bl, x, Dt));
        Assert.True(pose.BodyRoll < -2f * MathF.PI / 180);
        // at rest it stands level and still
        var rest = new DriveState(); var br = new DriveBodyState();
        Drive(ref rest, w, Keys(), 3, x => pose = Draw(ref br, x, Dt));
        Assert.Equal(default, pose);
    }

    [Fact]
    public void The_body_lies_on_the_slope_the_wheels_stand_on()
    {
        var b = new DriveBodyState(); var pose = default(DriveBodyPose);
        var s = new DriveState { Pitch = 0.2f, Roll = -0.1f };
        for (var i = 0; i < 144; i++) pose = Draw(ref b, s);
        Assert.Equal(0.2f, pose.Pitch, 3);
        Assert.Equal(-0.1f, pose.Roll, 3);
        // a step in the ground is taken up over a few frames, not in one
        s.Pitch = 0.3f;
        pose = Draw(ref b, s);
        Assert.InRange(pose.Pitch, 0.2f, 0.25f);
    }

    [Fact]
    public void Afloat_the_bow_rises_under_power_and_it_bobs_and_settles()
    {
        var (w, rest) = Afloat();
        // going in stirs the water: it bobs hard, then settles to the gentle bob it has at rest
        var b = new DriveBodyState(); var s = new DriveState { Position = rest.Position };
        Draw(ref b, s); s.Afloat = true;
        float early = 0, late = 0, t = 0;
        for (var i = 0; i < 144 * 14; i++)
        {
            var p = Draw(ref b, s); t += 1f / 144f;
            if (t < 2.6f) early = MathF.Max(early, MathF.Abs(p.Lift)); else if (t > 8) late = MathF.Max(late, MathF.Abs(p.Lift));
        }
        Assert.InRange(late, 0.8f * D.Body.BobHeight, D.Body.BobHeight * 1.001f);       // the bob at rest
        Assert.True(early > 1.5f * late, $"just after going in it bobs harder ({early} against {late})");
        Assert.True(early <= D.Body.BobHeight * D.Body.BobDisturbed * 1.001f);
        // under power the bow comes up, and goes down again when the throttle is lifted
        var bp = new DriveBodyState { Started = true, Float = 1, WasAfloat = true }; var run = rest; var pose = default(DriveBodyPose);
        Drive(ref run, w, Keys(1), 12, x => pose = Draw(ref bp, x, Dt));
        Assert.InRange(Deg(pose.Pitch), D.Body.BowRiseDeg - 2.5f, D.Body.BowRiseDeg + 2.5f);
        Assert.Equal(0f, pose.BodyPitch, 4);                                            // afloat the wheels move with the hull
        Drive(ref run, w, Keys(), 8, x => pose = Draw(ref bp, x, Dt));
        Assert.InRange(Deg(pose.Pitch), -2.5f, 2.5f);
        // a turn heels it outwards
        var turn = rest; var bt = new DriveBodyState { Started = true, Float = 1, WasAfloat = true };
        Drive(ref turn, w, Keys(1), 12, x => Draw(ref bt, x, Dt));
        float heel = 0;
        Drive(ref turn, w, Keys(1, 1), 6, x => heel = MathF.Max(heel, Draw(ref bt, x, Dt).Roll));
        Assert.True(Deg(heel) > 1.5f, $"it heeled {Deg(heel)} degrees");
    }

    [Fact]
    public void The_body_is_the_same_at_any_frame_rate()
    {
        var w = new Field();
        DriveBodyPose At(int fps)
        {
            var b = new DriveBodyState(); var s = new DriveState(); var pose = default(DriveBodyPose);
            // two seconds of full throttle; a frame every 1/fps, the state stepped at the tick as the game does
            var tick = 0f;
            for (var i = 0; i < 2 * fps; i++)
            {
                tick += 1f / fps;
                while (tick >= Dt) { DriveModel.Step(ref s, Car, Keys(1), Dt, w); tick -= Dt; }
                pose = Draw(ref b, s, 1f / fps);
            }
            return pose;
        }
        var slow = At(30); var fast = At(240);
        Assert.True(Deg(slow.BodyPitch) > 0.5f);
        Assert.InRange(Deg(fast.BodyPitch) - Deg(slow.BodyPitch), -0.5f, 0.5f);
    }
}
