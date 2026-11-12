using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The flak truck's gunner sits in the open turret and can be shot (<see cref="GunnerCover"/>): a ray that reaches his body hits him instead of the hull
/// around him, and every other rider and vehicle stays as it was. The truck stands at the origin facing -Z, its gunner on the turret behind the cab.
/// </summary>
public class GunnerCoverTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static VehicleDef Flak => Shipped.Vehicles.Vehicles.Single(v => v.Id == "flak");

    const ushort Gunner = 7, Shooter = 1, Other = 9;
    static readonly Vector3 Muzzle = new(0, 2.4f, 20f);        // a shooter's eye, 20 m behind the truck's tail
    static readonly Vector3 Back = new(0, 0, -1);              // looking at the truck (it is -Z of the shooter)
    static HitTarget GunnerOnTurret => new(Gunner, new Vector3(0, 1.59f, 2.37f), Stance.Stand, 0);

    /// <summary>Where the hull's box first stops a ray (the server's world ray with the hull in), infinity when it misses.</summary>
    static float HullStop(Vector3 origin, Vector3 dir)
    {
        var f = Flak;
        return VehicleBox.Ray(origin, dir, 1000f, Vector3.Zero, 0, f.HalfWidth, f.HalfLength, f.Height)?.Distance ?? float.PositiveInfinity;
    }

    [Fact]
    public void The_shipped_flak_has_an_exposed_gunner_and_1200_hp_and_the_jeeps_roof_gunner_stays_covered()
    {
        Assert.True(Flak.ExposedGunner);
        Assert.Equal(1200f, Flak.Hp);
        // the other open seat is the utility helicopter's door gunner, in the open door; its pilot is behind the hull like every driver
        Assert.True(Shipped.Vehicles.Vehicles.Single(v => v.Id == "utility_helicopter").ExposedGunner);
        foreach (var v in Shipped.Vehicles.Vehicles.Where(v => v.Id is not ("flak" or "utility_helicopter"))) Assert.False(v.ExposedGunner, v.Id);
    }

    [Fact]
    public void An_exposed_gunner_needs_a_gunner_seat()
    {
        var v = new VehicleDef { Id = "t", TopSpeed = 10, Acceleration = 2, Brake = 5, TurnRadius = 5, MaxLateralAccel = 5, Mass = 1000, ExposedGunner = true };
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(new VehiclesFile { Vehicles = { v } }));
        v.Seats.Add(new VehicleSeatDef { Role = "gunner" });
        VehiclesFile.Validate(new VehiclesFile { Vehicles = { v } });
    }

    [Fact]
    public void The_hull_shadows_a_covered_gunner_but_not_an_exposed_one()
    {
        var hull = HullStop(Muzzle, Back);
        Assert.True(hull < 20f, "the ray meets the hull's tail first");
        var targets = new[] { GunnerOnTurret };
        Assert.Null(HitMath.Raycast(Muzzle, Back, 300f, hull, targets, Shooter)); // the old rule: the hull stops it
        Assert.Null(GunnerCover.Raycast(Muzzle, Back, 300f, hull, targets, Shooter, null, float.PositiveInfinity)); // covered (jeep): unchanged
        var hit = GunnerCover.Raycast(Muzzle, Back, 300f, hull, targets, Shooter, GunnerOnTurret, float.PositiveInfinity);
        Assert.NotNull(hit);
        Assert.Equal(Gunner, hit!.Value.Id);
        Assert.True(hit.Value.Distance > hull, "he stands inside the hull's box, behind its surface");
    }

    [Fact]
    public void A_ray_that_misses_the_gunner_still_reaches_only_the_hull()
    {
        var low = new Vector3(0, 0.8f, 20f); // into the bed, below the turret deck
        var hull = HullStop(low, Back);
        Assert.True(hull < float.PositiveInfinity);
        Assert.Null(GunnerCover.Raycast(low, Back, 300f, hull, new[] { GunnerOnTurret }, Shooter, GunnerOnTurret, float.PositiveInfinity)); // no player hit: the caller damages the hull
        var wide = new Vector3(0.9f, 2.4f, 20f); // past his shoulder
        Assert.Null(GunnerCover.Raycast(wide, Back, 300f, HullStop(wide, Back), new[] { GunnerOnTurret }, Shooter, GunnerOnTurret, float.PositiveInfinity));
    }

    [Fact]
    public void A_wall_beyond_the_hull_but_before_the_gunner_still_stops_the_shot()
    {
        var hull = HullStop(Muzzle, Back);
        var wallPast = hull + 0.3f; // something solid between the hull's tail and the gunner
        Assert.Null(GunnerCover.Raycast(Muzzle, Back, 300f, hull, new[] { GunnerOnTurret }, Shooter, GunnerOnTurret, wallPast));
    }

    [Fact]
    public void Someone_standing_in_front_of_the_truck_takes_the_shot_meant_for_the_gunner()
    {
        var front = new HitTarget(Other, new Vector3(0, 0.9f, 12f), Stance.Stand, 0); // on the ray (feet up a little so the 2.4 m eye line meets his chest), nearer than the hull
        var hit = GunnerCover.Raycast(Muzzle, Back, 300f, HullStop(Muzzle, Back), new[] { front, GunnerOnTurret }, Shooter, GunnerOnTurret, float.PositiveInfinity);
        Assert.Equal(Other, hit!.Value.Id);
    }

    [Fact]
    public void Another_rider_stays_covered_while_the_gunner_is_exposed()
    {
        var passenger = new HitTarget(Other, new Vector3(-1.0f, 0.95f, -1.0f), Stance.Stand, 0); // beside the gunner's line, inside the hull's box
        var at = new Vector3(-1.0f, 2.0f, 20f);
        Assert.True(HullStop(at, Back) < 20f);
        Assert.Null(GunnerCover.Raycast(at, Back, 300f, HullStop(at, Back), new[] { passenger, GunnerOnTurret }, Shooter, GunnerOnTurret, float.PositiveInfinity));
    }

    [Fact]
    public void The_gunner_cannot_shoot_himself()
    {
        Assert.Null(GunnerCover.Raycast(Muzzle, Back, 300f, HullStop(Muzzle, Back), new[] { GunnerOnTurret }, Gunner, GunnerOnTurret, float.PositiveInfinity));
    }

    [Fact]
    public void A_shot_through_the_head_is_a_headshot_with_the_usual_multiplier()
    {
        var g = GunnerOnTurret;
        var head = HitMath.HeadCenter(g);
        var from = head + new Vector3(0, 0.3f, 18f);
        var dir = Vector3.Normalize(head - from);
        var hit = GunnerCover.Raycast(from, dir, 300f, 10f /* the hull, 10 m out */, new[] { g }, Shooter, g, float.PositiveInfinity);
        Assert.True(hit!.Value.Head);
        var w = Shipped.Weapons.Weapons.Single(x => x.Id == "ak47");
        Assert.Equal(HitMath.HitscanDamage(w, hit.Value.Distance, true), w.Damage * w.HeadshotMultiplier, 2f); // within the falloff of 18 m
        Assert.True(HitMath.HitscanDamage(w, hit.Value.Distance, true) > HitMath.HitscanDamage(w, hit.Value.Distance, false));
    }

    [Fact]
    public void A_rocket_that_stopped_on_the_hull_reaches_the_exposed_gunner_as_a_direct_hit()
    {
        var hull = HullStop(Muzzle, Back);
        var len = 40f;
        var covered = RocketFlight.Step(Muzzle, Back, 0f, 40f, 3.75f, 1f, hull, new[] { GunnerOnTurret }, Shooter);
        Assert.Equal(RocketOutcome.World, covered.Outcome);
        var open = RocketFlight.Step(Muzzle, Back, 0f, len, 3.75f, 1f, hull, new[] { GunnerOnTurret }, Shooter, GunnerOnTurret, float.PositiveInfinity);
        Assert.Equal(RocketOutcome.Player, open.Outcome);
        Assert.Equal(Gunner, open.Victim);
    }

    [Fact]
    public void A_shotgun_pellet_through_the_hull_reaches_the_exposed_gunner()
    {
        var w = Shipped.Weapons.Weapons.First(x => x.Pellets > 1);
        var dirs = new[] { Back };
        var walls = new[] { HullStop(Muzzle, Back) };
        var covered = HitMath.ResolvePellets(w, Muzzle, dirs, 300f, walls, new[] { GunnerOnTurret }, Shooter);
        Assert.Empty(covered);
        var open = HitMath.ResolvePellets(w, Muzzle, dirs, 300f, walls, new[] { GunnerOnTurret }, Shooter, new HitTarget?[] { GunnerOnTurret }, new[] { float.PositiveInfinity });
        Assert.True(open.ContainsKey(Gunner));
    }
}
