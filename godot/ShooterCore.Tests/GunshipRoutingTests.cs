using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Ticket #157: the gunship damage routing is pure (<see cref="GunshipRouting"/>), and it must damage every target a gunship
/// weapon can meet: dummies in every stance, every vehicle type through its hit volume, and every blast in its radius.
/// The numbers asserted here are the ones docs/shooter-v2.md and the resolution comment give.
/// </summary>
public class GunshipRoutingTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static GunshipDef Heli => Shipped.Killstreaks.Gunship!;
    static GunshipDef Ac130 => Shipped.Killstreaks.Ac130!;

    const ushort Owner = 1, Dummy = 500;
    static readonly Vector3 Above = new(0, 30, 0); // a turret up in the air, the way a gunship shoots

    /// <summary>Every round that is not a rocket (the rocket sim is the player's): the helicopter's cannon and the AC-130's guns and shell.</summary>
    public static IEnumerable<object[]> AllGunshipWeapons() =>
        new[] { new object[] { "cannon", "gunship" }, new object[] { "gun25", "ac130" }, new object[] { "gun40", "ac130" }, new object[] { "howitzer", "ac130" } };

    static GunshipWeaponDef Weapon(string gunship, string id) => (gunship == "gunship" ? Heli : Ac130).Weapons.Single(w => w.Id == id);

    static GunshipVehicle VehicleAt(int index, VehicleDef def, Vector3 feet, float yaw) =>
        new(index, feet, yaw, def.HalfWidth, def.HalfLength, def.Height);

    static Vector3 Dir(Vector3 from, Vector3 to) => Vector3.Normalize(to - from);

    static HitTarget DummyAt(Stance stance, Vector3 feet) => new(Dummy, feet, stance, 0f);

    // --- dummies -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllGunshipWeapons))]
    public void Every_gunship_cannon_round_hits_a_dummy_standing_crouching_or_prone(string weaponId, string gun)
    {
        var def = Weapon(gun, weaponId);
        foreach (var stance in new[] { Stance.Stand, Stance.Crouch, Stance.Prone })
        {
            var t = DummyAt(stance, new Vector3(0, 0, -30));
            var route = GunshipRouting.Route(def, Above, Dir(Above, HitMath.Centre(t)), float.PositiveInfinity, new[] { t }, Owner, Array.Empty<GunshipVehicle>(), Shipped.Vehicles.BulletDamageMultiplier);
            Assert.True(route.Impact == GunshipImpact.Body && route.TargetId == Dummy, $"{weaponId} missed a {stance} dummy: {route.Impact}");
            Assert.Equal(def.Damage, route.Damage);
        }
    }

    [Fact]
    public void A_wall_in_front_of_a_dummy_stops_the_round_before_it()
    {
        var def = Weapon("gunship", "cannon");
        var t = DummyAt(Stance.Stand, new Vector3(0, 0, -30));
        var route = GunshipRouting.Route(def, Above, Dir(Above, HitMath.Centre(t)), 10f, new[] { t }, Owner, Array.Empty<GunshipVehicle>(), 0.5f);
        Assert.Equal(GunshipImpact.World, route.Impact);
        Assert.Equal(10f, route.Distance);
    }

    // --- vehicles ------------------------------------------------------------------------------------------

    [Fact]
    public void Every_vehicle_type_is_hit_by_a_cannon_round_through_its_hit_volume()
    {
        var def = Weapon("gunship", "cannon");
        var vehicles = Shipped.Vehicles.Vehicles;
        Assert.NotEmpty(vehicles);
        for (var i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            var feet = new Vector3(0, 0, -25);
            var box = VehicleAt(i, v, feet, 0.4f);
            var aim = feet + new Vector3(0, v.Height * 0.5f, 0);
            // a low shot from a person's height, so the round meets the side of the body
            var origin = new Vector3(0, 1.2f, 0);
            var route = GunshipRouting.Route(def, origin, Dir(origin, aim), float.PositiveInfinity, Array.Empty<HitTarget>(), Owner, new[] { box }, Shipped.Vehicles.BulletDamageMultiplier);
            Assert.True(route.Impact == GunshipImpact.Vehicle && route.VehicleIndex == i, $"{v.Id} was not hit by a cannon round");
            Assert.True(route.Damage > 0);
        }
    }

    [Fact]
    public void A_round_over_a_car_roof_misses_it_and_hits_the_ground_behind()
    {
        var def = Weapon("gunship", "cannon");
        var car = Shipped.Vehicles.Vehicles.Single(v => v.Id == "car");
        var box = VehicleAt(0, car, new Vector3(0, 0, -25), 0f);
        var origin = new Vector3(0, 1.2f, 0);
        var over = new Vector3(0, car.Height + 1f, -25);
        var route = GunshipRouting.Route(def, origin, Dir(origin, over), 100f, Array.Empty<HitTarget>(), Owner, new[] { box }, Shipped.Vehicles.BulletDamageMultiplier);
        Assert.NotEqual(GunshipImpact.Vehicle, route.Impact);
    }

    [Fact]
    public void A_car_between_the_gun_and_a_dummy_takes_the_round()
    {
        var def = Weapon("gunship", "cannon");
        var car = Shipped.Vehicles.Vehicles.Single(v => v.Id == "car");
        var box = VehicleAt(0, car, new Vector3(0, 0, -10), 0f);
        var t = DummyAt(Stance.Stand, new Vector3(0, 0, -20));
        var origin = new Vector3(0, 1.2f, 0);
        var route = GunshipRouting.Route(def, origin, Dir(origin, HitMath.Centre(t)), float.PositiveInfinity, new[] { t }, Owner, new[] { box }, Shipped.Vehicles.BulletDamageMultiplier);
        Assert.Equal(GunshipImpact.Vehicle, route.Impact);
    }

    // --- blasts ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllGunshipWeapons))]
    public void Every_blast_damages_a_dummy_in_its_radius_in_every_stance(string weaponId, string gun)
    {
        var def = Weapon(gun, weaponId);
        Assert.NotNull(def.Splash);
        var radius = def.Splash!.Radius;
        foreach (var stance in new[] { Stance.Stand, Stance.Crouch, Stance.Prone })
        {
            var t = DummyAt(stance, new Vector3(0, 0, -30));
            var at = HitMath.Centre(t) + new Vector3(MathF.Min(2f, radius * 0.6f), 0, 0);
            var hits = GunshipRouting.Burst(def, at, Owner, null, -1, new[] { t }, Array.Empty<GunshipVehicle>(), 1.5f);
            var hit = Assert.Single(hits);
            Assert.False(hit.IsVehicle);
            Assert.Equal(Dummy, hit.TargetId);
            Assert.True(hit.Damage > 0 && hit.Damage <= def.Damage, $"{weaponId} blast on a {stance} dummy: {hit.Damage}");
        }
    }

    [Fact]
    public void The_direct_hit_takes_the_full_damage_and_the_rest_of_the_radius_takes_the_falloff()
    {
        var def = Weapon("ac130", "gun40");
        var near = DummyAt(Stance.Stand, new Vector3(0, 0, -30));
        var far = new HitTarget(Dummy + 1, new Vector3(0, 0, -30 - (def.Splash!.Radius - 1f)), Stance.Stand, 0f);
        var at = HitMath.Centre(near);
        var hits = GunshipRouting.Burst(def, at, Owner, Dummy, -1, new[] { near, far }, Array.Empty<GunshipVehicle>(), 1f);
        var direct = hits.Single(h => h.Direct);
        Assert.Equal(def.Damage, direct.Damage);
        var splash = hits.Single(h => !h.Direct);
        Assert.InRange(splash.Damage, def.Splash.EdgeDamage, def.Damage);
    }

    [Fact]
    public void The_owner_takes_nothing_from_its_own_guns()
    {
        var def = Weapon("ac130", "gun25");
        var self = DummyAt(Stance.Stand, new Vector3(0, 0, -30));
        var self2 = new HitTarget(Owner, self.Feet, Stance.Stand, 0f);
        var hits = GunshipRouting.Burst(def, HitMath.Centre(self2), Owner, null, -1, new[] { self2 }, Array.Empty<GunshipVehicle>(), 1.5f);
        Assert.Empty(hits);
    }

    [Fact]
    public void A_blast_damages_a_car_in_its_radius_with_the_explosive_multiplier()
    {
        var def = Weapon("ac130", "gun40");
        var car = Shipped.Vehicles.Vehicles.Single(v => v.Id == "car");
        var box = VehicleAt(3, car, new Vector3(0, 0, -30), 0f);
        var at = new Vector3(0, 0.5f, -30 + car.HalfLength + 1f); // a metre off the car's rear face
        var explosive = Shipped.Vehicles.RocketDamageMultiplier;
        var one = Assert.Single(GunshipRouting.Burst(def, at, Owner, null, -1, Array.Empty<HitTarget>(), new[] { box }, 1f));
        var multiplied = Assert.Single(GunshipRouting.Burst(def, at, Owner, null, -1, Array.Empty<HitTarget>(), new[] { box }, explosive));
        Assert.True(one.IsVehicle && one.VehicleIndex == 3);
        Assert.Equal(one.Damage * explosive, multiplied.Damage, 0.01f);
        Assert.True(multiplied.Damage > 0);
    }

    [Fact]
    public void A_direct_car_hit_takes_the_full_round_damage()
    {
        var def = Weapon("ac130", "howitzer");
        var car = Shipped.Vehicles.Vehicles.Single(v => v.Id == "car");
        var box = VehicleAt(0, car, new Vector3(0, 0, -30), 0f);
        var hit = Assert.Single(GunshipRouting.Burst(def, new Vector3(0, 0.5f, -30), Owner, null, 0, Array.Empty<HitTarget>(), new[] { box }, 1.5f));
        Assert.True(hit.Direct);
        Assert.Equal(def.Damage * 1.5f, hit.Damage, 0.01f);
    }

    // --- the numbers the resolution comment states ----------------------------------------------------------

    /// <summary>Rounds of a direct hit that destroy a car (hp from vehicles.json), with the explosive multiplier on the round.</summary>
    static int RoundsToDestroyCar(float roundDamage)
    {
        var car = Shipped.Vehicles.Vehicles.Single(v => v.Id == "car");
        return (int)MathF.Ceiling(car.Hp / (roundDamage * Shipped.Vehicles.RocketDamageMultiplier));
    }

    [Fact]
    public void Rounds_to_destroy_a_car_match_the_numbers_on_the_issue()
    {
        Assert.Equal(4, RoundsToDestroyCar(Weapon("ac130", "gun25").Damage));       // 25 mm: 50 x 1.5 = 75
        Assert.Equal(3, RoundsToDestroyCar(Weapon("ac130", "gun40").Damage));       // 40 mm: 80 x 1.5 = 120
        Assert.Equal(2, RoundsToDestroyCar(Weapon("ac130", "howitzer").Damage));    // 105 mm: 150 x 1.5 = 225
        Assert.Equal(3, RoundsToDestroyCar(Weapon("gunship", "cannon").Damage));    // helicopter cannon: 75 x 1.5 = 112.5
        Assert.Equal(2, RoundsToDestroyCar(100f));                                  // rocket (bazooka): 100 x 1.5 = 150
    }

    [Theory]
    [MemberData(nameof(AllGunshipWeapons))]
    public void Direct_damage_per_second_on_a_body_stays_in_a_sensible_band(string weaponId, string gun)
    {
        var def = Weapon(gun, weaponId);
        var dps = def.Damage / def.IntervalS;
        Assert.InRange(dps, 100f, 1500f); // a player has 100 hp: every gun kills in about a second or less of direct fire (the user raised the damage: heli 75, 25 mm 50, 40 mm 80)
    }
}
