using System.Buffers.Binary;
using Godot;

/// <summary>Approach B server: raw ENetConnection, quantised delta snapshots, optional radius interest management.</summary>
public class CustomServer : IRole
{
    class Cli
    {
        public int Id; public ENetPacketPeer Peer = null!; public NetPlayer Player = null!;
        public Wire.Ent[] Last = new Wire.Ent[256];
        public int Ack;
    }

    readonly ServerCore _core;
    readonly ENetConnection _host = new();
    readonly Dictionary<ENetPacketPeer, Cli> _clients = new();
    readonly Cli?[] _byId = new Cli?[256];
    readonly float _interest;
    readonly byte[] _buf = new byte[1400];
    int _nextId = 1;
    long _bytesSnap, _entriesFull, _entriesDelta, _snaps;

    public CustomServer(Node main, ServerCore core)
    {
        _core = core; core.Approach = "custom";
        _interest = core.Cfg.Float("interest", 0);
        core.Extra["interest_radius_m"] = _interest;
        var err = _host.CreateHostBound("*", core.Cfg.Int("port", 27015), core.Cfg.Int("n", 16) + 8, Wire.Channels, 0, 0);
        if (err != Error.Ok) throw new InvalidOperationException($"CreateHostBound failed: {err}");
        core.Host = _host;
        core.SendControl = code =>
        {
            foreach (var c in _clients.Values) c.Peer.Send(Wire.ChanCtl, new[] { code == Net.CtlGo ? Wire.TGo : Wire.TStop }, (int)ENetPacketPeer.FlagReliable);
            _host.Flush();
        };
    }

    public void Frame(double dt)
    {
        Poll();
        _core.Frame();
    }

    void Poll()
    {
        for (var guard = 0; guard < 100000; guard++)
        {
            var r = _host.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev == ENetConnection.EventType.None || ev == ENetConnection.EventType.Error) break;
            var peer = (ENetPacketPeer)r[1].AsGodotObject();
            switch (ev)
            {
                case ENetConnection.EventType.Connect: OnConnect(peer); break;
                case ENetConnection.EventType.Disconnect: OnDisconnect(peer); break;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0) OnPacket(peer, peer.GetPacket());
                    break;
            }
        }
    }

    void OnConnect(ENetPacketPeer peer)
    {
        if (_nextId > 250) { peer.PeerDisconnect(); return; }
        var c = new Cli { Id = _nextId++, Peer = peer };
        c.Player = NetPlayer.Make(c.Id, _core.RandomSpawn(), true);
        _core.World.AddChild(c.Player);
        _core.Register(c.Player);
        _clients[peer] = c; _byId[c.Id] = c;
        var w = new byte[14];
        w[0] = Wire.TWelcome; w[1] = (byte)c.Id;
        BinaryPrimitives.WriteSingleLittleEndian(w.AsSpan(2), _core.BMin.X);
        BinaryPrimitives.WriteSingleLittleEndian(w.AsSpan(6), _core.BMin.Y);
        BinaryPrimitives.WriteSingleLittleEndian(w.AsSpan(10), _core.BMin.Z);
        peer.Send(Wire.ChanCtl, w, (int)ENetPacketPeer.FlagReliable);
    }

    void OnDisconnect(ENetPacketPeer peer)
    {
        if (!_clients.Remove(peer, out var c)) return;
        _byId[c.Id] = null;
        _core.Unregister(c.Player);
        c.Player.QueueFree();
    }

    void OnPacket(ENetPacketPeer peer, byte[] d)
    {
        if (d.Length < 8 || d[0] != Wire.TInput || !_clients.TryGetValue(peer, out var c)) return;
        var seq = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(1));
        c.Player.SetInput(seq, (sbyte)d[3], (sbyte)d[4], BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5)), d[7]);
        c.Ack = seq;
    }

    public void Physics(double dt)
    {
        _core.TickBegin();
        _core.Sim((float)dt);
        SendSnapshots();
        _core.TickEnd();
    }

    void SendSnapshots()
    {
        var ps = _core.Players;
        var n = ps.Count;
        if (n == 0) return;
        var cur = new Wire.Ent[n];
        for (var i = 0; i < n; i++) cur[i] = Wire.Quantise(ps[i], _core.BMin);
        var r2 = _interest * _interest;
        var tick = _core.Tick;
        foreach (var c in _clients.Values)
        {
            var count = 0;
            var o = 8;
            for (var i = 0; i < n; i++)
            {
                var p = ps[i];
                if (_interest > 0 && p != c.Player && p.Position.DistanceSquaredTo(c.Player.Position) > r2)
                {
                    c.Last[p.Id].Valid = false; // re-entering interest range gets a full entry
                    continue;
                }
                ref var last = ref c.Last[p.Id];
                var q = cur[i];
                var isFull = false;
                if (!last.Valid || tick - last.LastFull >= Wire.KeyEvery)
                {
                    _buf[o++] = (byte)p.Id; _buf[o++] = (byte)(Wire.FPosAbs | Wire.FYaw | Wire.FVel);
                    W16(ref o, q.X); W16(ref o, q.Y); W16(ref o, q.Z); W16(ref o, q.Yaw);
                    _buf[o++] = (byte)q.Vx; _buf[o++] = (byte)q.Vy; _buf[o++] = (byte)q.Vz;
                    _entriesFull++;
                    // stagger keyframes over entities and clients so they do not all land on the same tick
                    q.LastFull = tick - (c.Id * 7 + p.Id * 3) % Wire.KeyEvery;
                    isFull = true;
                }
                else
                {
                    var flags = 0;
                    int dx = q.X - last.X, dy = q.Y - last.Y, dz = q.Z - last.Z;
                    var posChanged = dx != 0 || dy != 0 || dz != 0;
                    var small = Math.Abs(dx) <= 127 && Math.Abs(dy) <= 127 && Math.Abs(dz) <= 127;
                    if (posChanged) flags |= small ? Wire.FPosDelta : Wire.FPosAbs;
                    if (q.Yaw != last.Yaw) flags |= Wire.FYaw;
                    if (q.Vx != last.Vx || q.Vy != last.Vy || q.Vz != last.Vz) flags |= Wire.FVel;
                    if (flags == 0) continue;
                    _buf[o++] = (byte)p.Id; _buf[o++] = (byte)flags;
                    if ((flags & Wire.FPosAbs) != 0) { W16(ref o, q.X); W16(ref o, q.Y); W16(ref o, q.Z); }
                    if ((flags & Wire.FPosDelta) != 0) { _buf[o++] = (byte)(sbyte)dx; _buf[o++] = (byte)(sbyte)dy; _buf[o++] = (byte)(sbyte)dz; }
                    if ((flags & Wire.FYaw) != 0) W16(ref o, q.Yaw);
                    if ((flags & Wire.FVel) != 0) { _buf[o++] = (byte)q.Vx; _buf[o++] = (byte)q.Vy; _buf[o++] = (byte)q.Vz; }
                    _entriesDelta++;
                }
                if (!isFull) q.LastFull = last.LastFull;
                last = q;
                count++;
                if (count >= Wire.MaxEntries) { Flush(c, tick, count, o); count = 0; o = 8; }
            }
            if (count > 0) Flush(c, tick, count, o);
        }
        _host.Flush();
    }

    void W16(ref int o, ushort v) { _buf[o++] = (byte)v; _buf[o++] = (byte)(v >> 8); }

    void Flush(Cli c, int tick, int count, int len)
    {
        _buf[0] = Wire.TSnap;
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(1), (uint)tick);
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(5), (ushort)c.Ack);
        _buf[7] = (byte)count;
        c.Peer.Send(Wire.ChanData, _buf.AsSpan(0, len).ToArray(), 0); // 0 = unreliable sequenced
        _bytesSnap += len; _snaps++;
        _core.Extra["snapshot_payload_bytes_total"] = _bytesSnap;
        _core.Extra["snapshot_packets_total"] = _snaps;
        _core.Extra["entries_full"] = _entriesFull;
        _core.Extra["entries_delta"] = _entriesDelta;
    }
}
