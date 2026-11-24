using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Colliders;

/// <summary>The mover x obstacle rule table of ADR 0028 and the physics masks it gives.</summary>
public class CollisionRulesTests
{
    static readonly ObstacleClass[] Walls = [ObstacleClass.Terrain, ObstacleClass.Building, ObstacleClass.Fence, ObstacleClass.PlacedSolid, ObstacleClass.Trunk, ObstacleClass.ClosedDoor];
    static readonly ObstacleClass[] Npcs = [ObstacleClass.Citizen, ObstacleClass.Gang, ObstacleClass.Worker];

    static void Expect(MoverKind m, Rule rule, params ObstacleClass[] classes)
    {
        foreach (var o in classes) Assert.True(CollisionRules.Of(m, o) == rule, $"{m} x {o}: want {rule}, got {CollisionRules.Of(m, o)}");
    }

    [Fact]
    public void Player_stops_on_the_world_vehicles_and_animals_and_passes_cover_players_and_npcs()
    {
        Expect(MoverKind.Player, Rule.Collide, Walls);
        Expect(MoverKind.Player, Rule.Collide, ObstacleClass.Vehicle, ObstacleClass.ParkedVehicle, ObstacleClass.RaidVan, ObstacleClass.Wildlife, ObstacleClass.Pet, ObstacleClass.Demon);
        Expect(MoverKind.Player, Rule.Pass, ObstacleClass.Crown, ObstacleClass.Bush, ObstacleClass.Player);
        Expect(MoverKind.Player, Rule.Pass, Npcs);
    }

    [Fact]
    public void GroundVehicle_runs_over_what_walks_and_passes_cover()
    {
        Expect(MoverKind.GroundVehicle, Rule.Collide, Walls);
        Expect(MoverKind.GroundVehicle, Rule.Collide, ObstacleClass.Vehicle, ObstacleClass.ParkedVehicle, ObstacleClass.Demon);
        Expect(MoverKind.GroundVehicle, Rule.Pass, ObstacleClass.Crown, ObstacleClass.Bush);
        Expect(MoverKind.GroundVehicle, Rule.RunOver, ObstacleClass.Player, ObstacleClass.TargetDummy, ObstacleClass.Wildlife, ObstacleClass.Pet);
        Expect(MoverKind.GroundVehicle, Rule.RunOver, Npcs);
    }

    [Fact]
    public void Aircraft_is_a_ground_vehicle_that_also_stops_on_crowns_and_bushes()
    {
        foreach (var o in Enum.GetValues<ObstacleClass>())
        {
            var car = CollisionRules.Of(MoverKind.GroundVehicle, o);
            var air = CollisionRules.Of(MoverKind.Aircraft, o);
            if (o is ObstacleClass.Crown or ObstacleClass.Bush) Assert.Equal(Rule.Collide, air);
            else Assert.Equal(car, air);
        }
    }

    [Fact]
    public void RaidVan_follows_its_route_and_runs_over_players_and_npcs()
    {
        Expect(MoverKind.RaidVan, Rule.Pass, Walls);
        Expect(MoverKind.RaidVan, Rule.Pass, ObstacleClass.Vehicle, ObstacleClass.ParkedVehicle);
        Expect(MoverKind.RaidVan, Rule.RunOver, ObstacleClass.Player, ObstacleClass.Gang, ObstacleClass.Citizen, ObstacleClass.Worker, ObstacleClass.Wildlife, ObstacleClass.Pet);
    }

    [Fact]
    public void Animal_stops_on_the_world_water_parked_vehicles_players_and_other_herds()
    {
        Expect(MoverKind.Animal, Rule.Collide, Walls);
        Expect(MoverKind.Animal, Rule.Collide, ObstacleClass.DeepWater, ObstacleClass.ParkedVehicle, ObstacleClass.Player, ObstacleClass.Pet, ObstacleClass.Wildlife);
        Expect(MoverKind.Animal, Rule.Pass, ObstacleClass.Crown, ObstacleClass.Bush, ObstacleClass.SameHerd);
        Expect(MoverKind.Animal, Rule.Pass, Npcs);
    }

    [Fact]
    public void Walker_stops_on_the_world_and_a_parked_vehicle_and_passes_everything_alive()
    {
        Expect(MoverKind.Walker, Rule.Collide, Walls);
        Expect(MoverKind.Walker, Rule.Collide, ObstacleClass.ParkedVehicle);
        Expect(MoverKind.Walker, Rule.Pass, ObstacleClass.Crown, ObstacleClass.Bush, ObstacleClass.Player, ObstacleClass.Wildlife, ObstacleClass.Pet, ObstacleClass.Vehicle);
        Expect(MoverKind.Walker, Rule.Pass, Npcs);
    }

    [Fact]
    public void Shot_stops_on_walls_cover_and_the_analytic_hull_and_hits_players_and_npcs()
    {
        Expect(MoverKind.Shot, Rule.Collide, Walls);
        Expect(MoverKind.Shot, Rule.Collide, ObstacleClass.Crown, ObstacleClass.Bush, ObstacleClass.VehicleHull);
        Expect(MoverKind.Shot, Rule.Pass, ObstacleClass.Vehicle, ObstacleClass.ParkedVehicle);
        Expect(MoverKind.Shot, Rule.RunOver, ObstacleClass.Player, ObstacleClass.Wildlife, ObstacleClass.Pet, ObstacleClass.TargetDummy);
        Expect(MoverKind.Shot, Rule.RunOver, Npcs);
    }

    [Fact]
    public void Splash_is_stopped_by_any_wall_a_shot_is_and_hurts_players_and_npcs()
    {
        foreach (var o in Walls.Append(ObstacleClass.Crown).Append(ObstacleClass.Bush)) Assert.Equal(Rule.Collide, CollisionRules.Of(MoverKind.Splash, o));
        Expect(MoverKind.Splash, Rule.RunOver, ObstacleClass.Player, ObstacleClass.Wildlife, ObstacleClass.Pet);
        Expect(MoverKind.Splash, Rule.RunOver, Npcs);
        Expect(MoverKind.Splash, Rule.Pass, ObstacleClass.VehicleHull);
    }

    [Fact]
    public void Drop_lands_on_the_ground_and_passes_vehicles_players_and_cover()
    {
        Expect(MoverKind.Drop, Rule.Collide, Walls);
        Expect(MoverKind.Drop, Rule.Pass, ObstacleClass.Crown, ObstacleClass.Bush, ObstacleClass.Vehicle, ObstacleClass.ParkedVehicle, ObstacleClass.Player);
        Expect(MoverKind.Drop, Rule.Pass, Npcs);
    }

    [Fact]
    public void Casing_lands_on_the_world_and_a_vehicle_roof_and_passes_cover_players_and_water()
    {
        Expect(MoverKind.Casing, Rule.Collide, ObstacleClass.Terrain, ObstacleClass.Building, ObstacleClass.Trunk, ObstacleClass.ClosedDoor, ObstacleClass.Vehicle);
        Expect(MoverKind.Casing, Rule.Pass, ObstacleClass.Crown, ObstacleClass.Bush, ObstacleClass.Player, ObstacleClass.DeepWater);
        Expect(MoverKind.Casing, Rule.Pass, Npcs);
        Expect(MoverKind.Casing, Rule.Collide, Walls);
    }

    [Fact]
    public void Casing_floor_ray_mask_is_world_and_vehicle_solid_only()
    {
        // no crowns (ShotOnly), no players, no demon
        Assert.Equal(CollisionLayers.World | CollisionLayers.VehicleSolid, CollisionRules.BodyMask(MoverKind.Casing));
    }

    [Fact]
    public void RaidVan_does_not_run_over_the_demon_a_hit_count_not_hit_points()
    {
        Assert.Equal(Rule.Pass, CollisionRules.Of(MoverKind.RaidVan, ObstacleClass.Demon));
        Expect(MoverKind.RaidVan, Rule.RunOver, ObstacleClass.TargetDummy, ObstacleClass.Citizen, ObstacleClass.Gang, ObstacleClass.Worker);
    }

    [Fact]
    public void The_demon_blocks_a_player_on_foot_and_a_ground_vehicle_and_an_aircraft_through_its_own_layer()
    {
        Assert.Equal(CollisionLayers.Demon, CollisionRules.LayerOf(ObstacleClass.Demon));
        Assert.NotEqual(0u, CollisionRules.BodyMask(MoverKind.Player) & CollisionLayers.Demon);
        Assert.NotEqual(0u, CollisionLayers.OnFoot & CollisionLayers.Demon);
        Assert.NotEqual(0u, CollisionRules.SweepMask(MoverKind.GroundVehicle) & CollisionLayers.Demon);
        Assert.NotEqual(0u, CollisionRules.SweepMask(MoverKind.Aircraft) & CollisionLayers.Demon);
        // a shot, a drop and a walker pass it; no ground ray meets it
        foreach (var m in new[] { MoverKind.Shot, MoverKind.Drop, MoverKind.Walker, MoverKind.RaidVan }) Assert.Equal(0u, CollisionRules.BodyMask(m) & CollisionLayers.Demon);
        foreach (var m in Enum.GetValues<MoverKind>()) Assert.Equal(0u, CollisionRules.StaticMask(m) & CollisionLayers.Demon);
    }

    [Fact]
    public void SweepMask_of_a_vehicle_is_the_world_and_the_demon_and_of_an_aircraft_also_the_cover()
    {
        Assert.Equal(CollisionLayers.World | CollisionLayers.Demon, CollisionRules.SweepMask(MoverKind.GroundVehicle));
        Assert.Equal(CollisionLayers.World | CollisionLayers.ShotOnly | CollisionLayers.Demon, CollisionRules.SweepMask(MoverKind.Aircraft));
    }

    [Theory]
    [InlineData(1, ObstacleClass.Player)]
    [InlineData(0x3FFF, ObstacleClass.Player)]
    [InlineData(DemonRules.HitId, ObstacleClass.Demon)]
    [InlineData(CitizenRules.FirstHitId, ObstacleClass.Citizen)]
    [InlineData(CitizenRules.FirstWorkerHitId, ObstacleClass.Worker)]
    [InlineData(GangRules.FirstHitId, ObstacleClass.Gang)]
    [InlineData(RaidVanRules.FirstHitId, ObstacleClass.RaidVan)]
    [InlineData(WildlifeRules.FirstHitId, ObstacleClass.Wildlife)]
    [InlineData(PetRules.FirstHitId, ObstacleClass.Pet)]
    [InlineData(TargetDummies.FirstId, ObstacleClass.TargetDummy)]
    public void A_hit_id_tells_its_obstacle_class(int id, ObstacleClass want) => Assert.Equal(want, CollisionRules.ClassOfHitId((ushort)id));

    [Fact]
    public void The_bigfoot_a_rocket_and_a_vehicle_record_have_no_class()
    {
        Assert.Null(CollisionRules.ClassOfHitId(BigfootRules.TargetId)); // deferred: no row
        Assert.Null(CollisionRules.ClassOfHitId(0x8001));
        Assert.Null(CollisionRules.ClassOfHitId(VehicleRecord.FirstId));
        Assert.Null(CollisionRules.ClassOfHitId(0));
    }

    [Fact]
    public void Every_live_target_class_a_vehicle_runs_over_has_an_id_range()
    {
        // what the run-over asks of a live target: the classes of the ground vehicle row's RunOver are all reachable from a hit id
        var reachable = new HashSet<ObstacleClass>();
        foreach (var id in new ushort[] { 1, DemonRules.HitId, CitizenRules.FirstHitId, CitizenRules.FirstWorkerHitId, GangRules.FirstHitId, WildlifeRules.FirstHitId, PetRules.FirstHitId, TargetDummies.FirstId })
            if (CollisionRules.ClassOfHitId(id) is { } c) reachable.Add(c);
        foreach (var o in Enum.GetValues<ObstacleClass>())
            if (CollisionRules.Of(MoverKind.GroundVehicle, o) == Rule.RunOver) Assert.Contains(o, reachable);
    }

    [Fact]
    public void Default_is_Collide()
    {
        // an obstacle a row does not name stops it: nobody is exempt by accident
        Assert.Equal(Rule.Collide, CollisionRules.Of(MoverKind.Player, ObstacleClass.Demon));
        Assert.Equal(Rule.Collide, CollisionRules.Of(MoverKind.Animal, ObstacleClass.Demon));
    }

    [Fact]
    public void StaticMask_of_each_mover_is_the_layers_it_stops_on()
    {
        Assert.Equal(CollisionLayers.World, CollisionRules.StaticMask(MoverKind.Player));
        Assert.Equal(CollisionLayers.World, CollisionRules.StaticMask(MoverKind.GroundVehicle));
        Assert.Equal(CollisionLayers.World | CollisionLayers.ShotOnly, CollisionRules.StaticMask(MoverKind.Aircraft));
        Assert.Equal(CollisionLayers.World | CollisionLayers.ShotOnly, CollisionRules.StaticMask(MoverKind.Shot));
        Assert.Equal(CollisionLayers.World | CollisionLayers.ShotOnly, CollisionRules.StaticMask(MoverKind.Splash));
        Assert.Equal(CollisionLayers.World, CollisionRules.StaticMask(MoverKind.Drop));
        Assert.Equal(CollisionLayers.World, CollisionRules.StaticMask(MoverKind.Animal));
        Assert.Equal(CollisionLayers.World, CollisionRules.StaticMask(MoverKind.Walker));
        Assert.Equal(0u, CollisionRules.StaticMask(MoverKind.RaidVan));
    }

    [Fact]
    public void BodyMask_of_a_player_is_the_on_foot_mask_and_has_no_player_layer()
    {
        Assert.Equal(CollisionLayers.OnFoot, CollisionRules.BodyMask(MoverKind.Player));
        Assert.Equal(0u, CollisionRules.BodyMask(MoverKind.Player) & CollisionLayers.Players);
    }

    [Fact]
    public void SolidMask_of_an_animal_is_the_world_the_vehicle_boxes_and_the_demon_and_never_players_or_cover()
    {
        Assert.Equal(CollisionLayers.World | CollisionLayers.VehicleSolid | CollisionLayers.Demon, CollisionRules.SolidMask(MoverKind.Animal));
        Assert.Equal(CollisionRules.BodyMask(MoverKind.Animal) & ~CollisionLayers.Players, CollisionRules.SolidMask(MoverKind.Animal));
    }

    [Fact]
    public void BodyMask_of_an_animal_stops_on_players()
    {
        Assert.NotEqual(0u, CollisionRules.BodyMask(MoverKind.Animal) & CollisionLayers.Players);
    }

    [Fact]
    public void A_shots_mask_never_holds_a_body_layer()
    {
        // a shot meets the players and the vehicles by their hit shapes, not by physics
        Assert.Equal(0u, CollisionRules.BodyMask(MoverKind.Shot) & (CollisionLayers.Players | CollisionLayers.VehicleSolid | CollisionLayers.Vehicles));
    }

    [Fact]
    public void Layers_are_distinct_bits()
    {
        uint[] all = [CollisionLayers.World, CollisionLayers.Vehicles, CollisionLayers.ShotOnly, CollisionLayers.VehicleSolid, CollisionLayers.Players, CollisionLayers.Demon];
        uint seen = 0;
        foreach (var l in all) { Assert.Equal(0u, seen & l); seen |= l; }
    }
}
