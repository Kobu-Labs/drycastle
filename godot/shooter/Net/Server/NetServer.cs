using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Net.Server.BotRoster;
using Drycastle.Shooter.Net.Server.BusGarage;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.ChatRoom;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Crates;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Net.Server.Intel;
using Drycastle.Shooter.Net.Server.MineralLog;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Navigation;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Playground;
using Drycastle.Shooter.Net.Server.Profiles;
using Drycastle.Shooter.Net.Server.Remains;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Net.Server.Scenery;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Sentries;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Net.Server.Squads;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.CarePackages;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using Drycastle.ShooterServer.Persistence;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Authoritative 30 Hz server over raw ENet (see ADR 0002): ch0 reliable messages, ch1 unreliable-sequenced input and snapshots.
/// Each tick it polls the network, runs every player's next queued input through the shared movement step and
/// collision (<see cref="Player.Simulate"/>), then sends each client a delta snapshot with the last processed input seq.
/// </summary>
public partial class NetServer : Node
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
    /// <summary>Most bots a server takes (they need no peer slot, so this is apart from <see cref="MaxPeers"/>).</summary>
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

    // The features that are classes of their own (<c>Net/Server/&lt;Feature&gt;/</c>), made in _Ready once the match exists; each takes only the context.
    ServerContext _ctx = null!;
    Fleet _fleet = null!;
    readonly DeathPush _deathPush = new();
    CorpseBook _corpses = null!;
    RidingCorpses _riding = null!;
    CarSeats _seats = null!;
    CarDamage _carDamage = null!;
    CarBlast _carBlast = null!;
    CorpseServer _remains = null!;
    RunOverServer _runOver = null!;
    TowServer _tow = null!;
    RocketCarts _rocketCarts = null!;
    VehicleLoop _loop = null!;
    VehicleGuns _guns = null!;
    CarDealer _dealer = null!;
    PoseRewind _rewind = null!;
    GlassServer _glass = null!;
    ShotWorld _shot = null!;
    /// <summary>What a shot, a blast or a turret can hit, kind by kind in the order the list has them; made in <see cref="BuildMatch"/>, its sources named in <see cref="RegisterTargetSources"/>.</summary>
    TargetBook _targets = null!;
    ImpactFx _impacts = null!;
    Splash _splash = null!;
    ProgressionServer _progress = null!;
    NavService _navMesh = null!;
    BotServer _bots = null!;
    AdminSpawner _spawner = null!;
    SandboxServer _sandbox = null!;
    TeamServer _teams = null!;
    SceneryServer _scenery = null!;
    MineralsServer _minerals = null!;
    RozhlasServer _rozhlas = null!;
    GarageServer _garage = null!;
    IntelServer _intel = null!;
    ChatServer _chat = null!;
    ProfileServer _profiles = null!;
    CrateServer _crates = null!;
    SentryWorld _sentries = null!;
    HuntLogServer _hunts = null!;
    StatusServer _status = null!;
    ItemsServer _gear = null!;
    Sitters _sitters = null!;
    AnimalWorld _animals = null!;
    CompanionServer _companions = null!;
    PetServer _pets = null!;
    WildlifeServer _wildlife = null!;
    ThievesServer _thieves = null!;
    ChurchServer _church = null!;
    CitizenServer _citizens = null!;
    PounceServer _pounce = null!;
    DemonServer _demon = null!;
    MassServer _mass = null!;
    GunshipServer _gunships = null!;
    FlakServer _flak = null!;
    StreakServer _streaks = null!;
    LockOnServer _lockOn = null!;

    readonly ENetConnection _host = new();
    readonly Dictionary<ENetPacketPeer, ServerClient> _clients = new();
    uint _tick;
    readonly PlayerIds _ids = new();
    double _elapsed;
    readonly List<NetInput> _scratch = new();
    readonly Dictionary<ushort, ServerClient> _byId = new();
    /// <summary>The entity states of the recent ticks, shared by every client's encoder (the baselines of their deltas).</summary>
    readonly SnapshotHistory _sentStates = new();
    /// <summary>The world states of recent ticks the server sent (the world channel's baselines, shared by every client).</summary>
    readonly WorldHistory _sentWorld = new();
    MatchSim _match = null!;
    readonly Random _rng = new();
    /// <summary>What the smoke tests count (and their verdicts); nothing reads it on a production server.</summary>
    readonly SmokeStats _smoke = new();

    public override void _Ready()
    {
        PerfInit();
        if (!Listen()) return;
        Vehicles ??= new VehiclesFile { Vehicles = { new VehicleDef { Id = "car", TopSpeed = 44.4f, Acceleration = 3.5f, Brake = 9.5f, TurnRadius = 5.5f, MaxLateralAccel = 8, Mass = 1200 } } }; // the default table when the host sets none
        if (ArenaMode) BuildArena();
        BuildMatch();
        BuildServices();
        BuildWorldEntities();
        BuildFeatures();
        FinishStart();
    }

    /// <summary>Binds the port. False (and the server quits) when it cannot.</summary>
    bool Listen()
    {
        // one peer slot more than the players allowed: the connection past the limit is seen and told the server is full (T3-020)
        var err = _host.CreateHostBound("*", Port, MaxPeers + 1, Wire.Channels, 0, 0);
        if (err != Error.Ok)
        {
            GD.PrintErr($"server: cannot listen on port {Port}: {err}");
            GetTree().Quit(2);
            return false;
        }
        return true;
    }

    /// <summary>The match and the context every feature takes, and the blast kit. The first things made: the rest asks for them.</summary>
    void BuildMatch()
    {
        _match = new MatchSim(MatchConfig, () => _elapsed, Spawns);
        _match.Event += OnMatchEvent;
        _ctx = new ServerContext(_match, _byId, _clients.Values, () => _elapsed, () => _tick, BroadcastEvent, Send, (c, chan, packet) => { if (c.Peer is { } peer) Tx(peer, chan, packet, 0); });
        _fleet = new Fleet(_byId, World, Water, Map);
        _progress = new ProgressionServer(_ctx, Progression, Levels, Weapons, Perks, ArenaMode || DebugCommands, (c, l) => _streaks.Arm(c, l));
        _impacts = new ImpactFx(_ctx, Map, Water);
        _splash = new Splash(_ctx, () => MapShelters, () => Query, car => _rewind.PoseOf(car));
        RegisterTargetSources();
    }

    /// <summary>What the features share (animals, seats, targets) and the features of the village: the church, the radio, the chat, the profiles, the hunts, the statuses and the demon.</summary>
    void BuildServices()
    {
        _navMesh = new NavService(_ctx, World, MapDir, NavCacheDir, ArenaMode, () => _host.Flush(), (ticket, route) => _thieves.DeliverRoute(ticket, route));
        _animals = new AnimalWorld(_ctx, _fleet, Map, Water, () => Query, () => MapFlightBounds);
        _scenery = new SceneryServer(_ctx, Map, LampData, SignPlateData);
        _sitters = new Sitters(_ctx, OnStood);
        _church = new ChurchServer(_ctx, ArenaMode, MapDir);
        _citizens = new CitizenServer(_ctx, HolyMass, ArenaMode, MapDir, Map, () => _navMesh.Mesh, () => WalkerWorld, _navMesh.Ensure);
        _rozhlas = new RozhlasServer(_ctx, ArenaMode, MapDir, HolyMass, Weapons, _citizens.Hits, new TableSeating(_sitters.IsSeated, _sitters.StandUpAtTableExcept, _sitters.SitAtTable));
        _intel = new IntelServer(_ctx);
        _chat = new ChatServer(_ctx, admin => new ChatContext(admin, Vehicles?.Vehicles.Select(v => v.Id).ToList() ?? new List<string>(), Items.Items.Select(i => i.Id).ToList(), CompanionData.Companions.Select(i => i.Id).ToList()), AdminTokens, AdminAll);
        _profiles = new ProfileServer(_ctx, Progression, UnlockAll, Weapons, Perks, CamoData, Killstreaks, Join, OnProfileAttached, AllowSave);
        _bots = new BotServer(_ctx, _navMesh, _profiles, _progress, _sitters, Weapons, _rng, Bots, NewPlayerId, Join, Leave, WorldDistance);
        _spawner = new AdminSpawner(_ctx, () => Vehicles, _fleet, World, Water, () => MapFlightBounds, _rng);
        _sandbox = new SandboxServer(_ctx, AllowSandbox, () => _streaks, _spawner, () => Spawns, () => Query, SendAmmo, LeaveSeat, _rng);
        _teams = new TeamServer(_ctx, _chat, _sandbox);
        _garage = new GarageServer(_ctx, ArenaMode, MapDir, _fleet.Cars, () => Vehicles);
        _minerals = new MineralsServer(_ctx, Progression, () => _wildlife.Keeper.Drops);
        _hunts = new HuntLogServer(_ctx, Progression, AnimalData, Weapons, Items, CompanionData, _progress.Scores, _progress.WeaponKey,
            c => { _profiles.Send(c); _companions.FromProfile(c); if (c.Joined) _companions.SendRoster(c); });
        _sentries = new SentryWorld(_ctx, _targets, Deal, () => Query);
        _status = new StatusServer(_ctx, Deal, Weapons, GetHigh);
        _pounce = new PounceServer(_ctx, () => Demon, _status);
        _demon = new DemonServer(_ctx, () => Demon, () => _mass, _church, _citizens, _sitters, _status, _pounce, _targets, Deal, () => Query, (at, radius) => _glass.Blast(at, radius), () => MapShelters, Weapons, MapDir, World, _events);
        _rewind = new PoseRewind(_ctx, _fleet, _targets, _sitters, _pounce, NoLagComp);
        _glass = new GlassServer(_ctx, _fleet, _rewind, Map, GlassData);
        _shot = new ShotWorld(_ctx, _fleet, _rewind, _sitters, _pounce, _targets, _glass, World, _perf != null);
        _carDamage = new CarDamage(_ctx, _fleet, _glass, _smoke, Vehicles);
    }

    /// <summary>What a shot can hit, kind by kind; the order of the sources is the order of every target list. A kind that stood elsewhere on the shooter's screen says where.</summary>
    void RegisterTargetSources()
    {
        _targets = new TargetBook(() => _byId.Count);
        _targets.AddSource(list => _shot.AddPlayers(list), (List<HitTarget> list, in ShotView view) => _shot.AddPlayersSeen(list, view));
        _targets.AddSource(AddDummyTargets);
        _targets.AddSource(list => { if (BigfootTarget() is { } bigfoot) list.Add(bigfoot); }, // the Bigfoot is a hit target like a player (issue #264), rewound like one
            (List<HitTarget> list, in ShotView view) => { if (BigfootTarget() is { } bigfoot) list.Add(BigfootRewound(bigfoot, view.Tick)); });
        _targets.AddSource(list => _citizens.Crowd?.AddTargets(list)); // the citizens of the Holy Mass walk slowly: not rewound
        _targets.AddSource(list => _thieves.AddTargets(list), (List<HitTarget> list, in ShotView view) => _thieves.AddTargets(list, view.Tick)); // the Gang members and their Raid vans
        _targets.AddSource(AddAnimalTargets, (List<HitTarget> list, in ShotView view) => { _pets.AddTargets(list); _wildlife.AddTargets(list, view.BackTicks / (float)Wire.TickRate); }); // wild animals in the pose the shooter saw
        _targets.AddSource(list => _rozhlas.AddTargets(list)); // the workers of the Urad
        _targets.AddSource(list => _demon.AddTarget(list));
    }

    /// <summary>The world entities' end of life and their types, in the order they register, and what follows the same despawns (the corpse book, the Bigfoot).</summary>
    void BuildWorldEntities()
    {
        _match.Entities.Log = s => GD.PrintErr($"server: {s}");
        _match.Entities.Spawned += w => BroadcastEvent(EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State));
        _match.Entities.Despawned += w => BroadcastEvent(EventKind.WorldDespawn, WorldCodec.EncodeDespawn(w.Id, w.Reason));
        CorpseRules.Register(WorldEntityRegistry.Shared);
        GroundItemRules.Register(WorldEntityRegistry.Shared);
        TurretRules.Register(WorldEntityRegistry.Shared);
        CitizenRules.Register(WorldEntityRegistry.Shared);
        _corpses = new CorpseBook(_match.Entities);
        _gear = new ItemsServer(_ctx, Items, _corpses, () => Query, car => CarPose.Present(car).Feet);
        _riding = new RidingCorpses(_corpses);
        _seats = new CarSeats(_ctx, _fleet, _riding, _shot, _smoke, () => Vehicles, () => Clock.HourAt(_elapsed), id => _streaks.Rules.ControlOf(id) != null);
        _carBlast = new CarBlast(_ctx, _fleet, _carDamage, _seats, _riding, _glass, _impacts, _splash, _deathPush, _targets, Deal, (at, attacker) => ScudBlast(at, attacker), _smoke, Vehicles);
        _remains = new CorpseServer(_ctx, _corpses, _riding, _seats, _deathPush, _gear, _shot.Query, Water, Weapons, Items, HarvesterOnCorpse);
        InitBigfoot(); // after the corpse book, which follows the same despawns
    }

    /// <summary>The remaining features, after the kinds above: a feature that registers an entity type or a handler comes before the ones that name it.</summary>
    void BuildFeatures()
    {
        _pets = new PetServer(_ctx, PetsData, AnimalData, _animals, ArenaMode, MapDir, Map);
        _wildlife = new WildlifeServer(_ctx, WildlifeData, AnimalData, AnimalShapes, _animals, () => MapZonePoints, () => Clock.HourAt(_tick / (double)Wire.TickRate), () => Query,
            ArenaMode, MapDir, Map, (a, by) => _hunts.OnWildKilled(a, by), _minerals.OnDropped);
        _companions = new CompanionServer(_ctx, CompanionData, AnimalData, _animals.Companions, Progression, UnlockAll);
        _runOver = new RunOverServer(_ctx, _fleet, _targets, Deal, _deathPush, _wildlife.Keeper, _pets.Keeper, AnimalData, Vehicles, _smoke, id => _thieves.RoadkillCrew(id));
        _mass = new MassServer(_ctx, HolyMass, _citizens, _church, _demon, _sitters, _status, _events, _navMesh.Ensure);
        _thieves = BuildThieves();
        InitHarvester();
        InitScud();
        InitEvents(); // after the kinds above: the handler table names their start, end and cancel
        _gunships = new GunshipServer(_ctx, Killstreaks, Weapons, _targets, _fleet, () => Query, () => Vehicles, Deal, _carDamage.Damage, LaunchGunshipRocket, _impacts, _splash, _deathPush, () => _streaks.Rules);
        _flak = new FlakServer(_ctx, Weapons, _gunships, _targets, _fleet, () => Vehicles, _rng);
        _crates = new CrateServer(_ctx, Killstreaks.Rewards, () => Query, () => _streaks.Rules);
        _streaks = BuildStreaks(); // after the match and the crate book: the care package streak needs both; the turret streak spawns into the match's entities
        _lockOn = new LockOnServer(_ctx, _fleet, _gunships, () => _streaks.Rules, _sitters, id => _pounce.IsPinned(id), CarPose.SeatPos, _shot.WorldHit, _rocketIds, _smoke, _carDamage.Damage, () => Vehicles, _simClients);
        BuildVehicles();
        RegisterChatCommands(); // after the features it names exist
    }

    /// <summary>The vehicle step and what hangs on it, after the features it asks (the Thieves' vans, the harvester's driver, the Bigfoot's throw).</summary>
    void BuildVehicles()
    {
        _rocketCarts = new RocketCarts(_fleet, () => Query, Vehicles);
        _tow = new TowServer(_ctx, _fleet, _carBlast, _runOver, StepThrownVehicle, KillY);
        _loop = new VehicleLoop(_ctx, _fleet, _seats, _carDamage, _carBlast, _tow, _runOver, _glass, _smoke, Vehicles, Water, KillY, BlastSmoke, () => _thieves.ShoveCarsByVans(), StepThrownVehicle, driver => driver == _harvDriver);
        _guns = new VehicleGuns(_ctx, _rewind, _shot, _glass, _scenery, _flak, _targets, _impacts, Deal, _carDamage, Vehicles, _rng);
        _dealer = new CarDealer(_fleet, Vehicles, World, Map, Water, Spawns, _rng, _garage, BlastSmoke, CarSmoke, SmokeKind);
    }

    /// <summary>What the server needs once everything exists: the warmed indexes, the default vehicle table, the checks that fail at start and the two lines the smoke scripts wait for.</summary>
    void FinishStart()
    {
        _impacts.Warm(); // roads.json and the tree grid, so no impact pays for them on a tick (T3-003, T3-004)
        _flak.CheckWeapons(Vehicles);
        GD.Print($"server: {Spawns.Count} spawns, y {(Spawns.Count > 0 ? Spawns.Min(v => v.Y) : 0)}..{(Spawns.Count > 0 ? Spawns.Max(v => v.Y) : 0)}, killY {KillY}");
        GD.Print($"SHOOTER_NET_LISTENING port={Port} tick={Wire.TickRate}Hz");
    }

    /// <summary>
    /// The killstreaks: the table says which streak each entry is and the adapter of each is made here, in the table's order (the missile registers its entity type, the others only look).
    /// Streaks without an adapter keep their logging stub.
    /// </summary>
    StreakServer BuildStreaks() => new(_ctx, Killstreaks, _gunships, () => Spawns, _crates.GroundBelow, (streaks, id) =>
        id == MoabStreak.StreakId ? new MoabServer(_ctx, _targets, _hunts, _fleet, _carDamage.Damage, (owner, weapon) => DealBigfoot(owner, weapon), _impacts, _rng).Streak() :
        id == CarePackageRules.StreakId ? _crates.Streak() :
        id == AttackMissileStreak.StreakId ? new MissileServer(_ctx, Killstreaks, _targets, _shot.WorldHit, () => Vehicles != null, _carDamage.Rockets, (at, radius) => _glass.Blast(at, radius), Deal, _impacts, _splash, _deathPush).Streak() :
        id == TurretStreak.StreakId ? new TurretStreak(Turret.Def, _match.Entities, _sentries) :
        id == AttackHelicopterStreak.StreakId ? new AttackHelicopterServer(_ctx, Killstreaks, _gunships).Streak() :
        id == Ac130Streak.StreakId ? new Ac130Server(_ctx, Killstreaks, _gunships).Streak() :
        id == HelicopterHelperStreak.StreakId ? new HelperServer(_ctx, Killstreaks, Weapons, streaks.SpawnOverMap, _targets, _sentries, Deal, _impacts, streaks.Rng).Streak() : null);

    /// <summary>
    /// The Thieves event's parts, made in the order they register their world entity types: the Gang members, the Raid vans, the Loot, then the reward, which wraps the hit routing of the first two.
    /// </summary>
    ThievesServer BuildThieves()
    {
        var phase = new ThievesEvent();
        var gangs = new GangServer(_ctx, ThievesData, Weapons, Items, _corpses, Deal, () => _navMesh.Mesh, _navMesh.NextTicket, () => WalkerWorld, _progress.Scores, _progress.Award);
        var raids = new RaidServer(_ctx, ThievesData, phase, gangs, Map, World, _fleet, () => Vehicles, () => _navMesh.Mesh, () => MapFlightBounds,
            () => _church.Shell is { } church ? new System.Numerics.Vector2(church.DoorwayPos.X, church.DoorwayPos.Z) : null, _rng, (sweep, spare) => _runOver.Sweep(sweep, spare), _carDamage.Crash);
        var loot = new LootServer(_ctx, ThievesData, raids.Roster, gangs.Roster, _fleet, _pets.Keeper, Map, () => _navMesh.Mesh, ArenaMode, MapDir);
        var payout = new ThievesPayout(_ctx, ThievesData, Levels.Awards.EventWin, Items, Weapons, _corpses, _gear.BroadcastCorpseItems, _rng, _progress.Scores, _progress.Award, gangs.Roster, raids.Roster);
        return new ThievesServer(_ctx, ThievesData, _events, _intel, _rng, () => _navMesh.Mesh, gangs, raids, loot, payout, phase);
    }

    /// <summary>A sitter stood up: the dictation table lets go of the set, the Mass forgets the seat.</summary>
    void OnStood(ushort id, bool atTable)
    {
        if (atTable) _rozhlas.TableStood(id); // standing up from the dictation table lets go of the set
        _mass?.Stood(id);
    }

    public override void _ExitTree() { _rozhlas?.Dispose(); _perf?.Flush(); _host.Destroy(); _navMesh?.Free(); }

    public override void _Process(double delta)
    {
        if (Sim != null && Sim.Pump()) _host.Flush(); // delayed packets go out at frame rate, not only on physics ticks
        _rozhlas?.Pump(); // the live rozhlas feed's due frames (a thread feeds ffmpeg; the sending is here, on the game thread)
    }

    /// <summary>
    /// One 30 Hz tick, in this order (it decides the simulation, so it does not change lightly): receive and housekeeping, the world (match, animals, events), the killstreaks,
    /// the bots, the players' inputs, the rockets and corpses, the vehicles, the blasts and the tick's flushes, the lag-compensation poses, then the snapshots and the report.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        var tickStartUs = Time.GetTicksUsec();
        var catchUp = CatchUpTick(tickStartUs);
        if (Sim != null && Sim.Pump()) _host.Flush();
        _tick++;
        if (!_spawnsChecked)
        {
            if (_tick >= 3) { CheckSpawnGround(); _dealer.Deal(); PlanTargets(); _navMesh.Build(); } // always: citizens and events need it too, and a bake during play times the clients out
            return; // not accepting connections until the spawn list is validated
        }
        var dt = (float)delta;
        _elapsed += delta;
        _perf?.Begin();
        PhasesBegin();
        Prof.TickBegin();
        ReceiveAndHousekeep();
        TickWorld(dt);
        _streaks.Tick(dt);
        PerfLap(ServerPerf.Ph.Streaks);
        _bots.Tick(dt); // fills the roster and queues each bot's input for this tick (#214)
        HarvesterTick(dt); // the harvester event: its spawn, its driver's pedals
        ScudEventTick(dt); // the Scud event: who boarded the truck, the search time, the bunkers' carts
        PerfLap(ServerPerf.Ph.Bots);
        SimulatePlayers(dt);
        StepRockets(dt);
        PerfLap(ServerPerf.Ph.Rockets);
        _remains.Step(dt);
        PerfLap(ServerPerf.Ph.Corpses);
        StepVehiclesAndRiders(dt);
        ResolveBlastsAndFlush(dt);
        if (CarSmoke && _smoke.CarExits >= 1 && _smoke.FinishAt == 0) _smoke.FinishAt = _elapsed + 5.0; // after the bot's check that the empty car stays put
        _rewind.CapturePlayers(); // the poses this tick ends with, the state the snapshots carry (ADR 0013)
        CaptureBigfootPose();
        _thieves.RecordPoses(_tick); // Gang members and Raid vans
        _rewind.CaptureVehicles(); // and the vehicles' (B-02)
        PerfLap(ServerPerf.Ph.LagCapture);
        if (catchUp && LossTest == 0) _skippedSnapshots++; // the next regular tick sends the current state
        else SendSnapshots();
        PerfLap(ServerPerf.Ph.Snapshots);
        TickTelemetry(tickStartUs, delta);
        LossTickEnd(tickStartUs);
        PerfLap(ServerPerf.Ph.Telemetry);
        PerfEnd();
        ReportTick();
        FinishSmokeIfDue();
    }

    /// <summary>Network in: this tick's packets, the profile loads and saves that finished, and the connections and requests that wait on a clock.</summary>
    void ReceiveAndHousekeep()
    {
        foreach (var cl in _clients.Values) cl.PacketsThisTick = 0;
        Poll();
        PerfLap(ServerPerf.Ph.Poll);
        _profiles.Tick();
        _hunts.Tick();
        _minerals.Tick(Spawns.Count > 0);
        DropSilentPeers();
        _intel.Tick(); // a made-up event whose time is up leaves the intel screen
        PerfLap(ServerPerf.Ph.Housekeeping);
    }

    /// <summary>The match and everything that lives in the world besides players: the entities, the citizens, the animals, the Mass, the Thieves, the rozhlas.</summary>
    void TickWorld(float dt)
    {
        _audit?.Begin();
        var pt = Prof.Begin();
        _match.Update();
        _navMesh.Drain(); // the routes the worker finished since the last tick, applied here and nowhere else
        _audit?.Lap(0);
        pt = Prof.Lap(Prof.S.MatchUpdate, pt);
        BigfootPrepare(); // the players and vehicles the Bigfoot's brain looks at this tick
        _thieves.Prepare(); // the players the Gang members fight, and who each fights
        _animals.Prepare(); // the players and moving vehicles the animals look at, read once for all of them
        _audit?.Lap(1);
        pt = Prof.Lap(Prof.S.Prepare, pt);
        _match.Entities.Tick(dt);
        _audit?.Lap(2);
        pt = Prof.Lap(Prof.S.Entities, pt);
        _citizens.Tick(dt);
        _audit?.Lap(3);
        pt = Prof.Lap(Prof.S.Citizens, pt);
        _pets.Tick(dt);
        _audit?.Lap(4);
        pt = Prof.Lap(Prof.S.Pets, pt);
        _wildlife.Tick(dt);
        _companions.Tick();
        _animals.Done(); // the animals have ticked: the players move from here on
        _audit?.Lap(5);
        pt = Prof.Lap(Prof.S.Wildlife, pt);
        _mass.Tick(dt); // the Holy Mass: its clock, the pastor, the seats
        _audit?.Lap(6);
        _thieves.Tick(dt); // the Thieves event: its phase clock
        _audit?.Lap(7);
        _rozhlas.Tick(dt); // the village loudspeaker: the schedule and the hijack clock
        _audit?.Lap(8);
        Prof.End(Prof.S.Events, pt);
        PerfLap(ServerPerf.Ph.Match);
    }

    /// <summary>Every joined player takes the inputs queued for this tick (bots included), then the Lock-on launchers step.</summary>
    void SimulatePlayers(float dt)
    {
        foreach (var car in _fleet.Cars) car.PlaceSolid(); // the boxes on-foot players are stopped by, where the vehicles stand after the last tick (#217)
        _thieves.PlaceVanSolids(); // and the Raid vans' (a van that stands is solid)
        PerfLap(ServerPerf.Ph.PlaceSolids);
        _simClients.Clear(); _simClients.AddRange(_byId.Values); // a copy: a client may leave while the others are simulated
        foreach (var c in _simClients) SimulateClient(c, dt); // every joined player, bots included
        _lockOn.Step(dt); // the Lock-on launchers: each Lock steps where its player now looks (ADR 0031)
        PerfLap(ServerPerf.Ph.SimClients);
    }

    void StepVehiclesAndRiders(float dt)
    {
        StepScud(dt); // the Scud trucks' launchers: the crew's lock, the rack, the brakes (before the vehicles step)
        _loop.Step(dt);
        _riding.Step(dt); // the dead pilots and drivers move with the vehicles that just moved
        HarvesterAfterVehicles(dt); // what the harvester ran over, the corpses it draws in, the bales' fire
        PerfLap(ServerPerf.Ph.Vehicles);
    }

    /// <summary>What the tick's shots set off, and the messages that are sent once per tick however many things changed.</summary>
    void ResolveBlastsAndFlush(float dt)
    {
        _status.Tick(dt); // burns tick
        PlantationTick(dt); // highs run out
        _gunships.TickShells();  // shells whose tracer has arrived burst now (#162)
        _flak.Tick();    // flak shells that have arrived burst now
        _lockOn.StepVehicleHoming(dt); // missiles homing on flown helicopters fly and hit now
        _lockOn.DetonateHoming();  // homing missiles that have arrived hit their Aircraft now
        _gunships.FlushFx(); // the gunship rounds of this tick, coalesced (#159)
        FlushGlass();     // the glass panes that changed this tick (ADR 0007)
        _scenery.Tick(dt); // the pole lamps and road sign plates: clocks of the broken ones and the changes of this tick (ADR 0037, 0039)
        _status.Flush(); // statuses and burning that changed this tick
        FlushScoreboard(); // once a tick at most, and about once a second (the ping), however many kills the tick had (A-04)
        PerfLap(ServerPerf.Ph.Detonate);
    }

    /// <summary>The peak player count, the smoke test's moved distances and the 2 s player log.</summary>
    void ReportTick()
    {
        if (_clients.Count > _smoke.PeakPlayers) _smoke.PeakPlayers = _clients.Count;
        if (QuitAfter > 0) foreach (var c in _clients.Values) if (c.Joined) _smoke.MovedByName[c.Name] = c.MaxMoved; // only the smoke verdict reads it; a production server keeps no per-name history
        if (_tick % (Wire.TickRate * 2) != 0 || _clients.Count == 0) return;
        var logT = Prof.Begin(Prof.S.LogPlayers); // after the tick's clock stopped: this line is in no phase
        GD.Print($"server: tick {_tick}, players {string.Join(", ", _byId.Values.Select(c => $"{c.Name}#{c.Id}@({c.Body.GlobalPosition.X:F1},{c.Body.GlobalPosition.Y:F1},{c.Body.GlobalPosition.Z:F1}) ack={c.Ack}"))}");
        Prof.End(Prof.S.LogPlayers, logT);
    }

    bool _spawnsChecked;

    /// <summary>Keeps only spawn candidates with collision under them (some pipeline spawns lie outside the built colliders and would drop players into the void).</summary>
    void CheckSpawnGround()
    {
        _spawnsChecked = true;
        var before = Spawns.Count;
        Spawns.RemoveAll(sp => Query.Ground(MoverKind.Player, new Vector3(sp.X, sp.Y, sp.Z), 2f, 5f) == null);
        GD.Print($"server: {Spawns.Count}/{before} spawn candidates have ground");
        LossClusterSpawns();
    }

    void Poll()
    {
        for (var guard = 0; guard < 10000; guard++)
        {
            var r = _host.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev is ENetConnection.EventType.None or ENetConnection.EventType.Error) break;
            var peer = (ENetPacketPeer)r[1].AsGodotObject();
            switch (ev)
            {
                case ENetConnection.EventType.Connect:
                    if (_clients.Count >= MaxPeers) Reject(new ServerClient { Peer = peer }, $"The server is full ({MaxPeers} players). Try again later.");
                    else _clients[peer] = new ServerClient { Peer = peer, ConnectedAt = _elapsed };
                    break;
                case ENetConnection.EventType.Disconnect: Drop(peer); break;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0)
                    {
                        var pkt = peer.GetPacket();
                        if (_clients.TryGetValue(peer, out var rc) && ++rc.PacketsThisTick > PacketsPerTick) { _droppedPackets++; continue; }
                        OnPacket(peer, pkt);
                    }
                    break;
            }
        }
    }

    void OnPacket(ENetPacketPeer peer, byte[] d)
    {
        if (!_clients.TryGetValue(peer, out var c) || d.Length == 0) return;
        if (d[0] < Wire.TInput) OnReliable(c, Wire.DecodeReliable(d)); // message type tells the channel apart (Godot does not expose it per packet)
        else if (c.Joined && d[0] == Wire.TRozhlasMic) _rozhlas.OnMic(c, d); // the set holder's voice
        else if (c.Joined && d[0] == Wire.TInput) OnInputs(c, d);
    }

    /// <summary>A reliable message: the hello first, then the events by kind (the first that matches takes it), anything else is the profile's (loadouts, slots, camo).</summary>
    void OnReliable(ServerClient c, ReliableMsg? m)
    {
        if (m is HelloMsg h && c.Phase == SessionPhase.Connected) OnHello(c, h);
        else if (m is EventMsg { Kind: (byte)EventKind.Debug } e && c.Joined && (DebugCommands || ArenaMode)) OnDebug(c, e.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpse } pu) _remains.OnPickUp(c, pu.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpseItem } pi) _gear.OnPickUpCorpseItem(c, pi.Payload);
        else if (m is EventMsg { Kind: >= (byte)EventKind.EquipItem and <= (byte)EventKind.TakeGroundItem } ir) _gear.OnItemRequest(c, (EventKind)ir.Kind, ir.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SetZero } sz) { if (c.Joined && BulletDrop.TryDecodeZero(sz.Payload, out var zi, out var zm) && zi < Weapons.Weapons.Count && BulletDrop.HasDrop(Weapons.Weapons[zi])) c.Zero.Set(zi, Weapons.Weapons[zi], zm); }
        else if (m is EventMsg { Kind: (byte)EventKind.RaiseBinoculars } rb) { if (c.Joined && Binoculars.TryDecodeRaise(rb.Payload, out var up)) _gear.SetBinoculars(c, up && _gear.CarriesBinoculars(c)); }
        else if (m is EventMsg { Kind: (byte)EventKind.Redeploy } rd && c.Joined) _profiles.OnRedeploy(c, rd.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.OpenCarePackage } oc) _crates.OnOpen(c, oc.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.MineralPick } mp && c.Joined) _minerals.OnPick(c, mp.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.CompanionPick } cp && c.Joined) _companions.OnPick(c, cp.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SandboxGrant } sg && c.Joined) _sandbox.OnGrant(c, sg.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SandboxAction } sa && c.Joined) _sandbox.OnAction(c, sa.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SandboxTeleportTo } st && c.Joined) _sandbox.OnTeleportTo(c, st.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SeatSwitch } sw && c.Joined) _seats.OnSwitch(c, sw.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.ScudCommand } sc && c.Joined) OnScudCommand(c, sc.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.TeamAssign } ta && c.Joined) _teams.OnTeamAssign(c, ta.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SquadAssign } sq && c.Joined) _teams.OnSquadAssign(c, sq.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.HuntsRequest or (byte)EventKind.RecordsRequest or (byte)EventKind.PlayerLogRequest } hq && c.Joined) _hunts.OnRequest(c, hq);
        else if (m is EventMsg { Kind: (byte)EventKind.ChatSend } cs) _chat.OnSend(c, cs.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.ClientReport } cr) { if (c.Joined) OnClientReport(c, cr.Payload); }
        else if (c.Joined) _profiles.OnMessage(c, m);
    }

    /// <summary>A packet of inputs: the snapshot and world acks it carries, and the inputs the player has not sent before, queued for the ticks (at most 8).</summary>
    void OnInputs(ServerClient c, byte[] d)
    {
        _scratch.Clear();
        if (!Wire.TryDecodeInputs(d, _scratch, out var snapTick, out var worldTick)) return;
        if (snapTick != Wire.NoTick && snapTick <= _tick && _sentStates.Has(snapTick) && (c.SnapAck == Wire.NoTick || snapTick > c.SnapAck)) c.SnapAck = snapTick; // an older input's ack arriving late changes nothing
        if (worldTick != Wire.NoTick && worldTick <= _tick && _sentWorld.Has(worldTick) && (c.WAck == Wire.NoTick || worldTick > c.WAck)) c.WAck = worldTick; // the world channel's ack, the same rule
        foreach (var i in _scratch)
        {
            // redundant copies of already-seen inputs are skipped
            if (c.HasSeq && !NetInput.Newer(i.Seq, c.LastSeq)) continue;
            c.LastSeq = i.Seq; c.HasSeq = true;
            c.Inputs.Enqueue(i);
        }
        while (c.Inputs.Count > 8) c.Inputs.Dequeue(); // a stalled sim never builds unbounded lag
    }

    void Reject(ServerClient c, string reason)
    {
        c.Session.Enter(SessionPhase.Rejected);
        GD.Print($"server: rejecting {(c.Name == "" ? "a client" : c.Name)}: {reason}");
        Send(c, new ErrorMsg(reason));
        _host.Flush();
        c.Peer!.PeerDisconnectLater(0); // after the error has gone out
    }

    /// <summary>The client has its profile: the hunter's log learns its name, a collection tier reached while the rule or the weapon was not there yet is rewarded, the companions follow.</summary>
    void OnProfileAttached(ServerClient c)
    {
        _hunts.OnProfile(c);
        _companions.FromProfile(c);
    }

    void OnHello(ServerClient c, HelloMsg h)
    {
        if (h.Version != Wire.ProtocolVersion)
        {
            Reject(c, $"Protocol mismatch: this server speaks protocol {Wire.ProtocolVersion}, your game speaks {h.Version}. Update the game to the server's build.");
            return;
        }
        // the map build decides spawns, tree and parcel solids, glass indices and water: a different build would desync silently (E-06)
        if (Map != null && Map.BuildHash != 0 && h.MapHash != Map.BuildHash)
        {
            Reject(c, $"Map mismatch: this server runs map {Map.MapVersion} (build {Map.BuildHash:x16}), and your game has a different map build. Install the server's map folder, then start again.");
            return;
        }
        c.Id = NewPlayerId();
        if (c.Id == 0) { Reject(c, "The server is full."); return; }
        var name = LoadoutRules.SanitizePlayerName(h.Name);
        c.Name = name.Length == 0 ? $"player{c.Id}" : name;
        _sandbox.OnHello(c, h.Flags);
        c.Token = h.Token;
        DropSessionsWithToken(c); // a quick reconnect wins over its ghost, which would otherwise run on the same profile until its peer times out
        _profiles.StartLoad(c, h.Token); // joins once the profile is in (or the wait times out)
        GD.Print($"server: {c.Name} #{c.Id} hello, loading profile");
    }

    /// <summary>A free player id for a connection or a bot (0 when there is none).</summary>
    ushort NewPlayerId() => _ids.Allocate(id => _clients.Values.Any(o => o.Id == id) || _byId.ContainsKey(id));

    /// <summary>Joins a client: body, welcome, profile, the join broadcast and the spawn. Called once the client has a profile.</summary>
    void Join(ServerClient c)
    {
        // health, death and respawn belong to the match state machine, not the body
        _progress.ApplyLoadout(c);
        c.Recoil = new RecoilState(c.Id);
        c.Breath = new BreathState(c.Id);
        c.Worn = new Equipment(Items);
        c.Bag = new Bag();
        ItemRules.IssueStandard(Items, c.Bag, ProfileServer.OwnedItems(c)); // every player has the standard items (the binoculars) from the start
        c.Body = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, Authoritative = false, Name = $"P{c.Id}", Water = Water, Ladders = MapLadders, Bodies = _animals.Crowd };
        World.AddChild(c.Body);
        c.Body.Teleport(Vector3.Zero);
        c.Session.Enter(SessionPhase.Joined);
        _byId[c.Id] = c;
        c.Enc = new SnapshotEncoder(_sentStates);
        c.WEnc = new WorldSnapshotEncoder(history: _sentWorld);
        _profiles.Send(c);
        _companions.SendRoster(c); // the Companions they can pick and the one that is out (the Equipment screen's picker)
        _sandbox.SendStatus(c); // the answer to a sandbox request (#156), before the spawn
        _chat.SendStatus(c); // admin or not, and the vehicles /spawn takes
        Send(c, new WelcomeMsg(Wire.ProtocolVersion, c.Id, Wire.TickRate, _tick,
            (ushort)(Clock.StartMinute), (uint)Math.Round(Clock.DayLengthSeconds * 1000), Clock.Frozen));
        foreach (var o in _byId.Values)
        {
            Send(c, new JoinMsg(o.Id, o.Name));
            if (o != c) Send(o, new JoinMsg(c.Id, c.Name));
        }
        foreach (var w in _match.Entities.Snapshot()) Send(c, new EventMsg((byte)EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State)));
        _gear.SendCorpseItemsOnJoin(c); // the items the corpses carry, after the corpses themselves
        SendGlassOnJoin(c); // the glass panes that are not whole (ADR 0007)
        _scenery.SendOnJoin(c); // the pole lamps and sign plates that are not whole (ADR 0037, 0039)
        _church.SendOnJoin(c); // the church doors, when open (ADR 0019)
        _intel.SendOnJoin(c); // the events on the intel screen
        _mass.SendOnJoin(c); // the Holy Mass, when one runs
        _thieves.SendOnJoin(c); // the Thieves event, when one runs
        _minerals.SendOnJoin(c); // the mineral nodes
        _rozhlas.SendOnJoin(c); // the village loudspeaker, when a detour broadcast is on, who is on the mic, and the Urad door when it is open
        _garage.SendOnJoin(c); // and the garage doors, when they are open
        SendHarvesterOnJoin(c); // the harvester's job and the bales that burn
        SendScudOnJoin(c); // the Scud trucks' racks and proposals
        _demon.SendOnJoin(c); // and the demon's pounce, when one is on
        SendTargetRoster(c); // target dummies' names (#148)
        _profiles.SendAppearancesOnJoin(c); // camouflage (#140): every player's look reaches the joiner, the joiner's reaches everyone
        _gear.SendWornOnJoin(c); // what the others wear (#266), to draw on their characters
        _gear.SendBinocularsOnJoin(c); // and who holds the binoculars up
        _gear.SendHeadLampsOnJoin(c); // and whose head lamp is on
        _status.SendOnJoin(c); // who is blessed, cursed or burning
        _match.AddPlayer(c.Id, c.Name, c.Sandbox, c.IsBot); // spawns the player (event) and broadcasts the scoreboard
        _sandbox.Announce(c); // sandbox name tags both ways, and the held streak (#156)
        _host.Flush();
        GD.Print($"server: {c.Name} joined as #{c.Id} ({_byId.Count} players)");
    }

    /// <summary>Players the server takes; the ENet host has one more slot, so a connection past this is told the server is full.</summary>
    public const int MaxPeers = 32;

    void Drop(ENetPacketPeer peer)
    {
        if (!_clients.Remove(peer, out var c)) return;
        if (c.Joined) Leave(c);
    }

    /// <summary>Takes a joined player out of the match: a dropped connection, or a bot that is no longer wanted.</summary>
    void Leave(ServerClient c)
    {
        _streaks.Leave(c.Id);
        _companions.Leave(c.Id);
        _status.Leave(c.Id);
        _rozhlas.OnPlayerLeft(c.Id);
        _sitters.StandUp(c.Id); _sitters.Forget(c.Id);
        _seats.Detach(c);
        c.Body.QueueFree();
        _byId.Remove(c.Id);
        foreach (var o in _byId.Values) Send(o, new LeaveMsg(c.Id));
        _match.RemovePlayer(c.Id);
        GD.Print($"server: {c.Name} #{c.Id} left");
    }

    /// <summary>Drops every other client that uses <paramref name="keep"/>'s token, so a profile never has two live sessions.</summary>
    void DropSessionsWithToken(ServerClient keep)
    {
        if (keep.Token.Length == 0) return;
        foreach (var o in _clients.Values.Where(o => o != keep && o.Token == keep.Token).ToList())
        {
            GD.Print($"server: {o.Name} #{o.Id} replaced by a new session with the same token");
            Drop(o.Peer!);
            o.Peer!.PeerDisconnectLater(0);
        }
    }

    /// <summary>How long a connection may stay without a hello before it is dropped (#197); it holds one of <see cref="MaxPeers"/> slots.</summary>
    public const double HelloDeadlineSeconds = 5;

    void DropSilentPeers()
    {
        foreach (var c in _clients.Values.Where(c => c.Phase is SessionPhase.Connected or SessionPhase.Rejected && _elapsed - c.ConnectedAt > HelloDeadlineSeconds).ToList())
        {
            GD.Print("server: dropping a connection that sent no hello");
            Drop(c.Peer!);
            c.Peer!.PeerDisconnectLater(0);
        }
    }

    void Tx(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (_clients.TryGetValue(peer, out var c)) c.BytesOut += data.Length;
        _audit?.Sent(chan, data);
        if (Sim != null) Sim.Send(peer, chan, data, flags); else peer.Send(chan, data, flags);
    }

    /// <summary>A reliable message to one player. A bot has no connection: nothing is encoded or sent.</summary>
    void Send(ServerClient c, ReliableMsg m)
    {
        if (c.Peer is { } peer) Tx(peer, Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>Broadcast an event envelope to every joined client (shot, hit, kill, ... from later issues).</summary>
    public void BroadcastEvent(EventKind kind, byte[] payload)
    {
        var profT = Prof.Begin(Prof.S.BroadcastEvent);
        ReliableMsg m = new EventMsg((byte)kind, payload);
        PerfEventSent(kind, payload);
        _rozhlas.OnEvent(kind, payload); // shots, launches and blasts inside the Urad go to the rozhlas live feed while the mic is live
        var bytes = Wire.Encode(m); // encoded once: every recipient gets the same bytes (Tx copies them into the packet)
        // effects that may be lost go unreliable on their own channel, so they never hold back the reliable events (A-03)
        var effect = Wire.IsEffect(kind);
        foreach (var c in _clients.Values)
            if (c.Joined) Tx(c.Peer!, effect ? Wire.ChanEffects : Wire.ChanReliable, bytes, effect ? 0 : (int)ENetPacketPeer.FlagReliable);
        Prof.End(Prof.S.BroadcastEvent, profT);
    }

    void OnDebug(ServerClient c, byte[] payload)
    {
        if (WeaponCodec.DecodeDebugTeleport(payload) is { } tp)
        {
            if (!_byId.TryGetValue(tp.Target, out var t)) return;
            var p = new Vector3(tp.Pos.X, tp.Pos.Y + 0.1f, tp.Pos.Z);
            t.Body.Teleport(p);
            t.Inputs.Clear();
            _match.SetPosition(t.Id, tp.Pos);
            // the owning client resynchronises on its own spawn event (no protection)
            BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(t.Id, tp.Pos, 0)));
            return;
        }
        if (WeaponCodec.DecodeDebugVehicleDamage(payload) is { } vd)
        {
            var car = _fleet.Find(vd.Vehicle);
            if (car != null) _carDamage.Damage(car, vd.Amount, c.Id, SpecialWeapons.Debug, 0);
            return;
        }
        if (MatchCodec.DecodeDebugKill(payload) is not { } target) return;
        var r = _match.ApplyDamage(target, c.Id, SpecialWeapons.Debug, 9999);
        if (r == DamageResult.Ignored && _match.IsProtected(target)) _smoke.IgnoredHits++;
        GD.Print($"server: debug kill {c.Name} -> #{target}: {r}");
    }

    /// <summary>A new round: what the round's end left in the killstreaks, the shells, the XP book and the events starts over.</summary>
    void RoundStarted()
    {
        _streaks.RoundReset();
        _gunships.RoundReset(); // a round reset takes the shells in the air with it
        _flak.RoundReset();
        _progress.ResetXp();
        BigfootRoundReset();
        _thieves.RoundReset(); // the Thieves event does not outlive the round either, and what it stole is back
        _progress.ClearSummaries();
    }

    /// <summary>End of round: the killstreaks and shells go, then the awards and each player's XP summary.</summary>
    void RoundEnded()
    {
        _streaks.RoundReset();
        _gunships.RoundReset();
        _flak.RoundReset();
        _progress.RoundEnded();
    }

    void ChatTo(ServerClient c, ChatLineKind kind, string text) => _ctx.ChatTo(c, kind, text);

    void ChatToAll(ChatLineKind kind, string sender, string text) => _ctx.ChatToAll(kind, sender, text);

    /// <summary>Hands each admin command to the chat; the commands of a feature with a class of its own are that class's methods.</summary>
    void RegisterChatCommands()
    {
        var admin = new AdminCommands(_ctx, () => Clock, clock => Clock = clock, () => Query, Weapons, _rocketIds, _rozhlas, _intel, _demon, (kind, c) => _eventHandlers.Start(kind, c), ChatCurrentEvent);
        _chat.Register("bots", _bots.ChatCommand);
        _chat.Register("spawn", _spawner.ChatCommand);
        _chat.Register("unicorn", _wildlife.ChatUnicorn);
        _chat.Register("phoenix", _wildlife.ChatPhoenix);
        _chat.Register("sandbox", _sandbox.ChatCommand);
        _chat.Register("give", _gear.ChatGive);
        _chat.Register("companion", _companions.ChatCommand);
        _chat.Register("time", admin.Time);
        _chat.Register("explode", admin.Explode);
        _chat.Register("restart", admin.Restart);
        _chat.Register("event", admin.Event);
        _chat.Register("citizens", _citizens.Chat);
        _chat.Register("church", _church.Chat);
        _chat.Register("rozhlas", _rozhlas.ChatCommand);
        _chat.Register("status", _status.ChatCommand);
        _chat.Register("scud", ChatScud);
        _chat.Register("squad", _teams.ChatSquad);
    }
}
