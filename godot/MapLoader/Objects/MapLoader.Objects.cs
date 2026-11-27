using System.Text.Json;
using Godot;
using Drycastle.MapLoader.Objects;

using Drycastle.MapLoader.Core;

namespace Drycastle.MapLoader;

// Placed objects, the dynamic per-parcel props swap (parcel-run fragments) and their bookkeeping.
public sealed partial class MapLoader
{
    /// <summary><c>Root</c> is the folder <c>File</c> is relative to (null = the asset library). <c>Prefab</c>: a model of a private pack that brings its own
    /// collision bodies and wears the pack's materials (see MapLoader.Prefabs.cs).</summary>
    sealed record AssetEntry(string File, string? Root, bool Prefab, Vector3 Size, string Category, string Name, List<(string kind, int tier, Vector3 local)> Seats, List<Vector3> Lights,
        List<Drycastle.ShooterCore.Colliders.AssetSolid> Solids, List<Vector3> Plates);

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

    /// <summary>How a placed object stands in map space (Godot axes): its origin, its turn about the vertical axis (degrees, Godot's Y rotation) and its scale.</summary>
    public readonly record struct ObjectPose(string Id, Vector3 Position, float YawDeg, float Scale);

    /// <summary>
    /// The first placed object of an asset (the lowest object id: by the number after the prefix, so "o-4859" comes before "o-10000"; ordinal where there is none) and where it stands now; null when none is placed. From the
    /// object's own node transform, which comes from objects.json (position, rotation, scale, terrain height), never from the model.
    /// </summary>
    public ObjectPose? FirstObjectOfAsset(string asset)
    {
        foreach (var (id, node) in _objectNodes.OrderBy(kv => ObjectNumber(kv.Key)).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!GodotObject.IsInstanceValid(node) || !_objectAssets.TryGetValue(id, out var a) || a != asset) continue;
            var x = node.Basis.X; // the turn from the axis, not from Euler angles, which fold a turn past 90 degrees into the other axes
            return new ObjectPose(id, node.Position, Mathf.RadToDeg(MathF.Atan2(-x.Z, x.X)), x.Length());
        }
        return null;
    }

    /// <summary>The number after an object id's prefix ("o-4859" is 4859); an id without one sorts last.</summary>
    static long ObjectNumber(string id) => long.TryParse(id.AsSpan(id.LastIndexOf('-') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue;

    /// <summary>
    /// Opens parts of a placed object's model: every node of the object named in <paramref name="names"/> (a door of a prefab) is
    /// hidden and its collision bodies stop colliding. Returns how many nodes it found: none for an object that has no such part
    /// (a placeholder box, a headless load without the model).
    /// </summary>
    public int OpenObjectParts(string objectId, IEnumerable<string> names)
    {
        if (!_objectNodes.TryGetValue(objectId, out var root) || !GodotObject.IsInstanceValid(root)) return 0;
        GeometryChanged();
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var found = 0;
        foreach (var part in Descendants(root).Where(d => wanted.Contains(d.Name.ToString())).ToList())
        {
            found++;
            if (part is Node3D n3) n3.Visible = false;
            foreach (var body in new[] { part }.Concat(Descendants(part)).OfType<CollisionObject3D>())
            {
                body.CollisionLayer = 0;
                body.CollisionMask = 0;
            }
        }
        return found;
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
        FreeCrops();
        FreePackTrees();
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
                SizeM = PlacedObject.ReadSizeM(o),
                Model = o.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String ? mo.GetString() : null,
                Colour = o.TryGetProperty("colour", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                Solid = !(o.TryGetProperty("solid", out var so) && so.ValueKind == JsonValueKind.False),
                Parcel = o.TryGetProperty("parcel", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null,
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
        var tp = LoadTimers.Start();
        if (_objectsAhead is { } ahead) { (list, cables) = ahead; _objectsAhead = null; }
        else
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                list = ParseObjects(doc.RootElement.GetProperty("objects"));
                cables = ParseCables(doc.RootElement);
            }
        LoadTimers.Lap("objects.parse", tp);
        // the copies of the templates are made here, not on a worker: copying them on a worker was measured 6 to 20 times slower
        var n = 0;
        foreach (var o in list)
        {
            if (!_bakedObjects.TryGetValue(o.Group, out var l)) _bakedObjects[o.Group] = l = new();
            l.Add(o);
            var t = LoadTimers.Start();
            AddObject(o);
            if (LoadTimers.Enabled && LoadTimers.Ms(LoadTimers.Start() - t) is var ms and > 50) GD.Print($"loadobject {ms:F0} ms {o.Asset}"); // the join-time report: which object holds up a frame
            yield return (float)++n / list.Count;
        }
        var tcb = LoadTimers.Start(); SetCables(cables); LoadTimers.Lap("objects.cables", tcb); // after the poles they hang from
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
        var scale = ObjectScale(o);
        if (!CollisionOnly)
        {
            if (OwnModel(o) is { } own)
            {
                own.Scale = new Vector3(1f / scale.X, 1f / scale.Y, 1f / scale.Z); // its real size: undo the node's stretch
                node.AddChild(own);
            }
            else node.AddChild(CreateObjectModel(o.Asset, o.Colour, out _));
        }
        AddObjectColliders(node, o.Asset, o.Solid);
        if (!o.Solid) // a prefab or pack model brings its own bodies (client): switch them off like an opened door
            foreach (var body in Descendants(node).OfType<CollisionObject3D>())
            {
                body.CollisionLayer = 0;
                body.CollisionMask = 0;
            }
        container.AddChild(node);
        var y = o.Z ?? TerrainOrMean(o.X, -o.Y);
        node.Position = new Vector3(o.X, y, -o.Y);
        node.RotationDegrees = new Vector3(0, o.RotationDeg, 0);
        node.Scale = scale;
        _objectNodes[o.Id] = node;
        _objectGroup[o.Id] = group;
        _objectAssets[o.Id] = o.Asset;
    }

    /// <summary>The object's own model (<see cref="PlacedObject.Model"/>, a GLB inside the map directory), or null: none named, or it cannot be loaded.</summary>
    Node3D? OwnModel(PlacedObject o)
    {
        if (string.IsNullOrEmpty(o.Model)) return null;
        var path = Path.GetFullPath(Path.Combine(_mapDir, o.Model));
        if (!path.StartsWith(Path.GetFullPath(_mapDir) + Path.DirectorySeparatorChar) || !File.Exists(path)) return null;
        try
        {
            var scene = LoadGltf(path);
            if (scene == null) return null;
            foreach (var mi in AllMeshes(scene)) FixMaterials(mi);
            scene.Name = "Model";
            return scene;
        }
        catch (Exception e)
        {
            Warn($"object {o.Id}: model '{o.Model}': {e.Message}; using the asset's model");
            return null;
        }
    }

    /// <summary>
    /// The node scale of an object: uniform <see cref="PlacedObject.Scale"/>, or with a size_m the manifest size stretched to it per axis
    /// (length on glTF x, width on glTF z = map y, height on glTF y); colliders are children of the node and follow.
    /// </summary>
    Vector3 ObjectScale(PlacedObject o)
    {
        if (o.SizeM is { } s)
        {
            if (_assetManifest == null) ReadAssetManifest();
            var m = _assetManifest!.TryGetValue(o.Asset, out var e) ? e.Size : Vector3.One; // unknown asset: the 1 m placeholder box
            if (m.X > 0 && m.Y > 0 && m.Z > 0) return new Vector3(s.X / m.X, s.Z / m.Y, s.Y / m.Z);
        }
        return Vector3.One * (o.Scale > 0 ? o.Scale : 1f);
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
    /// Instant path for placed objects (no pipeline run): drop the objects with the ids in <paramref name="removed"/> and
    /// instance <paramref name="upserts"/>, with the same node names and grouping as <see cref="ApplyFragment"/>, so a
    /// later fragment swap stays consistent. An object whose id is already placed (it moved, turned, changed asset or
    /// colour, or now stands in another parcel) has its node replaced, never duplicated.
    /// </summary>
    public int ApplyObjectChanges(IEnumerable<string> removed, IEnumerable<PlacedObject> upserts)
    {
        foreach (var id in removed) RemoveObject(id);
        var n = 0;
        foreach (var o in upserts) { AddObject(o); n++; }
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
        ReadAssetEntries(path, null);
        // private (paid) packs: a folder beside assets/ with its own manifest, written by scripts/packs/private_pack_glb.gd; absent on a machine without them
        var privDir = PrivateAssetsDir()!;
        var priv = Path.Combine(privDir, "manifest.json");
        if (File.Exists(priv)) ReadAssetEntries(priv, privDir);
    }

    /// <summary>Reads a manifest; <paramref name="root"/> is the folder its "file" paths are relative to (null = the asset library).</summary>
    void ReadAssetEntries(string path, string? root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var file = a.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
            var prefab = a.TryGetProperty("prefab", out var pf) && pf.ValueKind == JsonValueKind.True;
            var sz = a.GetProperty("size_m");
            var id = a.GetProperty("id").GetString()!;
            if (_assetManifest!.ContainsKey(id)) continue; // the public library wins
            var cat = a.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "other";
            var name = a.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : id;
            _assetManifest[id] = new AssetEntry(file, root, prefab, new Vector3(sz[0].GetSingle(), sz[1].GetSingle(), sz[2].GetSingle()), cat, name, ParseSeats(a), ParseLights(a),
                Drycastle.ShooterCore.Colliders.ObjectColliders.ParseSolids(a), ParsePoints(a, "plates"));
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
        if (entry is { Prefab: true, Root: not null })
        {
            // a private pack prefab: a GLB beside the game with its own collision bodies, wearing the pack's shared materials
            path = Path.Combine(entry.Root, entry.File);
            if (!File.Exists(path))
                WarnNoPrivatePack(entry.Root);
            else
                try { scene = LoadPrefab(path); }
                catch (Exception e) { Warn($"object asset '{asset}': {e.Message}; using a placeholder box"); }
            _assetTemplates[asset] = scene;
            return scene;
        }
        if (asset.StartsWith("prim:"))
            path = Path.Combine(_mapDir, "prims", asset["prim:".Length..] + ".glb");
        else if (asset.StartsWith("pack:") && entry != null && entry.File != "" && (entry.Root ?? AssetsDir) is { } assetRoot)
            path = Path.Combine(assetRoot, entry.File);
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
        if (TryTakeGltf(path, out var ahead)) return ahead;
        return ParseGltf(path);
    }

    /// <summary>Parses a glTF file into a scene; <paramref name="parsed"/> sees its state first (the texture prefetch reads its materials).</summary>
    static Node3D? ParseGltf(string path, Action<string, GltfState>? parsed = null)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        var err = doc.AppendFromFile(path, state);
        if (err != Error.Ok) throw new InvalidDataException($"glTF load failed ({err}): {path}");
        parsed?.Invoke(path, state);
        return doc.GenerateScene(state) as Node3D;
    }

    /// <summary>glTF files being parsed ahead on a worker thread, by full path (<see cref="PrefetchGltf"/>).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.Task<Node3D?>> GltfAhead = new();

    /// <summary>Set by <see cref="StopPrefetch"/> (and never cleared: it is for quitting): the glTF worker parses no further file.</summary>
    static volatile bool GltfStop;

    /// <summary>The glTF worker tasks, for <see cref="StopPrefetch"/> to wait for.</summary>
    static readonly List<System.Threading.Tasks.Task> GltfWorkers = new();

    /// <summary>
    /// Starts parsing glTF files on a worker thread, one after another, so the loader (and the game's model caches) find them parsed:
    /// <see cref="TryTakeGltf"/> hands each one out once. A file already asked for is not parsed twice. With <paramref name="textures"/> a prefab's
    /// pack textures are decoded ahead too (<see cref="PrefetchPrefabTextures"/>); a collision-only load draws nothing.
    /// </summary>
    public static void PrefetchGltf(IEnumerable<string> paths, bool textures = false)
    {
        var todo = new List<(string Path, System.Threading.Tasks.TaskCompletionSource<Node3D?> Done)>();
        foreach (var p in paths.Select(Path.GetFullPath).Distinct())
        {
            if (!File.Exists(p)) continue;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<Node3D?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            if (GltfAhead.TryAdd(p, tcs.Task)) todo.Add((p, tcs));
        }
        if (todo.Count == 0) return;
        var worker = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            foreach (var (p, done) in todo)
            {
                if (GltfStop) { done.TrySetCanceled(); continue; } // the game is closing: leave the rest unparsed
                var t = LoadTimers.Start();
                try { done.SetResult(ParseGltf(p, textures ? PrefetchPrefabTextures : null)); }
                catch (Exception e) { done.SetException(e); }
                LoadTimers.Lap("worker.gltf_small", t);
            }
        }, System.Threading.Tasks.TaskCreationOptions.LongRunning);
        lock (GltfWorkers) { GltfWorkers.RemoveAll(t => t.IsCompleted); GltfWorkers.Add(worker); }
    }

    /// <summary>The scene a prefetch parsed for <paramref name="path"/> (waits for it); false when none was asked for, or it failed (the caller then parses, and reports, itself).</summary>
    public static bool TryTakeGltf(string path, out Node3D? scene)
    {
        scene = null;
        if (!GltfAhead.TryRemove(Path.GetFullPath(path), out var task)) return false;
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
        if (!task.IsCompletedSuccessfully) return false;
        scene = task.Result;
        return true;
    }

    /// <summary>The file an asset's template is read from, or null (unknown asset, or no file named); <paramref name="prefab"/>: a private pack prefab.</summary>
    string? TemplatePath(string asset, out bool prefab)
    {
        if (_assetManifest == null) ReadAssetManifest();
        _assetManifest!.TryGetValue(asset, out var entry);
        prefab = entry is { Prefab: true, Root: not null };
        if (prefab) return Path.Combine(entry!.Root!, entry.File);
        if (asset.StartsWith("prim:")) return Path.Combine(_mapDir, "prims", asset["prim:".Length..] + ".glb");
        if (asset.StartsWith("pack:") && entry != null && entry.File != "" && (entry.Root ?? AssetsDir) is { } assetRoot) return Path.Combine(assetRoot, entry.File);
        return null;
    }

    /// <summary>The own model file of a placed object inside the map folder, or null.</summary>
    string? OwnModelPath(PlacedObject o)
    {
        if (string.IsNullOrEmpty(o.Model)) return null;
        var path = Path.GetFullPath(Path.Combine(_mapDir, o.Model));
        return path.StartsWith(Path.GetFullPath(_mapDir) + Path.DirectorySeparatorChar) && File.Exists(path) ? path : null;
    }

    /// <summary>Starts parsing the files of these assets' templates on a worker thread (the game's vehicle models of the private packs). Main thread.</summary>
    public void PrefetchAssetModels(IEnumerable<string> assets) => PrefetchGltf(assets.Select(a => TemplatePath(a, out _)).OfType<string>().ToList(), !CollisionOnly);

    /// <summary>objects.json as read for the model prefetch (taken by the object build), or null.</summary>
    (List<PlacedObject> Objects, List<CableSpec> Cables)? _objectsAhead;

    /// <summary>Reads objects.json and starts parsing the models its objects need (asset templates, own models) on a worker thread.</summary>
    void PrefetchObjectModels(string objectsPath, IEnumerable<string> morePaths)
    {
        var paths = new List<string>(); // the objects' files first (needed first), then the pack trees' (needed last)
        if (File.Exists(objectsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(objectsPath));
            var list = ParseObjects(doc.RootElement.GetProperty("objects"));
            _objectsAhead = (list, ParseCables(doc.RootElement));
            foreach (var asset in list.Select(o => o.Asset).Distinct())
                if (TemplatePath(asset, out _) is { } p) paths.Add(p);
            if (!CollisionOnly) foreach (var o in list) if (OwnModelPath(o) is { } own) paths.Add(own);
        }
        paths.AddRange(morePaths);
        PrefetchGltf(paths, !CollisionOnly);
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
