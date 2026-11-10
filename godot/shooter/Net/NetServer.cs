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
    public Func<Vector3> PickSpawn = () => Vector3.Zero;
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

    public override void _Ready()
    {
        var err = _host.CreateHostBound("*", Port, 32, Wire.Channels, 0, 0);
        if (err != Error.Ok)
        {
            GD.PrintErr($"server: cannot listen on port {Port}: {err}");
            GetTree().Quit(2);
            return;
        }
        GD.Print($"SHOOTER_NET_LISTENING port={Port} tick={Wire.TickRate}Hz");
    }

    public override void _ExitTree() => _host.Destroy();

    public override void _PhysicsProcess(double delta)
    {
        _tick++;
        _elapsed += delta;
        Poll();
        foreach (var c in _clients.Values) if (c.Joined) SimulateClient(c, (float)delta);
        SendSnapshots();
        if (_clients.Count > _peakPlayers) _peakPlayers = _clients.Count;
        foreach (var c in _clients.Values) if (c.Joined) _movedByName[c.Name] = c.MaxMoved;
        if (_tick % (Wire.TickRate * 2) == 0 && _clients.Count > 0)
            GD.Print($"server: tick {_tick}, players {string.Join(", ", _clients.Values.Where(c => c.Joined).Select(c => $"{c.Name}#{c.Id}@({c.Body.GlobalPosition.X:F1},{c.Body.GlobalPosition.Z:F1}) ack={c.Ack}"))}");
        if (QuitAfter > 0 && _elapsed >= QuitAfter) Finish();
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
            if (Wire.DecodeReliable(d) is HelloMsg h && !c.Joined) OnHello(c, h);
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
        c.Body = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, KillY = KillY, PickSpawn = PickSpawn, Name = $"P{c.Id}" };
        World.AddChild(c.Body);
        c.Body.Respawn();
        c.Start = c.Body.GlobalPosition;
        c.Joined = true;
        Send(c, new WelcomeMsg(Wire.ProtocolVersion, c.Id, Wire.TickRate, _tick));
        foreach (var o in _clients.Values.Where(o => o.Joined))
        {
            Send(c, new JoinMsg(o.Id, o.Name));
            if (o != c) Send(o, new JoinMsg(c.Id, c.Name));
        }
        _host.Flush();
        GD.Print($"server: {c.Name} joined as #{c.Id} ({_clients.Count(x => x.Value.Joined)} players)");
    }

    void Drop(ENetPacketPeer peer)
    {
        if (!_clients.Remove(peer, out var c)) return;
        if (!c.Joined) return;
        c.Body.QueueFree();
        foreach (var o in _clients.Values.Where(o => o.Joined)) Send(o, new LeaveMsg(c.Id));
        GD.Print($"server: {c.Name} #{c.Id} left");
    }

    void Send(Client c, ReliableMsg m) => c.Peer.Send(Wire.ChanReliable, Wire.Encode(m), (int)ENetPacketPeer.FlagReliable);

    /// <summary>Broadcast an event envelope to every joined client (shot, hit, kill, ... from later issues).</summary>
    public void BroadcastEvent(EventKind kind, byte[] payload)
    {
        var m = new EventMsg((byte)kind, payload);
        foreach (var c in _clients.Values.Where(c => c.Joined)) Send(c, m);
    }

    void SimulateClient(Client c, float dt)
    {
        // normally one input per tick; if the queue backs up (jitter), catch up with a second one
        var n = c.Inputs.Count > 3 ? 2 : 1;
        for (var k = 0; k < n && c.Inputs.Count > 0; k++)
        {
            var i = c.Inputs.Dequeue();
            c.Body.Yaw = Quant.Yaw(i.Yaw);
            c.Body.Pitch = Quant.Pitch(i.Pitch);
            c.Body.Simulate(i.ToMove(), dt);
            c.Slot = i.Slot;
            c.Ack = i.Seq; c.HasAck = true;
        }
        var p = c.Body.GlobalPosition - c.Start;
        c.MaxMoved = Mathf.Max(c.MaxMoved, new Vector2(p.X, p.Z).Length());
    }

    void SendSnapshots()
    {
        var ents = new List<EntityRecord>();
        foreach (var c in _clients.Values)
            if (c.Joined)
                ents.Add(EntityRecord.Make(c.Id, EntityType.Player, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z),
                    c.Body.Yaw, c.Body.Pitch, c.Body.Health, EntityState.Alive, c.Slot));
        foreach (var c in _clients.Values)
        {
            if (!c.Joined) continue;
            foreach (var pkt in c.Enc.Encode(_tick, c.Ack, ents)) c.Peer.Send(Wire.ChanData, pkt, 0);
        }
        _host.Flush();
    }
}
