using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Scud;

/// <summary>
/// The Scud truck's and the rocket cart's shipped entries against the numbers their model scripts measured (the models are private, so the committed facts files stand for them:
/// docs/research/scud-models-facts.d/scud.json and rocket_cart.json): hit box, seats and eyes, lamps, glass, wheels, the rack's erected missile, the cart's eye and axle, the rig's length with
/// every tractor, and the cart's loaded and empty mass.
/// </summary>
public class ScudModelTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static VehicleDef Truck => Shipped.Vehicles.Single(v => v.Id == "scud");
    static VehicleDef Cart => Shipped.Vehicles.Single(v => v.Id == "rocket_cart");
    static IEnumerable<VehicleDef> Tractors => Shipped.Vehicles.Where(v => v.Tow != null);

    static JsonElement Facts(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "docs", "research", "scud-models-facts.d", name + ".json"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, $"docs/research/scud-models-facts.d/{name}.json not found above the test directory");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "docs", "research", "scud-models-facts.d", name + ".json"))).RootElement;
    }

    static Vector3 V(JsonElement a) => new(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    static float Flat(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.006f) => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    // ---- the Scud truck ----

    [Fact]
    public void The_trucks_model_hit_box_seats_and_lamps_are_the_models()
    {
        var f = Facts("scud"); var t = Truck;
        Assert.Equal(f.GetProperty("model").GetString(), t.Model);
        Assert.Equal("pack:synty-simple-military/scud_missile", f.GetProperty("missile_model").GetString()); // what ScudFx.MissileModel loads
        var box = f.GetProperty("suggested");
        Assert.Equal(box.GetProperty("half_width").GetSingle(), t.HalfWidth, 3);
        Assert.Equal(box.GetProperty("half_length").GetSingle(), t.HalfLength, 3);
        Assert.Equal(box.GetProperty("height_with_lying_missile").GetSingle(), t.Height, 3);
        var seats = f.GetProperty("seats"); var eyes = f.GetProperty("cockpit_eye");
        Near(V(seats.GetProperty("seat_driver")), V(t.Seats[0].Offset)); Near(V(seats.GetProperty("seat_operator")), V(t.Seats[1].Offset));
        // the eye of each seat (the facts' is 0.09 further back than the seat's x and z: the camera stays on the seat's) is inside the half cab: roof inside 2.66, floor 1.15
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(1.05f, t.Seats[i].EyeHeight!.Value, 3);
            Assert.False(t.SeatExposed(i));
            var eye = V(t.Seats[i].Offset) + new Vector3(0, t.Seats[i].EyeHeight!.Value, 0);
            Near(V(eyes.GetProperty(i == 0 ? "seat_driver" : "seat_operator")), eye + new Vector3(0, 0, 0.09f), 0.01f);
            Assert.InRange(eye.Y, 1.15f, 2.66f);
        }
        var lamps = f.GetProperty("lamps");
        Near(V(lamps.GetProperty("light_head_l")), V(t.Lights!.Head[0])); Near(V(lamps.GetProperty("light_head_r")), V(t.Lights.Head[1]));
        Near(V(lamps.GetProperty("light_tail_l")), V(t.Lights.Tail[0])); Near(V(lamps.GetProperty("light_tail_r")), V(t.Lights.Tail[1]));
        Assert.False(t.Returns);
        Assert.Equal(1, t.Launcher!.OperatorSeat);
    }

    [Fact]
    public void The_eight_glass_panes_are_the_models_with_the_glass_nodes_offset_the_facts_leave_out()
    {
        var f = Facts("scud"); var t = Truck;
        var glass = f.GetProperty("glass").EnumerateArray().ToList();
        Assert.Equal(8, t.Glass.Count);
        Assert.Equal(glass.Count, t.Glass.Count);
        for (var i = 0; i < glass.Count; i++)
        {
            Assert.Equal(glass[i].GetProperty("id").GetString(), t.Glass[i].Id);
            // the facts give the panes as the script's meshes have them; the model puts every glass_N node 0.37 m towards +z (checked against the GLB), so the pane is that much further back in the vehicle frame
            Near(V(glass[i].GetProperty("centre")) + new Vector3(0, 0, 0.37f), V(t.Glass[i].Centre));
            Near(V(glass[i].GetProperty("normal")), V(t.Glass[i].Normal));
            Assert.Equal(glass[i].GetProperty("outline").GetArrayLength(), t.Glass[i].Outline!.Length);
        }
        foreach (var g in t.Glass) { Assert.InRange(MathF.Abs(g.Centre[0]), 0f, t.HalfWidth); Assert.InRange(MathF.Abs(g.Centre[2]), 0f, t.HalfLength); Assert.InRange(g.Centre[1], 0f, t.Height); }
    }

    [Fact]
    public void The_wheels_are_eight_and_the_front_two_axles_steer_the_way_the_facts_say()
    {
        var f = Facts("scud"); var t = Truck.Launcher!;
        var w = f.GetProperty("wheels");
        Assert.Equal(w.GetProperty("radius").GetSingle(), t.WheelRadius, 3);
        var axles = w.GetProperty("axle_z"); var fixedMid = (axles.GetProperty("axle3").GetSingle() + axles.GetProperty("axle4").GetSingle()) / 2f;
        Assert.Equal(2, t.SteerArms.Length);
        Assert.Equal(fixedMid - axles.GetProperty("axle1").GetSingle(), t.SteerArms[0], 2);
        Assert.Equal(fixedMid - axles.GetProperty("axle2").GetSingle(), t.SteerArms[1], 2);
        Assert.Equal(8, w.GetProperty("centres").EnumerateObject().Count());
        // the front axle turns more than the second, both to the same side; reversing the curvature mirrors them; the angle is held to a limit
        var front = ScudRules.WheelAngle(t.SteerArms[0], 0.1f); var second = ScudRules.WheelAngle(t.SteerArms[1], 0.1f);
        Assert.True(front > second && second > 0);
        Assert.Equal(-front, ScudRules.WheelAngle(t.SteerArms[0], -0.1f), 5);
        Assert.Equal(0f, ScudRules.WheelAngle(t.SteerArms[0], 0f));
        Assert.Equal(0.6f, ScudRules.WheelAngle(t.SteerArms[0], 100f), 5);
        // the tightest turn the truck's own radius asks (vehicles.json turn_radius) is within what the wheels turn
        Assert.True(ScudRules.WheelAngle(t.SteerArms[0], 1f / Truck.TurnRadius) < 0.6f);
    }

    [Fact]
    public void The_missile_leaves_from_where_the_erected_rocket_stands()
    {
        var f = Facts("scud"); var rack = f.GetProperty("rack");
        var pivot = V(rack.GetProperty("pivot_hinge")); var lying = V(rack.GetProperty("missile_origin_com"));
        // the missile node's centre relative to the rack's pivot, turned 90 degrees up about +X (positive lifts a rack that lies forward, -Z): y' = y cos - z sin, z' = y sin + z cos
        var rel = lying - pivot;
        var erected = pivot + new Vector3(rel.X, -rel.Z, rel.Y);
        Near(erected, V(Truck.Launcher!.Muzzle), 0.01f);
        // its tail end stands 4.807 m under the centre (above the ground) and the nose 6.318 m over it
        var tail = erected.Y - rack.GetProperty("missile_tail_from_origin").GetSingle();
        Near(new Vector3(erected.X, tail, erected.Z), V(Truck.Launcher.Exhaust), 0.05f);
        Assert.InRange(erected.Y + rack.GetProperty("missile_nose_from_origin").GetSingle(), 12.1f, 12.3f);
    }

    // ---- the rocket cart ----

    [Fact]
    public void The_carts_box_eye_axle_and_wheels_are_the_models()
    {
        var f = Facts("rocket_cart"); var c = Cart;
        var box = f.GetProperty("suggested");
        Assert.Equal(box.GetProperty("half_width").GetSingle(), c.HalfWidth, 3);
        Assert.Equal(box.GetProperty("half_length").GetSingle(), c.HalfLength, 3);
        Assert.Equal(box.GetProperty("height").GetSingle(), c.Height, 3);
        Near(V(f.GetProperty("tow_eye")), V(c.Trailer!.Eye));
        Near(V(f.GetProperty("axle")), V(c.Trailer.Axle));
        Assert.Equal(f.GetProperty("drawbar_length_axle_to_eye").GetSingle(), c.Trailer.Drawbar, 2);
        Assert.Equal(f.GetProperty("wheels").GetProperty("radius").GetSingle(), c.Trailer.Axle[1], 3); // the wheels' centre height is the radius (car-design section 12b)
        Assert.Equal(f.GetProperty("model").GetString(), c.Model);
        Assert.True(c.Trailer.Eye[2] >= -c.HalfLength && c.Trailer.Eye[2] < -c.HalfLength + 0.5f, "the eye is the drawbar's tip, at the front of the box");
    }

    [Fact]
    public void Every_tractors_hitch_meets_the_eye_or_the_drawn_cart_pitches_by_less_than_two_degrees()
    {
        foreach (var tractor in Tractors)
        {
            var g = TowGeometry.Of(tractor, Cart);
            Assert.True(MathF.Abs(g.HitchY - g.EyeY) <= 0.13f, $"{tractor.Id}: hitch {g.HitchY} against the eye {g.EyeY}");
            var pitch = TowModel.PitchFor(g, g.HitchY); // level ground under the axle: the drawn pitch
            Assert.True(MathF.Abs(pitch) < 2f * MathF.PI / 180f, $"{tractor.Id}: the drawn cart pitches {pitch * 180 / MathF.PI:F2} degrees");
            // the cart's front edge is at the tractor's back (not inside it) when in line
            var b = TowModel.Pose(g, Vector3.Zero, 0f, 0f);
            Assert.True(b.Position.Z - Cart.HalfLength >= tractor.HalfLength - 1e-3f, $"{tractor.Id}: the cart starts inside it");
        }
        Assert.Contains(Tractors, t => t.Id == "humvee");
    }

    [Fact]
    public void A_seven_and_a_half_metre_drawbar_follows_a_turn_and_the_jackknife_limit_holds()
    {
        foreach (var tractor in Tractors)
        {
            var g = TowGeometry.Of(tractor, Cart);
            Assert.Equal(7.6f, g.Drawbar, 2);
            var tm = new TractorMotion(Vector3.Zero, 0f, 8f, 0f, 0.3f);
            var b = TowModel.Pose(g, tm.Position, tm.Yaw, 0f);
            for (var i = 0; i < 400; i++)
            {
                tm = tm with { Position = tm.Position + new Vector3(-MathF.Sin(tm.Yaw), 0, -MathF.Cos(tm.Yaw)) * (8f / 30f), Yaw = tm.Yaw + 0.3f / 30f };
                TowModel.Step(ref b, g, tm, 1f / 30f);
                Assert.True(MathF.Abs(b.Phi) <= g.Limit + 1e-4f, $"{tractor.Id}: the trailer passed the limit");
                // the eye stays on the hitch
                Assert.True(Flat(TowModel.HitchPoint(g, tm.Position, tm.Yaw), TowModel.EyePoint(g, b)) < 0.01f);
            }
            Assert.True(MathF.Abs(b.Phi) > 0.2f, $"{tractor.Id}: a steady turn must lay the long cart over, not leave it in line");
        }
    }

    [Fact]
    public void A_loaded_cart_weighs_more_than_an_empty_one_and_pulls_harder_on_the_tractor()
    {
        var tr = Cart.Trailer!;
        Assert.True(tr.EmptyMass > 0 && tr.EmptyMass < tr.Mass);
        Assert.Equal(tr.Mass, tr.MassFor(true)); Assert.Equal(tr.EmptyMass, tr.MassFor(false));
        foreach (var tractor in Tractors)
        {
            var loaded = TowLoad.Of(tractor, Cart); var empty = TowLoad.Of(tractor, Cart, loaded: false);
            Assert.Equal(loaded.AccelScale, TowLoad.Of(tractor, Cart, true).AccelScale);
            Assert.True(empty.AccelScale > loaded.AccelScale, $"{tractor.Id}: an empty cart is easier to pull");
            Assert.True(empty.BrakeScale > loaded.BrakeScale);
            Assert.Equal(tractor.Mass / (tractor.Mass + tr.Mass), loaded.AccelScale, 5);
        }
        // a trailer with no empty mass set weighs the same either way
        var same = new VehicleTrailerDef { Mass = 1200 };
        Assert.Equal(1200, same.MassFor(false));
        Assert.NotNull(new VehicleTrailerDef { Mass = 1200, EmptyMass = 1300 }.Validate());
        Assert.Null(new VehicleTrailerDef { Mass = 1200, EmptyMass = 900 }.Validate());
    }

    [Fact]
    public void An_emptied_cart_stays_empty_in_the_machine_until_it_is_loaded_again()
    {
        var c = new CartCargo();
        Assert.True(c.Set(false));
        Assert.False(c.Loaded);
        Assert.Equal(Cart.Hp, c.MaxHp(Cart));
        Assert.True(c.Set(true));
        Assert.Equal(Cart.Trailer!.LoadedHp, c.MaxHp(Cart));
    }
}
