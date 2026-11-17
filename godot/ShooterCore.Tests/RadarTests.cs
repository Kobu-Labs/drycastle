using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The radar unit (#371, ADR 0037): the shipped type, the sweep (stamps at the moment the arm passes, staleness, hitched only), who it shows (the UAV's filter, vehicles, teammates live) and the wire message.</summary>
public class RadarTests
{
    const float Dt = 1f / Wire.TickRate;
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Unit => Shipped.Vehicles.Single(v => v.Id == "radar_unit");

    static RadarBlip Blip(ushort id, float x, float z, RadarKind k = RadarKind.Player) => new(id, k, x, z);

    static List<RadarContact> Found(RadarSweep s) { var l = new List<RadarContact>(); s.Collect(l); return l; }

    /// <summary>Runs the sweep for <paramref name="seconds"/> of ticks over a fixed set of contacts, the unit at the origin.</summary>
    static void Run(RadarSweep s, float seconds, params RadarBlip[] contacts)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Dt) s.Tick(Dt, 0, 0, contacts);
    }

    // ---- the shipped type ----

    [Fact]
    public void The_radar_unit_is_a_trailer_with_one_covered_operator_seat_that_is_not_dealt()
    {
        var u = Unit;
        Assert.NotNull(u.Trailer);
        Assert.False(u.Trailer!.HasCargo);
        Assert.Null(u.Tow);
        Assert.Single(u.Seats);
        Assert.Equal(VehicleSeatDef.RadarOperator, u.Seats[0].Role);
        Assert.Equal(SeatCover.Glass, u.DeclaredCover(0));
        Assert.Equal(RadarSweep.DefaultRevolution, u.Radar!.RevolutionSeconds);
        Assert.Equal(0, u.SpawnCount);
        Assert.False(u.Returns);
        Assert.Single(u.FixedSpots);
        Assert.StartsWith("pack:", u.Model);
        Assert.True(TowGeometry.CanTow(Shipped.Vehicles.Single(v => v.Id == "humvee"), u));
        Assert.True(Shipped.Vehicles.IndexOf(u) < 16, "the kind is one nibble of the vehicle record's weapon byte");
    }

    [Fact]
    public void The_radar_unit_stands_at_the_military_base_a_few_tens_of_metres_from_the_helicopter()
    {
        var heli = Shipped.Vehicles.Single(v => v.Id == "utility_helicopter").FixedSpots.Single();
        var spot = Unit.FixedSpots.Single();
        var d = MathF.Sqrt((spot.X - heli.X) * (spot.X - heli.X) + (spot.Z - heli.Z) * (spot.Z - heli.Z));
        Assert.InRange(d, 15f, 60f);
    }

    [Fact]
    public void A_radar_block_needs_a_trailer_with_exactly_the_operator_seat_and_the_role_needs_the_block()
    {
        Assert.Null(new VehicleRadarDef().Validate(Unit));
        var noSeat = Load(v => v.Seats.Clear());
        Assert.NotNull(new VehicleRadarDef().Validate(noSeat));
        var slow = new VehicleRadarDef { RevolutionSeconds = 0.2f };
        Assert.NotNull(slow.Validate(Unit));
        var car = Shipped.Vehicles.Single(v => v.Id == "car");
        Assert.NotNull(new VehicleRadarDef().Validate(car)); // not a trailer
    }

    static VehicleDef Load(Action<VehicleDef> edit)
    {
        var v = VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json")).Vehicles.Single(x => x.Id == "radar_unit");
        edit(v);
        return v;
    }

    [Fact]
    public void The_sweep_machine_is_declared_and_hitching_starts_it_and_unhitching_forgets_everything()
    {
        Assert.Equal("RadarUnit.Sweep", RadarSweep.Spec.Name);
        var s = new RadarSweep();
        Assert.False(s.Sweeping);
        Run(s, 3, Blip(1, 50, 0)); // still dish: nothing happens
        Assert.Empty(Found(s));
        Assert.True(s.Power(true));
        Assert.False(s.Power(true));
        Assert.True(s.Sweeping);
        Run(s, 2, Blip(1, 50, 0));
        Assert.Single(Found(s));
        Assert.True(s.Power(false));
        Assert.False(s.Sweeping);
        Assert.Empty(Found(s));
        Assert.Equal(0f, s.Angle);
    }

    // ---- the sweep ----

    [Fact]
    public void A_revolution_takes_six_seconds_and_the_arm_runs_clockwise_from_north()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 1.5f);
        Assert.Equal(MathF.PI / 2, s.Angle, 0.03f); // a quarter turn: east
        Run(s, 3f);
        Assert.Equal(MathF.PI * 1.5f, s.Angle, 0.03f); // 4.5 s: west
        Run(s, 1.5f);
        Assert.True(s.Angle < 0.03f || s.Angle > MathF.Tau - 0.03f); // 6 s: back at north
    }

    [Fact]
    public void The_bearing_is_zero_along_minus_z_and_clockwise()
    {
        Assert.Equal(0f, RadarSweep.Fraction(0, -10), 1e-5f);
        Assert.Equal(0.25f, RadarSweep.Fraction(10, 0), 1e-5f); // east
        Assert.Equal(0.5f, RadarSweep.Fraction(0, 10), 1e-5f);  // south
        Assert.Equal(0.75f, RadarSweep.Fraction(-10, 0), 1e-5f); // west
    }

    [Fact]
    public void A_contact_is_stamped_when_the_arm_passes_it_and_not_before()
    {
        var s = new RadarSweep();
        s.Power(true);
        var east = Blip(1, 300, 0); // a quarter turn: 1.5 s
        Run(s, 1.4f, east);
        Assert.Empty(Found(s));
        Run(s, 0.2f, east); // 1.6 s
        var f = Assert.Single(Found(s));
        Assert.Equal(1, f.Id);
        Assert.InRange(f.Age, 0.05f, 0.15f); // stamped at 1.5 s, it is 0.1 s old
    }

    [Fact]
    public void It_reaches_the_whole_map_not_a_radius()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 6, Blip(1, 2900, -2900), Blip(2, -3000, 3000), Blip(3, 4, 4));
        Assert.Equal(new ushort[] { 1, 2, 3 }, Found(s).Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Between_passes_a_contact_stays_where_it_was_stamped()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 1.6f, Blip(1, 300, 0)); // stamped at (300, 0)
        // it drives away to the north-east, still east of the unit's east, but the arm is far past
        Run(s, 1.0f, Blip(1, 300, -200));
        var f = Assert.Single(Found(s));
        Assert.Equal(300f, f.X); Assert.Equal(0f, f.Z);
        Assert.InRange(f.Age, 1.0f, 1.2f);
    }

    [Fact]
    public void The_next_pass_moves_the_stamp_and_a_contact_not_found_again_is_dropped_after_a_revolution()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 1.6f, Blip(1, 300, 0));
        Run(s, 4.8f, Blip(1, 300, -200)); // 6.4 s in: the arm is at 0.4 s of its second turn, bearing (300,-200) is at 0.93 s: not yet passed
        Run(s, 1.0f, Blip(1, 300, -200));
        var f = Assert.Single(Found(s));
        Assert.Equal(-200f, f.Z); // re-stamped by the second pass
        // it disappears (dead, hidden): nothing re-stamps it, so after a revolution it is gone
        Run(s, 6.5f);
        Assert.Empty(Found(s));
    }

    [Fact]
    public void No_stamp_is_older_than_a_revolution()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 13, Blip(1, 100, 100), Blip(2, -100, 100), Blip(3, 0, -100), Blip(4, -5, -100));
        Assert.Equal(4, Found(s).Count);
        Assert.All(Found(s), c => Assert.InRange(c.Age, 0f, RadarSweep.DefaultRevolution + 1e-3f));
    }

    [Fact]
    public void The_arm_turns_about_the_unit_wherever_it_stands()
    {
        var s = new RadarSweep();
        s.Power(true);
        var blip = new[] { Blip(1, 1100, 500) }; // east of a unit at (1000, 500): a quarter turn, 1.5 s
        for (var t = 0f; t < 1.4f; t += Dt) s.Tick(Dt, 1000, 500, blip);
        Assert.Empty(Found(s));
        for (var t = 0f; t < 0.2f; t += Dt) s.Tick(Dt, 1000, 500, blip);
        Assert.Single(Found(s));
    }

    [Fact]
    public void A_big_step_across_the_end_of_a_revolution_stamps_what_it_passes_and_keeps_the_phase()
    {
        var s = new RadarSweep();
        s.Power(true);
        var fine = new RadarSweep(); fine.Power(true);
        var c = new[] { Blip(1, 0, -100), Blip(2, 0, 100), Blip(3, -100, 0) };
        s.Tick(5.5f, 0, 0, c); // 5.5 s: past south and west, not yet north
        Assert.Equal(new ushort[] { 2, 3 }, Found(s).Select(x => x.Id).ToArray());
        s.Tick(1.0f, 0, 0, c); // crosses the end at 6 s: north is passed, and the phase is 0.5 s
        Assert.Equal(3, Found(s).Count);
        Assert.Equal(MathF.Tau * 0.5f / 6f, s.Angle, 1e-3f);
    }

    [Fact]
    public void A_contact_due_north_is_stamped_as_the_revolution_ends()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 5.9f, Blip(1, 0, -100));
        Assert.Empty(Found(s));
        Run(s, 0.2f, Blip(1, 0, -100));
        Assert.Single(Found(s));
    }

    [Fact]
    public void Clearing_forgets_the_stamps_and_the_arm_keeps_turning()
    {
        var s = new RadarSweep();
        s.Power(true);
        Run(s, 6, Blip(1, 100, 100));
        Assert.NotEmpty(Found(s));
        var before = s.Angle;
        s.Clear();
        Assert.Empty(Found(s));
        Run(s, 0.5f);
        Assert.NotEqual(before, s.Angle);
    }

    [Fact]
    public void A_configured_revolution_time_is_honoured()
    {
        var s = new RadarSweep(3f);
        s.Power(true);
        Run(s, 0.75f);
        Assert.Equal(MathF.PI / 2, s.Angle, 0.05f);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarSweep(0f));
    }

    // ---- who is shown ----

    const ushort Owner = 1;
    static UavContact P(ushort id, float x, float z, byte team = Teams.None, float vis = 1) => new(id, x, z, vis, team);

    static (List<RadarBlip> Enemies, List<RadarBlip> Friends) Split(UavContact[] alive, RadarVehicle[] vehicles)
    {
        var e = new List<RadarBlip>(); var f = new List<RadarBlip>();
        RadarRules.Split(alive, vehicles, Owner, e, f);
        return (e, f);
    }

    [Fact]
    public void On_foot_the_filter_is_the_UAVs_everyone_but_the_owner_and_the_ghost()
    {
        var alive = new[] { P(Owner, 0, 0), P(2, 10, 0), P(3, 20, 0, vis: 0), P(4, 30, 0) };
        var (e, f) = Split(alive, Array.Empty<RadarVehicle>());
        Assert.Equal(new ushort[] { 2, 4 }, e.Select(b => b.Id).ToArray());
        Assert.All(e, b => Assert.Equal(RadarKind.Player, b.Kind));
        Assert.Empty(f); // free-for-all has no friends
        // the same ids the UAV would show
        Assert.Equal(UavRules.Visible(alive, Owner).Count, e.Count);
    }

    [Fact]
    public void Target_dummies_are_contacts_like_the_UAV_shows_them()
    {
        var (e, _) = Split(new[] { P(Owner, 0, 0), P(900, 5, 5) }, Array.Empty<RadarVehicle>());
        Assert.Equal(new ushort[] { 900 }, e.Select(b => b.Id).ToArray());
    }

    [Fact]
    public void A_rider_is_the_vehicle_not_a_person_on_foot()
    {
        var alive = new[] { P(Owner, 0, 0), P(2, 100, 100), P(3, 100, 100), P(4, 50, 0) };
        var cars = new[] { new RadarVehicle(0x4001, 100, 100, new ushort[] { 2, 3 }) };
        var (e, _) = Split(alive, cars);
        Assert.Equal(new ushort[] { 4, 0x4001 }, e.Select(b => b.Id).ToArray());
        Assert.Equal(RadarKind.Vehicle, e.Single(b => b.Id == 0x4001).Kind);
    }

    [Fact]
    public void A_vehicle_is_hidden_if_any_enemy_aboard_has_Ghost()
    {
        var alive = new[] { P(Owner, 0, 0), P(2, 100, 100), P(3, 100, 100, vis: 0) };
        var cars = new[] { new RadarVehicle(0x4001, 100, 100, new ushort[] { 2, 3 }) };
        var (e, _) = Split(alive, cars);
        Assert.Empty(e);
    }

    [Fact]
    public void An_empty_vehicle_shows_nothing_and_the_operators_own_vehicle_is_not_an_enemy()
    {
        var alive = new[] { P(Owner, 0, 0) };
        var (e, f) = Split(alive, new[] { new RadarVehicle(0x4002, 0, 0, new ushort[] { Owner }) });
        Assert.Empty(e); Assert.Empty(f);
    }

    [Fact]
    public void Teammates_are_live_friendlies_ghost_or_not_and_never_swept()
    {
        var alive = new[] { P(Owner, 0, 0, Teams.A), P(2, 10, 0, Teams.A), P(3, 20, 0, Teams.A, vis: 0), P(4, 30, 0, Teams.B), P(5, 40, 0, Teams.B, vis: 0) };
        var (e, f) = Split(alive, Array.Empty<RadarVehicle>());
        Assert.Equal(new ushort[] { 4 }, e.Select(b => b.Id).ToArray());
        Assert.Equal(new ushort[] { 2, 3 }, f.Select(b => b.Id).ToArray());
    }

    [Fact]
    public void A_vehicle_with_a_teammate_is_a_friendly_vehicle_and_one_with_both_is_both()
    {
        var alive = new[] { P(Owner, 0, 0, Teams.A), P(2, 10, 0, Teams.A), P(3, 20, 0, Teams.B), P(4, 30, 0, Teams.A) };
        var cars = new[] { new RadarVehicle(0x4001, 10, 0, new ushort[] { 2 }), new RadarVehicle(0x4002, 20, 0, new ushort[] { 3, 4 }) };
        var (e, f) = Split(alive, cars);
        Assert.Equal(new ushort[] { 0x4002 }, e.Select(b => b.Id).ToArray());
        Assert.Equal(new ushort[] { 0x4001, 0x4002 }, f.Select(b => b.Id).ToArray());
        Assert.All(f, b => Assert.Equal(RadarKind.Vehicle, b.Kind));
    }

    // ---- the wire ----

    [Fact]
    public void The_sweep_message_round_trips_and_ages_are_in_fortieths_of_a_second()
    {
        var msg = new RadarSweepMsg(0x4005, 1.25f, 6f,
            new[] { new RadarContact(7, RadarKind.Player, 123.5f, -88.25f, 2.5f), new RadarContact(0x4001, RadarKind.Vehicle, -5f, 600f, 5.99f) },
            new[] { new RadarBlip(9, RadarKind.Player, 1f, 2f), new RadarBlip(0x4002, RadarKind.Vehicle, 3f, 4f) });
        var bytes = RadarCodec.Encode(msg);
        Assert.Equal(14 + 2 * 12 + 2 * 11, bytes.Length);
        var back = RadarCodec.Decode(bytes)!;
        Assert.Equal(msg.Unit, back.Unit);
        Assert.Equal(msg.Angle, back.Angle);
        Assert.Equal(msg.Revolution, back.Revolution);
        Assert.Equal(msg.Friendlies, back.Friendlies);
        Assert.Equal(2, back.Swept.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(msg.Swept[i].Id, back.Swept[i].Id); Assert.Equal(msg.Swept[i].Kind, back.Swept[i].Kind);
            Assert.Equal(msg.Swept[i].X, back.Swept[i].X); Assert.Equal(msg.Swept[i].Z, back.Swept[i].Z);
            Assert.Equal(msg.Swept[i].Age, back.Swept[i].Age, RadarCodec.AgeStep);
        }
    }

    [Fact]
    public void An_empty_sweep_message_and_a_broken_one()
    {
        var empty = RadarCodec.Decode(RadarCodec.Encode(new RadarSweepMsg(1, 0, 6, Array.Empty<RadarContact>(), Array.Empty<RadarBlip>())))!;
        Assert.Empty(empty.Swept); Assert.Empty(empty.Friendlies);
        Assert.Null(RadarCodec.Decode(new byte[5]));
        var full = RadarCodec.Encode(new RadarSweepMsg(1, 0, 6, new[] { new RadarContact(2, RadarKind.Player, 1, 2, 1) }, Array.Empty<RadarBlip>()));
        Assert.Null(RadarCodec.Decode(full.AsSpan(0, full.Length - 3)));
        full[14] = 9; // an unknown kind
        Assert.Null(RadarCodec.Decode(full));
    }

    [Fact]
    public void The_event_kind_is_91_and_no_other_event_shares_it()
    {
        Assert.Equal(91, (int)EventKind.RadarSweep);
        Assert.Single(Enum.GetValues<EventKind>(), k => (int)k == 91);
    }

    [Fact]
    public void The_whole_sweep_to_the_wire_for_a_unit_with_an_operator()
    {
        // what the server does each tick: split, tick, collect, encode
        var s = new RadarSweep(); s.Power(true);
        var alive = new[] { P(Owner, 0, 0), P(2, 200, 0), P(3, -50, 0, vis: 0) };
        var e = new List<RadarBlip>(); var f = new List<RadarBlip>();
        RadarRules.Split(alive, Array.Empty<RadarVehicle>(), Owner, e, f);
        for (var i = 0; i < 6 * Wire.TickRate; i++) s.Tick(Dt, 0, 0, e);
        var swept = new List<RadarContact>(); s.Collect(swept);
        var back = RadarCodec.Decode(RadarCodec.Encode(new RadarSweepMsg(0x4000, s.Angle, s.Revolution, swept, f)))!;
        Assert.Equal(new ushort[] { 2 }, back.Swept.Select(c => c.Id).ToArray()); // the Ghost is not there
        Assert.Empty(back.Friendlies);
    }
}
