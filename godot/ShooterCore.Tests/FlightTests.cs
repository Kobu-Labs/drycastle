using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The utility helicopter's flight (ADR 0006). Nobody can feel it in a test, so the feel is pinned as numbers on the shipped vehicles.json:
/// how long to top speed, how far it takes to stop hands off, that the hover does not drift, that it levels without rocking back, that a
/// landing is free and a wall is not, and that the pilot's prediction and the server step to the same state from the same inputs.
/// </summary>
public class FlightTests
{
    readonly Xunit.Abstractions.ITestOutputHelper _out;
    public FlightTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Heli => Shipped.Vehicles.Single(v => v.Id == "utility_helicopter");
    static VehicleFlightDef F => Heli.Flight!;

    /// <summary>A flat field at height 0, with an optional wall across x, an optional lake and optional map edges.</summary>
    sealed class Field : IFlightWorld
    {
        public float Ground;
        public float? WallX, Water;
        public bool NoGround;
        public FlightBounds Bounds { get; set; } = FlightBounds.None;
        public float GroundY(Vector3 position) => NoGround ? float.NegativeInfinity : Ground;
        public float WaterY(float x, float z) => Water ?? float.NegativeInfinity;

        public FlightMove Move(Vector3 from, float yaw, Vector3 motion)
        {
            var t = 1f; var hit = false; var normal = Vector3.Zero;
            if (!NoGround && motion.Y < 0 && from.Y + motion.Y < Ground)
            {
                t = Math.Clamp((from.Y - Ground) / -motion.Y, 0f, 1f); hit = true; normal = Vector3.UnitY;
            }
            if (WallX is { } w && motion.X > 0 && from.X + motion.X > w)
            {
                var tw = Math.Clamp((w - from.X) / motion.X, 0f, 1f);
                if (tw < t) { t = tw; hit = true; normal = -Vector3.UnitX; }
            }
            return new FlightMove(from + motion * t, hit, normal);
        }
    }

    static FlightInput Keys(float x = 0, float y = 0, int lift = 0, float yaw = 0) => new() { MoveX = x, MoveY = y, Lift = lift, Yaw = yaw, Piloted = true, Occupied = true };

    /// <summary>A helicopter in a steady hover 50 m up, rotor at speed, facing -Z.</summary>
    static FlightState Hover(float height = 50) => new() { Position = new Vector3(0, height, 0), Rotor = 1 };

    static void Fly(ref FlightState s, IFlightWorld w, FlightInput input, float seconds, Action<FlightState>? each = null)
    {
        for (var i = 0; i < (int)MathF.Round(seconds / Dt); i++)
        {
            FlightModel.Step(ref s, Heli, input, Dt, w);
            each?.Invoke(s);
        }
    }

    static float Speed(in FlightState s) => new Vector2(s.Velocity.X, s.Velocity.Z).Length();
    static float Deg(float rad) => rad * 180f / MathF.PI;

    // ---- data ----

    [Fact]
    public void The_shipped_utility_helicopter_flies_has_a_pilot_seat_and_its_model()
    {
        var v = Heli;
        Assert.Equal(VehicleMedium.Air, v.Medium);
        Assert.NotNull(v.Flight);
        Assert.False(v.CanFloat);
        Assert.True(v.RunOver);   // runs people over only while skimming the ground (flight.run_over_agl)
        Assert.True(v.CrashDamage);
        Assert.Equal("driver", v.Seats[0].Role);   // the pilot's seat is the driver seat of the seat framework
        Assert.Equal("drycastle-generated-models/utility_helicopter.glb", v.Model);
        Assert.Equal(1, v.SpawnCount);
        Assert.Null(v.Flak);
    }

    [Fact]
    public void Every_handling_number_is_written_in_vehicles_json_not_left_to_a_default()
    {
        // unknown keys are skipped by the loader, so a typo would silently fly on a default: every key of the two blocks must be in the file
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "vehicles.json")));
        var heli = doc.RootElement.GetProperty("vehicles").EnumerateArray().Single(v => v.GetProperty("id").GetString() == "utility_helicopter");
        var flight = heli.GetProperty("flight");
        static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
        var wanted = typeof(VehicleFlightDef).GetProperties().Select(p => Snake(p.Name)).ToHashSet();
        Assert.Equal(wanted.OrderBy(x => x), flight.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        var camera = typeof(FlightCameraDef).GetProperties().Select(p => Snake(p.Name)).ToHashSet();
        Assert.Equal(camera.OrderBy(x => x), flight.GetProperty("camera").EnumerateObject().Select(p => p.Name).OrderBy(x => x));
    }

    [Fact]
    public void Flight_and_the_air_medium_go_together_and_bad_numbers_are_refused()
    {
        static VehiclesFile With(Action<VehicleDef> change)
        {
            var f = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
            change(f.Vehicles.Single(v => v.Id == "utility_helicopter"));
            return f;
        }
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Flight = null)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Medium = VehicleMedium.Land)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Flight!.AttitudeResponse = 20)));   // would ring at 30 ticks a second
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Flight!.DragLow = 1)));            // drag alone would hold it under its top speed
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Flight!.Camera.Tilt = 2)));
        VehiclesFile.Validate(With(_ => { }));
    }

    /// <summary>
    /// Not a check: the feel as a table, for tuning vehicles.json without the game. Run it with
    /// <c>dotnet test ShooterCore.Tests --filter The_feel_in_numbers --logger "console;verbosity=detailed"</c>.
    /// </summary>
    [Fact]
    public void The_feel_in_numbers()
    {
        var w = new Field();
        float TimeTo(float fraction, FlightInput keys, Func<FlightState, float> speed)
        {
            var s = Hover(); var t = 0f; var at = -1f;
            Fly(ref s, w, keys, 40, x => { t += Dt; if (at < 0 && speed(x) >= fraction) at = t; });
            return at;
        }
        var fwd = Hover(); Fly(ref fwd, w, Keys(y: 1), 40);
        var side = Hover(); Fly(ref side, w, Keys(x: 1), 40);
        var back = Hover(); Fly(ref back, w, Keys(y: -1), 40);
        _out.WriteLine($"top speed forward   {Speed(fwd):F1} m/s ({Speed(fwd) * 3.6f:F0} km/h), sideways {Speed(side):F1}, backward {Speed(back):F1}");
        _out.WriteLine($"0 to 50% of top     {TimeTo(0.5f * Heli.TopSpeed, Keys(y: 1), x => Speed(x)):F2} s");
        _out.WriteLine($"0 to 90% of top     {TimeTo(0.9f * Heli.TopSpeed, Keys(y: 1), x => Speed(x)):F2} s");
        _out.WriteLine($"full tilt reached   {TimeTo(0.95f * F.MaxPitchDeg, Keys(y: 1), x => -Deg(x.Pitch)):F2} s");
        var stop = fwd; var from = stop.Position; var ts = 0f; var stopAt = -1f;
        Fly(ref stop, w, Keys(), 30, x => { ts += Dt; if (stopAt < 0 && Speed(x) == 0) stopAt = ts; });
        _out.WriteLine($"hands-off stop      {Vector3.Distance(from, stop.Position):F1} m in {stopAt:F2} s from top speed");
        var half = Hover(); Fly(ref half, w, Keys(y: 1), TimeTo(0.5f * Heli.TopSpeed, Keys(y: 1), x => Speed(x)));
        from = half.Position; ts = 0; stopAt = -1;
        Fly(ref half, w, Keys(), 30, x => { ts += Dt; if (stopAt < 0 && Speed(x) == 0) stopAt = ts; });
        _out.WriteLine($"hands-off stop      {Vector3.Distance(from, half.Position):F1} m in {stopAt:F2} s from half speed");
        var turn = Hover(); var tt = 0f; var quarter = -1f;
        Fly(ref turn, w, Keys(yaw: MathF.PI / 2), 5, x => { tt += Dt; if (quarter < 0 && x.Yaw >= MathF.PI / 2 * 0.98f) quarter = tt; });
        _out.WriteLine($"quarter turn        {quarter:F2} s in the hover");
        var climb = Hover(20); Fly(ref climb, w, Keys(lift: 1), 1);
        _out.WriteLine($"climb after 1 s     {climb.Velocity.Y:F1} m/s (of {F.ClimbRate})");
        var land = Hover(40); var tl = 0f; var down = -1f; var hardest = 0f;
        Fly(ref land, w, Keys(lift: -1), 40, x => { tl += Dt; hardest = MathF.Max(hardest, x.Impact); if (down < 0 && x.Grounded) down = tl; });
        _out.WriteLine($"descent from 40 m   {down:F1} s, touchdown at {hardest:F2} m/s (damage starts at {Shipped.CrashMinSpeedLoss})");
    }

    // ---- forward flight ----

    [Fact]
    public void Full_forward_reaches_ninety_percent_of_top_speed_in_about_six_seconds_and_never_passes_it()
    {
        var w = new Field(); var s = Hover();
        var t90 = -1f; var t = 0f; var max = 0f;
        Fly(ref s, w, Keys(y: 1), 25, x => { t += Dt; max = MathF.Max(max, Speed(x)); if (t90 < 0 && Speed(x) >= 0.9f * Heli.TopSpeed) t90 = t; });
        Assert.InRange(t90, 4.5f, 7.5f);
        Assert.InRange(Speed(s), Heli.TopSpeed * 0.985f, Heli.TopSpeed * 1.001f);
        Assert.True(max <= Heli.TopSpeed * 1.001f, $"overshot the top speed: {max}");
        Assert.Equal(-F.MaxPitchDeg, Deg(s.Pitch), 1);       // nose down by the full tilt
        Assert.Equal(50f, s.Position.Y, 3);                  // level flight: no key, no climb
    }

    [Fact]
    public void The_airframe_tilts_first_and_the_speed_follows()
    {
        // motion comes from attitude: a third of a second in, the nose is well down and the helicopter has barely moved
        var w = new Field(); var s = Hover();
        FlightModel.Step(ref s, Heli, Keys(y: 1), Dt, w);
        Assert.True(s.Pitch < 0, "the response starts on the first tick");
        Fly(ref s, w, Keys(y: 1), 0.3f);
        Assert.True(Deg(s.Pitch) < -8, $"pitch {Deg(s.Pitch)}");
        Assert.True(Speed(s) < 2.5f, $"speed {Speed(s)}");
        Assert.True(-s.Position.Z < 0.5f, $"moved {-s.Position.Z} m");
    }

    [Fact]
    public void A_tap_is_a_small_correction_and_a_held_key_the_full_tilt()
    {
        var w = new Field(); var tap = Hover(); var peak = 0f;
        Fly(ref tap, w, Keys(y: 1), 0.1f);
        Fly(ref tap, w, Keys(), 3, x => peak = MathF.Max(peak, -Deg(x.Pitch)));
        Assert.InRange(peak, 0.5f, F.MaxPitchDeg * 0.3f);
        var held = Hover();
        Fly(ref held, w, Keys(y: 1), 2);
        Assert.Equal(F.MaxPitchDeg, -Deg(held.Pitch), 0);
    }

    [Fact]
    public void Sideways_and_backwards_are_slower_than_forwards()
    {
        var w = new Field();
        var side = Hover(); Fly(ref side, w, Keys(x: 1), 20);
        var back = Hover(); Fly(ref back, w, Keys(y: -1), 20);
        Assert.True(side.Velocity.X > 0 && MathF.Abs(side.Velocity.Z) < 0.01f, "D slides to the right");
        Assert.Equal(-F.MaxRollDeg, Deg(side.Roll), 1);       // right side down
        Assert.True(back.Velocity.Z > 0, "S flies backwards");
        Assert.InRange(Speed(side), Heli.TopSpeed * 0.3f, Heli.TopSpeed * 0.55f);
        Assert.InRange(Speed(back), Heli.TopSpeed * 0.25f, Heli.TopSpeed * 0.5f);
    }

    // ---- hands off ----

    [Fact]
    public void Hands_off_from_top_speed_it_stops_in_about_seventy_metres_and_never_rocks_back()
    {
        var w = new Field(); var s = Hover();
        Fly(ref s, w, Keys(y: 1), 25);
        var from = s.Position; var t = 0f; var stopped = -1f; var back = 0f; var flare = 0f;
        Fly(ref s, w, Keys(), 20, x =>
        {
            t += Dt;
            back = MathF.Min(back, -x.Velocity.Z);           // forward is -Z: a negative forward speed is rocking back
            flare = MathF.Max(flare, Deg(x.Pitch));
            if (stopped < 0 && Speed(x) == 0) stopped = t;
        });
        var distance = Vector3.Distance(from, s.Position);
        Assert.InRange(distance, 55f, 85f);
        Assert.InRange(stopped, 3.5f, 6.5f);
        Assert.True(back > -0.01f, $"rocked back at {back} m/s");
        Assert.InRange(flare, F.BrakeTiltDeg - 1.5f, F.BrakeTiltDeg + 0.5f);   // a visible nose-up flare, no further than the limit
        Assert.Equal(50f, s.Position.Y, 3);
    }

    [Fact]
    public void Auto_level_settles_without_overshoot_or_wobble()
    {
        // after the flare the pitch comes back to level from above and stays: one change of sign (nose down, then the flare), no second
        var w = new Field(); var s = Hover();
        Fly(ref s, w, Keys(y: 1), 10);
        var signs = new List<int>();
        Fly(ref s, w, Keys(), 20, x =>
        {
            var sign = Deg(x.Pitch) > 0.02f ? 1 : Deg(x.Pitch) < -0.02f ? -1 : 0;
            if (sign != 0 && (signs.Count == 0 || signs[^1] != sign)) signs.Add(sign);
        });
        Assert.Equal(new[] { -1, 1 }, signs);
        Assert.Equal(0f, s.Pitch, 4);
        Assert.Equal(0f, s.Roll, 4);
        Assert.Equal(0f, s.PitchRate, 4);

        // the roll does the same out of a slide to the right
        var r = Hover(); signs.Clear();
        Fly(ref r, w, Keys(x: 1), 10);
        Fly(ref r, w, Keys(), 20, x =>
        {
            var sign = Deg(x.Roll) > 0.02f ? 1 : Deg(x.Roll) < -0.02f ? -1 : 0;
            if (sign != 0 && (signs.Count == 0 || signs[^1] != sign)) signs.Add(sign);
        });
        Assert.Equal(new[] { -1, 1 }, signs);
        Assert.Equal(0f, Speed(r), 4);
    }

    [Fact]
    public void The_flare_gain_is_inside_the_bound_that_keeps_the_stop_from_ringing()
    {
        // speed -> flare tilt -> deceleration is a loop through the attitude spring; it has no oscillation while
        // (acceleration per radian of tilt) x (tilt per m/s) stays at or under 4/27 of the attitude response
        var perRadian = Heli.Acceleration / MathF.Sin(F.MaxPitchDeg * FlightMath.Deg2Rad);
        var loop = perRadian * F.BrakeGainDeg * FlightMath.Deg2Rad;
        Assert.True(loop <= 4f / 27f * F.AttitudeResponse, $"loop gain {loop} against the bound {4f / 27f * F.AttitudeResponse}");
        var sideways = F.StrafeAcceleration / MathF.Sin(F.MaxRollDeg * FlightMath.Deg2Rad) * F.BrakeGainDeg * FlightMath.Deg2Rad;
        Assert.True(sideways <= 4f / 27f * F.AttitudeResponse);
    }

    [Fact]
    public void The_hover_does_not_drift_at_all()
    {
        var w = new Field(); var s = Hover();
        Fly(ref s, w, Keys(x: 1, y: 1, lift: 1, yaw: 1.2f), 6);
        Fly(ref s, w, Keys(yaw: 1.2f), 25);
        var at = s.Position; var yaw = s.Yaw;
        Fly(ref s, w, Keys(yaw: 1.2f), 60, x =>
        {
            Assert.Equal(at, x.Position);        // exactly: not a millimetre in a minute
            Assert.Equal(Vector3.Zero, x.Velocity);
        });
        Assert.Equal(yaw, s.Yaw);
        Assert.Equal(0f, s.Pitch, 5);
        Assert.Equal(0f, s.Roll, 5);
    }

    [Fact]
    public void Releasing_the_climb_settles_at_a_height_without_sinking_back()
    {
        var w = new Field(); var s = Hover(20);
        Fly(ref s, w, Keys(lift: 1), 3);
        Assert.Equal(F.ClimbRate, s.Velocity.Y, 1);
        var last = s.Position.Y;
        Fly(ref s, w, Keys(), 5, x => { Assert.True(x.Position.Y >= last, "sank back after the key was released"); last = x.Position.Y; });
        Assert.Equal(0f, s.Velocity.Y, 4);
        var held = s.Position.Y;
        Fly(ref s, w, Keys(), 30);
        Assert.Equal(held, s.Position.Y, 3);
    }

    // ---- turning ----

    [Fact]
    public void The_nose_follows_the_view_without_overshoot_and_no_faster_than_its_yaw_rate()
    {
        var w = new Field(); var s = Hover();
        var target = 2.5f; var fastest = 0f; var furthest = 0f;
        Fly(ref s, w, Keys(yaw: target), 4, x => { fastest = MathF.Max(fastest, MathF.Abs(x.YawRate)); furthest = MathF.Max(furthest, x.Yaw); });
        Assert.True(Deg(fastest) <= F.MaxYawRateDeg + 0.01f);
        Assert.True(Deg(fastest) > F.MaxYawRateDeg - 1f, "a big turn uses the full yaw rate");
        Assert.True(furthest <= target + 1e-4f, $"overshot the view by {Deg(furthest - target)} degrees");
        Assert.Equal(target, s.Yaw, 3);
    }

    [Fact]
    public void A_turn_at_speed_banks_into_the_turn_and_the_travel_swings_round_with_the_nose()
    {
        var w = new Field(); var s = Hover();
        Fly(ref s, w, Keys(y: 1), 12);
        var entry = Speed(s); var bank = 0f;
        var left = MathF.PI / 2;                                 // a quarter turn to the left
        Fly(ref s, w, Keys(y: 1, yaw: left), 4, x => bank = MathF.Max(bank, Deg(x.Roll)));
        Assert.InRange(bank, 10f, MathF.Max(F.TurnBankMaxDeg, F.MaxRollDeg) + 0.5f);   // left side down: into the turn
        var (sy, cy) = MathF.SinCos(s.Yaw);
        var sideSlip = Vector3.Dot(s.Velocity, new Vector3(cy, 0, -sy));
        Assert.True(MathF.Abs(sideSlip) < 1f, $"still sliding sideways at {sideSlip} m/s");
        Assert.True(Speed(s) > entry * 0.8f, $"the turn cost too much speed: {entry} -> {Speed(s)}");
        Assert.True(s.Velocity.X < -30f, "flying where the nose points (towards -X)");
        Fly(ref s, w, Keys(y: 1, yaw: left), 3);
        Assert.Equal(0f, Deg(s.Roll), 0);                         // out of the turn it is level again
    }

    [Fact]
    public void In_the_hover_the_nose_turns_on_the_spot_without_banking()
    {
        var w = new Field(); var s = Hover(); var bank = 0f;
        Fly(ref s, w, Keys(yaw: 2f), 3, x => bank = MathF.Max(bank, MathF.Abs(Deg(x.Roll))));
        Assert.True(bank < 0.01f);
        Assert.Equal(new Vector3(0, 50, 0), s.Position);
    }

    // ---- the ground ----

    [Fact]
    public void The_rotor_spools_up_before_it_lifts_off_and_down_after_the_pilot_leaves()
    {
        var w = new Field(); var s = new FlightState { Grounded = true };
        var t = 0f; var off = -1f;
        Fly(ref s, w, Keys(lift: 1), 6, x => { t += Dt; if (off < 0 && !x.Grounded) off = t; });
        Assert.InRange(off, F.SpoolUpTime * F.LiftoffRotor - 0.1f, F.SpoolUpTime * F.LiftoffRotor + 0.1f);
        Assert.Equal(1f, s.Rotor);
        Assert.True(s.Position.Y > 5f);

        // on the ground with no pilot the rotor runs down; in the air it never does
        var parked = new FlightState { Grounded = true, Rotor = 1 };
        Fly(ref parked, w, FlightInput.Pilotless(false), F.SpoolDownTime + 0.2f);
        Assert.Equal(0f, parked.Rotor);
        Assert.True(parked.Grounded);
        Assert.Equal(Vector3.Zero, parked.Position);
    }

    [Fact]
    public void Holding_descend_all_the_way_down_is_a_touchdown_that_costs_nothing()
    {
        var w = new Field(); var s = Hover(40); var hardest = 0f; var fastest = 0f;
        Fly(ref s, w, Keys(lift: -1), 30, x => { hardest = MathF.Max(hardest, x.Impact); fastest = MathF.Max(fastest, -x.Velocity.Y); });
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Y, 3);
        Assert.Equal(F.DescendRate, fastest, 1);                                  // the full rate up high
        Assert.True(hardest <= F.TouchdownSpeed + 0.6f, $"touched down at {hardest} m/s");
        Assert.True(hardest < Shipped.CrashMinSpeedLoss, "a touchdown under power is under the crash threshold");
    }

    [Fact]
    public void A_running_landing_slides_to_a_stop_on_the_skids_without_damage()
    {
        var w = new Field(); var s = Hover(3); var hardest = 0f;
        Fly(ref s, w, Keys(y: 1), 2);
        Fly(ref s, w, Keys(y: 1, lift: -1), 12, x => hardest = MathF.Max(hardest, x.Impact));
        Fly(ref s, w, Keys(lift: -1), 8, x => hardest = MathF.Max(hardest, x.Impact));
        Assert.True(s.Grounded);
        Assert.Equal(Vector3.Zero, s.Velocity);
        Assert.True(hardest < Shipped.CrashMinSpeedLoss);
        Assert.Equal(0f, s.Pitch, 3);                                              // it stands level
    }

    [Fact]
    public void Flying_into_a_wall_is_a_crash_that_hurts_by_the_crash_damage_rules()
    {
        var w = new Field { WallX = 400 };
        var s = Hover(); s.Yaw = -MathF.PI / 2;                                    // facing +X
        var impact = 0f;
        Fly(ref s, w, Keys(y: 1, yaw: -MathF.PI / 2), 20, x => impact = MathF.Max(impact, x.Impact));
        Assert.True(s.Position.X <= 400f + 1e-3f, "it does not pass through the wall");
        Assert.True(impact > 35f, $"impact {impact}");
        var damage = (impact - Shipped.CrashMinSpeedLoss) * Shipped.CrashDamagePerSpeed;
        Assert.InRange(damage, 200f, Heli.Hp);                                     // a full-speed crash is heavy, and one is survivable
        Assert.True(MathF.Abs(s.Velocity.X) < 0.5f, "stopped against the wall");
    }

    [Fact]
    public void It_climbs_to_its_ceiling_and_no_higher()
    {
        var w = new Field(); var s = Hover(100); var top = 0f;
        Fly(ref s, w, Keys(lift: 1), 90, x => top = MathF.Max(top, x.Position.Y));
        Assert.InRange(top, F.Ceiling - 1f, F.Ceiling + 0.05f);
        Assert.Equal(0f, s.Velocity.Y, 2);
        // with no ground in reach under it (past the terrain) it does not climb at all
        var lost = Hover(100); var none = new Field { NoGround = true };
        Fly(ref lost, none, Keys(lift: 1), 5);
        Assert.Equal(100f, lost.Position.Y, 3);
    }

    [Fact]
    public void The_map_edge_slows_it_to_a_stop_and_lets_it_slide_along()
    {
        var w = new Field { Bounds = new FlightBounds(-500, 500, -300, 500) };
        var s = Hover();
        var seen = false;
        Fly(ref s, w, Keys(y: 1), 40, x => { Assert.True(x.Position.Z >= -300f - 1e-3f); seen |= x.AtEdge; });   // forward is -Z
        Assert.True(seen);
        Assert.InRange(s.Position.Z, -300f, -295f);
        Assert.True(Speed(s) < 1f);
        // turning away is free at once
        Fly(ref s, w, Keys(y: 1, yaw: MathF.PI), 6);
        Assert.True(s.Position.Z > -250f);
        Assert.False(s.AtEdge);
    }

    [Fact]
    public void Water_holds_it_at_the_surface_and_reports_the_skids_wet()
    {
        var w = new Field { Ground = -3, Water = 0 };
        var s = Hover(20);
        Fly(ref s, w, Keys(lift: -1), 15);
        Assert.Equal(0f, s.Position.Y, 3);
        Assert.True(s.InWater);
        Assert.False(s.Grounded);                              // it can still pull itself out
        Fly(ref s, w, Keys(lift: 1), 2);
        Assert.False(s.InWater);
        Assert.True(s.Position.Y > 5f);
    }

    // ---- nobody at the controls ----

    [Fact]
    public void With_a_gunner_aboard_and_no_pilot_it_levels_hovers_a_while_and_then_sets_itself_down()
    {
        var w = new Field(); var s = Hover(60);
        Fly(ref s, w, Keys(y: 1), 10);
        var t = 0f; var hardest = 0f; var fastest = 0f; var heightAtGraceEnd = 0f;
        Fly(ref s, w, FlightInput.Pilotless(occupied: true), F.PilotlessHoverTime, x => hardest = MathF.Max(hardest, x.Impact));
        Assert.Equal(60f, s.Position.Y, 2);                    // the hover is held while the gunner might take the seat
        heightAtGraceEnd = s.Position.Y;
        Fly(ref s, w, FlightInput.Pilotless(occupied: true), 40, x =>
        {
            t += Dt; hardest = MathF.Max(hardest, x.Impact); fastest = MathF.Max(fastest, -x.Velocity.Y);
        });
        Assert.True(s.Grounded);
        Assert.True(fastest <= F.AutorotateDescent + 0.01f, $"sank at {fastest} m/s");
        Assert.True(hardest < Shipped.CrashMinSpeedLoss, $"set down at {hardest} m/s: the gunner must walk away");
        Assert.True(heightAtGraceEnd > 59f);
        Assert.True(s.Rotor < 1f, "on the ground the rotor runs down");
    }

    [Fact]
    public void A_gunner_who_takes_the_pilot_seat_in_time_catches_it()
    {
        var w = new Field(); var s = Hover(60);
        Fly(ref s, w, FlightInput.Pilotless(occupied: true), F.PilotlessHoverTime + 5);
        Assert.True(s.Position.Y < 59f, "it had started down");
        Fly(ref s, w, Keys(), 3);
        var held = s.Position.Y;
        Fly(ref s, w, Keys(), 10);
        Assert.Equal(held, s.Position.Y, 2);
        Assert.Equal(0f, s.PilotlessTime);
    }

    [Fact]
    public void An_empty_helicopter_in_the_air_comes_down_at_once()
    {
        var w = new Field(); var s = Hover(30);
        Fly(ref s, w, FlightInput.Pilotless(occupied: false), 2);
        Assert.True(s.Position.Y < 27f);
        Fly(ref s, w, FlightInput.Pilotless(occupied: false), 20);
        Assert.True(s.Grounded);
    }

    // ---- attitude maths ----

    [Fact]
    public void A_seat_offset_tilts_with_the_airframe()
    {
        var s = new FlightState { Position = new Vector3(10, 20, 30), Pitch = 0.3f, Roll = -0.2f, Yaw = 1.1f };
        var offset = new[] { 0.55f, 0.72f, -1.9f };
        var world = s.ToWorld(offset);
        var back = FlightMath.Unrotate(world - s.Position, s.Yaw, s.Pitch, s.Roll);
        Assert.Equal(offset[0], back.X, 4); Assert.Equal(offset[1], back.Y, 4); Assert.Equal(offset[2], back.Z, 4);
        // nose up: a point ahead of the mast rises; left side down: a point on the right rises
        Assert.True(new FlightState { Pitch = 0.3f }.ToWorld(new[] { 0f, 0f, -2f }).Y > 0);
        Assert.True(new FlightState { Roll = 0.3f }.ToWorld(new[] { 1f, 0f, 0f }).Y > 0);
        // level, it is the seat rule every other vehicle uses
        var level = new FlightState { Position = new Vector3(1, 2, 3), Yaw = 0.7f };
        var seat = VehicleSeat.SeatPosition(level.Position, level.Yaw, offset);
        var mine = level.ToWorld(offset);
        Assert.Equal(seat.X, mine.X, 4); Assert.Equal(seat.Y, mine.Y, 4); Assert.Equal(seat.Z, mine.Z, 4);
    }

    // ---- the wire ----

    [Fact]
    public void The_flight_record_carries_velocity_pitch_roll_rotor_and_flags()
    {
        var s = new FlightState { Velocity = new Vector3(12.5f, -3.25f, -40f), Pitch = -0.31f, Roll = 0.22f, Rotor = 0.6f, Grounded = true, AtEdge = true };
        var r = FlightRecord.Make(VehicleRecord.FirstId + 7, s);
        Assert.Equal(EntityType.Flight, r.Type);
        Assert.Equal(FlightRecord.FirstId + 7, r.Id);
        Assert.Equal((ushort)(VehicleRecord.FirstId + 7), FlightRecord.CarOf(r.Id));
        Assert.Equal(s.Velocity.X, FlightRecord.Velocity(r).X, 2);
        Assert.Equal(s.Velocity.Y, FlightRecord.Velocity(r).Y, 2);
        Assert.Equal(s.Velocity.Z, FlightRecord.Velocity(r).Z, 2);
        Assert.Equal(s.Pitch, FlightRecord.Pitch(r), 3);
        Assert.Equal(s.Roll, FlightRecord.Roll(r), 3);
        Assert.Equal(-0.22f, FlightRecord.Roll(FlightRecord.Make(VehicleRecord.FirstId, s with { Roll = -0.22f })), 3);   // a right bank survives the unsigned field
        Assert.Equal(s.Rotor, FlightRecord.Rotor(r), 2);
        Assert.True(FlightRecord.Grounded(r) && FlightRecord.AtEdge(r) && !FlightRecord.InWater(r));
        // the ranges do not collide: vehicles, flight records, turret aims, rockets
        Assert.True(VehicleRecord.FirstId < FlightRecord.FirstId && FlightRecord.FirstId < FlakRecord.FirstId && FlakRecord.FirstId < 0x8000);
    }

    [Fact]
    public void The_flight_record_survives_the_snapshot_as_a_keyframe_and_as_a_delta()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var s = new FlightState { Position = new Vector3(100, 220, -50), Yaw = 1f, Velocity = new Vector3(30, 2, -20), Pitch = -0.3f, Roll = 0.2f, Rotor = 1 };
        EntityRecord[] Records(FlightState x) => new[] { VehicleRecord.Make(VehicleRecord.FirstId, x.Position, x.Yaw, 5, 0x80), FlightRecord.Make(VehicleRecord.FirstId, x) };
        foreach (var pkt in enc.Encode(100, 7, Wire.NoTick, Records(s))) dec.Apply(pkt, out _);
        var back = FlightRecord.State(dec.States[VehicleRecord.FirstId], dec.States[FlightRecord.FirstId]);
        Assert.True(Vector3.Distance(s.Position, back.Position) < 0.02f && Vector3.Distance(s.Velocity, back.Velocity) < 0.02f);
        Assert.Equal(s.Pitch, back.Pitch, 3); Assert.Equal(s.Roll, back.Roll, 3); Assert.Equal(1f, back.Rotor);

        var next = s; next.Position += new Vector3(1, 0.1f, -0.7f); next.Velocity = new Vector3(31, 1.5f, -21); next.Roll = -0.1f; next.Grounded = true;
        var bytes = 0;
        foreach (var pkt in enc.Encode(101, 8, 100, Records(next))) { bytes += pkt.Length; Assert.True(dec.Apply(pkt, out var h) && h.Ack == 8); }
        back = FlightRecord.State(dec.States[VehicleRecord.FirstId], dec.States[FlightRecord.FirstId]);
        Assert.True(Vector3.Distance(next.Velocity, back.Velocity) < 0.02f);
        Assert.Equal(-0.1f, back.Roll, 3);
        Assert.True(back.Grounded);
        Assert.Equal(EntityType.Flight, dec.States[FlightRecord.FirstId].Type);
        Assert.True(bytes < 80, $"a tick of a flying helicopter is {bytes} bytes");
    }

    // ---- prediction ----

    /// <summary>A scripted flight with everything in it: take-off, a dash, turns, a slide, a climb, a wall, a landing.</summary>
    static MoveInput Script(int tick)
    {
        var t = tick * Dt;
        var m = new MoveInput { SpeedScale = 1 };
        if (t < 5) m.Buttons = MoveButtons.Jump;
        else if (t < 12) { m.MoveY = 1; m.Yaw = -MathF.PI / 2; }
        else if (t < 16) { m.MoveY = 1; m.Yaw = 0.4f; m.MoveX = -1; }
        else if (t < 19) { m.Yaw = 0.4f; m.Buttons = MoveButtons.Crouch; }
        else if (t < 24) { m.MoveY = -1; m.MoveX = 1; m.Yaw = 2.2f; m.Buttons = MoveButtons.Jump; }
        else if (t < 40) { m.Yaw = 2.2f; m.Buttons = MoveButtons.Crouch; }
        else m.Yaw = 2.2f;
        return m;
    }

    [Fact]
    public void The_pilots_prediction_and_the_server_step_to_the_same_state_from_the_same_inputs()
    {
        // the client steps from the inputs it sends; the server from what it decodes off the wire, five ticks later; the snapshot that
        // acknowledges an input goes through the records' quantisation. No correction is ever needed, and the states are the same bit for bit.
        var def = Heli;
        var world = new Field { WallX = 180 };
        var start = new FlightState { Grounded = true };
        var client = new FlightPrediction(def, world, start);
        var server = start;
        var inFlight = new Queue<byte[]>();
        var history = new Dictionary<ushort, FlightState>();
        ushort seq = 0;
        for (var tick = 0; tick < 45 * Wire.TickRate; tick++)
        {
            var ni = NetInput.From(++seq, Script(tick), 0, 0);
            client.Step(ni.Seq, FlightInput.FromNet(ni), Dt);
            history[ni.Seq] = client.State;
            inFlight.Enqueue(Wire.EncodeInputs(new[] { ni }));
            if (inFlight.Count <= 5) continue;                       // the latency
            var got = new List<NetInput>();
            Assert.True(Wire.TryDecodeInputs(inFlight.Dequeue(), got, out _, out _));
            FlightModel.Step(ref server, def, FlightInput.FromNet(got[0]), Dt, world);
            Assert.Equal(history[got[0].Seq].Position, server.Position);
            Assert.Equal(history[got[0].Seq].Velocity, server.Velocity);
            Assert.Equal(history[got[0].Seq].Yaw, server.Yaw);
            Assert.Equal(history[got[0].Seq].Pitch, server.Pitch);
            Assert.Equal(history[got[0].Seq].Roll, server.Roll);
            var vehicle = VehicleRecord.Make(VehicleRecord.FirstId, server.Position, server.Yaw, 1, 0);
            var flight = FlightRecord.Make(VehicleRecord.FirstId, server);
            Assert.False(client.Reconcile(got[0].Seq, FlightRecord.State(vehicle, flight), Dt));
        }
        Assert.Equal(0, client.Corrections);
        Assert.True(client.MaxError < 0.02f, $"error {client.MaxError} m is only the snapshot's quantisation");
        Assert.True(client.PendingCount <= 6);
        Assert.True(server.Grounded, "the script ends with a landing");
        Assert.True(history.Values.Max(s => s.Impact) > Shipped.CrashMinSpeedLoss, "the script flew into the wall");
    }

    [Fact]
    public void A_prediction_the_server_disagrees_with_is_put_right_and_the_unseen_inputs_are_replayed()
    {
        var def = Heli; var world = new Field();
        var client = new FlightPrediction(def, world, Hover());
        var server = Hover();
        var sent = new Queue<NetInput>();
        ushort seq = 0;
        void Tick(int tick, bool shove)
        {
            var ni = NetInput.From(++seq, new MoveInput { MoveY = 1, SpeedScale = 1 }, 0, 0);
            client.Step(ni.Seq, FlightInput.FromNet(ni), Dt);
            sent.Enqueue(ni);
            if (sent.Count <= 4) return;
            var got = sent.Dequeue();
            FlightModel.Step(ref server, def, FlightInput.FromNet(got), Dt, world);
            if (shove) { server.Position += new Vector3(3, 1, 0); server.Velocity += new Vector3(4, 0, 0); }   // something only the server knows about
            var state = FlightRecord.State(VehicleRecord.Make(VehicleRecord.FirstId, server.Position, server.Yaw, 1, 0), FlightRecord.Make(VehicleRecord.FirstId, server));
            client.Reconcile(got.Seq, state, Dt);
        }
        for (var i = 0; i < 60; i++) Tick(i, false);
        Assert.Equal(0, client.Corrections);
        Tick(60, true);
        Assert.Equal(1, client.Corrections);
        // the client is where the server will be once it has run the four inputs it has not seen yet
        var ahead = server;
        foreach (var ni in sent) FlightModel.Step(ref ahead, def, FlightInput.FromNet(ni), Dt, world);
        Assert.True(Vector3.Distance(ahead.Position, client.State.Position) < 0.05f, $"off by {Vector3.Distance(ahead.Position, client.State.Position)} m after the correction");
        for (var i = 61; i < 200; i++) Tick(i, false);
        Assert.InRange(client.Corrections, 1, 3);                 // the quantised correction may take one more pass, then it holds
        Assert.True(client.MaxError > 3f);
    }

    [Fact]
    public void An_acknowledgement_from_before_the_pilot_sat_down_changes_nothing()
    {
        var client = new FlightPrediction(Heli, new Field(), Hover());
        client.Step(10, Keys(y: 1), Dt);
        var before = client.State;
        Assert.False(client.Reconcile(7, new FlightState { Position = new Vector3(99, 99, 99) }, Dt));
        Assert.Equal(before.Position, client.State.Position);
        Assert.Equal(1, client.PendingCount);
    }

    [Fact]
    public void A_pilot_steering_a_killstreak_leaves_the_helicopter_hands_off()
    {
        var ni = NetInput.From(1, new MoveInput { MoveX = 1, MoveY = 1, Yaw = 2f, Buttons = MoveButtons.Jump, SpeedScale = 1 }, 0, 0);
        var busy = FlightInput.FromNet(ni, handsOff: true);
        Assert.True(busy.Piloted && busy.HoldHeading);
        Assert.Equal(0f, busy.MoveX); Assert.Equal(0f, busy.MoveY); Assert.Equal(0, busy.Lift);
        var free = FlightInput.FromNet(ni);
        Assert.Equal(1f, free.MoveX, 2); Assert.Equal(1, free.Lift); Assert.False(free.HoldHeading);
        var down = FlightInput.FromNet(NetInput.From(2, new MoveInput { Buttons = MoveButtons.Crouch | MoveButtons.Jump, SpeedScale = 1 }, 0, 0));
        Assert.Equal(0, down.Lift);                               // both keys cancel
    }
}
