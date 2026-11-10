using System.Diagnostics;
using Godot;

/// <summary>
/// Approach-independent server part: loads the real map, owns the simulated players, runs the
/// measurement timeline (wait for clients, warm-up, go, measure, stop, write results).
/// </summary>
public class ServerCore
{
    public readonly Config Cfg;
    public readonly Suchohrad.MapLoader.MapLoader Map = new();
    public readonly Node3D World = new() { Name = "World" };
    public readonly List<NetPlayer> Players = new();
    public int Tick;
    public bool Measuring;
    public Vector3 BMin, BMax;
    public ENetConnection? Host;          // set by the approach (for byte counters)
    public Action<int>? SendControl;      // set by the approach: broadcast reliable go/stop
    public int Disconnects, JoinedTotal, Respawns;
    public string Approach = "";
    public Dictionary<string, object> Extra = new();

    readonly Node _owner;
    readonly RandomNumberGenerator _rng = new();
    readonly int _expect;
    readonly double _joinTimeout, _warmup, _seconds;
    readonly string _out;
    readonly Window _win = new();
    readonly List<double> _frameBusyMs = new(), _tickWorkMs = new(), _frameProcMs = new(), _framePhysMs = new();
    readonly List<float[]> _histPos = new();
    readonly List<int> _histTick = new();
    readonly List<double> _histStamp = new();
    int[] _histIds = Array.Empty<int>();
    enum St { Waiting, Warm, Run, Drain, Done }
    St _st = St.Waiting;
    double _tState;
    double _t0;
    int _ticksInWindow, _framesInWindow, _ticksStart;
    int _joinedAtGo, _atStop;

    public ServerCore(Node owner, Config cfg)
    {
        _owner = owner; Cfg = cfg;
        _expect = cfg.Int("n", 16);
        _joinTimeout = cfg.Float("join-timeout", 60);
        _warmup = cfg.Float("warmup", 4);
        _seconds = cfg.Float("seconds", 60);
        _out = cfg.Str("out", "out");
        Directory.CreateDirectory(_out);
        _rng.Seed = (ulong)cfg.Int("seed", 1);
        _t0 = Proc.Now(); _tState = _t0;
    }

    public void Load()
    {
        var dir = Cfg.Str("map", OS.GetEnvironment("SUCHOHRAD_MAP"));
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("no map: pass --map DIR or set SUCHOHRAD_MAP");
        _owner.AddChild(World);
        World.AddChild(Map.Root);
        Map.Load(Path.GetFullPath(dir));
        var s = Map.Stats;
        GD.Print($"map {Map.MapVersion}: chunks={s.ChunksLoaded} colliders={s.CollidersBuilt} spawns={Map.SpawnPoints.Count} load={s.LoadSeconds:F2}s");
        // world bounds for quantisation: terrain collision AABB padded
        Aabb? box = null;
        foreach (var mi in Descend(Map.Root).OfType<MeshInstance3D>())
            if (mi.Name.ToString() == "terrain_collision" && mi.Mesh != null)
                box = box == null ? mi.Mesh.GetAabb() : box.Value.Merge(mi.Mesh.GetAabb());
        var b = box ?? new Aabb(new Vector3(-100, -10, -100), new Vector3(200, 60, 200));
        BMin = b.Position - new Vector3(30, 20, 30);
        BMax = b.End + new Vector3(30, 40, 30);
        if (BMax.X - BMin.X > 500 || BMax.Z - BMin.Z > 500 || BMax.Y - BMin.Y > 500)
            GD.PrintErr("warning: map larger than the 512 m quantisation span of the custom layer");
    }

    static IEnumerable<Node> Descend(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            yield return c;
            foreach (var d in Descend(c)) yield return d;
        }
    }

    public Vector3 RandomSpawn()
    {
        var sp = Map.SpawnPoints;
        if (sp.Count == 0) return new Vector3(0, 5, 0);
        return sp[_rng.RandiRange(0, sp.Count - 1)].Position + Vector3.Up * 1.0f;
    }

    public void Register(NetPlayer p) { Players.Add(p); JoinedTotal++; }
    public void Unregister(NetPlayer p) { Players.Remove(p); if (Measuring) Disconnects++; }

    // ---- per tick ----
    long _tickStart;
    public void TickBegin() { _tickStart = Stopwatch.GetTimestamp(); Tick++; }

    public void Sim(float dt)
    {
        foreach (var p in Players)
        {
            p.Step(dt, Tick);
            if (p.Position.Y < BMin.Y + 5)
            {   // walked off the edge of the slice: out-of-bounds respawn, like a game would do
                p.Position = RandomSpawn(); p.Velocity = Vector3.Zero; Respawns++;
            }
        }
        if (!Measuring) return;
        var row = new float[_histIds.Length * 3];
        for (var i = 0; i < _histIds.Length; i++)
        {
            var p = Players.FirstOrDefault(q => q.Id == _histIds[i]);
            var pos = p?.Position ?? new Vector3(float.NaN, float.NaN, float.NaN);
            row[i * 3] = pos.X; row[i * 3 + 1] = pos.Y; row[i * 3 + 2] = pos.Z;
        }
        _histPos.Add(row); _histTick.Add(Tick); _histStamp.Add(Proc.Now());
    }

    public void TickEnd()
    {
        if (!Measuring) return;
        _tickWorkMs.Add((Stopwatch.GetTimestamp() - _tickStart) * 1000.0 / Stopwatch.Frequency);
        _ticksInWindow++;
    }

    // ---- per frame ----
    public void Frame()
    {
        var now = Proc.Now();
        if (Measuring)
        {
            // previous frame's process + physics time as measured by the engine (includes network poll/replication)
            var pr = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
            var ph = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
            _frameProcMs.Add(pr); _framePhysMs.Add(ph); _frameBusyMs.Add(pr + ph);
            _framesInWindow++;
        }
        switch (_st)
        {
            case St.Waiting:
                if (Players.Count >= _expect || now - _t0 > _joinTimeout)
                {
                    GD.Print($"NETSPIKE_JOINED {Players.Count}/{_expect}");
                    _st = St.Warm; _tState = now;
                }
                break;
            case St.Warm:
                if (now - _tState >= _warmup) Go(now);
                break;
            case St.Run:
                if (now - _tState >= _seconds) Stop(now);
                break;
            case St.Drain:
                if (now - _tState >= 2.0) Finish();
                break;
        }
    }

    void Go(double now)
    {
        _histIds = Players.Select(p => p.Id).ToArray();
        _joinedAtGo = Players.Count;
        Measuring = true; Disconnects = 0;
        _ticksStart = Tick;
        _win.Begin(Host);
        SendControl?.Invoke(Net.CtlGo);
        _st = St.Run; _tState = now;
        GD.Print("NETSPIKE_GO");
    }

    void Stop(double now)
    {
        Measuring = false; _atStop = Players.Count;
        _win.End(Host);
        SendControl?.Invoke(Net.CtlStop);
        _st = St.Drain; _tState = now;
        GD.Print("NETSPIKE_STOP");
    }

    void Finish()
    {
        _st = St.Done;
        var res = new Dictionary<string, object>
        {
            ["approach"] = Approach, ["expected_clients"] = _expect, ["joined_total"] = JoinedTotal,
            ["players_at_go"] = _joinedAtGo, ["respawns_total"] = Respawns, ["players_at_stop"] = _atStop, ["disconnects_in_window"] = Disconnects,
            ["window"] = _win.ToDict(),
            ["ticks_in_window"] = _ticksInWindow, ["achieved_tick_hz"] = _ticksInWindow / Math.Max(_win.Wall, 1e-9),
            ["frames_in_window"] = _framesInWindow,
            ["frame_busy_ms"] = Stats.Summary(_frameBusyMs),
            ["frame_process_ms"] = Stats.Summary(_frameProcMs), ["frame_physics_ms"] = Stats.Summary(_framePhysMs),
            ["frames_over_33ms"] = _frameBusyMs.Count(x => x > 33.3),
            ["tick_work_ms"] = Stats.Summary(_tickWorkMs),
            ["map_chunks"] = Map.Stats.ChunksLoaded, ["map_colliders"] = Map.Stats.CollidersBuilt,
            ["bounds_min"] = new[] { BMin.X, BMin.Y, BMin.Z }, ["bounds_max"] = new[] { BMax.X, BMax.Y, BMax.Z },
        };
        foreach (var kv in Extra) res[kv.Key] = kv.Value;
        Proc.WriteJson(Path.Combine(_out, "server.json"), res);
        // history: i32 nTicks, i32 nIds, i32 ids[], then per tick: i32 tick, f64 stamp, f32 xyz * nIds
        using (var bw = new BinaryWriter(File.Create(Path.Combine(_out, "history.bin"))))
        {
            bw.Write(_histTick.Count); bw.Write(_histIds.Length);
            foreach (var id in _histIds) bw.Write(id);
            for (var i = 0; i < _histTick.Count; i++)
            {
                bw.Write(_histTick[i]); bw.Write(_histStamp[i]);
                foreach (var f in _histPos[i]) bw.Write(f);
            }
        }
        GD.Print("NETSPIKE_DONE");
        _owner.GetTree().Quit(0);
    }
}

/// <summary>Approach-independent client part: bot input, measurement window, sample recording, results.</summary>
public class ClientCore
{
    public readonly Config Cfg;
    public readonly BotInput Bot;
    public readonly int Index;
    public bool Recording, Finished, Connected;
    public ENetConnection? Host;
    public string Approach = "";
    public long Updates;
    public Dictionary<string, object> Extra = new();

    readonly Node _owner;
    readonly string _out;
    readonly Window _win = new();
    readonly Dictionary<int, int> _ctr = new();
    readonly MemoryStream _ms = new();
    readonly BinaryWriter _bw;
    readonly int _every;
    long _visSum, _visFrames;
    int _samples;
    double _tStart, _tConnected;

    public ClientCore(Node owner, Config cfg)
    {
        _owner = owner; Cfg = cfg;
        Index = cfg.Int("index", 0);
        _out = cfg.Str("out", "out");
        Directory.CreateDirectory(_out);
        _every = cfg.Int("sample-every", 8);
        _bw = new BinaryWriter(_ms);
        Bot = new BotInput((ulong)(cfg.Int("seed", 1) * 1000 + Index));
        _tStart = Proc.Now();
    }

    public void OnConnected() { Connected = true; _tConnected = Proc.Now(); }

    public void Go() { Recording = true; _win.Begin(Host); }

    /// <summary>One state update for an entity, as received.</summary>
    public void Record(int tick, int id, Vector3 pos)
    {
        if (!Recording) return;
        Updates++;
        _ctr.TryGetValue(id, out var c); c++; _ctr[id] = c;
        if (c % _every != 0) return;
        _bw.Write(tick); _bw.Write(id); _bw.Write(pos.X); _bw.Write(pos.Y); _bw.Write(pos.Z); _bw.Write(Proc.Now());
        _samples++;
    }

    public void Frame(int visibleEntities)
    {
        if (!Recording) return;
        _visSum += visibleEntities; _visFrames++;
    }

    public void Finish(string note = "")
    {
        if (Finished) return;
        Finished = true;
        if (Recording) _win.End(Host);
        Recording = false;
        var res = new Dictionary<string, object>
        {
            ["index"] = Index, ["approach"] = Approach, ["connected"] = Connected,
            ["connect_ms"] = Connected ? (_tConnected - _tStart) * 1000.0 : -1.0,
            ["note"] = note, ["updates"] = Updates, ["samples"] = _samples,
            ["avg_visible_entities"] = _visFrames > 0 ? (double)_visSum / _visFrames : 0.0,
            ["window"] = _win.ToDict(),
        };
        foreach (var kv in Extra) res[kv.Key] = kv.Value;
        Proc.WriteJson(Path.Combine(_out, $"client_{Index}.json"), res);
        File.WriteAllBytes(Path.Combine(_out, $"client_{Index}.bin"), _ms.ToArray());
        _owner.GetTree().Quit(Connected ? 0 : 3);
    }
}
