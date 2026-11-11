using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Headless ground-collision audit (scene res://ground_check.tscn): loads a map, raycasts down on a grid over the parcel
/// extents and reports cells without ground, then sprints a real <see cref="Player"/> along long lines and logs any fall.
/// Usage: godot --headless --path godot/shooter res://ground_check.tscn -- --map DIR [--step 2] [--fix 0|1]. Exit 0 on pass, 2 on failure.
/// </summary>
public partial class GroundCheck : Node3D
{
    readonly Suchohrad.MapLoader.MapLoader _map = new();
    Player _p = null!;
    int _frame, _line, _lineStart;
    readonly List<(Vector3 from, float yaw)> _lines = new();
    float _lineGround;
    readonly List<string> _trail = new(); int _edges, _falls, _lineFrames;
    float _step = 8;
    bool _noGrid, _noWalk, _noCars;
    List<(ServerCar car, float ground)> _cars = new();
    int _carStart;
    float _minTerrainY = float.MaxValue;
    bool _walkReady;
    int _phase;
    Rect2 _ext;
    (float x, float z)[] _gaps = Array.Empty<(float, float)>();

    public override void _Ready()
    {
        var a = OS.GetCmdlineUserArgs();
        string dir = "", stepS = "8";
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == "--map") dir = a[i + 1];
            if (a[i] == "--step") stepS = a[i + 1];
            if (a[i] == "--no-grid") _noGrid = true;
            if (a[i] == "--no-walk") _noWalk = true;
            if (a[i] == "--no-cars") _noCars = true;
        }
        _step = float.Parse(stepS, System.Globalization.CultureInfo.InvariantCulture);
        AddChild(_map.Root);
        _map.Load(Path.GetFullPath(dir));
        var first = true;
        foreach (var p in _map.Parcels.Values)
        {
            if (p.Rings.Count == 0) continue;
            _ext = first ? p.Bounds : _ext.Merge(p.Bounds);
            first = false;
        }
        var sb = new Rect2(_map.SpawnPoints[0].Position.X, _map.SpawnPoints[0].Position.Z, 0, 0);
        foreach (var sp in _map.SpawnPoints) sb = sb.Expand(new Vector2(sp.Position.X, sp.Position.Z));
        _ext = sb.Grow(12);
        GD.Print($"ground-check: map {dir}, parcels {_map.Parcels.Count}, extent {_ext}");
    }

    Vector3? Ray(PhysicsDirectSpaceState3D s, float x, float z, bool terrainOnly)
    {
        var excl = new Godot.Collections.Array<Rid>();
        var from = new Vector3(x, 2000, z);
        for (var i = 0; i < 12; i++)
        {
            var q = PhysicsRayQueryParameters3D.Create(from, new Vector3(x, -500, z));
            q.Exclude = excl;
            var h = s.IntersectRay(q);
            if (h.Count == 0) return null;
            var pos = (Vector3)h["position"];
            var col = h["collider"].AsGodotObject() as Node;
            var nm = col?.GetParent()?.Name.ToString() ?? "";
            if (!terrainOnly || nm == "terrain_collision") return pos;
            excl.Add((Rid)h["rid"]);
        }
        return null;
    }

    void Grid()
    {
        var s = GetWorld3D().DirectSpaceState;
        int cells = 0, noAny = 0, noTerr = 0;
        var gaps = new List<(float, float)>();
        var inParcel = 0; Rect2? hb = null;
        for (var x = _ext.Position.X; x < _ext.End.X; x += _step)
            for (var z = _ext.Position.Y; z < _ext.End.Y; z += _step)
            {
                cells++;
                var any = Ray(s, x, z, false);
                if (any == null) { noAny++; if (noAny <= 15) GD.Print($"  no ground at ({x:F0}, {z:F0})"); if (_map.ParcelAt(x, z) != null) { inParcel++; } continue; }
                var t = Ray(s, x, z, true);
                if (t == null) { noTerr++; gaps.Add((x, z)); }
                else { _minTerrainY = Mathf.Min(_minTerrainY, t.Value.Y); hb = hb.HasValue ? hb.Value.Expand(new Vector2(x, z)) : new Rect2(x, z, 0, 0); }
            }
        _gaps = gaps.ToArray();
        for (var z = _ext.Position.Y; z < _ext.End.Y; z += 8)
        {
            var sb = new System.Text.StringBuilder();
            for (var x = _ext.Position.X; x < _ext.End.X; x += 4)
                sb.Append(Ray(s, x, z, false) == null ? (_map.ParcelAt(x, z) != null ? 'O' : '.') : '#');
            GD.Print("ROW " + sb);
        }
        GD.Print($"rim walls: {_map.BoundaryWalls}"); GD.Print($"GRID hit-bounds {hb}"); GD.Print($"GRID cells={cells} no_hit_at_all={noAny} (inside parcels {inParcel}) hit_but_no_terrain={noTerr} min_terrain_y={_minTerrainY:F1}");
        foreach (var g in gaps.Take(10)) GD.Print($"  non-terrain-only cell at {g}");
    }

    int _carBad;

    /// <summary>Same placement as NetServer.SpawnCars (road candidates first, 12 m apart, ground ray, origin +0.05) for every vehicle in vehicles.json.</summary>
    void SetupCars()
    {
        var vf = VehiclesFile.Load(Path.Combine(ProjectSettings.GlobalizePath("res://"), "data/vehicles.json"));
        var space = GetWorld3D().DirectSpaceState;
        var rng = new Random(3);
        var road = _map.SpawnPoints.Where(sp => sp.Kind == "road").Select(sp => sp.Position).ToList();
        var pool = road.OrderBy(_ => rng.Next()).Concat(_map.SpawnPoints.Select(sp => sp.Position).Where(v => !road.Contains(v)).OrderBy(_ => rng.Next())).ToList();
        var spots = new List<Vector3>();
        var pi = 0;
        foreach (var def in vf.Vehicles)
        {
            for (var got = 0; pi < pool.Count && got < def.SpawnCount; pi++)
            {
                var p = pool[pi];
                if (spots.Any(v => v.DistanceTo(p) < 12f)) continue;
                var yaw = (float)(rng.NextDouble() * Mathf.Tau);
                if (ServerCar.RestHeight(space, p, yaw, def.HalfWidth, def.HalfLength, p.Y) is not { } gy) continue;
                spots.Add(p);
                var car = new ServerCar { Name = $"{def.Id}{_cars.Count}", Kind = 0, Def = def, HalfWidth = def.HalfWidth, HalfLength = def.HalfLength,
                    OriginPos = new Vector3(p.X, gy + 0.02f, p.Z), OriginYaw = yaw };
                AddChild(car);
                _cars.Add((car, gy));
                got++;
            }
        }
        _carStart = _frame;
        GD.Print($"CARS spawned {_cars.Count}");
    }

    void ReportCars()
    {
        foreach (var (c, gy) in _cars)
        {
            var space = GetWorld3D().DirectSpaceState;
            var rest = ServerCar.RestHeight(space, c.GlobalPosition, c.Rotation.Y, c.HalfWidth, c.HalfLength, c.GlobalPosition.Y);
            var under = rest ?? float.NaN;
            var dy = c.GlobalPosition.Y - (float.IsNaN(under) ? gy : under);
            var bad = float.IsNaN(under) || Mathf.Abs(dy) > 0.03f || c.GlobalPosition.Y < gy - 1;
            if (bad) _carBad++;
            GD.Print($"  car {c.Name} moved={c.GlobalPosition.DistanceTo(c.OriginPos):F3} floor={c.IsOnFloor()} dy={dy * 100:F1}cm spawn_ground={gy:F2} now_y={c.GlobalPosition.Y:F2}{(bad ? "  BAD" : "")}");
        }
        GD.Print($"CARS total={_cars.Count} bad={_carBad}");
        _falls += _carBad;
    }

    void SetupWalk()
    {
        var rng = new Random(7);
        var sp = _map.SpawnPoints;
        for (var i = 0; i < 24 && sp.Count > 0; i++)
        {
            var s = sp[rng.Next(sp.Count)].Position;
            _lines.Add((s, i * Mathf.Tau / 8f + (i / 8) * 0.3f));
        }
        _p = new Player { Name = "P", CaptureMouse = false, UseCamera = false };
        AddChild(_p);
        _walkReady = true;
        StartLine();
    }

    void StartLine()
    {
        var (from, yaw) = _lines[_line];
        var s = GetWorld3D().DirectSpaceState;
        var g = Ray(s, from.X, from.Z, true);
        _p.Teleport((g ?? from) + Vector3.Up * 0.3f);
        _lineGround = (g ?? from).Y;
        _lineStart = _frame;
        _lineFrames = 0;
        _p.InputOverride = new MoveInput { MoveY = 1, Yaw = yaw, Buttons = MoveButtons.Sprint, SpeedScale = 1 };
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        if (_frame == 3)
        {
            if (!_noGrid) Grid();
            if (!_noCars) { SetupCars(); }
            else if (!_noWalk) SetupWalk();
            else Finish();
            return;
        }
        if (_cars.Count > 0 && !_walkReady)
        {
            foreach (var (c, _) in _cars) c.Step(c.Def, 0, 0, false, 1f / 30, Suchohrad.ShooterCore.Water.Empty);
            if (_frame - _carStart == 150) { ReportCars(); if (!_noWalk) SetupWalk(); else Finish(); }
            return;
        }
        if (!_walkReady) return;
        _lineFrames++;
        var pos = _p.GlobalPosition;
        var s = GetWorld3D().DirectSpaceState;
        var g = Ray(s, pos.X, pos.Z, true);
        var anyG = Ray(s, pos.X, pos.Z, false);
        _trail.Add($"f{_lineFrames} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) floor={_p.State.OnFloor} ground={(g == null ? "none" : g.Value.Y.ToString("F2"))} vy={_p.Velocity.Y:F2}");
        if (_trail.Count > 25) _trail.RemoveAt(0);
        if (anyG == null || pos.Y < (g ?? anyG ?? new Vector3(0, -1e9f, 0)).Y - 1.0f)
        { foreach (var t in _trail) GD.Print("  TRAIL " + t); _trail.Clear(); }
        var edge = anyG == null; // walked off the end of the map terrain: not a fall-through
        var fell = !edge && g != null && pos.Y < g.Value.Y - 1.0f;
        if (edge) { _edges++; _falls++; GD.Print($"FALL-EDGE line {_line} walked off the terrain after {_lineFrames / 30f:F1}s at {pos}"); fell = false; _lineFrames = 99999; }
        if (fell || _lineFrames > 30 * 40)
        {
            if (fell)
            {
                _falls++;
                GD.Print($"FALL line {_line} yaw {_lines[_line].yaw:F2} after {_lineFrames / 30f:F1}s at {pos}, ground under {(g == null ? "none" : g.Value.Y.ToString("F2"))}");
            }
            _line++;
            if (_line >= _lines.Count) { Finish(); return; }
            StartLine();
        }
        else _p.Health = 100;
    }

    void Finish()
    {
        GD.Print($"WALK lines={_lines.Count} falls={_falls} ended_at_terrain_edge={_edges}");
        GD.Print(_falls == 0 ? "GROUND_CHECK_OK" : "GROUND_CHECK_FAIL");
        GetTree().Quit(_falls == 0 ? 0 : 2);
        SetPhysicsProcess(false);
    }
}
