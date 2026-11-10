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
    sealed class Remote { public Node3D Node = null!; public readonly Interpolator Buf = new(); public NVec3 Min = new(float.MaxValue), Max = new(float.MinValue); }

    public const double InterpDelayTicks = 3.0;
    const float SnapDistance = 2f, ReconcileEpsilon = 0.03f;

    public Node3D World = null!;
    public string Host = "127.0.0.1";
    public int Port = 27015;
    public string PlayerName = "player";
    public bool Bot;
    public double BotSeconds = 6;
    public bool Headless;
    public float KillY = float.NegativeInfinity;
    /// <summary>Match smoke role: "alice" (kills bob with the debug command) or "bob" (fires after each spawn); empty = normal bot/player.</summary>
    public string MatchBot = "";
    /// <summary>Match events stored for the HUD to come: phase, countdown, scoreboard rows, kill feed, own spawn/death.</summary>
    public readonly ClientMatchState Match = new();
    public bool Dead { get; private set; }
    /// <summary>weapons.json: hotbar order and the per-weapon movement speed multiplier used by prediction.</summary>
    public WeaponsFile Weapons = null!;
    /// <summary>Weapons smoke role: "a" (shoots) or "b" (stands still and counts hits); empty = none.</summary>
    public string WeaponsBot = "";
    /// <summary>Latest owner ammo state from the server (null before the first one).</summary>
    public AmmoMsg? Ammo { get; private set; }
    readonly List<(double T, ShotMsg M)> _shotLog = new();
    readonly List<(double T, HitMsg M)> _hitLog = new();
    readonly List<(double T, AmmoMsg M)> _ammoLog = new();
    int _wantSlot = 1;
    Label? _hud;
    double _ammoAt;

    readonly ENetConnection _conn = new();
    ENetPacketPeer? _peer;
    Player? _me;
    ushort _myId;
    bool _welcomed, _gotSelf;
    ushort _seq;
    readonly List<Pending> _pending = new();
    readonly SnapshotDecoder _dec = new();
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
        var err = _conn.CreateHost(1, Wire.Channels, 0, 0);
        if (err != Error.Ok) { Fail($"CreateHost failed: {err}"); return; }
        _peer = _conn.ConnectToHost(Host, Port, Wire.Channels, 0);
        if (_peer == null) { Fail("ConnectToHost failed"); return; }
        GD.Print($"client: connecting to {Host}:{Port} as {PlayerName}");
    }

    public override void _ExitTree() => _conn.Destroy();

    void Fail(string why)
    {
        GD.PrintErr($"client: {why}");
        if (Bot) { GD.Print("SHOOTER_NET_CLIENT_FAIL"); GetTree().Quit(2); }
        else GetTree().Quit(2);
        _done = true;
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        Poll();
        if (!_welcomed)
        {
            _connectElapsed += delta;
            if (_connectElapsed > 10) Fail("connect timeout");
            return;
        }
        RenderRemotes();
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
                    peer.Send(Wire.ChanReliable, Wire.Encode(new HelloMsg(Wire.ProtocolVersion, PlayerName)), (int)ENetPacketPeer.FlagReliable);
                    _conn.Flush();
                    break;
                case ENetConnection.EventType.Disconnect:
                    if (!_done) Fail("server disconnected");
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
    }

    void OnReliable(ReliableMsg? m)
    {
        switch (m)
        {
            case WelcomeMsg w:
                _myId = w.PlayerId; _welcomed = true;
                _me = new Player { Name = "Me", UseCamera = !Bot, AutoSim = false, Authoritative = false, CaptureMouse = !Bot, KillY = KillY };
                World.AddChild(_me);
                if (WeaponsBot != "") NetServer.AddArenaColliders(World);
                if (Bot) _me.InputOverride = new MoveInput { SpeedScale = 1 };
                GD.Print($"client: welcome, id {_myId}, server tick {w.ServerTick}");
                break;
            case JoinMsg j: _names[j.PlayerId] = j.Name; GD.Print($"client: {j.Name} #{j.PlayerId} joined"); break;
            case LeaveMsg l:
                _names.Remove(l.PlayerId); Match.Remove(l.PlayerId); _dec.Remove(l.PlayerId);
                if (_remotes.Remove(l.PlayerId, out var r)) r.Node.QueueFree();
                break;
            case EventMsg e:
                OnWeaponEvent((EventKind)e.Kind, e.Payload);
                OnMatchEvent((EventKind)e.Kind, e.Payload);
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
            case EventKind.MatchState: GD.Print($"client {PlayerName}: {Match.ScoreboardText(Now)}"); break;
            case EventKind.Kill:
                var k = Match.KillFeed[^1];
                GD.Print($"client {PlayerName}: kill feed: {Match.NameOf(k.Killer)} -> {Match.NameOf(k.Victim)} (weapon {k.Weapon})");
                break;
            case EventKind.Spawn:
                var sp = Match.LastSpawn.Values.Last();
                GD.Print($"client {PlayerName}: spawn #{sp.Id} protected {sp.Protection:F1}s");
                if (sp.Id == _myId && _me != null)
                {
                    _wantSlot = StartSlot();
                    Dead = false; _gotSelf = false; _pending.Clear(); // the next snapshot carries the spawn position
                    _spawnAt = Now; _fireStart = 0; _earlyCounted = false; _spawnProtection = sp.Protection;
                }
                break;
            case EventKind.Death:
                var d = Match.LastDeath.Values.Last();
                GD.Print($"client {PlayerName}: death #{d.Victim} by #{d.Killer}, respawn in {d.RespawnDelay:F1}s");
                if (d.Victim == _myId) { Dead = true; _deaths++; }
                break;
        }
    }

    /// <summary>Raised for every reliable event envelope (shot, hit, kill, ... handled by later issues).</summary>
    public event Action<EventKind, byte[]>? EventReceived;

    void OnSnapshot(byte[] d)
    {
        if (_me == null || !_dec.Apply(d, out var h)) return;
        if (_lastTick != 0 && h.Tick < _lastTick) return;
        _lastTick = h.Tick; _lastArrival = Time.GetTicksMsec() / 1000.0; _snaps++;
        foreach (var id in _dec.RemoveStale(h.Tick, 90)) if (_remotes.Remove(id, out var gone)) gone.Node.QueueFree();
        foreach (var (id, rec) in _dec.States)
        {
            if (id == _myId) { OnSelf(rec, h.Ack); continue; }
            if (rec.Type != EntityType.Player) continue; // projectiles and vehicles are rendered by later issues
            if (!_remotes.TryGetValue(id, out var r)) { r = MakeRemote(id); _remotes[id] = r; }
            r.Buf.Push(h.Tick, rec.Position, rec.YawRad, rec.PitchRad);
            r.Min = NVec3.Min(r.Min, rec.Position); r.Max = NVec3.Max(r.Max, rec.Position);
        }
    }

    Remote MakeRemote(ushort id)
    {
        var node = new Node3D { Name = $"Remote{id}" };
        node.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = MoveConsts.CapsuleRadius, Height = MoveConsts.CapsuleHeight },
            Position = new Vector3(0, MoveConsts.CapsuleHeight / 2, 0),
        });
        node.AddChild(new Label3D
        {
            Text = _names.TryGetValue(id, out var n) ? n : $"#{id}",
            Position = new Vector3(0, MoveConsts.CapsuleHeight + 0.3f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = 0.005f, NoDepthTest = true,
        });
        World.AddChild(node);
        return new Remote { Node = node };
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
        if (rec.State == EntityState.Protected) _sawProtected = true;
        if (rec.State == EntityState.Alive && _fireStart > 0 && !_earlyCounted && Now - _fireStart < 0.6 && _fireStart - _spawnAt < _spawnProtection - 1.0) { _earlyEnds++; _earlyCounted = true; }
        var idx = _pending.FindIndex(p => p.Input.Seq == ack);
        if (idx < 0) return;
        var predicted = _pending[idx].After;
        var err = (predicted.Position - sp).Length();
        _maxErr = MathF.Max(_maxErr, err);
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
        if (Bot && _elapsed >= BotSeconds) { if (WeaponsBot != "") WeaponsFail("timeout"); else if (MatchBot != "") MatchFail("timeout"); else FinishBot(); return; }
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
        if (!Bot) ReadSlotKeys();
        _me.Yaw = mi.Yaw;
        var ni = NetInput.From(++_seq, mi, _me.Pitch, (byte)(Weapons != null ? _wantSlot : 0));
        _me.Simulate(ni.ToMove(SpeedFor(ni.Slot)), dt);
        _pending.Add(new Pending { Input = ni, After = _me.State });
        if (_pending.Count > 120) _pending.RemoveAt(0);
        var recent = new NetInput[Math.Min(3, _pending.Count)];
        for (var i = 0; i < recent.Length; i++) recent[i] = _pending[_pending.Count - recent.Length + i].Input;
        _peer.Send(Wire.ChanData, Wire.EncodeInputs(recent), 0);
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
            }
    }

    void Debug(ushort victim)
    {
        _peer!.Send(Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, MatchCodec.EncodeDebugKill(victim))), (int)ENetPacketPeer.FlagReliable);
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

    // ---- weapons: input, events, debug HUD line, placeholder effects ----

    int StartSlot() => Weapons == null ? 1 : Math.Max(1, Weapons.Weapons.FindIndex(w => w.Id == Weapons.StartingWeapon) + 1);

    /// <summary>Movement speed multiplier of the weapon in <paramref name="slot"/> (0: none, 1.0). The server applies the same value.</summary>
    float SpeedFor(byte slot) =>
        Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].MoveSpeedMultiplier : 1f;

    void ReadSlotKeys()
    {
        if (Dead || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        for (var k = 0; k < 4; k++)
            if (Input.IsKeyPressed((Key)((int)Key.Key1 + k))) _wantSlot = k + 1;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Bot || Weapons == null || Dead || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        if (e is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            var n = Weapons.Weapons.Count;
            _wantSlot = (_wantSlot - 1 + (b.ButtonIndex == MouseButton.WheelDown ? 1 : n - 1)) % n + 1;
        }
    }

    void OnWeaponEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Ammo when WeaponCodec.DecodeAmmo(payload) is { } a:
                Ammo = a; _ammoAt = Now; _ammoLog.Add((Now, a));
                break;
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } sh:
                _shotLog.Add((Now, sh));
                var melee = Weapons != null && sh.Weapon < Weapons.Weapons.Count && Weapons.Weapons[sh.Weapon].Kind == WeaponKind.Melee;
                ShotFx.Spawn(World, new Vector3(sh.Origin.X, sh.Origin.Y, sh.Origin.Z), new Vector3(sh.End.X, sh.End.Y, sh.End.Z), melee);
                break;
            case EventKind.Hit when WeaponCodec.DecodeHit(payload) is { } h:
                _hitLog.Add((Now, h));
                GD.Print($"client {PlayerName}: hit #{h.Attacker} -> #{h.Victim} {h.Damage}{(h.Headshot ? " head" : "")}{(h.Backstab ? " backstab" : "")}{(h.Kill ? " kill" : "")}");
                break;
        }
    }

    /// <summary>One debug text line (weapon, ammo): the real HUD is a separate issue.</summary>
    void UpdateHud()
    {
        if (!ShotFx.Enabled || Weapons == null) return;
        if (_hud == null)
        {
            _hud = new Label { Position = new Vector2(16, 16) };
            _hud.AddThemeFontSizeOverride("font_size", 18);
            var layer = new CanvasLayer();
            layer.AddChild(_hud);
            AddChild(layer);
        }
        if (Ammo is not { } a || a.Slot < 1 || a.Slot > Weapons.Weapons.Count) { _hud.Text = ""; return; }
        var w = Weapons.Weapons[a.Slot - 1];
        var (mag, reserve) = a.Slots[a.Slot - 1];
        var left = a.ReloadRemaining - (Now - _ammoAt);
        _hud.Text = w.MagazineSize == 0 ? $"[{a.Slot}] {w.Id}" : $"[{a.Slot}] {w.Id}  {mag} / {reserve}" + (left > 0 ? $"  reloading {left:F1}s" : "");
    }
}
