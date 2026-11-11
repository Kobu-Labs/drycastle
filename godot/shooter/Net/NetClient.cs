using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Network client over raw ENet (ADR 0002). Sends one input per 30 Hz physics tick (the last 3 for loss cover),
/// predicts the local player with the shared movement step and reconciles against the server position at the
/// acked input seq (replaying the unacked inputs), and renders remote players about 100 ms (3 ticks) in the past
/// from interpolation buffers as capsules with a name label.
/// </summary>
public partial class NetClient : Node
{
    sealed class Pending { public NetInput Input; public MoveState After; }
    sealed class Remote { public CharacterView? View; public byte Slot; public double FireUntil; public Vector3 LastPos; public double LastT; public float Speed, StepAcc; public EntityState State; public Stance Stance; public Node3D Node = null!; public readonly Interpolator Buf = new(); public NVec3 Min = new(float.MaxValue), Max = new(float.MinValue); }

    public const double InterpDelayTicks = 3.0;
    const float SnapDistance = 2f, ReconcileEpsilon = 0.03f;

    public Node3D World = null!;
    public string Host = "127.0.0.1";
    public int Port = 27015;
    public string PlayerName = "player";
    /// <summary>Player token sent in the hello (ADR 0003). Empty = a fresh one is generated (bots and CLI clients).</summary>
    public string Token = "";
    /// <summary>The server's profile for this player (level, XP, loadouts, active slot); null until it arrives.</summary>
    public ProfileMsg? Profile { get; private set; }
    /// <summary>Raised for an error the server sends after the join (for example an invalid loadout).</summary>
    public event Action<string>? ServerError;
    string? _rejectReason;
    public bool Bot;
    /// <summary>Network impairment for the loss and latency test (--net-sim); null = none.</summary>
    public NetSim? Sim;
    /// <summary>Loss test role ("walker" or "shooter"); empty = off. See NetClient.LossBot.cs.</summary>
    public string LossBot = "";
    public double BotSeconds = 6;
    public bool Headless;
    public float KillY = float.NegativeInfinity;
    /// <summary>The map's water (water.json), shared with the prediction so swimming matches the server.</summary>
    public Suchohrad.ShooterCore.Water Water = Suchohrad.ShooterCore.Water.Empty;
    /// <summary>Match smoke role: "alice" (kills bob with the debug command) or "bob" (fires after each spawn); empty = normal bot/player.</summary>
    public string MatchBot = "";
    /// <summary>Match events stored for the HUD to come: phase, countdown, scoreboard rows, kill feed, own spawn/death.</summary>
    public readonly ClientMatchState Match = new();
    public bool Dead { get; private set; }
    /// <summary>weapons.json: hotbar order and the per-weapon movement speed multiplier used by prediction.</summary>
    public WeaponsFile Weapons = null!;
    /// <summary>Weapons smoke role: "a" (shoots) or "b" (stands still and counts hits); empty = none.</summary>
    public string WeaponsBot = "";
    /// <summary>Bazooka smoke role ("a"/"b"); WeaponsBot carries the same letter so the shared bot plumbing runs.</summary>
    public string BazookaBot = "";
    /// <summary>Vehicle blast smoke role ("a"/"b"); WeaponsBot carries the same letter.</summary>
    public string BlastBot = "";
    /// <summary>Latest owner ammo state from the server (null before the first one).</summary>
    public AmmoMsg? Ammo { get; private set; }
    readonly List<(double T, ShotMsg M)> _shotLog = new();
    readonly List<(double T, HitMsg M)> _hitLog = new();
    readonly List<(double T, LaunchMsg M)> _launchLog = new();
    readonly List<(double T, ExplosionMsg M)> _explosionLog = new();
    sealed class Rocket { public double Born; public Node3D Node = null!; public NVec3 Pos, Dir; public double At; public float Speed; public LoopVoice? Snd; }
    readonly Dictionary<ushort, Rocket> _rockets = new();
    readonly HashSet<ushort> _deadRockets = new();
    readonly List<(double T, AmmoMsg M)> _ammoLog = new();
    int _wantSlot = 1;
    WeaponViewmodel? _viewmodel;
    double _ammoAt;

    readonly ENetConnection _conn = new();
    ENetPacketPeer? _peer;
    Player? _me;
    ushort _myId;
    bool _welcomed, _gotSelf;
    ushort _seq;
    readonly List<Pending> _pending = new();
    readonly SnapshotDecoder _dec = new();
    /// <summary>Replicated world entities (turrets, missiles, ...). Subscribe to Spawned/Despawned for visuals.</summary>
    public readonly WorldReplica WorldEntities = new();
    readonly Dictionary<ushort, string> _names = new();
    readonly Dictionary<ushort, Remote> _remotes = new();
    uint _lastTick;
    double _lastArrival;
    double _elapsed, _connectElapsed;
    int _recon, _snaps;
    float _maxErr;
    NVec3 _meMin = new(float.MaxValue), _meMax = new(float.MinValue);
    bool _done;
    // match smoke
    double _spawnAt, _fireStart, _spawnProtection = 99;
    EntityState _myState;
    bool _sawProtected, _earlyCounted;
    int _deaths, _earlyEnds;
    readonly MatchBotAlice _alice = new();

    public override void _Ready()
    {
        if (Token.Length == 0) Token = Guid.NewGuid().ToString();
        InitCorpses();
        InitCarePackages();
        InitMissile();
        InitTurrets();
        InitAircraft();
        var err = _conn.CreateHost(1, Wire.Channels, 0, 0);
        if (err != Error.Ok) { Fail($"CreateHost failed: {err}"); return; }
        _peer = _conn.ConnectToHost(Host, Port, Wire.Channels, 0);
        if (_peer == null) { Fail("ConnectToHost failed"); return; }
        GD.Print($"client: connecting to {Host}:{Port} as {PlayerName}");
    }

    public override void _ExitTree() => _conn.Destroy();

    /// <summary>Menu mode: raised instead of quitting when the connection fails or drops (the reason is short, for the error message).</summary>
    public Action<string, bool>? OnFailed;
    /// <summary>Menu mode: raised once the server's welcome arrived (the match is joined).</summary>
    public Action? OnWelcomed;

    /// <summary>Leaves the server and removes everything this client put into the world (back to the main menu).</summary>
    public void Teardown()
    {
        _done = true;
        SfxTeardown();
        if (_peer != null) { _peer.PeerDisconnect(); _conn.Flush(); _peer = null; }
        foreach (var r in _remotes.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        _remotes.Clear();
        foreach (var r in _rockets.Values) if (GodotObject.IsInstanceValid(r.Node)) r.Node.QueueFree();
        _rockets.Clear();
        foreach (var v in _cars.Values) if (GodotObject.IsInstanceValid(v.Node)) v.Node.QueueFree();
        _cars.Clear();
        if (_me != null && GodotObject.IsInstanceValid(_me)) _me.QueueFree();
        _me = null;
    }

    void Fail(string why)
    {
        GD.PrintErr($"client: {why}");
        if (OnFailed != null) { var joined = _welcomed; _done = true; OnFailed(why, joined); return; }
        if (Bot) { GD.Print("SHOOTER_NET_CLIENT_FAIL"); GetTree().Quit(2); }
        else GetTree().Quit(2);
        _done = true;
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

    public override void _Process(double delta)
    {
        if (Sim != null && Sim.Pump()) _conn.Flush(); // delayed packets go out at frame rate, not only on physics ticks
        if (_done) return;
        Poll();
        if (!_welcomed)
        {
            _connectElapsed += delta;
            if (_connectElapsed > 10) Fail("connect timeout");
            return;
        }
        PumpAutosave(); // loadout saves whose debounce ran out go out here, whichever screen is open (#145)
        RenderRemotes();
        RenderCorpses();
        RenderCarePackages();
        RenderTurrets();
        RenderCars();
        RenderAircraft(delta);
        RenderRockets();
        RenderMissiles();
        RenderMissilePov();
        RenderWater();
        UpdateViewmodel();
        UpdateShake();
        UpdateHud();
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
                    Tx(peer, Wire.ChanReliable, Wire.Encode(new HelloMsg(Wire.ProtocolVersion, PlayerName, Token)), (int)ENetPacketPeer.FlagReliable);
                    _conn.Flush();
                    break;
                case ENetConnection.EventType.Disconnect:
                    if (!_done) Fail(_rejectReason ?? "server disconnected");
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
        if (d[0] < Wire.TInput) OnReliable(Wire.DecodeReliable(d));
        else if (d[0] == Wire.TSnapshot) OnSnapshot(d);
        else if (d[0] == Wire.TWorldSnapshot) WorldEntities.Apply(d);
    }

    void OnReliable(ReliableMsg? m)
    {
        switch (m)
        {
            case WelcomeMsg w:
                _myId = w.PlayerId; _welcomed = true;
                DayNightCycle.Current?.SetServerClock(new DayClock { StartMinute = w.StartMinute, DayLengthSeconds = w.DayLengthMs / 1000.0, Frozen = w.Frozen }, w.ServerTick);
                _me = new Player { Name = "Me", UseCamera = !Bot, AutoSim = false, Authoritative = false, CaptureMouse = !Bot, SelfManageMouse = UiShell.Instance == null, KillY = KillY, Water = Water };
                World.AddChild(_me);
                if (!Bot && ShotFx.Enabled) { _viewmodel = new WeaponViewmodel(); _me.Camera.AddChild(_viewmodel); }
                if (WeaponsBot != "" || CarBot != "" || FxDemo.StartsWith("vehicle:")) NetServer.AddArenaColliders(World);
                if (Bot) _me.InputOverride = new MoveInput { SpeedScale = 1 };
                HudWelcome();
                OnWelcomed?.Invoke();
                GD.Print($"client: welcome, id {_myId}, server tick {w.ServerTick}");
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
                _names.Remove(l.PlayerId); Match.Remove(l.PlayerId); _dec.Remove(l.PlayerId);
                if (_remotes.Remove(l.PlayerId, out var r)) r.Node.QueueFree();
                break;
            case EventMsg e:
                if ((EventKind)e.Kind == EventKind.TargetRoster) OnTargetRoster(e.Payload); // target dummies' names (#148)
                OnWeaponEvent((EventKind)e.Kind, e.Payload);
                OnMatchEvent((EventKind)e.Kind, e.Payload);
                OnProgressionEvent((EventKind)e.Kind, e.Payload);
                UavEvent((EventKind)e.Kind, e.Payload);
                OnAppearanceEvent((EventKind)e.Kind, e.Payload);
                WorldEntities.OnEvent((EventKind)e.Kind, e.Payload);
                EventReceived?.Invoke((EventKind)e.Kind, e.Payload);
                break;
        }
    }

    double Now => Time.GetTicksMsec() / 1000.0;

    void OnMatchEvent(EventKind kind, byte[] payload)
    {
        if (!Match.Apply(kind, payload, Now)) return;
        switch (kind)
        {
            case EventKind.MatchState:
                SfxPhase(Match.Phase);
                if (Match.Restarts != _hudRestarts) { _hudRestarts = Match.Restarts; _hud?.ClearKills(); }
                _hud?.SetMatch(Match.Phase, Match.TimeRemaining(Now), Match.Rows, Match.Winners, Match.NameOf);
                GD.Print($"client {PlayerName}: {Match.ScoreboardText(Now)}"); break;
            case EventKind.Kill:
                var k = Match.KillFeed[^1];
                _hud?.AddKill(k, Match.NameOf(k.Killer), Match.NameOf(k.Victim));
                OnStreakKill(k);
                GD.Print($"client {PlayerName}: kill feed: {Match.NameOf(k.Killer)} -> {Match.NameOf(k.Victim)} (weapon {k.Weapon})");
                break;
            case EventKind.Spawn:
                var sp = Match.LastSpawn.Values.Last();
                GD.Print($"client {PlayerName}: spawn #{sp.Id} protected {sp.Protection:F1}s");
                if (sp.Id == _myId && _me != null)
                {
                    ApplySpawnLoadout(); // the hotbar and perk the server applied at this spawn
                    _wantSlot = StartSlot();
                    Dead = false; _gotSelf = false; _pending.Clear(); // the next snapshot carries the spawn position
                    _spawnAt = Now; _fireStart = 0; _earlyCounted = false; _spawnProtection = sp.Protection;
                    _hud?.ClearTransients(); _hud?.SetDead(false); _hud?.SetProtection(sp.Protection); _spread.Reset(); _recoil = new RecoilState((ulong)_myId); _breath = new BreathState((ulong)_myId);
                }
                break;
            case EventKind.Death:
                var d = Match.LastDeath.Values.Last();
                GD.Print($"client {PlayerName}: death #{d.Victim} by #{d.Killer}, respawn in {d.RespawnDelay:F1}s");
                SfxDeath(d);
                if (d.Victim == _myId) { Dead = true; _deaths++; _hud?.SetDead(true, d.Killer == d.Victim ? "" : Match.NameOf(d.Killer), d.RespawnDelay); _hud?.SetProtection(0); OnStreakDeath(); }
                break;
        }
    }

    /// <summary>Raised for every reliable event envelope (shot, hit, kill, ... handled by later issues).</summary>
    public event Action<EventKind, byte[]>? EventReceived;

    void OnSnapshot(byte[] d)
    {
        if (_me == null || !_dec.Apply(d, out var h)) return;
        if (_lastTick != 0 && h.Tick < _lastTick) return;
        LossOnSnapshot(h);
        _lastTick = h.Tick; _lastArrival = Time.GetTicksMsec() / 1000.0; _snaps++;
        DayNightCycle.Current?.SetServerTick(h.Tick);
        foreach (var id in _dec.RemoveStale(h.Tick, 90)) { if (_remotes.Remove(id, out var gone)) gone.Node.QueueFree(); if (_cars.Remove(id, out var cg)) { cg.Snd?.Release(); cg.Node.QueueFree(); } }
        foreach (var (id, rec) in _dec.States)
        {
            if (id == _myId) { OnSelf(rec, h.Ack); continue; }
            if (rec.Type == EntityType.Projectile) { OnRocketState(id, rec); continue; }
            if (rec.Type == EntityType.Vehicle) { OnVehicleState(id, rec, h.Tick); continue; }
            if (rec.Type is not (EntityType.Player or EntityType.Target)) continue;
            if (!_remotes.TryGetValue(id, out var r)) { r = MakeRemote(id, rec.Type == EntityType.Target); _remotes[id] = r; }
            r.State = rec.State; r.Stance = rec.Stance; r.Slot = rec.Weapon;
            r.Buf.Push(h.Tick, rec.Position, rec.YawRad, rec.PitchRad);
            r.Min = NVec3.Min(r.Min, rec.Position); r.Max = NVec3.Max(r.Max, rec.Position);
        }
        LossAfterSnapshot(h);
    }

    /// <summary>Rockets are drawn at the newest snapshot position and dead-reckoned along their direction (a 100 ms interpolation delay would make them vanish 4 m short of the blast).</summary>
    void OnRocketState(ushort id, EntityRecord rec)
    {
        if (_deadRockets.Contains(id)) return;
        var dir = HitMath.Forward(rec.YawRad, rec.PitchRad);
        var speed = Weapons != null && rec.Weapon < Weapons.Weapons.Count ? Weapons.Weapons[rec.Weapon].ProjectileSpeed : 40f;
        if (!_rockets.TryGetValue(id, out var r)) { r = new Rocket { Node = RocketFx.Make(World), Born = Now }; _rockets[id] = r; }
        r.Pos = rec.Position; r.Dir = dir; r.At = Now; r.Speed = speed;
    }

    void RenderRockets()
    {
        foreach (var r in _rockets.Values)
        {
            if (r.Node == null) continue;
            var p = r.Pos + r.Dir * (r.Speed * (float)Math.Min(0.1, Now - r.At));
            // visual only: the server rocket starts at the eye; draw it leaving the muzzle (0.8 m ahead, right and down) and blend onto the true path
            var k = Mathf.Max(0f, 1f - (float)(Now - r.Born) / 0.35f);
            var d = new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z);
            var side = d.Cross(Vector3.Up).Normalized();
            r.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z) + (d * 0.8f + side * 0.25f + Vector3.Down * 0.2f) * k;
            SfxRocket(r, r.Node.GlobalPosition);
            if (Mathf.Abs(r.Dir.Y) < 0.99f) r.Node.LookAt(r.Node.GlobalPosition + new Vector3(r.Dir.X, r.Dir.Y, r.Dir.Z));
        }
    }

    Remote MakeRemote(ushort id, bool target = false)
    {
        var node = new Node3D { Name = $"Remote{id}" };
        var view = CharacterView.Make();
        if (Appearances.TryGetValue(id, out var look)) view.SetCamo(look, CamoData); // the look may arrive before the first snapshot (#140)
        node.AddChild(view);
        node.AddChild(new Label3D
        {
            Text = _names.TryGetValue(id, out var n) ? n : $"#{id}",
            Modulate = target ? TargetTint : Colors.White, // orange name tag for target dummies (#148)
            Position = new Vector3(0, MoveConsts.CapsuleHeight + 0.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.005f, NoDepthTest = true,
        });
        World.AddChild(node);
        return new Remote { Node = node, View = view };
    }

    void OnSelf(EntityRecord rec, ushort ack)
    {
        var sp = rec.Position;
        if (!_gotSelf)
        {
            _gotSelf = true;
            _me!.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y });
            return;
        }
        _me!.Health = rec.Health;
        _myState = rec.State;
        var inCar = rec.State == EntityState.InVehicle;
        if (inCar != _driving)
        {
            // entering or leaving a car: no prediction while driving; on exit take the server's position (the free side) as is
            _driving = inCar; _pending.Clear(); _me.InVehicle = inCar;
            if (!inCar) { _me.SetState(new MoveState { Position = sp, OnFloor = false, FallPeakY = sp.Y }); _me.EndChase(_carYaw); }
            return;
        }
        if (_driving || _controlling || _gunship != null) { _pending.Clear(); return; }
        if (rec.State == EntityState.Protected) _sawProtected = true;
        if (rec.State == EntityState.Alive && _fireStart > 0 && !_earlyCounted && Now - _fireStart < 0.6 && _fireStart - _spawnAt < _spawnProtection - 1.0) { _earlyEnds++; _earlyCounted = true; }
        var idx = _pending.FindIndex(p => p.Input.Seq == ack);
        if (idx < 0) return;
        var predicted = _pending[idx].After;
        var err = (predicted.Position - sp).Length();
        _maxErr = MathF.Max(_maxErr, err);
        LossOnPredictionError(err);
        _pending.RemoveRange(0, idx + 1);
        if (err <= ReconcileEpsilon) return;
        _recon++;
        // server position wins; our own velocity/floor state for that tick is the best available guess for the rest
        var s = predicted;
        s.Position = sp;
        if (err > SnapDistance) { s.Velocity = default; _pending.Clear(); }
        _me.SetState(s);
        for (var i = 0; i < _pending.Count; i++)
        {
            _me.Simulate(_pending[i].Input.ToMove(SpeedFor(_pending[i].Input.Slot)), 1f / Wire.TickRate);
            _pending[i].After = _me.State;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done || !_welcomed || _me == null || _peer == null) return;
        _elapsed += delta;
        if (Bot && LossBot != "") { if (_elapsed >= BotSeconds) LossFail("timeout: round never reached its end"); }
        else if (Bot && _elapsed >= BotSeconds) { if (CarBot != "") CarResult(false, "timeout"); else if (WeaponsBot != "") WeaponsFail("timeout"); else if (MatchBot != "") MatchFail("timeout"); else FinishBot(); return; }
        if (!_gotSelf) return; // wait for our spawn position
        var dt = (float)delta;
        MoveInput mi;
        if (Bot && MatchBot != "")
        {
            // idle; bob fires 1 s after every spawn (ends spawn protection early), alice runs her script
            var btn = MoveButtons.None;
            if (MatchBot == "bob" && !Dead && Now - _spawnAt >= 1.0)
            {
                btn = MoveButtons.Fire;
                if (_fireStart == 0) _fireStart = Now;
            }
            mi = new MoveInput { Buttons = Dead ? MoveButtons.None : btn, SpeedScale = 1 };
            if (MatchBot == "alice") MatchAliceStep();
            else MatchBobCheck();
        }
        else if (Bot && LossBot != "") mi = LossBotStep();
        else if (Bot && CarBot != "") mi = CarBotStep();
        else if (Bot && WeaponsBot != "") mi = WeaponsBotStep();
        else if (Bot)
        {
            // scripted walk: forward, changing heading every 1.5 s, a jump now and then
            var t = _elapsed;
            var phase = (int)(t / 1.5);
            var yaw = (float)(phase * Math.PI / 2 + (PlayerName.GetHashCode() & 3) * Math.PI / 2);
            var jump = ((int)(t * 30) % 45) == 0 ? MoveButtons.Jump : MoveButtons.None;
            mi = new MoveInput { MoveY = 1, Yaw = yaw, Buttons = jump, SpeedScale = 1 };
        }
        else mi = Dead ? new MoveInput { SpeedScale = 1, Yaw = _me.Yaw } : _me.Sample();
        if (!Bot) { ReadSlotKeys(); ApplyScope(); TickCorpses(dt); TickCarePackages(dt); }
        FxDemoInput(ref mi);
        TickRecoil(mi);
        TickBreath(mi);
        if (_gunship == null) PredictFire(mi); // rounds from a turret are the server's alone
        ClampGunnerView(ref mi);
        _me.Yaw = _gunship != null ? _gunAimYaw : mi.Yaw; // at the turret the world look stays unclamped (ticket #154)
        var ni = NetInput.From(++_seq, mi, _me.Pitch, (byte)(Weapons != null ? _wantSlot : 0));
        if (!_driving && !_controlling && _gunship == null) { _me.Simulate(ni.ToMove(SpeedFor(ni.Slot)), dt); SfxOwnMovement(dt); } // a steered missile or a gunship: the body stands still; a turret's body moves
        _pending.Add(new Pending { Input = ni, After = _me.State });
        if (_pending.Count > 120) _pending.RemoveAt(0);
        var recent = new NetInput[Math.Min(3, _pending.Count)];
        for (var i = 0; i < recent.Length; i++) recent[i] = _pending[_pending.Count - recent.Length + i].Input;
        Tx(_peer, Wire.ChanData, Wire.EncodeInputs(recent), 0);
        _conn.Flush();
        var p = _me.State.Position;
        _meMin = NVec3.Min(_meMin, p); _meMax = NVec3.Max(_meMax, p);
    }

    void RenderRemotes()
    {
        if (_lastTick == 0) return;
        var rt = _lastTick + (Time.GetTicksMsec() / 1000.0 - _lastArrival) * Wire.TickRate - InterpDelayTicks;
        foreach (var r in _remotes.Values)
            if (r.Buf.At(rt) is { } s)
            {
                r.Node.GlobalPosition = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                r.Node.Rotation = new Vector3(0, s.Yaw, 0);
                r.Node.Visible = r.State != EntityState.InVehicle;
                r.View?.SetStance(r.Stance);
                AnimateRemote(r, s.Pos);
            }
    }

    /// <summary>Shows the held weapon of the current hotbar slot (the server-confirmed one once known) low on the right; hidden while dead or driving.</summary>
    void UpdateViewmodel()
    {
        if (_viewmodel == null) return;
        var slot = Ammo?.Slot ?? _wantSlot;
        var id = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].Id : "";
        _viewmodel.Show(id);
        _viewmodel.Visible = !Dead && !_driving && !_controlling && _gunship == null && _myState != EntityState.InVehicle && _me is not { ZoomFov: > 0 };
    }

    /// <summary>Drives the remote character's animation from snapshot state: horizontal speed of the interpolated position, a recent shot event, and the dead state.</summary>
    void AnimateRemote(Remote r, NVec3 pos)
    {
        var now = Now;
        var p = new Vector3(pos.X, pos.Y, pos.Z);
        if (r.LastT > 0 && now > r.LastT + 0.001)
        {
            var d = p - r.LastPos; d.Y = 0;
            var sp = d.Length() / (float)(now - r.LastT);
            if (sp > 30f) sp = 0; // a teleport (respawn), not running
            r.Speed = Mathf.Lerp(r.Speed, sp, 0.3f);
            SfxRemoteStep(r, p, (float)(now - r.LastT));
        }
        r.LastPos = p; r.LastT = now;
        var weaponId = Weapons != null && r.Slot >= 1 && r.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[r.Slot - 1].Id : "";
        r.View?.Apply(r.Speed, now < r.FireUntil, r.State == EntityState.Dead, weaponId);
    }

    void Debug(ushort victim)
    {
        Tx(_peer!, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, MatchCodec.EncodeDebugKill(victim))), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
    }

    sealed class MatchBotAlice
    {
        public ushort Victim;
        public bool Attempted, Verified, Killed;
        public double AttemptAt;
        public int KillsBefore, KillsSent, Attempts;
        public bool WasDead;
    }

    int MyKills() => Match.Rows.FirstOrDefault(r => r.Id == _myId).Kills;

    void MatchAliceStep()
    {
        var a = _alice;
        var now = Now;
        if (Match.Restarts > 0) { MatchBotFinishAlice(); return; }
        if (Match.Phase != MatchPhase.Running) return;
        if (a.Victim == 0)
        {
            var row = Match.Rows.FirstOrDefault(r => r.Name == "bob");
            if (row.Name == null) return;
            a.Victim = row.Id;
        }
        if (!_dec.States.TryGetValue(a.Victim, out var v)) return;
        switch (v.State)
        {
            case EntityState.Dead: a.Attempted = a.Verified = a.Killed = false; break;
            case EntityState.Protected when !a.Attempted:
                a.Attempted = true; a.AttemptAt = now; a.KillsBefore = MyKills(); a.Attempts++;
                GD.Print($"client alice: trying to kill protected bob (attempt {a.Attempts})");
                Debug(a.Victim);
                break;
            case EntityState.Protected when a.Attempted && !a.Verified && now - a.AttemptAt >= 0.4:
                if (MyKills() != a.KillsBefore) { MatchFail("kill landed on a protected player"); return; }
                a.Verified = true;
                break;
            case EntityState.Alive when !a.Killed && Match.Phase == MatchPhase.Running:
                a.Killed = true; a.KillsSent++;
                GD.Print($"client alice: killing unprotected bob (kill {a.KillsSent})");
                Debug(a.Victim);
                break;
        }
        // a respawn must take at least the announced delay
        if (Match.DiedAt.TryGetValue(a.Victim, out var died) && Match.SpawnedAt.TryGetValue(a.Victim, out var sp) && sp > died && Match.LastDeath.TryGetValue(a.Victim, out var dm)
            && sp - died < dm.RespawnDelay - 0.2 && !_respawnFailed)
        { _respawnFailed = true; MatchFail($"respawn after {sp - died:F2}s, expected {dm.RespawnDelay:F1}s"); }
    }

    bool _respawnFailed;

    void MatchBotFinishAlice()
    {
        var a = _alice;
        var ok = Match.RoundsEnded == 1 && Match.Restarts == 1 && Match.Rows.All(r => r.Kills == 0 && r.Deaths == 0)
                 && a.KillsSent >= 2 && a.Attempts >= 2 && !_respawnFailed;
        GD.Print($"client alice: kills sent {a.KillsSent}, protection attempts {a.Attempts}, rounds ended {Match.RoundsEnded}, restarts {Match.Restarts}");
        MatchResult(ok, ok ? "" : "round did not play out as expected");
    }

    void MatchBobCheck()
    {
        if (Match.Restarts > 0)
        {
            var ok = _deaths >= 2 && _sawProtected && _earlyEnds >= 1 && Match.RoundsEnded == 1;
            GD.Print($"client bob: deaths {_deaths}, saw protection {_sawProtected}, early protection ends {_earlyEnds}, rounds ended {Match.RoundsEnded}");
            MatchResult(ok, ok ? "" : "bob did not observe the expected events");
        }
    }

    void MatchFail(string why) => MatchResult(false, why);

    void MatchResult(bool ok, string why)
    {
        if (_done) return;
        _done = true;
        if (!ok) GD.PrintErr($"client {PlayerName}: match smoke failure: {why}");
        GD.Print(ok ? "SHOOTER_MATCH_CLIENT_OK" : "SHOOTER_MATCH_CLIENT_FAIL");
        GetTree().Quit(ok ? 0 : 2);
    }

    void FinishBot()
    {
        _done = true;
        var own = new Vector2(_meMax.X - _meMin.X, _meMax.Z - _meMin.Z).Length();
        var seen = _remotes.Count;
        var best = 0f;
        foreach (var r in _remotes.Values) best = MathF.Max(best, new Vector2(r.Max.X - r.Min.X, r.Max.Z - r.Min.Z).Length());
        GD.Print($"client {PlayerName}: moved {own:F1} m (predicted), saw {seen} remote player(s), remote moved up to {best:F1} m, snapshots {_snaps}, reconciliations {_recon}, max error {_maxErr:F3} m");
        var ok = own >= 2f && seen >= 1 && best >= 1f && _snaps > 30;
        GD.Print(ok ? "SHOOTER_NET_CLIENT_OK" : "SHOOTER_NET_CLIENT_FAIL");
        GetTree().Quit(ok ? 0 : 2);
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
        if (_me == null || _gunship != null) return; // at a turret the zoom is the gunship's
        var slot = Ammo?.Slot ?? _wantSlot;
        var zoom = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].ZoomFov : 0f;
        var on = zoom > 0 && !Dead && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        _me.ZoomFov = on ? zoom : 0f;
        // iron sights: any non-scoped, non-melee weapon while zoom is held
        var kind = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].Kind : WeaponKind.Melee;
        if (_viewmodel != null)
            _viewmodel.Aiming = zoom <= 0 && kind != WeaponKind.Melee && !Dead && !_driving && _myState != EntityState.InVehicle
                && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
    }

    void ReadSlotKeys()
    {
        if (Dead || Input.MouseMode != Input.MouseModeEnum.Captured || ChooserOpen) return;
        for (var k = 0; k < Math.Min(5, _hotbar.Length); k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) _wantSlot = _hotbar[k] + 1;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Bot || Weapons == null || Dead || ChooserOpen || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        if (e is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            _wantSlot = CycleSlot(_wantSlot, b.ButtonIndex == MouseButton.WheelDown ? 1 : -1);
        }
    }

    void OnWeaponEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Ammo when WeaponCodec.DecodeAmmo(payload) is { } a:
                Ammo = a; _ammoAt = Now; _ammoLog.Add((Now, a));
                PredictOnAmmo(a);
                SfxAmmo(a);
                HudAmmo();
                break;
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } sh:
                _shotLog.Add((Now, sh));
                if (_remotes.TryGetValue(sh.Shooter, out var shooter)) shooter.FireUntil = Now + 0.45;
                if (sh.Shooter == _myId && Weapons != null && sh.Weapon < Weapons.Weapons.Count) _spread.Shot(Weapons.Weapons[sh.Weapon], Now);
                var melee = Weapons != null && sh.Weapon < Weapons.Weapons.Count && Weapons.Weapons[sh.Weapon].Kind == WeaponKind.Melee;
                if (EchoOfPredicted(sh.Shooter)) break; // already played and drawn by prediction
                var endV = new Vector3(sh.End.X, sh.End.Y, sh.End.Z);
                SfxShot(sh, melee);
                if (!melee) { if (sh.Shooter == _myId) _viewmodel?.Flash(0.4f); else if (_remotes.TryGetValue(sh.Shooter, out var flasher)) flasher.View?.Flash(); }
                ShotFx.Spawn(World, MuzzleOf(sh.Shooter, new Vector3(sh.Origin.X, sh.Origin.Y, sh.Origin.Z), endV), endV, melee);
                if (!melee) ShotHole(new Vector3(sh.Origin.X, sh.Origin.Y, sh.Origin.Z), endV);
                if (!melee) BulletSurfaceHit(new Vector3(sh.Origin.X, sh.Origin.Y, sh.Origin.Z), endV);
                break;
            case EventKind.PelletFan when WeaponCodec.DecodePelletFan(payload) is { } pf:
                OnPelletFan(pf);
                break;
            case EventKind.Launch when WeaponCodec.DecodeLaunch(payload) is { } l:
                _launchLog.Add((Now, l));
                if (_remotes.TryGetValue(l.Owner, out var launcher)) launcher.FireUntil = Now + 0.45;
                if (_rockets.ContainsKey(l.Id) == false && !_deadRockets.Contains(l.Id) && ShotFx.Enabled && Weapons != null)
                {
                    // show the rocket at once from the launch event; snapshots take over
                    var v = new NVec3(l.Velocity.X, l.Velocity.Y, l.Velocity.Z);
                    _rockets[l.Id] = new Rocket { Node = RocketFx.Make(World), Born = Now, Pos = l.Origin, Dir = NVec3.Normalize(v), At = Now, Speed = v.Length() };
                }
                if (!EchoOfPredicted(l.Owner)) { SfxLaunch(l); if (l.Owner == _myId) _viewmodel?.Flash(0.5f); else if (_remotes.TryGetValue(l.Owner, out var lf)) lf.View?.Flash(0.6f); }
                break;
            case EventKind.Explosion when WeaponCodec.DecodeExplosion(payload) is { } ex:
                _explosionLog.Add((Now, ex));
                if (_rockets.Remove(ex.Id, out var rk)) { rk.Snd?.Release(); RocketFx.Retire(World, rk.Node); }
                SfxExplosion(ex);
                _dec.Remove(ex.Id);
                _deadRockets.Add(ex.Id);
                if (_deadRockets.Count > 256) _deadRockets.Clear();
                var exR = Weapons?.Weapons.ElementAtOrDefault(ex.Weapon)?.Splash?.Radius ?? 6f;
                var exAt = new Vector3(ex.Position.X, ex.Position.Y, ex.Position.Z);
                RocketFx.Explode(World, exAt, exR);
                ShakeFor(exAt, exR);
                break;
            case EventKind.VehicleExplosion when WeaponCodec.DecodeVehicleExplosion(payload) is { } vx:
                OnVehicleExplosion(vx);
                break;
            case EventKind.Moab when MoabEvents.Decode(payload) is { } mo:
                OnMoab(mo);
                break;
            case EventKind.MissileBlast when MissileCodec.DecodeBlast(payload) is { } mb:
                OnMissileBlast(mb);
                break;
            case EventKind.Ac130Blast when Ac130Codec.DecodeBlast(payload) is { } ab:
                OnAc130Blast(ab);
                break;
            case EventKind.ImpactFx when ImpactCodec.Decode(payload) is { } imp:
                OnImpactFx(imp);
                break;
            case EventKind.HelperWarning when HelperWarningCodec.Decode(payload) is { } hw:
                OnHelperWarning(hw);
                break;
            case EventKind.Hit when WeaponCodec.DecodeHit(payload) is { } h:
                _hitLog.Add((Now, h));
                SfxHit(h);
                HudHit(h);
                GD.Print($"client {PlayerName}: hit #{h.Attacker} -> #{h.Victim} {h.Damage}{(h.Headshot ? " head" : "")}{(h.Backstab ? " backstab" : "")}{(h.Kill ? " kill" : "")}");
                break;
        }
    }

}
