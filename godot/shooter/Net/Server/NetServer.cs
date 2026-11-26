using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Net.Server.BotRoster;
using Drycastle.Shooter.Net.Server.BigfootEvent;
using Drycastle.Shooter.Net.Server.BusGarage;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.ChatRoom;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Crates;
using Drycastle.Shooter.Net.Server.Diagnostics;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Net.Server.Highs;
using Drycastle.Shooter.Net.Server.Harvest;
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
using Drycastle.Shooter.Net.Server.RoundFlow;
using Drycastle.Shooter.Net.Server.Scuds;
using Drycastle.Shooter.Net.Server.Scenery;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Sessions;
using Drycastle.Shooter.Net.Server.Sentries;
using Drycastle.Shooter.Net.Server.Shooting;
using Drycastle.Shooter.Net.Server.Squads;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Terrain;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.CarePackages;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterServer.Persistence;
using Ph = Drycastle.Shooter.Net.Server.Diagnostics.ServerPerf.Ph;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Authoritative 30 Hz server over raw ENet (see ADR 0002): ch0 reliable messages, ch1 unreliable-sequenced input and snapshots.
/// Each tick it polls the network, runs every player's next queued input through the shared movement step and
/// collision (<see cref="Player.Simulate"/>), then sends each client a delta snapshot with the last processed input seq.
/// This class is the composition root: it makes the features in <see cref="_Ready"/> (the order matters), runs the tick as a list of named steps and routes the packets.
/// </summary>
public partial class NetServer : Node
{
    /// <summary>What the host sets before the server starts.</summary>
    public ServerConfig Config = new();

    /// <summary>A grenade's speed in the health byte of its snapshot record, per m/s (the client divides it back out).</summary>
    public const float GrenadeSpeedScale = 8f;

    // The features: classes of their own in Net/Server/<Feature>/, made in _Ready once the match exists; each takes the context and the few services it needs.
    ServerContext _ctx = null!;
    ServerDiagnostics _diag = null!;
    Outbox _outbox = null!;
    ConnectionServer _connections = null!;
    PlayerRoster _roster = null!;
    InputIntake _inputs = null!;
    DebugServer _debug = null!;
    // services several features share
    Fleet _fleet = null!; TargetBook _targets = null!; DamageServer _damage = null!; ProgressionServer _progress = null!; NavService _navMesh = null!;
    ImpactFx _impacts = null!; Splash _splash = null!; MapAreas _areas = null!; WalkerGround _walkers = null!; Sitters _sitters = null!; AnimalWorld _animals = null!;
    readonly DeathPush _deathPush = new();
    readonly RocketIds _rocketIds = new();
    // the shot path
    PoseRewind _rewind = null!; GlassServer _glass = null!; ShotWorld _shot = null!; RocketSim _rockets = null!; DummyServer _dummies = null!;
    ShotServer _shots = null!; InputServer _input = null!; SnapshotServer _snapshots = null!;
    // vehicles and corpses
    CorpseBook _corpses = null!; RidingCorpses _riding = null!; CarSeats _seats = null!; CarDamage _carDamage = null!; CarBlast _carBlast = null!; CorpseServer _remains = null!;
    RunOverServer _runOver = null!; TowServer _tow = null!; RocketCarts _rocketCarts = null!; VehicleLoop _loop = null!; VehicleGuns _guns = null!; CarDealer _dealer = null!;
    // players, chat and what they carry
    BotServer _bots = null!; AdminSpawner _spawner = null!; SandboxServer _sandbox = null!; TeamServer _teams = null!; ChatServer _chat = null!; ProfileServer _profiles = null!;
    ItemsServer _gear = null!; HuntLogServer _hunts = null!; StatusServer _status = null!; CompanionServer _companions = null!; PlantationServer _plantation = null!;
    // the village and its animals
    SceneryServer _scenery = null!; MineralsServer _minerals = null!; RozhlasServer _rozhlas = null!; GarageServer _garage = null!; HouseDoorServer _houses = null!; HouseDestructionServer _houseDestruction = null!; WorldBlasts _blasts = null!; IntelServer _intel = null!; CrateServer _crates = null!;
    SentryWorld _sentries = null!; PetServer _pets = null!; WildlifeServer _wildlife = null!; ChurchServer _church = null!; CitizenServer _citizens = null!;
    MassServer _mass = null!; PounceServer _pounce = null!; DemonServer _demon = null!;
    // events and killstreaks
    ThievesServer _thieves = null!; BigfootServer _bigfoot = null!; HarvesterServer _harvester = null!; ScudServer _scud = null!; ScudEventServer _scudEvent = null!;
    EventServer _eventServer = null!; GunshipServer _gunships = null!; FlakServer _flak = null!; StreakServer _streaks = null!; LockOnServer _lockOn = null!;
    ScoreboardServer _scoreboard = null!; MatchEventsServer _matchEvents = null!;
    /// <summary>Which event runs (shared by every <c>/event</c> kind: one at a time).</summary>
    readonly EventBook _events = new();

    readonly ENetConnection _host = new();
    readonly Dictionary<ENetPacketPeer, ServerClient> _clients = new();
    readonly Dictionary<ushort, ServerClient> _byId = new();
    readonly PlayerIds _ids = new();
    /// <summary>The clients simulated in this tick (reused every tick).</summary>
    readonly List<ServerClient> _simClients = new();
    /// <summary>The entity states of the recent ticks, shared by every client's encoder (the baselines of their deltas).</summary>
    readonly SnapshotHistory _sentStates = new();
    /// <summary>The world states of recent ticks the server sent (the world channel's baselines, shared by every client).</summary>
    readonly WorldHistory _sentWorld = new();
    MatchSim _match = null!;
    readonly Random _rng = new();
    /// <summary>What the smoke tests count (and their verdicts); nothing reads it on a production server.</summary>
    readonly SmokeStats _smoke = new();
    uint _tick;
    double _elapsed;
    bool _spawnsChecked;

    public override void _Ready()
    {
        if (!Listen()) return;
        Config.Vehicles ??= new VehiclesFile { Vehicles = { new VehicleDef { Id = "car", TopSpeed = 44.4f, Acceleration = 3.5f, Brake = 9.5f, TurnRadius = 5.5f, MaxLateralAccel = 8, Mass = 1200 } } }; // the default table when the host sets none
        if (Config.ArenaMode) SmokeArena.Build(Config);
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
        var err = _host.CreateHostBound("*", Config.Port, ConnectionServer.MaxPeers + 1, Wire.Channels, 0, 0);
        if (err != Error.Ok)
        {
            GD.PrintErr($"server: cannot listen on port {Config.Port}: {err}");
            GetTree().Quit(2);
            return false;
        }
        return true;
    }

    /// <summary>The match, the context every feature takes, the sending and the diagnostics. The first things made: the rest asks for them.</summary>
    void BuildMatch()
    {
        _match = new MatchSim(Config.MatchConfig, () => _elapsed, Config.Spawns);
        _match.Event += e => _matchEvents.OnEvent(e);
        _ctx = new ServerContext(_match, _byId, _clients.Values, () => _elapsed, () => _tick,
            (kind, payload) => _outbox.Broadcast(kind, payload), (c, m) => _outbox.Send(c, m), (c, channel, packet) => _outbox.SendUnreliable(c, channel, packet));
        _fleet = new Fleet(_byId, Config.World, Config.Water, Config.Map);
        _diag = new ServerDiagnostics(Config, _ctx, _smoke, _fleet, () => _rockets.Count, _host, Quit);
        _outbox = new Outbox(_clients, Config.Sim, _diag.Perf, (kind, payload) => _rozhlas.OnEvent(kind, payload));
        _progress = new ProgressionServer(_ctx, Config.Progression, Config.Levels, Config.Weapons, Config.Perks, Config.ArenaMode || Config.DebugCommands, (c, l) => _streaks.Arm(c, l));
        _scoreboard = new ScoreboardServer(_ctx);
        _areas = new MapAreas(Config.Map, () => _church.Shell, _rng);
        _walkers = new WalkerGround(Config.Map, () => _shot.Query);
        _impacts = new ImpactFx(_ctx, Config.Map, Config.Water);
        _splash = new Splash(_ctx, () => _areas.Shelters, () => _shot.Query, car => _rewind.PoseOf(car));
        RegisterTargetSources();
    }

    /// <summary>What the features share (animals, seats, targets) and the features of the village: the church, the radio, the chat, the profiles, the hunts, the statuses and the demon.</summary>
    void BuildServices()
    {
        _navMesh = new NavService(_ctx, Config.World, Config.MapDir, Config.NavCacheDir, Config.ArenaMode, () => _host.Flush(), (ticket, route) => _thieves.DeliverRoute(ticket, route));
        _animals = new AnimalWorld(_ctx, _fleet, Config.Map, Config.Water, () => _shot.Query, () => _fleet.FlightBounds);
        _scenery = new SceneryServer(_ctx, Config.Map, Config.LampData, Config.SignPlateData);
        _sitters = new Sitters(_ctx, OnStood);
        _church = new ChurchServer(_ctx, Config.ArenaMode, Config.MapDir);
        _citizens = new CitizenServer(_ctx, Config.HolyMass, Config.ArenaMode, Config.MapDir, Config.Map, () => _navMesh.Mesh, () => _walkers, _navMesh.Ensure);
        _rozhlas = new RozhlasServer(_ctx, Config.ArenaMode, Config.MapDir, Config.HolyMass, Config.Weapons, _citizens.Hits, new TableSeating(_sitters.IsSeated, _sitters.StandUpAtTableExcept, _sitters.SitAtTable));
        _intel = new IntelServer(_ctx);
        _chat = new ChatServer(_ctx, admin => new ChatContext(admin, Config.Vehicles?.Vehicles.Select(v => v.Id).ToList() ?? new List<string>(), Config.Items.Items.Select(i => i.Id).ToList(), Config.CompanionData.Companions.Select(i => i.Id).ToList()), Config.AdminTokens, Config.AdminAll);
        _profiles = new ProfileServer(_ctx, Config.Progression, Config.UnlockAll, Config.Weapons, Config.Perks, Config.CamoData, Config.Killstreaks, c => _roster.Join(c), OnProfileAttached, _diag.Health.AllowSave);
        _bots = new BotServer(_ctx, _navMesh, _profiles, _progress, _sitters, Config.Weapons, _rng, Config.Bots, () => _roster.NewPlayerId(), c => _roster.Join(c), c => _roster.Leave(c), (from, dir, max) => _shot.WorldDistance(from, dir, max));
        _spawner = new AdminSpawner(_ctx, () => Config.Vehicles, _fleet, Config.World, Config.Water, () => _fleet.FlightBounds, _rng);
        _sandbox = new SandboxServer(_ctx, Config.AllowSandbox, () => _streaks, _spawner, () => Config.Spawns, () => _shot.Query, _ctx.SendAmmo, (c, place) => _seats.Leave(c, place), _rng);
        _teams = new TeamServer(_ctx, _chat, _sandbox);
        _damage = new DamageServer(_ctx, _animals, _progress, _bots, _smoke, () => _streaks, () => _demon, () => _hunts, () => _status, () => _gunships, (attacker, weapon) => _bigfoot.Hit(attacker, weapon), _diag.Loss.Hit);
        _garage = new GarageServer(_ctx, Config.ArenaMode, Config.MapDir, _fleet.Cars, () => Config.Vehicles);
        _houses = new HouseDoorServer(_ctx, Config.ArenaMode, _fleet.Cars);
        _minerals = new MineralsServer(_ctx, Config.Progression, () => _wildlife.Keeper.Drops);
        _hunts = new HuntLogServer(_ctx, Config.Progression, Config.AnimalData, Config.Weapons, Config.Items, Config.CompanionData, _progress.Scores, _progress.WeaponKey,
            c => { _profiles.Send(c); _companions.FromProfile(c); if (c.Joined) _companions.SendRoster(c); });
        _sentries = new SentryWorld(_ctx, _targets, _damage.Deal, () => _shot.Query);
        _status = new StatusServer(_ctx, _damage.Deal, Config.Weapons, c => _plantation.GetHigh(c));
        _pounce = new PounceServer(_ctx, () => Config.Demon, _status);
        _demon = new DemonServer(_ctx, () => Config.Demon, () => _mass, _church, _citizens, _sitters, _status, _pounce, _targets, _damage.Deal, () => _shot.Query, (at, radius) => _blasts.Blast(at, radius, BlastSource.Demon), () => _areas.Shelters, Config.Weapons, Config.MapDir, Config.World, _events);
        _rewind = new PoseRewind(_ctx, _fleet, _targets, _sitters, _pounce, Config.NoLagComp);
        _glass = new GlassServer(_ctx, _fleet, _rewind, Config.Map, Config.GlassData);
        _houseDestruction = new HouseDestructionServer(_ctx, Config.HouseDestruction, Config.ArenaMode, Config.MapDir, Config.Map, () => _shot.Query);
        _blasts = new WorldBlasts(_glass, _houseDestruction); // every blast's panes and houses
        _houseDestruction.Changed = house => { _glass.HouseChanged(house); _houses.HouseChanged(house); }; // a pane whose frame a carve took shatters, a door is blown out
        _houseDestruction.Restored = _houses.RehangAll;
        _shot = new ShotWorld(_ctx, _fleet, _rewind, _sitters, _pounce, _targets, _glass, Config.World, _diag.Perf.Ticks != null);
        _carDamage = new CarDamage(_ctx, _fleet, _glass, _smoke, Config.Vehicles);
        _rocketCarts = new RocketCarts(_fleet, () => _shot.Query, Config.Vehicles);
        _rockets = new RocketSim(_ctx, _rewind, _shot, _blasts, _carDamage, _targets, _damage.Deal, _deathPush, _splash, _impacts, _rocketIds, Config.Weapons, _smoke);
        _dummies = new DummyServer(_ctx, _shot, _deathPush, () => _streaks, _progress, Config.Spawns, Config.MatchConfig);
    }

    /// <summary>What a shot can hit, kind by kind; the order of the sources is the order of every target list. A kind that stood elsewhere on the shooter's screen says where.</summary>
    void RegisterTargetSources()
    {
        _targets = new TargetBook(() => _byId.Count);
        _targets.AddSource(list => _shot.AddPlayers(list), (List<HitTarget> list, in ShotView view) => _shot.AddPlayersSeen(list, view));
        _targets.AddSource(list => _dummies.AddTargets(list));
        _targets.AddSource(list => { if (_bigfoot.Target() is { } bigfoot) list.Add(bigfoot); }, // the Bigfoot is a hit target like a player (issue #264), rewound like one
            (List<HitTarget> list, in ShotView view) => { if (_bigfoot.Target() is { } bigfoot) list.Add(_bigfoot.Rewound(bigfoot, view.Tick)); });
        _targets.AddSource(list => _citizens.Crowd?.AddTargets(list)); // the citizens of the Holy Mass walk slowly: not rewound
        _targets.AddSource(list => _thieves.AddTargets(list), (List<HitTarget> list, in ShotView view) => _thieves.AddTargets(list, view.Tick)); // the Gang members and their Raid vans
        _targets.AddSource(list => { var t = Prof.Begin(Prof.S.TargetsAnimals); _pets.AddTargets(list); _wildlife.AddTargets(list); Prof.End(Prof.S.TargetsAnimals, t); }, (List<HitTarget> list, in ShotView view) => { _pets.AddTargets(list); _wildlife.AddTargets(list, view.BackTicks / (float)Wire.TickRate); }); // wild animals in the pose the shooter saw
        _targets.AddSource(list => _rozhlas.AddTargets(list)); // the workers of the Urad
        _targets.AddSource(list => _demon.AddTarget(list));
    }

    /// <summary>The world entities' end of life and their types, in the order they register, and what follows the same despawns (the corpse book, the Bigfoot).</summary>
    void BuildWorldEntities()
    {
        _match.Entities.Log = s => GD.PrintErr($"server: {s}");
        _match.Entities.Spawned += w => _ctx.Broadcast(EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State));
        _match.Entities.Despawned += w => _ctx.Broadcast(EventKind.WorldDespawn, WorldCodec.EncodeDespawn(w.Id, w.Reason));
        CorpseRules.Register(WorldEntityRegistry.Shared);
        GroundItemRules.Register(WorldEntityRegistry.Shared);
        TurretRules.Register(WorldEntityRegistry.Shared);
        CitizenRules.Register(WorldEntityRegistry.Shared);
        _corpses = new CorpseBook(_match.Entities);
        _gear = new ItemsServer(_ctx, Config.Items, _corpses, () => _shot.Query, car => CarPose.Present(car).Feet);
        _riding = new RidingCorpses(_corpses);
        _seats = new CarSeats(_ctx, _fleet, _riding, _shot, _smoke, () => Config.Vehicles, () => Config.Clock.HourAt(_elapsed), id => _streaks.Rules.ControlOf(id) != null);
        _carBlast = new CarBlast(_ctx, _fleet, _carDamage, _seats, _riding, _blasts, _impacts, _splash, _deathPush, _targets, _damage.Deal, (at, attacker) => _scud.Blast(at, attacker), _smoke, Config.Vehicles);
        _remains = new CorpseServer(_ctx, _corpses, _riding, _seats, _deathPush, _gear, _shot.Query, Config.Water, Config.Weapons, Config.Items, (victim, id, corpse) => _harvester.OnCorpse(victim, id, corpse));
        _bigfoot = new BigfootServer(_ctx, Config.BigfootData, _events, new BigfootTerrain(Config.World, Config.Water), _fleet, _damage.Deal, _deathPush, _rng, Config.Weapons, new BigfootLoot(Config.BigfootData, Config.Items, Config.Weapons, _corpses, _gear), _areas,
            _hunts, () => _gunships, _smoke, CarPose.SeatPos, _progress.Scores, _progress.Award, Config.Levels.Awards.EventWin); // after the corpse book, which follows the same despawns
    }

    /// <summary>The remaining features, after the kinds above: a feature that registers an entity type or a handler comes before the ones that name it.</summary>
    void BuildFeatures()
    {
        _pets = new PetServer(_ctx, Config.PetsData, Config.AnimalData, _animals, Config.ArenaMode, Config.MapDir, Config.Map);
        _wildlife = new WildlifeServer(_ctx, Config.WildlifeData, Config.AnimalData, Config.AnimalShapes, _animals, () => _areas.ZonePoints, () => Config.Clock.HourAt(_tick / (double)Wire.TickRate), () => _shot.Query,
            Config.ArenaMode, Config.MapDir, Config.Map, (a, by) => _hunts.OnWildKilled(a, by), _minerals.OnDropped);
        _companions = new CompanionServer(_ctx, Config.CompanionData, Config.AnimalData, _animals.Companions, Config.Progression, Config.UnlockAll);
        _runOver = new RunOverServer(_ctx, _fleet, _targets, _damage.Deal, _deathPush, _wildlife.Keeper, _pets.Keeper, Config.AnimalData, Config.Vehicles, _smoke, id => _thieves.RoadkillCrew(id));
        _mass = new MassServer(_ctx, Config.HolyMass, _citizens, _church, _demon, _sitters, _status, _events, _navMesh.Ensure);
        _thieves = BuildThieves();
        _harvester = BuildHarvester();
        BuildScud();
        _eventServer = new EventServer(_ctx, _events); // after the kinds above: the handler table names their start, end and cancel
        _eventServer.Register(EventBook.Bigfoot, "Bigfoot event", _bigfoot.Start, _bigfoot.End, _bigfoot.Cancel);
        _eventServer.Register(EventBook.Mass, "Holy Mass", _mass.Chat, _mass.End); // the Holy Mass can only end (its handler answers a cancel with a refusal)
        _eventServer.Register(EventBook.Harvester, "harvester event", _harvester.Start, _harvester.End, _harvester.Cancel);
        _eventServer.Register(EventBook.Thieves, "Thieves event", _thieves.Start, _thieves.End, _thieves.Cancel);
        _eventServer.Register(EventBook.Scud, "Scud event", _scudEvent.Start, _scudEvent.End, _scudEvent.Cancel);
        _gunships = new GunshipServer(_ctx, Config.Killstreaks, Config.Weapons, _targets, _fleet, () => _shot.Query, () => Config.Vehicles, _damage.Deal, _carDamage.Damage, _rockets.LaunchFromGunship, _impacts, _splash, _deathPush, () => _streaks.Rules,
            (at, radius, source) => _blasts.Carve(new Vector3(at.X, at.Y, at.Z), radius, source));
        _flak = new FlakServer(_ctx, Config.Weapons, _gunships, _targets, _fleet, () => Config.Vehicles, _rng, (at, radius, source) => _blasts.Carve(new Vector3(at.X, at.Y, at.Z), radius, source));
        _crates = new CrateServer(_ctx, Config.Killstreaks.Rewards, () => _shot.Query, () => _streaks.Rules);
        _streaks = BuildStreaks(); // after the match and the crate book: the care package streak needs both; the turret streak spawns into the match's entities
        var keys = new KeyCompetition(_corpses, _crates, _areas, _garage, _houses, _mass, _rozhlas, _thieves, at => _seats.EnterableCar(new Vector3(at.X, at.Y, at.Z)) != null, c => _scud.HasLoadingOffer(c));
        _plantation = new PlantationServer(_ctx, Config.Plantation, _areas, keys, _sitters, _pounce, _streaks);
        _lockOn = new LockOnServer(_ctx, _fleet, _gunships, () => _streaks.Rules, _sitters, id => _pounce.IsPinned(id), CarPose.SeatPos, _shot.WorldHit, _rocketIds, _smoke, _carDamage.Damage, () => Config.Vehicles, _simClients);
        BuildVehicles();
        BuildShooting();
        var rounds = new RoundHooks(_streaks, _gunships, _flak, _progress, _bigfoot, _thieves);
        var roundReset = new RoundReset(
            _mass.RoundEnd, // the Holy Mass does not outlive the round
            () => _citizens.Crowd?.Clear(), // the match cleared the entities; those still waiting to leave go too
            _pets.RoundStart, _companions.RoundStart, _wildlife.Clear, _minerals.RoundStart,
            _wildlife.Populate, // the match cleared the pets' entities too; a new round moves new ones into the property groups
            _rozhlas.RoundReset, // the Urad workers of a detour still open sit down again
            _loop.RoundReset, _scenery.RoundStart, // the vehicles are back where they started, the lamps and plates whole
            _harvester.RoundReset, // the car reset brought the harvester back to its old field: hidden again
            _scudEvent.RoundReset, // the Scud event is over with the round (no reward); the loop below retires its truck and carts
            _scud.RoundReset, // the launchers start over; a Scud truck, which never returns, is retired
            _status.RoundStart, _plantation.RoundStart, _lockOn.RoundReset,
            _houseDestruction.RoundStart, // every hollow house whole again, the rubble mode back to house_destruction.json's
            rounds.Started);
        var life = new LifeEvents(_ctx, _smoke, _progress, _bots, Config.Items, _gear, _status, _rozhlas, _streaks, _deathPush, _remains, _seats, Config.WeaponsSmoke, Config.RpgSmoke, _harvester.OnKill, _diag.Loss.Kill, _diag.Loss.Death);
        _matchEvents = new MatchEventsServer(_ctx, _smoke, _scoreboard, roundReset, life, rounds.Ended, _diag.Loss.RoundStart, _dummies.OnKilled);
        BuildRoster();
        RegisterChatCommands(); // after the features it names exist
    }

    /// <summary>The shot, the pellet fan, the player's input and the snapshots: the hot path, after everything it asks.</summary>
    void BuildShooting()
    {
        var baleShot = _harvester.Shot;
        var pellets = new PelletFan(_ctx, _shot, _glass, _carDamage, _targets, _damage.Deal, _scenery, _wildlife, Config.Vehicles, _rng, baleShot);
        _shots = new ShotServer(_ctx, _rewind, _shot, _glass, _carDamage, _targets, _damage.Deal, _scenery, _sentries, _wildlife, _lockOn, _rockets, pellets, _impacts, _smoke, Config.Vehicles, _rng, Config.Turret.KnifeDamage, Config.WeaponsSmoke,
            baleShot, new LagCompLog(Config.PerfOut, Config.PerfLagComp, _ctx, _rewind, _shot, _targets).Shot);
        _input = new InputServer(_ctx, _streaks, _rozhlas, _thieves, _gear, _sitters, _pounce, _sandbox, _mass, _garage, _houses, _seats, _guns, _shots, _smoke, _plantation.Use, _scud.UseKey, Config.KillY);
        _snapshots = new SnapshotServer(_ctx, _fleet, _sitters, _dummies, _rockets, Config.Vehicles, _diag.Perf.AuditTick, _diag.Loss.LogStates);
    }

    /// <summary>The vehicle step and what hangs on it, after the features it asks (the Thieves' vans, the harvester's driver, the Bigfoot's throw).</summary>
    void BuildVehicles()
    {
        _tow = new TowServer(_ctx, _fleet, _carBlast, _runOver, _bigfoot.StepThrownVehicle, Config.KillY);
        _loop = new VehicleLoop(_ctx, _fleet, _seats, _carDamage, _carBlast, _tow, _runOver, _glass, _smoke, Config.Vehicles, Config.Water, Config.KillY, Config.BlastSmoke, () => _thieves.ShoveCarsByVans(), _bigfoot.StepThrownVehicle, _harvester.IsDriver);
        _guns = new VehicleGuns(_ctx, _rewind, _shot, _glass, _scenery, _flak, _targets, _impacts, _damage.Deal, _carDamage, Config.Vehicles, _rng);
        _dealer = new CarDealer(_fleet, Config.Vehicles, Config.World, Config.Map, Config.Water, Config.Spawns, _rng, _garage, Config.BlastSmoke, Config.CarSmoke, Config.SmokeKind);
    }

    /// <summary>What the server needs once everything exists: the warmed indexes, the default vehicle table, the checks that fail at start and the two lines the smoke scripts wait for.</summary>
    void FinishStart()
    {
        _impacts.Warm(); // roads.json and the tree grid, so no impact pays for them on a tick (T3-003, T3-004)
        _flak.CheckWeapons(Config.Vehicles);
        GD.Print($"server: {Config.Spawns.Count} spawns, y {(Config.Spawns.Count > 0 ? Config.Spawns.Min(v => v.Y) : 0)}..{(Config.Spawns.Count > 0 ? Config.Spawns.Max(v => v.Y) : 0)}, killY {Config.KillY}");
        GD.Print($"SHOOTER_NET_LISTENING port={Config.Port} tick={Wire.TickRate}Hz");
    }

    /// <summary>
    /// The killstreaks: the table says which streak each entry is and the adapter of each is made here, in the table's order (the missile registers its entity type, the others only look).
    /// Streaks without an adapter keep their logging stub.
    /// </summary>
    StreakServer BuildStreaks() => new(_ctx, Config.Killstreaks, _gunships, () => Config.Spawns, _crates.GroundBelow, (streaks, id) =>
        id == MoabStreak.StreakId ? new MoabServer(_ctx, _targets, _hunts, _fleet, _carDamage.Damage, (owner, weapon) => _bigfoot.Hit(owner, weapon), _impacts, _rng).Streak() :
        id == CarePackageRules.StreakId ? _crates.Streak() :
        id == AttackMissileStreak.StreakId ? new MissileServer(_ctx, Config.Killstreaks, _targets, _shot.WorldHit, () => Config.Vehicles != null, _carDamage.Rockets, (at, radius) => _blasts.Blast(at, radius, BlastSource.Missile), _damage.Deal, _impacts, _splash, _deathPush).Streak() :
        id == TurretStreak.StreakId ? new TurretStreak(Config.Turret.Def, _match.Entities, _sentries) :
        id == AttackHelicopterStreak.StreakId ? new AttackHelicopterServer(_ctx, Config.Killstreaks, _gunships).Streak() :
        id == Ac130Streak.StreakId ? new Ac130Server(_ctx, Config.Killstreaks, _gunships).Streak() :
        id == HelicopterHelperStreak.StreakId ? new HelperServer(_ctx, Config.Killstreaks, Config.Weapons, streaks.SpawnOverMap, _targets, _sentries, _damage.Deal, _impacts, streaks.Rng).Streak() : null);

    /// <summary>
    /// The Thieves event's parts, made in the order they register their world entity types: the Gang members, the Raid vans, the Loot, then the reward, which wraps the hit routing of the first two.
    /// </summary>
    ThievesServer BuildThieves()
    {
        var phase = new ThievesEvent();
        var gangs = new GangServer(_ctx, Config.ThievesData, Config.Weapons, Config.Items, _corpses, _damage.Deal, () => _navMesh.Mesh, _navMesh.NextTicket, () => _walkers, _progress.Scores, _progress.Award);
        var raids = new RaidServer(_ctx, Config.ThievesData, phase, gangs, Config.Map, Config.World, _fleet, () => Config.Vehicles, () => _navMesh.Mesh, () => _fleet.FlightBounds,
            () => _church.Shell is { } church ? new System.Numerics.Vector2(church.DoorwayPos.X, church.DoorwayPos.Z) : null, _rng, (sweep, spare) => _runOver.Sweep(sweep, spare), _carDamage.Crash);
        var loot = new LootServer(_ctx, Config.ThievesData, raids.Roster, gangs.Roster, _fleet, _pets.Keeper, Config.Map, () => _navMesh.Mesh, Config.ArenaMode, Config.MapDir);
        var payout = new ThievesPayout(_ctx, Config.ThievesData, Config.Levels.Awards.EventWin, Config.Items, Config.Weapons, _corpses, _gear.BroadcastCorpseItems, _rng, _progress.Scores, _progress.Award, gangs.Roster, raids.Roster);
        return new ThievesServer(_ctx, Config.ThievesData, _events, _intel, _rng, () => _navMesh.Mesh, gangs, raids, loot, payout, phase);
    }

    /// <summary>The harvester event's parts: the cut and the crushed crops, the bales' fire, what the reel does to people, and the job that drives them.</summary>
    HarvesterServer BuildHarvester()
    {
        var cut = new CropCut(_ctx, Config.Harvester, Config.CropCrush, Config.Map, _fleet);
        var bales = new BaleFire(_ctx, Config.Harvester, Config.Map, _corpses, _status, cut);
        var reel = new ReelServer(_ctx, Config.Harvester, _fleet, _targets, _damage.Deal, _deathPush, _corpses, _gear, Config.Weapons, Config.Map, bales, CarPose.Velocity, RunOverServer.Cooldown);
        return new HarvesterServer(_ctx, Config.Harvester, () => Config.Vehicles, Config.Map, Config.World, Config.Water, () => _fleet.FlightBounds, _rng, _events, _fleet, _profiles, _ids, c => _roster.Join(c), c => _roster.Leave(c), CarPose.SeatPos, _seats.Leave, cut, bales, reel);
    }

    /// <summary>The Scud truck's parts: the launchers, the loading from a cart, the blast, the trucks' own tick, and the event that hides one (<c>_scud</c>, <c>_scudEvent</c>). The truck registers the missile's entity type.</summary>
    void BuildScud()
    {
        var rigs = new ScudRigs(_ctx, Config.Scud, _fleet);
        var loading = new ScudLoading(_ctx, Config.Scud, _fleet, rigs, _thieves, RocketCarts.SetLoaded);
        var blast = new ScudBlastServer(_ctx, Config.Scud.Blast, _targets, _splash, _deathPush, _damage.Deal, _hunts, _fleet, () => _areas.Shelters, _bigfoot.Hit, (at, radius, glassDamage) => _blasts.Blast(at, radius, BlastSource.Scud, glassDamage), _carDamage.Damage, CarPose.HullDistanceNow);
        _scud = new ScudServer(_ctx, Config.Scud, () => Config.Vehicles, Config.Map, Config.World, Config.Water, () => _fleet.FlightBounds, _fleet, rigs, loading, blast, () => _shot.Query);
        var places = new ScudPlaces(_ctx, Config.Scud, Config.Map, Config.World, _fleet, _rng, Config.Spawns, () => _areas.Shelters, _areas.PickEventSpawn);
        _scudEvent = new ScudEventServer(_ctx, Config.Scud, () => Config.Vehicles, _events, _intel, _rng, _fleet, places, _scud, _rocketCarts.Spawn, _rocketCarts.RemoveAll, _seats.Leave, _progress.Scores, _progress.Award);
    }

    /// <summary>The roster and the connections, after every feature a joiner is told about or a leaver is taken out of. The list is what a joiner is told, in this order.</summary>
    void BuildRoster()
    {
        var tellJoiner = new Action<ServerClient>[]
        {
            _gear.SendCorpseItemsOnJoin, // the items the corpses carry, after the corpses themselves
            _glass.SendOnJoin, // the glass panes that are not whole (ADR 0007)
            _scenery.SendOnJoin, // the pole lamps and sign plates that are not whole (ADR 0037, 0039)
            _church.SendOnJoin, // the church doors, when open (ADR 0019)
            _intel.SendOnJoin, // the events on the intel screen
            _mass.SendOnJoin, // the Holy Mass, when one runs
            _thieves.SendOnJoin, // the Thieves event, when one runs
            _minerals.SendOnJoin, // the mineral nodes
            _rozhlas.SendOnJoin, // the village loudspeaker, when a detour broadcast is on, who is on the mic, and the Urad door when it is open
            _garage.SendOnJoin, // and the garage doors, when they are open
            _houses.SendOnJoin, // and the houses' open doors
            _houseDestruction.SendOnJoin, // the house destruction settings and every carved house's removed cells
            _harvester.SendOnJoin, // the harvester's job and the bales that burn
            _scud.SendOnJoin, // the Scud trucks' racks and proposals
            _demon.SendOnJoin, // and the demon's pounce, when one is on
            _dummies.SendRoster, // target dummies' names (#148)
            _profiles.SendAppearancesOnJoin, // camouflage (#140): every player's look reaches the joiner, the joiner's reaches everyone
            _gear.SendWornOnJoin, // what the others wear (#266), to draw on their characters
            _gear.SendBinocularsOnJoin, // and who holds the binoculars up
            _gear.SendHeadLampsOnJoin, // and whose head lamp is on
            _status.SendOnJoin, // who is blessed, cursed or burning
        };
        _roster = new PlayerRoster(_ctx, Config, _host, _byId, _ids, _sentStates, _sentWorld, _progress, _areas, _animals, _profiles, _companions, _sandbox, _chat, _streaks, _status, _rozhlas, _sitters, _seats, tellJoiner);
        _connections = new ConnectionServer(_ctx, _host, _clients, _roster, _diag.Health, _sandbox, _profiles, Config.Map);
        _inputs = new InputIntake(_ctx, _sentStates, _sentWorld);
        _debug = new DebugServer(_ctx, _fleet, _carDamage, _smoke);
    }

    /// <summary>A sitter stood up: the dictation table lets go of the set, the Mass forgets the seat.</summary>
    void OnStood(ushort id, bool atTable)
    {
        if (atTable) _rozhlas.TableStood(id); // standing up from the dictation table lets go of the set
        _mass?.Stood(id);
    }

    /// <summary>The client has its profile: the hunter's log learns its name, a collection tier reached while the rule or the weapon was not there yet is rewarded, the companions follow.</summary>
    void OnProfileAttached(ServerClient c)
    {
        _hunts.OnProfile(c);
        _companions.FromProfile(c);
    }

    public override void _ExitTree() { _rozhlas?.Dispose(); _diag?.Perf.Flush(); _host.Destroy(); _navMesh?.Free(); }

    public override void _Process(double delta)
    {
        if (Config.Sim != null && Config.Sim.Pump()) _host.Flush(); // delayed packets go out at frame rate, not only on physics ticks
        _rozhlas?.Pump(); // the live rozhlas feed's due frames (a thread feeds ffmpeg; the sending is here, on the game thread)
    }

    /// <summary>The end of a test run: the verdict is the exit code, and no tick runs after it.</summary>
    void Quit(bool ok)
    {
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }

    /// <summary>
    /// One 30 Hz tick, in this order (it decides the simulation, so it does not change lightly): receive and housekeeping, the world (match, animals, events), the killstreaks,
    /// the bots, the players' inputs, the rockets and corpses, the vehicles, the blasts and the tick's flushes, the lag-compensation poses, then the snapshots and the report.
    /// Each step ends its phase with <c>_diag.Lap</c> (the perf CSV and the stats line), where the phase ends.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        var tickStartUs = Time.GetTicksUsec();
        var catchUp = _diag.Health.CatchUpTick(tickStartUs);
        if (Config.Sim != null && Config.Sim.Pump()) _host.Flush();
        _tick++;
        if (!_spawnsChecked)
        {
            if (_tick >= 3) { CheckSpawnGround(); _dealer.Deal(); _dummies.Plan(); _navMesh.Build(); } // always: citizens and events need it too, and a bake during play times the clients out
            return; // not accepting connections until the spawn list is validated
        }
        var dt = (float)delta;
        _elapsed += delta;
        _diag.TickBegin();
        ReceiveAndHousekeep();
        TickWorld(dt);
        TickStreaksAndBots(dt);
        SimulatePlayers(dt);
        TickProjectilesAndCorpses(dt);
        StepVehiclesAndRiders(dt);
        ResolveBlastsAndFlush(dt);
        CapturePoses();
        SendSnapshots(catchUp);
        _diag.TickEnd(tickStartUs, delta);
    }

    /// <summary>Network in: this tick's packets, the profile loads and saves that finished, and the connections and requests that wait on a clock.</summary>
    void ReceiveAndHousekeep()
    {
        _connections.Poll(OnPacket);
        _diag.Lap(Ph.Poll);
        _profiles.Tick();
        _hunts.Tick();
        _minerals.Tick(Config.Spawns.Count > 0);
        _connections.DropSilentPeers();
        _intel.Tick(); // a made-up event whose time is up leaves the intel screen
        _diag.Lap(Ph.Housekeeping);
    }

    /// <summary>The match and everything that lives in the world besides players: the entities, the citizens, the animals, the Mass, the Thieves, the rozhlas.</summary>
    void TickWorld(float dt)
    {
        var audit = _diag.Perf.Audit;
        audit?.Begin();
        var pt = Prof.Begin();
        _match.Update();
        _navMesh.Drain(); // the routes the worker finished since the last tick, applied here and nowhere else
        audit?.Lap(0);
        pt = Prof.Lap(Prof.S.MatchUpdate, pt);
        _bigfoot.Prepare(); // the players and vehicles the Bigfoot's brain looks at this tick
        _thieves.Prepare(); // the players the Gang members fight, and who each fights
        _animals.Prepare(); // the players and moving vehicles the animals look at, read once for all of them
        audit?.Lap(1);
        pt = Prof.Lap(Prof.S.Prepare, pt);
        _match.Entities.Tick(dt);
        audit?.Lap(2);
        pt = Prof.Lap(Prof.S.Entities, pt);
        _citizens.Tick(dt);
        audit?.Lap(3);
        pt = Prof.Lap(Prof.S.Citizens, pt);
        _pets.Tick(dt);
        audit?.Lap(4);
        pt = Prof.Lap(Prof.S.Pets, pt);
        _wildlife.Tick(dt);
        _companions.Tick();
        _animals.Done(); // the animals have ticked: the players move from here on
        audit?.Lap(5);
        pt = Prof.Lap(Prof.S.Wildlife, pt);
        _mass.Tick(dt); // the Holy Mass: its clock, the pastor, the seats
        audit?.Lap(6);
        _thieves.Tick(dt); // the Thieves event: its phase clock
        audit?.Lap(7);
        _rozhlas.Tick(dt); // the village loudspeaker: the schedule and the hijack clock
        audit?.Lap(8);
        Prof.End(Prof.S.Events, pt);
        _diag.Lap(Ph.Match);
    }

    void TickStreaksAndBots(float dt)
    {
        _streaks.Tick(dt);
        _diag.Lap(Ph.Streaks);
        _bots.Tick(dt); // fills the roster and queues each bot's input for this tick (#214)
        _harvester.Tick(dt); // the harvester event: its spawn, its driver's pedals
        _scudEvent.Tick(dt); // the Scud event: who boarded the truck, the search time, the bunkers' carts
        _diag.Lap(Ph.Bots);
    }

    /// <summary>Every joined player takes the inputs queued for this tick (bots included), then the Lock-on launchers step.</summary>
    void SimulatePlayers(float dt)
    {
        foreach (var car in _fleet.Cars) car.PlaceSolid(); // the boxes on-foot players are stopped by, where the vehicles stand after the last tick (#217)
        _thieves.PlaceVanSolids(); // and the Raid vans' (a van that stands is solid)
        _diag.Lap(Ph.PlaceSolids);
        _simClients.Clear(); _simClients.AddRange(_byId.Values); // a copy: a client may leave while the others are simulated
        foreach (var c in _simClients) _input.Simulate(c, dt); // every joined player, bots included
        _lockOn.Step(dt); // the Lock-on launchers: each Lock steps where its player now looks (ADR 0031)
        _diag.Lap(Ph.SimClients);
    }

    void TickProjectilesAndCorpses(float dt)
    {
        _rockets.Step(dt);
        _diag.Lap(Ph.Rockets);
        _remains.Step(dt);
        _diag.Lap(Ph.Corpses);
    }

    void StepVehiclesAndRiders(float dt)
    {
        _scud.Tick(dt); // the Scud trucks' launchers: the crew's lock, the rack, the brakes (before the vehicles step)
        _loop.Step(dt);
        _riding.Step(dt); // the dead pilots and drivers move with the vehicles that just moved
        _harvester.AfterVehicles(dt); // what the harvester ran over, the corpses it draws in, the bales' fire
        _diag.Lap(Ph.Vehicles);
    }

    /// <summary>What the tick's shots set off, and the messages that are sent once per tick however many things changed.</summary>
    void ResolveBlastsAndFlush(float dt)
    {
        _status.Tick(dt); // burns tick
        _plantation.Tick(dt); // highs run out
        _gunships.TickShells();  // shells whose tracer has arrived burst now (#162)
        _flak.Tick();    // flak shells that have arrived burst now
        _lockOn.StepVehicleHoming(dt); // missiles homing on flown helicopters fly and hit now
        _lockOn.DetonateHoming();  // homing missiles that have arrived hit their Aircraft now
        _gunships.FlushFx(); // the gunship rounds of this tick, coalesced (#159)
        _glass.Flush();     // the glass panes that changed this tick (ADR 0007)
        _houseDestruction.Flush(); // the server colliders of the houses carved this tick, each once
        _scenery.Tick(dt); // the pole lamps and road sign plates: clocks of the broken ones and the changes of this tick (ADR 0037, 0039)
        _status.Flush(); // statuses and burning that changed this tick
        _scoreboard.Flush(); // once a tick at most, and about once a second (the ping), however many kills the tick had (A-04)
        _diag.Lap(Ph.Detonate);
    }

    /// <summary>The poses this tick ends with, the state the snapshots carry and the lag compensation rewinds to.</summary>
    void CapturePoses()
    {
        _rewind.CapturePlayers(); // the poses this tick ends with, the state the snapshots carry (ADR 0013)
        _bigfoot.CapturePose();
        _thieves.RecordPoses(_tick); // Gang members and Raid vans
        _rewind.CaptureVehicles(); // and the vehicles' (B-02)
        _diag.Lap(Ph.LagCapture);
    }

    void SendSnapshots(bool catchUp)
    {
        if (catchUp && !_diag.Loss.On) _diag.Health.SkippedSnapshots++; // the next regular tick sends the current state
        else { _snapshots.Send(); _host.Flush(); }
        _diag.Lap(Ph.Snapshots);
    }

    /// <summary>Keeps only spawn candidates with collision under them (some pipeline spawns lie outside the built colliders and would drop players into the void).</summary>
    void CheckSpawnGround()
    {
        _spawnsChecked = true;
        var before = Config.Spawns.Count;
        Config.Spawns.RemoveAll(sp => _shot.Query.Ground(MoverKind.Player, new Vector3(sp.X, sp.Y, sp.Z), 2f, 5f) == null);
        GD.Print($"server: {Config.Spawns.Count}/{before} spawn candidates have ground");
        _diag.Loss.KeepSpawnCluster(Config.Spawns);
    }

    void OnPacket(ENetPacketPeer peer, byte[] d)
    {
        if (!_clients.TryGetValue(peer, out var c) || d.Length == 0) return;
        if (d[0] < Wire.TInput) OnReliable(c, Wire.DecodeReliable(d)); // message type tells the channel apart (Godot does not expose it per packet)
        else if (c.Joined && d[0] == Wire.TRozhlasMic) _rozhlas.OnMic(c, d); // the set holder's voice
        else if (c.Joined && d[0] == Wire.TInput) _inputs.OnPacket(c, d);
    }

    /// <summary>A reliable message: the hello first, then the events by kind (the first that matches takes it), anything else is the profile's (loadouts, slots, camo).</summary>
    void OnReliable(ServerClient c, ReliableMsg? m)
    {
        if (m is HelloMsg h && c.Phase == SessionPhase.Connected) _connections.OnHello(c, h);
        else if (m is EventMsg { Kind: (byte)EventKind.Debug } e && c.Joined && (Config.DebugCommands || Config.ArenaMode)) _debug.OnMessage(c, e.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpse } pu) _remains.OnPickUp(c, pu.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpseItem } pi) _gear.OnPickUpCorpseItem(c, pi.Payload);
        else if (m is EventMsg { Kind: >= (byte)EventKind.EquipItem and <= (byte)EventKind.TakeGroundItem } ir) _gear.OnItemRequest(c, (EventKind)ir.Kind, ir.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SetZero } sz) { if (c.Joined && BulletDrop.TryDecodeZero(sz.Payload, out var zi, out var zm) && zi < Config.Weapons.Weapons.Count && BulletDrop.HasDrop(Config.Weapons.Weapons[zi])) c.Zero.Set(zi, Config.Weapons.Weapons[zi], zm); }
        else if (m is EventMsg { Kind: (byte)EventKind.RaiseBinoculars } rb) { if (c.Joined && Binoculars.TryDecodeRaise(rb.Payload, out var up)) _gear.SetBinoculars(c, up && _gear.CarriesBinoculars(c)); }
        else if (m is EventMsg { Kind: (byte)EventKind.Redeploy } rd && c.Joined) _profiles.OnRedeploy(c, rd.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.OpenCarePackage } oc) _crates.OnOpen(c, oc.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.MineralPick } mp && c.Joined) _minerals.OnPick(c, mp.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.CompanionPick } cp && c.Joined) _companions.OnPick(c, cp.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SandboxGrant } sg && c.Joined) _sandbox.OnGrant(c, sg.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SandboxAction } sa && c.Joined) _sandbox.OnAction(c, sa.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SandboxTeleportTo } st && c.Joined) _sandbox.OnTeleportTo(c, st.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SeatSwitch } sw && c.Joined) _seats.OnSwitch(c, sw.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.ScudCommand } sc && c.Joined) _scud.OnCommand(c, sc.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.TeamAssign } ta && c.Joined) _teams.OnTeamAssign(c, ta.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.SquadAssign } sq && c.Joined) _teams.OnSquadAssign(c, sq.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.HuntsRequest or (byte)EventKind.RecordsRequest or (byte)EventKind.PlayerLogRequest } hq && c.Joined) _hunts.OnRequest(c, hq);
        else if (m is EventMsg { Kind: (byte)EventKind.ChatSend } cs) _chat.OnSend(c, cs.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.HouseResync } hr && c.Joined) _houseDestruction.OnResync(c, hr.Payload);
        else if (m is EventMsg { Kind: (byte)EventKind.ClientReport } cr) { if (c.Joined) _diag.Metrics.OnClientReport(c, cr.Payload); }
        else if (c.Joined) _profiles.OnMessage(c, m);
    }

    /// <summary>Hands each admin command to the chat; the commands of a feature with a class of its own are that class's methods.</summary>
    void RegisterChatCommands()
    {
        var admin = new AdminCommands(_ctx, () => Config.Clock, clock => Config.Clock = clock, () => _shot.Query, Config.Weapons, _rocketIds, _rozhlas, _intel, _demon, (kind, c) => _eventServer.Start(kind, c), (c, action) => _eventServer.Current(c, action));
        ChatCommandTable.Register(_chat, admin, _bots, _spawner, _wildlife, _sandbox, _gear, _companions, _citizens, _church, _rozhlas, _status, _scud, _teams, _houseDestruction);
    }
}
