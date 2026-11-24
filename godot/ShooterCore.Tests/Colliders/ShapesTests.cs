using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Colliders;

/// <summary>ADR 0028, slice 4: two shapes per entity. The Hit shape is what a weapon is tested against, the Body shape what contact is.</summary>
public class HitCapsuleTests
{
    [Fact]
    public void The_players_hit_capsule_is_the_movement_capsule_and_the_head_sphere_is_kept()
    {
        Assert.Equal(MoveConsts.CapsuleRadius, PlayerHitBox.Radius);
        Assert.Equal(0.35f, PlayerHitBox.Radius, 4);
        Assert.Equal(0.25f, PlayerHitBox.HeadRadius, 4);
        Assert.Equal(PlayerHitBox.Radius, HitMath.BodyRadius(new HitTarget(1, Vector3.Zero, Stance.Stand, 0)), 4);
    }

    [Fact]
    public void A_citizen_a_gang_member_and_the_demon_follow_the_player_capsule_scaled_as_they_are()
    {
        // a citizen is a plain target (scale 1); a gang member and the demon grow the same capsule by their scale
        Assert.Equal(0.35f, HitMath.BodyRadius(new HitTarget(1, Vector3.Zero, Stance.Stand, 0)), 4);
        Assert.Equal(0.35f * 1.15f, HitMath.BodyRadius(new HitTarget(1, Vector3.Zero, Stance.Stand, 0, Scale: 1.15f)), 4);
        var demon = DemonRules.Target(Vector3.Zero, Vector3.UnitX, 1.8f, 0f, 1.6f);
        Assert.Equal(0.35f * 1.6f, HitMath.BodyRadius(demon), 4);
    }

    [Fact]
    public void A_prone_player_rests_the_narrower_capsule_on_the_ground()
    {
        var (a, b) = HitMath.Axis(new HitTarget(1, Vector3.Zero, Stance.Prone, 0));
        Assert.Equal(PlayerHitBox.Radius, a.Y, 4);
        Assert.Equal(PlayerHitBox.Radius, b.Y, 4);
    }

    [Fact]
    public void The_Bigfoot_and_the_Raid_van_keep_the_old_wide_capsule()
    {
        var giant = new HitTarget(1, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Scale: 2f, BaseRadius: PlayerHitBox.WideRadius);
        Assert.Equal(1.12f, HitMath.BodyRadius(giant), 4);
        var van = new HitTarget(1, Vector3.Zero, Stance.Prone, 0, HeadGuarded: true, Scale: 2.1f, BaseRadius: PlayerHitBox.WideRadius);
        var (a, b) = HitMath.Axis(van);
        Assert.Equal(0.56f * 2.1f, a.Y, 4); // its capsules rest on the ground by their own radius, as before
        Assert.Equal(0.56f * 2.1f, b.Y, 4);
        Assert.Equal(0.56f * 2.1f, HitMath.BodyRadius(van), 4);
    }
}

public class AnimalBlastAndKnifeTests
{
    // a 20 x 10 grid of 5 cm cells: from 0.5 m behind the position to 0.5 m ahead, 0.5 m high; only the middle 12 columns x 6 rows are covered, 20 cm half width
    static AnimalShape Shape()
    {
        var cells = new byte[20 * 10];
        for (var r = 0; r < 6; r++)
            for (var c = 4; c < 16; c++) cells[r * 20 + c] = 20;
        return new AnimalShape(0.05f, -0.5f, 20, 10, cells);
    }

    static HitTarget WithShape() => new(100, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Scale: 1f, Box: new Vector3(0.6f, 1.2f, 1.6f), BoxAhead: 0.2f, Shape: Shape());
    static HitTarget BoxOnly() => new(100, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Scale: 1.2f / 1.8f, Box: new Vector3(0.6f, 1.2f, 1.6f), BoxAhead: 0.2f);

    [Fact]
    public void A_shape_knows_the_bounding_box_of_what_it_covers()
    {
        var b = Shape().Bounds;
        Assert.Equal(-0.3f, b.Z0, 4);   // column 4 starts 0.2 m after the grid's start
        Assert.Equal(0.3f, b.Z1, 4);    // column 15 ends at 0.3 m
        Assert.Equal(0.3f, b.Top, 4);   // 6 rows
        Assert.Equal(0.2f, b.HalfWidth, 4);
    }

    [Fact]
    public void A_blast_measures_to_the_outlines_bounding_box()
    {
        var t = WithShape();
        Assert.True(HitMath.AnimalBounds(t, out _, out _));
        Assert.Equal(0f, HitMath.DistanceToBody(new Vector3(0, 0.1f, 0), t), 4);      // inside
        Assert.Equal(0.5f, HitMath.DistanceToBody(new Vector3(0, 0.1f, -0.8f), t), 4);  // ahead (-Z): the front is 0.3 m ahead
        Assert.Equal(0.3f, HitMath.DistanceToBody(new Vector3(0.5f, 0.1f, 0), t), 4);   // beside: 0.2 m half width
        Assert.Equal(0.7f, HitMath.DistanceToBody(new Vector3(0, 1.0f, 0), t), 4);      // over it: the top is 0.3 m, not the 1.2 m box
        Assert.Equal(0.5f, HitMath.DistanceToBody(new Vector3(0, 0.1f, 0.8f), t), 4);   // behind
    }

    [Fact]
    public void A_species_with_no_outline_is_measured_to_its_plain_box()
    {
        var t = BoxOnly();
        Assert.True(HitMath.AnimalBounds(t, out var lo, out var hi));
        Assert.Equal(new Vector3(-0.3f, 0, -0.6f), lo);
        Assert.Equal(new Vector3(0.3f, 1.2f, 1.0f), hi);
        Assert.Equal(0f, HitMath.DistanceToBody(new Vector3(0, 1.0f, 0), t), 4);        // inside the 1.2 m box
        Assert.Equal(1.0f, HitMath.DistanceToBody(new Vector3(0, 0.5f, -2.0f), t), 4);  // the front is 1.0 m ahead
        Assert.Equal(0.4f, HitMath.DistanceToBody(new Vector3(0, 0.5f, 1.0f), t), 4);   // behind: the box ends 0.6 m behind
    }

    [Fact]
    public void A_target_that_is_no_animal_is_still_a_capsule()
    {
        var t = new HitTarget(1, Vector3.Zero, Stance.Stand, 0);
        Assert.False(HitMath.AnimalBounds(t, out _, out _));
        Assert.Equal(0.3f, HitMath.DistanceToBody(new Vector3(PlayerHitBox.Radius + 0.3f, 1f, 0), t), 3);
    }

    [Fact]
    public void A_knife_reaches_the_outlines_bounding_box_not_a_scaled_human()
    {
        // the animal at the origin faces -Z, its front 0.3 m ahead; the attacker faces +Z from z = -2.1, so it is 1.8 m from that front
        var knife = 1.8f;
        var t = WithShape();
        Assert.NotNull(HitMath.Melee(new Vector3(0, 0, -2.1f), MathF.PI, knife, new[] { t }, 1));
        Assert.Null(HitMath.Melee(new Vector3(0, 0, -2.2f), MathF.PI, knife, new[] { t }, 1));
        var hit = HitMath.Melee(new Vector3(0, 0, -2.0f), MathF.PI, knife, new[] { t }, 1)!.Value;
        Assert.Equal(1.7f, hit.Distance, 3);
        // no outline: the plain box, whose front is 1.0 m ahead (the scaled human capsule was 1.5 m out at most)
        Assert.NotNull(HitMath.Melee(new Vector3(0, 0, -2.7f), MathF.PI, knife, new[] { BoxOnly() }, 1));
        Assert.Null(HitMath.Melee(new Vector3(0, 0, -2.9f), MathF.PI, knife, new[] { BoxOnly() }, 1));
        // from behind the animal, it is out of the 120 degree cone (the animal's own facing does not matter, only the attacker's)
        Assert.Null(HitMath.Melee(new Vector3(0, 0, -2.0f), 0f, knife, new[] { t }, 1));
    }

    [Fact]
    public void A_knife_inside_the_animals_footprint_always_reaches()
    {
        Assert.NotNull(HitMath.Melee(new Vector3(0, 0, 0.1f), 0f, 1.8f, new[] { WithShape() }, 1));
    }

    [Fact]
    public void The_shipped_outlines_cover_every_free_species()
    {
        var shapes = AnimalShapes.Load(Path.Combine(AppContext.BaseDirectory, "data", "animal_shapes.json"));
        foreach (var id in new[] { "deer", "stag", "wolf", "fox", "alpaca", "bull", "cow", "donkey", "horse", "white_horse", "husky", "shiba_inu" })
        {
            foreach (var pose in Enum.GetValues<AnimalPose>())
                Assert.NotNull(shapes.Of(id, pose));
            var b = shapes.Of(id, AnimalPose.Idle)!.Frames[0].Bounds;
            Assert.True(b.Top > 0.3f && b.HalfWidth > 0.05f && b.Z1 - b.Z0 > 0.5f, id);
        }
    }
}

public class RunOverFootprintTests
{
    // a car facing -Z with half width 1.1 and half length 2.15, standing still at the origin
    static bool Hits(RunOverFootprint f) => f.Hits(Vector3.Zero, Vector3.Zero, 0f, 1.1f, 2.15f);

    [Fact]
    public void A_body_is_the_capsules_circle_at_95_percent()
    {
        Assert.Equal(0.35f * 0.95f, RunOverFootprint.OfCapsule(Vector3.Zero).Radius, 4);
        // 0.34 m off the car's side: the full 0.35 m circle is hit, the 95% one (0.3325 m) is not
        var at = new Vector3(1.1f + 0.34f, 0, 0);
        Assert.True(CarFootprint.Hits(Vector3.Zero, Vector3.Zero, 0f, 1.1f, 2.15f, at, 0.35f));
        Assert.False(Hits(RunOverFootprint.OfCapsule(at)));
        Assert.True(Hits(RunOverFootprint.OfCapsule(new Vector3(1.1f + 0.32f, 0, 0))));
    }

    [Fact]
    public void An_animal_is_its_body_box_at_95_percent_about_its_centre()
    {
        // a 0.6 m wide, 2.0 m long body, centred where it stands
        static RunOverFootprint Animal(float x, float z) => RunOverFootprint.OfBody(BodyBox.Of(new Vector3(x, 0, z), 0f, 0.6f, 2.0f, 0f), 0f);
        // sideways: the body's edge is 0.3 m from its centre, 0.285 m scaled: touches the car's side (1.1) from 1.385
        Assert.True(Hits(Animal(1.37f, 0)));
        Assert.False(Hits(Animal(1.39f, 0))); // the full box (to 1.4 m) would have been hit
        // along: half length 1.0, 0.95 scaled: the car's front is at z -2.15, so a centre at -3.08 touches and -3.13 does not
        Assert.True(Hits(Animal(0, -3.08f)));
        Assert.False(Hits(Animal(0, -3.13f)));
    }

    [Fact]
    public void An_animals_box_turns_with_its_heading_and_a_box_is_hit_along_the_path()
    {
        // lying across the car's path (yaw 90 degrees): 2.0 m wide across the car's way, found by the sweep between the ticks
        var across = RunOverFootprint.OfBody(BodyBox.Of(new Vector3(0, 0, -6), MathF.PI / 2, 0.6f, 2.0f, 0f), 0f);
        Assert.False(across.Hits(Vector3.Zero, Vector3.Zero, 0f, 1.1f, 2.15f));
        Assert.True(across.Hits(Vector3.Zero, new Vector3(0, 0, -12), 0f, 1.1f, 2.15f));
        // feet far below or above the car are not run over
        var below = RunOverFootprint.OfBody(BodyBox.Of(new Vector3(0, 0, -1), 0f, 0.6f, 1f, 0f), -1f);
        Assert.False(below.Hits(Vector3.Zero, Vector3.Zero, 0f, 1.1f, 2.15f));
    }
}

public class HelicopterHullTests
{
    static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    static VehicleDef Heli => Data.Vehicles.Vehicles.Single(v => v.Id == "utility_helicopter");
    static VehicleDef Car => Data.Vehicles.Vehicles.Single(v => v.Id == "car");

    static VehicleBoxHit? Ray(Vector3 from, Vector3 dir, float pitch = 0, float roll = 0, float yaw = 0, Vector3 feet = default) =>
        VehicleBox.Ray(from, Vector3.Normalize(dir), 100, feet, yaw, pitch, roll, Heli);

    [Fact]
    public void The_helicopter_has_its_three_boxes_as_data_and_the_others_keep_the_one()
    {
        Assert.Equal(new[]
        {
            new HullBox(0, -0.5f, 1.3f, 3.5f, 0, 3.0f), new HullBox(0, 5.5f, 0.35f, 2.5f, 1.45f, 2.8f), new HullBox(0, 8.43f, 0.35f, 0.43f, 1.5f, 4.35f),
        }, Heli.HullBoxes);
        Assert.True(Heli.Tilts);
        Assert.True(Car.Tilts); // every driven type's one box tilts with its slope (#483)
        Assert.Equal(new[] { HullBox.Upright(Car.HalfWidth, Car.HalfLength, Car.Height) }, Car.HullBoxes);
        foreach (var v in Data.Vehicles.Vehicles.Where(v => v.Id is not ("utility_helicopter" or "rocket_cart"))) Assert.Null(v.Hull); // (the rocket cart's is #482)
    }

    [Fact]
    public void The_boxes_fit_the_model()
    {
        // the model's fuselage spans x +-1.41, y 0.02 to 3.69, z -3.85 to 8.82 (the tail rotor reaches y 4.35): every box is inside, and together they reach the nose and the fin
        float zMin = float.MaxValue, zMax = float.MinValue, yMax = 0;
        foreach (var b in Heli.HullBoxes)
        {
            Assert.True(b.Min.X >= -1.41f && b.Max.X <= 1.41f && b.Min.Y >= 0 && b.Max.Y <= 4.4f);
            zMin = MathF.Min(zMin, b.Min.Z); zMax = MathF.Max(zMax, b.Max.Z); yMax = MathF.Max(yMax, b.Y1);
        }
        Assert.InRange(zMin, -4.1f, -3.7f); Assert.InRange(zMax, 8.7f, 8.9f); Assert.Equal(4.35f, yMax, 3);
    }

    [Fact]
    public void A_ray_meets_the_cabin_the_tail_boom_and_the_fin_and_misses_the_air_around_them()
    {
        Assert.NotNull(Ray(new Vector3(10, 1.5f, -0.5f), -Vector3.UnitX));      // the cabin from the side
        Assert.NotNull(Ray(new Vector3(10, 2.0f, 5.5f), -Vector3.UnitX));       // the tail boom
        Assert.Null(Ray(new Vector3(10, 2.9f, 5.5f), -Vector3.UnitX));          // over the boom (its top is 2.8)
        Assert.Null(Ray(new Vector3(10, 0.5f, 5.5f), -Vector3.UnitX));          // under the boom
        Assert.NotNull(Ray(new Vector3(10, 4.0f, 8.43f), -Vector3.UnitX));      // the fin, well above the boom
        Assert.Null(Ray(new Vector3(10, 4.0f, 5.5f), -Vector3.UnitX));          // the same height at the boom: air
        Assert.Equal(10f - 0.35f, Ray(new Vector3(10, 1.5f, 7.0f), -Vector3.UnitX)!.Value.Distance, 3); // the boom is 0.35 m either side: nothing nearer than its face
    }

    [Fact]
    public void The_hit_is_in_the_node_frame_with_the_face_it_entered_by()
    {
        var h = Ray(new Vector3(10, 2.0f, 5.5f), -Vector3.UnitX)!.Value;
        Assert.Equal(10f - 0.35f, h.Distance, 3);
        Assert.Equal(0.35f, h.Local.X, 3);
        Assert.Equal(new Vector3(1, 0, 0), h.LocalNormal);
        // the vehicle turned by its yaw: the same ray along the world's Z meets the side that now faces it
        var turned = VehicleBox.Ray(new Vector3(0, 2.0f, 20), -Vector3.UnitZ, 100, Vector3.Zero, MathF.PI / 2, 0, 0, Heli)!.Value;
        Assert.InRange(turned.Distance, 1, 20);
    }

    [Fact]
    public void An_origin_inside_the_hull_is_not_a_hit()
    {
        Assert.Null(Ray(new Vector3(0, 1.5f, 0), Vector3.UnitX));       // in the cabin
        Assert.Null(Ray(new Vector3(0, 2.0f, 5.5f), Vector3.UnitZ));    // in the boom, heading for the fin
    }

    [Fact]
    public void The_boxes_tilt_with_pitch_and_roll()
    {
        var level = Ray(new Vector3(10, 2.0f, 5.5f), -Vector3.UnitX);
        Assert.NotNull(level);
        // nose up by -25 degrees lifts the tail: the ray that hit the boom now passes under it; one at the tail's new height hits
        var pitchedUp = -25f * MathF.PI / 180;
        Assert.Null(Ray(new Vector3(10, 2.0f, 5.5f), -Vector3.UnitX, pitch: pitchedUp));
        var boom = FlightMath.Rotate(new Vector3(0, 2.125f, 5.5f), 0, pitchedUp, 0);
        Assert.NotNull(Ray(new Vector3(10, boom.Y, boom.Z), -Vector3.UnitX, pitch: pitchedUp));
        // a roll swings the boom sideways: the cabin's side at the height of the boom's old position is still struck, but the boom's centre line moved off the vehicle's axis
        var rolled = 30f * MathF.PI / 180;
        var spot = FlightMath.Rotate(new Vector3(0, 2.125f, 5.5f), 0, 0, rolled);
        Assert.NotNull(Ray(new Vector3(10, spot.Y, spot.Z), -Vector3.UnitX, roll: rolled));
        Assert.Null(Ray(new Vector3(10, 2.125f + 1.0f, 5.5f), -Vector3.UnitX, roll: rolled)); // above the rolled boom's top
    }

    [Fact]
    public void The_hull_is_placed_by_the_vehicles_position_and_yaw()
    {
        var feet = new Vector3(100, 10, -40);
        var yaw = MathF.PI / 2; // turned a quarter, so the tail lies along the world's X
        var tail = feet + FlightMath.Rotate(new Vector3(0, 2.0f, 5.5f), yaw, 0, 0);
        Assert.NotNull(VehicleBox.Ray(tail + new Vector3(0, 0, 6), -Vector3.UnitZ, 20, feet, yaw, 0, 0, Heli));
        Assert.Null(VehicleBox.Ray(tail + new Vector3(0, 2, 6), -Vector3.UnitZ, 20, feet, yaw, 0, 0, Heli));
    }

    [Fact]
    public void A_ray_leaves_by_the_far_end_of_the_run_of_boxes()
    {
        // along the tail's axis at y 2.0: fin (z 8.86 to 8.0), boom (8.0 to 3.0) and cabin (3.0 to -4.0) are one volume
        var exit = VehicleBox.ExitDistance(new Vector3(0, 2.0f, 12), -Vector3.UnitZ, Heli.HullBoxes);
        Assert.Equal(16f, exit, 3);
        // across the cabin only
        Assert.Equal(11.3f, VehicleBox.ExitDistance(new Vector3(10, 1.5f, -0.5f), -Vector3.UnitX, Heli.HullBoxes), 3);
        // a type without a list: its one box, as before
        Assert.Equal(VehicleBox.ExitDistance(new Vector3(-3, 1, 0), Vector3.UnitX, Car.HalfWidth, Car.HalfLength, Car.Height), VehicleBox.ExitDistance(new Vector3(-3, 1, 0), Vector3.UnitX, Car), 4);
    }

    [Fact]
    public void A_blast_is_measured_to_the_nearest_box()
    {
        var feet = new Vector3(0, 0, 0);
        // 2 m beside the boom's centre: 2 - 0.35
        Assert.Equal(1.65f, HullDistance.Of(new Vector3(2, 2.0f, 5.5f), feet, 0, 0, 0, Heli), 3);
        Assert.Equal(0f, HullDistance.Of(new Vector3(0, 2.0f, 5.5f), feet, 0, 0, 0, Heli), 4); // inside the boom, beyond the old single box (it ended at z 4.0)
        // a car: the upright box, as VehicleDamage has it
        Assert.Equal(VehicleDamage.DistanceToBox(new Vector3(4, 1, 0), feet, 0.3f, Car.HalfWidth, Car.HalfLength, Car.Height), HullDistance.Of(new Vector3(4, 1, 0), feet, 0.3f, 0f, 0f, Car), 4); // (level: a tilted car is DriveSlopeHullTests)
    }

    [Fact]
    public void The_gunship_routes_a_round_through_the_boxes()
    {
        var v = new GunshipVehicle(0, Vector3.Zero, 0, Heli.HalfWidth, Heli.HalfLength, Heli.Height, Hull: Heli.HullBoxes);
        Assert.NotNull(v.Ray(new Vector3(10, 2.0f, 5.5f), -Vector3.UnitX, 50));
        Assert.Null(v.Ray(new Vector3(10, 2.0f, 6.5f), Vector3.UnitX, 50)); // away from it
        // the old upright box ended at z 4.0: nothing was there at 6.0 and 2.0 m high; the boom is
        var old = new GunshipVehicle(0, Vector3.Zero, 0, Heli.HalfWidth, Heli.HalfLength, Heli.Height);
        Assert.Null(old.Ray(new Vector3(10, 2.0f, 6.0f), -Vector3.UnitX, 50));
        Assert.NotNull(v.Ray(new Vector3(10, 2.0f, 6.0f), -Vector3.UnitX, 50));
        Assert.Equal(0f, v.DistanceTo(new Vector3(0, 2.0f, 5.5f)), 4);
    }

    [Fact]
    public void Vehicles_bump_by_the_boxes()
    {
        var heli = new ContactShape(Vector3.Zero, 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height, Hull: Heli.HullBoxes);
        var old = new ContactShape(Vector3.Zero, 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height);
        // a car (the upright box) under the fin: only the hull reaches it
        ContactShape CarAt(float x, float z) => new(new Vector3(x, 0, z), 0f, Car.HalfWidth, Car.HalfLength, Car.Height);
        Assert.Null(VehicleContact.Test(old, CarAt(0, 9.5f), true));
        Assert.NotNull(VehicleContact.Test(heli, CarAt(0, 9.5f), true));
        // beside the boom's width: a car at x 2.0 is clear of the 0.35 m boom, at 1.1 it is in it
        Assert.Null(VehicleContact.Test(heli, CarAt(2.0f, 6.0f), true));
        var hit = VehicleContact.Test(heli, CarAt(1.1f, 6.0f), true); // the car is 2.06 m wide: at 1.1 its side is in the boom
        Assert.NotNull(hit);
        // the hull is a body: a car in the cabin's side is pushed out sideways (the normal points from the helicopter to the car)
        var side = VehicleContact.Test(heli, CarAt(2.1f, -0.5f), true)!.Value;
        Assert.True(side.Normal.X > 0.9f, side.Normal.ToString());
    }

    [Fact]
    public void The_bump_follows_the_tilt()
    {
        ContactShape Heli_(float pitch) => new(Vector3.Zero, 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height, Pitch: pitch, Hull: Heli.HullBoxes);
        ContactShape CarAt(float x, float z) => new(new Vector3(x, 0, z), 0f, Car.HalfWidth, Car.HalfLength, Car.Height);
        // a car under the tail boom (it spans 1.45 to 2.8 m, the car is 2.2 m high): level, they touch
        Assert.NotNull(VehicleContact.Test(Heli_(0), CarAt(0, 6.0f), true));
        // nose up (negative pitch) lifts the tail well above the roof
        Assert.Null(VehicleContact.Test(Heli_(-25f * MathF.PI / 180), CarAt(0, 6.0f), true));
        // nose down drops it onto the car, and the push is up
        var down = VehicleContact.Test(Heli_(25f * MathF.PI / 180), CarAt(0, 6.0f), true);
        Assert.NotNull(down);
    }

    [Fact]
    public void The_rotor_disc_still_meets_a_car_beside_the_hull()
    {
        var heli = new ContactShape(new Vector3(0, 0, 0), 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height, 4.5f, 3.4f, 0.3f, Hull: Heli.HullBoxes);
        // a van-high box 4.0 m to the side and 3.4 m tall: out of the cabin (1.3) but inside the rotor (4.5) at the disc's height
        var tall = new ContactShape(new Vector3(5.2f, 0, 0), 0f, 1.2f, 2.3f, 4.0f);
        Assert.NotNull(VehicleContact.Test(heli, tall, true));
        Assert.Null(VehicleContact.Test(heli, new ContactShape(new Vector3(9f, 0, 0), 0f, 1.2f, 2.3f, 4.0f), true));
    }

    [Fact]
    public void Between_two_helicopters_the_boxes_meet_in_three_dimensions()
    {
        var a = new ContactShape(Vector3.Zero, 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height, Hull: Heli.HullBoxes);
        // another one's cabin, 2.4 m to the side: the two cabins (1.3 each) overlap by 0.2
        var b = new ContactShape(new Vector3(2.4f, 0, 0), 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height, Hull: Heli.HullBoxes);
        var hit = VehicleContact.Test(a, b, true)!.Value;
        Assert.True(hit.Normal.X > 0.9f);
        Assert.InRange(hit.Depth, 0.15f, 3.1f);
        Assert.Null(VehicleContact.Test(a, new ContactShape(new Vector3(8f, 0, 0), 0f, Heli.HalfWidth, Heli.HalfLength, Heli.Height, Hull: Heli.HullBoxes), true));
    }

    [Fact]
    public void A_hull_list_is_checked_when_the_file_loads()
    {
        string With(string hull) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Replace("\"hull\": [[0, -0.5, 1.3, 3.5, 0, 3.0], [0, 5.5, 0.35, 2.5, 1.45, 2.8], [0, 8.43, 0.35, 0.43, 1.5, 4.35]]", hull);
        VehiclesFile Load(string json)
        {
            var path = Path.Combine(Path.GetTempPath(), $"vehicles-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            try { return VehiclesFile.Load(path); } finally { File.Delete(path); }
        }
        Assert.Equal(3, Load(With("\"hull\": [[0, -0.5, 1.3, 3.5, 0, 3.0], [0, 5.5, 0.35, 2.5, 1.45, 2.8], [0, 8.43, 0.35, 0.43, 1.5, 4.35]]")).Vehicles.Single(v => v.Id == "utility_helicopter").HullBoxes.Length);
        Assert.Throws<DataFileException>(() => Load(With("\"hull\": []")));
        Assert.Throws<DataFileException>(() => Load(With("\"hull\": [[0, 0, 1, 1, 0]]")));        // five numbers
        Assert.Throws<DataFileException>(() => Load(With("\"hull\": [[0, 0, 0, 1, 0, 2]]")));      // no width
        Assert.Throws<DataFileException>(() => Load(With("\"hull\": [[0, 0, 1, 1, 2, 2]]")));      // no height
    }
}
