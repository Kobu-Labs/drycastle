using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Every ground vehicle with a driver is on the drive model (ADR 0036): it lies on the slope under its own wheels, its driver's client predicts it, and it
/// still drives by its own basic numbers (top speed, turn circle) as it did on the old car model.
/// </summary>
public class EveryVehicleDrivesTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));

    public static IEnumerable<object[]> Ground() => Shipped.Vehicles.Where(v => v.Flight == null && v.Trailer == null).Select(v => new object[] { v.Id });
    static VehicleDef Def(string id) => Shipped.Vehicles.Single(v => v.Id == id);

    /// <summary>A plane that climbs towards -Z (the way a vehicle at yaw 0 faces) by <see cref="Slope"/> metres a metre, and across +X by <see cref="Cross"/>.</summary>
    sealed class Hill : IDriveWorld
    {
        public float Slope, Cross;
        public float GroundY(Vector3 from) => -from.Z * Slope + from.X * Cross;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    static DriveInput Keys(float throttle = 0, float steer = 0) => new() { Throttle = throttle, Steer = steer };
    const float Dt = 1f / 60f;

    [Fact]
    public void Every_ground_vehicle_with_a_driver_has_a_drive_block()
    {
        foreach (var v in Shipped.Vehicles)
        {
            if (v.Flight != null || v.Trailer != null) Assert.Null(v.Drive);
            else Assert.True(v.Drive != null, $"{v.Id} has no drive block: it would not lie on a slope and its driver would not predict it (ADR 0036)");
        }
    }

    [Theory]
    [MemberData(nameof(Ground))]
    public void It_lies_on_the_slope_it_stands_on_with_its_origin_on_the_ground(string id)
    {
        var def = Def(id);
        var hill = new Hill { Slope = 0.3f };
        var s = new DriveState { Position = new Vector3(0, 0, 0) };
        DriveModel.Settle(ref s, def, hill);
        Assert.Equal(MathF.Atan(0.3f), s.Pitch, 3); // nose up the hill
        Assert.Equal(0f, s.Roll, 3);
        Assert.Equal(0f, s.Position.Y, 2); // the origin is on the plane wherever the axles are along the model
        // and it stays there driving down it: no wheel leaves the ground
        s.Yaw = MathF.PI; DriveModel.Settle(ref s, def, hill);
        for (var i = 0; i < 180; i++)
        {
            DriveModel.Step(ref s, def, Keys(throttle: 0.5f), Dt, hill);
            Assert.False(s.Airborne, $"{id} left the ground driving down a steady slope");
            Assert.Equal(hill.GroundY(s.Position), s.Position.Y, 2);
            Assert.Equal(-MathF.Atan(0.3f), s.Pitch, 2); // nose down
        }
    }

    [Theory]
    [MemberData(nameof(Ground))]
    public void Across_a_slope_it_rolls_with_the_ground_unless_it_has_one_track(string id)
    {
        var def = Def(id);
        var s = new DriveState();
        DriveModel.Settle(ref s, def, new Hill { Cross = 0.2f });
        if (def.Drive!.Track > 0) Assert.Equal(MathF.Atan(0.2f), s.Roll, 3);
        else Assert.Equal(0f, s.Roll); // a motorcycle stands upright
    }

    [Theory]
    [MemberData(nameof(Ground))]
    public void It_reaches_its_top_speed_and_turns_its_circle_on_the_flat(string id)
    {
        var def = Def(id);
        var flat = new Hill();
        var s = new DriveState();
        DriveModel.Settle(ref s, def, flat);
        for (var i = 0; i < 60 * 90; i++) DriveModel.Step(ref s, def, Keys(throttle: 1), Dt, flat);
        Assert.InRange(s.Speed, def.TopSpeed * 0.9f, def.TopSpeed * 1.02f);
        // at walking pace, full lock: the circle is the type's turn radius
        s = new DriveState(); DriveModel.Settle(ref s, def, flat);
        s.Speed = 1.5f;
        for (var i = 0; i < 30; i++) { s.Speed = 1.5f; DriveModel.Step(ref s, def, Keys(steer: 1), Dt, flat); }
        var radius = MathF.Abs(s.Speed / s.YawRate);
        Assert.InRange(radius, DriveModel.TurnRadiusAt(def, s.Speed) * 0.9f, DriveModel.TurnRadiusAt(def, s.Speed) * 1.25f);
    }

    [Theory]
    [MemberData(nameof(Ground))]
    public void Its_drive_block_says_where_it_turns_about_and_that_is_at_its_fixed_axles(string id)
    {
        var def = Def(id); var d = def.Drive!;
        Assert.True(d.Land.PivotBehind != null, $"{id}: drive.land.pivot_behind is not set (the z of the axle that does not steer, the mean of several): it would turn about its origin (#295)");
        var pivot = d.Land.PivotBehind!.Value;
        // behind the point between the axles, and no further back than the last axle (the data's wheelbase is to the mean of the fixed axles on some: a little slack)
        Assert.InRange(pivot, d.AxleCentre, d.AxleCentre + d.Wheelbase / 2f + 0.15f);
        // a model that is in the repository: the number is the model's (the mean z of the wheels without a steering parent)
        if (!def.Model.StartsWith("drycastle-generated-models/")) return;
        var nodes = AmphibiousModelTests.Nodes(def.Model);
        var wheels = WheelMath.AllNames().Where(nodes.ContainsKey).ToList();
        var z = wheels.Select(w => nodes[w].Z).ToArray();
        var steered = wheels.Select(w => nodes.ContainsKey(w + WheelMath.SteerSuffix)).ToArray();
        Assert.InRange(pivot - WheelMath.PivotZ(z, steered), -0.005f, 0.005f);
    }

    [Theory]
    [MemberData(nameof(Ground))]
    public void It_turns_about_its_fixed_axle_the_origin_ahead_of_it_sweeps_wider(string id)
    {
        var def = Def(id);
        var pivot = def.Drive!.Land.PivotBehind!.Value;
        var flat = new Hill();
        var s = new DriveState(); DriveModel.Settle(ref s, def, flat);
        Vector3 Fwd(float yaw) => new(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        Vector3 Right(float yaw) => new(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        for (var i = 0; i < 60; i++) { s.Speed = 2f; DriveModel.Step(ref s, def, Keys(steer: 1), Dt, flat); }
        var before = s;
        s.Speed = 2f; DriveModel.Step(ref s, def, Keys(steer: 1), Dt, flat);
        Assert.True(MathF.Abs(s.YawRate) > 0.05f, $"{id} does not turn");
        // the point on the fixed axle goes straight ahead along the heading: no sideways speed, so its circle is the turn radius
        var axle = (s.Position - Fwd(s.Yaw) * pivot) - (before.Position - Fwd(before.Yaw) * pivot);
        Assert.InRange(Vector3.Dot(axle, Right(s.Yaw)) / Dt, -0.03f, 0.03f); // (what is left is the tick's own arc)
        Assert.InRange(Vector3.Dot(axle, Fwd(s.Yaw)) / Dt, s.Speed - 0.03f, s.Speed + 0.03f);
        // the origin goes sideways into the turn by the yaw rate times its distance ahead of the axle; that is what a trailer on the hitch is pulled by
        var origin = s.Position - before.Position;
        Assert.Equal(-s.YawRate * pivot, Vector3.Dot(origin, Right(s.Yaw)) / Dt, 2);
        Assert.Equal(-s.YawRate * pivot, DriveModel.OriginSlip(s, def), 4);
        Assert.Equal(0f, s.Slip, 3); // the tyres hold: the slide itself is none
        // in the air nothing holds the axle: it turns about its origin
        var air = s; air.Airborne = true;
        Assert.Equal(0f, DriveModel.PivotBehind(air, def));
    }

    [Fact]
    public void A_held_vehicle_brakes_to_a_stop_whatever_the_driver_presses()
    {
        var def = Def("scud");
        var flat = new Hill();
        var s = new DriveState();
        DriveModel.Settle(ref s, def, flat);
        for (var i = 0; i < 600; i++) DriveModel.Step(ref s, def, Keys(throttle: 1), Dt, flat);
        Assert.True(s.Speed > 5f);
        for (var i = 0; i < 300; i++) DriveModel.Step(ref s, def, DriveInput.Held, Dt, flat);
        Assert.Equal(0f, s.Speed);
    }
}
