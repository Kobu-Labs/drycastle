using System.Text.Json;
using Godot;

namespace Suchohrad.MapLoader;

/// <summary>
/// Shared runtime loader for the Suchohrad base map. Loads a map folder (manifest.json,
/// parcels.json, spawns.json, roads.json, chunks/*.glb) with Godot's runtime glTF importer,
/// turns collision meshes into static trimesh colliders and supports parcel replacement.
/// Node names in a chunk: parcel_&lt;ID&gt;_visual / _collision (buildings), parcel_&lt;ID&gt;_props /
/// _props_collision (fences and trees of that parcel), terrain, terrain_collision, roads, and
/// props / props_collision (items without a parcel, e.g. OSM trees).
/// Independent of any mod. Axes: map x east, y north; Godot x east, y up, z = -north.
/// </summary>
public sealed partial class MapLoader
{
    readonly Dictionary<string, ParcelInfo> _parcels = new();
    readonly List<SpawnPoint> _spawns = new();
    readonly Dictionary<string, Node3D> _chunkRoots = new();
    readonly Dictionary<string, Node3D> _visuals = new();
    readonly Dictionary<string, List<CollisionShape3D>> _shapes = new();
    // Per-parcel fences and trees (parcel_<ID>_props / parcel_<ID>_props_collision), by parcel ID.
    // Kept for the later runtime swap of one parcel's props; ReplaceParcel leaves them alone.
    readonly Dictionary<string, MeshInstance3D> _propsVisuals = new();
    readonly Dictionary<string, List<CollisionShape3D>> _propsShapes = new();
    readonly Dictionary<string, Node3D> _replacements = new();
    readonly List<(Vector3[] faces, Aabb box)> _terrain = new();
    float _meanTerrainY = float.NaN;

    /// <summary>Root of everything the loader created. Add it to the scene tree.</summary>
    public Node3D Root { get; } = new() { Name = "Map" };
    public string MapVersion { get; private set; } = "";
    public string RoadsJson { get; private set; } = "";
    /// <summary>Contents of water.json (empty when the map has no water file); parsed by the shooter (ShooterCore.Water).</summary>
    public string WaterJson { get; private set; } = "";
    /// <summary>Origin of the local frame in S-JTSK (EPSG:5514) metres (east, north), from parcels.json (map 0.4.0+); null for older maps.</summary>
    public (double X, double Y)? OriginEpsg5514 { get; private set; }
    /// <summary>Rule values (fence and tree defaults) from the manifest (map 0.4.0+); null for older maps.</summary>
    public EditDefaults? EditDefaults { get; private set; }
    public LoadStats Stats { get; } = new();
    /// <summary>Per-parcel props visuals and colliders by parcel ID (for the runtime props swap).</summary>
    public IReadOnlyDictionary<string, MeshInstance3D> PropsVisuals => _propsVisuals;
    public IReadOnlyDictionary<string, List<CollisionShape3D>> PropsShapes => _propsShapes;
    public IReadOnlyDictionary<string, ParcelInfo> Parcels => _parcels;
    public IReadOnlyList<SpawnPoint> SpawnPoints => _spawns;
    /// <summary>Point lookup over the parcel polygons (valid after Load).</summary>
    public ParcelIndex ParcelIndex { get; private set; } = new(Array.Empty<ParcelInfo>());

    /// <summary>The parcel containing the ground point (Godot x, z); null when none.</summary>
    public ParcelInfo? ParcelAt(float x, float z) => ParcelIndex.At(x, z);

    /// <summary>Load a map folder. The path is an OS path, not res://. Throws on failure.</summary>
    public void Load(string mapDir)
    {
        var t0 = Time.GetTicksMsec();
        _mapDir = mapDir;
        if (!Directory.Exists(mapDir)) throw new DirectoryNotFoundException(mapDir);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapDir, "manifest.json")));
        MapVersion = manifest.RootElement.GetProperty("map_version").GetString() ?? "";
        if (manifest.RootElement.TryGetProperty("edit_defaults", out var ed)) EditDefaults = EditDefaults.Read(ed);
        ReadParcels(Path.Combine(mapDir, "parcels.json"));
        ParcelIndex = new ParcelIndex(_parcels.Values);
        ReadSpawns(Path.Combine(mapDir, "spawns.json"));
        var roads = Path.Combine(mapDir, "roads.json");
        if (File.Exists(roads)) RoadsJson = File.ReadAllText(roads); // no consumer in v1
        var water = Path.Combine(mapDir, "water.json");
        WaterJson = File.Exists(water) ? File.ReadAllText(water) : "";

        foreach (var c in manifest.RootElement.GetProperty("chunks").EnumerateArray())
        {
            var file = Path.Combine(mapDir, c.GetProperty("file").GetString()!);
            var ix = c.GetProperty("index")[0].GetInt32();
            var iy = c.GetProperty("index")[1].GetInt32();
            LoadChunk(file, new Vector2I(ix, iy));
        }
        if (manifest.RootElement.TryGetProperty("files", out var files)
            && files.TryGetProperty("objects", out var objFile) && objFile.GetString() is { } of)
            LoadObjects(Path.Combine(mapDir, of));
        if (BuildBoundary) BuildTerrainBoundary();
        Stats.LoadSeconds = (Time.GetTicksMsec() - t0) / 1000.0;
    }

    /// <summary>Invisible walls along the rim of the terrain (on by default). The exported terrain is a bounded, ragged patch while spawns, parcels and roads reach past it, so without a rim players walk off the edge and fall into the void.</summary>
    public bool BuildBoundary { get; set; } = true;
    /// <summary>Number of rim wall boxes built (0 without terrain).</summary>
    public int BoundaryWalls { get; private set; }
    const float RimCell = 1f, RimHeight = 24f;

    void BuildTerrainBoundary()
    {
        if (_terrain.Count == 0) return;
        var box = _terrain[0].box;
        foreach (var t in _terrain) box = box.Merge(t.box);
        int x0 = (int)Mathf.Floor(box.Position.X / RimCell) - 1, x1 = (int)Mathf.Ceil(box.End.X / RimCell) + 1;
        int z0 = (int)Mathf.Floor(box.Position.Z / RimCell) - 1, z1 = (int)Mathf.Ceil(box.End.Z / RimCell) + 1;
        int w = x1 - x0 + 1, h = z1 - z0 + 1;
        var ground = new float[w * h];
        for (var i = 0; i < w; i++)
            for (var j = 0; j < h; j++)
                ground[i * h + j] = GroundHeight((x0 + i + 0.5f) * RimCell, (z0 + j + 0.5f) * RimCell);
        bool Covered(int i, int j) => i >= 0 && j >= 0 && i < w && j < h && !float.IsNaN(ground[i * h + j]);

        var body = new StaticBody3D { Name = "TerrainRim" };
        var n = 0;
        // an empty cell touching a covered one becomes a wall cell; runs of them along an axis merge into one box
        var wall = new bool[w * h];
        var wallY = new float[w * h];
        for (var i = 0; i < w; i++)
            for (var j = 0; j < h; j++)
            {
                if (Covered(i, j)) continue;
                var gy = float.NaN;
                for (var di = -1; di <= 1 && float.IsNaN(gy); di++)
                    for (var dj = -1; dj <= 1; dj++)
                        if ((di != 0 || dj != 0) && Covered(i + di, j + dj)) { gy = ground[(i + di) * h + j + dj]; break; }
                if (float.IsNaN(gy)) continue;
                wall[i * h + j] = true; wallY[i * h + j] = gy;
            }
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                if (!wall[i * h + j]) continue;
                var e = i;
                while (e + 1 < w && wall[(e + 1) * h + j]) e++;
                var yMin = float.MaxValue;
                for (var k = i; k <= e; k++) { yMin = Mathf.Min(yMin, wallY[k * h + j]); wall[k * h + j] = false; }
                var len = (e - i + 1) * RimCell;
                body.AddChild(new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = new Vector3(len, RimHeight + 20f, RimCell) },
                    Position = new Vector3((x0 + i) * RimCell + len / 2, yMin - 20f + (RimHeight + 20f) / 2, (z0 + j + 0.5f) * RimCell),
                });
                n++;
            }
        Root.AddChild(body);
        BoundaryWalls = n;
        // a spawn within the capsule's reach of the rim would start inside a wall: drop those
        var dropped = _spawns.RemoveAll(sp =>
        {
            for (var k = 0; k < 8; k++)
            {
                var a = k * Mathf.Tau / 8;
                if (float.IsNaN(GroundHeight(sp.Position.X + Mathf.Cos(a) * 2f, sp.Position.Z + Mathf.Sin(a) * 2f))) return true;
            }
            return false;
        });
        if (dropped > 0) GD.Print($"map: dropped {dropped} spawn points within 2 m of the terrain rim");
    }

    void ReadParcels(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("origin_epsg5514", out var o) && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == 2)
            OriginEpsg5514 = (o[0].GetDouble(), o[1].GetDouble());
        foreach (var p in doc.RootElement.GetProperty("parcels").EnumerateArray())
        {
            var id = p.GetProperty("id").GetString()!;
            var a = p.GetProperty("anchor");
            var ch = p.GetProperty("chunk");
            var rings = new List<Vector2[]>();
            if (p.TryGetProperty("polygon", out var poly) && poly.TryGetProperty("coordinates", out var coords))
                foreach (var r in coords.EnumerateArray())
                    rings.Add(r.EnumerateArray()
                        .Select(c => new Vector2(c[0].GetSingle(), -c[1].GetSingle())).ToArray());
            var bounds = new Rect2();
            if (rings.Count > 0)
            {
                bounds = new Rect2(rings[0][0], Vector2.Zero);
                foreach (var ring in rings) foreach (var v in ring) bounds = bounds.Expand(v);
            }
            var area = 0f;
            for (var i = 0; i < rings.Count; i++) area += (i == 0 ? 1 : -1) * ParcelInfo.RingArea(rings[i]);
            _parcels[id] = new ParcelInfo
            {
                Id = id,
                Kind = p.GetProperty("kind").GetString() ?? "",
                CadastralRef = p.GetProperty("cadastral_ref").GetString() ?? "",
                Chunk = new Vector2I(ch[0].GetInt32(), ch[1].GetInt32()),
                AnchorMap = new Vector2(a[0].GetSingle(), a[1].GetSingle()),
                Rings = rings,
                Bounds = bounds,
                AreaM2 = Mathf.Max(0f, area),
                BuildingIds = p.GetProperty("buildings").EnumerateArray()
                    .Select(b => b.ToString()).ToList(),
            };
        }
    }

    void ReadSpawns(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var s in doc.RootElement.GetProperty("spawns").EnumerateArray())
        {
            var ch = s.GetProperty("chunk");
            _spawns.Add(new SpawnPoint
            {
                Id = s.GetProperty("id").GetString()!,
                Kind = s.GetProperty("kind").GetString() ?? "",
                Parcel = s.TryGetProperty("parcel", out var pp) ? pp.GetString() ?? "" : "",
                Highway = s.TryGetProperty("highway", out var hh) ? hh.GetString() ?? "" : "",
                Chunk = new Vector2I(ch[0].GetInt32(), ch[1].GetInt32()),
                Position = new Vector3(s.GetProperty("x").GetSingle(), s.GetProperty("z").GetSingle(),
                    -s.GetProperty("y").GetSingle()),
            });
        }
    }

    void LoadChunk(string glbPath, Vector2I index)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        var err = doc.AppendFromFile(glbPath, state);
        if (err != Error.Ok) throw new InvalidDataException($"glTF load failed ({err}): {glbPath}");
        var scene = doc.GenerateScene(state) as Node3D
            ?? throw new InvalidDataException($"glTF has no Node3D root: {glbPath}");

        var key = $"chunk_{index.X}_{index.Y}";
        var chunk = new Node3D { Name = key };
        chunk.AddChild(scene);
        Root.AddChild(chunk);
        _chunkRoots[key] = chunk;
        Stats.ChunksLoaded++;

        foreach (var mi in Descendants(scene).OfType<MeshInstance3D>().ToList())
        {
            var name = mi.Name.ToString();
            if (mi.Mesh == null) continue;
            if (name.EndsWith("_collision"))
            {
                BuildCollider(mi, name);
                continue;
            }
            FixMaterials(mi);
            Stats.Triangles += TriangleCount(mi.Mesh);
            if (name.StartsWith("parcel_") && name.EndsWith("_props"))
            {
                _propsVisuals[name["parcel_".Length..^"_props".Length]] = mi;
            }
            else if (RegionId(name) is { } regionId)
            {
                RegisterBakedRegion(regionId, mi);
            }
            else if (name.StartsWith("parcel_") && name.EndsWith("_visual"))
            {
                var id = name["parcel_".Length..^"_visual".Length];
                _visuals[id] = mi;
                if (_parcels.TryGetValue(id, out var info)) info.HasModel = true;
            }
        }
    }

    static IEnumerable<Node> Descendants(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    static int TriangleCount(Mesh m)
    {
        var n = 0;
        for (var s = 0; s < m.GetSurfaceCount(); s++)
        {
            var arrays = m.SurfaceGetArrays(s);
            var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            n += idx.Length > 0 ? idx.Length / 3 : arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;
        }
        return n;
    }

    /// <summary>Flat colour look: vertex colours drive albedo, fully rough, no specular.</summary>
    static void FixMaterials(MeshInstance3D mi)
    {
        mi.MaterialOverride = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            AlbedoColor = Colors.White,
            Roughness = 1f,
            Metallic = 0f,
            MetallicSpecular = 0f,
        };
    }

    /// <summary>Static trimesh collider under the (hidden) collision mesh. Dynamic ones (parcel swaps) are not registered.</summary>
    CollisionShape3D? BuildCollider(MeshInstance3D mi, string name, bool dynamic = false)
    {
        mi.Visible = false;
        var shape = mi.Mesh.CreateTrimeshShape() as ConcavePolygonShape3D;
        if (shape == null) return null;
        shape.BackfaceCollision = true;
        var body = new StaticBody3D { Name = "Body" };
        var cs = new CollisionShape3D { Name = "Shape", Shape = shape };
        body.AddChild(cs);
        mi.AddChild(body);
        Stats.CollidersBuilt++;
        if (dynamic) return cs;

        if (name == "terrain_collision")
        {
            var faces = shape.GetFaces();
            _terrain.Add((faces, FacesAabb(faces)));
            _terrainGrid = null;
        }
        else if (name.StartsWith("parcel_") && name.EndsWith("_props_collision"))
        {
            var id = name["parcel_".Length..^"_props_collision".Length];
            if (!_propsShapes.TryGetValue(id, out var l)) _propsShapes[id] = l = new();
            l.Add(cs);
        }
        else if (name.StartsWith("parcel_") && name.EndsWith("_collision"))
        {
            var id = name["parcel_".Length..^"_collision".Length];
            if (!_shapes.TryGetValue(id, out var l)) _shapes[id] = l = new();
            l.Add(cs);
        }
        return cs;
    }

    // Chunk nodes have identity transforms in the exported files; faces are used as-is.
    static Aabb FacesAabb(Vector3[] faces)
    {
        if (faces.Length == 0) return new Aabb();
        var min = faces[0]; var max = faces[0];
        foreach (var f in faces)
        {
            min = new Vector3(Mathf.Min(min.X, f.X), Mathf.Min(min.Y, f.Y), Mathf.Min(min.Z, f.Z));
            max = new Vector3(Mathf.Max(max.X, f.X), Mathf.Max(max.Y, f.Y), Mathf.Max(max.Z, f.Z));
        }
        return new Aabb(min, max - min);
    }

    const float TerrainCell = 8f;
    Dictionary<long, List<(int mesh, int tri)>>? _terrainGrid;

    static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    // Triangles bucketed by 8 m cells so a height lookup tests a handful of triangles, not the whole mesh.
    void BuildTerrainGrid()
    {
        var grid = new Dictionary<long, List<(int, int)>>();
        for (var m = 0; m < _terrain.Count; m++)
        {
            var faces = _terrain[m].faces;
            for (var t = 0; t * 3 + 2 < faces.Length; t++)
            {
                var a = faces[t * 3]; var b = faces[t * 3 + 1]; var c = faces[t * 3 + 2];
                int x0 = (int)Mathf.Floor(Mathf.Min(a.X, Mathf.Min(b.X, c.X)) / TerrainCell);
                int x1 = (int)Mathf.Floor(Mathf.Max(a.X, Mathf.Max(b.X, c.X)) / TerrainCell);
                int z0 = (int)Mathf.Floor(Mathf.Min(a.Z, Mathf.Min(b.Z, c.Z)) / TerrainCell);
                int z1 = (int)Mathf.Floor(Mathf.Max(a.Z, Mathf.Max(b.Z, c.Z)) / TerrainCell);
                for (var x = x0; x <= x1; x++)
                    for (var z = z0; z <= z1; z++)
                    {
                        var k = CellKey(x, z);
                        if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                        l.Add((m, t));
                    }
            }
        }
        _terrainGrid = grid;
    }

    /// <summary>Terrain height at Godot (x, z), from the terrain collision mesh. NaN if not covered.</summary>
    public float GroundHeight(float x, float z)
    {
        if (_terrain.Count == 0) return float.NaN;
        if (_terrainGrid == null) BuildTerrainGrid();
        if (!_terrainGrid!.TryGetValue(CellKey((int)Mathf.Floor(x / TerrainCell), (int)Mathf.Floor(z / TerrainCell)), out var tris))
            return float.NaN;
        foreach (var (m, t) in tris)
        {
            var faces = _terrain[m].faces;
            var a = faces[t * 3]; var b = faces[t * 3 + 1]; var c = faces[t * 3 + 2];
            var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Mathf.Abs(d) < 1e-9f) continue;
            var l1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
            var l2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
            var l3 = 1f - l1 - l2;
            const float e = -1e-4f;
            if (l1 >= e && l2 >= e && l3 >= e) return l1 * a.Y + l2 * b.Y + l3 * c.Y;
        }
        return float.NaN;
    }

    /// <summary>Mean terrain height over all terrain vertices (0 without terrain).</summary>
    public float MeanTerrainHeight
    {
        get
        {
            if (float.IsNaN(_meanTerrainY))
            {
                var ys = _terrain.SelectMany(t => t.faces).Select(f => f.Y).ToList();
                _meanTerrainY = ys.Count > 0 ? ys.Average() : 0f;
            }
            return _meanTerrainY;
        }
    }

    /// <summary>Terrain height at (x, z), or the mean terrain height where the terrain has no triangle.</summary>
    public float GroundHeightOrMean(float x, float z)
    {
        var h = GroundHeight(x, z);
        return float.IsNaN(h) ? MeanTerrainHeight : h;
    }

    float TerrainOrMean(float x, float z) => GroundHeightOrMean(x, z);

    /// <summary>Parcel anchor placed on the terrain, in Godot axes.</summary>
    public Vector3 AnchorOnTerrain(string parcelId)
    {
        var a = _parcels[parcelId].AnchorXZ;
        return new Vector3(a.X, TerrainOrMean(a.X, a.Z), a.Z);
    }

    public bool IsReplaced(string parcelId) => _replacements.ContainsKey(parcelId);

    /// <summary>
    /// Hide the parcel's visual node, disable its collision and instantiate the scene at the
    /// parcel anchor on the terrain. Returns the instance. A parcel replaced earlier is restored first.
    /// </summary>
    public Node3D ReplaceParcel(string parcelId, PackedScene scene)
    {
        if (!_parcels.TryGetValue(parcelId, out var info))
            throw new KeyNotFoundException($"unknown parcel {parcelId}");
        RestoreParcel(parcelId);
        if (_visuals.TryGetValue(parcelId, out var vis)) vis.Visible = false;
        if (_shapes.TryGetValue(parcelId, out var shapes))
            foreach (var s in shapes) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);

        var inst = scene.Instantiate<Node3D>();
        inst.Name = $"replacement_{parcelId}";
        var chunkKey = $"chunk_{info.Chunk.X}_{info.Chunk.Y}";
        var parent = _chunkRoots.TryGetValue(chunkKey, out var cr) ? cr : Root;
        parent.AddChild(inst);
        inst.GlobalPosition = AnchorOnTerrain(parcelId);
        _replacements[parcelId] = inst;
        return inst;
    }

    /// <summary>Undo ReplaceParcel: free the replacement, show the visual, re-enable collision.</summary>
    public void RestoreParcel(string parcelId)
    {
        if (_replacements.Remove(parcelId, out var inst)) inst.QueueFree();
        if (_visuals.TryGetValue(parcelId, out var vis)) vis.Visible = true;
        if (_shapes.TryGetValue(parcelId, out var shapes))
            foreach (var s in shapes) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, false);
    }

    /// <summary>Id of the parcel nearest (anchor distance in the ground plane) to p.</summary>
    public string? NearestParcel(Vector3 p, bool modelsOnly = false)
    {
        string? best = null; var bd = float.MaxValue;
        foreach (var pa in _parcels.Values)
        {
            if (modelsOnly && !pa.HasModel) continue;
            var d = new Vector2(pa.AnchorMap.X - p.X, -pa.AnchorMap.Y - p.Z).LengthSquared();
            if (d < bd) { bd = d; best = pa.Id; }
        }
        return best;
    }
}
