using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>A vehicle driven up and down a real slope, with the ground found by a ray that starts a metre above the point and reaches 8 m (as <c>DriveProbe.GroundY</c> does), never falls through it.</summary>
public class SlopeFallTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    public static IEnumerable<object[]> Ground() => Shipped.Vehicles.Where(v => v.Flight == null && v.Trailer == null).Select(v => new object[] { v.Id });

    /// <summary>Flat to z=0, then a ramp of <see cref="Slope"/> up to z=-Run, then flat again (a dyke's flank). A ray that starts inside the ground misses (a one-sided mesh).</summary>
    sealed class Ramp : IDriveWorld
    {
        public float Slope, Run = 20;
        public float Height(float z) { var t = Math.Clamp(-z, 0, Run); return t * Slope; }
        public float GroundY(Vector3 from)
        {
            var top = from.Y + 1f; var h = Height(from.Z);
            if (h > top || h < top - 9f) return float.NegativeInfinity;
            return h;
        }
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    [Theory]
    [MemberData(nameof(Ground))]
    public void Up_and_down_a_steep_ramp_it_stays_on_the_ground(string id)
    {
        var def = Shipped.Vehicles.Single(v => v.Id == id);
        foreach (var slope in new[] { 0.5f, 0.8f, 1.0f })
        foreach (var yaw in new[] { 0f, MathF.PI })
        {
            var w = new Ramp { Slope = slope };
            var z0 = yaw == 0 ? 5 : -w.Run - 5;
            var s = new DriveState { Position = new Vector3(0, w.Height(z0), z0), Yaw = yaw };
            DriveModel.Settle(ref s, def, w);
            for (var i = 0; i < 60 * 12; i++)
            {
                DriveModel.Step(ref s, def, new DriveInput { Throttle = 1 }, 1f / 60, w);
                var g = w.Height(s.Position.Z);
                Assert.True(s.Position.Y > g - 1.5f, $"{id} slope {slope} yaw {yaw}: fell through at tick {i}: y {s.Position.Y} ground {g}");
            }
        }
    }
}
