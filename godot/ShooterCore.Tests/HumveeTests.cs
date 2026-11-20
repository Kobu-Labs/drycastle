using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The Humvee (vehicles.json humvee, scripts/make_humvee_model.py): its shipped entry against the numbers the model script measured (the model itself is private,
/// so the committed facts file stands for it: docs/research/scud-models-facts.d/humvee.json), its five seats and roof ring gun, its place between the jeep and the
/// flak truck, and that it drives on the drive model and tows.
/// </summary>
public class HumveeTests
{
    const float Dt = 1f / Wire.TickRate;
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Humvee => Shipped.Vehicles.Single(v => v.Id == "humvee");
    static VehicleDef Jeep => Shipped.Vehicles.Single(v => v.Id == "jeep");
    static VehicleDef Flak => Shipped.Vehicles.Single(v => v.Id == "flak");

    static JsonElement Facts()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "docs", "research", "scud-models-facts.d", "humvee.json"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "docs/research/scud-models-facts.d/humvee.json not found above the test directory");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "docs", "research", "scud-models-facts.d", "humvee.json"))).RootElement;
    }

    static Vector3 V(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.006f) => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    // ---- the type ----

    [Fact]
    public void It_is_a_five_seat_drive_model_car_with_a_ring_gun_dealt_two_at_random()
    {
        var h = Humvee;
        Assert.Equal("pack:synty-simple-military/humvee", h.Model);
        Assert.Equal(VehicleMedium.Land, h.Medium);
        Assert.NotNull(h.Drive);
        Assert.Equal(2, h.SpawnCount);
        Assert.Empty(h.FixedSpots);
        Assert.Equal(new[] { "driver", "passenger", "passenger", "passenger", "gunner" }, h.Seats.Select(s => s.Role));
        Assert.NotNull(h.Gun);
        Assert.NotNull(h.DoorGun);
        Assert.Null(h.Flak);
        Assert.False(h.ExposedGunner, "the gunner's seat says it itself");
    }

    [Fact]
    public void It_is_a_tougher_jeep_and_lighter_than_both_trucks()
    {
        var h = Humvee;
        Assert.True(h.Mass < Jeep.Mass && Jeep.Mass < Flak.Mass, "the jeep is the 7 m cargo truck now (#325): the Humvee is lighter than it, it lighter than the flak truck");
        Assert.InRange(h.Hp, Jeep.Hp, Flak.Hp);
        Assert.InRange(h.TopSpeed, Flak.TopSpeed, Jeep.TopSpeed);
        Assert.InRange(h.Acceleration, Flak.Acceleration, Jeep.Acceleration);
        Assert.InRange(h.TurnRadius, Jeep.TurnRadius, Flak.TurnRadius);
        Assert.InRange(h.RunOverMass!.Value, Jeep.RunOverMass!.Value, Flak.RunOverMass!.Value);
        Assert.Equal(28f, h.TopSpeed);
    }

    [Fact]
    public void Its_roof_gun_fires_the_vehicle_machine_gun_round_and_turns_all_the_way_round()
    {
        var g = Humvee.Gun!;
        Assert.Equal((VehicleGunRound.Damage, VehicleGunRound.FireInterval, VehicleGunRound.Range, VehicleGunRound.SpreadDegrees, VehicleGunRound.HumveeMagazine, VehicleGunRound.HumveeReserve, VehicleGunRound.HumveeReloadTime),
            (g.Damage, g.FireInterval, g.Range, g.SpreadDegrees, g.Magazine, g.Reserve, g.ReloadTime));
        var d = Humvee.DoorGun!;
        Assert.Equal(180f, d.YawArcDeg);
        Assert.Equal(0f, d.OutwardYawDeg);
        Assert.True(d.RingRadius > 0);
        Assert.Equal(0f, d.GuardArcDeg); // nobody in front of the ring is open to it: the passengers are under the hull
        // looking straight back and out of either side is inside the arc, for any heading
        foreach (var look in new[] { 0f, MathF.PI / 2, -MathF.PI / 2, MathF.PI * 0.99f })
        {
            var aim = DoorGunRules.Aim(d, 0.7f, 0, 0, 0.7f + look, 0.1f);
            Assert.Equal(HitMath.Forward(0.7f + look, 0.1f).X, aim.Direction.X, 3);
            Assert.Equal(HitMath.Forward(0.7f + look, 0.1f).Z, aim.Direction.Z, 3);
        }
    }

    [Fact]
    public void Only_the_gunner_is_exposed_and_standing_and_no_seat_is_armed()
    {
        var h = Humvee;
        Assert.Equal(new[] { false, false, false, false, true }, Enumerable.Range(0, h.Seats.Count).Select(h.SeatExposed));
        Assert.Equal(new[] { false, false, false, false, true }, h.Seats.Select(s => s.Standing));
        Assert.All(h.Seats, s => Assert.False(s.Armed || s.Aim != null));
        Assert.Equal(4, h.Seats.FindIndex(s => s.Role == "gunner"));
    }

    [Fact]
    public void A_shot_that_reaches_the_gunner_hits_him_and_not_the_passengers_under_the_hull()
    {
        var h = Humvee;
        var seats = new VehicleSeat(h.Seats.Count);
        for (ushort p = 1; p <= 5; p++) seats.TryEnter(p, p - 1);
        var targets = Enumerable.Range(1, 5).Select(p => new HitTarget((ushort)p, new Vector3(0, 1, p), Stance.Stand, 0)).ToList();
        var exposed = RiderRules.Exposed(h, seats, targets);
        Assert.Equal(new ushort[] { 5 }, exposed!.Riders.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void The_nearest_free_seat_is_taken_and_the_gunner_seat_is_reached_from_behind_the_cab()
    {
        var h = Humvee; var s = new VehicleSeat(5);
        Assert.Equal(0, s.NearestFreeSeat(h, Vector3.Zero, 0, new Vector3(-2.2f, 0, -0.3f), 4f));   // left front door: the driver
        s.TryEnter(1, 0);
        Assert.Equal(1, s.NearestFreeSeat(h, Vector3.Zero, 0, new Vector3(2.2f, 0, -0.3f), 4f));    // right front door
        Assert.Equal(2, s.NearestFreeSeat(h, Vector3.Zero, 0, new Vector3(-2.2f, 0, 0.6f), 4f));    // left rear door
        Assert.Equal(3, s.NearestFreeSeat(h, Vector3.Zero, 0, new Vector3(2.2f, 0, 0.6f), 4f));     // right rear door
        Assert.Equal(-1, s.NearestFreeSeat(h, Vector3.Zero, 0, new Vector3(30, 0, 0), 4f));
    }

    // ---- the model's numbers ----

    [Fact]
    public void Its_hit_box_wheels_hitch_lamps_and_glass_are_the_models()
    {
        var f = Facts(); var h = Humvee;
        var box = f.GetProperty("suggested");
        Assert.Equal(box.GetProperty("half_width").GetSingle(), h.HalfWidth, 3);
        Assert.Equal(box.GetProperty("half_length").GetSingle(), h.HalfLength, 3);
        Assert.Equal(box.GetProperty("height").GetSingle(), h.Height, 3);
        var wheels = f.GetProperty("wheels");
        Assert.Equal(wheels.GetProperty("wheelbase").GetSingle(), h.Drive!.Wheelbase, 3);
        Assert.Equal(wheels.GetProperty("track").GetSingle(), h.Drive.Track, 3);
        Assert.InRange(wheels.GetProperty("radius").GetSingle(), 0.4f, 0.45f);
        Near(V(f.GetProperty("tow_hitch")), V(h.Tow!.Hitch));
        Assert.True(h.Tow.Hitch[2] > h.HalfLength, "the hitch is behind the footprint");
        var lamps = f.GetProperty("lamps");
        Near(V(lamps.GetProperty("light_head_l")), V(h.Lights!.Head[0])); Near(V(lamps.GetProperty("light_head_r")), V(h.Lights.Head[1]));
        Near(V(lamps.GetProperty("light_tail_l")), V(h.Lights.Tail[0])); Near(V(lamps.GetProperty("light_tail_r")), V(h.Lights.Tail[1]));
        var glass = f.GetProperty("glass").EnumerateArray().ToList();
        Assert.Equal(6, h.Glass.Count);
        Assert.Equal(glass.Count, h.Glass.Count);
        for (var i = 0; i < glass.Count; i++)
        {
            Assert.Equal(glass[i].GetProperty("id").GetString(), h.Glass[i].Id);
            Near(V(glass[i].GetProperty("centre")), V(h.Glass[i].Centre));
            Near(V(glass[i].GetProperty("normal")), V(h.Glass[i].Normal));
            Assert.Equal(glass[i].GetProperty("outline").GetArrayLength(), h.Glass[i].Outline!.Length);
            Assert.NotNull(h.Glass[i].Outline);
        }
        Assert.All(h.GlassRects, r => Assert.True(GlassShards.Area(r.Outline!) <= r.Width * r.Height + 1e-3f));
        // every pane is inside the hit box (the server tests panes in that frame)
        foreach (var g in h.Glass) { Assert.InRange(MathF.Abs(g.Centre[0]), 0f, h.HalfWidth); Assert.InRange(MathF.Abs(g.Centre[2]), 0f, h.HalfLength); Assert.InRange(g.Centre[1], 0f, h.Height); }
    }

    [Fact]
    public void Its_seats_are_the_models_and_the_gunners_feet_put_his_standing_eye_at_the_gun_sight()
    {
        var f = Facts(); var h = Humvee; var seats = f.GetProperty("seats");
        var names = new[] { "seat_driver", "seat_passenger_1", "seat_passenger_2", "seat_passenger_3" };
        for (var i = 0; i < 4; i++) Near(V(seats.GetProperty(names[i])), V(h.Seats[i].Offset));
        Assert.True(h.Seats[0].Offset[0] < 0, "left-hand drive");
        var gunner = V(h.Seats[4].Offset); var model = V(seats.GetProperty("seat_gunner"));
        Assert.Equal(model.X, gunner.X, 3); Assert.Equal(model.Z, gunner.Z, 3);
        var sight = V(f.GetProperty("roof_gun").GetProperty("gunner_sight"));
        Assert.Equal(sight.Y, gunner.Y + MoveConsts.EyeHeight, 0.05f); // a standing figure's eye on the gun's sight
        foreach (var s in h.Seats) { Assert.InRange(MathF.Abs(s.Offset[0]), 0f, h.HalfWidth); Assert.InRange(MathF.Abs(s.Offset[2]), 0f, h.HalfLength); }
    }

    [Fact]
    public void The_ring_gun_puts_its_muzzle_and_the_gunners_eye_where_the_model_has_them()
    {
        var f = Facts(); var d = Humvee.DoorGun!; var gun = f.GetProperty("roof_gun");
        var aim = DoorGunRules.FromLocal(d, 0, 0, 0, 0, 0);
        var pivot = DoorGunRules.GunPivot(d, Vector3.Zero, 0, 0, 0, aim);
        Near(V(gun.GetProperty("gun_pivot")), pivot);
        Near(V(gun.GetProperty("muzzle")), DoorGunRules.Muzzle(d, pivot, aim));
        var eye = DoorGunRules.Eye(d, pivot, 0, 0, 0, aim);
        Near(V(gun.GetProperty("gunner_sight")), eye);
        Assert.True(eye.Y > Humvee.Height + 0.3f, "the gunner's eye is above the roof");
        // the ring's centre is the gunner's place: swung round to the rear the gun's pivot is on the other side of it
        var back = DoorGunRules.FromLocal(d, 0, 0, 0, MathF.PI, 0);
        Assert.Equal(d.Pivot[2] + d.RingRadius, DoorGunRules.GunPivot(d, Vector3.Zero, 0, 0, 0, back).Z, 3);
        // level, the barrel is over the roof in every direction
        for (var yaw = -MathF.PI; yaw <= MathF.PI; yaw += MathF.PI / 6)
        {
            var a = DoorGunRules.FromLocal(d, 0, 0, 0, yaw, 0);
            Assert.True(DoorGunRules.Muzzle(d, DoorGunRules.GunPivot(d, Vector3.Zero, 0, 0, 0, a), a).Y > Humvee.Height);
        }
    }

    [Fact]
    public void Every_rider_has_an_eye_inside_the_cabin_and_a_window_to_see_out_of()
    {
        var f = Facts(); var h = Humvee;
        // the cabin inside: floor 0.53, walls +-1.06, from the cowl at z -0.955 to the rear partition at 0.955, roof inside 1.83 (scripts/make_humvee_model.py)
        var eyes = new List<(int Seat, Vector3 Eye)>();
        var driver = V(f.GetProperty("cockpit_eye").GetProperty("seat_driver"));
        eyes.Add((0, driver));
        for (var i = 1; i < 4; i++)
        {
            var seat = h.Seats[i];
            Assert.NotNull(seat.EyeHeight);
            Assert.False(seat.Exposed);
            eyes.Add((i, V(seat.Offset) + new Vector3(0, seat.EyeHeight!.Value, 0)));
        }
        Near(V(f.GetProperty("cockpit_eye").GetProperty("seat_passenger_1")), eyes[1].Eye + new Vector3(0, 0, 0.09f), 0.01f); // the facts' eye is 0.09 further back: the camera stays on the seat's x and z
        foreach (var (seat, eye) in eyes)
        {
            Assert.InRange(eye.X, -1.06f, 1.06f); Assert.InRange(eye.Z, -0.955f, 0.955f); Assert.InRange(eye.Y, 0.53f, 1.83f);
            // a pane on its own side (the windscreen for the front seats) that the eye looks out through: the outward normal is within 78 degrees of the line from the eye
            var side = h.Seats[seat].Offset[0] < 0 ? -1f : 1f;
            var seen = h.Glass.Where(g => (MathF.Sign(g.Normal[0]) == side || g.Normal[2] < -0.9f && seat < 2) && Vector3.Dot(V(g.Normal), Vector3.Normalize(V(g.Centre) - eye)) > 0.2f).ToList();
            Assert.NotEmpty(seen);
            Assert.All(seen, g => Assert.True(V(g.Centre).Y < h.Height && V(g.Centre).Y > 1.2f));
        }
        // the standing eye (the default) would have been over the roof: that is why the seats name theirs
        Assert.True(h.Seats[1].Offset[1] + MoveConsts.EyeHeight > h.Height);
    }

    // ---- driving and towing ----

    sealed class Flat : IDriveWorld
    {
        public float GroundY(Vector3 from) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    [Fact]
    public void Full_throttle_reaches_its_top_speed_slower_than_the_jeep_and_it_turns_wider()
    {
        static (float Top, float At20) Run(VehicleDef d)
        {
            var s = new DriveState(); float? at20 = null;
            for (var t = 0f; t < 25f; t += Dt)
            {
                DriveModel.Step(ref s, d, new DriveInput { Throttle = 1 }, Dt, new Flat());
                at20 ??= s.Speed >= 20f ? t : null;
            }
            return (s.Speed, at20 ?? float.PositiveInfinity);
        }
        var h = Humvee;
        var (top, at20) = Run(h);
        Assert.Equal(h.TopSpeed, top, 1);
        Assert.InRange(at20, 3f, 9f);
        // the jeep (CarModel) gets to 20 m/s sooner
        var jeep = new CarState(); float? jeepAt20 = null;
        for (var t = 0f; t < 25f; t += Dt)
        {
            CarModel.Step(ref jeep, Jeep, 1, 0, false, Dt);
            jeepAt20 ??= jeep.Speed >= 20f ? t : null;
        }
        Assert.True(at20 > jeepAt20, $"the Humvee {at20:F1} s, the jeep {jeepAt20:F1} s");
        Assert.True(CarModel.TurnRadiusAt(h, 20) > CarModel.TurnRadiusAt(Jeep, 20));
    }

    [Fact]
    public void It_stops_on_the_brake_and_steers_at_speed()
    {
        var h = Humvee; var s = new DriveState { Speed = 25 };
        for (var i = 0; i < 40; i++) DriveModel.Step(ref s, h, new DriveInput { Throttle = 1, Steer = 1 }, Dt, new Flat());
        Assert.InRange(MathF.Abs(s.YawRate), 0.2f, 1.6f);
        s = new DriveState { Speed = 25 };
        var dist = 0f;
        for (var i = 0; i < 20 * Wire.TickRate && s.Speed > 0.2f; i++) { var before = s.Position; DriveModel.Step(ref s, h, new DriveInput { Throttle = -1 }, Dt, new Flat()); dist += Vector3.Distance(before, s.Position); }
        Assert.InRange(dist, 20f, 70f); // v^2 / 2a = 42 m at 7.5 m/s^2
    }

    [Fact]
    public void It_tows_and_its_load_slows_it_like_any_tractor()
    {
        var h = Humvee;
        Assert.NotNull(h.Tow);
        Assert.Null(h.Trailer);
        Assert.Null(h.Tow!.Validate());
        Assert.True(h.MaxLateralAccel > 0 && h.Tow.LateralScale * h.MaxLateralAccel < h.MaxLateralAccel);
    }

    // ---- validation of what it brought ----

    [Fact]
    public void A_seats_eye_height_is_validated()
    {
        VehiclesFile With(Action<VehicleDef> change) { var f = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json")); change(f.Vehicles.Single(v => v.Id == "humvee")); return f; }
        VehiclesFile.Validate(With(_ => { }));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[1].EyeHeight = 0.1f)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[1].EyeHeight = 3f)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[1].EyeHeight = float.NaN)));
    }
}
