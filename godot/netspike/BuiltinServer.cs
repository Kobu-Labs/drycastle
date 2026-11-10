using Godot;

/// <summary>Shared scene layout of approach A (identical node paths on server and client).</summary>
public static class BuiltinScene
{
    /// <summary>Build Main/{Rpc, Players, Spawner}. Spawn function creates the player + synchronizer.</summary>
    public static (BuiltinRpc rpc, Node3D players, MultiplayerSpawner spawner) Build(Node main, Func<Variant, Node> spawn)
    {
        var rpc = new BuiltinRpc { Name = "Rpc" };
        var players = new Node3D { Name = "Players" };
        var spawner = new MultiplayerSpawner { Name = "Spawner", SpawnLimit = 0 };
        main.AddChild(rpc);
        main.AddChild(players);
        main.AddChild(spawner);
        spawner.SpawnPath = spawner.GetPathTo(players);
        spawner.SpawnFunction = Callable.From(spawn);
        return (rpc, players, spawner);
    }

    /// <summary>Player node + synchronizer replicating position, rotation, velocity (+ Tick for age measurement).</summary>
    public static NetPlayer MakePlayer(int id, Vector3 pos, bool server, bool interest, bool onChange)
    {
        var p = NetPlayer.Make(id, pos, server);
        var cfg = new SceneReplicationConfig();
        var mode = onChange ? SceneReplicationConfig.ReplicationMode.OnChange : SceneReplicationConfig.ReplicationMode.Always;
        foreach (var prop in new[] { ":position", ":rotation", ":velocity", ":Tick" })
        {
            var np = new NodePath(prop);
            cfg.AddProperty(np);
            cfg.PropertySetReplicationMode(np, mode);
            cfg.PropertySetSpawn(np, prop != ":Tick");
        }
        var sync = new MultiplayerSynchronizer { Name = "Sync", ReplicationConfig = cfg };
        sync.SetMultiplayerAuthority(1);
        if (server && interest)
        {
            sync.PublicVisibility = false;
            sync.SetVisibilityFor(id, true); // a player always sees itself
        }
        p.AddChild(sync);
        return p;
    }
}

/// <summary>Approach A server: ENetMultiplayerPeer, MultiplayerSpawner + MultiplayerSynchronizer, unreliable RPC input.</summary>
public class BuiltinServer : IRole
{
    readonly ServerCore _core;
    readonly Node _main;
    readonly Node3D _players;
    readonly BuiltinRpc _rpc;
    readonly MultiplayerSpawner _spawner;
    readonly float _interest;
    readonly bool _onChange;
    readonly Dictionary<int, NetPlayer> _byId = new();
    readonly HashSet<(int viewer, int target)> _visible = new();
    int _visTick;

    public BuiltinServer(Node main, ServerCore core)
    {
        _main = main; _core = core;
        core.Approach = "builtin";
        _interest = core.Cfg.Float("interest", 0);
        _onChange = core.Cfg.Str("sync-mode", "always") == "onchange";
        core.Extra["interest_radius_m"] = _interest;
        core.Extra["sync_mode"] = _onChange ? "onchange" : "always";
        (_rpc, _players, _spawner) = BuiltinScene.Build(main, SpawnFn);
        _rpc.InputReceived += OnInput;

        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(core.Cfg.Int("port", 27015), core.Cfg.Int("n", 16) + 8);
        if (err != Error.Ok) throw new InvalidOperationException($"CreateServer failed: {err}");
        main.GetTree().GetMultiplayer().MultiplayerPeer = peer;
        _core.Host = peer.Host;
        var mp = main.Multiplayer;
        mp.PeerConnected += id => OnConnected((int)id);
        mp.PeerDisconnected += id => OnDisconnected((int)id);
        core.SendControl = code => _rpc.Rpc(BuiltinRpc.MethodName.Control, code);
    }

    Node SpawnFn(Variant data)
    {
        var a = data.AsGodotArray();
        var p = BuiltinScene.MakePlayer(a[0].AsInt32(), a[1].AsVector3(), true, _interest > 0, _onChange);
        return p;
    }

    void OnConnected(int id)
    {
        var pos = _core.RandomSpawn();
        // The spawn function runs locally on the server and returns the node.
        var node = (NetPlayer)_spawner.Spawn(new Godot.Collections.Array { id, pos });
        _byId[id] = node;
        _core.Register(node);
        _rpc.RpcId(id, BuiltinRpc.MethodName.Control, Net.CtlWelcome);
    }

    void OnDisconnected(int id)
    {
        if (!_byId.Remove(id, out var n)) return;
        _core.Unregister(n);
        n.QueueFree();
        _visible.RemoveWhere(t => t.viewer == id || t.target == id);
    }

    void OnInput(int sender, int seq, int mx, int mz, int yawQ, int jump)
    {
        if (_byId.TryGetValue(sender, out var p)) p.SetInput(seq, mx, mz, yawQ, jump);
    }

    public void Physics(double dt)
    {
        _core.TickBegin();
        _core.Sim((float)dt);
        if (_interest > 0 && ++_visTick % 8 == 0) UpdateInterest();
        _core.TickEnd();
    }

    // Interest management via per-peer synchronizer visibility (spawner despawns invisible nodes on that peer).
    void UpdateInterest()
    {
        var r2 = _interest * _interest;
        var ps = _core.Players;
        foreach (var viewer in ps)
        {
            var changed = false;
            foreach (var t in ps)
            {
                if (t == viewer) continue;
                var want = viewer.Position.DistanceSquaredTo(t.Position) <= r2;
                var key = (viewer.Id, t.Id);
                var has = _visible.Contains(key);
                if (want == has) continue;
                var sync = t.GetNode<MultiplayerSynchronizer>("Sync");
                sync.SetVisibilityFor(viewer.Id, want);
                if (want) _visible.Add(key); else _visible.Remove(key);
                changed = true;
            }
            _ = changed;
        }
    }

    public void Frame(double dt) => _core.Frame();
}
