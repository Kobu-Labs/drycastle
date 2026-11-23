using Godot;
using Drycastle.Shooter.Bunkers;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Glass;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Scenery;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
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
    sealed class Pending { public NetInput Input; public MoveState After; }

    public const double InterpDelayTicks = 3.0;
    public const float SnapDistance = 2f;
    const float ReconcileEpsilon = 0.03f;

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
    public NetSim? Sim;
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
    /// <summary>Latest owner ammo state from the server (null before the first one).</summary>
    public AmmoMsg? Ammo { get; private set; }
    WeaponViewmodel? _viewmodel;
    double _ammoAt;

    readonly ENetConnection _conn = new();
    ENetPacketPeer? _peer;
    /// <summary>The link to the server (Client.Link): connecting, welcomed, spawned (own snapshot applied), failed, left. Replaces the old welcomed/gotSelf/done flags.</summary>
    readonly StateMachine<LinkPhase> _link = new(ConnectionPhases.Link);
    /// <summary>A welcome was received at some point (it stays true after a failure or a leave: the perf and telemetry output still flush, and Fail tells the menu whether the match was joined).</summary>
    bool _welcomed;
    bool OwnSpawned => _link.Is(LinkPhase.Spawned);
    bool LinkEnded => _link.Current.IsEnded();
    ushort _seq;
    readonly List<Pending> _pending = new();
    /// <summary>Replicated world entities (turrets, missiles, ...). Subscribe to Spawned/Despawned for visuals.</summary>
    public WorldReplica WorldEntities => Ctx.WorldEntities;
    double _lastArrival;
    double _elapsed;
    int _snaps;
    float _maxErr;
    NVec3 _meMin = new(float.MaxValue), _meMax = new(float.MinValue);
    int _deaths;

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

    public NetClient()
    {
        Ctx = new ClientContext(this) { Send = SendEvent, Connected = () => _peer != null, SendJoined = SendNow };
        Ctx.View.OnTurret = () => _gunship != null;
        Ctx.View.ScudScreen = () => ScudScreenOn;
        Ctx.View.Controlling = () => _controlling;
        Ctx.View.Pinned = () => _pinned;
        Ctx.View.Rising = () => _pRising;
        Sandbox = new SandboxClient(Ctx);
        Report = new ReportClient(Ctx, _conn, () => !Bot && !LinkEnded && _welcomed, () => _peer, () => _elapsed);
        _moab = new MoabClient(Ctx);
        _doors = new DoorsClient(Ctx);
        _uav = new UavClient(Ctx);
        _intel = new IntelClient(Ctx);
        _poop = new PoopClient(Ctx);
        _headLamps = new HeadLampClient(Ctx);
        _rozhlas = new RozhlasClient(Ctx, SendMic);
        _blastDust = new BlastDustClient(Ctx);
        _impacts = new ImpactsClient(Ctx);
    }

    public override void _Ready()
    {
        if (Token.Length == 0) Token = Guid.NewGuid().ToString();
        _blips = new BlipsClient(Ctx, BlipConfig);
        _mass = new MassClient(Ctx, HolyMassData, DropPrediction);
        Ctx.View.MassSeated = () => _mass.Seated;
        _citizens = new CitizensClient(Ctx, HolyMassData, _mass);
        _status = new StatusClient(Ctx, Plantation);
        High = new HighClient(Ctx, _status, Plantation, () => Mode, () => OwnSpawned);
        Squads = new SquadsClient(Ctx);
        Chat = new ChatClient(Ctx, ItemData, CompanionData, Squads);
        TeamAdmin = new TeamsClient(Ctx, Chat);
        _minerals = new MineralsClient(Ctx);
        Loadouts = new LoadoutClient(Ctx, PerkData, Chat);
        _streaks = new KillstreaksClient(Ctx, Loadouts);
        _streaks.Used += OnStreakUsed;
        _streaks.Ended += OnStreakEnded;
        _streaks.OwnerDied += ClearStreakViewsOnDeath;
        _streaks.Gunner.HintsChanged += () => _gunDirty = true;
        _session = new SessionLog(Ctx, Host, Port, () => !Bot && !LinkEnded && _welcomed, () => _peer);
        _perf = new PerfLog(Ctx, PerfOut, () => _welcomed, () => _peer, Ctx.Snapshots);
        _glass = new GlassClient(Ctx, GlassData);
        _driveClient = new DriveClient(Ctx, CarThrown, PlaceCarSolid, ScudImmobilises);
        _flight = new FlightClient(Ctx, CarThrown, PlaceCarSolid, _session.FlightCorrection);
        Camo = new CamoClient(Ctx, CamoData, DressCorpses);
        _hunts = new HuntsClient(Ctx, Sandbox, ItemData, CompanionData, AnimalData, AnimalShapes);
        _bot = MakeBot();
        InitCorpses();
        _screens = new ScreensClient(Ctx, Chat, Sandbox, _chooser);
        InitGroundItems();
        InitCarePackages();
        InitMissile();
        InitTurrets();
        InitAnimals();
        InitCompanions();
        InitFlocks();
        InitBigfoot();
        _thieves = new ThievesClient(Ctx, ThievesData, ItemData);
        InitDemon();
        InitAircraft();
        InitLockOn();
        InitFlak();
        InitSeats();
        InitScud();
        InitMatchEdges();
        var err = _conn.CreateHost(1, Wire.Channels, 0, 0);
        if (err != Error.Ok) { Fail($"CreateHost failed: {err}"); return; }
        _peer = _conn.ConnectToHost(Host, Port, Wire.Channels, 0);
        if (_peer == null) { Fail("ConnectToHost failed"); return; }
        GD.Print($"client: connecting to {Host}:{Port} as {PlayerName}");
    }

    public override void _ExitTree()
    {
        _session.Close();
        _conn.Destroy();
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
        SfxTeardown();
        _rozhlas.Reset(); // the mic is shut
        if (_peer != null) { _peer.PeerDisconnect(); _conn.Flush(); _peer = null; }
        foreach (var r in Ctx.Remotes.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        Ctx.Remotes.Clear();
        foreach (var r in _rockets.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        _rockets.Clear();
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
        _missiles.Clear();
        _animals.Clear(); // the nodes are under Owned
        _poop.Reset();
        _citizens.Reset();
        _impacts.Reset();
        _povCam = null;
    }

    void Fail(string why)
    {
        GD.PrintErr($"client: {why}");
        var joined = _welcomed;
        _link.Enter(LinkPhase.Failed);
        if (OnFailed != null) { OnFailed(why, joined); return; }
        if (Bot) GD.Print("SHOOTER_NET_CLIENT_FAIL");
        GetTree().Quit(2);
    }

    /// <summary>Sends one reliable event to the server (<see cref="ClientContext.Send"/>).</summary>
    void SendEvent(EventKind kind, byte[] payload)
    {
        if (_peer == null) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)kind, payload)), (int)ENetPacketPeer.FlagReliable);
    }

    void Tx(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (Sim != null) Sim.Send(peer, chan, data, flags); else peer.Send(chan, data, flags);
    }

    /// <summary>Sends a reliable message at once, flushed; false when not connected or no welcome has come.</summary>
    bool SendNow(ReliableMsg m)
    {
        if (_peer == null || !_welcomed) return false;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
        return true;
    }

    /// <summary>
    /// The player's own Respawn (#284): the server kills them as a suicide and the normal respawn applies the loadout. <paramref name="slot"/> is selected
    /// first (null keeps the active one); unsent loadout edits go out ahead of it, so "apply now" uses what is on screen.
    /// </summary>
    public void Redeploy(int? slot)
    {
        if (_peer == null || !_welcomed || Dead) return;
        Loadouts.FlushAutosave();
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Redeploy, Drycastle.ShooterCore.Match.Redeploy.Encode(slot))), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
    }

    public override void _Process(double delta)
    {
        _session.Frame(delta);
        _perf.Frame(delta);
        Report.Frame(delta);
        if (Sim != null && Sim.Pump()) _conn.Flush(); // delayed packets go out at frame rate, not only on physics ticks
        if (LinkEnded) return;
        _screens.UpdateMapCursor();
        if (_volumes != null) UpdateVolumes();
        var t = ClientProf.Begin();
        Poll();
        t = ClientProf.Lap(ClientProf.S.Net, t);
        if (_link.Is(LinkPhase.Connecting))
        {
            _link.Tick((float)delta);
            if (_link.Elapsed > ConnectionPhases.ConnectTimeoutSeconds) Fail("connect timeout");
            return;
        }
        Loadouts.PumpAutosave(); // loadout saves whose debounce ran out go out here, whichever screen is open (#145)
        RenderPlayersAndProps();
        t = ClientProf.Lap(ClientProf.S.Players, t);
        RenderCreatures(delta);
        t = ClientProf.Lap(ClientProf.S.Creatures, t);
        UpdateDeath(delta);
        UpdatePounce(delta);
        t = ClientProf.Lap(ClientProf.S.View, t);
        RenderVehicles(delta);
        t = ClientProf.Lap(ClientProf.S.Vehicles, t);
        RenderProjectiles(delta);
        t = ClientProf.Lap(ClientProf.S.Projectiles, t);
        UpdateView(delta);
        t = ClientProf.Lap(ClientProf.S.View, t);
        UpdateHud();
        _rozhlas.MicFrame(); // the set holder's mic and the ON AIR cue
        _rozhlasTest?.MicTick(_rozhlas.HoldsSet);
        ClientProf.End(ClientProf.S.Hud, t);
    }

    /// <summary>The players, their head lamps, the corpses, crates, items, minerals and turrets.</summary>
    void RenderPlayersAndProps()
    {
        RenderRemotes();
        _headLamps.Render();
        if (PoseDemo && !_poseDemoDone && Ctx.Me != null) SpawnPoseDemo();
        RenderCorpses();
        RenderGroundItems();
        RenderCarePackages();
        _minerals.Render();
        RenderTurrets();
    }

    void RenderCreatures(double delta)
    {
        RenderAnimals();
        RenderCompanions();
        TickCompanionMarks();
        RenderFlocks(delta);
        RenderBigfoot();
        _citizens.Render();
        _thieves.Render(delta);
        RenderDemon();
    }

    void RenderVehicles(double delta)
    {
        RenderCars();
        RenderHarvester((float)delta);
        RenderAircraft(delta);
    }

    void RenderProjectiles(double delta)
    {
        RenderRockets();
        RenderLockOn();
        RenderLauncherLoaded();
        RenderMissiles();
        RenderMissilePov();
        RenderScud(delta);
    }

    /// <summary>The own view: water, the viewmodel, the camera's shakes and sways, in the order that composes the camera.</summary>
    void UpdateView(double delta)
    {
        RenderWater();
        UpdateViewmodel();
        UpdateThrowArc();
        UpdateDemonQuake(delta);
        _blastDust.Update(); // before the shake: a blast wave that arrives this frame shakes this frame
        Ctx.Shake.Update(delta);
        High.Update(delta); // a high player's sway (NetClient.High.cs), before the roll is composed
        FadeOwnCorrection(delta);
    }

    void Poll()
    {
        for (var guard = 0; guard < 10000; guard++)
        {
            var r = _conn.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev is ENetConnection.EventType.None or ENetConnection.EventType.Error) break;
            var peer = (ENetPacketPeer)r[1].AsGodotObject();
            switch (ev)
            {
                case ENetConnection.EventType.Connect:
                    Tx(peer, Wire.ChanReliable, Wire.Encode(new HelloMsg(Wire.ProtocolVersion, PlayerName, Token, Sandbox.Requested ? HelloFlags.Sandbox : (byte)0, Map?.BuildHash ?? 0)), (int)ENetPacketPeer.FlagReliable);
                    _conn.Flush();
                    break;
                case ENetConnection.EventType.Disconnect:
                    if (!LinkEnded) Fail(_rejectReason ?? "server disconnected");
                    return;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0) OnPacket(peer.GetPacket());
                    break;
            }
        }
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
        Ctx.Me = new Player { Name = "Me", UseCamera = !Bot, AutoSim = false, Authoritative = false, CaptureMouse = !Bot && !Report.PerfPlay, SelfManageMouse = UiShell.Instance == null, KillY = KillY, Water = Water, Ladders = ObjectLadders.WithObjects(Shelters.FromJsonOrEmpty(Map?.BunkersJson, m => GD.PrintErr($"client: {m}; no ladders")).Ladders, Map), Bodies = this };
        World.AddChild(Ctx.Me);
        if (!Bot && ShotFx.Enabled) { _viewmodel = MakeViewmodel(); Ctx.Me.Camera.AddChild(_viewmodel); }
        if (!Bot && ShotFx.Enabled) GlassFx.WarmUp(Ctx.Me.Camera); // the glass shaders compile now, not at the first crack
        if (WeaponsBot != "" || CarBot != "" || FxDemo.StartsWith("vehicle:")) NetServer.AddArenaColliders(World);
        if (Bot) Ctx.Me.InputOverride = new MoveInput { SpeedScale = 1 };
        HudWelcome();
        OnWelcomed?.Invoke();
        GD.Print($"client: welcome, id {Ctx.MyId}, server tick {w.ServerTick}");
    }

    /// <summary>A player left: everything the client keeps per player goes.</summary>
    void OnLeave(LeaveMsg l)
    {
        Ctx.Names.Remove(l.PlayerId); Match.Remove(l.PlayerId); Ctx.Snapshots.Forget(l.PlayerId);
        if (Ctx.Remotes.Remove(l.PlayerId, out var r)) r.Node.QueueFree();
        ForgetWorn(l.PlayerId); _binocularsBy.Remove(l.PlayerId); _headLamps.Forget(l.PlayerId);
        _status.Forget(l.PlayerId);
    }

    /// <summary>One reliable event, to every feature that listens, in this order (some ignore the kinds that are not theirs).</summary>
    void DispatchEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.TargetRoster) OnTargetRoster(payload); // target dummies' names (#148)
        Sandbox.OnEvent(kind, payload); // sandbox answer (#156)
        Chat.OnEvent(kind, payload); // chat lines, the admin flag, the day clock
        _status.OnEvent(kind, payload); // statuses: blessing, curse, burning
        OnItemEvent(kind, payload); // what the player wears, what a corpse carries, an item crumbling
        OnCompanionEvent(kind, payload); // the Companions the player can pick and the one that is out
        _intel.OnEvent(kind, payload); // the announced events: the intel screen, the announcement
        _thieves.OnVoiceEvent(kind, payload); // a Gang member says a line (ticket 09)
        OnBigfootEvent(kind, payload); // the Bigfoot's effects and the footprints to its corpse (#264)
        OnHarvesterEvent(kind, payload); // the harvester's cut, its bales' fire (#331)
        _thieves.OnEvent(kind, payload); // the Raid vans' honk
        _doors.OnEvent(kind, payload); // the church doors, the Obecny Urad's door and the bus garage's doors
        _rozhlas.OnEvent(kind, payload); // the village loudspeaker's phase, broadcast and offset
        _mass.OnEvent(kind, payload); // the Holy Mass: chant, bells, the player's seat, the blessing
        OnDemonEvent(kind, payload); // the demon: fireballs, the curse
        _hunts.OnEvent(kind, payload); // the Hunter's Log: answers and announcements (#336)
        OnPhoenixEvent(kind, payload); // the Phoenix's fall: a flash and the crash site's warning light
        _minerals.OnEvent(kind, payload); // the mineral nodes and the player's finds (#401)
        OnWeaponEvent(kind, payload);
        OnStreetEvent(kind, payload); // the street lamps and sign plates
        OnMatchEvent(kind, payload);
        Loadouts.OnEvent(kind, payload);
        _streaks.OnEvent(kind, payload);
        _uav.OnEvent(kind, payload);
        Camo.OnEvent(kind, payload);
        WorldEntities.OnEvent(kind, payload);
        _perf.OnEvent(kind, payload);
        _bot?.OnEvent(kind, payload);
        EventReceived?.Invoke(kind, payload);
    }


    void OnMatchEvent(EventKind kind, byte[] payload)
    {
        if (!Match.Apply(kind, payload, Ctx.Now)) return;
        switch (kind)
        {
            case EventKind.MatchState:
                Ctx.Hud?.SetTeams(Match);
                Ctx.Hud?.SetMatch(Match.Phase, Match.TimeRemaining(Ctx.Now), Match.Rows, Match.Winners, Match.NameOf);
                break;
            case EventKind.Kill:
                var k = Match.KillFeed[^1];
                Ctx.Hud?.AddKill(k, Match.NameOf(k.Killer), Match.NameOf(k.Victim));
                _streaks.OnKill(k);
                GD.Print($"client {PlayerName}: kill feed: {Match.NameOf(k.Killer)} -> {Match.NameOf(k.Victim)} (weapon {k.Weapon})");
                break;
            case EventKind.Spawn:
                var sp = Match.LatestSpawn!; // the spawn of this event; the per-player table's last entry is another player's
                GD.Print($"client {PlayerName}: spawn #{sp.Id} protected {sp.Protection:F1}s");
                if (sp.Id == Ctx.MyId && Ctx.Me != null)
                {
                    Loadouts.ApplySpawn(); _streaks.Arm(); // the hotbar and perk the server applied at this spawn
                    Ctx.Hotbar.Want = Ctx.Hotbar.Start;
                    Ctx.Dead = false; _blastDust.ClearRinging(); if (OwnSpawned) _link.Enter(LinkPhase.Welcomed); _pending.Clear(); _viewFix = Vector3.Zero; // the next snapshot carries the spawn position
                    _bot?.OnSpawn(sp.Protection);
                    EndDeathView(); Ctx.Hud?.ClearTransients(); Ctx.Hud?.SetDead(false); Ctx.Hud?.SetProtection(sp.Protection); _spread.Reset(); _recoil = new RecoilState((ulong)Ctx.MyId); _breath = new BreathState((ulong)Ctx.MyId); _breathCues.Reset(); _zero.Reset(); ClearPredictedReload(); _pNextFire = 0;
                }
                break;
            case EventKind.Death:
                var d = Match.LatestDeath!;
                GD.Print($"client {PlayerName}: death #{d.Victim} by #{d.Killer}, respawn in {d.RespawnDelay:F1}s");
                SfxDeath(d);
                NoteDeath(d);
                if (d.Victim == Ctx.MyId) { _deaths++; _bot?.OnOwnDeath(); BeginDeathView(d.RespawnDelay, d.Flight); Ctx.Dead = true; ClearPredictedReload(); Ctx.Hud?.SetDead(true, d.Killer == d.Victim ? "" : Match.NameOf(d.Killer), d.RespawnDelay, fadeIn: true); Ctx.Hud?.SetProtection(0); _streaks.OnDeath(); }
                break;
        }
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
        foreach (var id in Ctx.Snapshots.Removed) { if (Ctx.Remotes.Remove(id, out var gone)) gone.Node.QueueFree(); if (Ctx.Cars.Remove(id)) { _flight.Remove(id); _driveClient.Remove(id); } }
        foreach (var (id, rec) in Ctx.Snapshots.States)
        {
            if (id == Ctx.MyId) { OnSelf(rec, h.Ack); continue; }
            if (rec.Type == EntityType.Projectile) { OnRocketState(id, rec); continue; }
            if (rec.Type == EntityType.Vehicle) { OnVehicleState(id, rec, h.Tick); continue; }
            if (rec.Type == EntityType.Seats) { OnSeatsState(rec); continue; }
            if (rec.Type == EntityType.Turret) { OnTurretState(rec, h.Tick); continue; }
            if (rec.Type == EntityType.Flight) { _flight.OnState(rec, h.Tick); continue; }
            if (rec.Type == EntityType.Drive) { _driveClient.OnState(rec, h.Tick); continue; }
            if (rec.Type is not (EntityType.Player or EntityType.Target)) continue;
            if (!Ctx.Remotes.TryGetValue(id, out var r)) { r = MakeRemote(id, rec.Type == EntityType.Target); Ctx.Remotes[id] = r; _status.SyncFx(id); }
            r.State = rec.State; r.Stance = rec.Stance; r.Slot = rec.Weapon;
            r.Buf.Push(h.Tick, rec.Position, rec.YawRad, rec.PitchRad);
            r.Min = NVec3.Min(r.Min, rec.Position); r.Max = NVec3.Max(r.Max, rec.Position);
        }
        _flight.Update(h.Ack); // the helicopter this player flies is predicted, and reconciled here (ADR 0006)
        _driveClient.Update(h.Ack);  // and so is the amphibious transport this player drives (ADR 0010)
        _bot?.AfterSnapshot(h);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (LinkEnded || !_welcomed || Ctx.Me == null || _peer == null) return;
        _elapsed += delta;
        _rozhlasTest?.Tick();
        if (_bot != null && _elapsed >= BotSeconds && _bot.TimedOut()) return;
        if (!OwnSpawned) return; // wait for our spawn position
        DemonBodyTick(); // the demon's Body capsule is where the server last put it, before the body is predicted
        var pt = ClientProf.Begin();
        ReplayReconcile(); // C-02: a snapshot's reconcile replays here, with the physics step (see OnSelf)
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
        if (!Bot) { ReadSlotKeys(); ApplyScope(); TickGroundItems(); TickCorpses(dt); TickCarePackages(dt); _thieves.Loot.TickReturn(dt); _minerals.Tick(dt); }
        FxDemoInput(ref mi);
        ScudFilterInput(ref mi); // a Scud screen's keys are the screen's: no driving, no weapon
        ThrowFilterInput(ref mi); // G throws a grenade only where it is not the Scud screen's key (#420)
        _aimHeld = mi.Has(MoveButtons.Aim);
        if (HeadLookWanted()) mi.Buttons |= MoveButtons.HeadLook; // seated and looking out of their own eyes: the view is the head's, shown to everyone (ADR 0026)
        TickRecoil(mi);
        TickBreath(mi);
        return mi;
    }

    /// <summary>Numbers the input and predicts it: the own body, and the helicopter or the amphibious transport the player steers. The input waits in <see cref="_pending"/> for the server's ack.</summary>
    void PredictTick(MoveInput mi, float dt)
    {
        if (_gunship == null) PredictFire(mi); // rounds from a turret are the server's alone
        var sentPitch = GunnerLook(ref mi); // at the turret the aim is sent (ground-stabilised, #160), and the player's own look is left alone
        if (_gunship == null) Ctx.Me!.Yaw = mi.Yaw;
        var ni = NetInput.From(++_seq, mi, sentPitch, (byte)(_gunship != null ? TurretKey() : Weapons != null ? Ctx.Hotbar.Want : 0), GunnerAim);
        // C-03: a dead body does not move on the server (its input is zero and it is skipped), so it is not predicted either; else each snapshot snapped it back
        if (Mode.BodyIsPredicted) { if (!Dead) Ctx.Me!.Simulate(ni.ToMove(SpeedFor(ni.Slot)), dt); SfxOwnMovement(dt); } // a steered missile or a gunship: the body stands still; a turret's body moves
        _flight.Predict(ni, dt); // the pilot of a helicopter steps it with the input the server will step it with
        _driveClient.Predict(ni, dt);  // and so does the driver of an amphibious transport
        _pending.Add(new Pending { Input = ni, After = Ctx.Me!.State });
        if (_pending.Count > 120) _pending.RemoveAt(0);
    }

    /// <summary>Sends the newest three inputs (loss cover); they name the entity and world ticks they follow (the server's delta baselines).</summary>
    void SendInputs()
    {
        var recent = new NetInput[Math.Min(3, _pending.Count)];
        for (var i = 0; i < recent.Length; i++) recent[i] = _pending[_pending.Count - recent.Length + i].Input;
        Tx(_peer!, Wire.ChanData, Wire.EncodeInputs(recent, Ctx.Snapshots.AckTick, WorldEntities.AckTick), 0);
        _conn.Flush();
    }

    // ---- weapons: input, events, placeholder effects (the HUD lives in NetClient.Hud.cs) ----

    public override void _UnhandledInput(InputEvent e)
    {
        if (Chat.HandleInput(e)) return; // T or "/": the chat box
        if (_screens.HandleInput(e)) return; // M: the tactical map, F: the Equipment screen (#268), J: the Hunter's Log (#336), N: the Mineral Log
        if (Sandbox.HandleInput(e)) return; // F4, the sandbox panel (#156)
        if (HandleVolumeInput(e)) return; // F3, the bounding-volume overlay; [ ] step its mode (#479)
        if (HandleScudInput(e)) return; // the Scud screen's keys (open, confirm, abort, lock, the wheel's zoom)
        if (HandleSeatInput(e)) return; // a number key while seated takes that seat
        if (HandleViewInput(e)) return; // the vehicle view key while driving: chase camera or cockpit
        if (HandleBinocularsInput(e)) return; // B: the binoculars from the bag
        if (Bot || Weapons == null || Dead || ChooserOpen || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        if (e is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (DialZero(b.ButtonIndex == MouseButton.WheelUp ? 1 : -1)) return; // a scope with bullet drop is up: the wheel is its zero dial
            Ctx.Hotbar.Want = Ctx.Hotbar.Cycle(Ctx.Hotbar.Want, b.ButtonIndex == MouseButton.WheelDown ? 1 : -1);
        }
    }

}
