using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class MechanicGarageTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Golf => Shipped.Vehicles.Single(v => v.Id == "golf_mk2");

    [Fact]
    public void The_golf_fits_inside_with_its_nose_clear_of_the_road_wall_and_the_plate_in_front_of_it()
    {
        var g = Golf;
        const float S = MechanicGarageSite.HalfZ;
        // the nose (the model faces -Z in the garage frame) is short of the wall, the tail short of the back wall
        Assert.InRange(MechanicGarageSite.GolfZ - g.HalfLength, -S, S);
        Assert.InRange(MechanicGarageSite.GolfZ + g.HalfLength, -S, S);
        Assert.Equal(MechanicGarageSite.GolfZ - g.HalfLength, -S + MechanicGarageSite.GolfNoseGap, 1);
        // beside the walls, and the plate is wider than the Golf by more than a mirror's width
        Assert.InRange(MechanicGarageSite.GolfX - g.HalfWidth, -MechanicGarageSite.HalfX, MechanicGarageSite.HalfX);
        Assert.True(MechanicGarageSite.PlateWidth >= g.HalfWidth * 2 + 0.4f, "the Golf has to drive out through the opening");
        Assert.True(MechanicGarageSite.PlateHeight >= g.Height + 0.5f);
        Assert.Equal(MechanicGarageSite.GolfX, MechanicGarageSite.PlateX);
        // the vehicle probe starts 3 m over the ground: it must start under the roof
        Assert.True(MechanicGarageSite.RoofLow > 3.2f);
    }

    [Fact]
    public void The_plate_is_in_the_road_wall_and_its_pane_faces_the_road()
    {
        var rect = MechanicGarageSite.PlateRect(100f);
        var outward = MechanicGarageSite.DirToWorld(0, -1);
        Assert.True(Vector3.Dot(rect.Normal, outward) > 0.999f);
        Assert.Equal(MechanicGarageSite.PlateWidth, rect.Width, 3);
        Assert.Equal(100f + MechanicGarageSite.PlateHeight / 2, rect.Centre.Y, 3);
        // on the outer face: the wall's outer plane is 4 m from the middle along the outward direction
        var middle = MechanicGarageSite.ToWorld(0, 0);
        Assert.Equal(MechanicGarageSite.Depth / 2, Vector3.Dot(new Vector3(rect.Centre.X - middle.X, 0, rect.Centre.Z - middle.Z), outward), 2);
        // the road is to the east: out of the plate goes east and a little south
        Assert.True(outward.X > 0.95f);
    }

    [Fact]
    public void The_plate_joins_the_maps_panes_once_in_wire_order_with_its_own_hit_points()
    {
        var panes = new List<MapGlassPane>
        {
            new("zz_window", GlassPaneRect.Make(Vector3.Zero, Vector3.UnitX, 1, 1), true),
            new("aa_window", GlassPaneRect.Make(Vector3.Zero, Vector3.UnitX, 1, 1), true),
        };
        MechanicGarageSite.AddPane(panes, 188f);
        MechanicGarageSite.AddPane(panes, 188f); // idempotent
        Assert.Equal(new[] { "aa_window", MechanicGarageSite.PlateNode, "zz_window" }, panes.Select(p => p.Node));
        var plate = panes.Single(p => p.Node == MechanicGarageSite.PlateNode);
        Assert.True(plate.Breakable);
        Assert.Equal(MechanicGarageSite.PlateHp, plate.Hp);
        MechanicGarageSite.AddPane(new List<MapGlassPane>(), float.NaN); // no ground: no pane
    }

    [Fact]
    public void The_floor_is_a_hair_over_the_highest_ground_under_it()
    {
        var f = MechanicGarageSite.FloorY((x, z) => 100f + 0.001f * x);
        Assert.InRange(f, 100f, 100.2f);
        Assert.True(float.IsNaN(MechanicGarageSite.FloorY((x, z) => float.NaN)));
        Assert.Equal(7.02f, MechanicGarageSite.FloorY((x, z) => 7f), 3);
    }

    [Fact]
    public void The_frame_maps_the_golf_spot_to_the_world_and_back_turns_it_out_of_the_plate()
    {
        var spot = MechanicGarageSite.GolfSpot;
        var p = MechanicGarageSite.ToWorld(MechanicGarageSite.GolfX, MechanicGarageSite.GolfZ);
        Assert.Equal(p.X, spot.X, 3);
        Assert.Equal(p.Z, spot.Z, 3);
        // game yaw: 0 faces -Z, positive turns left; the spot's heading is the garage's out direction
        var out_ = MechanicGarageSite.DirToWorld(0, -1);
        Assert.Equal(RoadHeading.YawOf(out_.X, out_.Z) * 180f / MathF.PI, spot.YawDeg, 1);
        Assert.True(MechanicGarageSite.Inside(MechanicGarageSite.GolfX, MechanicGarageSite.GolfZ));
        Assert.False(MechanicGarageSite.Inside(MechanicGarageSite.Width, 0));
    }

    [Fact]
    public void The_room_is_dark_then_the_tubes_flicker_without_lighting_it_then_the_lights_come_on()
    {
        StateMachineDiagnostics.Strict = true;
        var l = new MechanicGarageLights();
        Assert.Equal(GarageLightPhase.Sealed, l.Phase);
        Assert.Equal(1f, l.Veil);
        l.PlateBroken(true, animate: true);
        Assert.Equal(GarageLightPhase.Dark, l.Phase);
        Assert.Equal(0f, l.Glow);
        Assert.Equal(0f, l.Level);
        // through the dark moment, then the flicker: the tubes flash, the real lights never come up
        var sawGlow = false; var sawDarkInFlicker = false;
        var guard = 0;
        while (l.Phase != GarageLightPhase.On && guard++ < 2000)
        {
            l.Update(1f / 60);
            if (l.Phase == GarageLightPhase.Flicker)
            {
                Assert.Equal(0f, l.Level);
                if (l.Glow > 0) sawGlow = true; else sawDarkInFlicker = true;
            }
        }
        Assert.True(sawGlow && sawDarkInFlicker, "the tubes flash on and off");
        Assert.Equal(GarageLightPhase.On, l.Phase);
        for (var i = 0; i < 120; i++) l.Update(1f / 60);
        Assert.Equal(1f, l.Level);
        Assert.Equal(1f, l.Glow);
        Assert.Equal(0f, l.Veil, 3);
    }

    [Fact]
    public void The_whole_sequence_from_the_break_to_the_lights_on_is_short()
    {
        Assert.InRange(MechanicGarageLights.DarkSeconds + MechanicGarageLights.FlickerSeconds + MechanicGarageLights.RampSeconds, 0f, 2.5f);
    }

    [Fact]
    public void A_client_that_arrives_after_the_break_sees_the_lights_on_and_a_restored_plate_darkens_it()
    {
        StateMachineDiagnostics.Strict = true;
        var l = new MechanicGarageLights();
        l.PlateBroken(true, animate: false);
        Assert.Equal(GarageLightPhase.On, l.Phase);
        Assert.Equal(1f, l.Level);
        l.PlateBroken(true, animate: true); // already on: the sequence does not start over
        Assert.Equal(GarageLightPhase.On, l.Phase);
        l.PlateBroken(false, animate: false);
        Assert.Equal(GarageLightPhase.Sealed, l.Phase);
        Assert.Equal(0f, l.Level);
        Assert.Equal(1f, l.Veil);
    }

    [Fact]
    public void Covers_is_the_footprint_in_the_world_and_the_inverse_of_the_frame()
    {
        foreach (var (x, z) in new[] { (0f, 0f), (2.9f, 3.9f), (-3.2f, -3.9f), (MechanicGarageSite.GolfX, MechanicGarageSite.GolfZ) })
        {
            var p = MechanicGarageSite.ToWorld(x, z);
            Assert.True(MechanicGarageSite.Covers(p.X, p.Z, 0), $"({x}, {z}) is inside");
        }
        var out_ = MechanicGarageSite.ToWorld(MechanicGarageSite.Width / 2 + 0.4f, 0);
        Assert.False(MechanicGarageSite.Covers(out_.X, out_.Z, 0));
        Assert.True(MechanicGarageSite.Covers(out_.X, out_.Z, 0.5f));
        // the two baked trees (map x, y = x, -z) that stood on the floor
        Assert.True(MechanicGarageSite.Covers(31.8f, 18.1f, 0.5f));
        Assert.True(MechanicGarageSite.Covers(33.5f, 12.2f, 0.5f));
    }
}
