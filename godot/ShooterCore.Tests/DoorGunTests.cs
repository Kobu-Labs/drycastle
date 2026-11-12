using System.Numerics;
using System.Text.Json;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// The utility helicopter's door gun: the aim held inside its arc in the airframe's own frame, the arc clear of the helicopter's own hull,
/// skids, tail and rotor, the muzzle and the gunner's eye, and the shipped numbers agreeing with the generated model.
/// </summary>
public class DoorGunTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Heli => Shipped.Vehicles.Single(v => v.Id == "utility_helicopter");
    static VehicleDoorGunDef Gun => Heli.DoorGun!;
    const float Deg = MathF.PI / 180f;

    /// <summary>A view yaw that looks straight out of the right door of a helicopter with the given heading.</summary>
    static float Out(float carYaw = 0) => carYaw + Gun.OutwardYawDeg * Deg;

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    // ---- data ----

    [Fact]
    public void The_shipped_helicopter_has_a_pilot_and_a_door_gunner_and_fires_by_the_gun_rules()
    {
        var v = Heli;
        Assert.Equal(new[] { "driver", "gunner" }, v.Seats.Select(s => s.Role));
        Assert.NotNull(v.Gun);
        Assert.NotNull(v.DoorGun);
        Assert.Null(v.Flak);
        Assert.True(v.ExposedGunner, "the door gunner sits in the open door");
        var jeep = Shipped.Vehicles.Single(x => x.Id == "jeep").Gun!;
        Assert.Equal(jeep.Damage, v.Gun!.Damage);                 // the jeep's round
        Assert.Equal(jeep.FireInterval, v.Gun.FireInterval);
        Assert.True(v.Gun.Magazine >= jeep.Magazine);
        // the gunner sits on the gun's side, inside the hull, behind the pivot
        var seat = v.Seats[1].Offset;
        Assert.True(seat[0] > 0 && seat[0] < Gun.Pivot[0] && MathF.Abs(seat[0]) < v.HalfWidth);
    }

    [Fact]
    public void A_door_gun_needs_the_gun_rules_and_a_sane_arc()
    {
        static VehiclesFile With(Action<VehicleDef> change)
        {
            var f = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
            change(f.Vehicles.Single(v => v.Id == "utility_helicopter"));
            return f;
        }
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Gun = null)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.DoorGun!.MinPitchDeg = 20)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.DoorGun!.YawArcDeg = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.DoorGun!.Pivot = new[] { 1f, 2f })));
        VehiclesFile.Validate(With(_ => { }));
    }

    // ---- the aim ----

    [Fact]
    public void Looking_straight_out_of_the_door_is_the_guns_rest_direction()
    {
        var aim = DoorGunRules.Aim(Gun, 0, 0, 0, Out(), 0);
        Assert.Equal(0f, aim.Yaw, 4);
        Assert.Equal(0f, aim.Pitch, 4);
        Near(new Vector3(1, 0, 0), aim.Direction);                     // the right side of a helicopter facing -Z
        // with the helicopter turned, out of the door turns with it
        var turned = DoorGunRules.Aim(Gun, 1.2f, 0, 0, Out(1.2f), 0);
        Assert.Equal(0f, turned.Yaw, 4);
        Near(HitMath.Forward(Out(1.2f), 0), turned.Direction);
    }

    [Theory]
    [InlineData(25f, -10f)]
    [InlineData(-60f, 9f)]
    [InlineData(0f, -50f)]
    [InlineData(69f, 11f)]
    public void Inside_the_arc_the_gun_points_exactly_where_the_gunner_looks_whatever_the_airframe_does(float yawDeg, float pitchDeg)
    {
        // the view is world-stable: the helicopter banking and pitching under the gunner does not move the aim
        foreach (var (carYaw, carPitch, carRoll) in new[] { (0f, 0f, 0f), (2.1f, 0f, 0f), (0.4f, -0.3f, 0.25f), (-1f, 0.2f, -0.4f) })
        {
            var wanted = DoorGunRules.FromLocal(Gun, carYaw, carPitch, carRoll, yawDeg * Deg, pitchDeg * Deg);
            var aim = DoorGunRules.Aim(Gun, carYaw, carPitch, carRoll, wanted.ViewYaw, wanted.ViewPitch);
            Near(wanted.Direction, aim.Direction);
            Assert.Equal(yawDeg * Deg, aim.Yaw, 3);
            Assert.Equal(pitchDeg * Deg, aim.Pitch, 3);
            Near(HitMath.Forward(aim.ViewYaw, aim.ViewPitch), aim.Direction);   // the camera's yaw and pitch are that direction
        }
    }

    [Fact]
    public void Outside_the_arc_the_gun_stops_at_its_edge()
    {
        float arc = Gun.YawArcDeg * Deg, up = Gun.MaxPitchDeg * Deg, down = Gun.MinPitchDeg * Deg;
        Assert.Equal(arc, DoorGunRules.Aim(Gun, 0, 0, 0, 0, 0).Yaw, 4);                       // towards the nose
        Assert.Equal(-arc, DoorGunRules.Aim(Gun, 0, 0, 0, MathF.PI, 0).Yaw, 4);               // towards the tail
        Assert.Equal(arc, MathF.Abs(DoorGunRules.Aim(Gun, 0, 0, 0, Out() + MathF.PI, 0).Yaw), 4);   // through the cabin, out of the other door: one edge or the other
        Assert.Equal(up, DoorGunRules.Aim(Gun, 0, 0, 0, Out(), 1.2f).Pitch, 4);               // at the rotor
        Assert.Equal(down, DoorGunRules.Aim(Gun, 0, 0, 0, Out(), -1.4f).Pitch, 4);            // at the skid
        // the arc tilts with the airframe: banked right side down by 30 degrees, a level look is 30 above the gun's own level, beyond its stop
        var banked = DoorGunRules.Aim(Gun, 0, 0, -30 * Deg, Out(), 0);
        Assert.Equal(up, banked.Pitch, 3);
        Assert.True(banked.Direction.Y < -0.2f, "the gun cannot be brought level: it points below the horizon");
    }

    [Fact]
    public void The_arc_keeps_the_rounds_off_the_helicopters_own_hull_skids_tail_and_rotor()
    {
        // the airframe as boxes in the vehicle's frame (x right, y up, z back), from the model script's dimensions, and the rotor as the
        // full disc the blades sweep (6.7 m, not the smaller collision disc): no round from any aim in the arc passes through any of them
        var v = Heli;
        const float bladeRadius = 6.7f, bladeMargin = 0.35f;
        var rotorY = v.Flight!.RotorHeight;
        var boxes = new (string Name, Vector3 Min, Vector3 Max)[]
        {
            ("hull", new Vector3(-1.25f, 0.5f, -3.8f), new Vector3(1.25f, 2.95f, 3.2f)),
            ("skids", new Vector3(-1.15f, 0f, -2.5f), new Vector3(1.15f, 0.45f, 2.0f)),
            ("tail boom and fin", new Vector3(-0.4f, 1.4f, 3.0f), new Vector3(0.4f, 3.7f, 8.9f)),
            ("stabiliser", new Vector3(-1.2f, 1.85f, 5.9f), new Vector3(1.2f, 2.0f, 6.6f)),
            ("tail rotor", new Vector3(-0.35f, 1.8f, 7.1f), new Vector3(-0.1f, 4.4f, 9.8f)),
        };
        var pivot = new Vector3(Gun.Pivot[0], Gun.Pivot[1], Gun.Pivot[2]);
        for (var yaw = -Gun.YawArcDeg; yaw <= Gun.YawArcDeg; yaw += 2.5f)
        for (var pitch = Gun.MinPitchDeg; pitch <= Gun.MaxPitchDeg; pitch += 2.5f)
        {
            var aim = DoorGunRules.FromLocal(Gun, 0, 0, 0, yaw * Deg, pitch * Deg);
            var muzzle = DoorGunRules.Muzzle(Gun, pivot, aim);
            for (var d = 0f; d <= 40f; d += 0.05f)
            {
                var p = muzzle + aim.Direction * d;
                foreach (var (name, min, max) in boxes)
                    Assert.False(p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z,
                        $"aim yaw {yaw} pitch {pitch} passes through the {name} at {p}");
                var inDisc = MathF.Sqrt(p.X * p.X + p.Z * p.Z) <= bladeRadius;
                Assert.False(inDisc && MathF.Abs(p.Y - rotorY) <= bladeMargin, $"aim yaw {yaw} pitch {pitch} passes through the rotor at {p}");
            }
        }
    }

    [Fact]
    public void The_muzzle_is_a_barrel_length_from_the_pivot_and_always_outside_the_hit_volume()
    {
        var v = Heli;
        var s = new FlightState { Position = new Vector3(40, 90, -20), Yaw = 0.8f, Pitch = -0.25f, Roll = 0.3f };
        var pivot = DoorGunRules.Pivot(Gun, s.Position, s.Yaw, s.Pitch, s.Roll);
        Near(s.ToWorld(Gun.Pivot), pivot);                                        // the pivot rides the airframe like a seat does
        for (var yaw = -Gun.YawArcDeg; yaw <= Gun.YawArcDeg; yaw += 10)
        {
            var aim = DoorGunRules.FromLocal(Gun, s.Yaw, s.Pitch, s.Roll, yaw * Deg, 0);
            var muzzle = DoorGunRules.Muzzle(Gun, pivot, aim);
            Assert.Equal(Gun.MuzzleLength, Vector3.Distance(pivot, muzzle), 4);
            var local = FlightMath.Unrotate(muzzle - s.Position, s.Yaw, s.Pitch, s.Roll);
            Assert.True(local.X > v.HalfWidth, $"at yaw {yaw} the muzzle is inside the hull's width ({local.X})");
        }
    }

    [Fact]
    public void The_gunners_eye_is_behind_and_above_the_gun_and_swings_round_the_pintle_with_it()
    {
        var pivot = new Vector3(Gun.Pivot[0], Gun.Pivot[1], Gun.Pivot[2]);
        var rest = DoorGunRules.Eye(Gun, pivot, 0, 0, 0, DoorGunRules.FromLocal(Gun, 0, 0, 0, 0, 0));
        Near(pivot + new Vector3(-Gun.Eye[2], Gun.Eye[1], 0), rest);              // straight inboard of the pivot, and above it
        var aim = DoorGunRules.FromLocal(Gun, 0, 0, 0, 40 * Deg, -20 * Deg);
        var eye = DoorGunRules.Eye(Gun, pivot, 0, 0, 0, aim);
        // the eye stays on the gun: the same distance from the pivot, and the muzzle is dead ahead of it but for the height over the bore
        Assert.Equal(Vector3.Distance(pivot, rest), Vector3.Distance(pivot, eye), 4);
        var toMuzzle = Vector3.Normalize(DoorGunRules.Muzzle(Gun, pivot, aim) - eye);
        Assert.True(Vector3.Dot(toMuzzle, aim.Direction) > 0.98f);
        Assert.True(MathF.Abs(eye.X) < Heli.HalfWidth, "the gunner's head stays inside the cabin");
    }

    // ---- the record ----

    [Fact]
    public void The_aim_travels_as_a_turret_record_with_the_gunners_id()
    {
        var aim = DoorGunRules.Aim(Gun, 0.3f, 0, 0, Out(0.3f) + 0.5f, -0.4f);
        var r = FlakRecord.Make(VehicleRecord.FirstId + 9, 513, aim.Yaw, aim.Pitch);
        Assert.Equal(EntityType.Turret, r.Type);
        Assert.Equal((ushort)513, FlakRecord.Gunner(r));
        Assert.Equal((ushort)(VehicleRecord.FirstId + 9), FlakRecord.CarOf(r.Id));
        // a client poses the model from the record: the same direction to within the wire's quantisation
        var posed = DoorGunRules.FromLocal(Gun, 0.3f, 0, 0, FlightMath.Wrap(r.YawRad), r.PitchRad);
        Near(aim.Direction, posed.Direction, 2e-3f);
        // a yaw to the tail side (negative) survives the unsigned yaw field
        var back = FlakRecord.Make(VehicleRecord.FirstId, 1, -0.9f, 0.1f);
        Assert.Equal(-0.9f, FlightMath.Wrap(back.YawRad), 3);
    }

    // ---- the model ----

    static string AssetsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "assets");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new InvalidOperationException("assets/manifest.json not found above the test output folder");
    }

    /// <summary>The nodes of the generated model by name, each with its translation in the model's own frame (accumulated down the tree) and its children.</summary>
    static Dictionary<string, (Vector3 At, float Yaw)> ModelNodes()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AssetsDir(), Heli.Model));
        var jsonLength = BitConverter.ToInt32(bytes, 12);
        using var doc = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var nodes = doc.RootElement.GetProperty("nodes");
        var found = new Dictionary<string, (Vector3, float)>();
        void Walk(int index, Vector3 parentAt, float parentYaw)
        {
            var n = nodes[index];
            var t = n.TryGetProperty("translation", out var tr) ? new Vector3(tr[0].GetSingle(), tr[1].GetSingle(), tr[2].GetSingle()) : Vector3.Zero;
            var yaw = n.TryGetProperty("rotation", out var q) ? 2f * MathF.Atan2(q[1].GetSingle(), q[3].GetSingle()) : 0f;
            var at = parentAt + FlightMath.Rotate(t, parentYaw, 0, 0);
            found[n.GetProperty("name").GetString()!] = (at, parentYaw + yaw);
            if (n.TryGetProperty("children", out var kids)) foreach (var k in kids.EnumerateArray()) Walk(k.GetInt32(), at, parentYaw + yaw);
        }
        Walk(doc.RootElement.GetProperty("scenes")[0].GetProperty("nodes")[0].GetInt32(), Vector3.Zero, 0);
        return found;
    }

    [Fact]
    public void The_generated_model_has_every_moving_part_and_marker_as_its_own_node()
    {
        var nodes = ModelNodes();
        foreach (var name in new[] { "fuselage", "rotor_main", "rotor_tail", "gun_mount", "gun", "barrel", "muzzle", "gunner_sight", "seat_pilot", "seat_gunner",
                                     "light_head", "light_tail", "light_nav_l", "light_nav_r", "smoke_engine" })
            Assert.True(nodes.ContainsKey(name), $"the model has no node {name}");
    }

    [Fact]
    public void Vehicles_json_agrees_with_the_generated_model()
    {
        var nodes = ModelNodes(); var v = Heli;
        static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
        Near(nodes["gun"].At, V(Gun.Pivot), 0.01f);                                              // the pitch pivot
        Assert.Equal(nodes["gun_mount"].At.X, Gun.Pivot[0], 2);                                  // the yaw axis runs through it
        Assert.Equal(nodes["gun_mount"].At.Z, Gun.Pivot[2], 2);
        Assert.Equal(Gun.OutwardYawDeg * Deg, nodes["gun_mount"].Yaw, 3);                        // the mount's own -Z is the rest direction
        Assert.Equal(Gun.MuzzleLength, Vector3.Distance(nodes["gun"].At, nodes["muzzle"].At), 2);
        var rest = DoorGunRules.FromLocal(Gun, 0, 0, 0, 0, 0);
        Near(nodes["muzzle"].At, DoorGunRules.Muzzle(Gun, V(Gun.Pivot), rest), 0.01f);
        Near(nodes["gunner_sight"].At, DoorGunRules.Eye(Gun, V(Gun.Pivot), 0, 0, 0, rest), 0.01f);
        Near(nodes["seat_pilot"].At, V(v.Seats[0].Offset), 0.01f);
        Near(nodes["seat_gunner"].At, V(v.Seats[1].Offset), 0.01f);
        Near(nodes["smoke_engine"].At, V(v.Hood), 0.01f);
        Near(nodes["light_head"].At, V(v.Lights!.Head[0]), 0.01f);
        Near(nodes["light_tail"].At, V(v.Lights.Tail[0]), 0.01f);
        Assert.Equal(nodes["rotor_main"].At.Y, v.Flight!.RotorHeight, 2);                         // the collision disc is at the hub's height
        Assert.True(v.Flight.RotorRadius < 6.7f, "the collision disc is inside the blades");
    }
}
