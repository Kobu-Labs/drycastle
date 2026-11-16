using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The walkable map the bots find their routes on (#214): a navigation mesh baked from the server's own colliders, one
/// tile per map chunk, so it agrees with what stops a player. Settings come from the spike (<see cref="NavSpike"/>,
/// <c>make shooter-nav-spike</c>): 0.5 m cells, tree trunks left out (they are three quarters of the polygons, and every one is a hole a route is searched round) and
/// every door baked open (the state the mesh was baked in is not the state a door has when someone walks, so it is the walker's to ask: a bot's body stops at a closed door, a citizen's or
/// member's <see cref="WalkGuard"/> asks <see cref="WorldQuery.Ahead"/>, which also finds a trunk and a parked vehicle). The bake takes a few seconds for the village, so the tiles are kept on disk under a key made
/// from the map's manifest, its placed objects and lamps, and the settings: a server bakes once per map version.
/// <para>
/// A route costs 13-16 ms across the village, too much for the tick, so the tick does not compute them: <see cref="Submit"/> hands a request to ONE background worker and
/// the server collects the answers with <see cref="TryTake"/> at a fixed point of each tick, so they are applied on the tick thread and never from the worker. The map is
/// static after <see cref="Build"/> (no async iterations, forced update), and the engine's path query is made for other threads: it takes a path query slot of the map's
/// current iteration under a lock (Godot 4.7.2, <c>NavMap3D::query_path</c>), and the closest-point lookups read that iteration. The worker has its own query and result objects.
/// The synchronous <see cref="Route"/> stays for the callers that wait (citizens, on the tick thread, with its own pair). The engine's simple path call gives up after a fixed
/// number of polygons and returns a route cut short, so the query object is used with that limit off.
/// </para>
/// </summary>
public sealed class BotNav
{
    public const float Cell = 0.5f;
    /// <summary>
    /// The bake's vertical cell, metres. The mesh lies one cell above the ground plus the rounding up to the next cell, so with 0.25 m a walker (who takes its height from the route) went
    /// 0.25 to 0.5 m above flat ground; with 0.05 m it is 0.05 to 0.1 m.
    /// </summary>
    public const float CellHeight = 0.05f;
    /// <summary>The step a route may take, metres: a whole number of <see cref="CellHeight"/>.</summary>
    const float MaxClimb = 0.25f;
    /// <summary>The bake counts height in 13 bits of cells: a map taller than this many metres would be baked wrong.</summary>
    const float MaxBakeHeightM = 8000 * CellHeight;
    /// <summary>Part of the cache key: change it with any setting below, so old tiles are not reused.</summary>
    const string Settings = "nav3 cell=0.5 height=0.05 radius=0.5 climb=0.25 slope=45 edge=1.0 detail=0.25 trunks=out doors=open";
    const float AgentRadius = 0.5f; // one cell: the player capsule (0.35 m) rounded up
    const float Border = AgentRadius + 2 * Cell; // baked around a tile and thrown away, so its edge sees the neighbour's obstacles

    Rid _map;
    readonly List<Rid> _regions = new();
    readonly List<NavigationMesh> _meshes = new(); // kept alive: the regions hold them
    NavigationPathQueryParameters3D _query = null!;
    readonly NavigationPathQueryResult3D _result = new();

    // ---- the route worker ----

    /// <summary>Most requests out with the worker at once (queued or being worked on): about a tenth of a second of work, so a burst cannot make routes arrive seconds late.</summary>
    public const int MaxInFlight = 8;

    /// <summary>An answer: the ticket of the request, the points (empty when there is no mesh under the start) and how long the worker took.</summary>
    public readonly record struct Answer(long Ticket, Vector3[] Path, double WorkMs);

    readonly record struct Job(long Ticket, Vector3 From, Vector3 To);

    readonly ConcurrentQueue<Job> _jobs = new();
    readonly ConcurrentQueue<Answer> _answers = new();
    readonly SemaphoreSlim _wake = new(0);
    Thread? _worker;
    volatile bool _stop;
    int _inFlight; // submitted and not yet taken; the tick thread only

    /// <summary>Requests out with the worker (including answers not taken yet).</summary>
    public int InFlight => _inFlight;

    /// <summary>Room for another request (<see cref="MaxInFlight"/>).</summary>
    public bool HasRoom => _inFlight < MaxInFlight;

    /// <summary>
    /// Sends a route request to the worker; the answer comes back through <see cref="TryTake"/> carrying <paramref name="ticket"/> (unique per request, chosen by the caller, so it can
    /// tell a stale answer). False (nothing sent) when <see cref="HasRoom"/> is false. Tick thread.
    /// </summary>
    public bool Submit(long ticket, Vector3 from, Vector3 to)
    {
        if (_inFlight >= MaxInFlight) return false;
        _inFlight++;
        if (_worker == null) { _answers.Enqueue(new Answer(ticket, Array.Empty<Vector3>(), 0)); return true; } // no mesh: no route, at once
        _jobs.Enqueue(new Job(ticket, from, to));
        _wake.Release();
        return true;
    }

    /// <summary>The next finished route, oldest first. Called on the tick thread at one fixed point of the tick.</summary>
    public bool TryTake(out Answer answer)
    {
        if (!_answers.TryDequeue(out answer)) return false;
        _inFlight--;
        return true;
    }

    void StartWorker()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "bot-nav" };
        _worker.Start();
    }

    void Work()
    {
        // its own query and result: the tick thread's pair (Route) is never touched from here
        var query = new NavigationPathQueryParameters3D { Map = _map, PathSearchMaxPolygons = 0 };
        var result = new NavigationPathQueryResult3D();
        while (true)
        {
            _wake.Wait();
            if (_stop) break;
            if (!_jobs.TryDequeue(out var job)) continue;
            var t0 = Stopwatch.GetTimestamp();
            Vector3[] path;
            try { path = Query(_map, query, result, job.From, job.To); }
            catch (Exception e) { GD.PrintErr($"server: bot navigation worker: {e.Message}"); path = Array.Empty<Vector3>(); }
            _answers.Enqueue(new Answer(job.Ticket, path, Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
        }
        query.Dispose();
        result.Dispose();
    }

    static Vector3[] Query(Rid map, NavigationPathQueryParameters3D query, NavigationPathQueryResult3D result, Vector3 from, Vector3 to)
    {
        query.StartPosition = NavigationServer3D.MapGetClosestPoint(map, from);
        query.TargetPosition = NavigationServer3D.MapGetClosestPoint(map, to);
        NavigationServer3D.QueryPath(query, result);
        return result.Path;
    }

    public int Polygons { get; private set; }
    public int Tiles => _regions.Count;
    /// <summary>"cache" or "bake".</summary>
    public string Source { get; private set; } = "";
    public double BuildSeconds { get; private set; }

    /// <summary>
    /// Loads the tiles for this map from <paramref name="cacheRoot"/>, or bakes them from the static colliders under
    /// <paramref name="world"/> and saves them there. Blocks for the bake (seconds); call it before players join.
    /// </summary>
    public static BotNav Build(Node world, string mapDir, string cacheRoot, int threads)
    {
        var sw = Stopwatch.StartNew();
        var nav = new BotNav();
        var dir = Path.Combine(cacheRoot, CacheKey(mapDir));
        var meshes = LoadTiles(dir);
        nav.Source = meshes != null ? "cache" : "bake";
        if (meshes == null)
        {
            meshes = Bake(world, mapDir, threads);
            SaveTiles(dir, meshes);
        }
        nav._map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetCellSize(nav._map, Cell);
        NavigationServer3D.MapSetCellHeight(nav._map, CellHeight);
        NavigationServer3D.MapSetUseAsyncIterations(nav._map, false); // the map is built once, here, before anyone asks for a route
        NavigationServer3D.MapSetActive(nav._map, true);
        foreach (var m in meshes)
        {
            var region = NavigationServer3D.RegionCreate();
            NavigationServer3D.RegionSetMap(region, nav._map);
            NavigationServer3D.RegionSetNavigationMesh(region, m);
            nav._regions.Add(region);
            nav._meshes.Add(m);
            nav.Polygons += m.GetPolygonCount();
        }
        // take the regions in now: citizens build this map on first use and ask for routes in the same frame, before the next
        // physics frame would have synchronised it (the query then fails: "made before first map synchronization")
        NavigationServer3D.MapForceUpdate(nav._map);
        nav._query = new NavigationPathQueryParameters3D { Map = nav._map, PathSearchMaxPolygons = 0 };
        if (nav._regions.Count > 0) nav.StartWorker();
        nav.BuildSeconds = sw.Elapsed.TotalSeconds;
        return nav;
    }

    /// <summary>
    /// The points to walk from one place to another, both snapped to the mesh. When the goal cannot be reached the route
    /// ends at the reachable point nearest to it. Empty when there is no mesh under the start.
    /// </summary>
    public Vector3[] Route(Vector3 from, Vector3 to)
    {
        if (_regions.Count == 0) return Array.Empty<Vector3>();
        return Query(_map, _query, _result, from, to);
    }

    /// <summary>The point of the mesh nearest to <paramref name="p"/> (where a citizen stands when it leaves a house).</summary>
    public Vector3 Snap(Vector3 p) => NavigationServer3D.MapGetClosestPoint(_map, p);

    public void Free()
    {
        if (_worker != null) // stop it before the map goes: it may be inside a query
        {
            _stop = true;
            _wake.Release();
            _worker.Join();
            _worker = null;
        }
        foreach (var r in _regions) NavigationServer3D.FreeRid(r);
        _regions.Clear();
        _meshes.Clear();
        if (_map.IsValid) NavigationServer3D.FreeRid(_map);
    }

    // ---- bake ----

    static NavigationMesh NewMesh() => new()
    {
        CellSize = Cell,
        CellHeight = CellHeight,
        AgentRadius = AgentRadius,
        AgentHeight = MathF.Ceiling(MoveConsts.CapsuleHeight / CellHeight) * CellHeight, // whole cells, as the bake rounds it anyway (silences the precision warning)
        AgentMaxClimb = MaxClimb,   // the body has no step-up; this keeps kerbs and terrain noise walkable
        DetailSampleMaxError = MaxClimb / CellHeight, // in cells: 0.25 m, as it was with the 0.25 m cell. Left at 1 cell (0.05 m) the village bakes to 142 000 polygons, not 72 000, and no lower
        AgentMaxSlope = 45f,        // under the body's 50 degree floor limit
        EdgeMaxError = 1.0f,        // at 1.0 or below a tile's edge stays on the tile boundary, so neighbours join
        GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
        GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren,
        GeometryCollisionMask = CollisionLayers.World, // not the shot-only cover (tree crowns, bushes): players walk through it
    };

    static List<NavigationMesh> Bake(Node world, string mapDir, int threads)
    {
        // the tree trunks are taken off the world layer for the parse only, so the mesh has no hole per tree (a walker meets a trunk at walk time, WorldQuery.Ahead); the doors are
        // opened for it, so the doorways are in the mesh whatever state the doors happen to have now (the walker meets a closed one at walk time as well)
        var trunks = new List<(StaticBody3D Body, uint Layer)>();
        var doors = new List<DoorNode>();
        var stack = new Stack<Node>();
        stack.Push(world);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is StaticBody3D b && b.Name.ToString().Contains("TreeTrunks")) { trunks.Add((b, b.CollisionLayer)); b.CollisionLayer = 0; }
            else if (n is DoorNode d) { doors.Add(d); d.PassForBake(true); }
        }
        var source = new NavigationMeshSourceGeometryData3D();
        try { NavigationServer3D.ParseSourceGeometryData(NewMesh(), source, world); }
        finally
        {
            foreach (var (b, layer) in trunks) b.CollisionLayer = layer;
            foreach (var d in doors) d.PassForBake(false);
        }
        if (!source.HasData()) throw new InvalidOperationException("bot navigation: no static collider on the world layer to bake from");

        var bounds = source.GetBounds();
        if (bounds.Size.Y + 2f > MaxBakeHeightM) throw new InvalidOperationException($"bot navigation: the map is {bounds.Size.Y:F0} m tall, over the {MaxBakeHeightM:F0} m a {CellHeight} m cell can bake");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapDir, "manifest.json")));
        var chunk = (float)manifest.RootElement.GetProperty("chunk_size_m").GetDouble();
        if (MathF.Abs(chunk / Cell - MathF.Round(chunk / Cell)) > 1e-3f) throw new InvalidOperationException($"bot navigation: the chunk size {chunk} m is not a multiple of the {Cell} m cell");
        var tiles = new List<NavigationMesh>();
        foreach (var c in manifest.RootElement.GetProperty("chunks").EnumerateArray())
        {
            int ix = c.GetProperty("index")[0].GetInt32(), iy = c.GetProperty("index")[1].GetInt32();
            // map axes are x east, y north; Godot's z is -north, so the chunk's north edge is its low z
            var mesh = NewMesh();
            mesh.BorderSize = Border;
            mesh.FilterBakingAabb = new Aabb(new Vector3(ix * chunk - Border, bounds.Position.Y - 1f, -(iy + 1) * chunk - Border),
                new Vector3(chunk + 2 * Border, bounds.Size.Y + 2f, chunk + 2 * Border));
            tiles.Add(mesh);
        }
        // one tile cannot hold the whole map (the bake returns nothing), and tiles bake side by side
        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, t => NavigationServer3D.BakeFromSourceGeometryData(t, source));
        return tiles.Where(t => t.GetPolygonCount() > 0).ToList();
    }

    // ---- cache ----

    /// <summary>What the mesh depends on: the map build (the manifest lists every chunk's hash), the placed objects and lamps, and the settings. Trees are not baked.</summary>
    static string CacheKey(string mapDir)
    {
        using var sha = SHA256.Create();
        foreach (var f in new[] { "manifest.json", "objects.json", "lamps.json" })
        {
            var path = Path.Combine(mapDir, f);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        var settings = Encoding.UTF8.GetBytes(Settings);
        sha.TransformFinalBlock(settings, 0, settings.Length);
        return Convert.ToHexString(sha.Hash!)[..20].ToLowerInvariant();
    }

    const string DoneMarker = "complete";

    /// <summary>The cached tiles, or null when the folder is missing, unfinished or unreadable (then it is baked again).</summary>
    static List<NavigationMesh>? LoadTiles(string dir)
    {
        if (!File.Exists(Path.Combine(dir, DoneMarker))) return null;
        var meshes = new List<NavigationMesh>();
        foreach (var path in Directory.GetFiles(dir, "tile_*.res").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) is not NavigationMesh m) return null;
            meshes.Add(m);
        }
        return meshes.Count > 0 ? meshes : null;
    }

    /// <summary>Writes the tiles and, last, the marker that says the folder is whole. A failed save only costs the next start a bake.</summary>
    static void SaveTiles(string dir, List<NavigationMesh> meshes)
    {
        try
        {
            Directory.CreateDirectory(dir);
            for (var i = 0; i < meshes.Count; i++)
                if (ResourceSaver.Save(meshes[i], Path.Combine(dir, $"tile_{i:D3}.res"), ResourceSaver.SaverFlags.Compress) != Error.Ok) return;
            File.WriteAllText(Path.Combine(dir, DoneMarker), Settings + "\n");
        }
        catch (IOException e) { GD.PrintErr($"server: bot navigation cache not written: {e.Message}"); }
        catch (UnauthorizedAccessException e) { GD.PrintErr($"server: bot navigation cache not written: {e.Message}"); }
    }
}
