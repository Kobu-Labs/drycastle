using Godot;
using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Authoritative 30 Hz server over raw ENet (see ADR 0002): ch0 reliable messages, ch1 unreliable-sequenced input and snapshots.
/// Each tick it polls the network, runs every player's next queued input through the shared movement step and
/// collision (<see cref="Player.Simulate"/>), then sends each client a delta snapshot with the last processed input seq.
/// </summary>
public partial class NetServer : Node
{
    sealed class Client
    {
        /// <summary>The network peer; null for a bot (#214), which has no connection: nothing is sent to it and its inputs come from <see cref="Brain"/>.</summary>
        public ENetPacketPeer? Peer;
        /// <summary>The mind of a bot; null for a human player.</summary>
        public BotBrain? Brain;
        public bool IsBot => Brain != null;
        /// <summary>Sequence number of the bot's last input, and whether it was alive last tick (a respawn resets the brain).</summary>
        public ushort BotSeq;
        public bool BotWasAlive;
        /// <summary>This bot carries the RPG as its secondary weapon (#348); decided when it joins.</summary>
        public bool BotLauncher;
        public ushort Id;
        public string Name = "";
        /// <summary>Where this connection is: connected (hello awaited), rejected, loading its profile, joined. <see cref="Joined"/> and <see cref="Loading"/> follow it.</summary>
        public readonly StateMachine<SessionPhase> Session = new(ConnectionPhases.Session);
        public SessionPhase Phase => Session.Current;
        public bool Joined => Session.Is(SessionPhase.Joined);
        /// <summary>The client asked for sandbox mode in its hello (#156).</summary>
        public bool SandboxRequested;
        /// <summary>Sandbox mode granted (the server allows it and the client asked). See <see cref="SandboxRules"/>.</summary>
        public bool Sandbox;
        public Player Body = null!;
        /// <summary>Recoil of this player's trigger hand; the server fires hitscan shots with its offset.</summary>
        public RecoilState Recoil = new(0);
        /// <summary>Sniper breath hold of this player; the server adds its per-shot aim error to hitscan shots.</summary>
        public BreathState Breath = new(0);
        /// <summary>The zero this player dialled on each weapon with bullet drop (<see cref="BulletDrop"/>); a death resets it.</summary>
        public readonly ZeroDial Zero = new();
        /// <summary>The player holds the binoculars at the face (<see cref="Binoculars"/>): what the others are told, nothing the server acts on.</summary>
        public bool BinocularsUp;
        public readonly Queue<NetInput> Inputs = new();
        public ushort Ack;
        public bool HasAck;
        public ushort LastSeq;
        public bool HasSeq;
        public byte Slot;
        public PlayerWeapons W = null!;
        /// <summary>This player's Lock (Lock-on launcher, <c>NetServer.LockOn.cs</c>) and what of it was last sent to them (phase, Aircraft, tracking).</summary>
        public readonly LockOn Lock = new();
        public Change<(LockPhase, ushort, bool)> LockSent;
        /// <summary>What the player wears (items.json): set at join, taken off onto the corpse at death, never stored in the profile.</summary>
        public Equipment Worn = null!;
        /// <summary>The blessing and the curse the player carries (Holy Mass reward); ends at death, survives a round change, never stored.</summary>
        public readonly PlayerStatuses Status = new();
        /// <summary>Whether the last <see cref="EventKind.StatusState"/> said this player burns.</summary>
        public bool BurningSent;
        /// <summary>What the player carries unworn: set at join, emptied onto the corpse at death, never stored in the profile (ADR 0018).</summary>
        public Bag Bag = null!;
        /// <summary>The player's perk values. Neutral until the loadout ticket sets them from the player's loadout.</summary>
        public PerkModifiers Perks = PerkModifiers.None;
        /// <summary>XP granted to this player since the round began (the end-of-round summary).</summary>
        public readonly List<XpGrant> RoundXp = new();
        /// <summary>Entity snapshots for this client, against the states the server kept (set in Join).</summary>
        public SnapshotEncoder Enc = null!;
        /// <summary>The newest snapshot tick this client has committed (from its inputs), the baseline for its deltas; NoTick until then.</summary>
        public uint SnapAck = Wire.NoTick;
        /// <summary>World entities for this client, against the world states the server kept (set in Join).</summary>
        public WorldSnapshotEncoder WEnc = null!;
        /// <summary>The newest world tick this client has committed (from its inputs), the baseline for its world deltas; NoTick until then.</summary>
        public uint WAck = Wire.NoTick;
        public Vector3 Start;
        public float MaxMoved;
        /// <summary>Lag compensation (ADR 0013): this player's poses at the end of recent ticks, and the tick the current life (since the last teleport) began.</summary>
        public readonly PoseRing Poses = new();
        public uint LifeStart;
        public int TeleportsSeen;
        public ServerCar? Car;
        /// <summary>The harvester event's driver (#331): a seated bot the AI steers (<c>NetServer.Harvester.cs</c>); the bot roster neither counts nor feeds it.</summary>
        public bool HarvesterDriver;
        /// <summary>Seat of <see cref="Car"/> this player sits in (0 = driver).</summary>
        public int CarSeat;
        /// <summary>A passive rider's look against the hull (<see cref="RiderPose"/>): the turn and the pitch they last sent with the head-look bit, 0 and 0 with it clear. Held yaw is the hull's now plus the turn, so the figure keeps its look as the vehicle turns.</summary>
        public float HeadOff, HeadPitch;
        /// <summary>The flak status last sent to this client as the gunner (magazine, reserve, reloading), so only a change is sent; null until the first.</summary>
        public (int Mag, int Reserve, bool Reloading)? FlakSent;
        public bool LightsHeld, Handbrake;
        /// <summary>Held-key edges of Use and Killstreak. Fed every input (also while dead), so a key held across a respawn is not a fresh press.</summary>
        public Edge UseKey, StreakKey;
        /// <summary>The interact key beside a Scud truck with an empty rack and a loaded rocket cart: a tap gets in or out, a hold loads the rocket (NetServer.Scud.cs).</summary>
        public readonly ScudReloadKey ReloadKey = new();
        /// <summary>The interact key's hold at the rozhlas dictation table (NetServer.RozhlasSet.cs).</summary>
        public InteractHold SetHold;
        /// <summary>The high of the plantation (NetServer.Plantation.cs) and the interact key's hold inside one; the replicated bit is <see cref="StatusKind.High"/> of <see cref="Status"/>.</summary>
        public readonly PlayerHigh High = new();
        public InteractHold PlantHold;
        /// <summary>The use key's hold on lying Loot (NetServer.Thieves.Loot.cs) and the health it was last seen at (a drop is a hit taken).</summary>
        public LootReturnHold LootHold;
        public float LootHealth;
        /// <summary>The Bigfoot holds this player (issue #264) through this tick: their input is read and acknowledged but their body stays where it puts them.</summary>
        public uint HeldUntilTick;
        /// <summary>Until this server time a launched player's fall damage is the Bigfoot's own (the brain hurts them 2 to 5 on landing).</summary>
        public double LaunchGraceUntil;
        /// <summary>Server time of the Bigfoot's launch: until it lands (<see cref="LaunchedAt"/> plus a moment, then on the ground) the player cannot fire (#360).</summary>
        public double LaunchedAt;
        public float Throttle, Steer;
        public PlayerProfile? Profile;
        public ProfileLoad? Loading;
        /// <summary>Server time of the connect, for the hello deadline.</summary>
        public double ConnectedAt;
        public CatchUpBudget CatchUp;
        /// <summary>Ticks a living on-foot player was simulated (one or two inputs) and ticks it had no input queued, so its body stood still (A-08; loss-test telemetry).</summary>
        public int StepTicks, StarvedTicks;
        /// <summary>The profile token from the hello ("" = guest). One live session per token.</summary>
        public string Token = "";
        public int PacketsThisTick;
        public double SaveTokens = 10;
        /// <summary>What this player may send to the chat: a short burst, then about a line a second.</summary>
        public readonly ChatRateLimiter Chat = new();
        public long BytesOut;
    }

    public Node3D World = null!;

    WorldQuery? _query;
    /// <summary>The one answer to what is under a point, in the way of a mover, or in the way of a shot (ADR 0028). Built on first use: <see cref="World"/> is set by then.</summary>
    WorldQuery Query => _query ??= new WorldQuery(World, NearestHull, ParkedHullDistance);

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
    /// <summary>weapons.json (hotbar, ammo, damage numbers).</summary>
    public WeaponsFile Weapons = null!;
    /// <summary>Weapons smoke mode: builds a flat test arena with a wall far from the map and judges the scenario (implies debug commands).</summary>
    public bool WeaponsSmoke;
    public float KillY = float.NegativeInfinity;
    /// <summary>The map's water (water.json); players swim at the surface in it, and boats float on it.</summary>
    public Drycastle.ShooterCore.Water Water = Drycastle.ShooterCore.Water.Empty;
    public int Port = 27015;
    /// <summary>Network impairment for the loss and latency test (--net-sim); null = none.</summary>
    public NetSim? Sim;
    /// <summary>Quit after this many seconds and report whether two players were seen moving (smoke test); 0 = run forever.</summary>
    public double QuitAfter;
    /// <summary>Progression store (ADR 0003); null = every player is a guest. Set by the host, which owns and disposes it.</summary>
    public IProgressionStore? Progression;
    /// <summary>perks.json: the perk of each loadout and its modifiers.</summary>
    public PerksFile Perks = null!;
    /// <summary>levels.json: the XP curve and awards.</summary>
    public LevelsFile Levels = null!;
    MatchXp _xp = null!;

    readonly ENetConnection _host = new();
    readonly Dictionary<ENetPacketPeer, Client> _clients = new();
    uint _tick;
    readonly PlayerIds _ids = new();
    double _elapsed;
    int _peakPlayers;
    readonly Dictionary<string, float> _movedByName = new();
    readonly List<NetInput> _scratch = new();
    readonly Dictionary<ushort, Client> _byId = new();
    /// <summary>The entity states of the recent ticks, shared by every client's encoder (the baselines of their deltas).</summary>
    readonly SnapshotHistory _sentStates = new();
    /// <summary>The world states of recent ticks the server sent (the world channel's baselines, shared by every client).</summary>
    readonly WorldHistory _sentWorld = new();
    MatchSim _match = null!;
    // match smoke bookkeeping
    int _kills, _roundsEnded, _restarts, _ignoredHits, _earlyProtectionEnds;
    double _finishAt;
    readonly Random _rng = new();
    // weapons smoke bookkeeping
    int _shotsFired, _hitsLanded, _headshots, _wallBlocked, _backstabs, _refills;
    // rockets: server-owned projectile entities, ids from 0x8000 up (player ids count up from 1)
    /// <summary>A rocket in flight. Speed and Life are per launch when set (a gunship's, killstreaks.json); zero keeps the weapon's own.</summary>
    /// <summary>FromCar is the vehicle its owner fired it from, in an armed seat (null on foot): while the owner still rides it, the rocket passes its hull and spares its riders.</summary>
    sealed class Rocket { public ushort Id, Owner; public byte Weapon; public Vector3 Pos, Dir; public float Age; public SplashDef? Blast; public float Speed, Life; public ServerCar? FromCar; public int ViewBack = -1; public uint ViewLife; }
    readonly List<Rocket> _rockets = new();
    ushort _nextRocket = 0x8000;
    public bool RpgSmoke;
    /// <summary>Car smoke: flat arena, three cars at fixed spots, debug commands, judges the vehicle scenario.</summary>
    public bool CarSmoke;
    /// <summary>Blast smoke (ticket #103): flat arena with three cars (two side by side, one far), judges vehicle damage, the blast and the chain reaction.</summary>
    public bool BlastSmoke;
    bool ArenaMode => WeaponsSmoke || RpgSmoke || CarSmoke || BlastSmoke;
    int _rocketsFired, _explosions, _directHits, _splashHits, _selfHits, _worldExplosions, _expiredRockets, _knocked;
    public const float ArenaX = 20000f, ArenaZ = 20000f;

    public override void _Ready()
    {
        PerfInit();
        // one peer slot more than the players allowed: the connection past the limit is seen and told the server is full (T3-020)
        var err = _host.CreateHostBound("*", Port, MaxPeers + 1, Wire.Channels, 0, 0);
        if (err != Error.Ok)
        {
            GD.PrintErr($"server: cannot listen on port {Port}: {err}");
            GetTree().Quit(2);
            return;
        }
        if (ArenaMode) BuildArena();
        _xp = new MatchXp(Levels.Awards);
        _match = new MatchSim(MatchConfig, () => _elapsed, Spawns);
        _match.Event += OnMatchEvent;
        StatusInit();
        DemonInit();
        _match.Entities.Log = s => GD.PrintErr($"server: {s}");
        _match.Entities.Spawned += w => BroadcastEvent(EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State));
        _match.Entities.Despawned += w => BroadcastEvent(EventKind.WorldDespawn, WorldCodec.EncodeDespawn(w.Id, w.Reason));
        CorpseRules.Register(WorldEntityRegistry.Shared);
        GroundItemRules.Register(WorldEntityRegistry.Shared);
        TurretRules.Register(WorldEntityRegistry.Shared);
        CitizenRules.Register(WorldEntityRegistry.Shared);
        _corpses = new CorpseBook(_match.Entities);
        InitBigfoot(); // after the corpse book, which follows the same despawns
        InitPets();
        InitWildlife();
        InitThieves();
        InitHarvester();
        InitScud();
        InitEvents(); // after the kinds above: the handler table names their start, end and cancel
        InitAircraft();
        BuildCarePackages();
        BuildStreaks(); // after the match and the crate book: the care package streak needs both; the turret streak spawns into the match's entities
        WarmSurfaceIndexes(); // roads.json and the tree grid, so no impact pays for them on a tick (T3-003, T3-004)
        if (Vehicles == null) Vehicles = new VehiclesFile { Vehicles = { new VehicleDef { Id = "car", TopSpeed = 44.4f, Acceleration = 3.5f, Brake = 9.5f, TurnRadius = 5.5f, MaxLateralAccel = 8, Mass = 1200 } } };
        if (Weapons != null) foreach (var v in Vehicles.Vehicles) if (v.Flak is { } fk) FlakWeapon(fk); // a flak naming an unknown weapon fails here, not on the first shot
        GD.Print($"server: {Spawns.Count} spawns, y {(Spawns.Count > 0 ? Spawns.Min(v => v.Y) : 0)}..{(Spawns.Count > 0 ? Spawns.Max(v => v.Y) : 0)}, killY {KillY}");
        GD.Print($"SHOOTER_NET_LISTENING port={Port} tick={Wire.TickRate}Hz");
    }

    public override void _ExitTree() { _perf?.Flush(); _host.Destroy(); _nav?.Free(); }

    public override void _Process(double delta)
    {
        if (Sim != null && Sim.Pump()) _host.Flush(); // delayed packets go out at frame rate, not only on physics ticks
        RozhlasFeedPump(); // the live rozhlas feed's due frames (a thread feeds ffmpeg; the sending is here, on the game thread)
    }

    public override void _PhysicsProcess(double delta)
    {
        var tickStartUs = Time.GetTicksUsec();
        var catchUp = CatchUpTick(tickStartUs);
        if (Sim != null && Sim.Pump()) _host.Flush();
        _tick++;
        if (!_spawnsChecked)
        {
            if (_tick >= 3) { CheckSpawnGround(); SpawnCars(); PlanTargets(); BuildBotNav(force: true); } // always: citizens and events need it too, and a bake during play times the clients out
            return; // not accepting connections until the spawn list is validated
        }
        _elapsed += delta;
        _perf?.Begin();
        PhasesBegin();
        foreach (var cl in _clients.Values) cl.PacketsThisTick = 0;
        Poll();
        PerfLap(ServerPerf.Ph.Poll);
        CompleteProfileLoads();
        CompleteSaves();
        HuntTick();
        DropSilentPeers();
        IntelTick(); // a made-up event whose time is up leaves the intel screen
        PerfLap(ServerPerf.Ph.Housekeeping);
        _audit?.Begin();
        _match.Update();
        NavDrain(); // the routes the worker finished since the last tick, applied here and nowhere else
        _audit?.Lap(0);
        BigfootPrepare(); // the players and vehicles the Bigfoot's brain looks at this tick
        GangsPrepare(); // the players the Gang members fight, and who each fights
        _audit?.Lap(1);
        _match.Entities.Tick((float)delta);
        _audit?.Lap(2);
        CitizensTick((float)delta);
        _audit?.Lap(3);
        PetsTick((float)delta);
        _audit?.Lap(4);
        WildlifeTick((float)delta);
        _audit?.Lap(5);
        MassTick((float)delta); // the Holy Mass: its clock, the pastor, the seats
        _audit?.Lap(6);
        ThievesTick((float)delta); // the Thieves event: its phase clock
        _audit?.Lap(7);
        RozhlasTick((float)delta); // the village loudspeaker: the schedule and the hijack clock
        _audit?.Lap(8);
        PerfLap(ServerPerf.Ph.Match);
        StreakTick((float)delta);
        PerfLap(ServerPerf.Ph.Streaks);
        BotsTick((float)delta); // fills the roster and queues each bot's input for this tick (#214)
        HarvesterTick((float)delta); // the harvester event: its spawn, its driver's pedals
        ScudEventTick((float)delta); // the Scud event: who boarded the truck, the search time, the bunkers' carts
        PerfLap(ServerPerf.Ph.Bots);
        foreach (var car in _cars) car.PlaceSolid(); // the boxes on-foot players are stopped by, where the vehicles stand after the last tick (#217)
        PlaceVanSolids(); // and the Raid vans' (a van that stands is solid)
        PerfLap(ServerPerf.Ph.PlaceSolids);
        _simClients.Clear(); _simClients.AddRange(_byId.Values); // a copy: a client may leave while the others are simulated
        foreach (var c in _simClients) SimulateClient(c, (float)delta); // every joined player, bots included
        StepLocks((float)delta); // the Lock-on launchers: each Lock steps where its player now looks (ADR 0031)
        PerfLap(ServerPerf.Ph.SimClients);
        StepRockets((float)delta);
        PerfLap(ServerPerf.Ph.Rockets);
        StepCorpses((float)delta);
        PerfLap(ServerPerf.Ph.Corpses);
        StepScud((float)delta); // the Scud trucks' launchers: the crew's lock, the rack, the brakes (before the vehicles step)
        StepVehicles((float)delta);
        StepRidingCorpses((float)delta); // the dead pilots and drivers move with the vehicles that just moved
        HarvesterAfterVehicles((float)delta); // what the harvester ran over, the corpses it draws in, the bales' fire
        PerfLap(ServerPerf.Ph.Vehicles);
        StatusTick((float)delta); // burns tick
        PlantationTick((float)delta); // highs run out
        DetonateShells();  // shells whose tracer has arrived burst now (#162)
        DetonateFlak();    // flak shells that have arrived burst now
        DetonateHoming();  // homing missiles that have arrived hit their Aircraft now
        FlushGunshipFx(); // the gunship rounds of this tick, coalesced (#159)
        FlushGlass();     // the glass panes that changed this tick (ADR 0007)
        FlushStatuses(); // statuses and burning that changed this tick
        FlushScoreboard(); // once a tick at most, and about once a second (the ping), however many kills the tick had (A-04)
        PerfLap(ServerPerf.Ph.Detonate);
        if (CarSmoke && _carExits >= 1 && _finishAt == 0) _finishAt = _elapsed + 5.0; // after the bot's check that the empty car stays put
        CaptureLagPoses(); // the poses this tick ends with, the state the snapshots carry (ADR 0013)
        CaptureVehiclePoses(); // and the vehicles' (B-02)
        PerfLap(ServerPerf.Ph.LagCapture);
        if (catchUp && LossTest == 0) _skippedSnapshots++; // the next regular tick sends the current state
        else SendSnapshots();
        PerfLap(ServerPerf.Ph.Snapshots);
        TickTelemetry(tickStartUs, delta);
        LossTickEnd(tickStartUs);
        PerfLap(ServerPerf.Ph.Telemetry);
        PerfEnd();
        if (_clients.Count > _peakPlayers) _peakPlayers = _clients.Count;
        if (QuitAfter > 0) foreach (var c in _clients.Values) if (c.Joined) _movedByName[c.Name] = c.MaxMoved; // only the smoke verdict reads it; a production server keeps no per-name history
        if (_tick % (Wire.TickRate * 2) == 0 && _clients.Count > 0)
            GD.Print($"server: tick {_tick}, players {string.Join(", ", _byId.Values.Select(c => $"{c.Name}#{c.Id}@({c.Body.GlobalPosition.X:F1},{c.Body.GlobalPosition.Y:F1},{c.Body.GlobalPosition.Z:F1}) ack={c.Ack}"))}");
        if (RpgSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt || QuitAfter > 0 && _elapsed >= QuitAfter) FinishRpg();
        }
        else if (BlastSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt || QuitAfter > 0 && _elapsed >= QuitAfter) FinishBlast();
        }
        else if (CarSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt || QuitAfter > 0 && _elapsed >= QuitAfter) FinishCar();
        }
        else if (WeaponsSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt || QuitAfter > 0 && _elapsed >= QuitAfter) FinishWeapons();
        }
        else if (MatchSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt) FinishMatch();
            else if (QuitAfter > 0 && _elapsed >= QuitAfter) FinishMatch();
        }
        else if (LossTest > 0) { if (QuitAfter > 0 && _elapsed >= QuitAfter) LossEnd(false); }
        else if (QuitAfter > 0 && _elapsed >= QuitAfter) Finish();
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

    /// <summary>Flat floor (top at y 0) and a 14 x 4 m wall in the middle of nowhere, so the weapons smoke does not depend on the map layout.</summary>
    void BuildArena()
    {
        KillY = -1000f; // the arena lies far below the map spawns the host derived killY from
        AddArenaColliders(World);
        Spawns = new List<System.Numerics.Vector3>
        {
            new(ArenaX - 3, 0, ArenaZ + 5), new(ArenaX + 3, 0, ArenaZ + 5), new(ArenaX - 3, 0, ArenaZ + 9), new(ArenaX + 3, 0, ArenaZ + 9),
        };
    }

    /// <summary>The arena geometry; the weapons smoke clients build the same so their prediction stands on it too.</summary>
    public static void AddArenaColliders(Node3D world)
    {
        void Box(string name, Vector3 center, Vector3 size)
        {
            var b = new StaticBody3D { Name = name, Position = center };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            world.AddChild(b);
        }
        Box("ArenaFloor", new Vector3(ArenaX, -1, ArenaZ), new Vector3(400, 2, 400));
        Box("ArenaWall", new Vector3(ArenaX + 20, 2, ArenaZ - 3), new Vector3(14, 4, 0.5f));
    }

    void FinishRpg()
    {
        var ok = _rocketsFired >= 3 && _explosions >= 3 && _directHits >= 1 && _splashHits >= 1 && _selfHits >= 1 && _worldExplosions >= 2 && _kills >= 1 && _refills >= 1 && _knocked >= 1;
        GD.Print($"server: rpg smoke rockets={_rocketsFired} explosions={_explosions} direct={_directHits} splash={_splashHits} self={_selfHits} world={_worldExplosions} expired={_expiredRockets} knocked={_knocked} kills={_kills} refills={_refills}");
        GD.Print(ok ? "SHOOTER_RPG_SERVER_OK" : "SHOOTER_RPG_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }

    void FinishWeapons()
    {
        var ok = _kills >= 2 && _hitsLanded >= 5 && _headshots >= 1 && _wallBlocked >= 1 && _backstabs >= 1 && _refills >= 1;
        GD.Print($"server: weapons smoke shots={_shotsFired} hits={_hitsLanded} headshots={_headshots} backstabs={_backstabs} wallBlocked={_wallBlocked} kills={_kills} refills={_refills}");
        GD.Print(ok ? "SHOOTER_WEAPONS_SERVER_OK" : "SHOOTER_WEAPONS_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }

    void FinishMatch()
    {
        var ok = _kills >= MatchConfig.ScoreGoal && _roundsEnded == 1 && _restarts == 1 && _ignoredHits >= 2 && _earlyProtectionEnds >= 1
                 && _match.Phase == MatchPhase.Running && _match.Players.All(p => p.Kills == 0 && p.Deaths == 0);
        GD.Print($"server: match smoke kills={_kills} roundsEnded={_roundsEnded} restarts={_restarts} ignoredHits(protection)={_ignoredHits} earlyProtectionEnds={_earlyProtectionEnds} phase={_match.Phase}");
        GD.Print(ok ? "SHOOTER_MATCH_SERVER_OK" : "SHOOTER_MATCH_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
    }

    void Finish()
    {
        var movedOk = _movedByName.Count(kv => kv.Value >= 2f);
        var ok = _peakPlayers >= 2 && movedOk >= 2;
        GD.Print($"server: peak players {_peakPlayers}, players that moved >= 2 m: {movedOk} ({string.Join(", ", _movedByName.Select(kv => $"{kv.Key}={kv.Value:F1}m"))})");
        GD.Print(ok ? "SHOOTER_NET_SERVER_OK" : "SHOOTER_NET_SERVER_FAIL");
        GetTree().Quit(ok ? 0 : 2);
        SetPhysicsProcess(false);
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
                    if (_clients.Count >= MaxPeers) Reject(new Client { Peer = peer }, $"The server is full ({MaxPeers} players). Try again later.");
                    else _clients[peer] = new Client { Peer = peer, ConnectedAt = _elapsed };
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
        if (d[0] < Wire.TInput) // message type tells the channel apart (Godot does not expose it per packet)
        {
            var m = Wire.DecodeReliable(d);
            if (m is HelloMsg h && c.Phase == SessionPhase.Connected) OnHello(c, h);
            else if (m is EventMsg { Kind: (byte)EventKind.Debug } e && c.Joined && (DebugCommands || ArenaMode)) OnDebug(c, e.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpse } pu) OnPickUpCorpse(c, pu.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpseItem } pi) OnPickUpCorpseItem(c, pi.Payload);
            else if (m is EventMsg { Kind: >= (byte)EventKind.EquipItem and <= (byte)EventKind.TakeGroundItem } ir) OnItemRequest(c, (EventKind)ir.Kind, ir.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SetZero } sz) { if (c.Joined && BulletDrop.TryDecodeZero(sz.Payload, out var zi, out var zm) && zi < Weapons.Weapons.Count && BulletDrop.HasDrop(Weapons.Weapons[zi])) c.Zero.Set(zi, Weapons.Weapons[zi], zm); }
            else if (m is EventMsg { Kind: (byte)EventKind.RaiseBinoculars } rb) { if (c.Joined && Binoculars.TryDecodeRaise(rb.Payload, out var up)) SetBinoculars(c, up && CarriesBinoculars(c)); }
            else if (m is EventMsg { Kind: (byte)EventKind.Redeploy } rd && c.Joined) OnRedeploy(c, rd.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.OpenCarePackage } oc) OnOpenCarePackage(c, oc.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SandboxGrant } sg && c.Joined) OnSandboxGrant(c, sg.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SandboxAction } sa && c.Joined) OnSandboxAction(c, sa.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SandboxTeleportTo } st && c.Joined) OnSandboxTeleportTo(c, st.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SeatSwitch } sw && c.Joined) OnSeatSwitch(c, sw.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.ScudCommand } sc && c.Joined) OnScudCommand(c, sc.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.TeamAssign } ta && c.Joined) OnTeamAssign(c, ta.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.HuntsRequest or (byte)EventKind.RecordsRequest or (byte)EventKind.PlayerLogRequest } hq && c.Joined) OnHuntRequest(c, hq);
            else if (m is EventMsg { Kind: (byte)EventKind.ChatSend } cs) OnChatSend(c, cs.Payload);
            else if (c.Joined) OnProfileMessage(c, m);
        }
        else if (c.Joined && d[0] == Wire.TRozhlasMic) OnRozhlasMic(c, d); // the set holder's voice (NetServer.RozhlasSet.cs)
        else if (c.Joined && d[0] == Wire.TInput)
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
    }

    void OnHello(Client c, HelloMsg h)
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
        c.Id = _ids.Allocate(id => _clients.Values.Any(o => o.Id == id) || _byId.ContainsKey(id));
        if (c.Id == 0) { Reject(c, "The server is full."); return; }
        var name = LoadoutRules.SanitizePlayerName(h.Name);
        c.Name = name.Length == 0 ? $"player{c.Id}" : name;
        SandboxOnHello(c, h.Flags);
        c.Token = h.Token;
        DropSessionsWithToken(c); // a quick reconnect wins over its ghost, which would otherwise run on the same profile until its peer times out
        StartProfileLoad(c, h.Token); // joins once the profile is in (or the wait times out)
        GD.Print($"server: {c.Name} #{c.Id} hello, loading profile");
    }

    /// <summary>Joins a client: body, welcome, profile, the join broadcast and the spawn. Called once the client has a profile.</summary>
    void Join(Client c)
    {
        // health, death and respawn belong to the match state machine, not the body
        ApplyLoadout(c);
        c.Recoil = new RecoilState(c.Id);
        c.Breath = new BreathState(c.Id);
        c.Worn = new Equipment(Items);
        c.Bag = new Bag();
        ItemRules.IssueStandard(Items, c.Bag); // every player has the standard items (the binoculars) from the start
        c.Body = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, Authoritative = false, Name = $"P{c.Id}", Water = Water, Ladders = MapLadders, Bodies = _animalCrowd };
        World.AddChild(c.Body);
        c.Body.Teleport(Vector3.Zero);
        c.Session.Enter(SessionPhase.Joined);
        _byId[c.Id] = c;
        c.Enc = new SnapshotEncoder(_sentStates);
        c.WEnc = new WorldSnapshotEncoder(history: _sentWorld);
        SendProfile(c);
        SendSandboxStatus(c); // the answer to a sandbox request (#156), before the spawn
        SendChatStatus(c); // admin or not, and the vehicles /spawn takes
        Send(c, new WelcomeMsg(Wire.ProtocolVersion, c.Id, Wire.TickRate, _tick,
            (ushort)(Clock.StartMinute), (uint)Math.Round(Clock.DayLengthSeconds * 1000), Clock.Frozen));
        foreach (var o in _byId.Values)
        {
            Send(c, new JoinMsg(o.Id, o.Name));
            if (o != c) Send(o, new JoinMsg(c.Id, c.Name));
        }
        foreach (var w in _match.Entities.Snapshot()) Send(c, new EventMsg((byte)EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State)));
        SendCorpseItemsOnJoin(c); // the items the corpses carry, after the corpses themselves
        SendGlassOnJoin(c); // the glass panes that are not whole (ADR 0007)
        SendChurchOnJoin(c); // the church doors, when open (ADR 0019)
        SendIntelOnJoin(c); // the events on the intel screen
        SendMassOnJoin(c); // the Holy Mass, when one runs
        SendThievesOnJoin(c); // the Thieves event, when one runs
        SendRozhlasOnJoin(c); // the village loudspeaker, when a detour broadcast is on
        SendUradDoorOnJoin(c); // and the Urad door, when it is open
        SendGarageDoorOnJoin(c); // and the garage doors, when they are open
        SendHarvesterOnJoin(c); // the harvester's job and the bales that burn
        SendScudOnJoin(c); // the Scud trucks' racks and proposals
        SendPounceOnJoin(c); // and the demon's pounce, when one is on
        SendTargetRoster(c); // target dummies' names (#148)
        SendAppearancesOnJoin(c); // camouflage (#140): every player's look reaches the joiner, the joiner's reaches everyone
        SendWornOnJoin(c); // what the others wear (#266), to draw on their characters
        SendBinocularsOnJoin(c); // and who holds the binoculars up
        SendStatusOnJoin(c); // who is blessed, cursed or burning
        _match.AddPlayer(c.Id, c.Name, c.Sandbox, c.IsBot); // spawns the player (event) and broadcasts the scoreboard
        SandboxAnnounce(c); // sandbox name tags both ways, and the held streak (#156)
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
    void Leave(Client c)
    {
        StreakLeave(c.Id);
        StatusLeave(c.Id);
        RozhlasSetLeave(c.Id);
        MassStandUp(c.Id); _seatHint.Remove(c.Id);
        DetachFromCar(c);
        c.Body.QueueFree();
        _byId.Remove(c.Id);
        foreach (var o in _byId.Values) Send(o, new LeaveMsg(c.Id));
        _match.RemovePlayer(c.Id);
        GD.Print($"server: {c.Name} #{c.Id} left");
    }

    /// <summary>Drops every other client that uses <paramref name="keep"/>'s token, so a profile never has two live sessions.</summary>
    void DropSessionsWithToken(Client keep)
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
    void Send(Client c, ReliableMsg m)
    {
        if (c.Peer is { } peer) Tx(peer, Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>Broadcast an event envelope to every joined client (shot, hit, kill, ... from later issues).</summary>
    public void BroadcastEvent(EventKind kind, byte[] payload)
    {
        ReliableMsg m = new EventMsg((byte)kind, payload);
        PerfEventSent(kind, payload);
        RozhlasCombatEvent(kind, payload); // shots, launches and blasts inside the Urad go to the rozhlas live feed while the mic is live
        var bytes = Wire.Encode(m); // encoded once: every recipient gets the same bytes (Tx copies them into the packet)
        // effects that may be lost go unreliable on their own channel, so they never hold back the reliable events (A-03)
        var effect = Wire.IsEffect(kind);
        foreach (var c in _clients.Values)
            if (c.Joined) Tx(c.Peer!, effect ? Wire.ChanEffects : Wire.ChanReliable, bytes, effect ? 0 : (int)ENetPacketPeer.FlagReliable);
    }

    void OnDebug(Client c, byte[] payload)
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
            var car = _cars.FirstOrDefault(x => x.Id == vd.Vehicle);
            if (car != null) DamageVehicle(car, vd.Amount, c.Id, SpecialWeapons.Debug, 0);
            return;
        }
        if (MatchCodec.DecodeDebugKill(payload) is not { } target) return;
        var r = _match.ApplyDamage(target, c.Id, SpecialWeapons.Debug, 9999);
        if (r == DamageResult.Ignored && _match.IsProtected(target)) _ignoredHits++;
        GD.Print($"server: debug kill {c.Name} -> #{target}: {r}");
    }

    /// <summary>
    /// What a round leaves behind on the server, cleared when the next one starts and when the match drops back to waiting: the Holy Mass, the crowd, the
    /// vehicles (riders out of their seats, everything back at its origin, repaired), burning, killstreaks, shells in the air and the round's XP.
    /// </summary>
    void ResetRoundWorld()
    {
        _mass?.End(); // the Holy Mass does not outlive the round
        _crowd?.Clear(); // the match cleared the entities; those still waiting to leave go too
        PetsRoundStart();
        WildlifeRoundStart(); // the match cleared the pets' entities too; a new round moves new ones into the property groups
        WorkersAfterRoundReset(); // the Urad workers of a detour still open sit down again
        ResetCars();
        HarvesterRoundReset(); // the car reset brought the harvester back to its old field: hidden again
        ScudRoundReset(); // the launchers start over; a Scud truck, which never returns, is retired
        StatusRoundStart();
        LockRoundReset();
        RoundStarted();
    }

    void OnMatchEvent(MatchEvent e)
    {
        switch (e)
        {
            case ScoreboardChanged: _scoreDirty = true; break; // sent once the tick's events are in (FlushScoreboard)
            case PhaseChanged { Phase: MatchPhase.Ending }:
                _roundsEnded++;
                RoundEnded();
                GD.Print($"server: round over, winners {string.Join(",", _match.Winners)}");
                break;
            case PhaseChanged { Phase: MatchPhase.Waiting }: // too few players: the round is over without an end screen, nothing of it stays (the match cleared its entities)
                ResetRoundWorld();
                GD.Print("server: match waiting for players");
                break;
            case PhaseChanged { Phase: MatchPhase.Running }: // a new round: also an admin's restart of a running one
                ResetRoundWorld();
                LossRoundStart();
                if (_roundsEnded > 0) { _restarts++; _finishAt = _elapsed + 1.5; }
                GD.Print("server: round started");
                break;
            case PlayerSpawned s:
                if (_byId.TryGetValue(s.Id, out var sc))
                {
                    sc.Body.Teleport(new Vector3(s.Position.X, s.Position.Y + 0.1f, s.Position.Z));
                    sc.Body.Health = MoveConsts.MaxHealth;
                    sc.Inputs.Clear(); sc.Start = sc.Body.GlobalPosition; sc.MaxMoved = 0;
                    sc.HeldUntilTick = 0; sc.LaunchGraceUntil = 0; sc.LaunchedAt = 0; // a new life: no Bigfoot hold or launch grace left over from the last one
                    ApplyLoadout(sc); // a loadout changed while alive applies from this spawn on
                    sc.W.Reset(_elapsed);
                    sc.Recoil.Reset();
                    sc.Breath.Reset();
                    sc.Zero.Reset();
                    SetBinoculars(sc, false); // a new life starts with them down
                    ItemRules.IssueStandard(Items, sc.Bag); // and with the standard items (the binoculars) in the bag
                    SendEquipmentIfChanged(sc); // also what was issued at the join, before the first life
                    SendAmmo(sc);
                }
                BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(s.Id, s.Position, s.ProtectionSeconds)));
                break;
            case PlayerKilled k:
                HarvesterOnKill(k); // before the corpse: a body the harvester killed is not thrown
                if (_byId.TryGetValue(k.Victim, out var kv) && kv.Car != null) RiderDies(kv); // frees the seat; the corpse (PlayerDied) stays in a pilot's or driver's seat, else falls (ADR 0026)
                _kills += k.Killer != k.Victim ? 1 : 0;
                BotKillLog(k);
                if (WeaponsSmoke && _kills >= 2 && _finishAt == 0) _finishAt = _elapsed + 1.5;
                if (RpgSmoke && _kills >= 1 && _finishAt == 0) _finishAt = _elapsed + 1.5;
                LossKill(k.Killer, k.Victim);
                RecordKill(k);
                var fromStreak = StreakKill(k);
                if (Scores(k.Killer) && Scores(k.Victim)) AwardXp(_xp.OnKill(new KillFacts(k.Killer, k.Victim, k.Cause, fromStreak), _elapsed)); // the death of a bot (#214) or of a sandbox player grants no XP
                BroadcastEvent(EventKind.Kill, MatchCodec.Encode(new KillMsg(k.Killer, k.Victim, k.Weapon, k.Cause)));
                break;
            case PlayerTeamChanged tc: OnTeamChanged(tc); break;
            case TargetKilled tk: OnTargetKilled(tk); break;
            case PlayerDied d: OnStatusDeath(d.Victim); RozhlasSetDeath(d.Victim); RozhlasCombatDeath(d.Victim); LossDeath(d.Victim); RecordDeath(d.Victim); StreakDeath(d.Victim); SpawnCorpse(d.Victim); BroadcastEvent(EventKind.Death, MatchCodec.Encode(new DeathMsg(d.Victim, d.Killer, d.RespawnDelay))); break;
        }
    }

    /// <summary>Set by a scoreboard change; cleared by the flush that sends the table.</summary>
    bool _scoreDirty;

    /// <summary>
    /// Sends the scoreboard when it changed this tick, and about once a second regardless (the time left and the pings move). A kill
    /// used to resend the whole table at once, so a tick with three kills sent it three times (A-04).
    /// </summary>
    void FlushScoreboard()
    {
        var due = _scoreDirty || _tick % Wire.TickRate == 0;
        _scoreDirty = false;
        if (due && _clients.Count > 0) BroadcastEvent(EventKind.MatchState, MatchCodec.Encode(BuildMatchState()));
    }

    MatchStateMsg BuildMatchState()
    {
        var rows = _match.Ranking().Select(p => new ScoreRow(p.Id, p.Sandbox ? p.Name + " [SANDBOX]" : p.Name, p.Kills, p.Deaths, Ping(p.Id), p.Team, p.Sandbox, p.Bot)).ToList();
        return new MatchStateMsg(_match.Phase, _match.TimeRemaining, _match.Winners, rows, _match.Mode, _match.TeamScore(Teams.A), _match.TeamScore(Teams.B));
    }

    ushort Ping(ushort id) =>
        _byId.TryGetValue(id, out var c) && c.Peer is { } peer ? (ushort)Math.Clamp(peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime), 0, ushort.MaxValue) : (ushort)0; // a bot has no ping

    void SimulateClient(Client c, float dt)
    {
        // normally one input per tick; if the queue backs up (jitter), catch up with a second one
        var n = c.CatchUp.Steps(c.Inputs.Count); // a second input only while the catch-up credit lasts (#197)
        if (c.Inputs.Count == 0 && c.Car == null && _match.Get(c.Id)?.Life == PlayerLife.Alive) c.StarvedTicks++; // A-08: no input for this tick (the body does not move)
        for (var k = 0; k < n && c.Inputs.Count > 0; k++)
        {
            var i = c.Inputs.Dequeue();
            c.Ack = i.Seq; c.HasAck = true;
            // a killstreak's control session (a turret being placed) takes the owner's input first; while it holds it,
            // Fire and Use belong to the session, not to the weapon or the vehicles
            var controlled = _streaks.RouteInput(c.Id, i);
            if (controlled) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)(MoveButtons.Fire | MoveButtons.Use));
            if (c.Car == null && _elapsed < c.LaunchGraceUntil && (_elapsed < c.LaunchedAt + 0.3 || !c.Body.State.OnFloor)) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)MoveButtons.Fire); // thrown by the Bigfoot and in the air: no shooting (#360)
            var useDown = (i.Buttons & (ushort)MoveButtons.Use) != 0;
            var useEdge = c.UseKey.Pressed(useDown);
            RozhlasSetUse(c, useDown);
            LootUse(c, useDown);
            PlantationUse(c, useDown);
            var lightsEdge = Headlights.Edge((i.Buttons & (ushort)MoveButtons.Headlights) != 0, ref c.LightsHeld);
            var streakDown = (i.Buttons & (ushort)MoveButtons.Killstreak) != 0;
            var streakEdge = c.StreakKey.Pressed(streakDown);
            if (_match.Get(c.Id)?.Life != PlayerLife.Alive) continue; // dead or waiting: inputs are acked but ignored
            if (c.Car == null && _tick <= c.HeldUntilTick) continue; // in the Bigfoot's hand: the input is acked, the body is where the Bigfoot puts it (issue #264)
            if (c.Car == null && _sitters.TryGetValue(c.Id, out var sitter)) { SitterInput(c, sitter, i, useEdge); continue; } // seated in the church: no walking, no weapon
            if (c.Car == null && _pins.TryGetValue(c.Id, out var pinYaw)) { c.Body.Yaw = pinYaw; c.Body.Pitch = 0; continue; } // pinned by the demon: held on the floor, no walking, no weapon, no use
            if (streakEdge && !controlled) StreakActivate(c);
            if (c.Car == null && _streaks.ControlOf(c.Id) is { FreezesBody: true }) continue; // controlling a streak entity that holds the body (the missile, a gunship): the body stands still
            if (c.Car is { Thrown: not null }) continue; // the Bigfoot threw it: nobody steers, fires or gets out until it lands (issue #264)
            if (c.Car != null)
            {
                // driving: a helicopter or a predicted ground vehicle is stepped here from the driver's inputs, other cars in StepVehicles; no walking or weapon timers.
                // A gunner's inputs fire the car's gun instead (its aim is the view yaw and pitch).
                // The pilot of a helicopter steps it here, once per input, as the body on foot is: the pilot's client predicts the same steps (ADR 0006).
                if (c.CarSeat == 0 && c.Car.Flies) c.Car.StepFlight(FlightInput.FromNet(i, handsOff: controlled), dt, _tick);
                else if (c.CarSeat == 0 && c.Car.Drives) { c.Car.Wake(); c.Car.StepDrive(DriveInput.FromNet(i, handsOff: controlled), dt); c.Car.DriveTick = _tick; } // the same for a driven vehicle (ADR 0010, ADR 0014): a driver's input wakes a parked one
                else if (c.CarSeat == 0) { c.Throttle = Quant.Axis(i.MoveY); c.Steer = Quant.Axis(i.MoveX); c.Handbrake = (i.Buttons & (ushort)MoveButtons.Jump) != 0; }
                else if (c.Car.Gun != null && c.Car.Def.Seats[c.CarSeat].Role == "gunner")
                {
                    GunnerInput(c, i);
                    c.Body.Yaw = Quant.Yaw(i.Yaw); c.Body.Pitch = Quant.Pitch(i.Pitch); // a gunner's body faces where they aim, for everyone who sees them at the gun
                }
                else if (!controlled && RiderRules.Weapons(c.Car.Def, c.CarSeat, i) is { } seated) SeatWeapons(c, c.Car, i, seated); // an armed seat: the rider's own weapons (ADR 0011)
                if (!RiderPose.FollowsAim(c.Car.Def.Seats[c.CarSeat])) SeatLook(c, c.Car, i); // every other seat: the body is the vehicle's and the head looks where the rider's first-person view does (ADR 0026)
                if (lightsEdge) c.Car.Lights = Headlights.Toggle(c.Car.Lights);
                if (c.Car.Def.Launcher != null ? ScudUseKey(c, useDown, useEdge, dt) : TowUseKey(c, c.Car, useDown, useEdge, dt)) ExitCar(c); // a Scud truck: a tap leaves, a hold loads a cart's rocket (a truck does not tow); with a trailer on the hook a tap leaves and a hold unhitches (UnhitchKey); otherwise the press leaves
                continue;
            }
            var useAct = ScudUseKey(c, useDown, useEdge, dt); // beside a truck with an empty rack and a loaded cart a press waits to be a tap (it gets in) or a hold (it loads the rocket)
            if (useAct && LootInReach(c) == null && (MassSit(c) || GarageUse(c) || TryEnterCar(c))) continue; // the use key on lying Loot sends it back, it does not take a seat
            c.Body.Yaw = Quant.Yaw(i.Yaw);
            c.Body.Pitch = Quant.Pitch(i.Pitch);
            c.Body.Simulate(i.ToMove(c.W.SpeedFor(i.Slot)), dt);
            c.StepTicks++;
            c.Recoil.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Fire) != 0, c.W.Current.Recoil);
            // breath hold: sprint is cancelled while aiming, so the sprint bit with aim held is the breath key
            c.Breath.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
                (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
            var wi = new WeaponInput((i.Buttons & (ushort)MoveButtons.Fire) != 0 && !c.Body.State.OnLadder, (i.Buttons & (ushort)MoveButtons.Reload) != 0, i.Slot, (i.Buttons & (ushort)MoveButtons.Switch) != 0, HasLock(c));
            if (c.W.Update(_elapsed, wi) is { } shot)
            {
                if (_match.NotifyFired(c.Id)) _earlyProtectionEnds++; // spawn protection ends on a real shot, not on Fire held with an empty magazine
                FireWeapon(c, i, shot);
            }
            if (c.W.Changed) SendAmmo(c);
            c.Slot = (byte)(c.W.Slot - 1);
            if (c.Body.LastFallDamage > 0 && _elapsed >= c.LaunchGraceUntil) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Fall, c.Body.LastFallDamage); // a launched player lands for the Bigfoot's damage, not the fall's
            if (c.Body.GlobalPosition.Y < KillY) GD.Print($"server: {c.Name} fell out of the world at {c.Body.GlobalPosition} (killY {KillY}, spawn {_match.Get(c.Id)?.Position})");
            if (c.Body.GlobalPosition.Y < KillY) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Void, 9999, ignoreProtection: true);
        }
        _match.SetPosition(c.Id, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z));
        c.Body.Health = _match.Get(c.Id)?.Health ?? 0;
        var p = c.Body.GlobalPosition - c.Start;
        c.MaxMoved = Mathf.Max(c.MaxMoved, new Vector2(p.X, p.Z).Length());
    }

    /// <summary>
    /// One input of a rider in an armed seat (ADR 0011): what the weapon half of an input does on foot. The view is the aim, held inside the seat's
    /// arc (which turns and tilts with the vehicle), and it is where everyone sees the rider point; recoil and breath run as on foot; the weapon
    /// fires, reloads and switches by its own rules, and a shot is resolved from the seat (<see cref="FireWeapon"/>).
    /// </summary>
    void SeatWeapons(Client c, ServerCar car, NetInput i, WeaponInput input)
    {
        var (yaw, pitch, roll) = AttitudeOf(car);
        var aim = SeatAimRules.Aim(car.Def.Seats[c.CarSeat].Aim, yaw, pitch, roll, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        c.Body.Yaw = aim.ViewYaw; c.Body.Pitch = aim.ViewPitch;
        c.Recoil.Tick(_elapsed, input.Fire, c.W.Current.Recoil);
        c.Breath.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
            (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
        if (c.W.Update(_elapsed, input with { Locked = HasLock(c) }) is { } shot)
        {
            if (_match.NotifyFired(c.Id)) _earlyProtectionEnds++;
            FireWeapon(c, i, shot, car);
        }
        if (c.W.Changed) SendAmmo(c);
        c.Slot = (byte)(c.W.Slot - 1);
    }

    /// <summary>
    /// The look of a rider on a passive seat (ADR 0026): with the head-look bit set (their first-person view) the input's view is the head's, held to the neck's pitch
    /// and, for a helicopter's pilot, to the pitch alone (their yaw is the heading they steer to); with it clear the head faces ahead. The body keeps the vehicle's yaw
    /// either way and stands upright on the seat, as the hull tilts.
    /// </summary>
    void SeatLook(Client c, ServerCar car, NetInput i)
    {
        var (off, pitch) = RiderPose.Passive(c.CarSeat == 0 && car.Flies, (i.Buttons & (ushort)MoveButtons.HeadLook) != 0, AttitudeOf(car).Yaw, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        c.HeadOff = off; c.HeadPitch = pitch;
        ApplyHeadLook(c, car);
    }

    /// <summary>A passive rider's body yaw and pitch for the snapshot: the hull's yaw now, turned by their look (<see cref="Client.HeadOff"/>), at their look's pitch.</summary>
    void ApplyHeadLook(Client c, ServerCar car)
    {
        c.Body.Yaw = AttitudeOf(car).Yaw + c.HeadOff;
        c.Body.Pitch = c.HeadPitch;
    }

    void SendAmmo(Client c)
    {
        c.W.Changed = false;
        Send(c, new EventMsg((byte)EventKind.Ammo, WeaponCodec.Encode(WeaponCodec.BuildAmmo(c.W, _elapsed))));
    }

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Lists that <see cref="LiveTargets"/> handed out and their callers gave back (<see cref="ReturnTargets"/>), for the next call to reuse.</summary>
    readonly Stack<List<HitTarget>> _targetLists = new();

    /// <summary>
    /// The live targets now, in a list the caller owns until it gives it back with <see cref="ReturnTargets"/>. Each call gets its own list on purpose:
    /// callers nest (an explosion can detonate a car, which explodes again) and each holds its list while it iterates, so one shared buffer would be
    /// cleared under them. A list that is not given back only costs an allocation.
    /// </summary>
    List<HitTarget> LiveTargets()
    {
        var list = _targetLists.Count > 0 ? _targetLists.Pop() : new List<HitTarget>(_byId.Count + 4);
        foreach (var o in _byId.Values)
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive && !_pins.ContainsKey(o.Id)) list.Add(HitTargetOf(o)); // a pinned player is under the demon and untouchable: not a target, so no shot stops on them
        var dummies = _match.Targets.All; // living target dummies: an index loop, no enumerator object
        for (var i = 0; i < dummies.Count; i++)
        {
            var d = dummies[i];
            if (d.Alive) list.Add(new HitTarget(d.Id, d.Spot.Feet, d.Spot.Pose, d.Spot.Yaw));
        }
        if (BigfootTarget() is { } bigfoot) list.Add(bigfoot); // the Bigfoot is a hit target like a player (issue #264)
        _crowd?.AddTargets(list); // the citizens of the Holy Mass
        AddGangTargets(list); // the Gang members of the Thieves event
        AddVanTargets(list); // and their Raid vans
        AddPetTargets(list);
        AddWildlifeTargets(list);
        _workers?.AddTargets(list); // the workers of the Urad
        AddDemonTarget(list);
        PerfCounters.LiveTargetsCalls++; PerfCounters.LiveTargetsElems += list.Count;
        return list;
    }

    /// <summary>Gives a list from <see cref="LiveTargets"/> back, once nothing iterates it any more.</summary>
    void ReturnTargets(List<HitTarget> list)
    {
        list.Clear();
        _targetLists.Push(list);
    }

    /// <summary>
    /// A live player as a hit target. A rider in a seat that says it is exposed (the amphibious transport's, ADR 0012) is the figure every client
    /// draws there: as tall as the seat makes them (seated, or standing in the ring), and where the seat is now, since a driven vehicle moves on
    /// its driver's inputs before its riders' bodies are put back on their seats. Everyone else is their body, as before.
    /// </summary>
    HitTarget HitTargetOf(Client o)
    {
        if (o.Car is { } car && car.Def.Seats[o.CarSeat].Exposed) // the seat where the car stood at the shot's view (B-02), or now
            return new HitTarget(o.Id, N(SeatPosAt(car, o.CarSeat, PoseOf(car))), RiderRules.HitStance(car.Def, o.CarSeat, o.Body.State.Stance), o.Body.Yaw, o.Worn.HeadGuarded);
        return new HitTarget(o.Id, N(o.Body.GlobalPosition), BodyStanceOf(o), o.Body.Yaw, o.Worn.HeadGuarded);
    }

    /// <summary>Distance from <paramref name="from"/> along <paramref name="dir"/> to the first thing a shot stops on (<see cref="WorldQuery.Shot"/>), or infinity.</summary>
    float WorldDistance(Vector3 from, Vector3 dir, float max) => WorldHit(from, dir, max, out _);

    /// <summary>
    /// The walls a ray stops on: the world and the shot-only cover (tree crowns, bushes), by physics (<see cref="WorldQuery.Wall"/>). Vehicles are not in it: their hulls are
    /// <see cref="NearestHull"/>, analytic, at the pose the shot's view showed (ADR 0013, B-02).
    /// </summary>
    float WallDistance(Vector3 from, Vector3 dir, float max) => Query.Wall(from, dir, max);

    /// <summary>
    /// The nearest vehicle hull a ray from <paramref name="from"/> enters within <paramref name="max"/>, among the living vehicles that are not
    /// <paramref name="skipA"/>, <paramref name="skipB"/> or in <paramref name="passed"/>. The hull is the upright box of <see cref="VehicleBox"/>, or the tilted boxes
    /// of a type's <see cref="VehicleDef.Hull"/> (the ones <see cref="VehicleCollider"/> uses), at the pose <see cref="PoseOf"/> gives it. Distance in metres from <paramref name="from"/>.
    /// </summary>
    ServerCar? NearestHull(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB, List<ServerCar>? passed, out float distance, out VehiclePose pose)
    {
        ServerCar? best = null;
        distance = float.PositiveInfinity; pose = default;
        foreach (var car in _cars)
        {
            if (car == skipA || car == skipB || car.Destroyed || passed != null && passed.Contains(car)) continue;
            var p = PoseOf(car);
            if (VehicleBox.Ray(N(from), N(dir), max, p.Feet, p.Yaw, p.Pitch, p.Roll, car.Def) is not { } hit || hit.Distance >= distance) continue;
            best = car; distance = hit.Distance; pose = p;
        }
        return best;
    }

    /// <summary>
    /// Distance from <paramref name="from"/> along <paramref name="dir"/> to the first thing a ray stops on: a wall, or a vehicle's hull (the
    /// nearer of <see cref="WallDistance"/> and <see cref="NearestHull"/>), and which vehicle if it was one. The shooter's own vehicle
    /// (<paramref name="skip"/>) is not in the way.
    /// </summary>
    float WorldHit(Vector3 from, Vector3 dir, float max, out ServerCar? car, ServerCar? skip = null)
    {
        var stop = Query.Shot(from, dir, max, skip);
        car = stop.Car;
        return stop.Distance;
    }

    /// <summary>The distance the ray stops at with two vehicles left out (the hull it hit and the shooter's own): a wall or another hull.</summary>
    float NextStop(Vector3 from, Vector3 dir, float max, ServerCar? skipA, ServerCar? skipB) => Query.Shot(from, dir, max, skipA, skipB).Distance;

    const float MaxShotRange = 300f;

    /// <summary>
    /// Resolves one shot or swing against the current positions (no lag compensation) and walls, applies damage, sends events.
    /// <paramref name="seatCar"/> is the vehicle the shooter rides in an armed seat (ADR 0011), null on foot. From a seat the shot leaves the
    /// seated eye, its aim is held inside the seat's arc, the vehicle's speed widens the spread as the shooter's own does on foot, the vehicle's
    /// own hull is not in the way, and its riders are not targets: riders of one vehicle never hit each other. Everything else is the same shot,
    /// so its kills are ordinary kills with that weapon.
    /// </summary>
    void FireWeapon(Client c, in NetInput i, FireOutcome shot, ServerCar? seatCar = null)
    {
        var w = shot.Weapon;
        var back = VehicleBackOf(c); // the vehicles this shot rewinds (B-02): the same view tick as the players (ADR 0013)
        _vehicleView = ViewOf(back, c.LifeStart);
        var seat = seatCar?.Def.Seats[c.CarSeat];
        var feet = seatCar != null ? SeatPos(seatCar, c.CarSeat) : c.Body.GlobalPosition;
        var eye = feet + Vector3.Up * (seat != null ? SeatAimRules.EyeHeight(seat) : MovementStep.EyeHeight(c.Body.State.Stance));
        var yaw = Quant.Yaw(i.Yaw); var pitch = Quant.Pitch(i.Pitch);
        if (seatCar != null)
        {
            var (carYaw, carPitch, carRoll) = AttitudeOf(seatCar);
            var held = SeatAimRules.Aim(seat!.Aim, carYaw, carPitch, carRoll, yaw, pitch);
            yaw = held.ViewYaw; pitch = held.ViewPitch;
        }
        var targets = seatCar != null ? RiderRules.NotAboard(ShotTargets(c), seatCar.Seat) : ShotTargets(c); // players where the shooter's screen showed them (ADR 0013)
        _shotsFired++;
        var aiming = (i.Buttons & (ushort)MoveButtons.Aim) != 0;
        var body = c.Body.State;
        float Spread(float degrees) => seat != null ? SeatAimRules.ShotSpread(degrees, w, aiming, seatCar!.State.Speed, seat) : HitMath.ShotSpread(degrees, w, body, aiming);
        var stance = seat != null ? Stance.Stand : body.Stance; // a seat steadies the recoil no more than standing does
        if (w.Kind == WeaponKind.Hitscan && w.Pellets > 1) FirePellets(c, eye, yaw, pitch, w, (byte)shot.WeaponIndex, Spread(w.PelletSpread), targets, seatCar);
        else if (w.Kind == WeaponKind.Hitscan)
        {
            var kick = c.Recoil.Shot(w.Recoil, RecoilState.Multiplier(w.Recoil, aiming, stance));
            var sway = c.Breath.AimError(c.Breath.SwayDegrees(w));
            var d = HitMath.SpreadDirection(yaw + kick.YawRad + sway.YawRad, RecoilState.ClampPitch(pitch + kick.PitchRad + sway.PitchRad), Spread(shot.SpreadDegrees), _rng);
            var dir = new Vector3(d.X, d.Y, d.Z);
            var range = w.Range > 0 ? w.Range : MaxShotRange;
            // a weapon with bullet drop is resolved along its curve, one straight piece at a time (BulletDrop); any other, and a bot's, is the one piece of the whole range
            var drops = BulletDrop.HasDrop(w) && !c.IsBot;
            var zero = drops ? c.Zero.Of(shot.WeaponIndex, w) : 0f;
            var from = eye; var along = dir; var len = range; var before = 0f; // the piece: its start, direction, length, and the metres of line of sight before it
            List<GlassCross>? crossed; float wall; ServerCar? shotCar; RayHit? hit;
            while (true)
            {
                if (drops)
                {
                    var a = BulletDrop.Point(N(eye), d, w, zero, before);
                    var b = BulletDrop.Point(N(eye), d, w, zero, MathF.Min(range, before + BulletDrop.SegmentM));
                    var piece = b - a;
                    len = piece.Length();
                    from = new Vector3(a.X, a.Y, a.Z);
                    along = len > 1e-4f ? new Vector3(piece.X, piece.Y, piece.Z) / len : dir;
                }
                crossed = null;
                wall = BulletHit(from, along, len, out shotCar, ref crossed, seatCar);
                var (exposedGunner, pastHull) = ExposedGunnerBehind(shotCar, from, along, len, targets, skip: seatCar);
                hit = GunnerCover.Raycast(N(from), N(along), len, wall, targets, c.Id, exposedGunner, pastHull); // a flak gunner in the open is hit through his hull
                if (hit == null && HitMath.Raycast(N(from), N(along), len, float.PositiveInfinity, targets, c.Id) != null) _wallBlocked++;
                if (!drops || hit != null || wall <= len || before + BulletDrop.SegmentM >= range) break;
                var passed = before;
                DamageCrossed(crossed, float.PositiveInfinity, at => HitMath.HitscanDamage(w, passed + at, false)); // the panes of a piece the bullet flew through
                before += BulletDrop.SegmentM;
            }
            PerfShot(c, eye, dir, range, hit?.Id);
            var end = from + along * (hit?.Distance ?? Math.Min(wall, len));
            BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, N(eye), N(end))));
            BaleShot(N(from), N(along), hit?.Distance ?? Math.Min(wall, len)); // any bale the bullet passes catches fire (#331)
            if (hit is { } h)
            {
                // a wild animal under any hitscan weapon: where the bullet entered its body, taken before the hit can remove it (the hunter's log keeps it; a sniper's shooter is also shown it)
                var hunt = WildlifeRules.IsHitId(h.Id) ? HuntShotOf(targets, h.Id, N(from + along * h.Distance), before + h.Distance, (byte)shot.WeaponIndex, h.ShapeHead) : null;
                var dealt = Deal(c.Id, (byte)shot.WeaponIndex, h.Id, HitMath.HitscanDamage(w, before + h.Distance, h.Head), h.Head, false, shot: hunt, shotDistance: before + h.Distance);
                if (dealt == DamageResult.Killed && hunt is { } shown && w.Category == WeaponCategory.Sniper) Send(c, new EventMsg((byte)EventKind.HuntShot, HuntShot.Encode(shown)));
            }
            else if (shotCar != null && Vehicles != null && wall <= len)
                DamageVehicle(shotCar, HitMath.HitscanDamage(w, before + wall, false) * Vehicles.BulletDamageMultiplier, c.Id, (byte)shot.WeaponIndex, 0);
            else if (shotCar == null && wall <= len) BulletPuff(N(from + along * wall)); // dust where a bullet meets the ground or a wall (ticket #154)
            if (hit == null && wall <= len) ShotGlass(shotCar, from, along, wall, HitMath.HitscanDamage(w, before + wall, false)); // the pane the bullet crossed where it stopped (ADR 0007)
            DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, at => HitMath.HitscanDamage(w, before + at, false));
        }
        else if (w.Kind == WeaponKind.Projectile)
        {
            var dir = HitMath.Forward(yaw, pitch);
            if (w.Lock != null && FireHoming(c, shot, eye)) { _vehicleView = null; return; } // a Lock-on launcher's missile homes on its Aircraft (ADR 0031)
            var rocket = new Rocket { Id = _nextRocket, Owner = c.Id, Weapon = (byte)shot.WeaponIndex, Pos = eye, Dir = new Vector3(dir.X, dir.Y, dir.Z), FromCar = seatCar };
            _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
            rocket.ViewBack = back; rocket.ViewLife = c.LifeStart; // a rocket rewinds vehicles by the shooter's lag at each step (B-02)
            _rockets.Add(rocket);
            _rocketsFired++;
            BroadcastEvent(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, c.Id, rocket.Weapon, N(eye), dir * w.ProjectileSpeed)));
        }
        else if (w.Kind == WeaponKind.Melee)
        {
            var fwd = HitMath.Forward(yaw, 0);
            var hit = HitMath.Melee(N(feet), yaw, w.Reach, targets, c.Id);
            if (hit is { } h && _byId.ContainsKey(h.Id) && TargetFeet(targets, h.Id) is { } victimFeet)
            {
                // a wall between the attacker and the victim's chest blocks the stab (the chest where the victim was seen, ADR 0013)
                var chest = victimFeet + Vector3.Up * 1.0f;
                var to = chest - eye;
                if (WorldHit(eye, to.Normalized(), to.Length(), out _, seatCar) < to.Length() - 0.05f) { _wallBlocked++; hit = null; }
            }
            BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, N(eye), N(eye) + fwd * w.Reach)));
            if (hit is { } m) Deal(c.Id, (byte)shot.WeaponIndex, m.Id, HitMath.MeleeDamage(w, m.Backstab), false, m.Backstab);
            else if (TurretMelee(N(feet), yaw, w.Reach) is { } turret) _match.Entities.Damage(turret, Turret.KnifeDamage * c.Perks.VehicleDamageDealt, c.Id);
        }
        _vehicleView = null; // the blast and everything after the shot see the vehicles where they stand
    }

    /// <summary>Applies damage through the match and sends the hit event; returns the result (Ignored when absorbed).</summary>
    /// <param name="shot">Where the bullet entered a wild animal it hit (the hit diagram), and <paramref name="shotDistance"/> how far it flew: for the hunter's log (<see cref="OnWildKilled"/>).</param>
    DamageResult Deal(ushort attackerId, byte weaponIdx, ushort victim, float damage, bool head, bool backstab, KillCause cause = KillCause.Standard, HuntShotMsg? shot = null, float? shotDistance = null)
    {
        if (victim == BigfootRules.TargetId) return DealBigfoot(attackerId, weaponIdx); // a hit count, not hit points (issue #264)
        if (_streaks.ControlOf(victim) is { ShieldsBody: true }) return DamageResult.Ignored; // a pilot at the gun is not a sitting target (attack helicopter)
        if (DemonRules.IsHitId(victim)) return DemonHit(attackerId, weaponIdx, damage); // the demon: a hit count, no hit points
        if (WildlifeRules.IsHitId(victim)) _wildHit = new WildHit(weaponIdx, cause, shotDistance, shot); // read by the keeper's Killed event, inside this call
        var r = _match.ApplyDamage(victim, attackerId, weaponIdx, damage, cause: head ? KillCause.Headshot : cause);
        _wildHit = null;
        if (r == DamageResult.Ignored) return r; // dead, spawn protected or round not running: the shot is absorbed
        if (CitizenRules.IsHitId(victim) || PetRules.IsHitId(victim) || WildlifeRules.IsHitId(victim) || GangRules.IsHitId(victim) || RaidVanRules.IsHitId(victim)) // a citizen, a pet, a wild animal, a Gang member or a Raid van: the hit marker, but no assist, streak, refill or kill feed
        {
            if (_byId.TryGetValue(attackerId, out var shooter)) Send(shooter, new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(victim, attackerId, weaponIdx, (ushort)damage, head, backstab, r == DamageResult.Killed))));
            return r;
        }
        if (r == DamageResult.Saved) BroadcastEvent(EventKind.BlessingSaved, StatusCodec.EncodeSaved(victim)); // the blessing is used up; the mask follows with the flush
        else if (r == DamageResult.Damaged) CurseOnHit(attackerId, weaponIdx, victim);
        if (r == DamageResult.Damaged && Scores(attackerId) && Scores(victim)) _xp.NoteDamage(victim, attackerId, _elapsed); // an assist if the victim dies within the window
        BotHit(victim, attackerId);
        _hitsLanded++;
        LossHit(attackerId, victim, r == DamageResult.Killed);
        if (head) _headshots++;
        if (backstab) _backstabs++;
        var kill = r == DamageResult.Killed;
        var msg = new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(victim, attackerId, weaponIdx, (ushort)damage, head, backstab, kill)));
        _byId.TryGetValue(attackerId, out var attacker);
        if (attackerId == _gunFxOwner) _gunFx.Hit(attackerId, head, kill); // a gunner's hit marker rides the tick's gunship blasts (#159)
        else if (attacker != null) Send(attacker, msg);
        if (_byId.TryGetValue(victim, out var v) && v != attacker) Send(v, msg);
        if (kill && victim != attackerId && attacker != null) { attacker.W.RefillReserves(); _refills++; }
        return r;
    }

    /// <summary>Moves every rocket one tick: swept against the world and live players (not the owner), explodes on impact or when its life runs out.</summary>
    void StepRockets(float dt)
    {
        for (var k = _rockets.Count - 1; k >= 0; k--)
        {
            var r = _rockets[k];
            var w = Weapons.Weapons[r.Weapon];
            var speed = r.Speed > 0 ? r.Speed : w.ProjectileSpeed;
            var life = r.Life > 0 ? r.Life : w.ProjectileLife;
            var len = speed * dt;
            // a rocket fired from an armed seat passes its own vehicle's hull and riders for as long as its owner still rides it
            var own = RocketCar(r);
            // the vehicles a rocket meets are where its shooter's screen showed them, this step (B-02); the players are where they stand (ADR 0013)
            _vehicleView = ViewOf(r.ViewBack, r.ViewLife);
            var wall = WorldHit(r.Pos, r.Dir, len, out var hitCar, own);
            var all = LiveTargets();
            var targets = own != null ? RiderRules.NotAboard(all, own.Seat) : all;
            var (exposedGunner, pastHull) = ExposedGunnerBehind(hitCar, r.Pos, r.Dir, len, targets, skip: own);
            var step = RocketFlight.Step(N(r.Pos), N(r.Dir), r.Age, speed, life, dt, wall, targets, r.Owner, exposedGunner, pastHull);
            ReturnTargets(all); // the step is done with the targets (NotAboard copied its own)
            _vehicleView = null;
            switch (step.Outcome)
            {
                case RocketOutcome.Player:
                    _rockets.RemoveAt(k);
                    Explode(r, w, r.Pos + r.Dir * step.Distance, step.Victim);
                    continue;
                case RocketOutcome.World:
                    _rockets.RemoveAt(k);
                    _worldExplosions++;
                    // pulled back a hair so the blast centre is in front of the surface, not inside it
                    Explode(r, w, r.Pos + r.Dir * MathF.Max(0, step.Distance - 0.02f), null, hitCar);
                    continue;
            }
            r.Pos += r.Dir * len;
            r.Age += dt;
            if (step.Outcome == RocketOutcome.Expired) { _rockets.RemoveAt(k); _expiredRockets++; Explode(r, w, r.Pos, null); }
        }
    }

    /// <summary>The vehicle a rocket was fired from, while its owner still rides it; null otherwise (fired on foot, or the owner has got out or died).</summary>
    ServerCar? RocketCar(Rocket r) => r.FromCar is { Destroyed: false } car && car.Seat.SeatOf(r.Owner) >= 0 ? car : null;

    /// <summary>
    /// Explosion: the direct-hit victim takes the full damage (and no splash on top); everyone else alive within the splash
    /// radius takes the linear falloff damage, the owner scaled by the self multiplier. A wall between the blast and a target stops it, and a covered seat's rider takes half through the hull (<see cref="SplashShare"/>). Affected players get knockback. A rocket fired from an armed seat spares the shooter's fellow riders (riders
    /// of one vehicle never hit each other); the shooter takes their own blast as on foot, and so does the vehicle.
    /// </summary>
    void Explode(Rocket r, WeaponDef weapon, Vector3 pos, ushort? direct, ServerCar? directCar = null)
    {
        var w = ProjectileMath.WithBlast(weapon, r.Blast); // a gunship's rocket has its own blast (killstreaks.json); the rpg keeps weapons.json's
        _explosions++;
        if (Vehicles != null) RocketVsVehicles(pos, w, r.Owner, r.Weapon, directCar);
        BlastGlass(pos, w.Splash?.Radius ?? 0);
        BroadcastEvent(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(r.Id, r.Owner, r.Weapon, N(pos), w.Splash?.Radius ?? 0, w.Splash?.WaveSpeed ?? 0)));
        BroadcastImpact(N(pos), ImpactSize.Plume, body: direct != null && directCar == null, vehicle: directCar != null); // rockets throw up a plume (ticket #154)
        var own = RocketCar(r);
        var targets = LiveTargets();
        foreach (var t in targets)
        {
            var isDirect = direct == t.Id;
            var dist = HitMath.DistanceToBody(N(pos), t);
            var self = t.Id == r.Owner;
            if (!self && own != null && own.Seat.SeatOf(t.Id) >= 0) continue; // a fellow rider of the shooter's vehicle
            var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, self);
            if (dmg <= 0) continue;
            var share = isDirect ? 1f : SplashShare(N(pos), t); // walls, a bunker room's too, and a covered seat's hull
            if (share <= 0) continue;
            dmg *= share;
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(w, N(pos), HitMath.Centre(t), isDirect ? 1f : share * ProjectileMath.SplashFraction(w, dist));
            PushOnDeath(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = Deal(r.Owner, r.Weapon, t.Id, dmg, false, false);
            ClearPush(t.Id, res);
            if (res == DamageResult.Ignored) continue; // spawn protected: neither damage nor knockback
            if (isDirect) _directHits++; else if (self) _selfHits++; else _splashHits++;
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v) && k != NVec3.Zero) { v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z)); _knocked++; }
        }
        ReturnTargets(targets);
    }

    /// <summary>The clients simulated in this tick (reused every tick).</summary>
    readonly List<Client> _simClients = new();

    /// <summary>The entity records of the snapshot being sent (reused every tick; <see cref="SendSnapshots"/>).</summary>
    readonly List<EntityRecord> _snapEnts = new();

    void SendSnapshots()
    {
        var ents = _snapEnts; // one list for every tick (the encoders read it inside this call only)
        ents.Clear();
        foreach (var c in _byId.Values) // every joined player, bots included
                ents.Add(EntityRecord.Make(c.Id, EntityType.Player, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z),
                    c.Body.Yaw, c.Body.Pitch, c.Body.Health, c.Car != null && _match.StateOf(c.Id) != EntityState.Dead ? EntityState.InVehicle : _match.StateOf(c.Id), c.Slot,
                    c.Car != null ? RiderRules.HitStance(c.Car.Def, c.CarSeat, c.Body.State.Stance) : BodyStanceOf(c)));
        foreach (var car in _cars) if (car.Seat.Count > 1) ents.Add(SeatsRecord.Make(car.Id, SeatOccupants(car))); // who sits in seats 1..4, for every client (ADR 0026)
        foreach (var car in _cars)
        {
            if (car.Retired) continue; // removed for good (RemoveRocketCarts): not sent, so every client drops it
            var stage = Vehicles == null ? 0 : VehicleDamage.Stage(car.Hp, car.Life.MaxHp, Vehicles.DamageStages);
            ents.Add(car.IsTrailer
                ? VehicleRecord.MakeTrailer(car.Id, N(car.GlobalPosition), car.State.Yaw, car.Tractor?.Id ?? (ushort)0, car.Colour, car.Cargo!.Loaded, stage, car.Destroyed) // a trailer: its tractor in the driver's field, its load in a health bit (protocol 67)
                : VehicleRecord.Make(car.Id, N(car.GlobalPosition), car.State.Yaw, car.Seat.Driver, car.Colour, stage, car.Destroyed, car.Lights, car.Life.WheelsOff));
        }
        foreach (var car in _cars) if (car.Def.Flak != null) ents.Add(FlakRecordOf(car)); // a flak truck's turret aim, for everyone's model
        foreach (var car in _cars) if (car.Flies) ents.Add(FlightRecord.Make(car.Id, car.Flight)); // a helicopter's velocity, tilt and rotor (ADR 0006)
        foreach (var car in _cars) if (car.Drives) ents.Add(DriveRecord.Make(car.Id, car.Drive)); // a driven vehicle's speeds, slope, steering and whether it floats (ADR 0010)
        foreach (var car in _cars) if (car.Def.DoorGun != null) ents.Add(DoorGunRecordOf(car)); // a door gun's aim against its airframe, for everyone's model
        ents.AddRange(TargetRecords()); // target dummies (#148)
        foreach (var r in _rockets)
            ents.Add(EntityRecord.Make(r.Id, EntityType.Projectile, N(r.Pos),
                MathF.Atan2(-r.Dir.X, -r.Dir.Z), MathF.Asin(Math.Clamp(r.Dir.Y, -1f, 1f)), 0, EntityState.Alive, r.Weapon));
        // an empty tick (nothing changed for this client) sends no packet except the heartbeat, which carries the input ack (A-05)
        var heartbeat = _tick % Wire.EmptyTickHeartbeat == 0;
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            foreach (var pkt in c.Enc.Encode(_tick, c.Ack, c.SnapAck, ents))
                if (pkt.Length > SnapshotEncoder.HeaderBytes || heartbeat) Tx(c.Peer!, Wire.ChanData, pkt, 0);
        }
        var world = _match.Entities.Snapshot();
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            var at = c.Body.GlobalPosition; // the body, dead or spectating too: world interest follows where the player is (ADR 0025)
            foreach (var pkt in c.WEnc.Encode(_tick, c.WAck, world, new System.Numerics.Vector3(at.X, at.Y, at.Z))) Tx(c.Peer!, Wire.ChanData, pkt, 0);
        }
        AuditTick(world);
        LossLogStates(ents);
        _host.Flush();
    }
}
