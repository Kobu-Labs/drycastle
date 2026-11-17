using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Per-seat views (#339): the eye of every seat, the Rider.View machine and its defaults. Strict: an undeclared edge throws (StateMachineTests.Init).</summary>
public class SeatViewTests
{
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Def(string id) => Shipped.Vehicles.First(v => v.Id == id);

    static void Near(Vector3 want, Vector3 got) => Assert.True((want - got).Length() < 1e-4f, $"want {want} got {got}");

    [Fact]
    public void The_drivers_eyes_of_the_hand_built_cockpits_are_the_ones_the_rigs_had()
    {
        Near(new(-0.62f, 1.65f, -0.13f), SeatEye.For(Def("humvee"), 0)); // the Humvee must not change
        Near(new(-0.4875f, 1.50f, 0.30f), SeatEye.For(Def("golf_mk2"), 0));
        Near(new(-0.70f, 1.60f, -4.75f), SeatEye.For(Def("bus"), 0));
        Near(new(-0.50f, 2.05f, -1.25f), SeatEye.For(Def("flak"), 0));
        Near(new(-0.79f, 3.0f, -0.55f), SeatEye.For(Def("harvester"), 0));
        Near(new(-0.892f, 2.2f, -4.238f), SeatEye.For(Def("scud"), 0));
        Near(new(0.55f, 1.78f, -1.80f), SeatEye.For(Def("utility_helicopter"), 0));
    }

    [Fact]
    public void A_seat_without_an_override_has_its_feet_plus_eye_height_or_the_standing_eye()
    {
        Near(new(0.62f, 0.6f + 1.05f, -0.22f), SeatEye.For(Def("humvee"), 1)); // eye_height 1.05
        Near(new(0.53f, 0.38f + 1.05f, -2.175f), SeatEye.For(Def("bus"), 1)); // eye_height 1.05: the seated rider's head, not the standing eye (2.03, a head over the seat backs)
        Near(new(0.4875f, 1.50f, 0.2125f), SeatEye.For(Def("golf_mk2"), 1));
        Near(new(-0.387f, 0.374f + 1.0f, 0.244f), SeatEye.For(Def("car"), 0));
        Near(new(0.892f, 1.15f + 1.05f, -4.328f), SeatEye.For(Def("scud"), 1)); // the operator
    }

    [Fact]
    public void A_seat_in_the_open_has_the_riders_head()
    {
        Near(new(0f, 0.436f + MoveConsts.CrouchEyeHeight, 0.477f), SeatEye.For(Def("motorcycle"), 0));
        Near(new(0f, 0.419f + MoveConsts.CrouchEyeHeight, 0.168f), SeatEye.For(Def("quad"), 0));
        Near(new(-0.45f, 0.64f + MoveConsts.CrouchEyeHeight, -0.37f), SeatEye.For(Def("amphibious"), 0));
        Near(new(0f, 0.685f + MoveConsts.EyeHeight, 1.0f), SeatEye.For(Def("amphibious"), 2)); // standing in the ring
    }

    [Fact]
    public void Every_seat_of_every_vehicle_has_an_eye_above_its_feet()
    {
        foreach (var v in Shipped.Vehicles)
            for (var i = 0; i < v.Seats.Count; i++)
                Assert.True(SeatEye.For(v, i).Y > v.Seats[i].Offset[1], $"{v.Id} seat {i}");
    }

    [Fact]
    public void Gun_armed_and_operator_seats_are_sights_the_rest_switch()
    {
        Assert.Equal(SeatViewKind.Covered, RiderViewMode.KindOf(Def("humvee"), 0));
        Assert.Equal(SeatViewKind.Covered, RiderViewMode.KindOf(Def("humvee"), 2));
        Assert.Equal(SeatViewKind.Sight, RiderViewMode.KindOf(Def("humvee"), 4));   // the roof gun
        Assert.Equal(SeatViewKind.Sight, RiderViewMode.KindOf(Def("flak"), 1));     // the flak gun
        Assert.Equal(SeatViewKind.Sight, RiderViewMode.KindOf(Def("utility_helicopter"), 1)); // the door gun
        Assert.Equal(SeatViewKind.Sight, RiderViewMode.KindOf(Def("amphibious"), 1)); // armed
        Assert.Equal(SeatViewKind.Sight, RiderViewMode.KindOf(Def("scud"), 1));     // the operator's screen
        Assert.Equal(SeatViewKind.Sight, RiderViewMode.KindOf(Def("radar_unit"), 0)); // the radar operator's screen
        Assert.Equal(SeatViewKind.Open, RiderViewMode.KindOf(Def("motorcycle"), 0));
        Assert.Equal(SeatViewKind.Open, RiderViewMode.KindOf(Def("amphibious"), 0));
        Assert.Equal(SeatViewKind.Covered, RiderViewMode.KindOf(Def("scud"), 0));
    }

    [Fact]
    public void Defaults_a_driver_chases_a_passenger_looks_from_the_seat_a_rider_in_the_open_chases_a_gun_has_its_sight()
    {
        var m = new RiderViewMode();
        m.Board(Def("car"), 0); Assert.Equal(RiderView.Chase, m.Current);
        m.Leave(); m.Board(Def("car"), 1); Assert.Equal(RiderView.Cockpit, m.Current);
        m.Leave(); m.Board(Def("motorcycle"), 0); Assert.Equal(RiderView.Chase, m.Current);
        m.Leave(); m.Board(Def("humvee"), 4); Assert.Equal(RiderView.Sight, m.Current);
        Assert.False(m.Switchable);
        Assert.True(m.FirstPerson);
    }

    [Fact]
    public void The_view_key_flips_chase_and_cockpit_and_the_choice_outlives_the_vehicle()
    {
        var m = new RiderViewMode();
        m.Board(Def("car"), 0);
        Assert.True(m.Toggle()); Assert.Equal(RiderView.Cockpit, m.Current);
        m.Leave();
        m.Board(Def("bus"), 0); Assert.Equal(RiderView.Cockpit, m.Current); // chosen last: cockpit stays
        m.Leave();
        m.Board(Def("quad"), 0); Assert.Equal(RiderView.Chase, m.Current);  // the open seat starts in chase anyway
        Assert.True(m.Toggle()); Assert.Equal(RiderView.Cockpit, m.Current); // and can be looked from the head
        Assert.True(m.Toggle()); Assert.Equal(RiderView.Chase, m.Current);
        m.Leave();
        m.Board(Def("car"), 1); Assert.Equal(RiderView.Chase, m.Current);   // chased last: a passenger chases too
    }

    [Fact]
    public void A_seat_switch_keeps_the_mode_and_a_sight_seat_gives_it_back()
    {
        var humvee = Def("humvee");
        var m = new RiderViewMode();
        m.Board(humvee, 0); m.Toggle(); Assert.Equal(RiderView.Cockpit, m.Current);
        m.SwitchSeat(humvee, 1); Assert.Equal(RiderView.Cockpit, m.Current);
        m.SwitchSeat(humvee, 4); Assert.Equal(RiderView.Sight, m.Current);
        Assert.False(m.Toggle()); Assert.Equal(RiderView.Sight, m.Current);
        m.SwitchSeat(humvee, 0); Assert.Equal(RiderView.Cockpit, m.Current);
        m.Toggle(); m.SwitchSeat(humvee, 2); Assert.Equal(RiderView.Chase, m.Current); // chase is kept as well
        m.SwitchSeat(humvee, 4); m.SwitchSeat(humvee, 3); Assert.Equal(RiderView.Chase, m.Current);
    }

    [Fact]
    public void Chase_and_cockpit_declare_both_ways_and_the_machine_is_in_the_catalog()
    {
        Assert.True(RiderViewMode.Spec.Allows(RiderView.Chase, RiderView.Cockpit));
        Assert.True(RiderViewMode.Spec.Allows(RiderView.Cockpit, RiderView.Chase));
        Assert.Contains(StateMachineCatalog.All, s => s.Name == "Rider.View");
    }
}
