using Godot;
using Suchohrad.ShooterCore;
using Suchohrad.ShooterServer.Persistence;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

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
        public ushort Id;
        public string Name = "";
        public bool Joined;
        /// <summary>The client asked for sandbox mode in its hello (#156).</summary>
        public bool SandboxRequested;
        /// <summary>Sandbox mode granted (the server allows it and the client asked). See <see cref="SandboxRules"/>.</summary>
        public bool Sandbox;
        public Player Body = null!;
        /// <summary>Recoil of this player's trigger hand; the server fires hitscan shots with its offset.</summary>
        public RecoilState Recoil = new(0);
        /// <summary>Sniper breath hold of this player; the server adds its per-shot aim error to hitscan shots.</summary>
        public BreathState Breath = new(0);
        public readonly Queue<NetInput> Inputs = new();
        public ushort Ack;
        public bool HasAck;
        public ushort LastSeq;
        public bool HasSeq;
        public byte Slot;
        public PlayerWeapons W = null!;
        /// <summary>The player's perk values. Neutral until the loadout ticket sets them from the player's loadout.</summary>
        public PerkModifiers Perks = PerkModifiers.None;
        /// <summary>XP granted to this player since the round began (the end-of-round summary).</summary>
        public readonly List<XpGrant> RoundXp = new();
        /// <summary>Entity snapshots for this client, against the states the server kept (set in Join).</summary>
        public SnapshotEncoder Enc = null!;
        /// <summary>The newest snapshot tick this client has committed (from its inputs), the baseline for its deltas; NoTick until then.</summary>
        public uint SnapAck = Wire.NoTick;
        public readonly WorldSnapshotEncoder WEnc = new();
        public Vector3 Start;
        public float MaxMoved;
        public ServerCar? Car;
        /// <summary>Seat of <see cref="Car"/> this player sits in (0 = driver).</summary>
        public int CarSeat;
        /// <summary>The flak status last sent to this client as the gunner (magazine, reserve, reloading), so only a change is sent; null until the first.</summary>
        public (int Mag, int Reserve, bool Reloading)? FlakSent;
        public bool UseHeld, LightsHeld, Handbrake, StreakHeld;
        public float Throttle, Steer;
        public PlayerProfile? Profile;
        public ProfileLoad? Loading;
        /// <summary>Server time of the connect, for the hello deadline.</summary>
        public double ConnectedAt;
        public CatchUpBudget CatchUp;
        /// <summary>The profile token from the hello ("" = guest). One live session per token.</summary>
        public string Token = "";
        public int PacketsThisTick;
        public double SaveTokens = 10;
        public long BytesOut;
    }

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
    /// <summary>weapons.json (hotbar, ammo, damage numbers).</summary>
    public WeaponsFile Weapons = null!;
    /// <summary>Weapons smoke mode: builds a flat test arena with a wall far from the map and judges the scenario (implies debug commands).</summary>
    public bool WeaponsSmoke;
    public float KillY = float.NegativeInfinity;
    /// <summary>The map's water (water.json); players swim, drown and lose the weapon lock under it.</summary>
    public Suchohrad.ShooterCore.Water Water = Suchohrad.ShooterCore.Water.Empty;
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
    Godot.Collections.Array<Rid> _playerRids = new();
    bool _playerRidsDirty = true;
    MatchSim _match = null!;
    // match smoke bookkeeping
    int _kills, _roundsEnded, _restarts, _ignoredHits, _earlyProtectionEnds;
    double _finishAt;
    readonly Random _rng = new();
    // weapons smoke bookkeeping
    int _shotsFired, _hitsLanded, _headshots, _wallBlocked, _backstabs, _refills;
    // rockets: server-owned projectile entities, ids from 0x8000 up (player ids count up from 1)
    /// <summary>A rocket in flight. Speed and Life are per launch when set (a gunship's, killstreaks.json); zero keeps the weapon's own.</summary>
    sealed class Rocket { public ushort Id, Owner; public byte Weapon; public Vector3 Pos, Dir; public float Age; public SplashDef? Blast; public float Speed, Life; }
    readonly List<Rocket> _rockets = new();
    ushort _nextRocket = 0x8000;
    public bool BazookaSmoke;
    /// <summary>Car smoke: flat arena, three cars at fixed spots, debug commands, judges the vehicle scenario.</summary>
    public bool CarSmoke;
    /// <summary>Blast smoke (ticket #103): flat arena with three cars (two side by side, one far), judges vehicle damage, the blast and the chain reaction.</summary>
    public bool BlastSmoke;
    bool ArenaMode => WeaponsSmoke || BazookaSmoke || CarSmoke || BlastSmoke;
    int _rocketsFired, _explosions, _directHits, _splashHits, _selfHits, _worldExplosions, _expiredRockets, _knocked;
    public const float ArenaX = 20000f, ArenaZ = 20000f;

    public override void _Ready()
    {
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
        _match.Entities.Log = s => GD.PrintErr($"server: {s}");
        _match.Entities.Spawned += w => BroadcastEvent(EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State));
        _match.Entities.Despawned += w => BroadcastEvent(EventKind.WorldDespawn, WorldCodec.EncodeDespawn(w.Id, w.Reason));
        CorpseRules.Register(WorldEntityRegistry.Shared);
        TurretRules.Register(WorldEntityRegistry.Shared);
        _corpses = new CorpseBook(_match.Entities);
        InitAircraft();
        BuildCarePackages();
        BuildStreaks(); // after the match and the crate book: the care package streak needs both; the turret streak spawns into the match's entities
        WarmSurfaceIndexes(); // roads.json and the tree grid, so no impact pays for them on a tick (T3-003, T3-004)
        if (Vehicles == null) Vehicles = new VehiclesFile { Vehicles = { new VehicleDef { Id = "car", TopSpeed = 44.4f, Acceleration = 3.5f, Brake = 9.5f, TurnRadius = 5.5f, MaxLateralAccel = 8, Mass = 1200 } } };
        if (Weapons != null) foreach (var v in Vehicles.Vehicles) if (v.Flak is { } fk) FlakWeapon(fk); // a flak naming an unknown weapon fails here, not on the first shot
        GD.Print($"server: {Spawns.Count} spawns, y {(Spawns.Count > 0 ? Spawns.Min(v => v.Y) : 0)}..{(Spawns.Count > 0 ? Spawns.Max(v => v.Y) : 0)}, killY {KillY}");
        GD.Print($"SHOOTER_NET_LISTENING port={Port} tick={Wire.TickRate}Hz");
    }

    public override void _ExitTree() { _host.Destroy(); _nav?.Free(); }

    public override void _Process(double delta)
    {
        if (Sim != null && Sim.Pump()) _host.Flush(); // delayed packets go out at frame rate, not only on physics ticks
    }

    public override void _PhysicsProcess(double delta)
    {
        var tickStartUs = Time.GetTicksUsec();
        var catchUp = CatchUpTick(tickStartUs);
        if (Sim != null && Sim.Pump()) _host.Flush();
        _tick++;
        if (!_spawnsChecked)
        {
            if (_tick >= 3) { CheckSpawnGround(); SpawnCars(); PlanTargets(); BuildBotNav(); }
            return; // not accepting connections until the spawn list is validated
        }
        _elapsed += delta;
        foreach (var cl in _clients.Values) cl.PacketsThisTick = 0;
        Poll();
        CompleteProfileLoads();
        CompleteSaves();
        DropSilentPeers();
        _match.Update();
        if (_tick % Wire.TickRate == 0 && _clients.Count > 0) BroadcastEvent(EventKind.MatchState, MatchCodec.Encode(BuildMatchState())); // about once a second, so the scoreboard ping is not stale
        _match.Entities.Tick((float)delta);
        StreakTick((float)delta);
        BotsTick((float)delta); // fills the roster and queues each bot's input for this tick (#214)
        foreach (var car in _cars) car.PlaceSolid(); // the boxes on-foot players are stopped by, where the vehicles stand after the last tick (#217)
        foreach (var c in _byId.Values.ToList()) SimulateClient(c, (float)delta); // every joined player, bots included
        StepRockets((float)delta);
        StepCorpses((float)delta);
        StepVehicles((float)delta);
        DetonateShells();  // shells whose tracer has arrived burst now (#162)
        DetonateFlak();    // flak shells that have arrived burst now
        FlushGunshipFx(); // the gunship rounds of this tick, coalesced (#159)
        FlushGlass();     // the glass panes that changed this tick (ADR 0007)
        if (CarSmoke && _carReturns >= 1 && _finishAt == 0) _finishAt = _elapsed + 1.0;
        if (catchUp && LossTest == 0) _skippedSnapshots++; // the next regular tick sends the current state
        else SendSnapshots();
        TickTelemetry(tickStartUs, delta);
        LossTickEnd(tickStartUs);
        if (_clients.Count > _peakPlayers) _peakPlayers = _clients.Count;
        if (QuitAfter > 0) foreach (var c in _clients.Values) if (c.Joined) _movedByName[c.Name] = c.MaxMoved; // only the smoke verdict reads it; a production server keeps no per-name history
        if (_tick % (Wire.TickRate * 2) == 0 && _clients.Count > 0)
            GD.Print($"server: tick {_tick}, players {string.Join(", ", _byId.Values.Select(c => $"{c.Name}#{c.Id}@({c.Body.GlobalPosition.X:F1},{c.Body.GlobalPosition.Y:F1},{c.Body.GlobalPosition.Z:F1}) ack={c.Ack}"))}");
        if (BazookaSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt || QuitAfter > 0 && _elapsed >= QuitAfter) FinishBazooka();
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
        var space = World.GetWorld3D().DirectSpaceState;
        var before = Spawns.Count;
        Spawns.RemoveAll(sp =>
        {
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(sp.X, sp.Y + 2f, sp.Z), new Vector3(sp.X, sp.Y - 5f, sp.Z), CollisionLayers.World);
            return space.IntersectRay(q).Count == 0;
        });
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

    void FinishBazooka()
    {
        var ok = _rocketsFired >= 3 && _explosions >= 3 && _directHits >= 1 && _splashHits >= 1 && _selfHits >= 1 && _worldExplosions >= 2 && _kills >= 1 && _refills >= 1 && _knocked >= 1;
        GD.Print($"server: bazooka smoke rockets={_rocketsFired} explosions={_explosions} direct={_directHits} splash={_splashHits} self={_selfHits} world={_worldExplosions} expired={_expiredRockets} knocked={_knocked} kills={_kills} refills={_refills}");
        GD.Print(ok ? "SHOOTER_BAZOOKA_SERVER_OK" : "SHOOTER_BAZOOKA_SERVER_FAIL");
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
            if (m is HelloMsg h && c.Id == 0) OnHello(c, h);
            else if (m is EventMsg { Kind: (byte)EventKind.Debug } e && c.Joined && (DebugCommands || ArenaMode)) OnDebug(c, e.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.PickUpCorpse } pu) OnPickUpCorpse(c, pu.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.OpenCarePackage } oc) OnOpenCarePackage(c, oc.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SandboxGrant } sg && c.Joined) OnSandboxGrant(c, sg.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SandboxAction } sa && c.Joined) OnSandboxAction(c, sa.Payload);
            else if (m is EventMsg { Kind: (byte)EventKind.SeatSwitch } sw && c.Joined) OnSeatSwitch(c, sw.Payload);
            else if (c.Joined) OnProfileMessage(c, m);
        }
        else if (c.Joined && d[0] == Wire.TInput)
        {
            _scratch.Clear();
            if (!Wire.TryDecodeInputs(d, _scratch, out var snapTick)) return;
            if (snapTick != Wire.NoTick && snapTick <= _tick && _sentStates.Has(snapTick) && (c.SnapAck == Wire.NoTick || snapTick > c.SnapAck)) c.SnapAck = snapTick; // an older input's ack arriving late changes nothing
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
        c.Body = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, Authoritative = false, Name = $"P{c.Id}", Water = Water, Ladders = MapLadders };
        World.AddChild(c.Body);
        c.Body.Teleport(Vector3.Zero);
        c.Joined = true;
        foreach (var car in _cars) { car.AddCollisionExceptionWith(c.Body); car.Probe?.Ignore(c.Body); }
        _byId[c.Id] = c;
        _playerRidsDirty = true;
        c.Enc = new SnapshotEncoder(_sentStates);
        SendProfile(c);
        SendSandboxStatus(c); // the answer to a sandbox request (#156), before the spawn
        Send(c, new WelcomeMsg(Wire.ProtocolVersion, c.Id, Wire.TickRate, _tick,
            (ushort)(Clock.StartMinute), (uint)Math.Round(Clock.DayLengthSeconds * 1000), Clock.Frozen));
        foreach (var o in _byId.Values)
        {
            Send(c, new JoinMsg(o.Id, o.Name));
            if (o != c) Send(o, new JoinMsg(c.Id, c.Name));
        }
        foreach (var w in _match.Entities.Snapshot()) Send(c, new EventMsg((byte)EventKind.WorldSpawn, WorldCodec.EncodeSpawn(w.Id, w.Type, w.State)));
        SendGlassOnJoin(c); // the glass panes that are not whole (ADR 0007)
        SendTargetRoster(c); // target dummies' names (#148)
        SendAppearancesOnJoin(c); // camouflage (#140): every player's look reaches the joiner, the joiner's reaches everyone
        _match.AddPlayer(c.Id, c.Name, c.Sandbox); // spawns the player (event) and broadcasts the scoreboard
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
        if (c.Car != null) { c.Car.Seat.Exit(c.Id); c.Car = null; }
        foreach (var car in _cars) { car.RemoveCollisionExceptionWith(c.Body); car.Probe?.Unignore(c.Body); } // the exception outlives the freed body otherwise (T3-014)
        c.Body.QueueFree();
        _byId.Remove(c.Id);
        _playerRidsDirty = true;
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
        foreach (var c in _clients.Values.Where(c => c.Id == 0 && _elapsed - c.ConnectedAt > HelloDeadlineSeconds).ToList())
        {
            GD.Print("server: dropping a connection that sent no hello");
            Drop(c.Peer!);
            c.Peer!.PeerDisconnectLater(0);
        }
    }

    void Tx(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (_clients.TryGetValue(peer, out var c)) c.BytesOut += data.Length;
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
        var bytes = Wire.Encode(m); // encoded once: every recipient gets the same bytes (Tx copies them into the packet)
        foreach (var c in _clients.Values)
            if (c.Joined) Tx(c.Peer!, Wire.ChanReliable, bytes, (int)ENetPacketPeer.FlagReliable);
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

    void OnMatchEvent(MatchEvent e)
    {
        switch (e)
        {
            case ScoreboardChanged: BroadcastEvent(EventKind.MatchState, MatchCodec.Encode(BuildMatchState())); break;
            case PhaseChanged { Phase: MatchPhase.Ending }:
                _roundsEnded++;
                RoundEnded();
                GD.Print($"server: round over, winners {string.Join(",", _match.Winners)}");
                break;
            case PhaseChanged { Phase: MatchPhase.Running }:
                ResetCars();
                LossRoundStart();
                RoundStarted();
                if (_roundsEnded > 0) { _restarts++; _finishAt = _elapsed + 1.5; }
                GD.Print("server: round started");
                break;
            case PlayerSpawned s:
                if (_byId.TryGetValue(s.Id, out var sc))
                {
                    sc.Body.Teleport(new Vector3(s.Position.X, s.Position.Y + 0.1f, s.Position.Z));
                    sc.Body.Health = MoveConsts.MaxHealth;
                    sc.Inputs.Clear(); sc.Start = sc.Body.GlobalPosition; sc.MaxMoved = 0;
                    ApplyLoadout(sc); // a loadout changed while alive applies from this spawn on
                    sc.W.Reset(_elapsed);
                    sc.Recoil.Reset();
                    sc.Breath.Reset();
                    SendAmmo(sc);
                }
                BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(s.Id, s.Position, s.ProtectionSeconds)));
                break;
            case PlayerKilled k:
                if (_byId.TryGetValue(k.Victim, out var kv) && kv.Car != null) LeaveSeat(kv, false);
                _kills += k.Killer != k.Victim ? 1 : 0;
                BotKillLog(k);
                if (WeaponsSmoke && _kills >= 2 && _finishAt == 0) _finishAt = _elapsed + 1.5;
                if (BazookaSmoke && _kills >= 1 && _finishAt == 0) _finishAt = _elapsed + 1.5;
                LossKill(k.Killer, k.Victim);
                RecordKill(k);
                var fromStreak = StreakKill(k);
                if (Scores(k.Killer) && Scores(k.Victim)) AwardXp(_xp.OnKill(new KillFacts(k.Killer, k.Victim, k.Cause, fromStreak), _elapsed)); // the death of a bot (#214) or of a sandbox player grants no XP
                BroadcastEvent(EventKind.Kill, MatchCodec.Encode(new KillMsg(k.Killer, k.Victim, k.Weapon, k.Cause)));
                break;
            case TargetKilled tk: OnTargetKilled(tk); break;
            case PlayerDied d: LossDeath(d.Victim); RecordDeath(d.Victim); StreakDeath(d.Victim); SpawnCorpse(d.Victim); BroadcastEvent(EventKind.Death, MatchCodec.Encode(new DeathMsg(d.Victim, d.Killer, d.RespawnDelay))); break;
        }
    }

    MatchStateMsg BuildMatchState()
    {
        var rows = _match.Ranking().Select(p => new ScoreRow(p.Id, p.Sandbox ? p.Name + " [SANDBOX]" : p.Name, p.Kills, p.Deaths, Ping(p.Id))).ToList();
        return new MatchStateMsg(_match.Phase, _match.TimeRemaining, _match.Winners, rows);
    }

    ushort Ping(ushort id) =>
        _byId.TryGetValue(id, out var c) && c.Peer is { } peer ? (ushort)Math.Clamp(peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime), 0, ushort.MaxValue) : (ushort)0; // a bot has no ping

    void SimulateClient(Client c, float dt)
    {
        // normally one input per tick; if the queue backs up (jitter), catch up with a second one
        var n = c.CatchUp.Steps(c.Inputs.Count); // a second input only while the catch-up credit lasts (#197)
        for (var k = 0; k < n && c.Inputs.Count > 0; k++)
        {
            var i = c.Inputs.Dequeue();
            c.Ack = i.Seq; c.HasAck = true;
            // a killstreak's control session (a turret being placed) takes the owner's input first; while it holds it,
            // Fire and Use belong to the session, not to the weapon or the vehicles
            var controlled = _streaks.RouteInput(c.Id, i);
            if (controlled) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)(MoveButtons.Fire | MoveButtons.Use));
            var useDown = (i.Buttons & (ushort)MoveButtons.Use) != 0;
            var useEdge = useDown && !c.UseHeld;
            c.UseHeld = useDown;
            var lightsEdge = Headlights.Edge((i.Buttons & (ushort)MoveButtons.Headlights) != 0, ref c.LightsHeld);
            var streakDown = (i.Buttons & (ushort)MoveButtons.Killstreak) != 0;
            var streakEdge = streakDown && !c.StreakHeld;
            c.StreakHeld = streakDown;
            if (_match.Get(c.Id)?.Life != PlayerLife.Alive) continue; // dead or waiting: inputs are acked but ignored
            if (streakEdge && !controlled) StreakActivate(c);
            if (c.Car == null && _streaks.ControlOf(c.Id) is { FreezesBody: true }) continue; // controlling a streak entity that holds the body (the missile, a gunship): the body stands still
            if (c.Car != null)
            {
                // driving: the car is simulated in StepVehicles from the driver's inputs; no walking or weapon timers.
                // A gunner's inputs fire the car's gun instead (its aim is the view yaw and pitch).
                // The pilot of a helicopter steps it here, once per input, as the body on foot is: the pilot's client predicts the same steps (ADR 0006).
                if (c.CarSeat == 0 && c.Car.Flies) c.Car.StepFlight(FlightInput.FromNet(i, handsOff: controlled), dt, _tick);
                else if (c.CarSeat == 0) { c.Throttle = Quant.Axis(i.MoveY); c.Steer = Quant.Axis(i.MoveX); c.Handbrake = (i.Buttons & (ushort)MoveButtons.Jump) != 0; }
                else if (c.Car.Gun != null)
                {
                    GunnerInput(c, i);
                    c.Body.Yaw = Quant.Yaw(i.Yaw); c.Body.Pitch = Quant.Pitch(i.Pitch); // a gunner's body faces where they aim, for everyone who sees them at the gun
                }
                if (lightsEdge) c.Car.Lights = Headlights.Toggle(c.Car.Lights);
                if (useEdge) ExitCar(c);
                continue;
            }
            if (useEdge && TryEnterCar(c)) continue;
            c.Body.Yaw = Quant.Yaw(i.Yaw);
            c.Body.Pitch = Quant.Pitch(i.Pitch);
            c.Body.Simulate(i.ToMove(c.W.SpeedFor(i.Slot)), dt);
            c.Recoil.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Fire) != 0, c.W.Current.Recoil);
            // breath hold: sprint is cancelled while aiming, so the sprint bit with aim held is the breath key
            c.Breath.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
                (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
            var wi = new WeaponInput((i.Buttons & (ushort)MoveButtons.Fire) != 0 && !c.Body.HeadUnder && !c.Body.State.OnLadder, (i.Buttons & (ushort)MoveButtons.Reload) != 0, i.Slot, (i.Buttons & (ushort)MoveButtons.Switch) != 0);
            if (c.W.Update(_elapsed, wi) is { } shot)
            {
                if (_match.NotifyFired(c.Id)) _earlyProtectionEnds++; // spawn protection ends on a real shot, not on Fire held with an empty magazine
                FireWeapon(c, i, shot);
            }
            if (c.W.Changed) SendAmmo(c);
            c.Slot = (byte)(c.W.Slot - 1);
            if (c.Body.LastFallDamage > 0) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Fall, c.Body.LastFallDamage);
            if (c.Body.LastDrowningDamage > 0) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Drown, c.Body.LastDrowningDamage);
            if (c.Body.GlobalPosition.Y < KillY) GD.Print($"server: {c.Name} fell out of the world at {c.Body.GlobalPosition} (killY {KillY}, spawn {_match.Get(c.Id)?.Position})");
            if (c.Body.GlobalPosition.Y < KillY) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Void, 9999, ignoreProtection: true);
        }
        _match.SetPosition(c.Id, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z));
        c.Body.Health = _match.Get(c.Id)?.Health ?? 0;
        var p = c.Body.GlobalPosition - c.Start;
        c.MaxMoved = Mathf.Max(c.MaxMoved, new Vector2(p.X, p.Z).Length());
    }

    void SendAmmo(Client c)
    {
        c.W.Changed = false;
        Send(c, new EventMsg((byte)EventKind.Ammo, WeaponCodec.Encode(WeaponCodec.BuildAmmo(c.W, _elapsed))));
    }

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    List<HitTarget> LiveTargets()
    {
        // A fresh list each call on purpose: callers nest (an explosion can detonate a car, which explodes again) and each
        // holds its list while it iterates, so one shared buffer would be cleared under them.
        var list = new List<HitTarget>(_byId.Count);
        foreach (var o in _byId.Values)
            if (_match.Get(o.Id)?.Life == PlayerLife.Alive) list.Add(new HitTarget(o.Id, N(o.Body.GlobalPosition), o.Body.State.Stance, o.Body.Yaw));
        list.AddRange(TargetHitTargets());
        return list;
    }

    /// <summary>The player bodies' RIDs for ray exclusion, rebuilt only after a join or a drop (T2-02) instead of per ray.</summary>
    Godot.Collections.Array<Rid> PlayerRids()
    {
        if (!_playerRidsDirty) return _playerRids;
        _playerRids = new Godot.Collections.Array<Rid>();
        foreach (var o in _byId.Values) _playerRids.Add(o.Body.GetRid());
        _playerRidsDirty = false;
        return _playerRids;
    }

    /// <summary>Distance from <paramref name="from"/> along <paramref name="dir"/> to the first world collider (player bodies excluded), or infinity.</summary>
    float WorldDistance(Vector3 from, Vector3 dir, float max) => WorldHit(from, dir, max, out _);

    /// <summary>As <see cref="WorldDistance"/>, and the vehicle if that is what the ray stopped on (destroyed vehicles have no collision, so they are shot through).</summary>
    float WorldHit(Vector3 from, Vector3 dir, float max, out ServerCar? car)
    {
        car = null;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * max);
        q.CollisionMask = CollisionLayers.Bullets; // world (1), vehicles (2) and shot-only cover (tree crowns): bullets and rockets stop on them
        q.Exclude = PlayerRids();
        var r = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (r.Count == 0) return float.PositiveInfinity;
        car = r["collider"].As<GodotObject>() as ServerCar;
        return from.DistanceTo(r["position"].AsVector3());
    }

    const float MaxShotRange = 300f;

    /// <summary>Resolves one shot or swing against the current positions (no lag compensation) and walls, applies damage, sends events.</summary>
    void FireWeapon(Client c, in NetInput i, FireOutcome shot)
    {
        var w = shot.Weapon;
        var feet = c.Body.GlobalPosition;
        var eye = feet + Vector3.Up * MovementStep.EyeHeight(c.Body.State.Stance);
        var yaw = Quant.Yaw(i.Yaw); var pitch = Quant.Pitch(i.Pitch);
        var targets = LiveTargets();
        _shotsFired++;
        var aiming = (i.Buttons & (ushort)MoveButtons.Aim) != 0;
        if (w.Kind == WeaponKind.Hitscan && w.Pellets > 1) FirePellets(c, eye, yaw, pitch, w, (byte)shot.WeaponIndex, HitMath.ShotSpread(w.PelletSpread, w, c.Body.State, aiming));
        else if (w.Kind == WeaponKind.Hitscan)
        {
            var kick = c.Recoil.Shot(w.Recoil, RecoilState.Multiplier(w.Recoil, aiming, c.Body.State.Stance));
            var sway = c.Breath.AimError(c.Breath.SwayDegrees(w));
            var d = HitMath.SpreadDirection(yaw + kick.YawRad + sway.YawRad, RecoilState.ClampPitch(pitch + kick.PitchRad + sway.PitchRad), HitMath.ShotSpread(shot.SpreadDegrees, w, c.Body.State, aiming), _rng);
            var dir = new Vector3(d.X, d.Y, d.Z);
            var range = w.Range > 0 ? w.Range : MaxShotRange;
            List<GlassCross>? crossed = null;
            var wall = BulletHit(eye, dir, range, out var shotCar, ref crossed);
            var (exposedGunner, pastHull) = ExposedGunnerBehind(shotCar, eye, dir, range, targets);
            var hit = GunnerCover.Raycast(N(eye), d, range, wall, targets, c.Id, exposedGunner, pastHull); // a flak gunner in the open is hit through his hull
            if (hit == null && HitMath.Raycast(N(eye), d, range, float.PositiveInfinity, targets, c.Id) != null) _wallBlocked++;
            var end = eye + dir * (hit?.Distance ?? Math.Min(wall, range));
            BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, N(eye), N(end))));
            if (hit is { } h) Deal(c.Id, (byte)shot.WeaponIndex, h.Id, HitMath.HitscanDamage(w, h.Distance, h.Head), h.Head, false);
            else if (shotCar != null && Vehicles != null && wall <= range)
                DamageVehicle(shotCar, HitMath.HitscanDamage(w, wall, false) * Vehicles.BulletDamageMultiplier, c.Id, (byte)shot.WeaponIndex, 0);
            else if (shotCar == null && wall <= range) BulletPuff(N(eye + dir * wall)); // dust where a bullet meets the ground or a wall (ticket #154)
            if (hit == null && wall <= range) ShotGlass(shotCar, eye, dir, wall, HitMath.HitscanDamage(w, wall, false)); // the pane the bullet crossed where it stopped (ADR 0007)
            DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, d => HitMath.HitscanDamage(w, d, false));
        }
        else if (w.Kind == WeaponKind.Projectile)
        {
            var dir = HitMath.Forward(yaw, pitch);
            var rocket = new Rocket { Id = _nextRocket, Owner = c.Id, Weapon = (byte)shot.WeaponIndex, Pos = eye, Dir = new Vector3(dir.X, dir.Y, dir.Z) };
            _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
            _rockets.Add(rocket);
            _rocketsFired++;
            BroadcastEvent(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, c.Id, rocket.Weapon, N(eye), dir * w.ProjectileSpeed)));
        }
        else if (w.Kind == WeaponKind.Melee)
        {
            var fwd = HitMath.Forward(yaw, 0);
            var hit = HitMath.Melee(N(feet), yaw, w.Reach, targets, c.Id);
            if (hit is { } h && _byId.TryGetValue(h.Id, out var v))
            {
                // a wall between the attacker and the victim's chest blocks the stab
                var chest = v.Body.GlobalPosition + Vector3.Up * 1.0f;
                var to = chest - eye;
                if (WorldDistance(eye, to.Normalized(), to.Length()) < to.Length() - 0.05f) { _wallBlocked++; hit = null; }
            }
            BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, N(eye), N(eye) + fwd * w.Reach)));
            if (hit is { } m) Deal(c.Id, (byte)shot.WeaponIndex, m.Id, HitMath.MeleeDamage(w, m.Backstab), false, m.Backstab);
            else if (TurretMelee(N(feet), yaw, w.Reach) is { } turret) _match.Entities.Damage(turret, Turret.KnifeDamage * c.Perks.VehicleDamageDealt, c.Id);
        }
    }

    /// <summary>Applies damage through the match and sends the hit event; returns the result (Ignored when absorbed).</summary>
    DamageResult Deal(ushort attackerId, byte weaponIdx, ushort victim, float damage, bool head, bool backstab, KillCause cause = KillCause.Standard)
    {
        if (_streaks.ControlOf(victim) is { ShieldsBody: true }) return DamageResult.Ignored; // a pilot at the gun is not a sitting target (attack helicopter)
        var r = _match.ApplyDamage(victim, attackerId, weaponIdx, damage, cause: head ? KillCause.Headshot : cause);
        if (r == DamageResult.Ignored) return r; // dead, spawn protected or round not running: the shot is absorbed
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
            var wall = WorldHit(r.Pos, r.Dir, len, out var hitCar);
            var targets = LiveTargets();
            var (exposedGunner, pastHull) = ExposedGunnerBehind(hitCar, r.Pos, r.Dir, len, targets);
            var step = RocketFlight.Step(N(r.Pos), N(r.Dir), r.Age, speed, life, dt, wall, targets, r.Owner, exposedGunner, pastHull);
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

    /// <summary>
    /// Explosion: the direct-hit victim takes the full damage (and no splash on top); everyone else alive within the splash
    /// radius takes the linear falloff damage, the owner scaled by the self multiplier. Splash ignores walls, except a bunker room's (<see cref="BlastReaches"/>). Affected players get knockback.
    /// </summary>
    void Explode(Rocket r, WeaponDef weapon, Vector3 pos, ushort? direct, ServerCar? directCar = null)
    {
        var w = ProjectileMath.WithBlast(weapon, r.Blast); // a gunship's rocket has its own blast (killstreaks.json); the bazooka keeps weapons.json's
        _explosions++;
        if (Vehicles != null) RocketVsVehicles(pos, w, r.Owner, r.Weapon, directCar);
        BlastGlass(pos, w.Splash?.Radius ?? 0);
        BroadcastEvent(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(r.Id, r.Owner, r.Weapon, N(pos), w.Splash?.Radius ?? 0)));
        BroadcastImpact(N(pos), ImpactSize.Plume, body: direct != null && directCar == null, vehicle: directCar != null); // rockets throw up a plume (ticket #154)
        foreach (var t in LiveTargets())
        {
            var isDirect = direct == t.Id;
            if (!isDirect && !BlastReaches(N(pos), t)) continue; // a bunker room's walls are between them
            var dist = HitMath.DistanceToBody(N(pos), t);
            var self = t.Id == r.Owner;
            var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, self);
            if (dmg <= 0) continue;
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(w, N(pos), HitMath.Centre(t), isDirect ? 1f : ProjectileMath.SplashFraction(w, dist));
            PushOnDeath(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = Deal(r.Owner, r.Weapon, t.Id, dmg, false, false);
            ClearPush(t.Id, res);
            if (res == DamageResult.Ignored) continue; // spawn protected: neither damage nor knockback
            if (isDirect) _directHits++; else if (self) _selfHits++; else _splashHits++;
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v) && k != NVec3.Zero) { v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z)); _knocked++; }
        }
    }

    void SendSnapshots()
    {
        var ents = new List<EntityRecord>();
        foreach (var c in _byId.Values) // every joined player, bots included
                ents.Add(EntityRecord.Make(c.Id, EntityType.Player, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z),
                    c.Body.Yaw, c.Body.Pitch, c.Body.Health, c.Car != null && _match.StateOf(c.Id) != EntityState.Dead ? EntityState.InVehicle : _match.StateOf(c.Id), c.Slot, c.Body.State.Stance));
        foreach (var car in _cars) ents.Add(VehicleRecord.Make(car.Id, N(car.GlobalPosition), car.State.Yaw, car.Seat.Driver, car.Colour, Vehicles == null ? 0 : VehicleDamage.Stage(car.Hp, car.Def.Hp, Vehicles.DamageStages), car.Destroyed, car.Lights));
        foreach (var car in _cars) if (car.Def.Flak != null) ents.Add(FlakRecordOf(car)); // a flak truck's turret aim, for everyone's model
        foreach (var car in _cars) if (car.Flies) ents.Add(FlightRecord.Make(car.Id, car.Flight)); // a helicopter's velocity, tilt and rotor (ADR 0006)
        foreach (var car in _cars) if (car.Def.DoorGun != null) ents.Add(DoorGunRecordOf(car)); // a door gun's aim against its airframe, for everyone's model
        ents.AddRange(TargetRecords()); // target dummies (#148)
        foreach (var r in _rockets)
            ents.Add(EntityRecord.Make(r.Id, EntityType.Projectile, N(r.Pos),
                MathF.Atan2(-r.Dir.X, -r.Dir.Z), MathF.Asin(Math.Clamp(r.Dir.Y, -1f, 1f)), 0, EntityState.Alive, r.Weapon));
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            foreach (var pkt in c.Enc.Encode(_tick, c.Ack, c.SnapAck, ents)) Tx(c.Peer!, Wire.ChanData, pkt, 0);
        }
        var world = _match.Entities.Snapshot();
        foreach (var c in _clients.Values)
            if (c.Joined) foreach (var pkt in c.WEnc.Encode(_tick, world)) Tx(c.Peer!, Wire.ChanData, pkt, 0);
        LossLogStates(ents);
        _host.Flush();
    }
}
