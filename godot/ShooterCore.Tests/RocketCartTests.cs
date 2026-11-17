using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The rocket cart and what towing puts on the wire and on the client: the shipped type (no seats, data), zero-seat vehicles in the rules, the load and its hit points, the unhitch key, the trailer's
/// record (protocol 67) and the articulation read back from it, the pose a client draws from a tractor, the ground pitch, and the driver's prediction of a trailer (ADR 0014).
/// </summary>
public class RocketCartTests
{
    const float Dt = 1f / Wire.TickRate;
    static VehiclesFile Load() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static readonly VehiclesFile Shipped = Load();
    static VehicleDef Cart => Shipped.Vehicles.Single(v => v.Id == "rocket_cart");
    static VehicleDef Jeep => Shipped.Vehicles.Single(v => v.Id == "jeep");
    static VehicleDef Flak => Shipped.Vehicles.Single(v => v.Id == "flak");

    // ---- the shipped type ----

    [Fact]
    public void The_rocket_cart_is_a_seatless_trailer_that_is_not_dealt()
    {
        Assert.Empty(Cart.Seats);
        Assert.NotNull(Cart.Trailer);
        Assert.Null(Cart.Tow);
        Assert.Equal(0, Cart.SpawnCount);
        Assert.Empty(Cart.FixedSpots);
        Assert.True(Cart.Trailer!.HasCargo);
        Assert.Equal(150f, Cart.Trailer.LoadedHp);
        Assert.Equal("pack:synty-simple-military/rocket_cart", Cart.Model);
        Assert.Null(Cart.Lights); Assert.Null(Cart.Sounds); Assert.Empty(Cart.Glass);
        Assert.True(TowGeometry.CanTow(Jeep, Cart));
        Assert.True(TowGeometry.CanTow(Flak, Cart));
        Assert.True(TowGeometry.CanTow(Shipped.Vehicles.Single(v => v.Id == "humvee"), Cart));
        Assert.Equal(12.6f, Cart.HalfLength * 2, 2); // the model's 12.6 m including the drawbar (it carries the whole 11.1 m missile)
        Assert.Equal(2.46f, Cart.HalfWidth * 2, 2);
        Assert.Equal(7.6f, Cart.Trailer.Drawbar, 2);
        Assert.False(Cart.Returns); // the Scud event decides when a new cart stands at a bunker
        Assert.InRange(Cart.Trailer.Mass, 2500, 3500);
        Assert.InRange(Cart.Trailer.EmptyMass, 800, 1500);
    }

    [Fact]
    public void The_cart_fits_the_jeeps_and_the_flaks_hitch_without_starting_inside_them()
    {
        foreach (var tractor in new[] { Jeep, Flak, Shipped.Vehicles.Single(v => v.Id == "humvee") })
        {
            var g = TowGeometry.Of(tractor, Cart);
            var b = TowModel.Pose(g, Vector3.Zero, 0f, 0f);
            // in line, the cart's front edge (the drawbar's tip) is behind the tractor's back edge
            var frontEdge = b.Position.Z - Cart.HalfLength; // the tractor faces -Z: its back is +Z, and so is the cart's whole length
            Assert.True(MathF.Abs(TowModel.EyePoint(g, b).Z - g.HitchZ) < 1e-3f);
            Assert.True(frontEdge >= tractor.HalfLength - 1e-3f, $"{tractor.Id}: the cart starts behind the tractor ({frontEdge} against {tractor.HalfLength})");
        }
    }

    // ---- zero-seat vehicles ----

    [Fact]
    public void A_vehicle_with_no_seats_cannot_be_boarded_and_has_no_driver()
    {
        var seat = new VehicleSeat(0);
        Assert.Equal(0, seat.Count);
        Assert.Equal((ushort)0, seat.Driver);
        Assert.False(seat.Occupied);
        Assert.False(seat.HasFree); Assert.False(seat.HasBoardable);
        Assert.False(seat.TryEnter(5)); Assert.False(seat.TryBoard(5, 0, out _)); Assert.False(seat.TrySwitch(5, 0));
        Assert.Equal(-1, seat.SeatOf(5));
        Assert.False(seat.Exit(5));
        Assert.Equal(-1, seat.NearestFreeSeat(Cart, Vector3.Zero, 0f, Vector3.Zero, float.MaxValue, kickCorpses: true));
        seat.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => new VehicleSeat(VehicleSeat.MaxSeats + 1));
    }

    [Fact]
    public void A_vehicle_with_no_seats_and_no_trailer_block_is_refused_and_a_trailer_with_seats_too()
    {
        var f = Load();
        f.Vehicles.Single(v => v.Id == "jeep").Seats = new();
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        var g = Load();
        g.Vehicles.Single(v => v.Id == "rocket_cart").Seats = new() { new VehicleSeatDef { Role = "driver" } };
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(g));
    }

    [Fact]
    public void A_negative_loaded_hp_is_refused()
    {
        var f = Load();
        f.Vehicles.Single(v => v.Id == "rocket_cart").Trailer!.LoadedHp = -1;
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
        var ok = Load();
        ok.Vehicles.Single(v => v.Id == "rocket_cart").Trailer!.LoadedHp = 0; // a trailer with no load to speak of
        VehiclesFile.Validate(ok);
        Assert.False(ok.Vehicles.Single(v => v.Id == "rocket_cart").Trailer!.HasCargo);
    }

    [Fact]
    public void A_seatless_vehicle_has_no_rider_in_any_rule_that_asks()
    {
        Assert.Null(RiderRules.Exposed(Cart, new VehicleSeat(0), new List<HitTarget>()));
        Assert.False(Cart.SeatExposed(0));
        var layout = SeatSchematic.Layout(Cart, 120, 240, 10, 8);
        Assert.Empty(layout.Seats);
    }

    // ---- the load and its hit points ----

    [Fact]
    public void A_cart_starts_loaded_and_its_hit_points_are_the_loads_until_it_is_emptied()
    {
        var cargo = new CartCargo();
        Assert.True(cargo.Loaded);
        Assert.Equal(150f, cargo.MaxHp(Cart));
        Assert.True(cargo.Detonates(Cart));
        Assert.True(cargo.Set(false));
        Assert.False(cargo.Set(false)); // no change
        Assert.Equal(Cart.Hp, cargo.MaxHp(Cart));
        Assert.False(cargo.Detonates(Cart)); // an empty one is a small ordinary vehicle
        cargo.Reset();
        Assert.True(cargo.Loaded);
        Assert.False(new CartCargo().Detonates(Jeep)); // a type with no load never detonates
        Assert.Equal(Jeep.Hp, new CartCargo().MaxHp(Jeep));
    }

    [Fact]
    public void Loading_and_emptying_keep_the_cart_at_the_same_share_of_its_health()
    {
        var life = new VehicleLifecycle(150f);
        life.Damage(75f, 0, 0); // half
        life.SetMaxHp(200f);    // emptied: the ordinary hit points
        Assert.Equal(100f, life.Hp, 3);
        Assert.Equal(200f, life.MaxHp);
        life.SetMaxHp(150f);
        Assert.Equal(75f, life.Hp, 3);
        life.SetMaxHp(0f); life.SetMaxHp(-3f); // nonsense is ignored
        Assert.Equal(150f, life.MaxHp);
        life.Damage(1000f, 0, 0);
        Assert.True(life.Doomed);
        life.SetMaxHp(300f);
        Assert.Equal(0f, life.Hp); // nothing is given back to a cart that is going off
        life.Destroy(10); life.Reset(true);
        Assert.Equal(300f, life.Hp); // a repair restores the present maximum
    }

    // ---- the unhitch key ----

    [Fact]
    public void A_tap_of_the_key_leaves_the_vehicle_on_release_and_unhitches_nothing()
    {
        var key = new UnhitchKey();
        Assert.Equal(UnhitchAction.None, key.Tick(false, Dt));
        Assert.Equal(UnhitchAction.None, key.Tick(true, Dt));
        for (var i = 0; i < 5; i++) Assert.Equal(UnhitchAction.None, key.Tick(true, Dt));
        Assert.Equal(UnhitchAction.Exit, key.Tick(false, Dt));
        Assert.Equal(UnhitchKeyState.Idle, key.State);
    }

    [Fact]
    public void A_hold_unhitches_once_and_the_release_after_it_does_not_leave()
    {
        var key = new UnhitchKey();
        key.Tick(true, Dt);
        var unhitches = 0; var ticks = 0;
        for (; ticks < 200; ticks++)
        {
            var a = key.Tick(true, Dt);
            Assert.NotEqual(UnhitchAction.Exit, a);
            if (a == UnhitchAction.Unhitch) { unhitches++; break; }
        }
        Assert.Equal(1, unhitches);
        Assert.InRange(ticks * Dt, UnhitchKey.HoldSeconds - 2 * Dt, UnhitchKey.HoldSeconds + 2 * Dt);
        for (var i = 0; i < 60; i++) Assert.Equal(UnhitchAction.None, key.Tick(true, Dt)); // still held: nothing more
        Assert.Equal(UnhitchAction.None, key.Tick(false, Dt)); // let go: the driver stays in
        Assert.Equal(UnhitchKeyState.Idle, key.State);
        Assert.Equal(UnhitchAction.None, key.Tick(true, Dt)); // and the next press starts a new hold
        Assert.Equal(UnhitchKeyState.Holding, key.State);
    }

    [Fact]
    public void A_key_that_was_down_when_the_trailer_latched_must_be_let_go_first()
    {
        var key = new UnhitchKey();
        key.Arm(keyDown: true);
        for (var i = 0; i < 60; i++) Assert.Equal(UnhitchAction.None, key.Tick(true, Dt));
        Assert.Equal(UnhitchAction.None, key.Tick(false, Dt));
        key.Tick(true, Dt);
        Assert.Equal(UnhitchAction.Exit, key.Tick(false, Dt));
        key.Arm(keyDown: false);
        Assert.Equal(UnhitchKeyState.Idle, key.State);
    }

    // ---- the record on the wire (protocol 67) ----

    [Fact]
    public void The_protocol_is_67_or_later()
    {
        Assert.True(Wire.ProtocolVersion >= 67);
    }

    [Fact]
    public void A_trailers_record_carries_its_tractor_its_load_and_its_damage_through_the_snapshot()
    {
        var tractorId = (ushort)(VehicleRecord.FirstId + 3);
        var cartId = (ushort)(VehicleRecord.FirstId + 7);
        var kind = Shipped.Vehicles.FindIndex(v => v.Id == "rocket_cart");
        var pack = VehicleRecord.Pack(kind, 0);
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var tractor = VehicleRecord.Make(tractorId, new Vector3(10, 1, -20), 0.4f, 5, VehicleRecord.Pack(3, 1));
        var hitched = VehicleRecord.MakeTrailer(cartId, new Vector3(9, 1, -24), 0.9f, tractorId, pack, loaded: true, stage: 2);
        SnapshotHeader h = default; var ok = false;
        foreach (var p in enc.Encode(10, 1, Wire.NoTick, new[] { tractor, hitched })) ok |= dec.Apply(p, out h);
        Assert.True(ok);
        var got = dec.States[cartId];
        Assert.Equal(EntityType.Vehicle, got.Type);
        Assert.Equal(tractorId, VehicleRecord.Tractor(got));
        Assert.True(VehicleRecord.Loaded(got));
        Assert.Equal(2, VehicleRecord.Stage(got));
        Assert.Equal(kind, VehicleRecord.Kind(got));
        Assert.Equal(EntityState.Alive, got.State);
        Assert.False(VehicleRecord.Lights(got));
        Assert.Equal((byte)0, VehicleRecord.Wheels(got));

        // let go and emptied: the tractor field is 0, the bit is clear, and the change travels
        var loose = VehicleRecord.MakeTrailer(cartId, new Vector3(9, 1, -24), 0.9f, 0, pack, loaded: false, stage: 2);
        foreach (var p in enc.Encode(11, 2, 10, new[] { tractor, loose })) dec.Apply(p, out h);
        got = dec.States[cartId];
        Assert.Equal((ushort)0, VehicleRecord.Tractor(got));
        Assert.False(VehicleRecord.Loaded(got));

        // destroyed: Dead, whatever it carried
        var gone = VehicleRecord.MakeTrailer(cartId, new Vector3(9, 1, -24), 0.9f, 0, pack, loaded: true, destroyed: true);
        foreach (var p in enc.Encode(12, 3, 11, new[] { tractor, gone })) dec.Apply(p, out h);
        Assert.Equal(EntityState.Dead, dec.States[cartId].State);
    }

    [Fact]
    public void A_vehicles_driver_field_is_untouched_by_the_trailers_use_of_it()
    {
        var r = VehicleRecord.Make(VehicleRecord.FirstId, Vector3.Zero, 0, 513, 0x31, stage: 1, lights: true, wheelsOff: 5);
        Assert.Equal((ushort)513, VehicleRecord.Driver(r));
        Assert.Equal(1, VehicleRecord.Stage(r));
        Assert.True(VehicleRecord.Lights(r));
        Assert.Equal((byte)5, VehicleRecord.Wheels(r));
        Assert.False(VehicleRecord.Loaded(r), "bit 6 is not set by a driven vehicle's record");
        Assert.Equal(EntityState.InVehicle, r.State);
    }

    [Theory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(0.6f, 1.9f)]
    [InlineData(-0.9f, 3.0f)]
    [InlineData(1.3f, -3.1f)]
    [InlineData(-1.3f, 3.1f)]
    public void The_articulation_is_read_back_from_the_two_records_yaws(float phi, float tractorYaw)
    {
        var g = TowGeometry.Of(Jeep, Cart);
        var tractor = VehicleRecord.Make(VehicleRecord.FirstId, new Vector3(5, 0, 5), tractorYaw, 1, 0);
        var trailerBody = TowModel.Pose(g, tractor.Position, tractor.YawRad, phi);
        var trailer = VehicleRecord.MakeTrailer((ushort)(VehicleRecord.FirstId + 1), trailerBody.Position, trailerBody.Yaw, tractor.Id, 0, true);
        var read = FlightMath.Wrap(trailer.YawRad - tractor.YawRad);
        Assert.Equal(phi, read, 3); // the quantisation of two yaws is far inside a milliradian
    }

    // ---- what a client draws ----

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.7f)]
    [InlineData(-0.8f, -2.4f)]
    public void The_pose_drawn_from_a_tractor_puts_the_eye_on_the_hitch_and_matches_the_pinned_body(float phi, float yaw)
    {
        var g = TowGeometry.Of(Jeep, Cart);
        var at = new Vector3(40, 3, -12);
        var b = TowModel.Pose(g, at, yaw, phi);
        Assert.True(Vector3.Distance(TowModel.EyePoint(g, b), TowModel.HitchPoint(g, at, yaw)) < 1e-3f);
        Assert.Equal(FlightMath.Wrap(yaw + phi), b.Yaw, 5);
        Assert.Equal(phi, b.Phi, 5);
        // OriginAt of the hitch gives the same place
        var o = TowModel.OriginAt(g, TowModel.HitchPoint(g, at, yaw), b.Yaw);
        Assert.True(Vector3.Distance(o, b.Position) < 1e-3f);
        // the articulation is held to the limit as the server's body is
        Assert.Equal(g.Limit, TowModel.Pose(g, at, yaw, 3f).Phi, 5);
    }

    [Fact]
    public void The_ground_pitch_is_zero_on_level_ground_and_follows_a_rising_hitch_about_the_axle()
    {
        var g = TowGeometry.Of(Jeep, Cart);
        Assert.Equal(0f, TowModel.PitchFor(g, g.EyeY), 5); // the eye at its own height over the axle's ground: level
        var up = TowModel.PitchFor(g, g.EyeY + 0.6f);
        var down = TowModel.PitchFor(g, g.EyeY - 0.4f);
        Assert.True(up > 0 && down < 0);
        // a pitched trailer's eye, turned about the axle's contact, is at the height asked
        var eyeHeight = g.EyeY * MathF.Cos(up) + g.Drawbar * MathF.Sin(up);
        Assert.Equal(g.EyeY + 0.6f, eyeHeight, 4);
        Assert.Equal(0.45f, TowModel.PitchFor(g, 50f)); // held
        Assert.Equal(-0.45f, TowModel.PitchFor(g, -50f));
    }

    [Fact]
    public void A_trailer_on_its_own_has_the_footprint_of_its_type_and_rolls_to_a_stop()
    {
        var g = TowGeometry.ForTrailer(Cart);
        Assert.Equal(Cart.HalfWidth, g.HalfWidth); Assert.Equal(Cart.HalfLength, g.HalfLength);
        Assert.Equal(Cart.Trailer!.RollDecel, g.RollDecel);
        var b = TrailerBody.At(Vector3.Zero, 0f); b.Speed = 6f;
        for (var i = 0; i < 600; i++) TowModel.StepLoose(ref b, g, Dt);
        Assert.Equal(0f, b.Speed);
        Assert.True(b.Position.Z < -3f && b.Position.Z > -10f, "it rolled on ahead of where it was released and stopped");
        Assert.Throws<ArgumentException>(() => TowGeometry.ForTrailer(Jeep));
    }

    // ---- the driver's prediction of a trailer ----

    sealed class Flat : IDriveWorld
    {
        public float GroundY(Vector3 from) => 0f;
        public float WaterY(float x, float z) => float.NegativeInfinity;
        public DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion) => new(from + motion, false, Vector3.Zero);
    }

    static VehicleDef DriveTractor()
    {
        var car = Load().Vehicles.Single(v => v.Id == "car");
        car.Tow = new VehicleTowDef { Hitch = new[] { 0f, 0.5f, 2.3f } };
        return car;
    }

    static DriveInput Keys(float throttle, float steer) => new() { Throttle = throttle, Steer = steer };

    [Fact]
    public void The_drivers_prediction_steps_the_trailer_as_the_server_does_and_needs_no_correction()
    {
        var car = DriveTractor();
        var g = TowGeometry.Of(car, Cart);
        var world = new Flat();
        var start = new DriveState(); DriveModel.Settle(ref start, car, world);
        var server = start;
        // the server: the tractor's step with the effect, then the trailer after it
        var client = new DrivePrediction(car, world, server);
        var serverBody = TowModel.Pose(g, server.Position, server.Yaw, 0f);
        client.AttachTrailer(Cart, g, serverBody, null);
        Assert.True(client.HasTrailer);
        ushort seq = 0;
        var script = new List<DriveInput>();
        for (var i = 0; i < 150; i++) script.Add(Keys(1, i < 60 ? 0 : (i < 120 ? 0.7f : -0.4f)));
        for (var i = 0; i < 90; i++) script.Add(Keys(-1, i < 40 ? 0.5f : -0.5f)); // then backing, which folds it
        foreach (var keys in script)
        {
            seq++;
            var effect = new TowEffect(TowLoad.Of(car, Cart), TowModel.BlockOf(g, serverBody));
            DriveModel.Step(ref server, car, keys, Dt, world, effect);
            TowModel.Step(ref serverBody, g, TractorMotion.From(server, car), Dt);
            client.Step(seq, keys, Dt);
            Assert.Equal(server.Position, client.State.Position);
            Assert.Equal(serverBody.Phi, client.TrailerBody.Phi);
            Assert.Equal(serverBody.Position, client.TrailerBody.Position);
            if (seq % 3 == 0)
            {
                // what a snapshot says: the vehicle record and the trailer's record, the articulation their yaws' difference
                var vehicle = VehicleRecord.Make(VehicleRecord.FirstId, server.Position, server.Yaw, 1, 0);
                var trailer = VehicleRecord.MakeTrailer((ushort)(VehicleRecord.FirstId + 1), serverBody.Position, serverBody.Yaw, vehicle.Id, 0, true);
                var phi = FlightMath.Wrap(trailer.YawRad - vehicle.YawRad);
                Assert.False(client.Reconcile(seq, DriveRecord.State(vehicle, DriveRecord.Make(VehicleRecord.FirstId, server)), Dt, phi), $"corrected at input {seq}");
            }
        }
        Assert.Equal(0, client.Corrections);
        Assert.True(MathF.Abs(client.TrailerBody.Phi) > 0.05f, "the script did swing the trailer");
    }

    [Fact]
    public void A_server_that_says_the_trailer_is_elsewhere_is_taken_and_the_inputs_on_their_way_are_replayed_with_it()
    {
        var car = DriveTractor();
        var g = TowGeometry.Of(car, Cart);
        var world = new Flat();
        var start = new DriveState { Speed = 8 }; DriveModel.Settle(ref start, car, world); start.Speed = 8;
        var client = new DrivePrediction(car, world, start);
        client.AttachTrailer(Cart, g, TowModel.Pose(g, start.Position, start.Yaw, 0f), null);
        var inputs = new List<DriveInput>();
        for (ushort seq = 1; seq <= 10; seq++) { var k = Keys(1, 0.5f); inputs.Add(k); client.Step(seq, k, Dt); }
        // the server saw four inputs; its trailer is 0.3 rad further round than the client's
        var server = start; var body = TowModel.Pose(g, start.Position, start.Yaw, 0f);
        for (var i = 0; i < 4; i++)
        {
            DriveModel.Step(ref server, car, inputs[i], Dt, world, new TowEffect(TowLoad.Of(car, Cart), TowModel.BlockOf(g, body)));
            TowModel.Step(ref body, g, TractorMotion.From(server, car), Dt);
        }
        var serverPhi = body.Phi + 0.3f;
        Assert.True(client.Reconcile(4, server, Dt, serverPhi));
        Assert.Equal(1, client.Corrections);
        Assert.Equal(6, client.PendingCount);
        // the replay began from the server's articulation: the trailer is not where the uncorrected one would be
        var uncorrected = body;
        var s2 = server;
        for (var i = 4; i < 10; i++)
        {
            DriveModel.Step(ref s2, car, inputs[i], Dt, world, new TowEffect(TowLoad.Of(car, Cart), TowModel.BlockOf(g, uncorrected)));
            TowModel.Step(ref uncorrected, g, TractorMotion.From(s2, car), Dt);
        }
        Assert.True(MathF.Abs(client.TrailerBody.Phi - uncorrected.Phi) > 0.05f, "the 0.3 rad the server gave it is still in it");
        // and with no trailer the prediction is the one it always was
        client.DetachTrailer();
        Assert.False(client.HasTrailer);
    }

    [Fact]
    public void A_blocked_trailer_holds_the_predicted_tractor_where_it_was()
    {
        var car = DriveTractor();
        var g = TowGeometry.Of(car, Cart);
        var world = new Flat();
        var start = new DriveState(); DriveModel.Settle(ref start, car, world);
        var client = new DrivePrediction(car, world, start);
        client.AttachTrailer(Cart, g, TowModel.Pose(g, start.Position, start.Yaw, 0f), new Wall());
        for (ushort seq = 1; seq <= 90; seq++) client.Step(seq, Keys(-1, 0), Dt); // reversing: the trailer meets the wall behind
        Assert.InRange(client.State.Position.Z, 1f, 4f); // the cart's rear edge was at about 15 m: it stops about 3 m back, not at the full run of 90 ticks
        Assert.Equal(0f, client.State.Speed);
        var ahead = client.Ahead(Keys(-1, 0), Dt);
        Assert.Equal(client.State.Position, ahead.State.Position, new VectorComparer());
    }

    /// <summary>A wall across the world at z = 18 (the tractor's z grows as it reverses): the trailer is blocked when its box would reach it.</summary>
    sealed class Wall : ITrailerWorld
    {
        public bool Blocked(in BodyBox from, in BodyBox to, float y) => to.Z + to.HalfL > 18f;
    }

    sealed class VectorComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
        public int GetHashCode(Vector3 v) => v.GetHashCode();
    }
}
