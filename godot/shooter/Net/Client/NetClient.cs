using Godot;
using Drycastle.Shooter.Bunkers;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Glass;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Scenery;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Network client over raw ENet (ADR 0002). Sends one input per 30 Hz physics tick (the last 3 for loss cover),
/// predicts the local player with the shared movement step and reconciles against the server position at the
/// acked input seq (replaying the unacked inputs), and renders remote players about 100 ms (3 ticks) in the past
/// from interpolation buffers as capsules with a name label.
/// </summary>
public partial class NetClient : Node
{
    public const double InterpDelayTicks = 3.0;
    public const float SnapDistance = 2f;

    /// <summary>What the features of this client may use of the rest of it (see <see cref="ClientContext"/>); the plain members below forward to it.</summary>
    public ClientContext Ctx { get; }
    public Node3D World { get => Ctx.World; set => Ctx.World = value; }
    public string Host = "127.0.0.1";
    public int Port = 27015;
    public string PlayerName { get => Ctx.PlayerName; set => Ctx.PlayerName = value; }
    /// <summary>Player token sent in the hello (ADR 0003). Empty = a fresh one is generated (bots and CLI clients).</summary>
    public string Token = "";
    /// <summary>Raised for an error the server sends after the join (for example an invalid loadout).</summary>
    public event Action<string>? ServerError;
    string? _rejectReason;
    public bool Bot { get => Ctx.Bot; set => Ctx.Bot = value; }
    /// <summary>Network impairment for the loss and latency test (--net-sim); null = none.</summary>
    public NetSim? Sim { get => _server.Sim; set => _server.Sim = value; }
    public bool Headless;
    public float KillY = float.NegativeInfinity;
    /// <summary>The map's water (water.json), shared with the prediction so swimming matches the server.</summary>
    public Drycastle.ShooterCore.Waters.Water Water { get => Ctx.Water; set => Ctx.Water = value; }
    /// <summary>Match events stored for the HUD to come: phase, countdown, scoreboard rows, kill feed, own spawn/death.</summary>
    public ClientMatchState Match => Ctx.Match;
    public bool Dead => Ctx.Dead;

    /// <summary>The mode flags as one value to ask named questions of (<see cref="ClientView.Mode"/>).</summary>
    ClientModeInputs Mode => Ctx.View.Mode;
    /// <summary>holy_mass.json (set by the host): the bodies of the citizens and the Mass's chant.</summary>
    public HolyMassFile HolyMassData = new();
    /// <summary>thieves.json (set by the host): the looks and clips of the Gang members, the vans and the Loot.</summary>
    public ThievesFile ThievesData = new();
    /// <summary>weapons.json: hotbar order and the per-weapon movement speed multiplier used by prediction.</summary>
    /// <summary>killstreaks.json: streak names, thresholds and the tracker rules.</summary>
    public KillstreakFile Killstreaks { get => Ctx.Killstreaks!; set => Ctx.Killstreaks = value; }
    public WeaponsFile Weapons { get => Ctx.Weapons!; set => Ctx.Weapons = value; }
    /// <summary>match.json as the client shipped it: the gunfire blips' radius, lifetime and rate limit (null: no blips, e.g. headless bots).</summary>
    public MatchConfig? BlipConfig;
    OwnViewmodel _viewmodel => Ctx.Viewmodel;
    readonly FxDemoClient _fxDemo;
    /// <summary>Dev aid (<c>--fx-demo</c>, set by the host): see <see cref="FxDemoClient"/>.</summary>
    public string FxDemo { get => _fxDemo.Mode; set => _fxDemo.Mode = value; }

    readonly ServerLink _server = new();
    /// <summary>The link to the server (Client.Link): connecting, welcomed, spawned (own snapshot applied), failed, left. Replaces the old welcomed/gotSelf/done flags.</summary>
    readonly StateMachine<LinkPhase> _link = new(ConnectionPhases.Link);
    /// <summary>A welcome was received at some point (it stays true after a failure or a leave: the perf and telemetry output still flush, and Fail tells the menu whether the match was joined).</summary>
    bool _welcomed;
    bool OwnSpawned => _link.Is(LinkPhase.Spawned);
    bool LinkEnded => _link.Current.IsEnded();
    ushort _seq;
    readonly OwnPrediction _prediction;
    readonly BodyFlings _flings;
    RemotesClient _remotes = null!;
    BaleCorpses _bales = null!;
    CorpsesClient _corpses = null!;
    CorpseLoot _loot = null!;
    readonly InteractTargets _interact = new();
    GroundItemsClient _groundItems = null!;
    CarePackagesClient _crates = null!;
    HoldRing _holdRing = null!;
    HarvesterClient _harvester = null!;
    BigfootClient _bigfoot = null!;
    BigfootFx _bigfootFx = null!;
    /// <summary>bigfoot.json: set by the host.</summary>
    public BigfootFile BigfootData = new();
    BinocularsClient _binoculars = null!;
    LockOnClient _lockOn = null!;
    RocketsClient _rockets = null!;
    ThrowClient _thrower = null!;
    HeldWeapon _held = null!;
    ShotsClient _shots = null!;
    OwnReconcile _reconcile = null!;
    DeathView _death = null!;
    VehiclesClient _vehicles = null!;
    HudClient _hud = null!;
    EffectEvents _effects = null!;
    MatchEvents _match = null!;
    PoseDemoClient? _poseDemo;
    OwnAim _aim = null!;
    OwnWeapon _weapon = null!;
    /// <summary>Replicated world entities (turrets, missiles, ...). Subscribe to Spawned/Despawned for visuals.</summary>
    public WorldReplica WorldEntities => Ctx.WorldEntities;
    double _lastArrival;
    double _elapsed;
    int _snaps;
    NVec3 _meMin = new(float.MaxValue), _meMax = new(float.MinValue);

    // the features that need only the context; the ones that take the host's data are made in _Ready
    readonly MoabClient _moab;
    readonly DoorsClient _doors;
    readonly UavClient _uav;
    readonly IntelClient _intel;
    BlipsClient _blips = null!;
    readonly PoopClient _poop;
    readonly HeadLampClient _headLamps;
    readonly RozhlasClient _rozhlas;
    readonly BlastDustClient _blastDust;
    readonly ImpactsClient _impacts;
    MassClient _mass = null!;
    CitizensClient _citizens = null!;
    StatusClient _status = null!;
    ThievesClient _thieves = null!;
    /// <summary>The own high's nausea and cigarette (the Equipment screen asks whether the player is high).</summary>
    public HighClient High { get; private set; } = null!;
    /// <summary>plantation.json (the host sets it from the data directory; the shipped defaults otherwise).</summary>
    public PlantationFile Plantation = PlantationFile.Default;
    SessionLog _session = null!;
    PerfLog _perf = null!;
    /// <summary>The perf harness' client log directory (--perf-out); empty = no log.</summary>
    public string PerfOut = "";
    GlassClient _glass = null!;
    FlightClient _flight = null!;
    DriveClient _driveClient = null!;
    TowClient _tow = null!;
    SeatsClient _seats = null!;
    VehicleHulls _hulls = null!;
    VehicleGunsClient _guns = null!;
    MissileClient _missile = null!;
    TurretsClient _turrets = null!;
    ScudClient _scud = null!;
    readonly TargetsClient _targets;
    VolumesClient _volumes = null!;
    AnimalsClient _animals = null!;
    FlocksClient _flocks = null!;
    /// <summary>wildlife.json: set by the host (the flock kinds; the kind index of a state is a position in <c>Flocks</c>).</summary>
    public WildlifeFile WildlifeData = new();
    /// <summary>The Companions the player can pick and the one that is out (the Equipment screen's picker).</summary>
    public CompanionsClient Companions { get; private set; } = null!;
    /// <summary>companions.json: set by the host (the index of a Companion in it is what the wire carries).</summary>
    public CompanionsFile CompanionData = new();
    /// <summary>animals.json: set by the host (the species table; the species index of a state is a position in it).</summary>
    public AnimalsFile AnimalData = new();
    /// <summary>The hit shapes of the species: set by the host. The client only draws them (the hit diagram); the server tests bullets against them.</summary>
    public AnimalShapes AnimalShapes = new();
    PounceClient _pounce = null!;
    DemonClient _demon = null!;
    /// <summary>holy_mass_demon.json: set by the host (gravity of the fireballs, the turning's timeline).</summary>
    public DemonFile DemonData = new();
    /// <summary>scud.json, replaced by the host with the file's.</summary>
    public ScudFile Scud = ScudFile.Default;
    AircraftClient _aircraftClient = null!;
    readonly EnemyChestPoints _markers;
    readonly WaterClient _water;
    RiderCamera _rider = null!;
    CropCrushClient _crush = null!;
    /// <summary>glass.json (set by the host); the defaults when it sets none.</summary>
    public GlassConfig GlassData = new();
    MineralsClient _minerals = null!;
    HuntsClient _hunts = null!;
    ScreensClient _screens = null!;
    /// <summary>The sandbox mode's state and requests (the panel and the host reach them here).</summary>
    public SandboxClient Sandbox { get; }
    /// <summary>The performance report sent to the server and the scripted player of the client load test (--perf-play; the host sets it).</summary>
    public ReportClient Report { get; }
    /// <summary>The HUD screens a key opens (Equipment, the map, the logs): Esc, the mouse, the pause menu's entries.</summary>
    public ScreensClient Screens => _screens;
    /// <summary>The chat box, the Squads screen's and the Teams screen's requests (the menus reach them here).</summary>
    public ChatClient Chat { get; private set; } = null!;
    /// <summary>The player's profile and loadouts (the menus read and change them here).</summary>
    public LoadoutClient Loadouts { get; private set; } = null!;
    KillstreaksClient _streaks = null!;
    /// <summary>perks.json (set by the host): the loadout's perk id to its modifiers (prediction).</summary>
    public PerksFile PerkData = null!;
    /// <summary>True while a menu screen is open; the respawn number keys then leave the menu alone.</summary>
    public Func<bool>? MenuOpen { get => Ctx.MenuOpen; set => Ctx.MenuOpen = value; }
    /// <summary>The players' camouflage looks (the Equipment and Customization screens read and save them here).</summary>
    public CamoClient Camo { get; private set; } = null!;
    /// <summary>camo.json (set by the host).</summary>
    public CamoFile CamoData = null!;
    public SquadsClient Squads { get; private set; } = null!;
    public TeamsClient TeamAdmin { get; private set; } = null!;

    /// <summary>items.json: set by the host (an item is named on the wire by its position in this list).</summary>
    public ItemsFile ItemData = new();
    /// <summary>What the local player wears and carries, and what the corpses carry (the Equipment screen reads and asks here).</summary>
    public ItemsClient Items { get; private set; } = null!;
    /// <summary>vehicles.json (set by the host): the type per snapshot record, visuals and the smoke's expected run-over damage.</summary>
    public VehiclesFile? Vehicles { get => Ctx.Vehicles; set => Ctx.Vehicles = value; }
    /// <summary>Set by the host from <c>--pose-demo</c>.</summary>
    public bool PoseDemo;

    public NetClient()
    {
        Ctx = new ClientContext(this) { Send = _server.Send, Connected = () => _server.Peer != null, SendJoined = m => _welcomed && _server.SendNow(m) };
        Ctx.View.OnTurret = () => _aircraftClient.AtTurret;
        Ctx.View.ScudScreen = () => _scud.ScreenOn;
        Ctx.View.Controlling = () => _missile.Controlling;
        Ctx.View.Pinned = () => _pounce.Pinned;
        Ctx.View.Rising = () => _pounce.Rising;
        Ctx.Viewmodel.StepAcc = () => Ctx.Sound.StepAcc;
        _prediction = new OwnPrediction(Ctx, slot => _held.SpeedFor(slot));
        _flings = new BodyFlings(Ctx);
        _fxDemo = new FxDemoClient(Ctx);
        _water = new WaterClient(Ctx);
        _markers = new EnemyChestPoints(Ctx);
        Sandbox = new SandboxClient(Ctx);
        Ctx.Remotes.NameTag = Sandbox.NameTag;
        Report = new ReportClient(Ctx, _server.Conn, () => !Bot && !LinkEnded && _welcomed, () => _server.Peer, () => _elapsed);
        _moab = new MoabClient(Ctx);
        _doors = new DoorsClient(Ctx);
        _uav = new UavClient(Ctx);
        _intel = new IntelClient(Ctx);
        _poop = new PoopClient(Ctx);
        _headLamps = new HeadLampClient(Ctx);
        _rozhlas = new RozhlasClient(Ctx, _server.SendMic);
        _blastDust = new BlastDustClient(Ctx);
        _impacts = new ImpactsClient(Ctx);
        _targets = new TargetsClient(Ctx, Sandbox, _blastDust);
    }

    public override void _Ready()
    {
        if (Token.Length == 0) Token = Guid.NewGuid().ToString();
        if (KeepEventLogs) Ctx.Logs = _logs;
        Items = new ItemsClient(Ctx, ItemData, () => _rider.SelfView);
        _blips = new BlipsClient(Ctx, BlipConfig);
        _mass = new MassClient(Ctx, HolyMassData, _prediction.Drop);
        Ctx.View.MassSeated = () => _mass.Seated;
        _citizens = new CitizensClient(Ctx, HolyMassData, _mass);
        _status = new StatusClient(Ctx, Plantation);
        Ctx.Remotes.Appeared += (id, _) => _status.SyncFx(id);
        High = new HighClient(Ctx, _status, Plantation, () => Mode, () => OwnSpawned);
        Squads = new SquadsClient(Ctx);
        Chat = new ChatClient(Ctx, ItemData, CompanionData, Squads);
        TeamAdmin = new TeamsClient(Ctx, Chat);
        _minerals = new MineralsClient(Ctx);
        Loadouts = new LoadoutClient(Ctx, PerkData, Chat);
        _streaks = new KillstreaksClient(Ctx, Loadouts);
        _streaks.Used += id => _missile.OnStreakUsed(id);
        _streaks.Ended += id => _missile.OnStreakEnded(id);
        _streaks.OwnerDied += () => { _missile.OnOwnerDied(); _uav.Ended(); };
        _streaks.Gunner.HintsChanged += () => _aircraftClient.MarkDirty();
        _session = new SessionLog(Ctx, Host, Port, () => !Bot && !LinkEnded && _welcomed, () => _server.Peer);
        _perf = new PerfLog(Ctx, PerfOut, () => _welcomed, () => _server.Peer, Ctx.Snapshots);
        _glass = new GlassClient(Ctx, GlassData);
        _driveClient = new DriveClient(Ctx, id => _bigfoot.CarThrown(id), v => _hulls.PlaceSolid(v), id => _scud.Immobilises(id));
        _tow = new TowClient(Ctx, _driveClient);
        _crush = new CropCrushClient(Ctx, _driveClient);
        _flight = new FlightClient(Ctx, id => _bigfoot.CarThrown(id), v => _hulls.PlaceSolid(v), _session.FlightCorrection);
        _hulls = new VehicleHulls(Ctx, _flight, _driveClient, id => _bigfoot.CarThrown(id), GlassData);
        _aim = new OwnAim(Ctx, Loadouts, () => _binoculars.Up);
        _weapon = new OwnWeapon(Ctx, _aim, _hulls, _fxDemo, () => _lockOn.Complete, () => _flings.OwnFlung);
        _seats = new SeatsClient(Ctx);
        _rider = new RiderCamera(Ctx, _flight, DressSelf);
        _guns = new VehicleGunsClient(Ctx, _hulls, _rider, _water);
        Camo = new CamoClient(Ctx, CamoData, (owner, look) => _corpses.DressCamo(owner, look));
        Ctx.Remotes.Dressing += Camo.Dress;
        Ctx.Remotes.Dressing += Items.DressPlayer;
        _hunts = new HuntsClient(Ctx, Sandbox, ItemData, CompanionData, AnimalData, AnimalShapes);
        _bot = MakeBot();
        _reconcile = new OwnReconcile(Ctx, _prediction, _link, _perf, _session, Report, _bot);
        _poseDemo = PoseDemo ? new PoseDemoClient(Ctx, Camo) : null;
        _bales = new BaleCorpses(Ctx, Camo);
        _corpses = new CorpsesClient(Ctx, Items, Camo, _bales, BigfootData, _interact);
        _loot = new CorpseLoot(Ctx, Items, _corpses, _interact);
        _harvester = new HarvesterClient(Ctx, _corpses, _bales, new DroppedBales(Ctx, _bales, at => _harvester.NearestHarvester(at)), _crush);
        _screens = new ScreensClient(Ctx, Chat, Sandbox, _loot.Chooser);
        _groundItems = new GroundItemsClient(Ctx, Items, ItemData, _interact, _corpses);
        _holdRing = new HoldRing(Ctx, () => _crates.HoldFraction, () => _rozhlas.HoldFraction, () => _thieves.Loot.HoldFraction, () => _minerals.HoldFraction);
        _crates = new CarePackagesClient(Ctx, _interact, _holdRing);
        _missile = new MissileClient(Ctx, _blastDust, _markers);
        _turrets = new TurretsClient(Ctx);
        _animals = new AnimalsClient(Ctx, AnimalData, AnimalShapes, WildlifeData, _driveClient);
        Companions = new CompanionsClient(Ctx, CompanionData, AnimalData);
        _bigfoot = new BigfootClient(Ctx, BigfootData, _flings);
        _bigfootFx = new BigfootFx(Ctx, BigfootData, _bigfoot);
        _thieves = new ThievesClient(Ctx, ThievesData, ItemData);
        _pounce = new PounceClient(Ctx, _prediction, DemonData, () => _demon.FirstView());
        _binoculars = new BinocularsClient(Ctx, Items, ItemData, _hulls, _animals, () => _pounce.Pinned);
        Ctx.Remotes.Appeared += _binoculars.OnAppeared;
        _death = new DeathView(Ctx, _pounce, _corpses);
        _remotes = new RemotesClient(Ctx, _flings, _animals, _pounce, _targets) { BodyReplaced = (id, dead) => _corpses.CorpseOf(id) || _harvester.VictimHidden(id, dead) };
        _demon = new DemonClient(Ctx, DemonData, HolyMassData, _mass, _blastDust, _pounce);
        _aircraftClient = new AircraftClient(Ctx, _streaks, _blastDust, _markers, at => _rockets.HomingKillShown(at));
        EventReceived += _aircraftClient.OnEvent;
        _flocks = new FlocksClient(Ctx, WildlifeData, _aircraftClient);
        _lockOn = new LockOnClient(Ctx, _aircraftClient);
        EventReceived += _lockOn.OnEvent;
        _rockets = new RocketsClient(Ctx, _weapon, _blastDust, _lockOn);
        EventReceived += _rockets.OnEvent;
        EventReceived += _guns.OnEvent;
        EventReceived += _seats.OnEvent;
        _scud = new ScudClient(Ctx, Scud, _blastDust);
        _thrower = new ThrowClient(Ctx, _binoculars, _aircraftClient, _scud, _flings);
        _held = new HeldWeapon(Ctx, _aim, Loadouts, _binoculars, _thrower, _flings, _aircraftClient, _scud, _pounce, _interact);
        _vehicles = new VehiclesClient(Ctx, _tow, _glass, _hulls, _flight, _driveClient, _rider, _guns, _seats, _aircraftClient, _water, _scud, _blastDust) { PromptLine = PromptLine };
        _hud = new HudClient(Ctx, _weapon, _driveClient, _aircraftClient, _thieves, _bigfoot, _screens, _uav, new MinimapFeed(Ctx, _missile, _aircraftClient, _intel), _interact, () => _snaps);
        _shots = new ShotsClient(Ctx, _weapon, _hulls, _water, _guns, _aircraftClient, _impacts);
        _match = new MatchEvents(Ctx, _streaks, Loadouts, _blastDust, _prediction, _link, _weapon, _death, _corpses, _scud, _bot);
        _effects = new EffectEvents(Ctx, _weapon, _hud, _blips, _guns, _shots, _rockets, _hunts, _moab, _missile, _impacts, _aircraftClient, _remotes, _vehicles, _glass);
        _volumes = new VolumesClient(Ctx, _animals, AnimalShapes, _citizens, _thieves, _demon, _hulls);
        EventReceived += _scud.OnEvent;
        Match.RoundRestarted += () => { Ctx.Hud?.ClearKills(); _harvester.RoundRestart(); };
        if (_server.Connect(Host, Port) is { } failure) { Fail(failure); return; }
        GD.Print($"client: connecting to {Host}:{Port} as {PlayerName}");
    }

    public override void _ExitTree()
    {
        _session.Close();
        _server.Destroy();
        FreeOwned(); // also when the client leaves without Teardown
    }

    /// <summary>Menu mode: raised instead of quitting when the connection fails or drops (the reason is short, for the error message).</summary>
    public Action<string, bool>? OnFailed;
    /// <summary>Menu mode: raised once the server's welcome arrived (the match is joined).</summary>
    public Action? OnWelcomed;

    /// <summary>Leaves the server and removes everything this client put into the world (back to the main menu).</summary>
    public void Teardown()
    {
        _link.Enter(LinkPhase.Left);
        _session.Close();
        foreach (var r in Ctx.Rockets.Values) r.Snd?.Release();
        Ctx.Cars.ReleaseSounds();
        _rozhlas.Reset(); // the mic is shut
        _server.Disconnect();
        Ctx.Remotes.Clear();
        foreach (var r in Ctx.Rockets.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        Ctx.Rockets.Clear();
        Ctx.Cars.Clear(); _flight.Clear(); _driveClient.Clear(); // the probes are under Owned and go with them
        _glass.Teardown();
        if (Ctx.Me != null && GodotObject.IsInstanceValid(Ctx.Me)) Ctx.Me.QueueFree();
        Ctx.Me = null;
        FreeOwned();
    }

    /// <summary>Frees everything this client put under <see cref="Owned"/> (effects, pools, props, missiles), so a leave and a rejoin leave no nodes behind (#169, T2-17).</summary>
    void FreeOwned()
    {
        Ctx.FreeOwned();
        _missile.Clear();
        _animals.Clear(); // the nodes are under Owned
        _poop.Reset();
        _citizens.Reset();
        _impacts.Reset();
    }

    /// <summary>Dresses the own figure seated in a vehicle: the camouflage, and the headgear (#266).</summary>
    void DressSelf(CharacterView view) { Camo.Dress(view, Ctx.MyId); Items.DressPlayer(view, Ctx.MyId); }

    /// <summary>The prompt line of the frame: what the keys do where the player stands, from the ride's own prompt and the other features', in their order of priority (a placing turret owns it).</summary>
    string? PromptLine(string? ride) =>
        _vehicles.LadderPrompt() ?? _turrets.Prompt() ?? _thieves.Loot.Prompt() ?? _doors.GarageDoorPrompt(Ctx.Driving) ?? _scud.LoadPrompt(ride) ?? ride ?? _mass.Prompt() ?? _rozhlas.Prompt() ?? _minerals.Prompt()
        ?? _crates.Prompt() ?? _groundItems.Prompt() ?? _loot.Prompt() ?? _vehicles.LadderReachPrompt();

    void HudWelcome()
    {
        if (!ShotFx.Enabled || Ctx.Hud != null) return; // headless runs have no HUD
        Ctx.Hud = new Hud { MyId = Ctx.MyId, KillWeaponName = id => Weapons != null && id < Weapons.Weapons.Count ? ClientText.WeaponName(Weapons.Weapons[id].Id) : null, Scoped = () => Ctx.Me is { ZoomFov: > 0 } && !_binoculars.Up, Binoculars = _binoculars.View, Aim = () => _viewmodel.AimAmount, Breath = _aim.BreathView, Scope = _aim.ScopeView, Spread = _hud.CrosshairSpread };
        AddChild(Ctx.Hud); // the hotbar names are set at each spawn (ApplySpawnLoadout)
        Ctx.Hud.TacticalMapTeleport = _screens.OnMapTeleport;
        Sandbox.ShowOnHud(); // the SANDBOX badge (#156)
        Loadouts.WireHud(Ctx.Hud);
        Ctx.Hud.AttachEquipment(this); // F (#268)
        Ctx.Hud.AttachHuntersLog(_hunts.Environment()); // J (#336)
        Ctx.Hud.AttachMineralLog(); // N
        _hunts.Data.RequestLog(0); // learns the player's own id in the log (their own announcements say "you"), and warms My Log
        Ctx.Hud.Minimap.Build(Map); // once per connection; the texture comes from the map's parcels and roads (ticket #119)
        Ctx.Hud.PrewarmTacticalMap(GetViewport().GetVisibleRect().Size); // the full-screen map's picture is made on a worker meanwhile
    }

    /// <summary>The loaded map (set by the host): the minimap's texture source.</summary>
    public Drycastle.MapLoader.MapLoader? Map { get => Ctx.Map; set => Ctx.Map = value; }

    void Fail(string why)
    {
        GD.PrintErr($"client: {why}");
        var joined = _welcomed;
        _link.Enter(LinkPhase.Failed);
        if (OnFailed != null) { OnFailed(why, joined); return; }
        if (Bot) GD.Print("SHOOTER_NET_CLIENT_FAIL");
        GetTree().Quit(2);
    }

    /// <summary>
    /// The player's own Respawn (#284): the server kills them as a suicide and the normal respawn applies the loadout. <paramref name="slot"/> is selected
    /// first (null keeps the active one); unsent loadout edits go out ahead of it, so "apply now" uses what is on screen.
    /// </summary>
    public void Redeploy(int? slot)
    {
        if (_server.Peer == null || !_welcomed || Dead) return;
        Loadouts.FlushAutosave();
        _server.SendNow(new EventMsg((byte)EventKind.Redeploy, Drycastle.ShooterCore.Match.Redeploy.Encode(slot)));
    }

    public override void _Process(double delta)
    {
        _session.Frame(delta);
        _perf.Frame(delta);
        Report.Frame(delta);
        _server.Pump();
        if (LinkEnded) return;
        _screens.UpdateMapCursor();
        if (_volumes.On) _volumes.Update();
        var t = ClientProf.Begin();
        _server.Poll(() => new HelloMsg(Wire.ProtocolVersion, PlayerName, Token, Sandbox.Requested ? HelloFlags.Sandbox : (byte)0, Map?.BuildHash ?? 0), OnServerGone, OnPacket);
        t = ClientProf.Lap(ClientProf.S.Net, t);
        if (_link.Is(LinkPhase.Connecting))
        {
            _link.Tick((float)delta);
            if (_link.Elapsed > ConnectionPhases.ConnectTimeoutSeconds) Fail("connect timeout");
            return;
        }
        Loadouts.PumpAutosave(); // loadout saves whose debounce ran out go out here, whichever screen is open (#145)
        RenderPlayersAndProps(delta);
        t = ClientProf.Lap(ClientProf.S.Players, t);
        RenderCreatures(delta);
        t = ClientProf.Lap(ClientProf.S.Creatures, t);
        _death.Update(delta);
        _pounce.Update(delta);
        t = ClientProf.Lap(ClientProf.S.View, t);
        RenderVehicles(delta);
        t = ClientProf.Lap(ClientProf.S.Vehicles, t);
        RenderProjectiles(delta);
        t = ClientProf.Lap(ClientProf.S.Projectiles, t);
        UpdateView(delta);
        t = ClientProf.Lap(ClientProf.S.View, t);
        _hud.Update();
        _rozhlas.MicFrame(); // the set holder's mic and the ON AIR cue
        _rozhlasTest?.MicTick(_rozhlas.HoldsSet);
        ClientProf.End(ClientProf.S.Hud, t);
    }

    /// <summary>The players, their head lamps, the corpses, crates, items, minerals and turrets.</summary>
    void RenderPlayersAndProps(double delta)
    {
        _remotes.Render(delta);
        _headLamps.Render();
        _poseDemo?.Tick();
        _corpses.Render(delta);
        _groundItems.Render();
        _crates.Render();
        _minerals.Render();
        _turrets.Render();
    }

    void RenderCreatures(double delta)
    {
        _animals.Render();
        Companions.Render();
        Companions.TickMarks();
        _flocks.Render(delta);
        _bigfoot.Render(delta);
        _bigfootFx.Render();
        _citizens.Render();
        _thieves.Render(delta);
        _demon.Render();
    }

    void RenderVehicles(double delta)
    {
        _vehicles.Render(delta);
        _harvester.Render((float)delta);
        _aircraftClient.Render(delta);
    }

    void RenderProjectiles(double delta)
    {
        _rockets.Render(delta);
        _lockOn.Render();
        _weapon.ShowLoaded();
        _missile.RenderMissiles();
        _missile.RenderPov();
        _scud.Render(delta);
    }

    /// <summary>Fades the drawn part of the last on-foot correction, once per frame, at the same rate as the vehicles' corrections.</summary>
    void FadeOwnCorrection(double delta)
    {
        if (Ctx.Me == null) return;
        _prediction.FadeCorrection(delta);
        Ctx.Me.ViewOffset = _prediction.ViewFix + _bigfoot.HeldSwing; // in the Bigfoot's hand the eye swings with the hand (#264)
        Ctx.Me.ViewRoll = _bigfoot.HeldRoll + High.Roll;
    }

    /// <summary>The own view: water, the viewmodel, the camera's shakes and sways, in the order that composes the camera.</summary>
    void UpdateView(double delta)
    {
        _water.Render();
        _held.ShowViewmodel();
        _thrower.UpdateArc();
        _demon.UpdateQuake(delta);
        _blastDust.Update(); // before the shake: a blast wave that arrives this frame shakes this frame
        Ctx.Shake.Update(delta);
        High.Update(delta); // a high player's sway, before the roll is composed
        FadeOwnCorrection(delta);
    }

    void OnServerGone()
    {
        if (!LinkEnded) Fail(_rejectReason ?? "server disconnected");
    }

    void OnPacket(byte[] d)
    {
        if (d.Length == 0) return;
        var t = ClientProf.Begin();
        if (d[0] < Wire.TInput)
        {
            var m = Wire.DecodeReliable(d);
            OnReliable(m);
            Report.Event(m is EventMsg e ? e.Kind : (byte)0, ClientProf.Move(ClientProf.S.Net, ClientProf.S.NetEvents, t));
        }
        else if (d[0] == Wire.TSnapshot) { OnSnapshot(d); ClientProf.Move(ClientProf.S.Net, ClientProf.S.NetSnapshot, t); }
        else if (d[0] == Wire.TWorldSnapshot) { WorldEntities.Apply(d); ClientProf.Move(ClientProf.S.Net, ClientProf.S.NetWorld, t); }
        else if (d[0] == Wire.TRozhlasFeed && RozhlasFeedWire.TryDecode(d, out var feedSeq, out var feedOpus)) { RozhlasFeedPlayer.Current?.Push(feedSeq, feedOpus); _rozhlasTest?.OnFeedFrame(feedOpus); } // the live rozhlas feed (silent when headless)
    }

    void OnReliable(ReliableMsg? m)
    {
        switch (m)
        {
            case WelcomeMsg w:
                OnWelcome(w);
                break;
            case ProfileMsg p:
                Loadouts.OnProfile(p);
                GD.Print($"client: profile {p.Name}, level {p.Level}, xp {p.Xp}, active slot {p.ActiveSlot}");
                break;
            case ErrorMsg er:
                GD.PrintErr($"client: server error: {er.Reason}");
                if (!_welcomed) _rejectReason = er.Reason; // the server disconnects after a refused hello
                ServerError?.Invoke(er.Reason);
                break;
            case JoinMsg j: Ctx.Names[j.PlayerId] = j.Name; GD.Print($"client: {j.Name} #{j.PlayerId} joined"); break;
            case LeaveMsg l:
                OnLeave(l);
                break;
            case EventMsg e:
                DispatchEvent((EventKind)e.Kind, e.Payload);
                break;
        }
    }

    /// <summary>The server's welcome: the player's id, the day clock, the predicted body with its camera and viewmodel, the HUD.</summary>
    void OnWelcome(WelcomeMsg w)
    {
        Ctx.MyId = w.PlayerId; _welcomed = true; _link.Enter(LinkPhase.Welcomed);
        _rozhlas.Reset(); Rozhlas.Reset(); // a new connection starts with the loudspeaker Idle until the server says otherwise
        DayNightCycle.Current?.SetServerClock(new DayClock { StartMinute = w.StartMinute, DayLengthSeconds = w.DayLengthMs / 1000.0, Frozen = w.Frozen }, w.ServerTick);
        CasingFx.Water = Water;
        Ctx.Me = new Player { Name = "Me", UseCamera = !Bot, AutoSim = false, Authoritative = false, CaptureMouse = !Bot && !Report.PerfPlay, SelfManageMouse = UiShell.Instance == null, KillY = KillY, Water = Water, Ladders = ObjectLadders.WithObjects(Shelters.FromJsonOrEmpty(Map?.BunkersJson, m => GD.PrintErr($"client: {m}; no ladders")).Ladders, Map), Bodies = _animals };
        World.AddChild(Ctx.Me);
        _viewmodel.Create();
        if (!Bot && ShotFx.Enabled) GlassFx.WarmUp(Ctx.Me.Camera); // the glass shaders compile now, not at the first crack
        if (WeaponsBot != "" || CarBot != "" || _fxDemo.Mode.StartsWith("vehicle:")) NetServer.AddArenaColliders(World);
        if (Bot) Ctx.Me.InputOverride = new MoveInput { SpeedScale = 1 };
        HudWelcome();
        OnWelcomed?.Invoke();
        GD.Print($"client: welcome, id {Ctx.MyId}, server tick {w.ServerTick}");
    }

    /// <summary>A player left: everything the client keeps per player goes.</summary>
    void OnLeave(LeaveMsg l)
    {
        Ctx.Names.Remove(l.PlayerId); Match.Remove(l.PlayerId); Ctx.Snapshots.Forget(l.PlayerId);
        Ctx.Remotes.Remove(l.PlayerId);
        Items.Forget(l.PlayerId); _binoculars.Forget(l.PlayerId); _headLamps.Forget(l.PlayerId);
        _status.Forget(l.PlayerId);
    }

    /// <summary>One reliable event, to every feature that listens, in this order (some ignore the kinds that are not theirs).</summary>
    void DispatchEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.TargetRoster) _targets.OnRoster(payload); // target dummies' names (#148)
        Sandbox.OnEvent(kind, payload); // sandbox answer (#156)
        Chat.OnEvent(kind, payload); // chat lines, the admin flag, the day clock
        _status.OnEvent(kind, payload); // statuses: blessing, curse, burning
        Items.OnEvent(kind, payload); // what the player wears, what a corpse carries, an item crumbling
        _binoculars.OnEvent(kind, payload); // the others' raised binoculars
        _headLamps.OnEvent(kind, payload); // the head lamps switched on and off
        Companions.OnEvent(kind, payload); // the Companions the player can pick and the one that is out
        _intel.OnEvent(kind, payload); // the announced events: the intel screen, the announcement
        _thieves.OnVoiceEvent(kind, payload); // a Gang member says a line (ticket 09)
        _bigfootFx.OnEvent(kind, payload); // the Bigfoot's effects and the footprints to its corpse (#264)
        _harvester.OnEvent(kind, payload); // the harvester's cut, its bales' fire (#331)
        _thieves.OnEvent(kind, payload); // the Raid vans' honk
        _doors.OnEvent(kind, payload); // the church doors, the Obecny Urad's door and the bus garage's doors
        _rozhlas.OnEvent(kind, payload); // the village loudspeaker's phase, broadcast and offset
        _mass.OnEvent(kind, payload); // the Holy Mass: chant, bells, the player's seat, the blessing
        _demon.OnEvent(kind, payload); // the demon: fireballs, the curse
        _hunts.OnEvent(kind, payload); // the Hunter's Log: answers and announcements (#336)
        _animals.OnPhoenixEvent(kind, payload); // the Phoenix's fall: a flash and the crash site's warning light
        _minerals.OnEvent(kind, payload); // the mineral nodes and the player's finds (#401)
        _effects.OnEvent(kind, payload); // the ammo, shots, launches, hits, blasts, and the street lamps and sign plates
        _match.OnEvent(kind, payload); // the match: phase, scoreboard, kills, the own spawn and death
        Loadouts.OnEvent(kind, payload);
        _streaks.OnEvent(kind, payload);
        _uav.OnEvent(kind, payload);
        Camo.OnEvent(kind, payload);
        WorldEntities.OnEvent(kind, payload);
        _perf.OnEvent(kind, payload);
        _bot?.OnEvent(kind, payload);
        EventReceived?.Invoke(kind, payload);
    }

    /// <summary>Raised for every reliable event envelope (shot, hit, kill, ... handled by later issues).</summary>
    public event Action<EventKind, byte[]>? EventReceived;

    void OnSnapshot(byte[] d)
    {
        if (Ctx.Me == null || !Ctx.Snapshots.Apply(d, out var h)) return;
        if (Ctx.LastTick != 0 && h.Tick < Ctx.LastTick) return;
        _bot?.OnSnapshot(h);
        _perf.Snapshot();
        _session.Snapshot(h.Tick);
        Report.Snapshot(h.Tick);
        Ctx.LastTick = h.Tick; _lastArrival = Time.GetTicksMsec() / 1000.0; _snaps++;
        Ctx.Clock.OnSnapshot(h.Tick, _lastArrival);
        DayNightCycle.Current?.SetServerTick(h.Tick);
        foreach (var id in Ctx.Snapshots.Removed) { Ctx.Remotes.Remove(id); if (Ctx.Cars.Remove(id)) { _flight.Remove(id); _driveClient.Remove(id); } }
        foreach (var (id, rec) in Ctx.Snapshots.States)
        {
            if (id == Ctx.MyId) { _reconcile.OnState(rec, h.Ack); continue; }
            if (rec.Type == EntityType.Projectile) { _rockets.OnState(id, rec); continue; }
            if (rec.Type == EntityType.Vehicle) { _vehicles.OnState(id, rec, h.Tick); continue; }
            if (rec.Type == EntityType.Seats) { _seats.OnRecord(rec); continue; }
            if (rec.Type == EntityType.Turret) { _guns.OnRecord(rec, h.Tick); continue; }
            if (rec.Type == EntityType.Flight) { _flight.OnState(rec, h.Tick); continue; }
            if (rec.Type == EntityType.Drive) { _driveClient.OnState(rec, h.Tick); continue; }
            if (rec.Type is not (EntityType.Player or EntityType.Target)) continue;
            Ctx.Remotes.OnRecord(id, rec, h.Tick);
        }
        _flight.Update(h.Ack); // the helicopter this player flies is predicted, and reconciled here (ADR 0006)
        _driveClient.Update(h.Ack);  // and so is the amphibious transport this player drives (ADR 0010)
        _bot?.AfterSnapshot(h);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (LinkEnded || !_welcomed || Ctx.Me == null || _server.Peer == null) return;
        _elapsed += delta;
        _rozhlasTest?.Tick();
        if (_bot != null && _elapsed >= BotSeconds && _bot.TimedOut()) return;
        if (!OwnSpawned) return; // wait for our spawn position
        _demon.BodyTick(); // the demon's Body capsule is where the server last put it, before the body is predicted
        var pt = ClientProf.Begin();
        _prediction.Replay(); // C-02: a snapshot's reconcile replays here, with the physics step (see OnSelf)
        _driveClient.ReplayReconcile(); // and so does a predicted vehicle's (ADR 0010, ADR 0014)
        pt = ClientProf.Lap(ClientProf.S.Reconcile, pt);
        var dt = (float)delta;
        PredictTick(SampleInput(dt), dt);
        SendInputs();
        var p = Ctx.Me.State.Position;
        _meMin = NVec3.Min(_meMin, p); _meMax = NVec3.Max(_meMax, p);
        ClientProf.End(ClientProf.S.Predict, pt);
    }

    /// <summary>What the player (or the bot) presses this tick, after the on-foot keys, the Scud screen and the throw key had their say, and the weapon's recoil and breath followed it.</summary>
    MoveInput SampleInput(float dt)
    {
        var mi = _bot != null ? _bot.Step() : Dead ? new MoveInput { SpeedScale = 1, Yaw = Ctx.Me!.Yaw } : Report.PerfPlay ? Report.PlayStep() : Ctx.Me!.Sample();
        if (!Bot) { _held.ReadSlotKeys(); _held.ApplyScope(); _groundItems.Tick(); _loot.Tick(dt); TickCrates(dt); _thieves.Loot.TickReturn(dt); _minerals.Tick(dt); }
        _fxDemo.Input(ref mi);
        _scud.FilterInput(ref mi); // a Scud screen's keys are the screen's: no driving, no weapon
        _thrower.FilterInput(ref mi); // G throws a grenade only where it is not the Scud screen's key (#420)
        _weapon.AimHeld = mi.Has(MoveButtons.Aim);
        if (_rider.HeadLookWanted()) mi.Buttons |= MoveButtons.HeadLook; // seated and looking out of their own eyes: the view is the head's, shown to everyone (ADR 0026)
        _aim.Tick(mi);
        return mi;
    }

    /// <summary>The care package's hold of the interact key, the loudspeaker's (the same key) and the ring that shows whichever is on.</summary>
    void TickCrates(double dt)
    {
        var use = Ctx.InteractHeld;
        _crates.Tick(use, dt);
        _rozhlas.TickHold(use, dt);
        _holdRing.Show(dt);
    }

    /// <summary>Numbers the input and predicts it: the own body, and the helicopter or the amphibious transport the player steers. The input waits in <see cref="OwnPrediction"/> for the server's ack.</summary>
    void PredictTick(MoveInput mi, float dt)
    {
        if (!_aircraftClient.AtTurret) _weapon.PredictFire(mi); // rounds from a turret are the server's alone
        var sentPitch = _aircraftClient.GunnerLook(ref mi); // at the turret the aim is sent (ground-stabilised, #160), and the player's own look is left alone
        if (!_aircraftClient.AtTurret) Ctx.Me!.Yaw = mi.Yaw;
        var ni = NetInput.From(++_seq, mi, sentPitch, (byte)(_aircraftClient.AtTurret ? _held.TurretKey() : Weapons != null ? Ctx.Hotbar.Want : 0), _aircraftClient.GunnerAim);
        // C-03: a dead body does not move on the server (its input is zero and it is skipped), so it is not predicted either; else each snapshot snapped it back
        if (Mode.BodyIsPredicted) { if (!Dead) Ctx.Me!.Simulate(ni.ToMove(_held.SpeedFor(ni.Slot)), dt); Ctx.Sound.OwnMovement(dt); } // a steered missile or a gunship: the body stands still; a turret's body moves
        _flight.Predict(ni, dt); // the pilot of a helicopter steps it with the input the server will step it with
        _driveClient.Predict(ni, dt);  // and so does the driver of an amphibious transport
        _prediction.Add(ni, Ctx.Me!.State);
    }

    /// <summary>Sends the newest three inputs (loss cover); they name the entity and world ticks they follow (the server's delta baselines).</summary>
    void SendInputs()
    {
        _server.SendInputs(Wire.EncodeInputs(_prediction.Recent(3), Ctx.Snapshots.AckTick, WorldEntities.AckTick));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Chat.HandleInput(e)) return; // T or "/": the chat box
        if (_screens.HandleInput(e)) return; // M: the tactical map, F: the Equipment screen (#268), J: the Hunter's Log (#336), N: the Mineral Log
        if (Sandbox.HandleInput(e)) return; // F4, the sandbox panel (#156)
        if (_volumes.HandleInput(e)) return; // F3, the bounding-volume overlay; [ ] step its mode (#479)
        if (_scud.HandleInput(e)) return; // the Scud screen's keys (open, confirm, abort, lock, the wheel's zoom)
        if (_seats.HandleInput(e)) return; // a number key while seated takes that seat
        if (_rider.HandleInput(e)) return; // the vehicle view key while driving: chase camera or cockpit
        if (_binoculars.HandleInput(e)) return; // B: the binoculars from the bag
        _held.HandleInput(e);
    }

}
