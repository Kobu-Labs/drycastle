using Godot;

/// <summary>Approach A bot client: sends input by unreliable RPC, receives spawned/synchronized players.</summary>
public class BuiltinClient : IRole
{
    readonly ClientCore _core;
    readonly BuiltinRpc _rpc;
    readonly Node3D _players;
    readonly Dictionary<int, int> _lastTick = new();
    double _connectDeadline;
    bool _connected;

    public BuiltinClient(Node main, ClientCore core)
    {
        _core = core; core.Approach = "builtin";
        (_rpc, _players, _) = BuiltinScene.Build(main, SpawnFn);
        _rpc.ControlReceived += OnControl;
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(core.Cfg.Str("host", "127.0.0.1"), core.Cfg.Int("port", 27015));
        if (err != Error.Ok) { core.Finish($"CreateClient failed: {err}"); return; }
        main.GetTree().GetMultiplayer().MultiplayerPeer = peer;
        core.Host = peer.Host;
        var mp = main.Multiplayer;
        mp.ConnectedToServer += () => { _connected = true; core.OnConnected(); };
        mp.ConnectionFailed += () => core.Finish("connection failed");
        mp.ServerDisconnected += () => core.Finish(core.Finished ? "" : "server disconnected early");
        _connectDeadline = Proc.Now() + core.Cfg.Float("connect-timeout", 30);
    }

    static Node SpawnFn(Variant data)
    {
        var a = data.AsGodotArray();
        return BuiltinScene.MakePlayer(a[0].AsInt32(), a[1].AsVector3(), false, false, false);
    }

    void OnControl(int code)
    {
        if (code == Net.CtlGo) _core.Go();
        else if (code == Net.CtlStop) _core.Finish();
    }

    public void Physics(double dt)
    {
        if (!_connected || _core.Finished) return;
        var b = _core.Bot;
        b.Next((float)dt);
        _rpc.RpcId(1, BuiltinRpc.MethodName.SendInput, b.Seq, b.Mx, b.Mz, b.YawQ, b.Jump);
    }

    public void Frame(double dt)
    {
        if (_core.Finished) return;
        if (!_connected && Proc.Now() > _connectDeadline) { _core.Finish("connect timeout"); return; }
        var n = 0;
        foreach (var c in _players.GetChildren())
        {
            if (c is not NetPlayer p) continue;
            n++;
            _lastTick.TryGetValue(p.Id, out var lt);
            if (p.Tick != lt && p.Tick != 0)
            {
                _lastTick[p.Id] = p.Tick;
                _core.Record(p.Tick, p.Id, p.Position);
            }
        }
        _core.Frame(n);
    }
}
