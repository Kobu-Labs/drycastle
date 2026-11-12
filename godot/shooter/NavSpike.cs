using System.Diagnostics;
using System.Text.Json;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Bot navigation spike (#214): can the server bake a navigation mesh for the whole map from its own colliders, and is the
/// result usable? Headless, after the collision-only map load (<c>--role server --nav-spike</c>, <c>make shooter-nav-spike</c>).
/// It parses the solid world (layer 1: terrain, buildings, fences, trunks, lamps, placed solids) once, bakes one tile per map
/// chunk on worker threads, puts the tiles on a navigation map and then measures:
/// the bake (per tile and wall time), the mesh size, a save and reload of every tile (the disk cache a server would use),
/// paths between random spawn pairs (found, detour, query time) and paths from the nearest road spawn into every garden and
/// building plot (do the fence gates work). One report.json and a SHOOTER_NAV_SPIKE_OK or _FAIL line, then exit.
/// Nothing else in the game uses this class.
/// </summary>
public partial class NavSpike : Node
{
    /// <summary>The node whose descendants hold the map's static bodies (the shooter root: map chunks and the lamp poles).</summary>
    public Node3D World = null!;
    public Drycastle.MapLoader.MapLoader Map = null!;
    public string MapDir = "";
    /// <summary>Folder for report.json and the cached tiles; empty = no files, the cache test is skipped.</summary>
    public string OutDir = "";
    /// <summary>Navmesh cell size in metres (--nav-cell). Chunk edges must be a multiple of it.</summary>
    public float Cell = 0.25f;
    /// <summary>Bake threads (--nav-threads).</summary>
    public int Threads = Math.Max(1, System.Environment.ProcessorCount / 2);
    /// <summary>Tiles per chunk edge (--nav-split): 1 bakes one tile per 250 m chunk, 2 bakes four.</summary>
    public int Split = 1;
    /// <summary>Random spawn pairs to route between (--nav-pairs).</summary>
    public int Pairs = 300;
    /// <summary>Contour simplification error in cells (--nav-edge-err). At 1.0 or below tile edges stay on the tile boundary; the engine default is 1.3.</summary>
    public float EdgeMaxError = 1.0f;
    /// <summary>Detail mesh sampling distance in cells (--nav-detail-dist; engine default 6, 0 = no height samples, fewest polygons).</summary>
    public float DetailSampleDistance = 6f;
    /// <summary>Detail mesh height error in cells (--nav-detail-err; engine default 1).</summary>
    public float DetailSampleMaxError = 1f;
    /// <summary>Snap baked vertices to this fraction of a cell (--nav-snap; 0 = off), so the vertices of neighbouring tiles land on the same spot.</summary>
    public float Snap;
    /// <summary>Let the map join free edges that lie within its margin (--nav-edge-connections 0|1), on top of exact shared edges.</summary>
    public bool EdgeConnections = true;

    const float CellHeight = 0.25f;
    /// <summary>A path counts as arriving when it ends this close to the goal (metres, horizontal).</summary>
    const float ReachM = 1.0f;
    /// <summary>A point farther than this from the mesh (metres, horizontal) is counted as off the mesh, not routed to.</summary>
    const float OffMeshM = 2.0f;

    /// <summary>X0, Z0 and Size are the tile itself (the baked box without its border).</summary>
    sealed record Tile(int Index, float X0, float Z0, float Size, Aabb Filter, NavigationMesh Mesh) { public double BakeMs; }

    /// <summary>Leave the tree trunks out of the bake (--nav-skip-trunks): far fewer polygons, and a bot must steer around trees itself.</summary>
    public bool SkipTrunks;
    /// <summary>Polygons a path search may visit before it gives up (--nav-max-polys); 0 = no limit.</summary>
    public int MaxPolygons;
    NavigationPathQueryParameters3D? _query;
    readonly NavigationPathQueryResult3D _result = new();
    int _frames, _bakedAt = -1;
    float _tileSize;
    Rid _map;
    readonly List<Tile> _tiles = new();
    readonly List<Rid> _regions = new();
    readonly Dictionary<string, object?> _report = new();

    public override void _Process(double delta)
    {
        _frames++;
        try
        {
            // a few frames after the load so every collider is in the tree; the queries a few frames after the bake so the map has synchronised
            if (_frames == 3) { Bake(); _bakedAt = _frames; }
            else if (_bakedAt > 0 && _frames == _bakedAt + 5) { Measure(); Finish(true); }
        }
        catch (Exception e)
        {
            GD.PrintErr($"nav-spike: {e}");
            Finish(false);
        }
    }

    void Finish(bool ok)
    {
        SetProcess(false);
        ok &= _tiles.Sum(t => t.Mesh.GetPolygonCount()) > 0;
        if (OutDir != "")
        {
            Directory.CreateDirectory(OutDir);
            var path = Path.Combine(OutDir, "report.json");
            File.WriteAllText(path, JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"nav-spike: report {path}");
        }
        GD.Print(ok ? "SHOOTER_NAV_SPIKE_OK" : "SHOOTER_NAV_SPIKE_FAIL");
        GetTree().Quit(ok ? 0 : 2);
    }

    // ---- bake ----

    NavigationMesh NewMesh() => new()
    {
        CellSize = Cell,
        CellHeight = CellHeight,
        AgentRadius = MathF.Ceiling(MoveConsts.CapsuleRadius / Cell) * Cell, // whole cells, as the bake rounds it anyway
        AgentHeight = MoveConsts.CapsuleHeight,
        AgentMaxClimb = CellHeight, // the body has no step-up; one cell keeps kerbs and terrain noise walkable
        AgentMaxSlope = 45f,        // under the body's 50 degree floor limit
        EdgeMaxError = EdgeMaxError,
        DetailSampleDistance = DetailSampleDistance,
        DetailSampleMaxError = DetailSampleMaxError,
        GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
        GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren,
        GeometryCollisionMask = CollisionLayers.World, // not the shot-only cover (tree crowns, bushes): players walk through it
    };

    void Bake()
    {
        var mem0 = OS.GetStaticMemoryUsage();
        var sw = Stopwatch.StartNew();
        var source = new NavigationMeshSourceGeometryData3D();
        // --nav-skip-trunks: the tree trunks are taken off the world layer for the parse, so the mesh has no hole per tree
        var trunks = new List<(StaticBody3D Body, uint Layer)>();
        if (SkipTrunks)
        {
            var stack = new Stack<Node>();
            stack.Push(World);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                foreach (var c in n.GetChildren()) stack.Push(c);
                if (n is StaticBody3D b && b.Name.ToString().Contains("TreeTrunks")) { trunks.Add((b, b.CollisionLayer)); b.CollisionLayer = 0; }
            }
            GD.Print($"nav-spike: {trunks.Count} tree trunk bodies left out of the bake");
        }
        NavigationServer3D.ParseSourceGeometryData(NewMesh(), source, World);
        foreach (var (b, layer) in trunks) b.CollisionLayer = layer;
        var how = "engine parser (static colliders)";
        var skipped = 0;
        if (!source.HasData())
        {
            // the engine parser found nothing (it may skip bodies under hidden nodes): read the shapes ourselves
            how = "manual (trimesh and box shapes)";
            skipped = CollectShapes(World, source);
        }
        var parseMs = sw.Elapsed.TotalMilliseconds;
        if (!source.HasData()) throw new InvalidOperationException("no source geometry: no static collider on layer 1 under the world node");
        var triangles = source.GetIndices().Length / 3;
        var bounds = source.GetBounds();
        GD.Print($"nav-spike: source geometry by {how}: {triangles} triangles, bounds {bounds.Position} + {bounds.Size}, {parseMs:F0} ms{(skipped > 0 ? $", {skipped} shapes of other kinds skipped" : "")}");

        var agentRadius = MathF.Ceiling(MoveConsts.CapsuleRadius / Cell) * Cell;
        var border = MathF.Max(1f, agentRadius + 2 * Cell); // the strip baked and thrown away around a tile, so the erosion at its edge sees the neighbour's obstacles
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(MapDir, "manifest.json")));
        var chunk = (float)manifest.RootElement.GetProperty("chunk_size_m").GetDouble();
        var size = chunk / Math.Max(1, Split);
        if (MathF.Abs(size / Cell - MathF.Round(size / Cell)) > 1e-3f) throw new InvalidOperationException($"tile edge {size} m is not a multiple of the cell size {Cell} m");
        _tileSize = size;
        // --nav-split 0: the control, one bake of the whole map with no tiles and so no tile edges to join
        if (Split == 0) _tiles.Add(new Tile(0, bounds.Position.X, bounds.Position.Z, MathF.Max(bounds.Size.X, bounds.Size.Z), default, NewMesh()));
        foreach (var c in manifest.RootElement.GetProperty("chunks").EnumerateArray())
        {
            if (Split == 0) break;
            int ix = c.GetProperty("index")[0].GetInt32(), iy = c.GetProperty("index")[1].GetInt32();
            // map axes are x east, y north; Godot's z is -north, so the chunk's north edge is its low z
            float x0 = ix * chunk, z0 = -(iy + 1) * chunk;
            for (var sx = 0; sx < Split; sx++)
                for (var sz = 0; sz < Split; sz++)
                {
                    var filter = new Aabb(new Vector3(x0 + sx * size - border, bounds.Position.Y - 1f, z0 + sz * size - border),
                        new Vector3(size + 2 * border, bounds.Size.Y + 2f, size + 2 * border));
                    var mesh = NewMesh();
                    mesh.BorderSize = border;
                    mesh.FilterBakingAabb = filter;
                    _tiles.Add(new Tile(_tiles.Count, x0 + sx * size, z0 + sz * size, size, filter, mesh));
                }
        }

        GD.Print($"nav-spike: baking {_tiles.Count} tiles of {size} m (cell {Cell} m, agent radius {agentRadius} m, border {border} m) on {Threads} threads");
        sw.Restart();
        var done = 0;
        Parallel.ForEach(_tiles, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            var one = Stopwatch.StartNew();
            NavigationServer3D.BakeFromSourceGeometryData(t.Mesh, source);
            t.BakeMs = one.Elapsed.TotalMilliseconds;
            if (Snap > 0)
            {
                var v = t.Mesh.GetVertices();
                for (var i = 0; i < v.Length; i++) v[i] = v[i].Snapped(Vector3.One * (Cell * Snap));
                t.Mesh.SetVertices(v);
            }
            var n = Interlocked.Increment(ref done);
            if (n % 8 == 0 || n == _tiles.Count) GD.Print($"nav-spike: baked {n}/{_tiles.Count}");
        });
        var bakeWallMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        _map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetCellSize(_map, Cell);
        NavigationServer3D.MapSetCellHeight(_map, CellHeight);
        NavigationServer3D.MapSetUseAsyncIterations(_map, false);
        NavigationServer3D.MapSetUseEdgeConnections(_map, EdgeConnections);
        NavigationServer3D.MapSetActive(_map, true);
        foreach (var t in _tiles)
        {
            if (t.Mesh.GetPolygonCount() == 0) continue;
            var region = NavigationServer3D.RegionCreate();
            NavigationServer3D.RegionSetMap(region, _map);
            NavigationServer3D.RegionSetNavigationMesh(region, t.Mesh);
            _regions.Add(region);
        }
        NavigationServer3D.MapForceUpdate(_map);
        var mapMs = sw.Elapsed.TotalMilliseconds;

        var ms = _tiles.Select(t => t.BakeMs).ToList();
        var polys = _tiles.Sum(t => t.Mesh.GetPolygonCount());
        var verts = _tiles.Sum(t => t.Mesh.GetVertices().Length);
        var memMb = (OS.GetStaticMemoryUsage() - mem0) / (1024.0 * 1024.0);
        GD.Print($"nav-spike: bake wall {bakeWallMs / 1000:F1} s on {Threads} threads; per tile ms p50 {Pct(ms, 50):F0}, p95 {Pct(ms, 95):F0}, max {ms.Max():F0}, sum {ms.Sum() / 1000:F1} s");
        GD.Print($"nav-spike: {polys} polygons, {verts} vertices in {_regions.Count} non-empty tiles of {_tiles.Count}; map build {mapMs:F0} ms; engine memory +{memMb:F0} MB");
        _report["map_version"] = Map.MapVersion;
        _report["config"] = new Dictionary<string, object?> { ["cell_m"] = Cell, ["cell_height_m"] = CellHeight, ["agent_radius_m"] = agentRadius, ["border_m"] = border, ["tile_m"] = size, ["threads"] = Threads,
            ["edge_max_error"] = EdgeMaxError, ["detail_sample_distance"] = DetailSampleDistance, ["detail_sample_max_error"] = DetailSampleMaxError, ["snap"] = Snap, ["edge_connections"] = EdgeConnections,
            ["skip_trunks"] = SkipTrunks, ["max_polygons"] = MaxPolygons };
        _report["source"] = new Dictionary<string, object?> { ["how"] = how, ["triangles"] = triangles, ["parse_ms"] = parseMs, ["shapes_skipped"] = skipped };
        _report["bake"] = new Dictionary<string, object?>
        {
            ["tiles"] = _tiles.Count, ["tiles_with_mesh"] = _regions.Count, ["wall_s"] = bakeWallMs / 1000, ["cpu_s"] = ms.Sum() / 1000,
            ["tile_ms_p50"] = Pct(ms, 50), ["tile_ms_p95"] = Pct(ms, 95), ["tile_ms_max"] = ms.Max(),
            ["polygons"] = polys, ["vertices"] = verts, ["map_build_ms"] = mapMs, ["engine_memory_mb"] = memMb,
        };
    }

    /// <summary>
    /// Fallback source geometry: the faces of every trimesh and box shape of the static bodies on the world layer. Other shape
    /// kinds (the pyramids of some props) are counted and left out. Winding does not matter for obstacles, and the walkable
    /// surfaces (terrain, roofs) are trimeshes with their own winding.
    /// </summary>
    static int CollectShapes(Node root, NavigationMeshSourceGeometryData3D source)
    {
        var skipped = 0;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is not CollisionShape3D { Disabled: false } cs || cs.GetParent() is not StaticBody3D body || (body.CollisionLayer & CollisionLayers.World) == 0) continue;
            switch (cs.Shape)
            {
                case ConcavePolygonShape3D tri: source.AddFaces(tri.GetFaces(), cs.GlobalTransform); break;
                case BoxShape3D box: source.AddFaces(BoxFaces(box.Size), cs.GlobalTransform); break;
                default: skipped++; break;
            }
        }
        return skipped;
    }

    static Vector3[] BoxFaces(Vector3 size)
    {
        var h = size / 2;
        var v = new Vector3[8];
        for (var i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
        // two triangles per side: -x, +x, -y, +y, -z, +z
        int[] quads = { 0, 2, 6, 4, 1, 5, 7, 3, 0, 4, 5, 1, 2, 3, 7, 6, 0, 1, 3, 2, 4, 6, 7, 5 };
        var faces = new Vector3[36];
        for (var q = 0; q < 6; q++)
        {
            Vector3 a = v[quads[q * 4]], b = v[quads[q * 4 + 1]], c = v[quads[q * 4 + 2]], d = v[quads[q * 4 + 3]];
            faces[q * 6] = a; faces[q * 6 + 1] = b; faces[q * 6 + 2] = c;
            faces[q * 6 + 3] = a; faces[q * 6 + 4] = c; faces[q * 6 + 5] = d;
        }
        return faces;
    }

    // ---- measurements ----

    void Measure()
    {
        if (_regions.Count == 0) throw new InvalidOperationException("the bake produced no polygons");
        if (NavigationServer3D.MapGetIterationId(_map) == 0) throw new InvalidOperationException("the navigation map never synchronised (iteration id 0)");
        var connections = _regions.Sum(r => NavigationServer3D.RegionGetConnectionsCount(r));
        var alone = _regions.Count(r => NavigationServer3D.RegionGetConnectionsCount(r) == 0);
        GD.Print($"nav-spike: {connections} tile-to-tile connections; {alone} tiles connect to no neighbour");
        // how far each tile's mesh stops short of (+) or runs past (-) its own boundary, per side: a stitched map needs about 0 on shared sides
        var insets = new List<double>();
        foreach (var t in _tiles)
        {
            var v = t.Mesh.GetVertices();
            if (v.Length == 0) continue;
            insets.Add(v.Min(p => p.X) - t.X0); insets.Add(t.X0 + t.Size - v.Max(p => p.X));
            insets.Add(v.Min(p => p.Z) - t.Z0); insets.Add(t.Z0 + t.Size - v.Max(p => p.Z));
        }
        GD.Print($"nav-spike: tile side inset m (mesh edge to tile boundary) min {insets.Min():F3}, p25 {Pct(insets, 25):F3}, p50 {Pct(insets, 50):F3}, p75 {Pct(insets, 75):F3}, max {insets.Max():F1}");
        _report["stitching"] = new Dictionary<string, object?> { ["connections"] = connections, ["tiles_without_neighbour"] = alone,
            ["side_inset_m_min"] = insets.Min(), ["side_inset_m_p25"] = Pct(insets, 25), ["side_inset_m_p50"] = Pct(insets, 50), ["side_inset_m_p75"] = Pct(insets, 75) };
        CacheTest();
        Islands();
        SpawnPairs();
        Yards();
    }

    /// <summary>Saves every tile and loads it back: the size and the time of the disk cache a server would keep per chunk hash.</summary>
    void CacheTest()
    {
        if (OutDir == "") return;
        var dir = Path.Combine(OutDir, "tiles");
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        var paths = new List<string>();
        foreach (var t in _tiles.Where(t => t.Mesh.GetPolygonCount() > 0))
        {
            var path = Path.Combine(dir, $"tile_{t.Index:D3}.res");
            var err = ResourceSaver.Save(t.Mesh, path, ResourceSaver.SaverFlags.Compress);
            if (err != Error.Ok) throw new InvalidOperationException($"saving {path}: {err}");
            paths.Add(path);
        }
        var saveMs = sw.Elapsed.TotalMilliseconds;
        var bytes = paths.Sum(p => new FileInfo(p).Length);
        sw.Restart();
        var polys = 0;
        foreach (var p in paths)
            polys += (ResourceLoader.Load(p, "", ResourceLoader.CacheMode.Ignore) as NavigationMesh)?.GetPolygonCount() ?? throw new InvalidOperationException($"loading {p} gave no navigation mesh");
        var loadMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"nav-spike: cache {paths.Count} tiles, {bytes / (1024.0 * 1024.0):F1} MB, save {saveMs:F0} ms, load {loadMs:F0} ms ({polys} polygons back)");
        _report["cache"] = new Dictionary<string, object?> { ["tiles"] = paths.Count, ["bytes"] = bytes, ["save_ms"] = saveMs, ["load_ms"] = loadMs, ["polygons_loaded"] = polys };
    }

    /// <summary>End is where the path stopped: the goal, or for an unreached goal the reachable point nearest to it (the barrier).</summary>
    sealed record Route(bool Reached, double StraightM, double PathM, double QueryUs, int Points, Vector3 End);

    /// <summary>Horizontal distance from a point to the nearest tile boundary line.</summary>
    float ToTileEdge(Vector3 p)
    {
        static float Off(float v, float size) { var m = v - MathF.Floor(v / size) * size; return MathF.Min(m, size - m); }
        return MathF.Min(Off(p.X, _tileSize), Off(p.Z, _tileSize));
    }

    /// <summary>
    /// Which spawns can reach which: the groups (islands of the mesh that hold a spawn), and where the routes out of the
    /// largest group stop. A stop on a tile boundary means the tiles did not join there; anywhere else it is the map itself
    /// (a fence without a gate, water, a slope).
    /// </summary>
    void Islands()
    {
        var spawns = Map.SpawnPoints.Select(s => NavigationServer3D.MapGetClosestPoint(_map, s.Position)).ToList();
        var group = Enumerable.Repeat(-1, spawns.Count).ToArray();
        var groups = new List<List<int>>();
        var stops = new List<Vector3>();
        for (var i = 0; i < spawns.Count; i++)
        {
            if (group[i] >= 0) continue;
            var g = groups.Count;
            groups.Add(new List<int> { i });
            group[i] = g;
            for (var j = i + 1; j < spawns.Count; j++)
            {
                if (group[j] >= 0) continue;
                var r = Query(spawns[i], spawns[j]);
                if (r.Reached) { group[j] = g; groups[g].Add(j); }
                else if (g == 0) stops.Add(r.End);
            }
        }
        var sizes = groups.Select(g => g.Count).OrderByDescending(n => n).ToList();
        var onEdge = stops.Count(s => ToTileEdge(s) <= 0.5f);
        GD.Print($"nav-spike: islands: {spawns.Count} spawns fall into {groups.Count} groups, sizes {string.Join(", ", sizes.Take(12))}{(sizes.Count > 12 ? ", ..." : "")}");
        GD.Print($"nav-spike: islands: {onEdge}/{stops.Count} routes out of the first group stop within 0.5 m of a tile boundary");
        // the most common stopping places, on a 5 m grid
        var places = stops.GroupBy(s => (X: MathF.Round(s.X / 5) * 5, Z: MathF.Round(s.Z / 5) * 5)).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new Dictionary<string, object?> { ["x"] = g.Key.X, ["z"] = g.Key.Z, ["routes"] = g.Count(), ["to_tile_edge_m"] = Math.Round(g.Min(ToTileEdge), 2) }).ToList();
        foreach (var p in places) GD.Print($"nav-spike: islands: {p["routes"]} routes stop near x {p["x"]}, z {p["z"]} (map north {-(float)p["z"]!}), {p["to_tile_edge_m"]} m from a tile boundary");
        _report["islands"] = new Dictionary<string, object?> { ["groups"] = groups.Count, ["sizes"] = sizes, ["stops"] = stops.Count, ["stops_on_tile_edge"] = onEdge, ["stop_places"] = places };
    }

    Route Query(Vector3 from, Vector3 to)
    {
        // MapGetPath searches a limited number of polygons (the engine default) and then returns a path to the nearest polygon it found,
        // which cuts every long route short on a map this size; the query object lifts the limit
        _query ??= new NavigationPathQueryParameters3D { Map = _map, PathSearchMaxPolygons = MaxPolygons };
        _query.StartPosition = from;
        _query.TargetPosition = to;
        var sw = Stopwatch.StartNew();
        NavigationServer3D.QueryPath(_query, _result);
        var us = sw.Elapsed.TotalMicroseconds;
        var path = _result.Path;
        double len = 0;
        for (var i = 1; i < path.Length; i++) len += path[i - 1].DistanceTo(path[i]);
        var reached = path.Length > 0 && Flat(path[^1], to) <= ReachM;
        return new Route(reached, from.DistanceTo(to), len, us, path.Length, path.Length > 0 ? path[^1] : from);
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>Random pairs of spawn points: the long routes a bot walks to reach the action.</summary>
    void SpawnPairs()
    {
        var spawns = Map.SpawnPoints.Select(s => s.Position).ToList();
        if (spawns.Count < 2) { GD.Print("nav-spike: fewer than two spawns, no pair test"); return; }
        var onMesh = spawns.Select(s => NavigationServer3D.MapGetClosestPoint(_map, s)).ToList();
        var off = Enumerable.Range(0, spawns.Count).Count(i => Flat(spawns[i], onMesh[i]) > OffMeshM);
        var rng = new Random(1);
        var routes = new List<Route>();
        for (var k = 0; k < Pairs; k++)
        {
            int a = rng.Next(spawns.Count), b = rng.Next(spawns.Count);
            if (a == b) continue;
            routes.Add(Query(onMesh[a], onMesh[b]));
        }
        var ok = routes.Where(r => r.Reached).ToList();
        var detour = ok.Where(r => r.StraightM > 1).Select(r => r.PathM / r.StraightM).ToList();
        var us = routes.Select(r => r.QueryUs).ToList();
        GD.Print($"nav-spike: spawn pairs {ok.Count}/{routes.Count} reached; {off}/{spawns.Count} spawns are off the mesh; detour p50 {Pct(detour, 50):F2}, p95 {Pct(detour, 95):F2}; "
                 + $"query us p50 {Pct(us, 50):F0}, p95 {Pct(us, 95):F0}, max {(us.Count > 0 ? us.Max() : 0):F0}; straight m p50 {Pct(routes.Select(r => r.StraightM).ToList(), 50):F0}");
        _report["spawn_pairs"] = new Dictionary<string, object?>
        {
            ["pairs"] = routes.Count, ["reached"] = ok.Count, ["spawns"] = spawns.Count, ["spawns_off_mesh"] = off,
            ["detour_p50"] = Pct(detour, 50), ["detour_p95"] = Pct(detour, 95),
            ["query_us_p50"] = Pct(us, 50), ["query_us_p95"] = Pct(us, 95), ["query_us_max"] = us.Count > 0 ? us.Max() : 0,
            ["straight_m_p50"] = Pct(routes.Select(r => r.StraightM).ToList(), 50), ["path_points_max"] = routes.Count > 0 ? routes.Max(r => r.Points) : 0,
        };
    }

    /// <summary>
    /// From the nearest road spawn to the anchor of every garden and building plot: whether a bot gets into a yard (through
    /// the gate gap of its fence). An anchor off the mesh (inside the building, usually) is counted apart.
    /// </summary>
    void Yards()
    {
        var roads = Map.SpawnPoints.Where(s => s.Kind == "road").Select(s => s.Position).ToList();
        if (roads.Count == 0) roads = Map.SpawnPoints.Select(s => s.Position).ToList();
        if (roads.Count == 0) return;
        int tested = 0, reached = 0, offMesh = 0, onRoof = 0, gardensUnreached = 0;
        var unreached = new List<string>();
        var us = new List<double>();
        foreach (var p in Map.Parcels.Values.Where(p => p.Kind is "garden" or "building_plot").OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var a = p.AnchorXZ;
            var anchor = new Vector3(a.X, Map.GroundHeightOrMean(a.X, a.Z), a.Z);
            var goal = NavigationServer3D.MapGetClosestPoint(_map, anchor);
            if (Flat(goal, anchor) > OffMeshM) { offMesh++; continue; }
            var start = NavigationServer3D.MapGetClosestPoint(_map, roads.MinBy(r => Flat(r, anchor)));
            var route = Query(start, goal);
            tested++;
            us.Add(route.QueryUs);
            if (route.Reached) reached++;
            else
            {
                unreached.Add(p.Id);
                // an anchor under a roof snaps to the roof's own patch of mesh, which nothing on the ground connects to
                if (goal.Y - anchor.Y > 1.5f) onRoof++;
                else if (p.Kind == "garden") gardensUnreached++;
            }
        }
        GD.Print($"nav-spike: yards {reached}/{tested} reached from the nearest road spawn; {offMesh} anchors off the mesh; query us p95 {Pct(us, 95):F0}");
        GD.Print($"nav-spike: yards unreached: {onRoof} have their goal on a roof (the anchor is inside the building), {unreached.Count - onRoof} on the ground, of those {gardensUnreached} gardens");
        _report["yards_unreached"] = new Dictionary<string, object?> { ["goal_on_roof"] = onRoof, ["on_ground"] = unreached.Count - onRoof, ["gardens_on_ground"] = gardensUnreached };
        if (unreached.Count > 0) GD.Print($"nav-spike: first unreached yards: {string.Join(", ", unreached.Take(20))}");
        _report["yards"] = new Dictionary<string, object?> { ["tested"] = tested, ["reached"] = reached, ["anchor_off_mesh"] = offMesh, ["query_us_p95"] = Pct(us, 95), ["unreached"] = unreached };
    }

    static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * s.Count) - 1))];
    }
}
