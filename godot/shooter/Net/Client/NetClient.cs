using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Bunkers;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Glass;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Scenery;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Perks;
using Drycastle.ShooterCore.Progression;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Scenery;
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
    sealed class Pending { public NetInput Input; public MoveState After; }

    public const double InterpDelayTicks = 3.0;
    public const float SnapDistance = 2f;
    const float ReconcileEpsilon = 0.03f;

    /// <summary>What the features of this client may use of the rest of it (see <see cref="ClientContext"/>); the plain members below forward to it.</summary>
    public ClientContext Ctx { get; }
    public Node3D World { get => Ctx.World; set => Ctx.World = value; }
    Node3D Owned => Ctx.Owned;
    public string Host = "127.0.0.1";
    public int Port = 27015;
    public string PlayerName { get => Ctx.PlayerName; set => Ctx.PlayerName = value; }
    /// <summary>Player token sent in the hello (ADR 0003). Empty = a fresh one is generated (bots and CLI clients).</summary>
    public string Token = "";
    /// <summary>The server's profile for this player (level, XP, loadouts, active slot); null until it arrives.</summary>
    public ProfileMsg? Profile { get; private set; }
    /// <summary>Raised for an error the server sends after the join (for example an invalid loadout).</summary>
    public event Action<string>? ServerError;
    string? _rejectReason;
    public bool Bot { get => Ctx.Bot; set => Ctx.Bot = value; }
    /// <summary>Network impairment for the loss and latency test (--net-sim); null = none.</summary>
    public NetSim? Sim;
    public bool Headless;
    public float KillY = float.NegativeInfinity;
    /// <summary>The map's water (water.json), shared with the prediction so swimming matches the server.</summary>
    public Drycastle.ShooterCore.Waters.Water Water = Drycastle.ShooterCore.Waters.Water.Empty;
    /// <summary>Match events stored for the HUD to come: phase, countdown, scoreboard rows, kill feed, own spawn/death.</summary>
    public ClientMatchState Match => Ctx.Match;
    public bool Dead => Ctx.Dead;

    /// <summary>
    /// The mode flags (dead, in a vehicle, at a turret, steering the missile, held by the Mass, the demon or the Bigfoot) as one value to ask named questions of
    /// (<see cref="ClientModeInputs"/>); build it where it is used, it is a few bools. <c>InArmedSeat</c> is only ever true while driving (<see cref="MySeat"/> needs it).
    /// </summary>
    ClientModeInputs Mode => new(Dead, _driving, _gunship != null || ScudScreenOn, _controlling, _mass.Seated, _pinned, _heldByBigfoot, _pRising, _myState == EntityState.InVehicle, _driving && InArmedSeat);
    /// <summary>holy_mass.json (set by the host): the bodies of the citizens and the Mass's chant.</summary>
    public HolyMassFile HolyMassData = new();
    /// <summary>weapons.json: hotbar order and the per-weapon movement speed multiplier used by prediction.</summary>
    public WeaponsFile Weapons { get => Ctx.Weapons!; set => Ctx.Weapons = value; }
    /// <summary>match.json as the client shipped it: the gunfire blips' radius, lifetime and rate limit (null: no blips, e.g. headless bots).</summary>
    public MatchConfig? BlipConfig;
    /// <summary>Latest owner ammo state from the server (null before the first one).</summary>
    public AmmoMsg? Ammo { get; private set; }
    sealed class Rocket { public double Born; public Node3D Node = null!; public float Tumble; public NVec3 Pos, Dir; public double At; public float Speed; public LoopVoice? Snd; /* a homing missile (NetClient.LockOn.cs): the Aircraft it closes on and when it gets there */ public bool Homing; public ushort Target; public double Arrive; /* a thrown grenade: drawn falling along its arc from the last snapshot */ public bool Grenade; public float Gravity; }
    readonly Dictionary<ushort, Rocket> _rockets = new();
    readonly HashSet<ushort> _deadRockets = new();
    int _wantSlot = 1;
    WeaponViewmodel? _viewmodel;
    double _ammoAt;

    readonly ENetConnection _conn = new();
    ENetPacketPeer? _peer;
    Player? _me { get => Ctx.Me; set => Ctx.Me = value; }
    ushort _myId { get => Ctx.MyId; set => Ctx.MyId = value; }
    /// <summary>The link to the server (Client.Link): connecting, welcomed, spawned (own snapshot applied), failed, left. Replaces the old welcomed/gotSelf/done flags.</summary>
    readonly StateMachine<LinkPhase> _link = new(ConnectionPhases.Link);
    /// <summary>A welcome was received at some point (it stays true after a failure or a leave: the perf and telemetry output still flush, and Fail tells the menu whether the match was joined).</summary>
    bool _welcomed;
    bool OwnSpawned => _link.Is(LinkPhase.Spawned);
    bool LinkEnded => _link.Current.IsEnded();
    ushort _seq;
    readonly List<Pending> _pending = new();
    readonly SnapshotDecoder _dec = new();
    /// <summary>Replicated world entities (turrets, missiles, ...). Subscribe to Spawned/Despawned for visuals.</summary>
    public WorldReplica WorldEntities => Ctx.WorldEntities;
    readonly Dictionary<ushort, string> _names = new();
    Dictionary<ushort, RemotePlayer> _remotes => Ctx.Remotes;
    uint _lastTick { get => Ctx.LastTick; set => Ctx.LastTick = value; }
    double _lastArrival;
    /// <summary>The render clock of the remote entities (A-06): a filtered estimate of the server's tick, which never runs backwards.</summary>
    RenderClock _clock => Ctx.Clock;
    double _elapsed;
    int _recon, _snaps;
    float _maxErr;
    NVec3 _meMin = new(float.MaxValue), _meMax = new(float.MinValue);
    EntityState _myState;
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
    MassClient _mass = null!;
    CitizensClient _citizens = null!;
    StatusClient _status = null!;

    public NetClient()
    {
        Ctx = new ClientContext(this) { Send = SendEvent, InVehicle = () => _driving || _myState == EntityState.InVehicle };
        _moab = new MoabClient(Ctx);
        _doors = new DoorsClient(Ctx);
        _uav = new UavClient(Ctx);
        _intel = new IntelClient(Ctx);
        _poop = new PoopClient(Ctx);
        _headLamps = new HeadLampClient(Ctx);
        _rozhlas = new RozhlasClient(Ctx, SendMic);
        _blastDust = new BlastDustClient(Ctx);
    }

    public override void _Ready()
    {
        if (Token.Length == 0) Token = Guid.NewGuid().ToString();
        _blips = new BlipsClient(Ctx, BlipConfig);
        _mass = new MassClient(Ctx, HolyMassData, DropPrediction);
        _citizens = new CitizensClient(Ctx, HolyMassData, _mass);
        _status = new StatusClient(Ctx, Plantation);
        _bot = MakeBot();
        InitCorpses();
        InitGroundItems();
        InitCarePackages();
        InitMissile();
        InitTurrets();
        InitAnimals();
        InitCompanions();
        InitFlocks();
        InitBigfoot();
        InitThieves();
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
        TelemetryClose();
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
        TelemetryClose();
        SfxTeardown();
        _rozhlas.Reset(); // the mic is shut
        if (_peer != null) { _peer.PeerDisconnect(); _conn.Flush(); _peer = null; }
        foreach (var r in _remotes.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        _remotes.Clear();
        foreach (var r in _rockets.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        _rockets.Clear();
        foreach (var v in _cars.Values) if (GodotObject.IsInstanceValid(v.Node)) v.Node.QueueFree();
        _cars.Clear(); _flights.Clear(); _pilot = null; _drives.Clear(); _drive = null; // the probes are under Owned and go with them
        GlassTeardown();
        if (_me != null && GodotObject.IsInstanceValid(_me)) _me.QueueFree();
        _me = null;
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
        _impacts = null; Array.Clear(_impactNext);
        _rings = null; _ringNext = 0; _ringMesh = null;
        _dust = null; _dustNext = 0;
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

    void Tx(ENetPacketPeer peer, int chan, byte[] data, int flags)
    {
        if (Sim != null) Sim.Send(peer, chan, data, flags); else peer.Send(chan, data, flags);
    }

    /// <summary>Asks the server to replace a loadout slot. The server answers with a <see cref="LoadoutAckInfo"/> (matched by seq) and the new profile. False when not connected.</summary>
    public bool SaveLoadout(Loadout loadout, uint seq)
    {
        if (_peer == null || !_welcomed) return false;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new SaveLoadoutMsg(loadout, seq)), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
        return true;
    }

    /// <summary>Asks the server to make a loadout slot the active one. Answered like <see cref="SaveLoadout"/>.</summary>
    public void SelectSlot(int slot)
    {
        if (_peer == null || !_welcomed) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new SelectSlotMsg(slot)), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
    }

    /// <summary>
    /// The player's own Respawn (#284): the server kills them as a suicide and the normal respawn applies the loadout. <paramref name="slot"/> is selected
    /// first (null keeps the active one); unsent loadout edits go out ahead of it, so "apply now" uses what is on screen.
    /// </summary>
    public void Redeploy(int? slot)
    {
        if (_peer == null || !_welcomed || Dead) return;
        FlushAutosave();
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Redeploy, Drycastle.ShooterCore.Match.Redeploy.Encode(slot))), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
    }

    public override void _Process(double delta)
    {
        TelemetryFrame(delta);
        PerfClientFrame(delta);
        ReportFrame(delta);
        if (Sim != null && Sim.Pump()) _conn.Flush(); // delayed packets go out at frame rate, not only on physics ticks
        if (LinkEnded) return;
        UpdateMapCursor();
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
        PumpAutosave(); // loadout saves whose debounce ran out go out here, whichever screen is open (#145)
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
        if (PoseDemo && !_poseDemoDone && _me != null) SpawnPoseDemo();
        RenderCorpses();
        RenderGroundItems();
        RenderCarePackages();
        RenderMinerals();
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
        RenderGang(delta);
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
        UpdateNausea(delta); // a high player's sway (NetClient.High.cs), before the roll is composed
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
                    Tx(peer, Wire.ChanReliable, Wire.Encode(new HelloMsg(Wire.ProtocolVersion, PlayerName, Token, SandboxRequested ? HelloFlags.Sandbox : (byte)0, Map?.BuildHash ?? 0)), (int)ENetPacketPeer.FlagReliable);
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
            ReportEvent(m is EventMsg e ? e.Kind : (byte)0, ClientProf.Move(ClientProf.S.Net, ClientProf.S.NetEvents, t));
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
                Profile = p;
                OnProfileReceived(p);
                GD.Print($"client: profile {p.Name}, level {p.Level}, xp {p.Xp}, active slot {p.ActiveSlot}");
                break;
            case ErrorMsg er:
                GD.PrintErr($"client: server error: {er.Reason}");
                if (!_welcomed) _rejectReason = er.Reason; // the server disconnects after a refused hello
                ServerError?.Invoke(er.Reason);
                break;
            case JoinMsg j: _names[j.PlayerId] = j.Name; GD.Print($"client: {j.Name} #{j.PlayerId} joined"); break;
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
        _myId = w.PlayerId; _welcomed = true; _link.Enter(LinkPhase.Welcomed);
        _rozhlas.Reset(); Rozhlas.Reset(); // a new connection starts with the loudspeaker Idle until the server says otherwise
        DayNightCycle.Current?.SetServerClock(new DayClock { StartMinute = w.StartMinute, DayLengthSeconds = w.DayLengthMs / 1000.0, Frozen = w.Frozen }, w.ServerTick);
        CasingFx.Water = Water;
        _me = new Player { Name = "Me", UseCamera = !Bot, AutoSim = false, Authoritative = false, CaptureMouse = !Bot && !PerfPlay, SelfManageMouse = UiShell.Instance == null, KillY = KillY, Water = Water, Ladders = ObjectLadders.WithObjects(Shelters.FromJsonOrEmpty(Map?.BunkersJson, m => GD.PrintErr($"client: {m}; no ladders")).Ladders, Map), Bodies = this };
        World.AddChild(_me);
        if (!Bot && ShotFx.Enabled) { _viewmodel = MakeViewmodel(); _me.Camera.AddChild(_viewmodel); }
        if (!Bot && ShotFx.Enabled) GlassFx.WarmUp(_me.Camera); // the glass shaders compile now, not at the first crack
        if (WeaponsBot != "" || CarBot != "" || FxDemo.StartsWith("vehicle:")) NetServer.AddArenaColliders(World);
        if (Bot) _me.InputOverride = new MoveInput { SpeedScale = 1 };
        HudWelcome();
        OnWelcomed?.Invoke();
        GD.Print($"client: welcome, id {_myId}, server tick {w.ServerTick}");
    }

    /// <summary>A player left: everything the client keeps per player goes.</summary>
    void OnLeave(LeaveMsg l)
    {
        _names.Remove(l.PlayerId); Match.Remove(l.PlayerId); _dec.Forget(l.PlayerId);
        if (_remotes.Remove(l.PlayerId, out var r)) r.Node.QueueFree();
        ForgetWorn(l.PlayerId); _binocularsBy.Remove(l.PlayerId); _headLamps.Forget(l.PlayerId);
        _status.Forget(l.PlayerId);
    }

    /// <summary>One reliable event, to every feature that listens, in this order (some ignore the kinds that are not theirs).</summary>
    void DispatchEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.TargetRoster) OnTargetRoster(payload); // target dummies' names (#148)
        OnSandboxEvent(kind, payload); // sandbox answer (#156)
        OnChatEvent(kind, payload); // chat lines, the admin flag, the day clock
        _status.OnEvent(kind, payload); // statuses: blessing, curse, burning
        OnItemEvent(kind, payload); // what the player wears, what a corpse carries, an item crumbling
        OnCompanionEvent(kind, payload); // the Companions the player can pick and the one that is out
        _intel.OnEvent(kind, payload); // the announced events: the intel screen, the announcement
        OnThiefAudioEvent(kind, payload); // a Gang member says a line (ticket 09)
        OnBigfootEvent(kind, payload); // the Bigfoot's effects and the footprints to its corpse (#264)
        OnHarvesterEvent(kind, payload); // the harvester's cut, its bales' fire (#331)
        OnThievesEvent(kind, payload); // the Raid vans' honk
        _doors.OnEvent(kind, payload); // the church doors, the Obecny Urad's door and the bus garage's doors
        _rozhlas.OnEvent(kind, payload); // the village loudspeaker's phase, broadcast and offset
        _mass.OnEvent(kind, payload); // the Holy Mass: chant, bells, the player's seat, the blessing
        OnDemonEvent(kind, payload); // the demon: fireballs, the curse
        OnHuntEvent(kind, payload); // the Hunter's Log: answers and announcements (#336)
        OnPhoenixEvent(kind, payload); // the Phoenix's fall: a flash and the crash site's warning light
        OnMineralEvent(kind, payload); // the mineral nodes and the player's finds (#401)
        OnWeaponEvent(kind, payload);
        OnMatchEvent(kind, payload);
        OnProgressionEvent(kind, payload);
        _uav.OnEvent(kind, payload);
        OnAppearanceEvent(kind, payload);
        WorldEntities.OnEvent(kind, payload);
        PerfClientEvent(kind, payload);
        _bot?.OnEvent(kind, payload);
        EventReceived?.Invoke(kind, payload);
    }

    double Now => Time.GetTicksMsec() / 1000.0;

    void OnMatchEvent(EventKind kind, byte[] payload)
    {
        if (!Match.Apply(kind, payload, Now)) return;
        switch (kind)
        {
            case EventKind.MatchState:
                _hud?.SetTeams(Match);
                _hud?.SetMatch(Match.Phase, Match.TimeRemaining(Now), Match.Rows, Match.Winners, Match.NameOf);
                break;
            case EventKind.Kill:
                var k = Match.KillFeed[^1];
                _hud?.AddKill(k, Match.NameOf(k.Killer), Match.NameOf(k.Victim));
                OnStreakKill(k);
                GD.Print($"client {PlayerName}: kill feed: {Match.NameOf(k.Killer)} -> {Match.NameOf(k.Victim)} (weapon {k.Weapon})");
                break;
            case EventKind.Spawn:
                var sp = Match.LatestSpawn!; // the spawn of this event; the per-player table's last entry is another player's
                GD.Print($"client {PlayerName}: spawn #{sp.Id} protected {sp.Protection:F1}s");
                if (sp.Id == _myId && _me != null)
                {
                    ApplySpawnLoadout(); // the hotbar and perk the server applied at this spawn
                    _wantSlot = StartSlot();
                    Ctx.Dead = false; _blastDust.ClearRinging(); if (OwnSpawned) _link.Enter(LinkPhase.Welcomed); _pending.Clear(); _viewFix = Vector3.Zero; // the next snapshot carries the spawn position
                    _bot?.OnSpawn(sp.Protection);
                    EndDeathView(); _hud?.ClearTransients(); _hud?.SetDead(false); _hud?.SetProtection(sp.Protection); _spread.Reset(); _recoil = new RecoilState((ulong)_myId); _breath = new BreathState((ulong)_myId); _breathCues.Reset(); _zero.Reset(); ClearPredictedReload(); _pNextFire = 0;
                }
                break;
            case EventKind.Death:
                var d = Match.LatestDeath!;
                GD.Print($"client {PlayerName}: death #{d.Victim} by #{d.Killer}, respawn in {d.RespawnDelay:F1}s");
                SfxDeath(d);
                NoteDeath(d);
                if (d.Victim == _myId) { _deaths++; _bot?.OnOwnDeath(); BeginDeathView(d.RespawnDelay, d.Flight); Ctx.Dead = true; ClearPredictedReload(); _hud?.SetDead(true, d.Killer == d.Victim ? "" : Match.NameOf(d.Killer), d.RespawnDelay, fadeIn: true); _hud?.SetProtection(0); OnStreakDeath(); }
                break;
        }
    }

    /// <summary>Raised for every reliable event envelope (shot, hit, kill, ... handled by later issues).</summary>
    public event Action<EventKind, byte[]>? EventReceived;

    void OnSnapshot(byte[] d)
    {
        if (_me == null || !_dec.Apply(d, out var h)) return;
        if (_lastTick != 0 && h.Tick < _lastTick) return;
        _bot?.OnSnapshot(h);
        PerfClientSnapshot();
        TelemetrySnapshot(h.Tick);
        ReportSnapshot(h.Tick);
        _lastTick = h.Tick; _lastArrival = Time.GetTicksMsec() / 1000.0; _snaps++;
        _clock.OnSnapshot(h.Tick, _lastArrival);
        DayNightCycle.Current?.SetServerTick(h.Tick);
        foreach (var id in _dec.Removed) { if (_remotes.Remove(id, out var gone)) gone.Node.QueueFree(); if (_cars.Remove(id, out var cg)) { cg.Snd?.Release(); cg.Engine?.Release(); cg.Node.QueueFree(); cg.Solid?.QueueFree(); _flights.Remove(id); _drives.Remove(id); _seatOccupants.Remove(id); } }
        foreach (var (id, rec) in _dec.States)
        {
            if (id == _myId) { OnSelf(rec, h.Ack); continue; }
            if (rec.Type == EntityType.Projectile) { OnRocketState(id, rec); continue; }
            if (rec.Type == EntityType.Vehicle) { OnVehicleState(id, rec, h.Tick); continue; }
            if (rec.Type == EntityType.Seats) { OnSeatsState(rec); continue; }
            if (rec.Type == EntityType.Turret) { OnTurretState(rec, h.Tick); continue; }
            if (rec.Type == EntityType.Flight) { OnFlightState(rec, h.Tick); continue; }
            if (rec.Type == EntityType.Drive) { OnDriveState(rec, h.Tick); continue; }
            if (rec.Type is not (EntityType.Player or EntityType.Target)) continue;
            if (!_remotes.TryGetValue(id, out var r)) { r = MakeRemote(id, rec.Type == EntityType.Target); _remotes[id] = r; _status.SyncFx(id); }
            r.State = rec.State; r.Stance = rec.Stance; r.Slot = rec.Weapon;
            r.Buf.Push(h.Tick, rec.Position, rec.YawRad, rec.PitchRad);
            r.Min = NVec3.Min(r.Min, rec.Position); r.Max = NVec3.Max(r.Max, rec.Position);
        }
        UpdatePiloting(h.Ack); // the helicopter this player flies is predicted, and reconciled here (ADR 0006)
        UpdateDriving(h.Ack);  // and so is the amphibious transport this player drives (ADR 0010)
        _bot?.AfterSnapshot(h);
    }

    /// <summary>Rockets are drawn at the newest snapshot position and dead-reckoned along their direction (a 100 ms interpolation delay would make them vanish 4 m short of the blast).</summary>
    void OnRocketState(ushort id, EntityRecord rec)
    {
        if (_deadRockets.Contains(id)) return;
        var dir = HitMath.Forward(rec.YawRad, rec.PitchRad);
        // the speed is the launch's (set there); a snapshot before the launch falls back to the weapon's, and the launch corrects it
        if (!_rockets.TryGetValue(id, out var r))
        {
            var speed = Weapons != null && rec.Weapon < Weapons.Weapons.Count ? Weapons.Weapons[rec.Weapon].ProjectileSpeed : 40f;
            r = new Rocket { Node = RocketFx.Make(Owned, WeaponId(rec.Weapon)), Born = Now, Speed = speed }; _rockets[id] = r;
            r.Gravity = GrenadeGravity(rec.Weapon); r.Grenade = r.Gravity > 0;
        }
        if (r.Grenade) r.Speed = rec.Health / NetServer.GrenadeSpeedScale; // a grenade's speed rides the health byte
        r.Pos = rec.Position; r.Dir = dir; r.At = Now;
    }

    /// <summary>The fall of a thrown weapon (weapons.json grenade.gravity, m/s²); 0 for a rocket.</summary>
    float GrenadeGravity(int weapon) => Weapons != null && weapon >= 0 && weapon < Weapons.Weapons.Count ? Weapons.Weapons[weapon].Grenade?.Gravity ?? 0f : 0f;

    void RenderRockets()
    {
        var camAt = GetViewport()?.GetCamera3D()?.GlobalPosition;
        foreach (var r in _rockets.Values)
        {
            if (r.Node == null) continue;
            if (r.Homing) StepHoming(r);
            var dtp = (float)Math.Min(0.1, Now - r.At);
            var falls = r.Speed > GrenadeFlight.RestSpeed; // a grenade lying still does not keep falling between snapshots (it sank up to 10 cm into the road, #421)
            var p = r.Homing ? r.Pos : r.Pos + r.Dir * (r.Speed * dtp) - NVec3.UnitY * (falls ? 0.5f * r.Gravity * dtp * dtp : 0f); // a grenade falls between snapshots
            // visual only: the server rocket starts at the eye; draw it leaving the muzzle (0.8 m ahead, right and down) and blend onto the true path
            var k = Mathf.Max(0f, 1f - (float)(Now - r.Born) / 0.35f);
            var d = new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z);
            var side = d.Cross(Vector3.Up).Normalized();
            r.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z) + (d * 0.8f + side * 0.25f + Vector3.Down * 0.2f) * k;
            if (r.Grenade)
            {
                // it tumbles while it moves, and lies on its side when it stops (upright, its lower half would sink into the road ribbons floating 2-8 cm over the ground)
                var spin = (float)GetProcessDeltaTime();
                r.Tumble = r.Speed > GrenadeFlight.RestSpeed ? r.Tumble + 9f * spin : Mathf.MoveToward(r.Tumble, MathF.Round((r.Tumble - Mathf.Pi / 2) / Mathf.Pi) * Mathf.Pi + Mathf.Pi / 2, 8f * spin);
                r.Node.Rotation = new Vector3(r.Tumble, 0, 0);
                continue; // no trail, no flight loop
            }
            SfxRocket(r, r.Node.GlobalPosition);
            if (camAt is { } eye) RocketFx.GateTrail(r.Node, eye);
            if (Mathf.Abs(r.Dir.Y) < 0.99f) r.Node.LookAt(r.Node.GlobalPosition + new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z));
        }
    }

    RemotePlayer MakeRemote(ushort id, bool target = false)
    {
        var node = new Node3D { Name = $"Remote{id}" };
        var view = CharacterView.Make();
        if (Appearances.TryGetValue(id, out var look)) view.SetCamo(look, CamoData); // the look may arrive before the first snapshot (#140)
        DressPlayer(view, id); // what they wear (#266), likewise
        node.AddChild(view);
        node.AddChild(new Label3D
        {
            Text = _names.TryGetValue(id, out var n) ? NameTag(id, n) : $"#{id}", // [SANDBOX] tag for sandbox players (#156)
            Modulate = target ? TargetTint : Colors.White, // orange name tag for target dummies (#148)
            Position = new Vector3(0, MoveConsts.CapsuleHeight + 0.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.005f, NoDepthTest = true,
        });
        World.AddChild(node);
        return new RemotePlayer { Node = node, View = view, Binoculars = _binocularsBy.GetValueOrDefault(id) };
    }

    void OnSelf(EntityRecord rec, ushort ack)
    {
        var sp = rec.Position;
        if (!OwnSpawned)
        {
            _link.Enter(LinkPhase.Spawned);
            _me!.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y });
            _viewFix = Vector3.Zero;
            return;
        }
        _me!.Health = rec.Health;
        _myState = rec.State;
        var inCar = rec.State == EntityState.InVehicle;
        if (inCar != _driving)
        {
            // entering or leaving a car: no prediction while driving; on exit take the server's position (the free side) as is
            _driving = inCar; _pending.Clear(); _reconcile = null; _me.InVehicle = inCar;
            if (!inCar) { _reconcile = null; _viewFix = Vector3.Zero; _me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); _me.EndChase(_carYaw); ClearSeats(); }
            return;
        }
        if (Mode.HeldOnFoot) { _pending.Clear(); _reconcile = null; _viewFix = Vector3.Zero; _me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); return; } // in the Bigfoot's hand: the body is where the server puts it, nothing is predicted (#264)
        if (Mode.ServerHoldsBody) { _pending.Clear(); _reconcile = null; _me.SetState(new MoveState { Position = sp, OnFloor = true, FallPeakY = sp.Y }); return; } // seated at the Mass or pinned by the demon: the server holds the body, take its place as is
        if (Mode.SkipsReconcile) { _pending.Clear(); return; }
        _bot?.OnOwnState(rec);
        var idx = _pending.FindIndex(p => p.Input.Seq == ack);
        if (idx < 0) return;
        var predicted = _pending[idx].After;
        var err = (predicted.Position - sp).Length();
        _maxErr = MathF.Max(_maxErr, err);
        PerfPredErr(err, predicted.Position - sp);
        LastNearRemote = NearRemotePlayer(sp);
        LastVertical = MathF.Abs(predicted.Position.Y - sp.Y) > 0.5f * err; // the error is mostly up or down (steps, slopes, jumps, landings)
        if (err > ReconcileEpsilon) _rub.AddOnFoot(err, LastNearRemote, LastVertical);
        _bot?.OnPredictionError(err);
        TelemetryPredictionError(err);
        ReportPredictionError(err);
        _pending.RemoveRange(0, idx + 1);
        if (err <= ReconcileEpsilon)
        {
            // a newer ack agrees with the prediction: a reconcile still waiting for the next physics tick (from an older snapshot) is stale, and replaying it
            // would restart from that older server position while the inputs after this ack are already gone (code review finding)
            _reconcile = null;
            return;
        }
        _recon++;
        // server position wins; our own velocity/floor state for that tick is the best available guess for the rest
        var s = predicted;
        s.Position = sp;
        if (err > SnapDistance) { s.Velocity = default; _pending.Clear(); }
        // C-02: the replay is run by ReplayReconcile in the next physics tick, not here. This runs in _Process, where
        // Godot's MoveAndSlide uses the frame delta (60 Hz) instead of the physics delta (30 Hz): every replayed step
        // moved half a tick, and the next comparison was off by exactly that (8.9 cm, always ahead).
        _reconcile = s;
    }

    /// <summary>Set by the last acked comparison: whether another living player stood within 1 m of the body then (I-rubber-banding telemetry).</summary>
    bool LastNearRemote, LastVertical;

    bool NearRemotePlayer(NVec3 at)
    {
        foreach (var r in _remotes.Values)
        {
            if (r.State == EntityState.Dead || r.Node == null) continue;
            var d = new Vector2(r.LastPos.X - at.X, r.LastPos.Z - at.Z);
            if (d.LengthSquared() < 1f) return true;
        }
        return false;
    }

    /// <summary>The inputs waiting for an ack and a reconcile waiting for the next tick are void (the server took over the body).</summary>
    void DropPrediction() { _pending.Clear(); _reconcile = null; }

    /// <summary>A reconcile from a snapshot, waiting for the next physics tick (see <see cref="OnSelf"/>).</summary>
    MoveState? _reconcile;

    /// <summary>Puts the body at the server's position and replays the unacked inputs, in a physics tick so the steps match the server's.</summary>
    void ReplayReconcile()
    {
        if (_reconcile is not { } s) return;
        _reconcile = null;
        var before = _me!.State.Position; // where the eye was drawn: the predicted present
        if (_me.IsOnFloor() != s.OnFloor) _rub.FloorFlagMismatch++; // measurement: a replay that would start with the wrong floor flag without SetStateForReplay
        _me.SetStateForReplay(s); // the body's floor flag must match the state's before the first replayed step (I-rubber-banding round 2)
        for (var i = 0; i < _pending.Count; i++)
        {
            _me.Simulate(_pending[i].Input.ToMove(SpeedFor(_pending[i].Input.Slot)), 1f / Wire.TickRate);
            _pending[i].After = _me.State;
        }
        // I-rubber-banding: the state moved by the correction; the eye keeps drawing the old place and glides over (FadeOwnCorrection).
        // A respawn or teleport (beyond SnapDistance) is not a correction to glide over.
        var moved = before - _me.State.Position;
        _viewFix = moved.Length() <= SnapDistance ? _viewFix + ToVec(moved) : Vector3.Zero;
    }

    /// <summary>The part of an on-foot correction still to be drawn (metres, world), faded out over a few frames (see <see cref="ReplayReconcile"/>).</summary>
    Vector3 _viewFix;

    static Vector3 ToVec(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Fades the drawn part of the last on-foot correction, once per frame, at the same rate as the vehicles' corrections.</summary>
    void FadeOwnCorrection(double delta)
    {
        if (_me == null) return;
        _viewFix *= Mathf.Exp(-CorrectionFade * (float)delta);
        if (_viewFix.LengthSquared() < 1e-6f) _viewFix = Vector3.Zero; // under a millimetre: done
        _me.ViewOffset = _viewFix + _heldSwing; // in the Bigfoot's hand the eye swings with the hand (#264)
        _me.ViewRoll = _heldRoll + _nauseaRoll;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (LinkEnded || !_welcomed || _me == null || _peer == null) return;
        _elapsed += delta;
        _rozhlasTest?.Tick();
        if (_bot != null && _elapsed >= BotSeconds && _bot.TimedOut()) return;
        if (!OwnSpawned) return; // wait for our spawn position
        DemonBodyTick(); // the demon's Body capsule is where the server last put it, before the body is predicted
        var pt = ClientProf.Begin();
        ReplayReconcile(); // C-02: a snapshot's reconcile replays here, with the physics step (see OnSelf)
        ReplayDriveReconcile(); // and so does a predicted vehicle's (ADR 0010, ADR 0014)
        pt = ClientProf.Lap(ClientProf.S.Reconcile, pt);
        var dt = (float)delta;
        PredictTick(SampleInput(dt), dt);
        SendInputs();
        var p = _me.State.Position;
        _meMin = NVec3.Min(_meMin, p); _meMax = NVec3.Max(_meMax, p);
        ClientProf.End(ClientProf.S.Predict, pt);
    }

    /// <summary>What the player (or the bot) presses this tick, after the on-foot keys, the Scud screen and the throw key had their say, and the weapon's recoil and breath followed it.</summary>
    MoveInput SampleInput(float dt)
    {
        var mi = _bot != null ? _bot.Step() : Dead ? new MoveInput { SpeedScale = 1, Yaw = _me!.Yaw } : PerfPlay ? PerfPlayStep() : _me!.Sample();
        if (!Bot) { ReadSlotKeys(); ApplyScope(); TickGroundItems(); TickCorpses(dt); TickCarePackages(dt); TickLootReturn(dt); TickMinerals(dt); }
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
        if (_gunship == null) _me!.Yaw = mi.Yaw;
        var ni = NetInput.From(++_seq, mi, sentPitch, (byte)(_gunship != null ? TurretKey() : Weapons != null ? _wantSlot : 0), GunnerAim);
        // C-03: a dead body does not move on the server (its input is zero and it is skipped), so it is not predicted either; else each snapshot snapped it back
        if (Mode.BodyIsPredicted) { if (!Dead) _me!.Simulate(ni.ToMove(SpeedFor(ni.Slot)), dt); SfxOwnMovement(dt); } // a steered missile or a gunship: the body stands still; a turret's body moves
        PredictFlight(ni, dt); // the pilot of a helicopter steps it with the input the server will step it with
        PredictDrive(ni, dt);  // and so does the driver of an amphibious transport
        _pending.Add(new Pending { Input = ni, After = _me!.State });
        if (_pending.Count > 120) _pending.RemoveAt(0);
    }

    /// <summary>Sends the newest three inputs (loss cover); they name the entity and world ticks they follow (the server's delta baselines).</summary>
    void SendInputs()
    {
        var recent = new NetInput[Math.Min(3, _pending.Count)];
        for (var i = 0; i < recent.Length; i++) recent[i] = _pending[_pending.Count - recent.Length + i].Input;
        Tx(_peer!, Wire.ChanData, Wire.EncodeInputs(recent, _dec.AckTick, WorldEntities.AckTick), 0);
        _conn.Flush();
    }

    /// <summary>The fractional server tick the remote entities are drawn at this frame (see <see cref="RenderClock"/>).</summary>
    double RenderTickNow() => Ctx.RenderTick;

    void RenderRemotes()
    {
        if (_lastTick == 0) return;
        var rt = RenderTickNow();
        var dt = (float)GetProcessDeltaTime();
        var drawn = 0;
        foreach (var (id, r) in _remotes)
            if (r.Buf.At(rt) is { } s)
            {
                drawn++;
                var at = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                var dead = r.State == EntityState.Dead;
                at = GuessRunOver(id, r, at, dead, dt);
                var guessed = r.Guess.Active; // the body the driver's vehicle has just hit: thrown on the guessed arc, ahead of the server
                r.GuessRagdoll = guessed && (r.GuessRagdoll || dead); // the corpse's ragdoll only once the server says dead; before that the thrown body tumbles as one that may get up
                r.ShownYaw = s.Yaw; r.ShownPitch = s.Pitch;
                if (dead) EndFling(id);
                TumbleRemote(id, r, at, s.Yaw, dead && TargetDummies.IsDummyId(id) || r.GuessRagdoll, dt);
                PounceLieRemote(id, r, at); // the one the demon has pinned lies on its back
                // a killed player's thrown corpse is its own entity: the body standing at the death spot would be a ghost behind it
                r.Node.Visible = guessed || !(dead && !TargetDummies.IsDummyId(id) && (CorpseOf(id) || HarvestVictimHidden(id, dead))); // the harvester's victim is in the machine, then inside its bale: no body at the spot where it died // riders are drawn in their seat (the server puts the body there), seated rather than standing
                r.View?.SetStance(r.State == EntityState.InVehicle ? (r.RideStanding ? Stance.Stand : Stance.Crouch) : r.Stance); // a rider sits; the gunner in a ring stands
                if (r.State == EntityState.InVehicle) _lastRode[id] = Now;
                AnimateRemote(r, s.Pos);
                UpdateNameTag(id, r);
            }
        _rub.AddRender(rt, _lastTick, drawn);
    }

    /// <summary>
    /// The driver's picture of a run-over (#411, as <see cref="ThrowGuess"/> does for animals): the vehicle this player drives is drawn ahead of the remote bodies (about the
    /// interpolation delay and a round trip), so a body it hits is seen inside the hull until the server's throw arrives. The moment the predicted vehicle's box touches a living
    /// body as drawn, at any speed that moves a body, the body is shown thrown on the arc the server will give it. The server throws every player a vehicle touches, killed or
    /// not, and one who lives through a hit that hurts is drawn tumbling by everybody (PlayerFlung, <see cref="BodyFlingTracker"/>): so a hit that hurts tumbles here at once,
    /// which is right whether the server then says dead (the corpse's ragdoll takes over in flight) or alive (it lands, lies and gets up). A slow push is an upright shove.
    /// Only a touch the server did not see (a graze, within the lag) is taken back: the body glides to where the server has it. Returns where the body is drawn.
    /// </summary>
    Vector3 GuessRunOver(ushort id, RemotePlayer r, Vector3 at, bool dead, float dt)
    {
        var g = r.Guess;
        var feet = new NVec3(at.X, at.Y, at.Z);
        if (g.Ready && r.State == EntityState.Alive && Vehicles != null && ThrowGuessCar() is { } c
            && RunOverFootprint.OfCapsule(feet).Hits(c.State.Position, c.State.Position, c.State.Yaw, c.Def.HalfWidth, c.Def.HalfLength))
        {
            var launch = CorpseRules.ThrowFromCar(new NVec3(-MathF.Sin(c.State.Yaw), 0, -MathF.Cos(c.State.Yaw)), c.State.Speed);
            if (g.Start(feet, launch))
            {
                r.GuessLaunch = new NVec3(launch.X, 0, launch.Z).Length(); r.GuessConfirmed = false;
                r.GuessFlung = RunOver.Damage(Vehicles, c.State.Speed, Vehicles.RunOverScale(c.Def)) > 0; // a hit that hurts knocks the body off its feet
                if (r.GuessFlung) FlingOf(id).Thrown();
            }
        }
        // the server's own throw of a living body: its event says it threw, its speed says when its body has come down; the guessed arc is kept until both have, as for a corpse
        var moved = at - r.GuessServerLast; moved.Y = 0; r.GuessServerLast = at;
        var flies = !dead && r.GuessConfirmed && dt > 0 && ThrowGuess.ServerStillFlies(moved.Length() / dt, r.GuessLaunch);
        var was = g.Phase;
        var p = g.Step(dt, feet, dead || r.GuessConfirmed, flies, g.Active ? GroundUnder(g.Position) : null);
        if (was == ThrowGuessPhase.Flying && g.Phase == ThrowGuessPhase.Settling && r.GuessFlung && !dead) EndFling(id); // the server saw no hit: the tumble is taken back with the arc
        if (!g.Active) r.GuessFlung = false;
        return new Vector3(p.X, p.Y, p.Z);
    }

    /// <summary>The server's word that a vehicle threw a player alive (<see cref="EventKind.PlayerFlung"/>, #411): the body tumbles and gets up, on every client; the driver's own guess of it is confirmed.</summary>
    void OnPlayerFlung(ushort id)
    {
        FlingOf(id).Thrown();
        if (_remotes.TryGetValue(id, out var r) && r.Guess.Active) r.GuessConfirmed = true;
    }

    /// <summary>
    /// The ragdoll of a killed dummy: while it is thrown (dead and moving fast) it spins about its middle with its limbs flailing, then lies flat with
    /// them loose. A dummy that fell where it stood plays the death clip; everything else stands upright at its feet. Corpses do the same in <see cref="RenderCorpses"/>.
    /// </summary>
    void TumbleRemote(ushort id, RemotePlayer r, Vector3 at, float yaw, bool dummyDown, float dt)
    {
        var t = r.Tumble;
        if (_flings.TryGetValue(id, out var fling) && fling.Active && !dummyDown) { FlingRemote(id, r, fling, at, yaw, dt); return; } // in the Bigfoot's fist or flying from it (#360)
        if (!dummyDown) { if (t.Thrown) { t.Reset(); r.View?.SetRagdoll(false); } }
        else
        {
            var speed = dt > 0 ? (at - r.TumbleLast).Length() / dt : 0;
            t.Update(at, speed > 2.5f && speed < 60f, dt);
            if (t.Thrown) r.View?.SetRagdoll(true, t.Amount, t.AtRest);
        }
        r.TumbleLast = at;
        if (r.View != null) r.View.Position = t.Thrown ? new Vector3(0, -RagdollTumble.Half, 0) : Vector3.Zero;
        r.Node.GlobalPosition = t.Thrown ? at + Vector3.Up * t.PivotHeight : at;
        r.Node.Basis = t.Thrown ? t.BasisFor(yaw) : new Basis(Vector3.Up, yaw);
    }

    /// <summary>True while a corpse of player <paramref name="owner"/> is on the map.</summary>
    bool CorpseOf(ushort owner)
    {
        foreach (var id in _corpses.Keys)
            if (WorldEntities.Entities.TryGetValue(id, out var e) && e.State.Owner == owner) return true;
        return false;
    }

    /// <summary>Shows the held weapon of the current hotbar slot (the server-confirmed one once known) low on the right; hidden while dead or driving.</summary>
    void UpdateViewmodel()
    {
        if (_viewmodel == null) return;
        var slot = Ammo?.Slot ?? _wantSlot;
        var id = ThrowHeldId() ?? (Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].Id : ""); // the throw key held: the grenade, whatever was selected
        _viewmodel.Show(id);
        _viewmodel.Visible = Mode.ShowsViewmodel && !OwnBodyFlung && _me is not { ZoomFov: > 0 }; // an armed seat's rider holds their weapon (ADR 0011)
    }

    /// <summary>Drives the remote character's animation from snapshot state: horizontal speed of the interpolated position, a recent shot event, and the dead state.</summary>
    void AnimateRemote(RemotePlayer r, NVec3 pos)
    {
        var now = Now;
        var p = new Vector3(pos.X, pos.Y, pos.Z);
        if (r.LastT > 0 && now > r.LastT + 0.001)
        {
            var d = p - r.LastPos; d.Y = 0;
            var sp = d.Length() / (float)(now - r.LastT);
            if (sp > 30f) sp = 0; // a teleport (respawn), not running
            // a rider sits: the seat moving (the car driving, a flak turret turning the gunner about its ring) is not the body walking
            if (r.State == EntityState.InVehicle) sp = 0;
            r.Speed = Mathf.Lerp(r.Speed, sp, 0.3f);
            if (r.State != EntityState.InVehicle) SfxRemoteStep(r, p, (float)(now - r.LastT));
        }
        r.LastPos = p; r.LastT = now;
        var weaponId = Weapons != null && r.Slot >= 1 && r.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[r.Slot - 1].Id : "";
        r.View?.SetBinoculars(r.Binoculars);
        r.View?.Apply(r.Speed, now < r.FireUntil, r.State == EntityState.Dead || r.GuessRagdoll, weaponId);
    }

    // ---- weapons: input, events, placeholder effects (the HUD lives in NetClient.Hud.cs) ----

    /// <summary>Movement speed multiplier of the weapon in <paramref name="slot"/> (0: none, 1.0), times the run speed of <see cref="Perks"/>. The server applies the same value.</summary>
    float SpeedFor(byte slot) =>
        (Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].MoveSpeedMultiplier : 1f) * Perks.RunSpeed;

    /// <summary>This player's perk values, mirroring the server's copy for prediction. Neutral until the loadout ticket sets them.</summary>
    public PerkModifiers Perks = PerkModifiers.None;

    /// <summary>Holding zoom with a scoped weapon (sniper) narrows the camera; never while dead or with the mouse released.</summary>
    void ApplyScope()
    {
        if (_me == null || _gunship != null || ScudScreenOn) return; // at a turret the zoom is the gunship's; on a Scud screen it is the screen's
        if (Mode.ScopeSuppressed)
        {
            // seated (roof/door gun, turret, any seat) or steering a missile: the held weapon's scope and iron-sight zoom stay off (#241)
            _me.ZoomFov = 0f; _me.IronSightZoom = false;
            if (_viewmodel != null) _viewmodel.Aiming = false;
            LowerBinoculars();
            return;
        }
        if (TickBinoculars()) return; // raised binoculars are the zoom: no scope, no iron sights
        var slot = Ammo?.Slot ?? _wantSlot;
        var zoom = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].ZoomFov : 0f;
        var on = zoom > 0 && !Dead && !_pinned && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        _me.ZoomFov = on ? zoom : 0f;
        // iron sights: any non-scoped, non-melee weapon while zoom is held
        var kind = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].Kind : WeaponKind.Melee;
        if (_viewmodel != null)
            _viewmodel.Aiming = zoom <= 0 && kind != WeaponKind.Melee && !Dead && (!_driving && _myState != EntityState.InVehicle || InArmedSeat)
                && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        _me.IronSightZoom = _viewmodel is { Aiming: true };
    }

    /// <summary>At a turret the slot sent is the key held (1-5, 0 none), not the body's hotbar weapon: the turret reads it as its weapon number (the AC-130's 1-3).</summary>
    byte TurretKey()
    {
        if (Dead || Input.MouseMode != Input.MouseModeEnum.Captured) return 0;
        for (var k = 0; k < 5; k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) return (byte)(k + 1);
        return 0;
    }

    void ReadSlotKeys()
    {
        if (!Mode.HotbarKeysLive || Input.MouseMode != Input.MouseModeEnum.Captured || ChooserOpen) return; // seated, the number keys are the seats' (HandleSeatInput)
        for (var k = 0; k < Math.Min(5, _hotbar.Length); k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) _wantSlot = _hotbar[k] + 1;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (HandleChatInput(e)) return; // T or "/": the chat box
        if (HandleMapInput(e)) return; // M: the tactical map
        if (HandleEquipmentInput(e)) return; // F: the Equipment screen (#268)
        if (HandleHuntersLogInput(e)) return; // J: the Hunter's Log (#336)
        if (HandleMineralLogInput(e)) return; // N: the Mineral Log
        if (HandleSandboxInput(e)) return; // F4, the sandbox panel (#156)
        if (HandleVolumeInput(e)) return; // F3, the bounding-volume overlay; [ ] step its mode (#479)
        if (HandleScudInput(e)) return; // the Scud screen's keys (open, confirm, abort, lock, the wheel's zoom)
        if (HandleSeatInput(e)) return; // a number key while seated takes that seat
        if (HandleViewInput(e)) return; // the vehicle view key while driving: chase camera or cockpit
        if (HandleBinocularsInput(e)) return; // B: the binoculars from the bag
        if (Bot || Weapons == null || Dead || ChooserOpen || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        if (e is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (DialZero(b.ButtonIndex == MouseButton.WheelUp ? 1 : -1)) return; // a scope with bullet drop is up: the wheel is its zero dial
            _wantSlot = CycleSlot(_wantSlot, b.ButtonIndex == MouseButton.WheelDown ? 1 : -1);
        }
    }

    void OnWeaponEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Ammo when WeaponCodec.DecodeAmmo(payload) is { } a:
                Ammo = a; _ammoAt = Now;
                PredictOnAmmo(a);
                SfxAmmo(a);
                HudAmmo();
                break;
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } sh:
                _blips.Add(sh.Shooter, sh.Weapon, sh.Origin);
                if (!OnDoorGunShot(sh)) OnShot(sh); // a door gun's round leaves its muzzle on the model
                break;
            case EventKind.PelletFan when WeaponCodec.DecodePelletFan(payload) is { } pf:
                _blips.Add(pf.Shooter, pf.Weapon, pf.Origin);
                OnPelletFan(pf);
                break;
            case EventKind.Launch when WeaponCodec.DecodeLaunch(payload) is { } l:
                OnLaunch(l);
                break;
            case EventKind.Explosion when WeaponCodec.DecodeExplosion(payload) is { } ex:
                OnExplosion(ex);
                break;
            case EventKind.VehicleExplosion when WeaponCodec.DecodeVehicleExplosion(payload) is { } vx:
                OnVehicleExplosion(vx);
                break;
            case EventKind.Moab when MoabEvents.Decode(payload) is { } mo:
                _moab.OnMoab(mo);
                break;
            case EventKind.MissileBlast when MissileCodec.DecodeBlast(payload) is { } mb:
                OnMissileBlast(mb);
                break;
            case EventKind.GunshipShots when GunshipFxCodec.DecodeShots(payload) is { } gs:
                OnGunshipShots(gs);
                break;
            case EventKind.GunshipImpacts when GunshipFxCodec.DecodeImpacts(payload) is { } gi:
                OnGunshipImpacts(gi);
                break;
            case EventKind.GunshipBlasts when GunshipFxCodec.DecodeBlasts(payload) is { } gb:
                OnGunshipBlasts(gb);
                break;
            case EventKind.ImpactFx when ImpactCodec.Decode(payload) is { } imp:
                OnImpactFx(imp);
                break;
            case EventKind.HelperWarning when HelperWarningCodec.Decode(payload) is { } hw:
                OnHelperWarning(hw);
                break;
            case EventKind.PlayerFlung when PlayerFlungCodec.Decode(payload) is { } flung:
                OnPlayerFlung(flung);
                break;
            case EventKind.VehicleCrash when VehicleCrashCodec.Decode(payload) is { } crash:
                OnVehicleCrash(crash.Car, crash.Sound);
                break;
            case EventKind.GlassPane when GlassCodec.DecodePane(payload) is { } gp:
                OnGlassPane(gp);
                break;
            case EventKind.GlassSync when GlassCodec.DecodeSync(payload) is { } gsync:
                OnGlassSync(gsync);
                break;
            case EventKind.LampState when LampCodec.DecodeState(payload) is { } lamp:
                StreetLamps.Current?.SetPoleLamp(lamp.Index, lamp.State);
                break;
            case EventKind.LampSync when LampCodec.DecodeSync(payload) is { } lamps:
                StreetLamps.Current?.SyncPoleLamps(lamps);
                break;
            case EventKind.SignPlateState when SignPlateCodec.DecodeState(payload) is { } plate:
                SignPlates.Current?.SetPlate(plate.Index, plate.State);
                break;
            case EventKind.SignPlateSync when SignPlateCodec.DecodeSync(payload) is { } plates:
                SignPlates.Current?.SyncPlates(plates);
                break;
            case EventKind.HuntShot when HuntShot.Decode(payload) is { } hs:
                HudHuntShot(hs);
                break;
            case EventKind.Hit when WeaponCodec.DecodeHit(payload) is { } h:
                if (KeepEventLogs) _logs.Hits.Add((Now, h));
                SfxHit(h);
                HudHit(h);
                break;
        }
    }

    /// <summary>A rocket or a thrown grenade left its owner: drawn at once from the event (snapshots take over), the sound, the flash and the backblast.</summary>
    void OnLaunch(LaunchMsg l)
    {
        if (KeepEventLogs) _logs.Launches.Add((Now, l));
        if (_remotes.TryGetValue(l.Owner, out var launcher)) launcher.FireUntil = Now + 0.45;
        // the launch's velocity is the flight's speed and direction (a gunship's rocket is faster than the rpg's)
        if (_rockets.TryGetValue(l.Id, out var known)) { known.Speed = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z).Length(); known.Gravity = GrenadeGravity(l.Weapon); known.Grenade = known.Gravity > 0; }
        else if (!_deadRockets.Contains(l.Id) && ShotFx.Enabled && Weapons != null)
        {
            // show the rocket at once from the launch event; snapshots take over
            var v = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z);
            _rockets[l.Id] = new Rocket { Node = RocketFx.Make(Owned, WeaponId(l.Weapon)), Born = Now, Pos = l.Origin, Dir = NVec3.Normalize(v), At = Now, Speed = v.Length(), Gravity = GrenadeGravity(l.Weapon), Grenade = GrenadeGravity(l.Weapon) > 0 };
        }
        if (!EchoOfPredicted(l.Owner))
        {
            SfxLaunch(l);
            if (GrenadeGravity(l.Weapon) > 0) { if (_remotes.TryGetValue(l.Owner, out var thrower)) thrower.FireUntil = Now + 0.45; return; } // a throw: no flash, no backblast
            if (l.Owner == _myId) _viewmodel?.Flash(0.5f); else if (_remotes.TryGetValue(l.Owner, out var lf)) lf.View?.Flash(0.6f);
            // a held launcher's backblast, behind the shooter's shoulder (a gunship's rocket has no owner on foot: its owner is not a remote here)
            if (_remotes.ContainsKey(l.Owner) && l.Velocity.LengthSquared() > 1e-4f) { var ld = G(NVec3.Normalize(l.Velocity)); RocketFx.Backblast(Owned, G(l.Origin) - ld * WeaponFx.LauncherRear + Vector3.Down * 0.15f, ld); }
        }
    }

    /// <summary>A rocket blew up: its model retires, the blast is drawn and felt.</summary>
    void OnExplosion(ExplosionMsg ex)
    {
        if (KeepEventLogs) _logs.Explosions.Add((Now, ex));
        if (_rockets.Remove(ex.Id, out var rk)) { rk.Snd?.Release(); RocketFx.Retire(Owned, rk.Node); if (rk.Homing) NoteHomingBlast(new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z), ex.Radius); }
        _dec.Forget(ex.Id);
        _deadRockets.Add(ex.Id);
        if (_deadRockets.Count > 256) _deadRockets.Clear();
        var exR = ex.Radius > 0 ? ex.Radius : 6f; // the blast the server used (a gunship rocket's is 8 m)
        var exAt = new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z);
        RocketFx.Explode(Owned, exAt, exR, ex.WaveSpeed);
        _blastDust.BlastFelt(exAt, exR, SoundRules.Event("explosion", WeaponId(ex.Weapon)), ex.WaveSpeed);
    }
}
