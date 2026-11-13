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
/// <c>make shooter-nav-spike</c>): 0.5 m cells, and tree trunks left out (they are three quarters of the polygons; a bot
/// steers round a tree itself). The bake takes a few seconds for the village, so the tiles are kept on disk under a key made
/// from the map's manifest, its placed objects and lamps, and the settings: a server bakes once per map version.
/// <para>
/// Route queries run on the tick (the engine does not promise they are safe on another thread), a few milliseconds each
/// across the village; the caller rations them. The engine's simple path call gives up after a fixed number of polygons
/// and returns a route cut short, so the query object is used with that limit off.
/// </para>
/// </summary>
public sealed class BotNav
{
    public const float Cell = 0.5f, CellHeight = 0.25f;
    /// <summary>Part of the cache key: change it with any setting below, so old tiles are not reused.</summary>
    const string Settings = "nav1 cell=0.5 height=0.25 radius=0.5 climb=0.25 slope=45 edge=1.0 trunks=out";
    const float AgentRadius = 0.5f; // one cell: the player capsule (0.35 m) rounded up
    const float Border = AgentRadius + 2 * Cell; // baked around a tile and thrown away, so its edge sees the neighbour's obstacles

    Rid _map;
    readonly List<Rid> _regions = new();
    readonly List<NavigationMesh> _meshes = new(); // kept alive: the regions hold them
    NavigationPathQueryParameters3D _query = null!;
    readonly NavigationPathQueryResult3D _result = new();

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
        // the map takes the regions in at the next physics frame; players (and so bots) join later than that
        nav._query = new NavigationPathQueryParameters3D { Map = nav._map, PathSearchMaxPolygons = 0 };
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
        _query.StartPosition = NavigationServer3D.MapGetClosestPoint(_map, from);
        _query.TargetPosition = NavigationServer3D.MapGetClosestPoint(_map, to);
        NavigationServer3D.QueryPath(_query, _result);
        return _result.Path;
    }

    /// <summary>The point of the mesh nearest to <paramref name="p"/> (where a citizen stands when it leaves a house).</summary>
    public Vector3 Snap(Vector3 p) => NavigationServer3D.MapGetClosestPoint(_map, p);

    public void Free()
    {
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
        AgentHeight = MoveConsts.CapsuleHeight,
        AgentMaxClimb = CellHeight, // the body has no step-up; one cell keeps kerbs and terrain noise walkable
        AgentMaxSlope = 45f,        // under the body's 50 degree floor limit
        EdgeMaxError = 1.0f,        // at 1.0 or below a tile's edge stays on the tile boundary, so neighbours join
        GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
        GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren,
        GeometryCollisionMask = CollisionLayers.World, // not the shot-only cover (tree crowns, bushes): players walk through it
    };

    static List<NavigationMesh> Bake(Node world, string mapDir, int threads)
    {
        // the tree trunks are taken off the world layer for the parse only, so the mesh has no hole per tree
        var trunks = new List<(StaticBody3D Body, uint Layer)>();
        var stack = new Stack<Node>();
        stack.Push(world);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.GetChildren()) stack.Push(c);
            if (n is StaticBody3D b && b.Name.ToString().Contains("TreeTrunks")) { trunks.Add((b, b.CollisionLayer)); b.CollisionLayer = 0; }
        }
        var source = new NavigationMeshSourceGeometryData3D();
        try { NavigationServer3D.ParseSourceGeometryData(NewMesh(), source, world); }
        finally { foreach (var (b, layer) in trunks) b.CollisionLayer = layer; }
        if (!source.HasData()) throw new InvalidOperationException("bot navigation: no static collider on the world layer to bake from");

        var bounds = source.GetBounds();
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
