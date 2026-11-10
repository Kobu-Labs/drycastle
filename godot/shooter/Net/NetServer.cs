using Godot;
using Suchohrad.ShooterCore;

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
        public ENetPacketPeer Peer = null!;
        public ushort Id;
        public string Name = "";
        public bool Joined;
        public Player Body = null!;
        public readonly Queue<NetInput> Inputs = new();
        public ushort Ack;
        public bool HasAck;
        public ushort LastSeq;
        public bool HasSeq;
        public byte Slot;
        public readonly SnapshotEncoder Enc = new();
        public Vector3 Start;
        public float MaxMoved;
    }

    public Node3D World = null!;
    /// <summary>Spawn candidates (the map's pipeline spawn points); the match state machine picks among them.</summary>
    public List<System.Numerics.Vector3> Spawns = new();
    public MatchConfig MatchConfig = new();
    /// <summary>Honour the client debug "kill" command (dev servers and the match smoke only).</summary>
    public bool DebugCommands;
    /// <summary>Match smoke mode: QuitAfter is a failure deadline; the server finishes itself after one full round and restart.</summary>
    public bool MatchSmoke;
    public float KillY = float.NegativeInfinity;
    public int Port = 27015;
    /// <summary>Quit after this many seconds and report whether two players were seen moving (smoke test); 0 = run forever.</summary>
    public double QuitAfter;

    readonly ENetConnection _host = new();
    readonly Dictionary<ENetPacketPeer, Client> _clients = new();
    uint _tick;
    ushort _nextId = 1;
    double _elapsed;
    int _peakPlayers;
    readonly Dictionary<string, float> _movedByName = new();
    readonly List<NetInput> _scratch = new();
    readonly Dictionary<ushort, Client> _byId = new();
    MatchSim _match = null!;
    // match smoke bookkeeping
    int _kills, _roundsEnded, _restarts, _ignoredHits, _earlyProtectionEnds;
    double _finishAt;

    public override void _Ready()
    {
        var err = _host.CreateHostBound("*", Port, 32, Wire.Channels, 0, 0);
        if (err != Error.Ok)
        {
            GD.PrintErr($"server: cannot listen on port {Port}: {err}");
            GetTree().Quit(2);
            return;
        }
        _match = new MatchSim(MatchConfig, () => _elapsed, Spawns);
        _match.Event += OnMatchEvent;
        GD.Print($"server: {Spawns.Count} spawns, y {(Spawns.Count > 0 ? Spawns.Min(v => v.Y) : 0)}..{(Spawns.Count > 0 ? Spawns.Max(v => v.Y) : 0)}, killY {KillY}");
        GD.Print($"SHOOTER_NET_LISTENING port={Port} tick={Wire.TickRate}Hz");
    }

    public override void _ExitTree() => _host.Destroy();

    public override void _PhysicsProcess(double delta)
    {
        _tick++;
        if (!_spawnsChecked)
        {
            if (_tick >= 3) CheckSpawnGround();
            return; // not accepting connections until the spawn list is validated
        }
        _elapsed += delta;
        Poll();
        _match.Update();
        foreach (var c in _clients.Values.ToList()) if (c.Joined) SimulateClient(c, (float)delta);
        SendSnapshots();
        if (_clients.Count > _peakPlayers) _peakPlayers = _clients.Count;
        foreach (var c in _clients.Values) if (c.Joined) _movedByName[c.Name] = c.MaxMoved;
        if (_tick % (Wire.TickRate * 2) == 0 && _clients.Count > 0)
            GD.Print($"server: tick {_tick}, players {string.Join(", ", _clients.Values.Where(c => c.Joined).Select(c => $"{c.Name}#{c.Id}@({c.Body.GlobalPosition.X:F1},{c.Body.GlobalPosition.Y:F1},{c.Body.GlobalPosition.Z:F1}) ack={c.Ack}"))}");
        if (MatchSmoke)
        {
            if (_finishAt > 0 && _elapsed >= _finishAt) FinishMatch();
            else if (QuitAfter > 0 && _elapsed >= QuitAfter) FinishMatch();
        }
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
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(sp.X, sp.Y + 2f, sp.Z), new Vector3(sp.X, sp.Y - 5f, sp.Z));
            return space.IntersectRay(q).Count == 0;
        });
        GD.Print($"server: {Spawns.Count}/{before} spawn candidates have ground");
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
                case ENetConnection.EventType.Connect: _clients[peer] = new Client { Peer = peer }; break;
                case ENetConnection.EventType.Disconnect: Drop(peer); break;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0) OnPacket(peer, peer.GetPacket());
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
            if (m is HelloMsg h && !c.Joined) OnHello(c, h);
            else if (m is EventMsg { Kind: (byte)EventKind.Debug } e && c.Joined && DebugCommands) OnDebug(c, e.Payload);
        }
        else if (c.Joined && d[0] == Wire.TInput)
        {
            _scratch.Clear();
            if (!Wire.TryDecodeInputs(d, _scratch)) return;
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
        if (h.Version != Wire.ProtocolVersion) { GD.Print($"server: rejecting {h.Name}: protocol {h.Version}"); c.Peer.PeerDisconnect(); return; }
        c.Id = _nextId++;
        c.Name = string.IsNullOrWhiteSpace(h.Name) ? $"player{c.Id}" : h.Name.Trim();
        // health, death and respawn belong to the match state machine, not the body
        c.Body = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, Authoritative = false, Name = $"P{c.Id}" };
        World.AddChild(c.Body);
        c.Body.Teleport(Vector3.Zero);
        c.Joined = true;
        _byId[c.Id] = c;
        Send(c, new WelcomeMsg(Wire.ProtocolVersion, c.Id, Wire.TickRate, _tick));
        foreach (var o in _clients.Values.Where(o => o.Joined))
        {
            Send(c, new JoinMsg(o.Id, o.Name));
            if (o != c) Send(o, new JoinMsg(c.Id, c.Name));
        }
        _match.AddPlayer(c.Id, c.Name); // spawns the player (event) and broadcasts the scoreboard
        _host.Flush();
        GD.Print($"server: {c.Name} joined as #{c.Id} ({_clients.Count(x => x.Value.Joined)} players)");
    }

    void Drop(ENetPacketPeer peer)
    {
        if (!_clients.Remove(peer, out var c)) return;
        if (!c.Joined) return;
        c.Body.QueueFree();
        _byId.Remove(c.Id);
        foreach (var o in _clients.Values.Where(o => o.Joined)) Send(o, new LeaveMsg(c.Id));
        _match.RemovePlayer(c.Id);
        GD.Print($"server: {c.Name} #{c.Id} left");
    }

    void Send(Client c, ReliableMsg m) => c.Peer.Send(Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);

    /// <summary>Broadcast an event envelope to every joined client (shot, hit, kill, ... from later issues).</summary>
    public void BroadcastEvent(EventKind kind, byte[] payload)
    {
        var m = new EventMsg((byte)kind, payload);
        foreach (var c in _clients.Values.Where(c => c.Joined)) Send(c, m);
    }

    void OnDebug(Client c, byte[] payload)
    {
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
            case PhaseChanged { Phase: MatchPhase.Ending }: _roundsEnded++; GD.Print($"server: round over, winners {string.Join(",", _match.Winners)}"); break;
            case PhaseChanged { Phase: MatchPhase.Running }:
                if (_roundsEnded > 0) { _restarts++; _finishAt = _elapsed + 1.5; }
                GD.Print("server: round started");
                break;
            case PlayerSpawned s:
                if (_byId.TryGetValue(s.Id, out var sc))
                {
                    sc.Body.Teleport(new Vector3(s.Position.X, s.Position.Y + 0.1f, s.Position.Z));
                    sc.Body.Health = MoveConsts.MaxHealth;
                    sc.Inputs.Clear(); sc.Start = sc.Body.GlobalPosition; sc.MaxMoved = 0;
                }
                BroadcastEvent(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(s.Id, s.Position, s.ProtectionSeconds)));
                break;
            case PlayerKilled k:
                _kills += k.Killer != k.Victim ? 1 : 0;
                BroadcastEvent(EventKind.Kill, MatchCodec.Encode(new KillMsg(k.Killer, k.Victim, k.Weapon)));
                break;
            case PlayerDied d: BroadcastEvent(EventKind.Death, MatchCodec.Encode(new DeathMsg(d.Victim, d.Killer, d.RespawnDelay))); break;
        }
    }

    MatchStateMsg BuildMatchState()
    {
        var rows = _match.Ranking().Select(p => new ScoreRow(p.Id, p.Name, p.Kills, p.Deaths, Ping(p.Id))).ToList();
        return new MatchStateMsg(_match.Phase, _match.TimeRemaining, _match.Winners, rows);
    }

    ushort Ping(ushort id) =>
        _byId.TryGetValue(id, out var c) ? (ushort)Math.Clamp(c.Peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime), 0, ushort.MaxValue) : (ushort)0;

    void SimulateClient(Client c, float dt)
    {
        // normally one input per tick; if the queue backs up (jitter), catch up with a second one
        var n = c.Inputs.Count > 3 ? 2 : 1;
        for (var k = 0; k < n && c.Inputs.Count > 0; k++)
        {
            var i = c.Inputs.Dequeue();
            c.Ack = i.Seq; c.HasAck = true;
            if (_match.Get(c.Id)?.Life != PlayerLife.Alive) continue; // dead or waiting: inputs are acked but ignored
            if ((i.Buttons & (ushort)MoveButtons.Fire) != 0 && _match.NotifyFired(c.Id)) _earlyProtectionEnds++;
            c.Body.Yaw = Quant.Yaw(i.Yaw);
            c.Body.Pitch = Quant.Pitch(i.Pitch);
            c.Body.Simulate(i.ToMove(), dt);
            c.Slot = i.Slot;
            if (c.Body.LastFallDamage > 0) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Fall, c.Body.LastFallDamage);
            if (c.Body.GlobalPosition.Y < KillY) GD.Print($"server: {c.Name} fell out of the world at {c.Body.GlobalPosition} (killY {KillY}, spawn {_match.Get(c.Id)?.Position})");
            if (c.Body.GlobalPosition.Y < KillY) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Void, 9999, ignoreProtection: true);
        }
        _match.SetPosition(c.Id, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z));
        c.Body.Health = _match.Get(c.Id)?.Health ?? 0;
        var p = c.Body.GlobalPosition - c.Start;
        c.MaxMoved = Mathf.Max(c.MaxMoved, new Vector2(p.X, p.Z).Length());
    }

    void SendSnapshots()
    {
        var ents = new List<EntityRecord>();
        foreach (var c in _clients.Values)
            if (c.Joined)
                ents.Add(EntityRecord.Make(c.Id, EntityType.Player, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z),
                    c.Body.Yaw, c.Body.Pitch, c.Body.Health, _match.StateOf(c.Id), c.Slot));
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            foreach (var pkt in c.Enc.Encode(_tick, c.Ack, ents)) c.Peer.Send(Wire.ChanData, pkt, 0);
        }
        _host.Flush();
    }
}
