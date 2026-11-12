using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// The amphibious transport's three riders: the driver, the armed seat beside them (ADR 0008) and the ring gun in the rear. Nothing covers any of
/// them (ADR 0009): a ray that reaches a rider's body hits that rider and not the hull, a ray that misses them hits the hull as before, and riders
/// of one vehicle never hit each other. The flak truck and the utility helicopter keep exactly the one exposed gunner they had.
/// </summary>
public class AmphibiousTransportTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly VehiclesFile Shipped = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
    static readonly WeaponsFile Weapons = WeaponsFile.Load(Path.Combine(DataDir, "weapons.json"));
    static VehicleDef Car => Shipped.Vehicles.Single(v => v.Id == "amphibious");
    static VehicleDoorGunDef Ring => Car.DoorGun!;
    const float Deg = MathF.PI / 180f;
    const int DriverSeat = 0, ArmedSeat = 1, GunnerSeat = 2;
    const ushort Driver = 11, Passenger = 12, Gunner = 13, Shooter = 1, Bystander = 2;

    /// <summary>The transport stands at the origin facing -Z, full: who sits where.</summary>
    static VehicleSeat Full()
    {
        var s = new VehicleSeat(Car.Seats.Count);
        Assert.True(s.TryEnter(Driver, DriverSeat) && s.TryEnter(Passenger, ArmedSeat) && s.TryEnter(Gunner, GunnerSeat));
        return s;
    }

    /// <summary>A rider as the server makes them a hit target: on their seat, as tall as the seat makes them.</summary>
    static HitTarget Rider(ushort id, int seat)
    {
        var o = Car.Seats[seat].Offset;
        return new HitTarget(id, new Vector3(o[0], o[1], o[2]), RiderRules.HitStance(Car, seat, Stance.Stand), 0);
    }

    static List<HitTarget> Riders() => new() { Rider(Driver, DriverSeat), Rider(Passenger, ArmedSeat), Rider(Gunner, GunnerSeat) };

    /// <summary>Where the hull's box first stops a ray (the server's world ray with the hull in), infinity when it misses.</summary>
    static float HullStop(Vector3 origin, Vector3 dir) =>
        VehicleBox.Ray(origin, dir, 1000f, Vector3.Zero, 0, Car.HalfWidth, Car.HalfLength, Car.Height)?.Distance ?? float.PositiveInfinity;

    /// <summary>A shot as the server resolves it: the hull stops the ray, the exposed riders are reached past it.</summary>
    static RayHit? Shoot(Vector3 from, Vector3 dir, List<HitTarget> targets, VehicleSeat seats, ushort shooter = Shooter) =>
        GunnerCover.Raycast(from, dir, 300f, HullStop(from, dir), targets, shooter, RiderRules.Exposed(Car, seats, targets), float.PositiveInfinity);

    // ---- data ----

    [Fact]
    public void The_shipped_transport_has_a_driver_an_armed_seat_and_a_ring_gunner_all_in_the_open()
    {
        var v = Car;
        Assert.Equal(new[] { "driver", "passenger", "gunner" }, v.Seats.Select(s => s.Role));
        Assert.All(v.Seats, s => Assert.True(s.Exposed));
        Assert.Equal(new[] { false, true, false }, v.Seats.Select(s => s.Armed));
        Assert.Equal(new[] { false, false, true }, v.Seats.Select(s => s.Standing));
        for (var i = 0; i < 3; i++) Assert.True(v.SeatExposed(i));
        Assert.False(v.ExposedGunner);                       // the vehicle-level flag stays the flak truck's and the helicopter's
        Assert.NotNull(v.Gun);
        Assert.NotNull(v.DoorGun);
        Assert.Null(v.Flak);
        var jeep = Shipped.Vehicles.Single(x => x.Id == "jeep").Gun!;
        Assert.Equal(jeep.Damage, v.Gun!.Damage);            // the jeep's round, the helicopter's belt
        Assert.Equal(jeep.FireInterval, v.Gun.FireInterval);
        Assert.Equal(Shipped.Vehicles.Single(x => x.Id == "utility_helicopter").Gun!.Magazine, v.Gun.Magazine);
    }

    [Fact]
    public void Armed_and_exposed_are_properties_of_a_seat_and_no_other_vehicle_has_them()
    {
        foreach (var v in Shipped.Vehicles.Where(v => v.Id != "amphibious"))
            Assert.All(v.Seats, s => Assert.False(s.Armed || s.Exposed || s.Standing || s.Aim != null, $"{v.Id}"));
        // the bus's passengers stay unarmed: their input does nothing to their weapons
        var bus = Shipped.Vehicles.Single(v => v.Id == "bus");
        var fire = new NetInput { Buttons = (ushort)(MoveButtons.Fire | MoveButtons.Reload), Slot = 2 };
        for (var s = 0; s < bus.Seats.Count; s++) Assert.Null(RiderRules.Weapons(bus, s, fire));
        // and the flak truck and the helicopter have exactly the one exposed seat they had
        foreach (var id in new[] { "flak", "utility_helicopter" })
        {
            var v = Shipped.Vehicles.Single(x => x.Id == id);
            Assert.Equal(new[] { false, true }, Enumerable.Range(0, v.Seats.Count).Select(v.SeatExposed));
        }
    }

    [Fact]
    public void The_seat_properties_are_validated()
    {
        static VehiclesFile With(Action<VehicleDef> change)
        {
            var f = VehiclesFile.Load(Path.Combine(DataDir, "vehicles.json"));
            change(f.Vehicles.Single(v => v.Id == "amphibious"));
            return f;
        }
        VehiclesFile.Validate(With(_ => { }));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[DriverSeat].Armed = true)));           // a driver drives
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[GunnerSeat].Armed = true)));           // a gunner has the vehicle's gun
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[GunnerSeat].Exposed = false)));        // standing needs exposed
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[DriverSeat].Aim = new VehicleSeatAimDef())));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[ArmedSeat].Aim!.ArcDeg = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[ArmedSeat].Aim!.MinPitchDeg = 80)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Seats[ArmedSeat].Aim!.SpreadFullSpeed = 0)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.DoorGun!.RingRadius = -1)));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.DoorGun!.GuardMinPitchDeg = -50)));          // below the gun's own lowest
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(With(v => v.Gun = null)));                                // the ring gun fires by the gun rules
    }

    // ---- exposure ----

    [Fact]
    public void A_ray_through_the_hull_reaches_each_rider_and_the_hull_takes_nothing()
    {
        var seats = Full(); var targets = Riders();
        // one metre up: under the top of the sides, so the hull's box is in the way of every one of these
        var fromLeft = new Vector3(-20, 1.0f, Car.Seats[DriverSeat].Offset[2]);
        var fromRight = new Vector3(20, 1.0f, Car.Seats[ArmedSeat].Offset[2]);
        var fromBehind = new Vector3(0, 1.0f, 20);
        foreach (var (from, dir, victim) in new[] { (fromLeft, Vector3.UnitX, Driver), (fromRight, -Vector3.UnitX, Passenger), (fromBehind, -Vector3.UnitZ, Gunner) })
        {
            var hull = HullStop(from, dir);
            Assert.True(hull < 30f, "the hull's box is in the ray's way");
            // without the rule the hull shadows them all, as it does the riders of every closed vehicle
            Assert.Null(HitMath.Raycast(from, dir, 300f, hull, targets, Shooter));
            // with it the rider is hit, further along the ray than the hull's surface: the caller then damages the rider and not the hull
            var hit = Shoot(from, dir, targets, seats);
            Assert.Equal(victim, hit!.Value.Id);
            Assert.True(hit.Value.Distance > hull);
        }
    }

    [Fact]
    public void The_driver_is_as_exposed_as_the_others_and_the_nearer_of_two_riders_in_line_takes_the_shot()
    {
        var seats = Full(); var targets = Riders();
        // straight across the front seats from the left: the driver is first, the passenger behind them
        var from = new Vector3(-20, 1.3f, Car.Seats[DriverSeat].Offset[2]);
        Assert.Equal(Driver, Shoot(from, Vector3.UnitX, targets, seats)!.Value.Id);
        Assert.Equal(Passenger, Shoot(new Vector3(20, 1.3f, from.Z), -Vector3.UnitX, targets, seats)!.Value.Id);
        // with the driver's seat empty the same ray goes on to the passenger
        seats.Exit(Driver);
        var two = targets.Where(t => t.Id != Driver).ToList();
        Assert.Equal(Passenger, Shoot(from, Vector3.UnitX, two, seats)!.Value.Id);
    }

    [Fact]
    public void A_ray_that_misses_the_riders_still_hits_the_hull()
    {
        var seats = Full(); var targets = Riders();
        foreach (var (from, dir) in new[]
                 {
                     (new Vector3(-20, 0.3f, Car.Seats[DriverSeat].Offset[2]), Vector3.UnitX),   // low, into the hull under the driver
                     (new Vector3(-20, 0.9f, -1.9f), Vector3.UnitX),                             // through the bow, ahead of everyone
                     (new Vector3(0.0f, 0.5f, -20), Vector3.UnitZ),                              // into the stem, under the two in front
                 })
        {
            var hull = HullStop(from, dir);
            Assert.True(hull < 30f);
            Assert.Null(Shoot(from, dir, targets, seats)); // no player hit: the caller damages the hull at that distance
        }
        // and a ray that passes the vehicle altogether hits nothing
        var wide = new Vector3(-20, 1.0f, 6);
        Assert.Equal(float.PositiveInfinity, HullStop(wide, Vector3.UnitX));
        Assert.Null(Shoot(wide, Vector3.UnitX, targets, seats));
    }

    [Fact]
    public void A_rider_shows_above_the_sides_and_is_hit_there_without_the_hull_being_touched()
    {
        var targets = Riders();
        var from = new Vector3(-20, 1.6f, Car.Seats[DriverSeat].Offset[2]); // head height, over the top of the hull's box
        Assert.Equal(float.PositiveInfinity, HullStop(from, Vector3.UnitX));
        var hit = HitMath.Raycast(from, Vector3.UnitX, 300f, float.PositiveInfinity, targets, Shooter);
        Assert.Equal(Driver, hit!.Value.Id);
        Assert.True(hit.Value.Head, "at head height it is a headshot");
        // a seated rider is as tall as a crouch, the gunner stands in the ring: the hit volume is the drawn figure
        Assert.Equal(Stance.Crouch, targets[0].Stance);
        Assert.Equal(Stance.Crouch, targets[1].Stance);
        Assert.Equal(Stance.Stand, targets[2].Stance);
        var overTheFront = new Vector3(-20, 2.0f, Car.Seats[DriverSeat].Offset[2]); // above a seated head
        Assert.Null(HitMath.Raycast(overTheFront, Vector3.UnitX, 300f, float.PositiveInfinity, targets, Shooter));
        var atTheGunner = new Vector3(-20, 2.0f, Car.Seats[GunnerSeat].Offset[2]);  // the standing gunner is that tall
        Assert.Equal(Gunner, HitMath.Raycast(atTheGunner, Vector3.UnitX, 300f, float.PositiveInfinity, targets, Shooter)!.Value.Id);
    }

    [Fact]
    public void A_wall_or_a_bystander_in_front_still_stops_the_shot()
    {
        var seats = Full(); var targets = Riders();
        var from = new Vector3(-20, 1.0f, Car.Seats[DriverSeat].Offset[2]);
        var hull = HullStop(from, Vector3.UnitX);
        // something solid behind the hull's surface and before the rider (the world with the hull left out)
        Assert.Null(GunnerCover.Raycast(from, Vector3.UnitX, 300f, hull, targets, Shooter, RiderRules.Exposed(Car, seats, targets), hull + 0.1f));
        // a wall in front of the vehicle stops it before either
        Assert.Null(GunnerCover.Raycast(from, Vector3.UnitX, 300f, 5f, targets, Shooter, RiderRules.Exposed(Car, seats, targets), 5f));
        // someone standing beside the vehicle, on the ray, takes it
        var all = new List<HitTarget>(targets) { new(Bystander, new Vector3(-6, 0, from.Z), Stance.Stand, 0) };
        Assert.Equal(Bystander, Shoot(from, Vector3.UnitX, all, seats)!.Value.Id);
    }

    [Fact]
    public void Riders_of_the_same_vehicle_do_not_hit_each_other()
    {
        var seats = Full(); var targets = Riders();
        // the passenger fires across the driver: the server gives a rider's shot only the targets who are not aboard, and leaves their own hull out
        var mine = RiderRules.NotAboard(targets, seats);
        Assert.Empty(mine);
        var eye = new Vector3(Car.Seats[ArmedSeat].Offset[0], Car.Seats[ArmedSeat].Offset[1] + SeatAimRules.EyeHeight(Car.Seats[ArmedSeat]), Car.Seats[ArmedSeat].Offset[2]);
        Assert.Null(GunnerCover.Raycast(eye, -Vector3.UnitX, 300f, float.PositiveInfinity, mine, Passenger, RiderRules.Exposed(Car, seats, mine), float.PositiveInfinity));
        Assert.Null(RiderRules.Exposed(Car, seats, mine));
        // someone on foot beyond the driver is hit by that same shot
        var all = new List<HitTarget>(targets) { new(Bystander, new Vector3(-8, 0, eye.Z), Stance.Stand, 0) };
        var hit = GunnerCover.Raycast(eye, -Vector3.UnitX, 300f, float.PositiveInfinity, RiderRules.NotAboard(all, seats), Passenger, null, float.PositiveInfinity);
        Assert.Equal(Bystander, hit!.Value.Id);
        // the riders of another transport are fair game
        var other = new VehicleSeat(3); other.TryEnter(40, 0);
        Assert.Single(RiderRules.NotAboard(new List<HitTarget> { new(40, new Vector3(30, 0.64f, 0), Stance.Crouch, 0) }, seats));
        _ = other;
    }

    [Fact]
    public void A_rocket_and_a_pellet_that_stopped_on_the_hull_reach_a_rider_directly()
    {
        var seats = Full(); var targets = Riders();
        var from = new Vector3(-20, 1.0f, Car.Seats[DriverSeat].Offset[2]);
        var hull = HullStop(from, Vector3.UnitX);
        var covered = RocketFlight.Step(from, Vector3.UnitX, 0f, 40f, 3.75f, 1f, hull, targets, Shooter);
        Assert.Equal(RocketOutcome.World, covered.Outcome);
        var open = RocketFlight.Step(from, Vector3.UnitX, 0f, 40f, 3.75f, 1f, hull, targets, Shooter, RiderRules.Exposed(Car, seats, targets), float.PositiveInfinity);
        Assert.Equal(RocketOutcome.Player, open.Outcome);
        Assert.Equal(Driver, open.Victim);

        var shotgun = Weapons.Weapons.First(x => x.Pellets > 1);
        var dirs = new[] { Vector3.UnitX };
        Assert.Empty(HitMath.ResolvePellets(shotgun, from, dirs, 300f, new[] { hull }, targets, Shooter));
        var totals = HitMath.ResolvePelletsPastHulls(shotgun, from, dirs, 300f, new[] { hull }, targets, Shooter, new[] { RiderRules.Exposed(Car, seats, targets) }, new[] { float.PositiveInfinity });
        Assert.True(totals.ContainsKey(Driver));
        Assert.Single(totals);
    }

    [Fact]
    public void A_killstreak_round_reaches_a_rider_through_the_transports_hit_volume()
    {
        var seats = Full(); var targets = Riders();
        var cannon = new GunshipWeaponDef { Id = "cannon", Kind = GunshipWeaponKind.Cannon, Damage = 40, RangeM = 500 };
        var z = Car.Seats[DriverSeat].Offset[2];
        var low = new Vector3(-30, 1.0f, z); // level, under the top of the sides
        // a vehicle that says nothing about its riders keeps them behind its volume, as every closed vehicle does
        var covered = new[] { new GunshipVehicle(0, Vector3.Zero, 0, Car.HalfWidth, Car.HalfLength, Car.Height) };
        Assert.Equal(GunshipImpact.Vehicle, GunshipRouting.Route(cannon, low, Vector3.UnitX, float.PositiveInfinity, targets, Shooter, covered, 0.5f).Impact);
        var open = new[] { new GunshipVehicle(0, Vector3.Zero, 0, Car.HalfWidth, Car.HalfLength, Car.Height, RiderRules.Exposed(Car, seats, targets, seatsOnly: true)) };
        var route = GunshipRouting.Route(cannon, low, Vector3.UnitX, float.PositiveInfinity, targets, Shooter, open, 0.5f);
        Assert.Equal(GunshipImpact.Body, route.Impact);
        Assert.Equal(Driver, route.TargetId);
        // a round that misses them is the vehicle's
        var under = GunshipRouting.Route(cannon, new Vector3(-30, 0.3f, z), Vector3.UnitX, float.PositiveInfinity, targets, Shooter, open, 0.5f);
        Assert.Equal(GunshipImpact.Vehicle, under.Impact);
        // another vehicle standing behind the transport does not hide its riders, and one in front of it still takes the round
        var behind = open.Append(new GunshipVehicle(1, new Vector3(6, 0, z), 0, 1, 2, 2)).ToArray();
        Assert.Equal(Driver, GunshipRouting.Route(cannon, low, Vector3.UnitX, float.PositiveInfinity, targets, Shooter, behind, 0.5f).TargetId);
        var inFront = open.Append(new GunshipVehicle(1, new Vector3(-6, 0, z), 0, 1, 2, 2)).ToArray();
        var first = GunshipRouting.Route(cannon, low, Vector3.UnitX, float.PositiveInfinity, targets, Shooter, inFront, 0.5f);
        Assert.Equal(GunshipImpact.Vehicle, first.Impact);
        Assert.Equal(1, first.VehicleIndex);
    }

    [Fact]
    public void The_flak_truck_and_the_helicopter_keep_exactly_their_one_exposed_gunner()
    {
        foreach (var id in new[] { "flak", "utility_helicopter" })
        {
            var v = Shipped.Vehicles.Single(x => x.Id == id);
            var seats = new VehicleSeat(v.Seats.Count);
            seats.TryEnter(Driver, 0); seats.TryEnter(Gunner, 1);
            var targets = new List<HitTarget> { new(Driver, Vector3.Zero, Stance.Stand, 0), new(Gunner, new Vector3(0, 1, 1), Stance.Stand, 0) };
            var exposed = RiderRules.Exposed(v, seats, targets);
            Assert.Equal(new[] { Gunner }, exposed!.Riders.Select(r => r.Id));    // the gunner, never the driver or the pilot
            Assert.Null(RiderRules.Exposed(v, seats, targets, seatsOnly: true)); // and not for the weapons that never reached them
            // their gunner is the body they are, as before: no seat changes their stance
            Assert.Equal(Stance.Stand, RiderRules.HitStance(v, 1, Stance.Stand));
            Assert.Equal(Stance.Prone, RiderRules.HitStance(v, 1, Stance.Prone));
            // with the gunner gone there is nobody to reach
            seats.Exit(Gunner);
            Assert.Null(RiderRules.Exposed(v, seats, targets));
        }
        // one exposed gunner still converts to the riders the rule takes, so the old calls mean what they meant
        HitTarget? none = null;
        ExposedRiders? asRiders = none;
        Assert.Null(asRiders);
        asRiders = new HitTarget(Gunner, Vector3.Zero, Stance.Stand, 0);
        Assert.Equal(Gunner, Assert.Single(asRiders!.Riders).Id);
    }

    [Fact]
    public void A_rider_who_dies_frees_their_seat_and_the_others_keep_theirs()
    {
        var seats = Full();
        Assert.True(seats.Exit(Driver));               // the server does this on the kill
        Assert.Equal(0, seats.Driver);                 // nobody at the wheel: the vehicle step drives it hands off, and it coasts (DriveTests)
        Assert.True(seats.IsFree(DriverSeat));
        Assert.Equal(Passenger, seats.Occupant(ArmedSeat));
        Assert.Equal(Gunner, seats.Occupant(GunnerSeat));
        Assert.True(seats.Occupied);
        Assert.True(seats.TrySwitch(Passenger, DriverSeat)); // and the passenger can take the wheel with the seat switch
        // the dead driver is no longer a target aboard
        var targets = Riders().Where(t => t.Id != Driver).ToList();
        Assert.Equal(new[] { Passenger, Gunner }, RiderRules.Exposed(Car, seats, targets)!.Riders.Select(r => r.Id));
    }

    // ---- the armed seat ----

    [Fact]
    public void Only_the_armed_seats_rider_has_their_weapons_and_all_of_what_the_input_does_on_foot()
    {
        var input = new NetInput { Buttons = (ushort)(MoveButtons.Fire | MoveButtons.Reload | MoveButtons.Switch), Slot = 2 };
        Assert.Null(RiderRules.Weapons(Car, DriverSeat, input));
        Assert.Null(RiderRules.Weapons(Car, GunnerSeat, input));
        Assert.Null(RiderRules.Weapons(Car, 7, input));
        var w = RiderRules.Weapons(Car, ArmedSeat, input)!.Value;
        Assert.True(w.Fire && w.Reload && w.Next);
        Assert.Equal(2, w.Slot);
        var idle = RiderRules.Weapons(Car, ArmedSeat, new NetInput { Slot = 1 })!.Value;
        Assert.False(idle.Fire || idle.Reload || idle.Next);
    }

    [Fact]
    public void From_the_seat_a_weapon_fires_reloads_and_switches_by_its_own_rules()
    {
        var w = new PlayerWeapons(Weapons);
        w.Reset(0);
        var slot = w.Slot;
        var def = w.Current;
        Assert.True(def.Kind == WeaponKind.Hitscan && def.MagazineSize > 1);
        var fire = RiderRules.Weapons(Car, ArmedSeat, new NetInput { Buttons = (ushort)MoveButtons.Fire, Slot = (byte)slot })!.Value;
        var shot = w.Update(0.0, fire);
        Assert.Equal(slot - 1, shot!.Value.WeaponIndex);                // the kill-feed weapon: an ordinary kill with that weapon
        Assert.Equal(def.MagazineSize - 1, w.Mag(slot));
        Assert.Null(w.Update(def.FireInterval * 0.5, fire));           // no faster than the weapon fires
        if (def.FullAuto) Assert.NotNull(w.Update(def.FireInterval + 0.001, fire));
        // reload: the key starts it, the magazine is full after the weapon's reload time, and it cannot fire meanwhile
        var reload = RiderRules.Weapons(Car, ArmedSeat, new NetInput { Buttons = (ushort)MoveButtons.Reload, Slot = (byte)slot })!.Value;
        var t = 1.0;
        Assert.Null(w.Update(t, reload));
        Assert.True(w.Reloading);
        Assert.Null(w.Update(t + def.ReloadTime * 0.5, fire));
        w.Update(t + def.ReloadTime + 0.01, RiderRules.Weapons(Car, ArmedSeat, new NetInput { Slot = (byte)slot })!.Value);
        Assert.False(w.Reloading);
        Assert.Equal(def.MagazineSize, w.Mag(slot));
        // the wanted slot switches weapon: the mouse wheel sends it, since the number keys are the seats' while seated
        var other = Enumerable.Range(1, w.WeaponCount).First(s => s != slot && w.OnHotbar(s));
        w.Update(t + 10, RiderRules.Weapons(Car, ArmedSeat, new NetInput { Slot = (byte)other })!.Value);
        Assert.Equal(other, w.Slot);
    }

    [Fact]
    public void The_vehicles_speed_widens_the_riders_spread_as_their_own_does_on_foot()
    {
        var seat = Car.Seats[ArmedSeat];
        var w = Weapons.Weapons.First(x => x.Kind == WeaponKind.Hitscan && x.Pellets <= 1 && x.MoveSpread > 0);
        var own = w.SpreadBase;
        var still = SeatAimRules.ShotSpread(own, w, false, 0f, seat);
        var half = SeatAimRules.ShotSpread(own, w, false, seat.Aim!.SpreadFullSpeed / 2, seat);
        var full = SeatAimRules.ShotSpread(own, w, false, seat.Aim.SpreadFullSpeed, seat);
        var flat = SeatAimRules.ShotSpread(own, w, false, Car.TopSpeed, seat);
        Assert.Equal(own, still, 4);                                   // at a standstill: the weapon's own spread, as standing still on foot
        Assert.Equal(HitMath.ShotSpread(own, w, Stance.Stand, false, 0f, true), still, 4);
        Assert.Equal(own + w.MoveSpread / 2, half, 3);                 // in proportion
        Assert.Equal(own + w.MoveSpread, full, 3);                     // all of it at spread_full_speed: as sprinting
        Assert.Equal(HitMath.ShotSpread(own, w, Stance.Stand, false, MoveConsts.SprintSpeed, true), full, 3);
        Assert.Equal(full, flat, 4);                                   // and no wider beyond it
        Assert.Equal(full, SeatAimRules.ShotSpread(own, w, false, -seat.Aim.SpreadFullSpeed, seat), 4); // reversing counts the same
        // the seat is no crouch: it gives no stance bonus; aiming narrows it as on foot
        Assert.True(still >= HitMath.ShotSpread(own, w, Stance.Crouch, false, 0f, true));
        Assert.Equal(still * w.AdsSpreadMultiplier, SeatAimRules.ShotSpread(own, w, true, 0f, seat), 4);
        // afloat at the water's top speed it is steadier than at the land's
        Assert.True(SeatAimRules.ShotSpread(own, w, false, Car.WaterTopSpeed, seat) < full);
        // a shotgun's cone follows the same rule
        var shotgun = Weapons.Weapons.First(x => x.Pellets > 1);
        Assert.Equal(HitMath.ShotSpread(shotgun.PelletSpread, shotgun, Stance.Stand, false, MoveConsts.SprintSpeed, true), SeatAimRules.ShotSpread(shotgun.PelletSpread, shotgun, false, 30f, seat), 3);
    }

    [Fact]
    public void The_armed_rider_aims_from_sixty_degrees_left_of_the_nose_round_the_right_side_to_the_rear_quarter()
    {
        var aim = Car.Seats[ArmedSeat].Aim!;
        Assert.Equal(60f, aim.YawDeg + aim.ArcDeg, 3);          // as far left as 60 degrees off the nose: not across the driver
        Assert.Equal(-150f, aim.YawDeg - aim.ArcDeg, 3);        // round the right side to 150 degrees: not back through the gunner
        // inside the arc the view is the aim, bit for bit
        foreach (var (yaw, pitch) in new[] { (0f, 0f), (-90 * Deg, 0.2f), (50 * Deg, -0.5f), (-140 * Deg, 1.0f) })
        {
            var a = SeatAimRules.Aim(aim, 0, 0, 0, yaw, pitch);
            Assert.Equal(yaw, a.ViewYaw);
            Assert.Equal(pitch, a.ViewPitch);
        }
        // outside it the aim stops at the edge: looking at the driver (90 degrees left) aims 60 degrees left
        Assert.Equal(60f, SeatAimRules.Aim(aim, 0, 0, 0, 90 * Deg, 0).ViewYaw / Deg, 2);
        Assert.Equal(-150f, SeatAimRules.Aim(aim, 0, 0, 0, 180 * Deg, 0).ViewYaw / Deg, 2);
        Assert.Equal(-150f, SeatAimRules.Aim(aim, 0, 0, 0, 170 * Deg, 0).ViewYaw / Deg, 2);  // straight back is nearer the right edge
        Assert.Equal(60f, SeatAimRules.Aim(aim, 0, 0, 0, 120 * Deg, 0).ViewYaw / Deg, 2);
        // up and down
        Assert.Equal(aim.MinPitchDeg, SeatAimRules.Aim(aim, 0, 0, 0, 0, -80 * Deg).ViewPitch / Deg, 2);
        Assert.Equal(aim.MaxPitchDeg, SeatAimRules.Aim(aim, 0, 0, 0, 0, 85 * Deg).ViewPitch / Deg, 2);
        // the arc turns with the vehicle: with the vehicle turned a quarter left, its nose is at 90 degrees and the left edge at 150
        Assert.Equal(150f, SeatAimRules.Aim(aim, 90 * Deg, 0, 0, 180 * Deg, 0).ViewYaw / Deg, 2);
        Assert.Equal(90f, SeatAimRules.Aim(aim, 90 * Deg, 0, 0, 90 * Deg, 0).ViewYaw / Deg, 2);
        // and tilts with it: on a 20 degree up-slope, level ahead is inside the arc and stays
        Assert.Equal(0f, SeatAimRules.Aim(aim, 0, 20 * Deg, 0, 0, 0).ViewPitch, 4);
        // a seat with no arc aims anywhere
        Assert.Equal(2.5f, SeatAimRules.Aim(null, 0, 0, 0, 2.5f, -1.2f).ViewYaw);
        // the eye is a seated one, above the top of the sides, so the rider sees and shoots over them
        Assert.Equal(MoveConsts.CrouchEyeHeight, SeatAimRules.EyeHeight(Car.Seats[ArmedSeat]));
        Assert.True(Car.Seats[ArmedSeat].Offset[1] + SeatAimRules.EyeHeight(Car.Seats[ArmedSeat]) > Car.Height + 0.3f);
        Assert.Equal(MoveConsts.EyeHeight, SeatAimRules.EyeHeight(Car.Seats[GunnerSeat]));
    }

    [Fact]
    public void The_knife_reaches_someone_beside_the_vehicle_and_never_a_fellow_rider()
    {
        var seats = Full();
        var knife = Weapons.Weapons.First(x => x.Kind == WeaponKind.Melee);
        var seat = Car.Seats[ArmedSeat].Offset;
        var feet = new Vector3(seat[0], seat[1], seat[2]);
        var beside = new HitTarget(Bystander, new Vector3(seat[0] + 1.3f, 0, seat[2]), Stance.Stand, MathF.PI / 2); // standing at the right side, facing the vehicle
        var all = new List<HitTarget>(Riders()) { beside };
        var targets = RiderRules.NotAboard(all, seats);
        var hit = HitMath.Melee(feet, -90 * Deg, knife.Reach, targets, Passenger); // the rider turns to the right
        Assert.Equal(Bystander, hit!.Value.Id);
        // turned to the driver, half a metre away, the knife finds nobody
        Assert.Null(HitMath.Melee(feet, 90 * Deg, knife.Reach, targets, Passenger));
    }

    // ---- the ring gun ----

    [Fact]
    public void The_ring_guns_numbers_are_the_models()
    {
        var n = AmphibiousModelTests.Nodes(Car.Model);
        var g = Ring;
        Assert.Equal(n["gun_mount"].X, g.Pivot[0], 2);
        Assert.Equal(n["gun_mount"].Z, g.Pivot[2], 2);                       // the yaw axis is the ring's centre
        Assert.Equal(n["gun"].Y, g.Pivot[1], 2);                             // at the height of the gun
        Assert.Equal(n["gun_mount"].Z - n["gun"].Z, g.RingRadius, 2);        // the gun rides the ring ahead of the centre
        Assert.Equal(n["gun"].Z - n["muzzle"].Z, g.MuzzleLength, 2);
        Assert.Equal(0f, g.OutwardYawDeg);                                   // at rest it points over the bow
        Assert.Equal(180f, g.YawArcDeg);                                     // and turns all the way round
        var at = DoorGunRules.FromLocal(g, 0, 0, 0, 0, 0);
        var pivot = DoorGunRules.GunPivot(g, Vector3.Zero, 0, 0, 0, at);
        Assert.True(Vector3.Distance(n["gun"], pivot) < 0.01f);
        Assert.True(Vector3.Distance(n["muzzle"], DoorGunRules.Muzzle(g, pivot, at)) < 0.01f);
        Assert.True(Vector3.Distance(n["gunner_sight"], DoorGunRules.Eye(g, pivot, 0, 0, 0, at)) < 0.01f);
        // the gunner stands on the ring's centre, and the gun swings round them: aimed left, the gun is on their left
        var seat = Car.Seats[GunnerSeat].Offset;
        Assert.Equal(g.Pivot[0], seat[0], 2);
        Assert.Equal(g.Pivot[2], seat[2], 2);
        var left = DoorGunRules.GunPivot(g, Vector3.Zero, 0, 0, 0, DoorGunRules.FromLocal(g, 0, 0, 0, 90 * Deg, 0));
        Assert.True(Vector3.Distance(new Vector3(g.Pivot[0] - g.RingRadius, g.Pivot[1], g.Pivot[2]), left) < 1e-3f);
        var back = DoorGunRules.GunPivot(g, Vector3.Zero, 0, 0, 0, DoorGunRules.FromLocal(g, 0, 0, 0, 180 * Deg, 0));
        Assert.True(Vector3.Distance(new Vector3(g.Pivot[0], g.Pivot[1], g.Pivot[2] + g.RingRadius), back) < 1e-3f);
    }

    [Fact]
    public void The_ring_gun_turns_all_the_way_round_and_follows_the_view()
    {
        for (var deg = -180; deg < 180; deg += 15)
        {
            var aim = DoorGunRules.Aim(Ring, 0, 0, 0, deg * Deg, 10 * Deg);
            Assert.Equal(0f, FlightMath.Wrap(aim.Yaw - deg * Deg), 3);       // no stop anywhere round the ring
            Assert.Equal(10f, aim.Pitch / Deg, 2);
            Assert.Equal(0f, FlightMath.Wrap(aim.ViewYaw - deg * Deg), 3);   // the gun points where the gunner looks
        }
        // it turns with the vehicle and lies on its slope
        var turned = DoorGunRules.Aim(Ring, 40 * Deg, 0, 0, 100 * Deg, 0);
        Assert.Equal(60f, turned.Yaw / Deg, 2);
        Assert.Equal(Ring.MaxPitchDeg, DoorGunRules.Aim(Ring, 0, 0, 0, 0, 80 * Deg).Pitch / Deg, 2);
        Assert.Equal(Ring.MinPitchDeg, DoorGunRules.Aim(Ring, 0, 0, 0, 120 * Deg, -80 * Deg).Pitch / Deg, 2);
    }

    [Fact]
    public void Over_the_two_in_front_the_gun_cannot_be_depressed_and_its_arc_never_meets_a_rider_or_the_hull()
    {
        var g = Ring;
        // the guard: inside it the lowest pitch is the guard's, outside the blend it is the gun's own, and between them it comes down steadily
        Assert.Equal(g.GuardMinPitchDeg, DoorGunRules.MinPitch(g, 0) / Deg, 3);
        Assert.Equal(g.GuardMinPitchDeg, DoorGunRules.MinPitch(g, g.GuardArcDeg * Deg) / Deg, 3);
        Assert.Equal(g.GuardMinPitchDeg, DoorGunRules.MinPitch(g, -g.GuardArcDeg * Deg) / Deg, 3);
        Assert.Equal(g.MinPitchDeg, DoorGunRules.MinPitch(g, (g.GuardArcDeg + g.GuardBlendDeg) * Deg) / Deg, 3);
        Assert.Equal(g.MinPitchDeg, DoorGunRules.MinPitch(g, 180 * Deg) / Deg, 3);
        Assert.Equal((g.GuardMinPitchDeg + g.MinPitchDeg) / 2, DoorGunRules.MinPitch(g, (g.GuardArcDeg + g.GuardBlendDeg / 2) * Deg) / Deg, 2);
        Assert.Equal(g.GuardMinPitchDeg, DoorGunRules.Aim(g, 0, 0, 0, 0, -60 * Deg).Pitch / Deg, 2);    // looking down over the bow: the gun stops above the heads

        // every aim the gun can take: the barrel and the first metres of the line of fire stay out of the two front riders' bodies, and the
        // line leaves the vehicle without crossing its own hit volume
        var front = new[] { Rider(Driver, DriverSeat), Rider(Passenger, ArmedSeat) };
        for (var yaw = -180; yaw < 180; yaw += 2)
            for (var ask = -90; ask <= 90; ask += 5)
            {
                var aim = DoorGunRules.FromLocal(g, 0, 0, 0, yaw * Deg, ask * Deg);
                var pivot = DoorGunRules.GunPivot(g, Vector3.Zero, 0, 0, 0, aim);
                foreach (var rider in front)
                {
                    var (a, b) = HitMath.Axis(rider);
                    var body = HitMath.RayCapsule(pivot, aim.Direction, a, b, PlayerHitBox.Radius);
                    Assert.True(body is null || body > 6f, $"yaw {yaw}, pitch {aim.Pitch / Deg:F0}: the line of fire meets rider {rider.Id} {body:F2} m from the gun");
                }
                Assert.Null(VehicleBox.Ray(DoorGunRules.Muzzle(g, pivot, aim), aim.Direction, 50f, Vector3.Zero, 0, Car.HalfWidth, Car.HalfLength, Car.Height));
                Assert.True(DoorGunRules.Muzzle(g, pivot, aim).Y > Car.Height, "the muzzle is above the hull");
            }
    }

    [Fact]
    public void The_door_gun_is_what_it_was()
    {
        var heli = Shipped.Vehicles.Single(v => v.Id == "utility_helicopter");
        var g = heli.DoorGun!;
        Assert.Equal(0f, g.RingRadius);
        Assert.Equal(0f, g.GuardArcDeg);
        // a pintle: the gun's pivot is the mount's, whatever the aim, and its lowest pitch is the same all across its arc
        foreach (var yaw in new[] { -60f, 0f, 45f })
        {
            var aim = DoorGunRules.FromLocal(g, 0.3f, 0.1f, -0.05f, yaw * Deg, 0);
            var at = new Vector3(5, 20, -3);
            Assert.Equal(DoorGunRules.Pivot(g, at, 0.3f, 0.1f, -0.05f), DoorGunRules.GunPivot(g, at, 0.3f, 0.1f, -0.05f, aim));
            Assert.Equal(g.MinPitchDeg * Deg, DoorGunRules.MinPitch(g, yaw * Deg));
        }
    }
}
