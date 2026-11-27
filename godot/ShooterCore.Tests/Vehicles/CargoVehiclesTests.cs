using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Vehicles;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Vehicles;

/// <summary>
/// The cargo vehicles of the Delivery rework (docs/cartel.md "Cargo vehicles", vehicles.json cargo_volume): which types take Duffels and how many (the owner's numbers), the open bed, the slots, access point and
/// doors a type declares, and the rules that refuse a bad block. The per-vehicle geometry (slots inside the volume and clear of each other, access behind the cargo end) is <see cref="VehicleContractTests"/>.
/// </summary>
public class CargoVehiclesTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Of(string id) => Shipped.Vehicles.Single(v => v.Id == id);

    [Theory]
    [InlineData("car", 1)]
    [InlineData("golf_mk2", 2)]
    [InlineData("truck", 8)]
    [InlineData("cartel_van", 12)]
    public void A_cargo_type_takes_the_number_of_Duffels_the_owner_set(string id, int capacity)
    {
        var c = Of(id).CargoVolume;
        Assert.NotNull(c);
        Assert.Equal(capacity, c!.Capacity);
        Assert.Equal(capacity, c.Slots.Count);
    }

    [Fact]
    public void No_other_vehicle_takes_Duffels()
    {
        Assert.Equal(new[] { "car", "golf_mk2", "truck", "cartel_van" }.Order(), Shipped.Vehicles.Where(v => v.CargoVolume != null).Select(v => v.Id).Order());
    }

    [Fact]
    public void The_truck_bed_is_open_the_others_have_doors()
    {
        var truck = Of("truck").CargoVolume!;
        Assert.True(truck.FromTop);
        Assert.Empty(truck.Doors);
        foreach (var id in new[] { "car", "golf_mk2", "cartel_van" })
        {
            var c = Of(id).CargoVolume!;
            Assert.False(c.FromTop, id);
            Assert.NotEmpty(c.Doors);
            Assert.All(c.Doors, d => Assert.Null(d.Problem()));
        }
        Assert.Equal(new[] { "cargo_door_l", "cargo_door_r" }, Of("cartel_van").CargoVolume!.Doors.Select(d => d.Node));
    }

    [Fact]
    public void A_slot_is_the_centre_of_the_base_and_a_turn_in_the_vehicle_frame()
    {
        var c = Of("truck").CargoVolume!;
        var s = c.Slot(0)!.Value;
        Assert.Equal(new Vector3(c.Slots[0][0], c.Slots[0][1], c.Slots[0][2]), s.Position);
        Assert.Equal(c.Slots[0][3], s.YawDeg);
        Assert.Null(c.Slot(c.Capacity));
        Assert.Null(c.Slot(-1));
    }

    [Fact]
    public void A_door_swings_open_in_a_fraction_of_a_second_eases_at_both_ends_and_turns_back_from_where_it_is()
    {
        var d = new CargoDoorSwing();
        Assert.False(d.Moving);
        Assert.False(d.Step(0.1f));
        d.SetOpen(true);
        Assert.True(d.Step(CargoDoorSwing.Seconds / 2));
        Assert.Equal(0.5f, d.Open, 3);
        Assert.Equal(0.5f, d.Eased, 3); // smoothstep is 0.5 at the middle
        Assert.True(CargoDoorSwing.Seconds is > 0.3f and < 0.5f);
        d.Step(CargoDoorSwing.Seconds / 10);
        d.SetOpen(false); // reversed at 0.6: it comes back from there
        d.Step(CargoDoorSwing.Seconds / 10);
        Assert.Equal(0.5f, d.Open, 3);
        d.Step(10);
        Assert.Equal(0f, d.Open);
        Assert.False(d.Moving);
        d.SetOpen(true);
        d.Step(10);
        Assert.Equal(1f, d.Open);
        Assert.Equal(1f, d.Eased);
        Assert.False(d.Step(1)); // standing open: nothing moves
        d.SetOpen(false, instant: true);
        Assert.Equal(0f, d.Open);
        Assert.False(d.Moving);
    }

    static VehicleDef Truckish(Action<VehicleCargoVolumeDef> edit)
    {
        var v = new VehicleDef { Id = "t", HalfWidth = 1.5f, HalfLength = 3.5f, Height = 3f };
        var c = new VehicleCargoVolumeDef { Centre = new[] { 0f, 1.5f, 1f }, Size = new[] { 2f, 1f, 3f }, Capacity = 2, Slots = { new[] { -0.5f, 1f, 1f, 0f }, new[] { 0.5f, 1f, 1f, 90f } }, Access = new[] { 0f, 0f, 4f } };
        edit(c);
        v.CargoVolume = c;
        return v;
    }

    [Fact]
    public void A_well_formed_block_passes_and_every_part_of_it_can_be_refused()
    {
        Assert.Null(Truckish(_ => { }).CargoVolume!.Problem(Truckish(_ => { })));
        void Refused(Action<VehicleCargoVolumeDef> edit, string why)
        {
            var v = Truckish(edit);
            Assert.True(v.CargoVolume!.Problem(v) is { } p && p.Contains(why), $"{why}: {v.CargoVolume.Problem(v)}");
        }
        Refused(c => c.Slots.RemoveAt(1), "slots: 1 for a capacity of 2");
        Refused(c => c.Slots[0] = new[] { 0f, 1f, 1f }, "slots[0] is [x, y, z, yaw_deg]");
        Refused(c => c.Slots[1] = new[] { 0.5f, 1f, 5f, 0f }, "slots[1] lies outside the volume");
        Refused(c => c.Slots[1] = new[] { 0.5f, 0.2f, 1f, 0f }, "slots[1] lies outside the volume");
        Refused(c => c.Access = new[] { 0f, 0f }, "access is [x, y, z]");
        Refused(c => c.AccessRadiusM = 0, "access_radius_m");
        Refused(c => c.Doors.Add(new VehicleCargoDoorDef { Node = "" }), "a door needs a node");
        Refused(c => c.Doors.Add(new VehicleCargoDoorDef { Node = "d", Axis = new[] { 0f, 0f, 0f } }), "axis");
        Refused(c => c.Doors.Add(new VehicleCargoDoorDef { Node = "d", OpenDeg = 0 }), "open_deg");
        Refused(c => c.Doors.Add(new VehicleCargoDoorDef { Node = "d", OpenDeg = 200 }), "open_deg");
    }

    [Fact]
    public void The_new_keys_are_read_from_the_json()
    {
        string Json(string cargo) => "{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 0, "
            + "\"half_width\": 1, \"half_length\": 2.5, \"height\": 2.2, \"cargo_volume\": " + cargo + " } ] }";
        var c = VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(Json(
            "{ \"centre\": [0, 1.2, 1], \"size\": [1.6, 1.0, 2.0], \"capacity\": 1, \"from_top\": true, \"slots\": [[0, 0.8, 1, 90]], \"access\": [0, 0, 3], \"access_radius_m\": 1.2, "
            + "\"doors\": [{ \"node\": \"cargo_door\", \"axis\": [-1, 0, 0], \"open_deg\": 100 }] }"), "vehicles.json")).Vehicles[0].CargoVolume!;
        Assert.True(c.FromTop);
        Assert.Equal(new[] { 0f, 0.8f, 1f, 90f }, c.Slots[0]);
        Assert.Equal(new[] { 0f, 0f, 3f }, c.Access);
        Assert.Equal(1.2f, c.AccessRadiusM);
        var d = Assert.Single(c.Doors);
        Assert.Equal(("cargo_door", 100f), (d.Node, d.OpenDeg));
        Assert.Equal(new[] { -1f, 0f, 0f }, d.Axis);
    }
}
