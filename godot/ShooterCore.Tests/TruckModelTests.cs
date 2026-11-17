using System.Text.Json;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The `jeep` type is the Synty cargo truck SM_Veh_Truck_01 (#325): what is particular to it (the generic part is StandardizedVehicleTests and VehicleContractTests).</summary>
public class TruckModelTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Truck => Shipped.Vehicles.Single(v => v.Id == "jeep");

    static JsonElement Facts()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "docs", "research", "vehicle-facts.d", "truck.json"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "docs/research/vehicle-facts.d/truck.json not found");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "docs", "research", "vehicle-facts.d", "truck.json"))).RootElement;
    }

    [Fact]
    public void It_is_drawn_at_real_size_with_no_gun_and_no_gunner_and_three_seats_on_the_bench_eyes_inside_the_cab()
    {
        Assert.Equal(new[] { "driver", "passenger", "passenger" }, Truck.Seats.Select(s => s.Role));
        Assert.Null(Truck.Gun);
        Assert.Null(Truck.DoorGun);
        Assert.False(Truck.ExposedGunner);
        Assert.Equal((1f, 0f), (Truck.ModelScale, Truck.ModelYawDeg));
        Assert.True(Truck.Seats[0].Offset[0] < 0, "the driver sits on the left (-x)");
        foreach (var s in Truck.Seats)
        {
            Assert.InRange(s.EyeHeight!.Value, 0.9f, 1.1f);
            Assert.InRange(s.Offset[1] + s.EyeHeight.Value, 2.4f, Truck.Height - 0.3f); // the eye is in the window band, under the roof
        }
        Assert.Equal(5, Truck.Glass.Count); // windscreen, two cab-back panes, two sides
        Assert.Equal(2, Truck.Glass.Count(g => g.Id.StartsWith("rear")));
    }

    [Fact]
    public void It_has_six_wheels_on_three_axles_and_only_the_front_pair_steers()
    {
        var nodes = Facts().GetProperty("nodes").EnumerateArray().Select(n => n.GetString()!).ToHashSet();
        foreach (var w in new[] { "l1", "r1", "l2", "r2", "l3", "r3" }) Assert.Contains($"wheel_{w}", nodes);
        Assert.Contains("wheel_l1_steer", nodes);
        Assert.Contains("wheel_r1_steer", nodes);
        Assert.Equal(2, nodes.Count(n => n.StartsWith("wheel_") && n.EndsWith("_steer")));
        Assert.Contains("tow_hitch", nodes);
        var centres = Facts().GetProperty("wheels").GetProperty("centres");
        Assert.Equal(6, centres.EnumerateObject().Count());
        foreach (var w in centres.EnumerateObject()) Assert.Equal(w.Name.EndsWith("1"), w.Value.GetProperty("steer").GetBoolean());
    }

    [Fact]
    public void Its_hitch_is_behind_the_footprint_at_the_rocket_carts_eye_height_and_the_cart_starts_clear_of_it()
    {
        var cart = Shipped.Vehicles.Single(v => v.Id == "rocket_cart");
        Assert.Equal(cart.Trailer!.Eye[1], Truck.Tow!.Hitch[1], 3);
        Assert.True(Truck.Tow.Hitch[2] > Truck.HalfLength + 0.1f, "behind the bed tail and clear of the footprint");
        Assert.True(TowGeometry.CanTow(Truck, cart));
    }
}
