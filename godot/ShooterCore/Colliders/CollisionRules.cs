namespace Drycastle.ShooterCore;

/// <summary>What moves (ADR 0028). Bigfoot is not one: its rules stay in its own code.</summary>
public enum MoverKind : byte
{
    /// <summary>A player or a bot, on foot.</summary>
    Player,
    GroundVehicle,
    Aircraft,
    /// <summary>The Thieves event's raid van: scripted along its route.</summary>
    RaidVan,
    /// <summary>Wildlife and pets.</summary>
    Animal,
    /// <summary>A navmesh walker: citizen, gang member, worker, a bot's route.</summary>
    Walker,
    /// <summary>Bullet, rocket, gunship round, turret sight, demon fireball.</summary>
    Shot,
    Splash,
    /// <summary>An item, a corpse or a care package falling to the ground.</summary>
    Drop,
    /// <summary>A shell casing (client only).</summary>
    Casing,
}

/// <summary>
/// What is met (ADR 0028). The static world classes share physics layers (<see cref="CollisionRules.LayerOf"/>); the rest are
/// told apart by the server's own lists.
/// </summary>
public enum ObstacleClass : byte
{
    Terrain,
    /// <summary>Water deeper than the mover may enter. Not a physics body: only the animals' row stops on it, every other mover has its own water rule (ADR 0023, a water craft).</summary>
    DeepWater,
    Building,
    Fence,
    PlacedSolid,
    Trunk,
    /// <summary>Tree crown: shot-only cover.</summary>
    Crown,
    Bush,
    ClosedDoor,
    /// <summary>A vehicle as a solid body: its solid box for on foot movement. A moving one for an NPC or animal.</summary>
    Vehicle,
    ParkedVehicle,
    /// <summary>A vehicle's analytic hit box (<c>VehicleBox</c>): what a shot or a blast tests, no physics body.</summary>
    VehicleHull,
    /// <summary>A raid van that is parked (its solid box is a physics body like a vehicle's).</summary>
    RaidVan,
    Player,
    /// <summary>A wild animal of another herd than the mover's.</summary>
    Wildlife,
    SameHerd,
    Pet,
    Demon,
    Citizen,
    Gang,
    Worker,
    TargetDummy,
}

/// <summary>Collide: the mover is stopped (or the shot, the blast or the drop lands). Pass: goes through. RunOver: the mover is not stopped and the obstacle is hurt (a vehicle runs it over, a shot or a blast hits it).</summary>
public enum Rule : byte { Collide = 0, Pass, RunOver }

/// <summary>
/// The rule table of ADR 0028: <see cref="MoverKind"/> x <see cref="ObstacleClass"/> -> <see cref="Rule"/>, default Collide. An exemption is a row
/// here, never a mask at a call site: <see cref="StaticMask"/> and <see cref="BodyMask"/> turn a row into the physics mask.
/// </summary>
public static class CollisionRules
{
    static readonly int MoverCount = Enum.GetValues<MoverKind>().Length;
    static readonly int ObstacleCount = Enum.GetValues<ObstacleClass>().Length;
    /// <summary>Terrain, building, fence, placed solid, trunk, closed door: all layer 1.</summary>
    static readonly ObstacleClass[] Walls = [ObstacleClass.Terrain, ObstacleClass.Building, ObstacleClass.Fence, ObstacleClass.PlacedSolid, ObstacleClass.Trunk, ObstacleClass.ClosedDoor];
    static readonly ObstacleClass[] Cover = [ObstacleClass.Crown, ObstacleClass.Bush];
    static readonly ObstacleClass[] Npcs = [ObstacleClass.Citizen, ObstacleClass.Gang, ObstacleClass.Worker];
    static readonly Rule[,] Table = Build(); // after the groups it reads

    /// <summary>The rule for <paramref name="mover"/> meeting <paramref name="obstacle"/>.</summary>
    public static Rule Of(MoverKind mover, ObstacleClass obstacle) => Table[(int)mover, (int)obstacle];

    public static bool Collides(MoverKind mover, ObstacleClass obstacle) => Of(mover, obstacle) == Rule.Collide;

    /// <summary>
    /// The class of the live target with this hit id, so a mover's row can be asked about it: the id ranges are the one place that tells a player, a citizen, a worker, a gang
    /// member, a Raid van, a wild animal, a pet, the demon and a target dummy apart. Null for what has no row: the Bigfoot (deferred, ADR 0028), a rocket, a vehicle's record id.
    /// </summary>
    public static ObstacleClass? ClassOfHitId(ushort id)
    {
        if (id >= PlayerIds.First && id <= PlayerIds.Last) return ObstacleClass.Player;
        if (DemonRules.IsHitId(id)) return ObstacleClass.Demon;
        if (CitizenRules.IsHitId(id)) return id >= CitizenRules.FirstWorkerHitId ? ObstacleClass.Worker : ObstacleClass.Citizen;
        if (GangRules.IsHitId(id)) return ObstacleClass.Gang;
        if (RaidVanRules.IsHitId(id)) return ObstacleClass.RaidVan;
        if (WildlifeRules.IsHitId(id)) return ObstacleClass.Wildlife;
        if (PetRules.IsHitId(id)) return ObstacleClass.Pet;
        if (id >= TargetDummies.FirstId && id < BigfootRules.TargetId) return ObstacleClass.TargetDummy;
        return null;
    }

    /// <summary>The physics layer the obstacle's body is on; 0 for a class that is no physics body (the server's lists answer for it).</summary>
    public static uint LayerOf(ObstacleClass obstacle) => obstacle switch
    {
        ObstacleClass.Terrain or ObstacleClass.Building or ObstacleClass.Fence or ObstacleClass.PlacedSolid or ObstacleClass.Trunk or ObstacleClass.ClosedDoor => CollisionLayers.World,
        ObstacleClass.Crown or ObstacleClass.Bush => CollisionLayers.ShotOnly,
        ObstacleClass.Vehicle or ObstacleClass.ParkedVehicle or ObstacleClass.RaidVan => CollisionLayers.VehicleSolid,
        ObstacleClass.Player => CollisionLayers.Players,
        ObstacleClass.Demon => CollisionLayers.Demon,
        _ => 0,
    };

    /// <summary>The physics mask for the static world: the layers of every wall and cover class the mover Collides with (World, ShotOnly). A ray for this mover uses it.</summary>
    public static uint StaticMask(MoverKind mover) => MaskOf(mover, CollisionLayers.World | CollisionLayers.ShotOnly);

    /// <summary>
    /// The mask of the mover's own body: <see cref="StaticMask"/> plus the vehicles' solid boxes, the players' bodies and the demon's body it Collides with. An on-foot player
    /// gets World | VehicleSolid | Demon (<see cref="CollisionLayers.OnFoot"/>), and no Players layer: it passes other players.
    /// </summary>
    public static uint BodyMask(MoverKind mover) => MaskOf(mover, CollisionLayers.World | CollisionLayers.ShotOnly | CollisionLayers.VehicleSolid | CollisionLayers.Players | CollisionLayers.Demon);

    /// <summary>
    /// What a vehicle's own sweep (its body, or the probe that stands for it) stops on: <see cref="StaticMask"/> and the demon's body when its row Collides with it. Not the vehicles'
    /// solid boxes (a vehicle meets another by an overlap test, <c>VehicleContact</c>) and not the players (it runs them over). A ground ray for the ground under a wheel is <see cref="StaticMask"/>.
    /// </summary>
    public static uint SweepMask(MoverKind mover) => MaskOf(mover, CollisionLayers.World | CollisionLayers.ShotOnly | CollisionLayers.Demon);

    /// <summary>
    /// <see cref="BodyMask"/> without the players' bodies: the static world, the vehicles' solid boxes and the demon's body the mover Collides with. For a mover that tests players
    /// itself, analytically (the animals: no physics body of theirs, so a player is a footprint in ShooterCore, not a ray hit).
    /// </summary>
    public static uint SolidMask(MoverKind mover) => MaskOf(mover, CollisionLayers.World | CollisionLayers.ShotOnly | CollisionLayers.VehicleSolid | CollisionLayers.Demon);

    static uint MaskOf(MoverKind mover, uint layers)
    {
        uint mask = 0;
        for (var o = 0; o < ObstacleCount; o++)
        {
            var layer = LayerOf((ObstacleClass)o);
            if (layer != 0 && (layer & layers) != 0 && Table[(int)mover, o] == Rule.Collide) mask |= layer;
        }
        return mask;
    }

    static Rule[,] Build()
    {
        var t = new Rule[MoverCount, ObstacleCount]; // Collide is 0: every cell starts as the default
        void Set(MoverKind m, Rule rule, params IEnumerable<ObstacleClass>[] groups)
        {
            foreach (var g in groups) foreach (var o in g) t[(int)m, (int)o] = rule;
        }
        IEnumerable<ObstacleClass> One(ObstacleClass o) => [o];
        var (water, hull, same, dummy) = (One(ObstacleClass.DeepWater), One(ObstacleClass.VehicleHull), One(ObstacleClass.SameHerd), One(ObstacleClass.TargetDummy));
        var (player, vehicle, parked, van) = (One(ObstacleClass.Player), One(ObstacleClass.Vehicle), One(ObstacleClass.ParkedVehicle), One(ObstacleClass.RaidVan));
        var (wild, pet, demon) = (One(ObstacleClass.Wildlife), One(ObstacleClass.Pet), One(ObstacleClass.Demon));

        // the classes no mover meets as a body (water, the analytic hull, a herd mate) pass by default; a row sets the exceptions below
        foreach (MoverKind m in Enum.GetValues<MoverKind>()) Set(m, Rule.Pass, water, hull, same);

        // Player, bot: terrain, building, fence, placed solid, trunk, closed door, vehicle, parked raid van, wildlife, pet, demon | passes crown, bush, other player, citizen, gang, worker
        Set(MoverKind.Player, Rule.Pass, Cover, player, Npcs, dummy); // a target dummy has no body to stop on

        // Ground vehicle: terrain, building, fence, placed solid, trunk, closed door, other vehicle, demon | passes crown, bush | runs over player, target dummy, wildlife, pet, citizen, gang, worker
        Set(MoverKind.GroundVehicle, Rule.Pass, Cover);
        Set(MoverKind.GroundVehicle, Rule.RunOver, player, dummy, wild, pet, Npcs);

        // Aircraft: as a ground vehicle, plus crown and bush
        Set(MoverKind.Aircraft, Rule.RunOver, player, dummy, wild, pet, Npcs); // when low: the caller's condition

        // Raid van: follows its route (nothing of the world stops it); shoves vehicles; runs over a player (moving), gang and every other NPC
        Set(MoverKind.RaidVan, Rule.Pass, Walls, Cover, vehicle, parked, van, demon);
        Set(MoverKind.RaidVan, Rule.RunOver, player, dummy, wild, pet, Npcs); // not the demon: a hit count, not hit points

        // Wildlife, pet: terrain, deep water, building, fence, placed solid, trunk, closed door, parked vehicle, player, pet, wildlife of another herd | passes crown, bush, same herd, citizen, gang, worker
        Set(MoverKind.Animal, Rule.Collide, water);
        Set(MoverKind.Animal, Rule.Pass, Cover, same, Npcs, hull, dummy);

        // Citizen, gang, worker, bot route: terrain, building, fence, placed solid, trunk, door closed now, parked vehicle | passes crown, bush, player, other NPC, wildlife, pet
        // a moving vehicle runs a walker over, so the walker does not steer for it: Pass
        Set(MoverKind.Walker, Rule.Pass, Cover, vehicle, van, player, wild, pet, demon, dummy, Npcs);

        // Shot: terrain, building, fence, placed solid, trunk, crown, bush, closed door, vehicle (analytic hull) | hits player and NPC
        Set(MoverKind.Shot, Rule.Pass, vehicle, parked, van); // the physical solid boxes never stop a shot; the analytic hull does
        Set(MoverKind.Shot, Rule.Collide, hull);
        Set(MoverKind.Shot, Rule.RunOver, player, dummy, wild, pet, demon, Npcs);

        // Splash: any wall (one line of sight per target) | hurts player, NPC; x0.5 through a vehicle hull for a covered seat, full for an exposed seat
        Set(MoverKind.Splash, Rule.Pass, vehicle, parked, van); // the hull is a damage factor (RunOver of the victims), not a wall
        Set(MoverKind.Splash, Rule.RunOver, player, dummy, wild, pet, demon, Npcs);

        // Drop: terrain, building, trunk, door (lands on it); fence and placed solid are the same layer | passes crown, bush, vehicle (lands under it), player, NPC
        Set(MoverKind.Drop, Rule.Pass, Cover, vehicle, parked, van, player, dummy, wild, pet, demon, Npcs);

        // Casing (client only): terrain, building, trunk, door, vehicle roof from outside | passes crown, bush, player, NPC; water (sinks to the bed, no clink)
        Set(MoverKind.Casing, Rule.Pass, Cover, parked, van, player, dummy, wild, pet, demon, Npcs);
        return t;
    }
}
