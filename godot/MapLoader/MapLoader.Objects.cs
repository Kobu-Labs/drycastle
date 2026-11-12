using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader;

/// <summary>A placed object (objects.json entry or parcel-run fragment object). Map metres, x east, y north.</summary>
public sealed class PlacedObject
{
    public string Id { get; init; } = "";
    public string Asset { get; init; } = "";
    public float X { get; init; }
    public float Y { get; init; }
    /// <summary>Elevation in metres; null = terrain height under the object.</summary>
    public float? Z { get; init; }
    public float RotationDeg { get; init; }
    public float Scale { get; init; } = 1f;
    public string? Colour { get; init; }
    /// <summary>Parcel containing the object (null: outside every parcel).</summary>
    public string? Parcel { get; init; }
    /// <summary>Parcel entry that lists the object; the removal group. Falls back to Parcel.</summary>
    public string? Owner { get; init; }
    public string Group => Owner ?? Parcel ?? NoParcelGroup;
    public const string NoParcelGroup = "_none";
}

/// <summary>An entry of assets/manifest.json: id ("pack:..." / "prim:..."), category, display name and size in metres (glTF axes).</summary>
public sealed record AssetInfo(string Id, string Category, string Name, Vector3 Size);

/// <summary>Result of <see cref="MapLoader.ApplyFragment"/>.</summary>
public sealed record FragmentResult(IReadOnlyList<string> Replaced, int Objects, int PropsNodes);

// Placed objects, the dynamic per-parcel props swap (parcel-run fragments) and their bookkeeping.
public sealed partial class MapLoader
{
    sealed record AssetEntry(string File, Vector3 Size, string Category, string Name, List<(string kind, int tier, Vector3 local)> Seats, List<Vector3> Lights,
        List<Drycastle.ShooterCore.AssetSolid> Solids);

    string _mapDir = "";
    Node3D? _objectsRoot;
    // object id -> node; group (owner parcel) -> container and ids
    readonly Dictionary<string, Node3D> _objectNodes = new();
    readonly Dictionary<string, string> _objectGroup = new();
    readonly Dictionary<string, Node3D> _groupNodes = new();
    readonly Dictionary<string, List<PlacedObject>> _bakedObjects = new();
    // parcel id -> container of the dynamic (regenerated) props nodes of that parcel
    readonly Dictionary<string, Node3D> _dynamicProps = new();
    readonly Dictionary<string, Node3D?> _assetTemplates = new();
    Dictionary<string, AssetEntry>? _assetManifest;
    string? _assetsDir;

    /// <summary>
    /// Directory holding assets/manifest.json and the packs. Set before <see cref="Load"/>; default:
    /// env DRYCASTLE_ASSETS, else an "assets" folder found by walking up from the map folder.
    /// </summary>
    public string? AssetsDir
    {
        get => _assetsDir ?? DefaultAssetsDir();
        set { _assetsDir = value; _assetManifest = null; _assetTemplates.Clear(); }
    }

    /// <summary>Every asset of the manifest in file order (empty when the manifest is missing).</summary>
    public IReadOnlyList<AssetInfo> AssetCatalog
    {
        get
        {
            if (_assetManifest == null) ReadAssetManifest();
            return _assetManifest!.Select(kv => new AssetInfo(kv.Key, kv.Value.Category, kv.Value.Name, kv.Value.Size)).ToList();
        }
    }

    /// <summary>A fresh copy of an asset's scene (not in the tree; the caller frees it); null when unknown or unloadable.</summary>
    public Node3D? InstantiateAsset(string asset)
        => GetAssetTemplate(asset, out _) is { } t ? (Node3D)t.Duplicate() : null;

    /// <summary>Free the cached asset templates (they are not in the tree). Call when the map is torn down.</summary>
    public void FreeCaches()
    {
        foreach (var t in _assetTemplates.Values)
            if (t != null && GodotObject.IsInstanceValid(t)) t.Free();
        _assetTemplates.Clear();
        FreeClutter();
    }

    /// <summary>Messages the loader would print as warnings (also written with GD.PushWarning).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Node "Objects": one container "objects_&lt;parcel&gt;" per parcel group, one "object_&lt;id&gt;" each.</summary>
    public Node3D? ObjectsRoot => _objectsRoot;
    public IReadOnlyDictionary<string, Node3D> ObjectNodes => _objectNodes;
    /// <summary>Dynamic props containers ("dyn_props_&lt;ID&gt;") of parcels swapped by ApplyFragment.</summary>
    public IReadOnlyDictionary<string, Node3D> DynamicProps => _dynamicProps;
    public bool IsPropsSwapped(string parcelId) => _dynamicProps.ContainsKey(parcelId) || _swapped.Contains(parcelId);
    readonly HashSet<string> _swapped = new();

    string? DefaultAssetsDir()
    {
        var env = System.Environment.GetEnvironmentVariable("DRYCASTLE_ASSETS");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(Path.GetFullPath(_mapDir == "" ? "." : _mapDir)); d != null; d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "assets");
            if (File.Exists(Path.Combine(cand, "manifest.json"))) return cand;
        }
        return null;
    }

    void Warn(string message)
    {
        Warnings.Add(message);
        GD.PushWarning(message);
        GD.Print($"warning: {message}");
    }

    // ---- objects.json ----

    static List<PlacedObject> ParseObjects(JsonElement array)
    {
        var list = new List<PlacedObject>();
        foreach (var o in array.EnumerateArray())
        {
            var pos = o.GetProperty("position");
            list.Add(new PlacedObject
            {
                Id = o.GetProperty("id").GetString()!,
                Asset = o.GetProperty("asset").GetString() ?? "",
                X = pos[0].GetSingle(),
                Y = pos[1].GetSingle(),
                Z = o.TryGetProperty("z", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetSingle() : null,
                RotationDeg = o.TryGetProperty("rotation_deg", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : 0f,
                Scale = o.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetSingle() : 1f,
                Colour = o.TryGetProperty("colour", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                Parcel = o.TryGetProperty("parcel", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null,
                Owner = o.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.String ? ow.GetString() : null,
            });
        }
        return list;
    }

    void LoadObjects(string path) { foreach (var _ in LoadObjectSteps(path)) { } }

    /// <summary>Places the baked objects, yielding the fraction done after each one (each may load a model file).</summary>
    IEnumerable<float> LoadObjectSteps(string path)
    {
        List<PlacedObject> list;
        List<CableSpec> cables;
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            list = ParseObjects(doc.RootElement.GetProperty("objects"));
            cables = ParseCables(doc.RootElement);
        }
        var n = 0;
        foreach (var o in list)
        {
            if (!_bakedObjects.TryGetValue(o.Group, out var l)) _bakedObjects[o.Group] = l = new();
            l.Add(o);
            AddObject(o);
            yield return (float)++n / list.Count;
        }
        SetCables(cables); // after the poles they hang from
    }

    Node3D ObjectsContainer()
    {
        if (_objectsRoot == null)
        {
            _objectsRoot = new Node3D { Name = "Objects" };
            Root.AddChild(_objectsRoot);
        }
        return _objectsRoot;
    }

    static string SafeName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Instance one object (replacing a node of the same id).</summary>
    void AddObject(PlacedObject o)
    {
        GeometryChanged();
        RemoveObject(o.Id);
        var group = o.Group;
        if (!_groupNodes.TryGetValue(group, out var container))
        {
            container = new Node3D { Name = $"objects_{SafeName(group)}" };
            ObjectsContainer().AddChild(container);
            _groupNodes[group] = container;
        }
        var node = new Node3D { Name = $"object_{SafeName(o.Id)}" };
        // a headless load (CollisionOnly) builds the colliders alone: no model is loaded for the object
        if (!CollisionOnly) node.AddChild(CreateObjectModel(o.Asset, o.Colour, out _));
        AddObjectColliders(node, o.Asset);
        container.AddChild(node);
        var y = o.Z ?? TerrainOrMean(o.X, -o.Y);
        node.Position = new Vector3(o.X, y, -o.Y);
        node.RotationDegrees = new Vector3(0, o.RotationDeg, 0);
        node.Scale = Vector3.One * (o.Scale > 0 ? o.Scale : 1f);
        _objectNodes[o.Id] = node;
        _objectGroup[o.Id] = group;
        _objectAssets[o.Id] = o.Asset;
    }

    /// <summary>
    /// A fresh instance of an asset (a copy of its cached template, or a magenta box of the manifest size when it
    /// cannot be loaded), with the optional "#rrggbb" colour override applied. Not in the tree; the caller owns it.
    /// </summary>
    public Node3D CreateObjectModel(string asset, string? colour, out Vector3 size)
    {
        var template = GetAssetTemplate(asset, out var sz);
        size = sz ?? Vector3.One;
        Node3D model;
        if (template != null) model = (Node3D)template.Duplicate();
        else
            model = new MeshInstance3D
            {
                Name = "Placeholder",
                Mesh = new BoxMesh { Size = size },
                Position = new Vector3(0, size.Y / 2f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0f, 1f, 1f), Roughness = 1f },
            };
        if (!string.IsNullOrEmpty(colour) && Color.HtmlIsValid(colour))
        {
            var mat = new StandardMaterial3D { AlbedoColor = Color.FromHtml(colour), Roughness = 1f, MetallicSpecular = 0f };
            foreach (var mi in AllMeshes(model)) mi.MaterialOverride = mat;
        }
        return model;
    }

    /// <summary>
    /// Instant path for placed objects (no pipeline run): drop every object listed under the given parcels
    /// (<see cref="PlacedObject.Owner"/>) and instance <paramref name="objects"/> in their place, with the same
    /// node names and ownership as <see cref="ApplyFragment"/>, so a later fragment swap stays consistent. An object
    /// whose id exists under another owner is moved (its node is replaced), never duplicated.
    /// </summary>
    public int ApplyObjects(IEnumerable<string> parcels, IEnumerable<PlacedObject> objects)
    {
        foreach (var id in parcels) RemoveGroupObjects(id);
        var n = 0;
        foreach (var o in objects) { AddObject(o); n++; }
        if (_cables.Count > 0) RebuildCables(); // poles may have moved, come or gone
        return n;
    }

    static IEnumerable<MeshInstance3D> AllMeshes(Node n)
    {
        if (n is MeshInstance3D m) yield return m;
        foreach (var d in Descendants(n)) if (d is MeshInstance3D mi) yield return mi;
    }

    bool RemoveObject(string id)
    {
        GeometryChanged();
        if (!_objectNodes.Remove(id, out var node)) return false;
        var group = _objectGroup[id];
        _objectGroup.Remove(id);
        _objectAssets.Remove(id);
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
        DropEmptyGroup(group);
        return true;
    }

    void DropEmptyGroup(string group)
    {
        if (_groupNodes.TryGetValue(group, out var c) && c.GetChildCount() == 0)
        {
            _groupNodes.Remove(group);
            c.GetParent()?.RemoveChild(c);
            c.QueueFree();
        }
    }

    void RemoveGroupObjects(string group)
    {
        foreach (var id in _objectGroup.Where(kv => kv.Value == group).Select(kv => kv.Key).ToList())
            RemoveObject(id);
    }

    // ---- assets ----

    void ReadAssetManifest()
    {
        _assetManifest = new();
        var dir = AssetsDir;
        var path = dir == null ? null : Path.Combine(dir, "manifest.json");
        if (path == null || !File.Exists(path))
        {
            Warn($"assets manifest not found (assets dir: {dir ?? "none"}); pack: objects use placeholders");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var file = a.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
            var sz = a.GetProperty("size_m");
            var id = a.GetProperty("id").GetString()!;
            var cat = a.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "other";
            var name = a.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : id;
            _assetManifest[id] = new AssetEntry(file, new Vector3(sz[0].GetSingle(), sz[1].GetSingle(), sz[2].GetSingle()), cat, name, ParseSeats(a), ParseLights(a),
                Drycastle.ShooterCore.ObjectColliders.ParseSolids(a));
        }
    }

    /// <summary>Template scene of an asset (cached, never in the tree); null = unknown or unloadable (placeholder).</summary>
    Node3D? GetAssetTemplate(string asset, out Vector3? size)
    {
        size = null;
        if (_assetManifest == null) ReadAssetManifest();
        if (_assetManifest!.TryGetValue(asset, out var entry)) size = entry.Size;
        if (_assetTemplates.TryGetValue(asset, out var cached)) return cached;

        Node3D? scene = null;
        string? path = null;
        if (asset.StartsWith("prim:"))
            path = Path.Combine(_mapDir, "prims", asset["prim:".Length..] + ".glb");
        else if (asset.StartsWith("pack:") && entry != null && entry.File != "" && AssetsDir != null)
            path = Path.Combine(AssetsDir, entry.File);
        if (path == null)
            Warn($"object asset '{asset}' is unknown (not in the assets manifest); using a placeholder box");
        else if (!File.Exists(path))
            Warn($"object asset '{asset}': file not found: {path}; using a placeholder box");
        else
        {
            try
            {
                scene = LoadGltf(path);
                if (scene != null && asset.StartsWith("prim:"))
                    foreach (var mi in AllMeshes(scene)) FixMaterials(mi);
            }
            catch (Exception e)
            {
                Warn($"object asset '{asset}': {e.Message}; using a placeholder box");
            }
        }
        _assetTemplates[asset] = scene;
        return scene;
    }

    static Node3D? LoadGltf(string path)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        var err = doc.AppendFromFile(path, state);
        if (err != Error.Ok) throw new InvalidDataException($"glTF load failed ({err}): {path}");
        return doc.GenerateScene(state) as Node3D;
    }

    // ---- parcel-run fragments ----

    /// <summary>
    /// Apply a parcel-run fragment (JSON written with --out, and --glb for the props): for every
    /// parcel in "replaces" hide the baked props visual and collider, drop the previous dynamic
    /// props nodes and objects of that parcel, add the fragment's GLB nodes (with colliders) and
    /// its objects. Safe to repeat; nothing is changed if the fragment or GLB cannot be read.
    /// </summary>
    public FragmentResult ApplyFragment(string fragmentJsonPath)
    {
        string glbPath = "";
        List<string> replaces;
        List<PlacedObject> objects;
        using (var doc = JsonDocument.Parse(File.ReadAllText(fragmentJsonPath)))
        {
            var r = doc.RootElement;
            var arr = r.TryGetProperty("replaces", out var rep) ? rep : r.GetProperty("parcels");
            replaces = arr.EnumerateArray().Select(e => e.GetString()!).ToList();
            glbPath = r.TryGetProperty("glb", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()! : "";
            objects = ParseObjects(r.GetProperty("objects"));
        }
        // load the GLB before touching anything so a bad file leaves the map as it was
        Node3D? scene = null;
        if (glbPath != "") scene = LoadGltf(glbPath);

        GeometryChanged();
        var set = replaces.ToHashSet();
        var propsNodes = 0;
        foreach (var id in replaces)
        {
            RemoveDynamic(id);
            HideBaked(id);
            _swapped.Add(id);
            RemoveGroupObjects(id);
        }
        if (scene != null)
        {
            var byParcel = new Dictionary<string, List<(MeshInstance3D mi, string name)>>();
            foreach (var mi in AllMeshes(scene).ToList())
            {
                var name = mi.Name.ToString();
                if (mi.Mesh == null || !name.StartsWith("parcel_")) continue;
                string? id = name.EndsWith("_props_collision") ? name["parcel_".Length..^"_props_collision".Length]
                    : PropsParcelId(SplitTexNode(name).Base);  // the flat props node or a textured sibling
                if (id == null) continue;
                if (!set.Contains(id)) { Warn($"fragment GLB has props for {id}, which is not in 'replaces'; ignored"); continue; }
                if (!byParcel.TryGetValue(id, out var l)) byParcel[id] = l = new();
                l.Add((mi, name));
            }
            foreach (var (id, meshes) in byParcel)
            {
                var container = new Node3D { Name = $"dyn_props_{id}" };
                Root.AddChild(container);
                _dynamicProps[id] = container;
                foreach (var (mi, name) in meshes)
                {
                    mi.GetParent().RemoveChild(mi);
                    mi.Owner = null;
                    container.AddChild(mi);
                    mi.Transform = Transform3D.Identity;
                    if (name.EndsWith("_collision")) BuildCollider(mi, name, dynamic: true);
                    else ApplyMaterial(mi, name);
                    propsNodes++;
                }
            }
            scene.Free();
        }
        foreach (var o in objects) AddObject(o);
        if (_cables.Count > 0) RebuildCables();
        return new FragmentResult(replaces, objects.Count, propsNodes);
    }

    void RemoveDynamic(string id)
    {
        GeometryChanged();
        if (!_dynamicProps.Remove(id, out var c)) return;
        c.GetParent()?.RemoveChild(c);
        c.QueueFree();
    }

    void HideBaked(string id)
    {
        GeometryChanged();
        if (_propsVisuals.TryGetValue(id, out var v)) v.Visible = false;
        if (_propsTexVisuals.TryGetValue(id, out var tex)) foreach (var t in tex) t.Visible = false;
        if (_propsShapes.TryGetValue(id, out var shapes))
            foreach (var s in shapes) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
    }

    /// <summary>Undo ApplyFragment for one parcel: drop its dynamic props and objects, show the baked props and objects.</summary>
    public void RevertParcel(string id)
    {
        GeometryChanged();
        RemoveDynamic(id);
        _swapped.Remove(id);
        if (_propsVisuals.TryGetValue(id, out var v)) v.Visible = true;
        if (_propsTexVisuals.TryGetValue(id, out var tex)) foreach (var t in tex) t.Visible = true;
        if (_propsShapes.TryGetValue(id, out var shapes))
            foreach (var s in shapes) s.SetDeferred(CollisionShape3D.PropertyName.Disabled, false);
        RemoveGroupObjects(id);
        if (_bakedObjects.TryGetValue(id, out var baked))
            foreach (var o in baked) AddObject(o);
        if (_cables.Count > 0) RebuildCables();
    }
}
