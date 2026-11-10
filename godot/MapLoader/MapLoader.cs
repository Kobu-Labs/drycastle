using System.Text.Json;
using Godot;

namespace Suchohrad.MapLoader;

/// <summary>
/// Shared runtime loader for the Suchohrad base map. Loads a map folder (manifest.json,
/// parcels.json, spawns.json, roads.json, chunks/*.glb) with Godot's runtime glTF importer,
/// turns collision meshes into static trimesh colliders and supports parcel replacement.
/// Independent of any mod. Axes: map x east, y north; Godot x east, y up, z = -north.
/// </summary>
public sealed class MapLoader
{
    readonly Dictionary<string, ParcelInfo> _parcels = new();
    readonly List<SpawnPoint> _spawns = new();
    readonly Dictionary<string, Node3D> _chunkRoots = new();
    readonly Dictionary<string, Node3D> _visuals = new();
    readonly Dictionary<string, List<CollisionShape3D>> _shapes = new();
    readonly Dictionary<string, Node3D> _replacements = new();
    readonly List<(Vector3[] faces, Aabb box)> _terrain = new();
    float _meanTerrainY = float.NaN;

    /// <summary>Root of everything the loader created. Add it to the scene tree.</summary>
    public Node3D Root { get; } = new() { Name = "Map" };
    public string MapVersion { get; private set; } = "";
    public string RoadsJson { get; private set; } = "";
    public LoadStats Stats { get; } = new();
    public IReadOnlyDictionary<string, ParcelInfo> Parcels => _parcels;
    public IReadOnlyList<SpawnPoint> SpawnPoints => _spawns;

    /// <summary>Load a map folder. The path is an OS path, not res://. Throws on failure.</summary>
    public void Load(string mapDir)
    {
        var t0 = Time.GetTicksMsec();
        if (!Directory.Exists(mapDir)) throw new DirectoryNotFoundException(mapDir);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapDir, "manifest.json")));
        MapVersion = manifest.RootElement.GetProperty("map_version").GetString() ?? "";
        ReadParcels(Path.Combine(mapDir, "parcels.json"));
        ReadSpawns(Path.Combine(mapDir, "spawns.json"));
        var roads = Path.Combine(mapDir, "roads.json");
        if (File.Exists(roads)) RoadsJson = File.ReadAllText(roads); // no consumer in v1

        foreach (var c in manifest.RootElement.GetProperty("chunks").EnumerateArray())
        {
            var file = Path.Combine(mapDir, c.GetProperty("file").GetString()!);
            var ix = c.GetProperty("index")[0].GetInt32();
            var iy = c.GetProperty("index")[1].GetInt32();
            LoadChunk(file, new Vector2I(ix, iy));
        }
        Stats.LoadSeconds = (Time.GetTicksMsec() - t0) / 1000.0;
    }

    void ReadParcels(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.GetProperty("parcels").EnumerateArray())
        {
            var id = p.GetProperty("id").GetString()!;
            var a = p.GetProperty("anchor");
            var ch = p.GetProperty("chunk");
            _parcels[id] = new ParcelInfo
            {
                Id = id,
                Kind = p.GetProperty("kind").GetString() ?? "",
                CadastralRef = p.GetProperty("cadastral_ref").GetString() ?? "",
                Chunk = new Vector2I(ch[0].GetInt32(), ch[1].GetInt32()),
                AnchorMap = new Vector2(a[0].GetSingle(), a[1].GetSingle()),
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
            if (name.StartsWith("parcel_") && name.EndsWith("_visual"))
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

    void BuildCollider(MeshInstance3D mi, string name)
    {
        mi.Visible = false;
        var shape = mi.Mesh.CreateTrimeshShape() as ConcavePolygonShape3D;
        if (shape == null) return;
        shape.BackfaceCollision = true;
        var body = new StaticBody3D { Name = "Body" };
        var cs = new CollisionShape3D { Name = "Shape", Shape = shape };
        body.AddChild(cs);
        mi.AddChild(body);
        Stats.CollidersBuilt++;

        if (name == "terrain_collision")
        {
            var faces = shape.GetFaces();
            _terrain.Add((faces, FacesAabb(faces)));
        }
        else if (name.StartsWith("parcel_"))
        {
            var id = name["parcel_".Length..^"_collision".Length];
            if (!_shapes.TryGetValue(id, out var l)) _shapes[id] = l = new();
            l.Add(cs);
        }
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

    /// <summary>Terrain height at Godot (x, z), from the terrain collision mesh. NaN if not covered.</summary>
    public float GroundHeight(float x, float z)
    {
        foreach (var (faces, box) in _terrain)
        {
            if (x < box.Position.X || x > box.End.X || z < box.Position.Z || z > box.End.Z) continue;
            for (var i = 0; i + 2 < faces.Length; i += 3)
            {
                var a = faces[i]; var b = faces[i + 1]; var c = faces[i + 2];
                var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                if (Mathf.Abs(d) < 1e-9f) continue;
                var l1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
                var l2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
                var l3 = 1f - l1 - l2;
                const float e = -1e-4f;
                if (l1 >= e && l2 >= e && l3 >= e) return l1 * a.Y + l2 * b.Y + l3 * c.Y;
            }
        }
        return float.NaN;
    }

    float TerrainOrMean(float x, float z)
    {
        var h = GroundHeight(x, z);
        if (!float.IsNaN(h)) return h;
        if (float.IsNaN(_meanTerrainY))
        {
            var ys = _terrain.SelectMany(t => t.faces).Select(f => f.Y).ToList();
            _meanTerrainY = ys.Count > 0 ? ys.Average() : 0f;
        }
        return _meanTerrainY;
    }

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
