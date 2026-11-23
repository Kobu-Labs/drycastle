using System.Numerics;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>A driven vehicle's one box lies on its slope, as it is drawn (#483): the drive state's signs, the raised and the lowered end, the contact of two tilted ground vehicles.</summary>
public class DriveSlopeHullTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Truck => Shipped.Vehicles.Single(v => v.Id == "truck");
    const float Pitch = 0.3f;

    static bool Hits(VehicleDef def, float pitch, float roll, Vector3 from, Vector3 dir) => VehicleBox.Ray(from, dir, 20f, Vector3.Zero, 0, pitch, roll, def) != null;

    [Fact]
    public void Every_shipped_driven_type_tilts_without_a_hull_list()
    {
        foreach (var v in Shipped.Vehicles.Where(v => v.Drive != null)) { Assert.True(v.Tilts, v.Id); Assert.Null(v.Hull); }
    }

    [Fact]
    public void Positive_drive_pitch_raises_the_nose_and_the_hull_turns_with_the_seats()
    {
        var s = new DriveState { Pitch = Pitch };
        Assert.True(s.ToWorld(new[] { 0f, 0f, -2f }).Y > 0.5f); // the nose is -z: a positive pitch lifts it (DriveState's own doc)
        Assert.True(s.ToWorld(new[] { 0f, 0f, 2f }).Y < -0.5f);
        var r = new DriveState { Roll = Pitch };
        Assert.True(r.ToWorld(new[] { -1f, 0f, 0f }).Y < -0.2f); // positive roll drops the left side (-x)
        Assert.True(Hits(Truck, 0, Pitch, new Vector3(-0.5f, 5f, -2f), -Vector3.UnitY)); // sanity: the box is there
    }

    [Fact]
    public void A_shot_at_the_raised_nose_hits_over_the_roof_height_and_one_under_it_passes()
    {
        var hl = 2f; // two metres out from the origin along the body
        var dir = -Vector3.UnitX;
        Assert.False(Hits(Truck, 0, 0, new Vector3(5f, Truck.Height + 0.3f, -hl), dir)); // level: over the roof
        Assert.True(Hits(Truck, Pitch, 0, new Vector3(5f, Truck.Height + 0.3f, -hl), dir)); // the nose end stands higher
        Assert.True(Hits(Truck, 0, 0, new Vector3(5f, 0.3f, -hl), dir));
        Assert.False(Hits(Truck, Pitch, 0, new Vector3(5f, 0.3f, -hl), dir)); // the air under the lifted nose
        Assert.True(Hits(Truck, Pitch, 0, new Vector3(5f, 0.3f, hl), dir)); // the low end is still there
        Assert.False(Hits(Truck, Pitch, 0, new Vector3(5f, Truck.Height + 0.3f, hl), dir)); // and not over it
    }

    [Fact]
    public void Two_tilted_ground_vehicles_are_pushed_apart_sideways_and_stay_finite()
    {
        var a = new ContactShape(Vector3.Zero, 0, Truck.HalfWidth, Truck.HalfLength, Truck.Height, Pitch: 0.35f, Roll: 0.1f, Hull: Truck.HullBoxes);
        var b = new ContactShape(new Vector3(1.2f, 0, 0.5f), 0.4f, Truck.HalfWidth, Truck.HalfLength, Truck.Height, Pitch: 0.35f, Roll: 0.1f, Hull: Truck.HullBoxes);
        var hit = VehicleContact.Test(a, b, vertical: false)!.Value;
        Assert.Equal(0f, hit.Normal.Y, 4);
        Assert.Equal(1f, hit.Normal.Length(), 3);
        Assert.True(hit.Depth > 0);
        var bump = VehicleContact.Bump(hit, new BumpBody(1200f, Vector3.Zero, false), new BumpBody(1200f, Vector3.Zero, false), 0.3f);
        Assert.True(float.IsFinite(bump.MoveA.X) && float.IsFinite(bump.MoveB.Z));
        Assert.Null(VehicleContact.Test(a, a with { Feet = new Vector3(20f, 0, 0) }, false));
    }
}
