using Godot;

/// <summary>
/// Test for godotengine/godot#103938 (MultiplayerSynchronizer stops syncing beyond 64 watched
/// properties). A server and a client share a PropProbe node; K of its properties are watched by
/// S synchronizers (properties dealt round-robin). The server changes every property each tick;
/// the client counts how many of them actually update.
/// </summary>
public class ProbeRole : IRole
{
    readonly bool _server;
    readonly Config _cfg;
    readonly Node _main;
    readonly PropProbe _probe;
    readonly int _k, _s;
    int _tick; bool _connected;
    double _tConn, _tEnd;
    readonly float[] _last = new float[160];
    readonly int[] _changes = new int[160];

    public ProbeRole(Node main, Config cfg, bool server)
    {
        _main = main; _cfg = cfg; _server = server;
        _k = cfg.Int("k", 64); _s = cfg.Int("syncs", 1);
        var onChange = cfg.Str("sync-mode", "always") == "onchange";
        _probe = new PropProbe { Name = "Probe" };
        main.AddChild(_probe);
        for (var s = 0; s < _s; s++)
        {
            var rc = new SceneReplicationConfig();
            for (var i = s; i < _k; i += _s)
            {
                var np = new NodePath($":P{i}");
                rc.AddProperty(np);
                rc.PropertySetReplicationMode(np, onChange ? SceneReplicationConfig.ReplicationMode.OnChange : SceneReplicationConfig.ReplicationMode.Always);
            }
            _probe.AddChild(new MultiplayerSynchronizer { Name = $"Sync{s}", ReplicationConfig = rc });
        }
        var peer = new ENetMultiplayerPeer();
        var port = cfg.Int("port", 27015);
        var err = _server ? peer.CreateServer(port, 4) : peer.CreateClient(cfg.Str("host", "127.0.0.1"), port);
        if (err != Error.Ok) throw new InvalidOperationException($"probe peer failed: {err}");
        main.GetTree().GetMultiplayer().MultiplayerPeer = peer;
        var mp = main.Multiplayer;
        if (_server) mp.PeerConnected += _ => { _connected = true; _tConn = Proc.Now(); };
        else mp.ConnectedToServer += () => { _connected = true; _tConn = Proc.Now(); };
        _tEnd = Proc.Now() + cfg.Float("connect-timeout", 20);
        if (_server) GD.Print("NETSPIKE_READY");
    }

    public void Physics(double dt)
    {
        if (!_server || !_connected) return;
        _tick++;
        for (var i = 0; i < _k; i++) _probe.SetP(i, i + 1 + _tick);
    }

    public void Frame(double dt)
    {
        var now = Proc.Now();
        if (_server)
        {
            if (now > _tEnd + 5 && !_connected) _main.GetTree().Quit(3);
            if (_connected && now - _tConn > _cfg.Float("seconds", 8) + 3) _main.GetTree().Quit(0);
            return;
        }
        if (!_connected)
        {
            if (now > _tEnd) Finish(false);
            return;
        }
        if (now - _tConn < 2) return; // settle
        for (var i = 0; i < _k; i++)
        {
            var v = _probe.Get(i);
            if (v != _last[i]) { _last[i] = v; _changes[i]++; }
        }
        if (now - _tConn > 2 + _cfg.Float("seconds", 8)) Finish(true);
    }

    void Finish(bool connected)
    {
        var live = new List<int>(); var stuck = new List<int>();
        for (var i = 0; i < _k; i++) (_changes[i] >= 10 ? live : stuck).Add(i);
        Proc.WriteJson(Path.Combine(_cfg.Str("out", "out"), $"probe_k{_k}_s{_s}_{_cfg.Str("sync-mode", "always")}.json"), new Dictionary<string, object>
        {
            ["k"] = _k, ["syncs"] = _s, ["sync_mode"] = _cfg.Str("sync-mode", "always"), ["connected"] = connected,
            ["watched_properties"] = _k, ["properties_updating"] = live.Count, ["properties_stuck"] = stuck.Count,
            ["first_stuck_index"] = stuck.Count > 0 ? stuck[0] : -1,
            ["updating_indices_max"] = live.Count > 0 ? live.Max() : -1,
        });
        _main.GetTree().Quit(0);
    }
}
