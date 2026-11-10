using System.Buffers.Binary;
using Godot;

/// <summary>Approach B bot client: raw ENetConnection, decodes quantised delta snapshots.</summary>
public class CustomClient : IRole
{
    readonly ClientCore _core;
    readonly ENetConnection _host = new();
    ENetPacketPeer? _peer;
    readonly Wire.Ent[] _ent = new Wire.Ent[256];
    readonly double[] _seen = new double[256];
    Vector3 _bmin;
    int _myId;
    bool _connected;
    double _connectDeadline;

    public CustomClient(Node main, ClientCore core)
    {
        _core = core; core.Approach = "custom"; core.Host = _host;
        var err = _host.CreateHost(1, Wire.Channels, 0, 0);
        if (err != Error.Ok) { core.Finish($"CreateHost failed: {err}"); return; }
        _peer = _host.ConnectToHost(core.Cfg.Str("host", "127.0.0.1"), core.Cfg.Int("port", 27015), Wire.Channels, 0);
        if (_peer == null) { core.Finish("ConnectToHost failed"); return; }
        _connectDeadline = Proc.Now() + core.Cfg.Float("connect-timeout", 30);
    }

    public void Physics(double dt)
    {
        if (!_connected || _core.Finished || _peer == null) return;
        var b = _core.Bot;
        b.Next((float)dt);
        _peer.Send(Wire.ChanData, Wire.Input(b.Seq, b.Mx, b.Mz, b.YawQ, b.Jump), 0);
        _host.Flush();
    }

    public void Frame(double dt)
    {
        if (_core.Finished) return;
        if (!_connected && Proc.Now() > _connectDeadline) { _core.Finish("connect timeout"); return; }
        for (var guard = 0; guard < 100000; guard++)
        {
            var r = _host.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev == ENetConnection.EventType.None || ev == ENetConnection.EventType.Error) break;
            var peer = (ENetPacketPeer)r[1].AsGodotObject();
            switch (ev)
            {
                case ENetConnection.EventType.Connect: break; // wait for welcome
                case ENetConnection.EventType.Disconnect:
                    _core.Finish(_core.Finished ? "" : "server disconnected early");
                    return;
                case ENetConnection.EventType.Receive:
                    while (peer.GetAvailablePacketCount() > 0) OnPacket(peer.GetPacket());
                    break;
            }
        }
        if (_core.Recording)
        {
            var now = Proc.Now(); var n = 0;
            for (var i = 0; i < 256; i++) if (now - _seen[i] < 1.0) n++;
            _core.Frame(n);
        }
    }

    void OnPacket(byte[] d)
    {
        if (d.Length == 0) return;
        switch (d[0])
        {
            case Wire.TWelcome:
                _myId = d[1];
                _bmin = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(2)),
                    BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(6)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(10)));
                _connected = true; _core.OnConnected();
                break;
            case Wire.TGo: _core.Go(); break;
            case Wire.TStop: _core.Finish(); break;
            case Wire.TSnap: OnSnapshot(d); break;
        }
    }

    void OnSnapshot(byte[] d)
    {
        var tick = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(1));
        int count = d[7], o = 8;
        var now = Proc.Now();
        for (var i = 0; i < count; i++)
        {
            int id = d[o++]; int flags = d[o++];
            ref var e = ref _ent[id];
            if ((flags & Wire.FPosAbs) != 0)
            {
                e.X = R16(d, ref o); e.Y = R16(d, ref o); e.Z = R16(d, ref o);
            }
            if ((flags & Wire.FPosDelta) != 0)
            {
                e.X = (ushort)(e.X + (sbyte)d[o++]); e.Y = (ushort)(e.Y + (sbyte)d[o++]); e.Z = (ushort)(e.Z + (sbyte)d[o++]);
            }
            if ((flags & Wire.FYaw) != 0) e.Yaw = R16(d, ref o);
            if ((flags & Wire.FVel) != 0) { e.Vx = (sbyte)d[o++]; e.Vy = (sbyte)d[o++]; e.Vz = (sbyte)d[o++]; }
            e.Valid = true; _seen[id] = now;
            _core.Record(tick, id, new Vector3(Net.DPos(e.X, _bmin.X), Net.DPos(e.Y, _bmin.Y), Net.DPos(e.Z, _bmin.Z)));
        }
    }

    static ushort R16(byte[] d, ref int o) { var v = (ushort)(d[o] | (d[o + 1] << 8)); o += 2; return v; }
}
