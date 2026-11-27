using Drycastle.ShooterCore.Citizens;
using Godot;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server;

/// <summary>What the host sets on the server before it starts: the map and the data files, the flags of the command line and the test modes. The server reads it and changes only <see cref="Spawns"/>, <see cref="KillY"/>, <see cref="Vehicles"/> and <see cref="Clock"/>.</summary>
public sealed class ServerConfig
{
    public Node3D World = null!;

    /// <summary>Spawn candidates (the map's pipeline spawn points); the match state machine picks among them.</summary>
    public List<System.Numerics.Vector3> Spawns = new();
    public MatchConfig MatchConfig = new();
    /// <summary>The day/night clock the server owns; sent to each client in the welcome (snapshots carry the tick).</summary>
    public DayClock Clock = new();
    /// <summary>Honour the client debug "kill" command (dev servers and the match smoke only).</summary>
    public bool DebugCommands;
    /// <summary>--allow-sandbox: grant sandbox mode (godmode, every killstreak on demand) to clients that ask for it (#156). Dev servers only.</summary>
    public bool AllowSandbox;
    /// <summary>Match smoke mode: QuitAfter is a failure deadline; the server finishes itself after one full round and restart.</summary>
    public bool MatchSmoke;
    /// <summary><c>--no-lagcomp</c>: shots resolve against present positions (the A/B baseline for the perf harness).</summary>
    public bool NoLagComp;
    /// <summary>glass.json; the defaults when the host sets none.</summary>
    public GlassConfig GlassData = new();
    /// <summary>house_destruction.json (docs/house-destruction.md); the defaults when the host sets none.</summary>
    public Drycastle.ShooterCore.Buildings.Destruction.DestructionConfig HouseDestruction = Drycastle.ShooterCore.Buildings.Destruction.DestructionConfig.Default;
    /// <summary>vehicles.json (run-over numbers, spawn count, car stats): a default table when the host sets none.</summary>
    public VehiclesFile Vehicles = null!;
    /// <summary>Vehicle type of the car smoke arena (vehicles.json id).</summary>
    public string SmokeKind = "car";
    /// <summary>turret.json: set by the host before the server starts.</summary>
    public TurretFile Turret = null!;
    /// <summary>weapons.json (hotbar, ammo, damage numbers).</summary>
    public WeaponsFile Weapons = null!;
    /// <summary>Weapons smoke mode: builds a flat test arena with a wall far from the map and judges the scenario (implies debug commands).</summary>
    public bool WeaponsSmoke;
    public float KillY = float.NegativeInfinity;
    /// <summary>The map's water (water.json); players swim at the surface in it, and boats float on it.</summary>
    public Drycastle.ShooterCore.Waters.Water Water = Drycastle.ShooterCore.Waters.Water.Empty;
    /// <summary>The map this server runs on (the parcels, the ground height and roads.json); null in tests.</summary>
    public Drycastle.MapLoader.MapLoader? Map;
    public int Port = 27015;
    /// <summary>Network impairment for the loss and latency test (--net-sim); null = none.</summary>
    public NetSim? Sim;
    /// <summary>Quit after this many seconds and report whether two players were seen moving (smoke test); 0 = run forever.</summary>
    public double QuitAfter;
    /// <summary>Dev mode (issue #149, <c>--unlock-all</c>): every item unlocked, showcase starter loadouts, nothing of it saved. Set by the host.</summary>
    public bool UnlockAll;
    /// <summary>camo.json as loaded by the host (set in <c>Shooter.BuildServer</c>).</summary>
    public CamoFile CamoData = null!;
    /// <summary>Progression store (ADR 0003); null = every player is a guest. Set by the host, which owns and disposes it.</summary>
    public IProgressionStore? Progression;
    /// <summary>perks.json: the perk of each loadout and its modifiers.</summary>
    public PerksFile Perks = null!;
    /// <summary>levels.json: the XP curve and awards.</summary>
    public LevelsFile Levels = null!;
    /// <summary>companions.json: the Companions of the game (set by the host).</summary>
    public CompanionsFile CompanionData = new();
    /// <summary>items.json: the equippable items. Empty when the data directory has none.</summary>
    public ItemsFile Items = new();
    /// <summary>animals.json: the species table (set by the host).</summary>
    public AnimalsFile AnimalData = new();
    /// <summary>The hit shapes of the wild species (set by the host): what a bullet is tested against.</summary>
    public AnimalShapes AnimalShapes = new();
    /// <summary>pets.json: which groups keep pets and the poop rules (set by the host).</summary>
    public PetsFile PetsData = new();
    /// <summary>wildlife.json: which wild species the map holds and how many (set by the host).</summary>
    public WildlifeFile WildlifeData = new();
    /// <summary>Player tokens (lower case compare) of the admins, from --admin-token. A guest (no token) is never an admin.</summary>
    public HashSet<string> AdminTokens = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>--admin-all (dev servers only): every human is an admin, so nobody has to look up a token. Release servers never pass it.</summary>
    public bool AdminAll;
    /// <summary>--bots N: how many bots join while at least one human is in the match. 0 = none. Read once at start; an admin changes the number in <see cref="BotServer"/>.</summary>
    public int Bots;
    /// <summary>Most bots a server takes (they need no peer slot, so this is apart from <see cref="Sessions.ConnectionServer.MaxPeers"/>).</summary>
    public const int MaxBots = ChatCommands.MaxBots;
    /// <summary>The map folder (for the navigation bake: its manifest lists the chunks) and where baked tiles are kept. Set by the host.</summary>
    public string MapDir = "";
    public string NavCacheDir = "";
    /// <summary>environment.json "lamp" and "sign_plate"; the defaults when the host sets none.</summary>
    public LampLook LampData = new();
    public SignPlateLook SignPlateData = new();
    /// <summary>holy_mass.json (set by the host): the citizens, the Mass.</summary>
    public HolyMassFile HolyMass = new();
    /// <summary>holy_mass_demon.json (set by the host). Without it (zero hits) the demon does not appear.</summary>
    public DemonFile Demon = new();
    /// <summary>killstreaks.json: the streak table. Set by the host before the server starts.</summary>
    public KillstreakFile Killstreaks = null!;
    /// <summary>thieves.json: every number of the Thieves event (set by the host).</summary>
    public ThievesFile ThievesData = new();
    /// <summary>plantation.json (the host sets it from the data directory; the shipped defaults otherwise).</summary>
    public PlantationFile Plantation = PlantationFile.Default;
    /// <summary>duffels.json (the host sets it from the data directory; the shipped defaults otherwise).</summary>
    public Drycastle.ShooterCore.Cartel.DuffelsFile Duffels = Drycastle.ShooterCore.Cartel.DuffelsFile.Default;
    /// <summary>bigfoot.json: every number of the fight.</summary>
    public BigfootFile BigfootData = new();
    /// <summary>Every number of the harvester event; the host may replace it.</summary>
    public HarvesterConfig Harvester = new();
    /// <summary>The numbers of the vehicles' crop damage (#328): what a ground vehicle crushes driving through a crop field.</summary>
    public CropCrushConfig CropCrush = new();
    /// <summary>scud.json: the launcher's timings, the targeting screen, the missile's flight and the blast. The host replaces the defaults with the file.</summary>
    public ScudFile Scud = ScudFile.Default;


    // The test and measuring modes (Diagnostics/); the hosts' flags set them, a production server leaves them off.
    public bool RpgSmoke;
    /// <summary>Car smoke: flat arena, three cars at fixed spots, debug commands, judges the vehicle scenario.</summary>
    public bool CarSmoke;
    /// <summary>Blast smoke (ticket #103): flat arena with three cars (two side by side, one far), judges vehicle damage, the blast and the chain reaction.</summary>
    public bool BlastSmoke;
    /// <summary>The smokes that play in the flat test arena instead of on the map.</summary>
    public bool ArenaMode => WeaponsSmoke || RpgSmoke || CarSmoke || BlastSmoke;
    /// <summary>--loss-test N: wait for N players, run the scripted round and record tick time, bandwidth and per-player counters. 0 = off.</summary>
    public int LossTest;
    public double LossMeasure = 30;
    /// <summary>Directory for server_states.csv (per-tick quantised states of every player, as sent).</summary>
    public string LossOut = "";
    /// <summary>--cluster-spawns: keep one dense cluster of spawn candidates, as the loss test does. A dev aid for trying bots on the full map.</summary>
    public bool ClusterSpawns;
    /// <summary>--perf-out DIR: write per-phase tick timings to DIR/server-ticks.csv (measurement only).</summary>
    public string PerfOut = "";
    /// <summary>--perf-lagcomp: with --perf-out, log every hitscan shot resolved three ways (<see cref="Diagnostics.LagCompLog"/>).</summary>
    public bool PerfLagComp;
}
