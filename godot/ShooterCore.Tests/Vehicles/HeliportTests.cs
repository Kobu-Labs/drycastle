using System.Numerics;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>
/// The utility helicopter stands at a fixed spot on the military base's heliport instead of being dealt at random: where, which way it faces
/// (square to the two open hangars beside it), and that it sits still there and comes back to it.
/// </summary>
public class HeliportTests
{
    const float Dt = 1f / Wire.TickRate;
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Heli => Shipped.Vehicles.Single(v => v.Id == "utility_helicopter");

    // The hangars o-0582 / o-0583 (pack:synty-military/SM_Bld_Hangar_Open_01) of the village map's objects.json: map x east, y north; rotation_deg is
    // the Godot Y turn of the node, whose world position is (x, -y). The model is 30.6 m wide (x) and 36.3 m deep (z), centred, and open on its +Z side.
    const float HangarTurnDeg = 332.5f;
    static readonly Vector2[] Hangars = { new(537.706f, -585.106f), new(564.946f, -570.946f) };
    const float HangarHalfX = 15.32f, HangarHalfZ = 18.13f;

    static VehicleSpotDef Spot => Assert.Single(Heli.FixedSpots);

    [Fact]
    public void It_is_never_dealt_and_stands_at_one_spot_on_the_base()
    {
        Assert.Equal(0, Heli.SpawnCount);
        Assert.Equal(509.6f, Spot.X, 2);
        Assert.Equal(-538.1f, Spot.Z, 2);
        Assert.NotNull(Spot.YawDeg); // an explicit heading: a road near the spot must not turn it
    }

    [Fact]
    public void It_stands_square_to_the_hangars_with_its_nose_away_from_their_open_side()
    {
        var yaw = Spot.YawDeg!.Value;
        var offAxis = ((yaw - HangarTurnDeg) % 90f + 90f) % 90f;
        Assert.True(offAxis < 0.01f || offAxis > 89.99f, $"yaw {yaw} is {offAxis} deg off the hangars' axes");
        // a node turned by t about Y has its +Z side facing world (sin t, cos t); a vehicle turned by y has its nose along (-sin y, -cos y): same turn, same world
        var t = HangarTurnDeg * MathF.PI / 180f; var y = yaw * MathF.PI / 180f;
        var open = new Vector2(MathF.Sin(t), MathF.Cos(t));
        var nose = new Vector2(-MathF.Sin(y), -MathF.Cos(y));
        Assert.True(Vector2.Dot(nose, open) > 0.999f, "the nose points out of the hangars, the way their open side faces");
        foreach (var h in Hangars)
        {
            var toSpot = Vector2.Normalize(new Vector2(Spot.X, Spot.Z) - h);
            Assert.True(Vector2.Dot(open, toSpot) > 0.8f, "the spot is in front of the hangar's open side");
        }
    }

    [Fact]
    public void Its_hull_and_rotor_disc_are_clear_of_the_hangars()
    {
        var def = Heli;
        var reach = Math.Max(def.HalfLength, def.Flight!.RotorRadius);
        var t = HangarTurnDeg * MathF.PI / 180f;
        foreach (var h in Hangars)
        {
            var d = new Vector2(Spot.X, Spot.Z) - h;
            // into the hangar's own frame: the node's X axis is (cos t, -sin t) and its Z axis (sin t, cos t) in the world's (x, z)
            var lx = d.X * MathF.Cos(t) - d.Y * MathF.Sin(t);
            var lz = d.X * MathF.Sin(t) + d.Y * MathF.Cos(t);
            var gap = MathF.Max(MathF.Abs(lx) - HangarHalfX, MathF.Abs(lz) - HangarHalfZ);
            Assert.True(gap > reach + 3f, $"{gap:F1} m from the hangar's wall, the rotor reaches {reach} m");
        }
    }

    [Fact]
    public void A_road_beside_the_spot_does_not_turn_it()
    {
        var road = new RoadLine(new[] { new Vector2(Spot.X - 50, -Spot.Z + 3), new Vector2(Spot.X + 50, -Spot.Z + 3) }, 5f, "service"); // 3 m north of the spot, running east
        var yaw = RoadHeading.YawFor(Spot, new[] { road });
        Assert.Equal(Spot.YawDeg!.Value * MathF.PI / 180f, yaw, 5);
    }

    [Fact]
    public void It_rests_where_it_stands_hands_off()
    {
        // ServerCar.ResetFlight (a return after destruction too): on its skids at the origin, rotor stopped, facing the spot's yaw. Nobody aboard: it must neither lift nor creep nor turn.
        var yaw = FlightMath.Wrap(Spot.YawDeg!.Value * MathF.PI / 180f);
        var at = new Vector3(Spot.X, 189f, Spot.Z);
        var s = new FlightState { Position = at, Yaw = yaw, Grounded = true };
        var world = new Flat { Ground = 189f };
        for (var i = 0; i < 30 * Wire.TickRate; i++) FlightModel.Step(ref s, Heli, new FlightInput(), Dt, world);
        Assert.True(s.Grounded);
        Assert.Equal(at.X, s.Position.X, 3);
        Assert.Equal(at.Z, s.Position.Z, 3);
        Assert.InRange(s.Position.Y, 189f - 0.05f, 189f + 0.05f);
        Assert.Equal(yaw, s.Yaw, 4);
        Assert.Equal(0f, s.Velocity.Length(), 3);
    }

    sealed class Flat : IFlightWorld
    {
        public float Ground;
        public FlightBounds Bounds { get; set; } = FlightBounds.None;
        public float GroundY(Vector3 position) => Ground;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public FlightMove Move(Vector3 from, float yaw, Vector3 motion)
        {
            if (motion.Y < 0 && from.Y + motion.Y < Ground)
            {
                var t = Math.Clamp((from.Y - Ground) / -motion.Y, 0f, 1f);
                return new FlightMove(from + motion * t, true, Vector3.UnitY);
            }
            return new FlightMove(from + motion, false, Vector3.Zero);
        }
    }
}
