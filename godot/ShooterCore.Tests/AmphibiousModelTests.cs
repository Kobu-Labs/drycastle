using System.Numerics;
using System.Text;
using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The amphibious transport's generated model (scripts/make_amphibious_model.py) against its vehicles.json entry: every moving part is a node
/// at its pivot, and the numbers the game uses (seats, lamps, the smoke point, the hit box) are the model's own.
/// </summary>
public class AmphibiousModelTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Transport => Shipped.Vehicles.Single(v => v.Id == "amphibious");

    /// <summary>Every node of a .glb by name, with its origin in the model's frame (the translations summed down the tree; the model has no rotations).</summary>
    internal static Dictionary<string, Vector3> Nodes(string model)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "assets", model))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, $"assets/{model} not found above {AppContext.BaseDirectory}");
        var bytes = File.ReadAllBytes(Path.Combine(dir!, "assets", model));
        Assert.Equal("glTF", Encoding.ASCII.GetString(bytes, 0, 4));
        var jsonLength = BitConverter.ToInt32(bytes, 12);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength));
        var nodes = doc.RootElement.GetProperty("nodes");
        var result = new Dictionary<string, Vector3>();
        void Walk(int index, Vector3 parent)
        {
            var n = nodes[index];
            Assert.False(n.TryGetProperty("rotation", out _), "the model's nodes are not rotated");
            var at = parent;
            if (n.TryGetProperty("translation", out var t)) at += new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle());
            result[n.GetProperty("name").GetString()!] = at;
            if (n.TryGetProperty("children", out var kids)) foreach (var k in kids.EnumerateArray()) Walk(k.GetInt32(), at);
        }
        foreach (var top in doc.RootElement.GetProperty("scenes")[0].GetProperty("nodes").EnumerateArray()) Walk(top.GetInt32(), Vector3.Zero);
        return result;
    }

    static void Near(Vector3 expected, float[] actual, float tolerance = 0.006f) =>
        Assert.True(Vector3.Distance(expected, new Vector3(actual[0], actual[1], actual[2])) < tolerance, $"expected {expected}, got ({actual[0]}, {actual[1]}, {actual[2]})");

    [Fact]
    public void Every_moving_part_is_a_node_at_its_pivot()
    {
        var n = Nodes(Transport.Model);
        foreach (var name in new[]
                 {
                     "hull", "wheel_fl_steer", "wheel_fr_steer", "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr", "propeller", "gun_mount", "gun", "barrel", "muzzle",
                     "gunner_sight", "seat_driver", "seat_passenger", "seat_gunner", "light_head_l", "light_head_r", "light_tail_l", "light_tail_r", "smoke_engine",
                     "wake_bow", "wake_stern_l", "wake_stern_r",
                 })
            Assert.True(n.ContainsKey(name), $"the model has no node {name}");
        // the wheels turn about their centres, the steering pivots are at the front wheel centres, and all four stand on the ground
        Assert.Equal(n["wheel_fl_steer"], n["wheel_fl"]);
        Assert.Equal(n["wheel_fr_steer"], n["wheel_fr"]);
        foreach (var w in new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" }) Assert.InRange(n[w].Y, 0.3f, 0.5f);
        Assert.True(n["wheel_fl"].Z < 0 && n["wheel_rl"].Z > 0 && n["wheel_fl"].X < 0 && n["wheel_fr"].X > 0);
        // the propeller is at the stern and low, the muzzle ahead of the barrel root, the sight behind and above the gun's pivot
        Assert.True(n["propeller"].Z > 2f && n["propeller"].Y < 0.6f);
        Assert.True(n["muzzle"].Z < n["barrel"].Z && n["barrel"].Z < n["gun"].Z);
        Assert.True(n["gunner_sight"].Z > n["gun"].Z && n["gunner_sight"].Y > n["gun"].Y);
        // the gun's yaw axis is the ring's centre, and the gunner stands on it
        Assert.Equal(n["gun_mount"].X, n["seat_gunner"].X, 3);
        Assert.Equal(n["gun_mount"].Z, n["seat_gunner"].Z, 3);
    }

    [Fact]
    public void The_seats_lamps_and_smoke_point_of_vehicles_json_are_the_models()
    {
        var v = Transport;
        var n = Nodes(v.Model);
        Assert.Equal(3, v.Seats.Count);
        Near(n["seat_driver"], v.Seats[0].Offset);
        Near(n["seat_passenger"], v.Seats[1].Offset);
        Near(n["seat_gunner"], v.Seats[2].Offset);
        Assert.True(v.Seats[0].Offset[0] < 0 && v.Seats[0].Offset[2] < 0, "the driver sits front left");
        Assert.True(v.Seats[1].Offset[0] > 0 && v.Seats[1].Offset[2] < 0, "the passenger sits beside the driver");
        Assert.True(v.Seats[2].Offset[2] > 0.5f, "the gunner is in the rear");
        Near(n["light_head_l"], v.Lights!.Head[0]);
        Near(n["light_head_r"], v.Lights.Head[1]);
        Near(n["light_tail_l"], v.Lights.Tail[0]);
        Near(n["light_tail_r"], v.Lights.Tail[1]);
        Near(n["smoke_engine"], v.Hood);
        Assert.Equal(1f, v.ModelScale);
        Assert.Equal(0f, v.ModelYawDeg);
    }

    [Fact]
    public void The_hit_box_is_the_hull_and_the_riders_stand_out_of_it()
    {
        var v = Transport;
        var n = Nodes(v.Model);
        // the footprint covers the wheels and the hull from the stem to the propeller
        Assert.InRange(v.HalfWidth, MathF.Abs(n["wheel_fl"].X) + 0.12f, MathF.Abs(n["wheel_fl"].X) + 0.25f);
        Assert.True(v.HalfLength >= n["propeller"].Z && v.HalfLength >= -n["wake_bow"].Z && v.HalfLength <= 2.5f);
        // its top is the top of the low sides and the decks: a seated rider's head and chest are above it, so they can be seen and shot
        Assert.InRange(v.Height, 1.1f, 1.3f);
        foreach (var seat in v.Seats.Take(2))
            Assert.True(seat.Offset[1] + MovementStep.CapsuleHeight(Stance.Crouch) > v.Height + 0.4f, "a seated rider stands at least 0.4 m out of the hull");
        // it is about the size of the jeep, and a little lower
        var jeep = Shipped.Vehicles.Single(x => x.Id == "jeep");
        Assert.InRange(v.HalfLength / jeep.HalfLength, 0.9f, 1.1f);
        Assert.InRange(v.HalfWidth / jeep.HalfWidth, 0.8f, 1.1f);
        Assert.True(v.Height < jeep.Height);
    }
}
