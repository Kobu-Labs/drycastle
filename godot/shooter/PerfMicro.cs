using System.Diagnostics;
using System.Text;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// In-process micro-benchmarks on the real map's physics world (perf investigation N7, N11, C2, E: docs/perf-investigation/measurement-plan.md).
/// <c>--role server --perf-micro --perf-out DIR</c> loads the map exactly as the server does, runs the benchmarks once in a physics callback (the
/// direct space state is only valid there), writes DIR/micro.json and quits. Measurement only: nothing here runs in play.
/// Every timed call is one sample (nanoseconds), so the JSON has p50/p95/p99/max per operation, not just a mean.
/// </summary>
public partial class PerfMicro : Node
{
    public Node3D World = null!;
    public Drycastle.MapLoader.MapLoader Map = null!;
    public Water Water = Water.Empty;
    public VehiclesFile Vehicles = null!;
    public string OutDir = "";
    public int Rays = 20000, WalkSteps = 3000, Settle = 6;

    int _frame;
    readonly Random _rng = new(12345);
    readonly Dictionary<string, object> _out = new();

    public override void _PhysicsProcess(double delta)
    {
        if (++_frame < Settle) return;
        if (_frame > Settle) return;
        try { Run((float)delta); }
        catch (Exception e) { GD.PrintErr($"perf-micro failed: {e}"); GetTree().Quit(2); return; }
        System.IO.Directory.CreateDirectory(OutDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(OutDir, "micro.json"), System.Text.Json.JsonSerializer.Serialize(_out, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        GD.Print("PERF_MICRO_DONE");
        GetTree().Quit(0);
    }

    static double Ns(long ticks) => ticks * 1e9 / Stopwatch.Frequency;

    static Dictionary<string, object> Stats(List<double> ns)
    {
        ns.Sort();
        double P(double p) => ns.Count == 0 ? 0 : ns[Math.Min(ns.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * ns.Count) - 1))];
        return new() { ["n"] = ns.Count, ["mean_us"] = ns.Count == 0 ? 0 : ns.Average() / 1000, ["p50_us"] = P(50) / 1000, ["p95_us"] = P(95) / 1000, ["p99_us"] = P(99) / 1000, ["max_us"] = P(100) / 1000, ["min_us"] = P(0) / 1000 };
    }

    List<Vector3> Anchors() => Map.SpawnPoints.Select(s => s.Position).ToList();

    Vector3 Near(List<Vector3> a, float spread)
    {
        var p = a[_rng.Next(a.Count)];
        return new Vector3(p.X + (float)(_rng.NextDouble() * 2 - 1) * spread, p.Y, p.Z + (float)(_rng.NextDouble() * 2 - 1) * spread);
    }

    void Run(float dt)
    {
        var anchors = Anchors();
        if (anchors.Count == 0) throw new InvalidOperationException("map has no spawn points");
        var space = World.GetWorld3D().DirectSpaceState;
        _out["map_version"] = Map.MapVersion;
        _out["spawns"] = anchors.Count;

        // ---- rays: the cost of one IntersectRay, by kind of ray (N7)
        var rays = new Dictionary<string, object>();
        List<double> Time(Func<PhysicsRayQueryParameters3D> make)
        {
            var ns = new List<double>(Rays);
            var hits = 0;
            for (var i = 0; i < Rays; i++)
            {
                var q = make();
                var t0 = Stopwatch.GetTimestamp();
                var r = space.IntersectRay(q);
                ns.Add(Ns(Stopwatch.GetTimestamp() - t0));
                if (r.Count > 0) hits++;
            }
            ns.Add(0); ns.RemoveAt(ns.Count - 1);
            _out["last_hits"] = hits;
            return ns;
        }
        Dictionary<string, object> WithHits(List<double> ns) { var d = Stats(ns); d["hit_share"] = (int)_out["last_hits"] / (double)Rays; return d; }
        rays["down_world"] = WithHits(Time(() => { var p = Near(anchors, 60); return PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 40, p + Vector3.Down * 40, CollisionLayers.World); }));
        rays["horizontal_20m_bullets"] = WithHits(Time(() => HRay(anchors, 20, CollisionLayers.Bullets)));
        rays["horizontal_100m_bullets"] = WithHits(Time(() => HRay(anchors, 100, CollisionLayers.Bullets)));
        rays["horizontal_300m_bullets"] = WithHits(Time(() => HRay(anchors, 300, CollisionLayers.Bullets)));
        rays["horizontal_300m_world_only"] = WithHits(Time(() => HRay(anchors, 300, CollisionLayers.World)));
        rays["horizontal_300m_shotonly_cover"] = WithHits(Time(() => HRay(anchors, 300, CollisionLayers.ShotOnly)));
        _out["rays"] = rays;
        _out.Remove("last_hits");

        // ---- on-foot players: one Simulate = one MoveAndSlide plus the shared step (C-07, E-08)
        var moves = new Dictionary<string, object>();
        Player NewPlayer(Vector3 at)
        {
            var p = new Player { UseCamera = false, AutoSim = false, CaptureMouse = false, Authoritative = false, Water = Water };
            World.AddChild(p);
            p.Teleport(at);
            return p;
        }
        var one = NewPlayer(anchors[0]);
        var idle = new MoveInput { SpeedScale = 1 };
        for (var i = 0; i < 30; i++) one.Simulate(idle, dt); // settle on the floor
        var ns1 = new List<double>(WalkSteps);
        for (var i = 0; i < WalkSteps; i++) { var t0 = Stopwatch.GetTimestamp(); one.Simulate(idle, dt); ns1.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
        moves["player_idle_simulate"] = Stats(ns1);
        var walk = new MoveInput { MoveY = 1, SpeedScale = 1, Yaw = 0 };
        var ns2 = new List<double>(WalkSteps);
        for (var i = 0; i < WalkSteps; i++)
        {
            if (i % 90 == 0) { walk.Yaw = (float)(_rng.NextDouble() * Math.PI * 2); one.Teleport(Near(anchors, 40)); for (var k = 0; k < 5; k++) one.Simulate(idle, dt); }
            var t0 = Stopwatch.GetTimestamp(); one.Simulate(walk, dt); ns2.Add(Ns(Stopwatch.GetTimestamp() - t0));
        }
        moves["player_walk_simulate"] = Stats(ns2);

        // 16 and 32 players stepped as the server does each tick, spread over the map, then all stacked in a 2 x 2 m cluster (contact cost, C-01)
        foreach (var n in new[] { 16, 32 })
        {
            var ps = Enumerable.Range(0, n).Select(_ => NewPlayer(Near(anchors, 30))).ToList();
            for (var i = 0; i < 30; i++) foreach (var p in ps) p.Simulate(idle, dt);
            var tick = new List<double>();
            for (var t = 0; t < 200; t++) { var t0 = Stopwatch.GetTimestamp(); foreach (var p in ps) p.Simulate(walk, dt); tick.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
            moves[$"players_{n}_spread_tick_all"] = Stats(tick);
            var c = anchors[0];
            for (var k = 0; k < ps.Count; k++) ps[k].Teleport(new Vector3(c.X + (k % 4) * 0.6f, c.Y, c.Z + (k / 4) * 0.6f));
            for (var i = 0; i < 10; i++) foreach (var p in ps) p.Simulate(idle, dt);
            tick = new List<double>();
            for (var t = 0; t < 200; t++) { var t0 = Stopwatch.GetTimestamp(); foreach (var p in ps) p.Simulate(idle, dt); tick.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
            moves[$"players_{n}_cluster_tick_all"] = Stats(tick);
            foreach (var p in ps) { World.RemoveChild(p); p.QueueFree(); }
        }

        // C-01: do two player bodies stop each other? One walks into the other from 1.5 m for 60 ticks; a free pass ends near 0 m apart.
        {
            var s = anchors[0];
            var a = NewPlayer(s); var b = NewPlayer(new Vector3(s.X + 1.5f, s.Y, s.Z));
            for (var i = 0; i < 30; i++) { a.Simulate(idle, dt); b.Simulate(idle, dt); }
            var toward = new MoveInput { MoveY = 1, SpeedScale = 1, Yaw = -Mathf.Pi / 2 }; // +X
            var minD = float.MaxValue;
            for (var i = 0; i < 90; i++)
            {
                a.Simulate(toward, dt); b.Simulate(idle, dt);
                var d = new Vector2(a.GlobalPosition.X - b.GlobalPosition.X, a.GlobalPosition.Z - b.GlobalPosition.Z).Length();
                if (d < minD) minD = d;
            }
            moves["kinematic_pair"] = new Dictionary<string, object> { ["start_m"] = 1.5, ["min_distance_m"] = minD, ["a_layer"] = a.CollisionLayer, ["a_mask"] = a.CollisionMask,
                ["bodies_block_each_other"] = minD > 0.4 };
            World.RemoveChild(a); World.RemoveChild(b); a.QueueFree(); b.QueueFree();
        }
        World.RemoveChild(one); one.QueueFree();
        _out["moves"] = moves;

        // ---- vehicles: a parked car's step (the server steps every driverless car every tick, D-01) and RestHeight (rays per spawn)
        var cars = new Dictionary<string, object>();
        var def = Vehicles.Vehicles.First(v => v.Flight == null && v.Drive == null && v.Seats.Count > 0 && !v.CanFloat);
        var roads = Map.SpawnPoints.Where(s => s.Kind == "road").Select(s => s.Position).ToList();
        if (roads.Count == 0) roads = anchors;
        ServerCar NewCar(int i, Vector3 at, VehicleDef d)
        {
            var car = new ServerCar { Name = $"perf{d.Id}{i}", Id = (ushort)(VehicleRecord.FirstId + i), Def = d, HalfWidth = d.HalfWidth, HalfLength = d.HalfLength,
                OriginPos = new Vector3(at.X, at.Y + 0.02f, at.Z), OriginYaw = (float)(_rng.NextDouble() * Math.PI * 2), FlightWater = Water };
            World.AddChild(car);
            return car;
        }
        var c1 = NewCar(0, roads[0], def);
        for (var i = 0; i < 30; i++) c1.Step(def, 0, 0, false, dt, Water);
        var nsC = new List<double>(WalkSteps);
        for (var i = 0; i < WalkSteps; i++) { var t0 = Stopwatch.GetTimestamp(); c1.Step(def, 0, 0, false, dt, Water); nsC.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
        cars["parked_car_step"] = Stats(nsC);
        var nsM = new List<double>(WalkSteps);
        for (var i = 0; i < WalkSteps; i++) { var t0 = Stopwatch.GetTimestamp(); c1.MoveAndSlide(); nsM.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
        cars["parked_car_moveandslide_only"] = Stats(nsM);
        var nsR = new List<double>();
        for (var i = 0; i < 200; i++) { var p = Near(roads, 5); var t0 = Stopwatch.GetTimestamp(); ServerCar.RestHeight(space, p, 0f, def.HalfWidth, def.HalfLength, p.Y); nsR.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
        cars["rest_height_per_spawn"] = Stats(nsR);
        World.RemoveChild(c1); c1.QueueFree();

        // the server's own loop: every car in the list, every tick (N4 / N11: the per-tick cost as the vehicle count grows)
        foreach (var n in new[] { 8, 32, 64, 128 })
        {
            var list = Enumerable.Range(0, n).Select(i => NewCar(i + 1, roads[i % roads.Count], def)).ToList();
            for (var i = 0; i < 20; i++) foreach (var c in list) c.Step(def, 0, 0, false, dt, Water);
            var tk = new List<double>();
            for (var t = 0; t < 200; t++) { var t0 = Stopwatch.GetTimestamp(); foreach (var c in list) { c.PlaceSolid(); c.Step(def, 0, 0, false, dt, Water); } tk.Add(Ns(Stopwatch.GetTimestamp() - t0)); }
            cars[$"parked_cars_{n}_tick_all"] = Stats(tk);
            foreach (var c in list) { World.RemoveChild(c); c.QueueFree(); }
        }
        cars["vehicle_type"] = def.Id;
        _out["cars"] = cars;
        GD.Print($"perf-micro: {rays.Count} ray kinds, {moves.Count} move kinds, {cars.Count} car kinds");
    }

    PhysicsRayQueryParameters3D HRay(List<Vector3> anchors, float len, uint mask)
    {
        var p = Near(anchors, 40) + Vector3.Up * 1.5f;
        var a = (float)(_rng.NextDouble() * Math.PI * 2);
        return PhysicsRayQueryParameters3D.Create(p, p + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * len, mask);
    }
}

public partial class Shooter
{
    void BuildPerfMicro(Config cfg)
    {
        var m = new PerfMicro { World = this, Map = _map, Water = _water, Vehicles = _data.Vehicles, OutDir = cfg.Str("perf-out", "out/perf/micro") };
        AddChild(m);
    }
}
