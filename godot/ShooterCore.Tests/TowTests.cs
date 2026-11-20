using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Towing's pure core: the kinematic single-axle trailer (straight pull, steady turn, jackknife and its limit, sway), the auto-latch and the lockout, the load a trailer puts on
/// its tractor (<see cref="DriveModel"/> and <see cref="CarModel"/>), the footprint and the blocked trailer, determinism at the game's tick, and the data blocks.
/// </summary>
public class TowTests
{
    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static VehiclesFile Load() => VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static readonly VehiclesFile Shipped = Load();
    static VehicleDef Jeep => Shipped.Vehicles.Single(v => v.Id == "jeep");
    static VehicleDef Flak => Shipped.Vehicles.Single(v => v.Id == "flak");

    /// <summary>The standard car with a hitch: a tractor on the drive model.</summary>
    static VehicleDef Tractor()
    {
        var car = Load().Vehicles.Single(v => v.Id == "car");
        car.Tow = new VehicleTowDef { Hitch = new[] { 0f, 0.5f, 2.3f } };
        return car;
    }

    /// <summary>A rocket-cart-like trailer: drawbar 2.2 m, 1500 kg.</summary>
    static VehicleDef Cart(float grip = 5.5f) => new()
    {
        Id = "cart", TopSpeed = 20, Acceleration = 1, Mass = 1500, HalfWidth = 0.9f, HalfLength = 2.2f, Seats = new(),
        Trailer = new VehicleTrailerDef { Eye = new[] { 0f, 0.5f, -1.8f }, Axle = new[] { 0f, 0.4f, 0.4f }, Mass = 1500, Grip = grip },
    };

    /// <summary>A tractor driven on a prescribed path: speed and yaw rate set, pose integrated (to test the trailer alone).</summary>
    sealed class Path_
    {
        public Vector3 Pos; public float Yaw, Speed, Slip, YawRate;
        public void Move(float dt)
        {
            Yaw = FlightMath.Wrap(Yaw + YawRate * dt);
            Pos += new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)) * Speed * dt + new Vector3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw)) * Slip * dt;
        }
        public TractorMotion Motion => new(Pos, Yaw, Speed, Slip, YawRate);
    }

    static TowGeometry Geo(VehicleDef cart = null!, VehicleDef? tractor = null) => TowGeometry.Of(tractor ?? Tractor(), cart ?? Cart());

    /// <summary>The trailer standing with its eye exactly on the tractor's hitch, heading <paramref name="yaw"/>.</summary>
    static TrailerBody AtHitch(in TowGeometry g, in TractorMotion t, float yaw, Vector3 shift = default)
    {
        var hitch = TowModel.HitchPoint(g, t.Position, t.Yaw);
        var (s, c) = MathF.SinCos(yaw);
        return TrailerBody.At(new Vector3(hitch.X - (g.EyeX * c + g.EyeZ * s), hitch.Y - g.EyeY, hitch.Z - (-g.EyeX * s + g.EyeZ * c)) + shift, yaw);
    }

    static TrailerBody Hitched(in TowGeometry g, in TractorMotion t, float phi)
    {
        var b = new TrailerBody { Phi = phi };
        b.Yaw = FlightMath.Wrap(t.Yaw + phi);
        TowModel.Step(ref b, g, t, 0f); // pins it
        return b;
    }

    // ---- data ----

    [Fact]
    public void The_jeep_the_humvee_and_the_flak_truck_tow_and_nobody_else_does()
    {
        Assert.Equal(new[] { "flak", "humvee", "jeep" }, Shipped.Vehicles.Where(v => v.Tow != null).Select(v => v.Id).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "rocket_cart" }, Shipped.Vehicles.Where(v => v.Trailer != null).Select(v => v.Id).ToArray()); // the only trailer shipped
        foreach (var v in new[] { Jeep, Flak, Shipped.Vehicles.Single(x => x.Id == "humvee") })
            Assert.True(v.Tow!.Hitch[2] > v.HalfLength, $"{v.Id}: the hitch is behind the footprint");
    }

    [Fact]
    public void A_tow_and_a_trailer_block_parse_and_validate()
    {
        const string json = """
            { "version": 1, "vehicles": [
              { "id": "cart", "top_speed": 20, "acceleration": 1, "mass": 900, "half_width": 0.9, "half_length": 2.2, "seats": [],
                "trailer": { "eye": [0, 0.5, -1.8], "axle": [0, 0.4, 0.4], "mass": 900, "grip": 6 } },
              { "id": "truck", "top_speed": 20, "acceleration": 3, "mass": 4000,
                "tow": { "hitch": [0, 0.6, 3.0], "limit_deg": 70, "speed_fraction": 0.5 } } ] }
            """;
        var f = VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(json, "test"));
        var cart = f.Vehicles.Single(v => v.Id == "cart"); var truck = f.Vehicles.Single(v => v.Id == "truck");
        Assert.Equal(2.2f, cart.Trailer!.Drawbar, 3);
        Assert.Equal(6f, cart.Trailer.Grip);
        Assert.Equal(70f, truck.Tow!.LimitDeg);
        Assert.Equal(0.5f, truck.Tow.SpeedFraction);
        Assert.Equal(0.8f, truck.Tow.LatchRadius); // defaults
        Assert.True(TowGeometry.CanTow(truck, cart));
        Assert.False(TowGeometry.CanTow(cart, truck));
    }

    [Theory]
    [InlineData("seat")]
    [InlineData("both")]
    [InlineData("drawbar")]
    [InlineData("eye_x")]
    [InlineData("hitch_front")]
    [InlineData("limit")]
    [InlineData("fraction")]
    public void A_bad_tow_or_trailer_block_is_refused(string what)
    {
        var f = Load();
        var cart = Cart(); f.Vehicles.Add(cart);
        var jeep = f.Vehicles.Single(v => v.Id == "jeep");
        switch (what)
        {
            case "seat": cart.Seats = new() { new VehicleSeatDef { Role = "driver" } }; break;
            case "both": cart.Tow = new VehicleTowDef(); break;
            case "drawbar": cart.Trailer!.Axle = new[] { 0f, 0.4f, -1.6f }; break;
            case "eye_x": cart.Trailer!.Eye = new[] { 0.3f, 0.5f, -1.8f }; break;
            case "hitch_front": jeep.Tow!.Hitch = new[] { 0f, 0.5f, -2.4f }; break;
            case "limit": jeep.Tow!.LimitDeg = 95; break;
            case "fraction": jeep.Tow!.SpeedFraction = 1.5f; break;
        }
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        // and the unchanged file with a sound cart is accepted
        var ok = Load(); ok.Vehicles.Add(Cart());
        VehiclesFile.Validate(ok);
    }

    // ---- the trailer's kinematics ----

    [Fact]
    public void Pulled_straight_the_trailer_stays_in_line_behind_the_hitch()
    {
        var g = Geo(); var t = new Path_ { Speed = 12 };
        var b = Hitched(g, t.Motion, 0f);
        for (var i = 0; i < 300; i++) { t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt); }
        Assert.Equal(0f, b.Phi, 6);
        Assert.Equal(t.Yaw, b.Yaw, 6);
        Assert.Equal(0f, b.Slide);
        Assert.Equal(12f, b.Speed, 3);
        Assert.Equal(t.Pos.X, b.Position.X, 4); // straight behind
        Assert.True(Vector3.Distance(TowModel.EyePoint(g, b), TowModel.HitchPoint(g, t.Pos, t.Yaw)) < 1e-3f, "the eye is on the hitch");
    }

    [Fact]
    public void A_trailer_pulled_off_line_swings_in_behind_the_tractor()
    {
        var g = Geo(); var t = new Path_ { Speed = 10 };
        var b = Hitched(g, t.Motion, 0.5f);
        var last = MathF.Abs(b.Phi);
        for (var i = 0; i < 150; i++)
        {
            t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt);
            Assert.True(MathF.Abs(b.Phi) <= last + 1e-6f, "the angle only closes");
            last = MathF.Abs(b.Phi);
        }
        Assert.True(last < 0.01f);
    }

    [Theory]
    [InlineData(5f, 0.2f)]
    [InlineData(8f, -0.3f)]
    [InlineData(12f, 0.1f)]
    public void A_steady_turn_converges_to_the_expected_articulation(float speed, float yawRate)
    {
        var cart = Cart(grip: 100f); // tyres hold: no sway
        var g = Geo(cart); var t = new Path_ { Speed = speed, YawRate = yawRate };
        var b = Hitched(g, t.Motion, 0f);
        for (var i = 0; i < 30 * 15; i++) { t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt); }
        // dphi/dt = 0 with the trailer turning at the tractor's rate: v sin(phi) + w hz cos(phi) = -L w, so phi = asin(-L w / R) - atan2(w hz, v) with R = hypot(v, w hz)
        var along = yawRate * g.HitchZ;
        var expected = MathF.Asin(-g.Drawbar * yawRate / MathF.Sqrt(speed * speed + along * along)) - MathF.Atan2(along, speed);
        Assert.Equal(expected, b.Phi, 3);
        Assert.True(MathF.Sign(b.Phi) == -MathF.Sign(yawRate), "the trailer cuts the corner: it lags to the inside");
        Assert.Equal(0f, b.Slide);
    }

    [Fact]
    public void Reversing_jackknifes_until_the_limit_stops_it()
    {
        var g = Geo(); var t = new Path_ { Speed = -2.5f };
        var b = Hitched(g, t.Motion, 0.02f);
        var last = b.Phi;
        for (var i = 0; i < 30 * 30; i++)
        {
            t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt);
            Assert.True(b.Phi >= last - 1e-6f, "reversing only folds it further");
            last = b.Phi;
        }
        Assert.Equal(g.Limit, b.Phi, 4);
        Assert.True(g.Limit > 70f * MathF.PI / 180f);
        var block = TowModel.BlockOf(g, b);
        Assert.True(block.Active);
        Assert.Equal(1, block.Side);
        float v = -2.5f, slip = 0, w = 0;
        Assert.True(block.Clamp(ref v, ref slip, ref w));
        Assert.Equal(0f, v);
        v = 2f; w = 0;
        Assert.False(block.Clamp(ref v, ref slip, ref w)); // forward unfolds it: free
        Assert.Equal(2f, v);
        v = 1f; w = -0.5f; // turning the tractor further round the fold, even slowly forward
        Assert.True(block.Increases(0f, 0f, -0.5f));
    }

    [Fact]
    public void Reversing_folds_to_the_other_side_for_a_left_kink_and_the_block_follows()
    {
        var g = Geo(); var t = new Path_ { Speed = -2.5f };
        var b = Hitched(g, t.Motion, -0.02f);
        for (var i = 0; i < 30 * 30; i++) { t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt); }
        Assert.Equal(-g.Limit, b.Phi, 4);
        Assert.Equal(-1, TowModel.BlockOf(g, b).Side);
    }

    [Fact]
    public void The_drive_model_stops_a_reversing_tractor_at_the_jackknife_limit()
    {
        var def = Tractor(); var cart = Cart(); var g = TowGeometry.Of(def, cart);
        var world = new Flat();
        var s = new DriveState { Position = new Vector3(0, 0, 0) };
        DriveModel.Settle(ref s, def, world);
        var link = new TowLink(Vector3.Zero, 0f);
        link.Body = AtHitch(g, TractorMotion.From(s, def), 0.03f);
        Assert.True(link.TryLatch(g, new TractorMotion(s.Position, s.Yaw, -1f, 0, 0))); // reversing onto it
        var effect = default(TowEffect);
        var minSpeed = 0f;
        for (var i = 0; i < 30 * 40; i++)
        {
            effect = link.Effect(def, cart, g);
            DriveModel.Step(ref s, def, new DriveInput { Throttle = -1f }, Dt, world, effect);
            minSpeed = MathF.Min(minSpeed, s.Speed);
            link.StepHitched(g, TractorMotion.From(s, def), Dt);
            Assert.True(MathF.Abs(link.Body.Phi) <= g.Limit + 1e-4f);
        }
        Assert.True(minSpeed < -1f, "it did reverse until it folded");
        Assert.Equal(0f, s.Speed, 3);
        Assert.Equal(g.Limit, MathF.Abs(link.Body.Phi), 3);
        // and drives off again forwards, unfolding
        for (var i = 0; i < 30 * 12; i++)
        {
            DriveModel.Step(ref s, def, new DriveInput { Throttle = 1f }, Dt, world, link.Effect(def, cart, g));
            link.StepHitched(g, TractorMotion.From(s, def), Dt);
        }
        Assert.True(s.Speed > 3f);
        Assert.True(MathF.Abs(link.Body.Phi) < 0.2f, "pulled forwards the trailer straightens");
    }

    [Fact]
    public void Taking_a_turn_too_fast_lets_the_trailer_swing_out()
    {
        var cart = Cart(grip: 5.5f); var g = Geo(cart);
        // 10 m/s at 0.8 rad/s asks 8 m/s² of the 5.5 the tyres hold
        var t = new Path_ { Speed = 10, YawRate = 0.8f };
        var b = Hitched(g, t.Motion, 0f);
        for (var i = 0; i < 30 * 8; i++) { t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt); }
        var grippy = Cart(grip: 100f); var g2 = Geo(grippy);
        var t2 = new Path_ { Speed = 10, YawRate = 0.8f };
        var b2 = Hitched(g2, t2.Motion, 0f);
        for (var i = 0; i < 30 * 8; i++) { t2.Move(Dt); TowModel.Step(ref b2, g2, t2.Motion, Dt); }
        Assert.True(b.Slide < -1f, "the axle slides out of a left bend: to the right");
        Assert.Equal(0f, b2.Slide);
        Assert.True(b.Phi > b2.Phi + 0.1f, "the rear is out wider than the tyres would have held it");
        Assert.True(MathF.Abs(b.Phi) < g.Limit);
        // a gentle bend does not sway
        var t3 = new Path_ { Speed = 5, YawRate = 0.3f };
        var b3 = Hitched(g, t3.Motion, 0f);
        for (var i = 0; i < 30 * 5; i++) { t3.Move(Dt); TowModel.Step(ref b3, g, t3.Motion, Dt); }
        Assert.Equal(0f, b3.Slide);
    }

    // ---- latch ----

    [Fact]
    public void A_tractor_reversing_its_hitch_onto_the_eye_latches_at_once()
    {
        var g = Geo(); var tractor = new TractorMotion(new Vector3(3, 0.2f, -7), 0.1f, -1.2f, 0f, 0f);
        var link = new TowLink(Vector3.Zero, 0f) { Body = AtHitch(g, tractor, 0.1f) };
        Assert.Equal(LatchCheck.Ok, link.CheckLatch(g, tractor));
        Assert.True(link.TryLatch(g, tractor));
        Assert.Equal(TrailerState.Hitched, link.State);
        Assert.True(link.Hitched);
        Assert.Equal(0f, link.Body.Phi, 4);
        Assert.Equal(LatchCheck.AlreadyHitched, link.CheckLatch(g, tractor));
    }

    [Fact]
    public void The_latch_needs_the_tractor_to_be_reversing()
    {
        var g = Geo(); var at = new TractorMotion(Vector3.Zero, 0f, 0f, 0f, 0f);
        var b = AtHitch(g, at, 0f);
        var link = new TowLink(Vector3.Zero, 0f) { Body = b };
        Assert.Equal(LatchCheck.NotReversing, link.CheckLatch(g, at with { Speed = 0f }));
        Assert.Equal(LatchCheck.NotReversing, link.CheckLatch(g, at with { Speed = 3f }));
        Assert.Equal(LatchCheck.NotReversing, link.CheckLatch(g, at with { Speed = -0.2f })); // creeping is not reversing
        Assert.Equal(LatchCheck.Ok, link.CheckLatch(g, at with { Speed = -0.5f }));
        Assert.False(link.TryLatch(g, at with { Speed = 3f }));
        Assert.Equal(TrailerState.Loose, link.State);
    }

    [Fact]
    public void The_latch_needs_the_hitch_within_the_radius_of_the_eye()
    {
        var g = Geo(); var at = new TractorMotion(Vector3.Zero, 0f, -1f, 0f, 0f);
        var link = new TowLink(Vector3.Zero, 0f);
        link.Body = AtHitch(g, at, 0f, new Vector3(0, 0, 0.7f));
        Assert.Equal(LatchCheck.Ok, link.CheckLatch(g, at));
        link.Body = AtHitch(g, at, 0f, new Vector3(0, 0, 0.9f));
        Assert.Equal(LatchCheck.TooFar, link.CheckLatch(g, at));
        link.Body = AtHitch(g, at, 0f, new Vector3(0.6f, 0, 0.6f)); // 0.85 diagonally
        Assert.Equal(LatchCheck.TooFar, link.CheckLatch(g, at));
        link.Body = AtHitch(g, at, 0f, new Vector3(0, 1.5f, 0)); // above it
        Assert.Equal(LatchCheck.TooFar, link.CheckLatch(g, at));
        Assert.False(link.TryLatch(g, at));
    }

    [Fact]
    public void The_latch_needs_the_headings_within_the_angle()
    {
        var g = Geo(); var at = new TractorMotion(new Vector3(5, 0, 5), 0.2f, -1f, 0f, 0f);
        var link = new TowLink(Vector3.Zero, 0f);
        link.Body = AtHitch(g, at, 0.2f + 30f * MathF.PI / 180f);
        Assert.Equal(LatchCheck.Ok, link.CheckLatch(g, at));
        link.Body = AtHitch(g, at, 0.2f - 30f * MathF.PI / 180f);
        Assert.Equal(LatchCheck.Ok, link.CheckLatch(g, at));
        link.Body = AtHitch(g, at, 0.2f + 40f * MathF.PI / 180f);
        Assert.Equal(LatchCheck.AngleTooBig, link.CheckLatch(g, at));
        link.Body = AtHitch(g, at, 0.2f + MathF.PI); // facing the tractor
        Assert.Equal(LatchCheck.AngleTooBig, link.CheckLatch(g, at));
    }

    [Fact]
    public void After_an_unhitch_the_trailer_cannot_be_latched_for_two_seconds()
    {
        var g = Geo(); var at = new TractorMotion(Vector3.Zero, 0f, -1f, 0f, 0f);
        var link = new TowLink(Vector3.Zero, 0f) { Body = AtHitch(g, at, 0f) };
        Assert.False(link.Unhitch(g)); // nothing to unhitch
        Assert.True(link.TryLatch(g, at));
        Assert.True(link.Unhitch(g));
        Assert.Equal(TrailerState.Released, link.State);
        Assert.False(link.Unhitch(g));
        Assert.Equal(LatchCheck.LockedOut, link.CheckLatch(g, at));
        Assert.False(link.TryLatch(g, at));
        for (var i = 0; i < 59; i++) link.StepFree(g, Dt); // 1.97 s
        Assert.Equal(TrailerState.Released, link.State);
        Assert.InRange(link.LockoutLeft, 0.01f, 0.1f);
        Assert.False(link.TryLatch(g, at));
        for (var i = 0; i < 3; i++) link.StepFree(g, Dt);
        Assert.Equal(TrailerState.Loose, link.State);
        Assert.Equal(0f, link.LockoutLeft);
        Assert.True(link.TryLatch(g, at), "the same tractor still reversing at the eye latches it again");
    }

    [Fact]
    public void A_trailer_released_at_speed_rolls_to_a_stop_and_then_stands()
    {
        var g = Geo(); var t = new Path_ { Speed = 6 };
        var link = new TowLink(Vector3.Zero, 0f) { Body = AtHitch(g, t.Motion, 0f) };
        Assert.True(link.TryLatch(g, new TractorMotion(t.Pos, t.Yaw, -1f, 0, 0)));
        for (var i = 0; i < 60; i++) { t.Move(Dt); link.StepHitched(g, t.Motion, Dt); }
        Assert.True(link.Unhitch(g));
        var from = link.Body.Position;
        Assert.Equal(6f, link.Body.Speed, 2);
        for (var i = 0; i < 30 * 6; i++) link.StepFree(g, Dt);
        Assert.Equal(0f, link.Body.Speed);
        Assert.Equal(from.Z - 6f * 6f / (2f * g.RollDecel), link.Body.Position.Z, 0); // v²/2a along its heading (-Z)
        var rest = link.Body.Position;
        for (var i = 0; i < 30; i++) link.StepFree(g, Dt);
        Assert.Equal(rest, link.Body.Position);
        Assert.Equal(TrailerState.Loose, link.State);
    }

    // ---- footprint, blocked trailer ----

    sealed class Wall : ITrailerWorld
    {
        public float MinZ = float.NegativeInfinity;
        public bool Blocked(in BodyBox from, in BodyBox to, float y) => to.Z > MinZ && MinZ > float.NegativeInfinity;
    }

    [Fact]
    public void The_footprint_is_the_trailers_box_at_its_pose()
    {
        var g = Geo();
        var b = TrailerBody.At(new Vector3(4, 0, 9), 0.7f);
        var box = TowModel.Footprint(g, b);
        Assert.Equal((4f, 9f, 0.9f, 2.2f, 0.7f), (box.X, box.Z, box.HalfW, box.HalfL, box.Yaw));
    }

    [Fact]
    public void A_blocked_trailer_keeps_its_place_and_the_tractor_is_held()
    {
        var def = Tractor(); var cart = Cart(); var g = TowGeometry.Of(def, cart);
        var world = new Flat();
        var s = new DriveState(); DriveModel.Settle(ref s, def, world);
        var link = new TowLink(Vector3.Zero, 0f) { Body = AtHitch(g, TractorMotion.From(s, def), 0f) };
        link.TryLatch(g, new TractorMotion(s.Position, s.Yaw, -1f, 0, 0));
        var wall = new Wall(); // the trailer can go back no further than z = 8
        wall.MinZ = 8f;
        var held = false;
        for (var i = 0; i < 30 * 10; i++)
        {
            var before = s; var bodyBefore = link.Body;
            DriveModel.Step(ref s, def, new DriveInput { Throttle = -1f }, Dt, world, link.Effect(def, cart, g));
            if (!link.StepHitched(g, TractorMotion.From(s, def), Dt, wall))
            {
                held = true;
                TowModel.Hold(ref s, before);
                Assert.Equal(bodyBefore.Position, link.Body.Position);
                Assert.Equal(before.Position, s.Position);
                Assert.Equal(0f, s.Speed);
                Assert.True(s.Impact > 0f);
            }
        }
        Assert.True(held);
        Assert.True(link.Body.Position.Z - g.HalfLength <= 8f + 1f, "stopped at the wall");
        Assert.True(s.Position.Z < 8f - 2.3f, "the tractor stood still behind the trailer");
        Assert.True(link.Hitched);
    }

    /// <summary>
    /// A dyke 3 m high: flat on top for z &gt;= 0, falling at 30 degrees to its foot. The ground is found only from near it, as a ray does (nothing from more than 1.5 m under it), and a trailer
    /// asked about at a height that far under the ground it stands on is in the dyke: blocked.
    /// </summary>
    sealed class Dyke : ITrailerWorld
    {
        public static float Ground(float z) => Math.Clamp(3f + z * MathF.Tan(MathF.PI / 6), 0f, 3f);
        public float GroundY(float x, float z, float hint) => Ground(z) <= hint + 1.5f ? Ground(z) : hint;
        public bool Blocked(in BodyBox from, in BodyBox to, float y) => Ground(to.Z) > y + 1.5f;
    }

    [Fact]
    public void A_trailer_on_a_dyke_keeps_its_own_ground_while_the_tractor_is_at_the_foot()
    {
        // #377: the tractor has gone down (-Z) and stands 3 m lower; the hitch's height is its ground, not the trailer's
        var def = Tractor(); var cart = Cart(); var g = TowGeometry.Of(def, cart);
        var world = new Dyke();
        var t = new TractorMotion(new Vector3(0, 0, -5.2f), 0f, 3f, 0f, 0f);
        var body = TowModel.Pose(g, t.Position, t.Yaw, 0f);
        Assert.True(Dyke.Ground(body.Position.Z) > 1.5f, "the trailer is still high on the slope");
        body.Position.Y = Dyke.Ground(body.Position.Z);
        for (var i = 0; i < 30 * 4; i++)
        {
            t = t with { Position = t.Position + new Vector3(0, 0, -t.Speed * Dt) };
            Assert.True(TowModel.StepChecked(ref body, g, t, Dt, world), $"blocked at tick {i}, z {body.Position.Z}");
            Assert.Equal(Dyke.Ground(body.Position.Z), body.Position.Y, 3);
        }
        Assert.Equal(0f, body.Position.Y, 3); // down with the tractor
    }

    [Fact]
    public void Coupling_keeps_the_trailers_own_height()
    {
        var def = Tractor(); var cart = Cart(); var g = TowGeometry.Of(def, cart);
        var body = TrailerBody.At(new Vector3(0, 2.5f, 6f), 0f);
        TowModel.Couple(ref body, g, new TractorMotion(Vector3.Zero, 0f, 0f, 0f, 0f));
        Assert.Equal(2.5f, body.Position.Y);
        Assert.Equal(TowModel.HitchPoint(g, Vector3.Zero, 0f).Z, TowModel.EyePoint(g, body).Z, 4);
    }

    // ---- load ----

    [Fact]
    public void The_load_scales_pull_brake_top_speed_and_grip_by_the_masses_and_the_data()
    {
        var def = Tractor(); var cart = Cart();
        var load = TowLoad.Of(def, cart);
        Assert.True(load.Hitched);
        Assert.Equal(1200f / 2700f, load.AccelScale, 5);
        Assert.Equal(1200f / 2700f, load.BrakeScale, 5);
        Assert.Equal(0.6f, load.TopSpeedScale);
        Assert.Equal(0.8f, load.LateralScale);
        var none = TowLoad.None;
        Assert.False(none.Hitched);
        Assert.Equal((1f, 1f, 1f, 1f), (none.AccelScale, none.BrakeScale, none.TopSpeedScale, none.LateralScale));
        Assert.Equal(1f, default(TowEffect).Load.AccelScale);
        Assert.False(default(TowEffect).Block.Active);
        Assert.Throws<ArgumentException>(() => TowLoad.Of(cart, def));
        // the jeep and the flak truck tow the cart as well
        Assert.True(TowLoad.Of(Jeep, cart).AccelScale < TowLoad.Of(Flak, cart).AccelScale, "a heavier tractor feels less of it");
    }

    sealed class Flat : IDriveWorld
    {
        public float GroundY(Vector3 from) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    static DriveState Run(VehicleDef def, DriveState s, in DriveInput input, int ticks, in TowEffect tow)
    {
        var world = new Flat();
        for (var i = 0; i < ticks; i++) DriveModel.Step(ref s, def, input, Dt, world, tow);
        return s;
    }

    static TowEffect Hooked(VehicleDef def) => new(TowLoad.Of(def, Cart()), default);

    [Fact]
    public void Hitched_the_drive_model_pulls_slower_and_tops_out_lower()
    {
        var def = Tractor(); var world = new Flat();
        var start = new DriveState(); DriveModel.Settle(ref start, def, world);
        var free = Run(def, start, new DriveInput { Throttle = 1f }, 30 * 3, TowEffect.None);
        var towing = Run(def, start, new DriveInput { Throttle = 1f }, 30 * 3, Hooked(def));
        Assert.True(towing.Speed < free.Speed * 0.75f, $"{towing.Speed} against {free.Speed}");
        var top = Run(def, start, new DriveInput { Throttle = 1f }, 30 * 60, Hooked(def));
        Assert.InRange(top.Speed, def.TopSpeed * 0.6f * 0.95f, def.TopSpeed * 0.6f + 0.05f);
    }

    [Fact]
    public void Hitched_it_takes_longer_to_stop()
    {
        var def = Tractor(); var world = new Flat();
        float Distance(TowEffect tow)
        {
            var s = new DriveState(); DriveModel.Settle(ref s, def, world); s.Speed = 15f;
            var d = 0f;
            for (var i = 0; i < 30 * 20 && s.Speed > 0.01f; i++) { var p = s.Position; DriveModel.Step(ref s, def, new DriveInput { Throttle = -1f }, Dt, world, tow); d += Vector3.Distance(p, s.Position); }
            return d;
        }
        var free = Distance(TowEffect.None); var towing = Distance(Hooked(def));
        Assert.True(towing > free * 1.8f, $"{towing} m against {free} m");
    }

    [Fact]
    public void Hitched_it_holds_less_sideways_acceleration()
    {
        var def = Tractor(); var world = new Flat();
        var s = new DriveState(); DriveModel.Settle(ref s, def, world); s.Speed = 20f;
        var free = Run(def, s, new DriveInput { Throttle = 0.5f, Steer = 1f }, 30, TowEffect.None);
        var towing = Run(def, s, new DriveInput { Throttle = 0.5f, Steer = 1f }, 30, Hooked(def));
        var cap = def.MaxLateralAccel * 0.8f;
        Assert.True(MathF.Abs(free.YawRate) * free.Speed > cap * 1.5f, "unhitched it takes the bend harder than the cap");
        Assert.True(MathF.Abs(towing.YawRate) * towing.Speed <= cap * 1.02f, $"{MathF.Abs(towing.YawRate) * towing.Speed} against {cap}");
    }

    [Fact]
    public void An_unhitched_step_is_unchanged_with_or_without_the_tow_argument()
    {
        var def = Tractor(); var world = new Flat();
        var a = new DriveState(); DriveModel.Settle(ref a, def, world); var b = a;
        for (var i = 0; i < 200; i++)
        {
            var input = new DriveInput { Throttle = i < 120 ? 1f : -1f, Steer = MathF.Sin(i * 0.1f), Handbrake = i > 180 };
            DriveModel.Step(ref a, def, input, Dt, world);
            DriveModel.Step(ref b, def, input, Dt, world, TowEffect.None);
        }
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_car_model_takes_the_same_load()
    {
        var jeep = Jeep; var cart = Cart();
        var load = TowLoad.Of(jeep, cart);
        var tow = new TowEffect(load, default);
        var free = new CarState(); var towing = new CarState();
        for (var i = 0; i < 30 * 60; i++)
        {
            CarModel.Step(ref free, jeep, 1f, 0f, false, Dt);
            CarModel.Step(ref towing, jeep, 1f, 0f, false, Dt, false, tow);
        }
        Assert.Equal(jeep.TopSpeed, free.Speed, 3);
        Assert.Equal(jeep.TopSpeed * 0.6f, towing.Speed, 3);
        // braking from the same speed takes longer
        var a = new CarState { Speed = 15 }; var b = new CarState { Speed = 15 };
        float da = 0, db = 0;
        for (var i = 0; i < 30 * 30; i++)
        {
            if (a.Speed > 0) { CarModel.Step(ref a, jeep, -1f, 0f, false, Dt); da += a.Speed * Dt; }
            if (b.Speed > 0) { CarModel.Step(ref b, jeep, -1f, 0f, false, Dt, false, tow); db += b.Speed * Dt; }
        }
        Assert.True(db > da * 1.15f); // the jeep is a 6500 kg cargo truck: a 1500 kg cart takes a fifth of its braking
        // the bend is wider, and a block at the limit stops a reversing one
        var c = new CarState { Speed = 12 };
        CarModel.Step(ref c, jeep, 0f, 1f, false, 1f, false, tow);
        Assert.True(MathF.Abs(c.YawRate) * MathF.Abs(c.Speed) <= jeep.MaxLateralAccel * 0.8f * 1.02f);
        var g = TowGeometry.Of(jeep, cart);
        var jam = TowModel.BlockOf(g, new TrailerBody { Phi = g.Limit });
        var r = new CarState { Speed = -3 };
        CarModel.Step(ref r, jeep, -1f, 0f, false, Dt, false, new TowEffect(load, jam));
        Assert.Equal(0f, r.Speed);
    }

    // ---- determinism, stability ----

    static TrailerBody Drive(TowGeometry g, float dt, float seconds, float speed, float yawRate)
    {
        var t = new Path_ { Speed = speed, YawRate = yawRate };
        var b = Hitched(g, t.Motion, 0.4f);
        for (var i = 0; i < (int)MathF.Round(seconds / dt); i++) { t.Move(dt); TowModel.Step(ref b, g, t.Motion, dt); }
        return b;
    }

    [Fact]
    public void The_same_inputs_give_the_same_trailer_bit_for_bit()
    {
        var g = Geo();
        var a = Drive(g, Dt, 20, 9f, 0.35f); var b = Drive(g, Dt, 20, 9f, 0.35f);
        Assert.Equal(a, b);
        var c = Drive(g, Dt, 20, -3f, 0.2f); var d = Drive(g, Dt, 20, -3f, 0.2f);
        Assert.Equal(c, d);
    }

    [Fact]
    public void The_articulation_does_not_depend_on_the_tick_rate_and_never_explodes()
    {
        var g = Geo(Cart(grip: 100f));
        var at30 = Drive(g, 1f / 30f, 12, 10f, 0.25f);
        var at60 = Drive(g, 1f / 60f, 12, 10f, 0.25f);
        var at120 = Drive(g, 1f / 120f, 12, 10f, 0.25f);
        Assert.Equal(at120.Phi, at30.Phi, 2);
        Assert.Equal(at120.Phi, at60.Phi, 2);
        // a stalled frame, a fast tractor and a short drawbar at once
        foreach (var dt in new[] { 0.05f, 0.1f, 0.2f, 1f })
            foreach (var speed in new[] { -40f, -5f, 5f, 40f })
            {
                var b = Drive(g, dt, 20, speed, 0.5f);
                Assert.True(float.IsFinite(b.Phi) && float.IsFinite(b.Position.X) && float.IsFinite(b.Slide), $"dt {dt} speed {speed}");
                Assert.True(MathF.Abs(b.Phi) <= g.Limit + 1e-4f);
            }
        // the sway stays bounded at any tick rate
        var swayy = Geo(Cart(grip: 2f));
        foreach (var dt in new[] { 1f / 30f, 0.1f, 0.2f, 1f })
        {
            var b = Drive(swayy, dt, 30, 15f, 0.8f);
            Assert.True(float.IsFinite(b.Slide) && MathF.Abs(b.Slide) < 20f, $"dt {dt}: slide {b.Slide}");
        }
    }

    [Fact]
    public void The_hitch_behind_the_rear_axle_swings_the_trailer_when_the_tractor_turns_on_the_spot()
    {
        // a tractor turning in place at a standstill: the hitch is 2.4 m behind its origin, so it sweeps sideways and drags the trailer round
        var g = Geo(Cart(grip: 100f)); var t = new Path_ { Speed = 0, YawRate = 0.5f };
        var b = Hitched(g, t.Motion, 0f);
        for (var i = 0; i < 30; i++) { t.Move(Dt); TowModel.Step(ref b, g, t.Motion, Dt); }
        Assert.True(MathF.Abs(b.Phi) > 0.01f, "the trailer reacts although the tractor does not move forward");
        Assert.True(MathF.Sign(b.Phi) == -MathF.Sign(t.YawRate));
    }

    // ---- the machine ----

    [Fact]
    public void The_trailer_machine_is_in_the_catalog_and_has_no_undeclared_route()
    {
        var spec = StateMachineCatalog.All.Single(s => s.Name == "Trailer.Tow");
        Assert.Equal(new[] { "Loose", "Hitched", "Released" }, spec.StateNames.ToArray());
        Assert.Equal(new[] { ("Loose", "Hitched"), ("Hitched", "Released"), ("Released", "Loose") }, spec.TransitionNames.ToArray());
        Assert.Empty(spec.Unreachable);
        // a released trailer is never latched directly (Released -> Hitched is not an edge)
        Assert.False(TowLink.Spec.Allows(TrailerState.Released, TrailerState.Hitched));
    }
}
